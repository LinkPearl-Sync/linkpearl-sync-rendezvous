using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Linkpearl.Core.Transport.Rendezvous;

namespace Linkpearl.Rendezvous;

/// <summary>Une connexion cliente, et les jetons qu'elle a annoncés.</summary>
public sealed class PeerSession(TcpClient client) : IDisposable
{
    private readonly NetworkStream _stream = client.GetStream();
    private readonly SemaphoreSlim _sending = new(1);

    // Des ensembles concurrents et non des listes : le balayage et l'appariement
    // par une autre session retirent des clés pendant que la boucle de service
    // en ajoute.
    private readonly ConcurrentDictionary<string, byte> _keys = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _mailboxes = new(StringComparer.Ordinal);

    /// <summary>
    /// L'adresse distante, capturée à la construction.
    /// </summary>
    /// <remarks>
    /// Lue sur la socket au moment où elle est encore ouverte : le journal et
    /// l'oubli des attentes en ont besoin après la fermeture, et la socket
    /// lève alors au lieu de répondre.
    /// </remarks>
    private readonly IPAddress _remote = RemoteOf(client);

    public string Address => _remote.ToString();

    public Socket Socket => client.Client;

    /// <summary>La clé sous laquelle le limiteur compte cette connexion.</summary>
    public string Bucket { get; } = AddressBucket.Of(RemoteOf(client));

    /// <summary>Les jetons sur lesquels cette session attend, annonce ou relais.</summary>
    public ICollection<string> Keys => _keys.Keys;

    public int KeyCount => _keys.Count;

    /// <summary>Pages de liste de bannissement servies sur cette connexion.</summary>
    /// <remarks>
    /// Trois octets de demande pour jusqu'à 64 Kio de réponse : sans plafond,
    /// une connexion ferait du service un amplificateur. Une liste entière se
    /// lit en 64 pages au plus.
    /// </remarks>
    public int BanPagesServed { get; set; }

    public bool HasKey(string key) => _keys.ContainsKey(key);

    public void Remember(string key) => _keys[key] = 0;

    public void ForgetKey(string key) => _keys.TryRemove(key, out _);

    public ICollection<string> Mailboxes => _mailboxes.Keys;

    public int MailboxCount => _mailboxes.Count;

    public bool HasMailbox(string key) => _mailboxes.ContainsKey(key);

    public void RememberMailbox(string key) => _mailboxes[key] = 0;

    private readonly TaskCompletionSource _relayFinished =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsRelaying { get; private set; }

    /// <summary>
    /// Vrai quand la session attend un pair pour relayer.
    /// </summary>
    /// <remarks>
    /// Une session garée ne doit plus être lue par sa boucle de service : le
    /// pontage lira sa socket, et deux lecteurs concurrents sur le même flux se
    /// volent les trames. C'est arrivé, et le symptôme était qu'un seul des deux
    /// pairs recevait.
    /// </remarks>
    public bool IsParked { get; private set; }

    public void Park()
    {
        IsParked = true;
        IsRelaying = true;
    }

    /// <summary>Attend que le pontage soit terminé, sans lire la socket.</summary>
    public Task WaitUntilRelayFinishedAsync(CancellationToken ct) => _relayFinished.Task.WaitAsync(ct);

    public void ReleaseFromRelay() => _relayFinished.TrySetResult();

