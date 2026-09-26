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

    [Fact]
    public async Task Un_echec_est_retente_bien_avant_l_intervalle()
    {
        // L'autorité se présente à elle-même au démarrage, avant d'écouter :
        // la première tentative échoue, et un jour d'attente la ferait
        // disparaître du cercle jusqu'au lendemain.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var received = new TaskCompletionSource<DirectoryEntry>();

        var announcing = Announcer.SubmitEveryAsync(
            new RendezvousAddress("127.0.0.1", port), "rdv.candidat.ch", "", TimeSpan.FromDays(1), stop.Token,
            retry: TimeSpan.FromMilliseconds(300));

        await Task.Delay(100, stop.Token);
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        var directory = Task.Run(() => CountAsync(listener, entry => received.TrySetResult(entry), stop.Token));

        Assert.Equal("rdv.candidat.ch", (await received.Task.WaitAsync(stop.Token)).Address);

        await stop.CancelAsync();
        await announcing;
        listener.Stop();

        try
        {
            await directory;
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Fact]
    public void Sans_consigne_le_service_se_presente_a_l_autorite_officielle()
        => Assert.Equal(["rdv.linkpearl.eorzea.events"], Announcer.Targets([], optOut: false));

    [Fact]
    public void Des_annuaires_donnes_remplacent_l_autorite_officielle()
        => Assert.Equal(["annuaire.ami.ch"], Announcer.Targets(["annuaire.ami.ch"], optOut: false));

    [Fact]
    public void Le_refus_l_emporte_sur_tout()
        => Assert.Empty(Announcer.Targets(["annuaire.ami.ch"], optOut: true));
}
