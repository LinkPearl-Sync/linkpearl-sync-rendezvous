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