    /// <summary>
    /// Arme le keepalive TCP sur une socket acceptée.
    /// </summary>
    /// <remarks>
    /// Sans lui, une connexion morte sans FIN (câble tiré, veille, NAT qui a
    /// oublié le flux) reste ouverte côté serveur jusqu'à ce que le noyau
    /// abandonne, ce qui se compte en heures : la boîte reste ouverte sur un
    /// joueur parti et le descripteur reste pris. Au mieux de ce que la plateforme
    /// accepte : un noyau qui refuse un réglage n'empêche pas de servir.
    /// </remarks>
    public static void Harden(Socket socket, RendezvousLimits limits)
    {
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, (int)limits.KeepAliveTime.TotalSeconds);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, (int)limits.KeepAliveInterval.TotalSeconds);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, limits.KeepAliveRetryCount);
        }
        catch (SocketException)
        {
            // Plateforme sans ces options : on sert quand même.
        }
    }

    public async Task<byte[]?> ReadFrameAsync(CancellationToken ct)
    {
        var header = new byte[sizeof(int)];

        if (await ReadExactlyAsync(header, ct).ConfigureAwait(false) is false)
            return null;

        var length = BinaryPrimitives.ReadInt32BigEndian(header);

        // Une longueur venant du réseau ne s'alloue jamais telle quelle.
        if (length is <= 0 or > RendezvousWire.MaxFrameLength)
            return null;

        var body = new byte[length];
        return await ReadExactlyAsync(body, ct).ConfigureAwait(false) ? body : null;
    }

    public async Task SendAsync(byte[] body, CancellationToken ct)
    {
        await _sending.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(RendezvousWire.Frame(body), ct).ConfigureAwait(false);
        }
        finally
        {
            _sending.Release();
        }
    }

    /// <summary>
    /// Envoie à une autre session que la sienne, sans que son échec nous atteigne.
    /// </summary>
    /// <remarks>
    /// Un partenaire parti entre son annonce et la nôtre, ou un destinataire de
    /// boîte dont la socket vient de mourir, ferait lever l'envoi dans la
    /// boucle de service de celui qui parle : c'est lui qui serait coupé, pour
    /// une faute qui n'est pas la sienne.
    /// </remarks>
    public async Task<bool> TrySendAsync(byte[] body, CancellationToken ct)
    {
        try
        {
            await SendAsync(body, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Met deux sessions bout à bout jusqu'à ce que l'une se ferme.
    /// </summary>
    /// <remarks>
    /// Les octets sont recopiés sans être inspectés : ils sont déjà chiffrés de
    /// bout en bout par le canal des pairs. Le serveur transporte sans pouvoir
    /// lire, et c'est ce qui permet au relais de ne pas trahir la promesse du
    /// projet.
    /// </remarks>
    public static async Task PipeAsync(PeerSession one, PeerSession other, CancellationToken ct)
    {
        one.IsRelaying = true;
        other.IsRelaying = true;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var directions = new[]
        {
            Copy(one, other, linked.Token),
            Copy(other, one, linked.Token),
        };

        await Task.WhenAny(directions).ConfigureAwait(false);
        await linked.CancelAsync().ConfigureAwait(false);

        try
        {
            await Task.WhenAll(directions).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // La fermeture d'un côté fait échouer l'autre : c'est le cas normal.
        }
        finally
        {
            // Libère la boucle de service du pair qui attendait garé.
            one.ReleaseFromRelay();
            other.ReleaseFromRelay();
        }
    }

    /// <summary>
    /// Octets relayés depuis le démarrage, tous pontages confondus.
    /// </summary>
    /// <remarks>
    /// Statique parce qu'un pontage naît et meurt avec deux sessions, alors que
    /// le compteur doit survivre aux deux. Un seul service tourne par processus,
    /// donc un compteur de processus est bien un compteur de service.
    /// </remarks>
    public static long TotalRelayedBytes => Interlocked.Read(ref _relayedBytes);

    private static long _relayedBytes;

    private static async Task Copy(PeerSession from, PeerSession to, CancellationToken ct)
    {
        while (ct.IsCancellationRequested is false)
        {
            var frame = await from.ReadFrameAsync(ct).ConfigureAwait(false);

            if (frame is null)
                return;

            if (frame[0] != RendezvousKind.RelayData)
                return;

            await to.SendAsync(frame, ct).ConfigureAwait(false);
            Interlocked.Add(ref _relayedBytes, frame.Length);
        }
    }

    private async Task<bool> ReadExactlyAsync(Memory<byte> buffer, CancellationToken ct)
    {
        var offset = 0;

        while (offset < buffer.Length)
        {
            var read = await _stream.ReadAsync(buffer[offset..], ct).ConfigureAwait(false);

            if (read == 0)
                return false;

            offset += read;
        }

        return true;
    }

    private static IPAddress RemoteOf(TcpClient client)
    {
        if (client.Client.RemoteEndPoint is not IPEndPoint endpoint)
            return IPAddress.None;

        return endpoint.Address.IsIPv4MappedToIPv6 ? endpoint.Address.MapToIPv4() : endpoint.Address;
    }

    public void Dispose()
    {
        _sending.Dispose();
        _stream.Dispose();
        client.Dispose();
    }
}
