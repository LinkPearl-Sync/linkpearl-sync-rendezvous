using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Linkpearl.Core.Transport.Rendezvous;

namespace Linkpearl.Rendezvous;

/// <summary>Une connexion cliente, et les jetons qu'elle a annoncés.</summary>
public sealed class PeerSession(TcpClient client) : IDisposable
{
    private readonly NetworkStream _stream = client.GetStream();
    private readonly SemaphoreSlim _sending = new(1);
    private readonly List<string> _keys = [];

    private readonly List<string> _mailboxes = [];

    public IReadOnlyList<string> Keys => _keys;

    public IReadOnlyList<string> Mailboxes => _mailboxes;

    public void RememberMailbox(string key) => _mailboxes.Add(key);

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

    public string Address =>
        client.Client.RemoteEndPoint is IPEndPoint endpoint
            ? (endpoint.Address.IsIPv4MappedToIPv6 ? endpoint.Address.MapToIPv4() : endpoint.Address).ToString()
            : "inconnue";

    public void Remember(string key) => _keys.Add(key);

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

    public void Dispose()
    {
        _sending.Dispose();
        _stream.Dispose();
        client.Dispose();
    }
}
