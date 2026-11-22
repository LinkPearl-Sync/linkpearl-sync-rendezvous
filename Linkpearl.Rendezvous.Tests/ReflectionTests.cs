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
    public void Une_requete_envoyee_par_LiteNetLib_recoit_sa_reponse_dans_la_meme_enveloppe()
    {
        // Ce que le plugin met réellement sur le fil : l'en-tête d'un message
        // hors connexion de LiteNetLib, puis la requête bourrée.
        var framed = new byte[1 + RendezvousServer.PaddedReflectSize];
        framed[0] = RendezvousServer.LiteNetUnconnectedMessage;
        framed[1] = RendezvousKind.Reflect;

        var reply = Server().Reflection(framed, Client);

        Assert.NotNull(reply);
        Assert.Equal(RendezvousServer.LiteNetUnconnectedMessage, reply[0]);
        Assert.Equal(RendezvousKind.Reflected, reply[1]);
        Assert.Equal(Client.Address, new IPAddress(reply[3..7]));
        Assert.Equal(Client.Port, (reply[7] << 8) | reply[8]);
        Assert.True(reply.Length < framed.Length);
    }

    [Fact]
    public void Une_requete_LiteNetLib_non_bourree_ne_recoit_rien()
        => Assert.Null(Server().Reflection([RendezvousServer.LiteNetUnconnectedMessage, RendezvousKind.Reflect], Client));

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

    private static IPEndPoint Source(int i) => new(IPAddress.Parse($"198.51.{i / 256}.{i % 256}"), 40000);

    [Fact]
    public void Une_rafale_de_sources_usurpees_ne_remplit_pas_la_table_au_dela_de_son_plafond()
    {
        // Chaque source jamais vue gardait une entrée une minute, sans
        // plafond : quelques dizaines de milliers de paquets usurpés par
        // seconde remplissaient les 512 Mo de l'unité systemd.
        var server = Server(RendezvousLimits.Default with { MaxReflectionSources = 8 });
        var answered = 0;

        for (var i = 0; i < 100; i++)
            answered += server.Reflection(Padded(), Source(i)) is null ? 0 : 1;

        Assert.Equal(8, answered);
    }

    [Fact]
    public void Table_pleine_une_source_deja_comptee_recoit_encore_sa_reponse()
    {
        var server = Server(RendezvousLimits.Default with { MaxReflectionSources = 2 });

        Assert.NotNull(server.Reflection(Padded(), Source(0)));
        Assert.NotNull(server.Reflection(Padded(), Source(1)));
        Assert.Null(server.Reflection(Padded(), Source(2)));

        Assert.NotNull(server.Reflection(Padded(), Source(0)));
    }

    [Fact]
    public void Une_fois_la_fenetre_passee_le_balayage_rend_la_place()
    {
        var clock = new ManualClock();
        var server = new RendezvousServer(
            0, new PeerDirectory("peers.txt", "pending.txt"), RendezvousLimits.Default with { MaxReflectionSources = 2 }, clock);

        server.Reflection(Padded(), Source(0));
        server.Reflection(Padded(), Source(1));
        Assert.Null(server.Reflection(Padded(), Source(2)));

        clock.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));
        server.Sweep();

        Assert.NotNull(server.Reflection(Padded(), Source(2)));
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
