using System.Net;
using System.Net.Sockets;
using Linkpearl.Core.Transport.Rendezvous;
using Linkpearl.Rendezvous;
using Xunit;

namespace Linkpearl.Rendezvous.Tests;

/// <summary>
/// Le service sur le fil : ce qu'il apparie, ce qu'il relaie, ce qu'il refuse.
/// </summary>
public sealed class RendezvousServerTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(400);

    private static byte[] Ticket(byte seed) => Enumerable.Repeat(seed, RendezvousTicket.SizeInBytes).ToArray();

    private static byte[] Announce(byte[] sealedCandidates, params byte[][] tickets)
        => RendezvousWire.Announce(new Announcement(tickets, sealedCandidates));

    [Fact]
    public async Task La_reflexion_udp_renvoie_ladresse_et_le_port_vus()
    {
        await using var harness = await ServerHarness.StartAsync();

        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await probe.SendAsync(new[] { RendezvousKind.Reflect }, new IPEndPoint(IPAddress.Loopback, harness.Port));

        var reply = await probe.ReceiveAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);

        Assert.Equal(RendezvousKind.Reflected, reply.Buffer[0]);
        Assert.Equal(4, reply.Buffer[1]);
        Assert.Equal(IPAddress.Loopback, new IPAddress(reply.Buffer[2..6]));
        Assert.Equal(((IPEndPoint)probe.Client.LocalEndPoint!).Port, (reply.Buffer[6] << 8) | reply.Buffer[7]);
    }

    [Fact]
    public async Task Deux_annonces_du_meme_jeton_sont_appariees()
    {
        await using var harness = await ServerHarness.StartAsync();

        var a = await harness.ConnectAsync();
        var b = await harness.ConnectAsync();

        await a.SendAsync(Announce([1, 1, 1], Ticket(0x11)));
        Assert.True(await a.IsSilentAsync(Short));

        await b.SendAsync(Announce([2, 2, 2], Ticket(0x11)));

        var toA = await a.ReadFrameAsync();
        var toB = await b.ReadFrameAsync();

        Assert.Equal(RendezvousKind.Matched, toA![0]);
        Assert.Equal(new byte[] { 2, 2, 2 }, toA[1..]);
        Assert.Equal(new byte[] { 1, 1, 1 }, toB![1..]);
        Assert.Equal(1, harness.Server.Snapshot().Matches);
    }

    [Fact]
    public async Task Une_attente_expire_avec_la_fenetre_du_jeton()
    {
        await using var harness = await ServerHarness.StartAsync();

        var a = await harness.ConnectAsync();
        await a.SendAsync(Announce([1], Ticket(0x11)));
        Assert.True(await a.IsSilentAsync(Short));
        Assert.Equal(1, harness.Server.Snapshot().PendingAnnouncements);

        harness.Clock.Advance(RendezvousTicket.Window + TimeSpan.FromSeconds(1));
        harness.Server.Sweep();

        Assert.Equal(0, harness.Server.Snapshot().PendingAnnouncements);
    }
}

/// <summary>Ce qu'une connexion doit faire pour être gardée.</summary>
public sealed class ConnectionLimitTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(400);

    [Fact]
    public async Task Une_connexion_muette_est_fermee_apres_le_delai()
    {
        // Se connecter et se taire par milliers épuiserait la table des
        // descripteurs sans jamais envoyer un octet.
        await using var harness = await ServerHarness.StartAsync(
            new RendezvousLimits { FirstFrameTimeout = TimeSpan.FromMilliseconds(300) });

        var mute = await harness.ConnectAsync();

        Assert.True(await mute.IsClosedAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Une_connexion_qui_parle_reste_ouverte_au_dela_du_delai()
    {
        await using var harness = await ServerHarness.StartAsync(
            new RendezvousLimits { FirstFrameTimeout = TimeSpan.FromMilliseconds(300) });

        var talker = await harness.ConnectAsync();
        await talker.SendAsync(RendezvousWire.MailboxOpen([new byte[RendezvousWire.MailboxAddressSize]]));

        Assert.True(await talker.IsSilentAsync(TimeSpan.FromMilliseconds(800)));
    }

    [Fact]
    public async Task Au_dela_du_plafond_par_adresse_la_connexion_est_fermee()
    {
        await using var harness = await ServerHarness.StartAsync(
            new RendezvousLimits { MaxConnectionsPerAddress = 2 });

        var first = await harness.ConnectAsync();
        var second = await harness.ConnectAsync();
        var third = await harness.ConnectAsync();

        Assert.True(await third.IsClosedAsync(TimeSpan.FromSeconds(5)));
        Assert.True(await first.IsSilentAsync(Short));
        Assert.True(await second.IsSilentAsync(Short));
        Assert.Equal(1, harness.Server.Snapshot().RefusedConnections);
    }

    [Fact]
    public async Task Au_dela_du_plafond_global_la_connexion_est_fermee()
    {
        await using var harness = await ServerHarness.StartAsync(
            new RendezvousLimits { MaxConnections = 2 });

        var first = await harness.ConnectAsync();
        var second = await harness.ConnectAsync();
        var third = await harness.ConnectAsync();

        Assert.True(await third.IsClosedAsync(TimeSpan.FromSeconds(5)));
        Assert.True(await first.IsSilentAsync(Short));
        Assert.True(await second.IsSilentAsync(Short));
    }

    [Fact]
    public async Task Une_place_liberee_se_reprend()
    {
        await using var harness = await ServerHarness.StartAsync(
            new RendezvousLimits { MaxConnectionsPerAddress = 1 });

        var first = await harness.ConnectAsync();
        await first.SendAsync(RendezvousWire.MailboxOpen([new byte[RendezvousWire.MailboxAddressSize]]));
        Assert.True(await first.IsSilentAsync(Short));
        Assert.Equal(1, harness.Server.Snapshot().Connections);

        first.Dispose();

        // Le service constate le départ en lisant la fin du flux, ce qui prend
        // un instant : on attend que le compteur retombe avant de réessayer.
        for (var i = 0; i < 50 && harness.Server.Snapshot().Connections > 0; i++)
            await Task.Delay(20);

        var second = await harness.ConnectAsync();
        await second.SendAsync(RendezvousWire.MailboxOpen([new byte[RendezvousWire.MailboxAddressSize]]));

        Assert.True(await second.IsSilentAsync(Short));
    }
}
