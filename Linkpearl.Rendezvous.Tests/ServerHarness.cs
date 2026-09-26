using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Linkpearl.Core.Transport.Rendezvous;
using Linkpearl.Rendezvous;

namespace Linkpearl.Rendezvous.Tests;

/// <summary>
/// Un service sur un port éphémère, et des clients qui lui parlent en
/// <see cref="RendezvousWire"/>.
/// </summary>
/// <remarks>
/// Le port zéro laisse le système en choisir un libre : chaque test a son
/// service, et xunit peut les faire tourner en parallèle sans collision.
/// L'horloge est manuelle et le balayage se déclenche à la demande, donc
/// aucun test n'attend une expiration pour de vrai.
/// </remarks>
public sealed class ServerHarness : IAsyncDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"lprdv-server-{Guid.NewGuid():N}");
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<TestClient> _clients = [];

    private Task _running = Task.CompletedTask;

    public ManualClock Clock { get; } = new();

    public PeerDirectory Directory { get; private set; } = null!;

    public BanStore Bans { get; private set; } = null!;

    public RendezvousServer Server { get; private set; } = null!;

    public int Port { get; private set; }

    // Synchronisé : les sessions écrivent depuis plusieurs fils. Le writer
    // synchronisé verrouille sur lui-même, et la lecture prend ce même verrou.
    private readonly StringWriter _sink = new();
    private readonly TextWriter _log;

    private ServerHarness() => _log = TextWriter.Synchronized(_sink);

    /// <summary>Ce que le service a journalisé jusqu'ici.</summary>
    public string Log
    {
        get
        {
            lock (_log)
                return _sink.ToString();
        }
    }

    public static async Task<ServerHarness> StartAsync(
        RendezvousLimits? limits = null, bool verbose = false, IConsensusSource? consensus = null,
        Action<DirectoryEntry, string>? candidacy = null)
    {
        var harness = new ServerHarness();

        System.IO.Directory.CreateDirectory(harness._dir);

        harness.Directory = new PeerDirectory(
            Path.Combine(harness._dir, "peers.txt"), Path.Combine(harness._dir, "pending.txt"));

        harness.Bans = new BanStore(Path.Combine(harness._dir, "bans.json"));

        harness.Server = new RendezvousServer(
            0, harness.Directory, limits ?? RendezvousLimits.Default, harness.Clock, verbose)
        {
            Log = harness._log,
            Bans = harness.Bans,
            Consensus = consensus,
            Candidacy = candidacy,
        };

        harness._running = harness.Server.RunAsync(harness._stopping.Token);
        harness.Port = await harness.Server.Listening.WaitAsync(TimeSpan.FromSeconds(5));

        return harness;
    }

    public async Task<TestClient> ConnectAsync()
    {
        var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(IPAddress.Loopback, Port);

        var wrapped = new TestClient(client);
        _clients.Add(wrapped);
        return wrapped;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
            client.Dispose();

        await _stopping.CancelAsync();

        try
        {
            await _running;
        }
        catch (OperationCanceledException)
        {
            // L'arrêt annule l'acceptation : c'est la sortie normale.
        }

        _stopping.Dispose();
        System.IO.Directory.Delete(_dir, recursive: true);
    }
}

/// <summary>Un client qui parle des trames, et rien de plus.</summary>
public sealed class TestClient(TcpClient client) : IDisposable
{
    private static readonly TimeSpan DefaultPatience = TimeSpan.FromSeconds(5);

    private readonly NetworkStream _stream = client.GetStream();

    public Socket Socket => client.Client;

    public Task SendAsync(byte[] body) => _stream.WriteAsync(RendezvousWire.Frame(body)).AsTask();

    /// <summary>La prochaine trame, ou null si le service a fermé.</summary>
    public async Task<byte[]?> ReadFrameAsync(TimeSpan? patience = null)
    {
        using var deadline = new CancellationTokenSource(patience ?? DefaultPatience);

        var header = new byte[sizeof(int)];

        if (await ReadExactlyAsync(header, deadline.Token) is false)
            return null;

        var body = new byte[BinaryPrimitives.ReadInt32BigEndian(header)];
        return await ReadExactlyAsync(body, deadline.Token) ? body : null;
    }

    /// <summary>Vrai si le service ferme la connexion dans le délai.</summary>
    public async Task<bool> IsClosedAsync(TimeSpan patience)
    {
        try
        {
            return await ReadFrameAsync(patience) is null;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Vrai si rien n'arrive pendant le délai : la connexion est ouverte et muette.</summary>
    public async Task<bool> IsSilentAsync(TimeSpan patience)
    {
        try
        {
            await ReadFrameAsync(patience);
            return false;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }

    private async Task<bool> ReadExactlyAsync(Memory<byte> buffer, CancellationToken ct)
    {
        var offset = 0;

        while (offset < buffer.Length)
        {
            int read;

            try
            {
                read = await _stream.ReadAsync(buffer[offset..], ct);
            }
            catch (IOException)
            {
                // Une réinitialisation vaut fermeture.
                return false;
            }

            if (read == 0)
                return false;

            offset += read;
        }

        return true;
    }

    public void Dispose()
    {
        _stream.Dispose();
        client.Dispose();
    }
}
