using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Linkpearl.Core.Transport.Rendezvous;
using Xunit;

namespace Linkpearl.Rendezvous.Tests;

/// <summary>La candidature renvoyée à intervalle régulier, jusqu'à l'arrêt du service.</summary>
public sealed class AnnouncerTests
{
    /// <summary>Un faux annuaire qui compte les candidatures reçues.</summary>
    private static async Task CountAsync(TcpListener listener, Action<DirectoryEntry> received, CancellationToken ct)
    {
        while (ct.IsCancellationRequested is false)
        {
            using var socket = await listener.AcceptTcpClientAsync(ct);
            var stream = socket.GetStream();
            var header = new byte[4];
            await stream.ReadExactlyAsync(header, ct);
            var body = new byte[BinaryPrimitives.ReadInt32BigEndian(header)];
            await stream.ReadExactlyAsync(body, ct);

            if (body[0] == RendezvousKind.DirectorySubmit && RendezvousWire.TryReadDirectory(body, out var entries, out _))
                received(entries.Single());
        }
    }

    [Fact]
    public async Task La_candidature_est_renvoyee_a_chaque_intervalle()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var received = new List<DirectoryEntry>();
        var twice = new TaskCompletionSource();

        var directory = Task.Run(() => CountAsync(listener, entry =>
        {
            lock (received)
            {
                received.Add(entry);

                if (received.Count >= 2)
                    twice.TrySetResult();
            }
        }, stop.Token));

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var announcing = Announcer.SubmitEveryAsync(
            new RendezvousAddress("127.0.0.1", port), "rdv.candidat.ch", "Candidat", TimeSpan.FromMilliseconds(200), stop.Token);

        await twice.Task.WaitAsync(stop.Token);

        Assert.All(received, entry => Assert.Equal("rdv.candidat.ch", entry.Address));

        await stop.CancelAsync();
        await announcing;
        listener.Stop();

        // Le faux annuaire s'arrête par l'annulation, levée ou constatée en
        // tête de boucle selon l'instant : les deux fins sont bonnes.
        try
        {
            await directory;
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Fact]
    public async Task Un_annuaire_injoignable_n_arrete_pas_la_boucle()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));

        // Trois tentatives échouent sans lever ; la boucle ne s'arrête qu'à l'annulation.
        await Announcer.SubmitEveryAsync(
            new RendezvousAddress("127.0.0.1", port), "rdv.candidat.ch", "", TimeSpan.FromMilliseconds(200), stop.Token);

        Assert.True(stop.IsCancellationRequested);
    }
}
