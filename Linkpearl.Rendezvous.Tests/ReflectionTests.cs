using System.Net;
using System.Net.Sockets;
using Linkpearl.Core.Transport.Rendezvous;
using Linkpearl.Rendezvous;
using Xunit;

namespace Linkpearl.Rendezvous.Tests;

/// <summary>La réflexion UDP : utile au client, inutile à qui voudrait amplifier.</summary>
public sealed class ReflectionTests
{
    private static readonly IPEndPoint Client = new(IPAddress.Parse("198.51.100.7"), 40000);

    private static RendezvousServer Server(RendezvousLimits? limits = null)
        => new(0, new PeerDirectory("peers.txt", "pending.txt"), limits ?? RendezvousLimits.Default, new ManualClock());

    private static byte[] Padded()
    {
        var request = new byte[RendezvousServer.PaddedReflectSize];
        request[0] = RendezvousKind.Reflect;
        return request;
    }

    [Fact]
    public void Une_requete_bourree_recoit_une_reponse_plus_petite_quelle()
    {
        var reply = Server().Reflection(Padded(), Client);

        Assert.NotNull(reply);
        Assert.Equal(RendezvousKind.Reflected, reply[0]);
        Assert.True(reply.Length <= RendezvousServer.PaddedReflectSize);
        Assert.Equal(Client.Address, new IPAddress(reply[2..6]));
    }

    [Fact]
    public void Une_requete_dun_octet_des_clients_actuels_recoit_encore_sa_reponse()
        => Assert.NotNull(Server().Reflection([RendezvousKind.Reflect], Client));

    [Fact]
    public void Une_autre_trame_ne_recoit_rien()
    {
        Assert.Null(Server().Reflection([], Client));
        Assert.Null(Server().Reflection([RendezvousKind.Announce, 0, 0], Client));
    }

    [Fact]
    public void Au_dela_du_debit_une_source_ne_recoit_plus_rien()
    {
        var server = Server(RendezvousLimits.Default with { ReflectionsPerMinute = 3 });

        for (var i = 0; i < 3; i++)
            Assert.NotNull(server.Reflection([RendezvousKind.Reflect], Client));

        Assert.Null(server.Reflection(Padded(), Client));

        // Une autre source n'en pâtit pas.
        Assert.NotNull(server.Reflection(Padded(), new IPEndPoint(IPAddress.Parse("198.51.100.8"), 40000)));
    }

    [Fact]
    public void Un_48_ne_contourne_pas_le_debit_en_changeant_de_64()
    {
        var server = Server(RendezvousLimits.Default with { ReflectionsPerMinute = 1 });
        var answered = 0;

        for (var subnet = 0; subnet < 20; subnet++)
            answered += server.Reflection(Padded(), new IPEndPoint(IPAddress.Parse($"2001:db8:9:{subnet:x}::1"), 40000)) is null ? 0 : 1;

        Assert.Equal(RendezvousServer.PrefixRateFactor, answered);
    }

    [Fact]
    public async Task Le_service_repond_a_une_requete_bourree_sur_le_fil()
    {
        await using var harness = await ServerHarness.StartAsync();

        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await probe.SendAsync(Padded(), new IPEndPoint(IPAddress.Loopback, harness.Port));

        var reply = await probe.ReceiveAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);

        Assert.Equal(RendezvousKind.Reflected, reply.Buffer[0]);
        Assert.True(reply.Buffer.Length <= RendezvousServer.PaddedReflectSize);
    }
}
