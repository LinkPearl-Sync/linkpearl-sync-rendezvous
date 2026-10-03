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

    public IPAddress Remote => _remote;

    public Socket Socket => client.Client;

    /// <summary>La clé sous laquelle le limiteur compte cette connexion.</summary>
    public string Bucket { get; } = AddressBucket.Of(RemoteOf(client));

    /// <summary>Le /48 sous lequel cette connexion compte aussi, en IPv6 ; nul en IPv4.</summary>
    public string? WideBucket { get; } = AddressBucket.Wide(RemoteOf(client));

    private long _lastActivity;

    /// <summary>La dernière trame reçue, à l'heure de l'horloge du service.</summary>
    public DateTimeOffset LastActivity => new(Interlocked.Read(ref _lastActivity), TimeSpan.Zero);

    public void Touch(DateTimeOffset now) => Interlocked.Exchange(ref _lastActivity, now.UtcTicks);

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

    /// <summary>Pages de liste signée servies sur cette connexion, bornées comme celles des bans.</summary>
    public int ConsensusPagesServed { get; set; }

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

    /// <summary>Vrai quand la session compte dans le plafond de relais de son adresse.</summary>
    public bool HoldsRelaySlot { get; set; }

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

    /// <summary>
    /// Temps accordé à un envoi vers cette session, attente du tour comprise.
    /// </summary>
    /// <remarks>
    /// Un client qui cesse de lire sans fermer remplit son tampon, puis tout
    /// envoi vers lui reste suspendu : le keepalive ne le voit pas, puisque
    /// sa pile TCP acquitte toujours. Sans délai, chaque session qui lui
    /// remettait un dépôt restait bloquée avec lui, et il gardait sa place
    /// et ses boîtes pour toujours.
    /// </remarks>
    public TimeSpan SendTimeout { get; init; } = RendezvousLimits.Default.PeerSendTimeout;

    /// <summary>
    /// Temps accordé, dans un relais, à cette session pour prendre un bloc.
    /// </summary>
    /// <remarks>
    /// Plus long que <see cref="SendTimeout"/> : un relais pousse des
    /// mégaoctets, et un tampon plein y est la contre-pression normale d'un
    /// lien plus lent que l'autre, pas un abandon. Ne rien prendre pendant
    /// ce délai, en revanche, en est un.
    /// </remarks>
    public TimeSpan RelayStallTimeout { get; init; } = RendezvousLimits.Default.RelayStallTimeout;

    public Task SendAsync(byte[] body, CancellationToken ct) => SendAsync(body, SendTimeout, ct);

    /// <summary>
    /// Envoie une trame, ou coupe la session si elle ne la prend pas à temps.
    /// </summary>
    /// <remarks>
    /// Un destinataire trop lent est coupé, et non simplement sauté : un
    /// envoi abandonné à mi-trame laisserait son flux désaligné, et le
    /// garder ouvert ne ferait que reporter le blocage au dépôt suivant. La
    /// coupure fait tomber sa propre boucle de service, qui rend sa place et
    /// ses boîtes.
    /// </remarks>
    public async Task SendAsync(byte[] body, TimeSpan patience, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(patience);

        try
        {
            await _sending.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested is false)
        {
            throw TooSlow();
        }

        try
        {
            await _stream.WriteAsync(RendezvousWire.Frame(body), deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested is false)
        {
            throw TooSlow();
        }
        finally
        {
            _sending.Release();
        }
    }

    private IOException TooSlow()
    {
        Interlocked.Increment(ref _slowCuts);
        Abort();
        return new IOException("destinataire trop lent, session coupée");
    }

    /// <summary>Sessions coupées pour n'avoir pas lu à temps, depuis le démarrage.</summary>
    public static long SlowCuts => Interlocked.Read(ref _slowCuts);

    private static long _slowCuts;

    /// <summary>
    /// Ferme la socket sans attendre, depuis n'importe quel fil.
    /// </summary>
    /// <remarks>
    /// Une remise à zéro et non une fermeture polie : ce qui reste dans le
    /// tampon d'un lecteur qui ne lit plus n'arrivera jamais, et le garder
    /// occuperait la mémoire du noyau. La lecture en cours dans la boucle de
    /// service lève alors, et c'est son <c>finally</c> qui libère tout. Une
    /// session garée pour un relais est réveillée aussi, sans quoi sa boucle
    /// attendrait un pontage qui ne viendra plus.
    /// </remarks>
    public void Abort()
    {
        try
        {
            client.Client.Close(0);
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            // Déjà fermée : c'est ce qu'on voulait.
        }

        _relayFinished.TrySetResult();
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
    /// Met deux sessions bout à bout jusqu'à ce que l'une se ferme, ou que le
    /// relais ait épuisé son volume ou sa durée.
    /// </summary>
    /// <remarks>
    /// Les octets sont recopiés sans être inspectés : ils sont déjà chiffrés de
    /// bout en bout par le canal des pairs. Le serveur transporte sans pouvoir
    /// lire, et c'est ce qui permet au relais de ne pas trahir la promesse du
    /// projet.
    ///
    /// Le volume se compte sur les deux sens ensemble : c'est la bande
    /// passante de l'opérateur qu'il protège, et elle se paie dans les deux.
    /// Un relais épuisé se ferme sans un mot, comme une coupure : le pair
    /// sait reprendre un transfert, et redemande un relais s'il le faut.
    /// </remarks>
    /// <returns>Vrai si le relais a été fermé par un quota.</returns>
    public static async Task<bool> PipeAsync(
        PeerSession one, PeerSession other, long byteQuota, TimeSpan maxDuration, CancellationToken ct)
    {
        one.IsRelaying = true;
        other.IsRelaying = true;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(maxDuration);

        var budget = new RelayBudget(byteQuota);

        var directions = new[]
        {
            Copy(one, other, budget, linked.Token),
            Copy(other, one, budget, linked.Token),
        };

        await Task.WhenAny(directions).ConfigureAwait(false);
        var expired = linked.IsCancellationRequested && ct.IsCancellationRequested is false;
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

        return expired || budget.Exhausted;
    }

    /// <summary>Les octets qu'un relais peut encore porter, partagés par ses deux sens.</summary>
    private sealed class RelayBudget(long quota)
    {
        private long _used;

        public bool Exhausted => Interlocked.Read(ref _used) > quota;

        /// <summary>Compte un bloc, et dit s'il tient encore dans le quota.</summary>
        public bool Spend(int bytes) => Interlocked.Add(ref _used, bytes) <= quota;
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

    private static async Task Copy(PeerSession from, PeerSession to, RelayBudget budget, CancellationToken ct)
    {
        while (ct.IsCancellationRequested is false)
        {
            var frame = await from.ReadFrameAsync(ct).ConfigureAwait(false);

            if (frame is null)
                return;

            if (frame[0] != RendezvousKind.RelayData)
                return;

            // Le bloc qui dépasse n'est pas transmis : il ferait sinon passer
            // le quota d'une trame entière à chaque relais.
            if (budget.Spend(frame.Length) is false)
                return;

            await to.SendAsync(frame, to.RelayStallTimeout, ct).ConfigureAwait(false);
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
        // Le sémaphore n'est pas libéré : une autre session peut être en
        // train de nous envoyer une trame à cet instant, et son Release
        // lèverait sur un objet détruit. Sans poignée d'attente demandée, il
        // ne tient aucune ressource du système.
        _stream.Dispose();
        client.Dispose();
    }
}
