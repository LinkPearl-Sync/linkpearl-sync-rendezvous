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

/// <summary>Ce que le limiteur compte, et ce que les plafonds retiennent.</summary>
public sealed class LimiterTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(400);

    private static byte[] Ticket(byte seed) => Enumerable.Repeat(seed, RendezvousTicket.SizeInBytes).ToArray();

    private static byte[] Box(byte seed) => Enumerable.Repeat(seed, RendezvousWire.MailboxAddressSize).ToArray();

    private static byte[] Invitation(byte seed) => Enumerable.Repeat(seed, RendezvousWire.InvitationTicketSize).ToArray();

    private static byte[] Announce(byte[] sealedCandidates, params byte[][] tickets)
        => RendezvousWire.Announce(new Announcement(tickets, sealedCandidates));

    private static async Task AssertErrorAsync(TestClient client, string fragment)
    {
        var frame = await client.ReadFrameAsync();

        Assert.NotNull(frame);
        Assert.Equal(RendezvousKind.Error, frame[0]);
        Assert.Contains(fragment, System.Text.Encoding.UTF8.GetString(frame, 1, frame.Length - 1));
    }

    [Fact]
    public async Task Ouvrir_des_boites_compte_dans_le_limiteur()
    {
        // Sans cela, ouvrir des boîtes était la seule trame gratuite, donc la
        // seule qu'un client pouvait répéter mille fois par seconde.
        await using var harness = await ServerHarness.StartAsync(new RendezvousLimits { AnnouncementsPerMinute = 2 });

        var client = await harness.ConnectAsync();
        await client.SendAsync(RendezvousWire.MailboxOpen([Box(1)]));
        await client.SendAsync(RendezvousWire.MailboxOpen([Box(2)]));
        await client.SendAsync(RendezvousWire.MailboxOpen([Box(3)]));

        await AssertErrorAsync(client, "trop");
    }

    [Fact]
    public async Task Demander_un_relais_compte_dans_le_limiteur()
    {
        await using var harness = await ServerHarness.StartAsync(new RendezvousLimits { AnnouncementsPerMinute = 1 });

        var first = await harness.ConnectAsync();
        await first.SendAsync(RendezvousWire.RelayOpen(Ticket(0x11)));
        Assert.True(await first.IsSilentAsync(Short));

        var second = await harness.ConnectAsync();
        await second.SendAsync(RendezvousWire.RelayOpen(Ticket(0x22)));

        await AssertErrorAsync(second, "trop");
    }

    [Fact]
    public async Task Une_session_ne_tient_pas_plus_de_boites_que_le_plafond()
    {
        await using var harness = await ServerHarness.StartAsync(new RendezvousLimits { MaxMailboxesPerSession = 2 });

        var client = await harness.ConnectAsync();
        await client.SendAsync(RendezvousWire.MailboxOpen([Box(1), Box(2)]));

        // Rouvrir les mêmes ne compte pas : le plugin le fait à chaque fenêtre.
        await client.SendAsync(RendezvousWire.MailboxOpen([Box(1), Box(2)]));
        Assert.True(await client.IsSilentAsync(Short));
        Assert.Equal(2, harness.Server.Snapshot().OpenMailboxes);

        await client.SendAsync(RendezvousWire.MailboxOpen([Box(3)]));

        await AssertErrorAsync(client, "trop de boîtes");
    }

    [Fact]
    public void Une_connexion_tient_par_defaut_les_boites_de_dix_groupes()
        // Deux personnelles, puis deux de présence et deux d'admission par
        // groupe au changement de fenêtre : 42 pour dix groupes.
        => Assert.True(new RendezvousLimits().MaxMailboxesPerSession >= 42);

    [Fact]
    public async Task Une_session_nattend_pas_sur_plus_de_jetons_que_le_plafond()
    {
        await using var harness = await ServerHarness.StartAsync(new RendezvousLimits { MaxWaitingKeysPerSession = 2 });

        var client = await harness.ConnectAsync();
        await client.SendAsync(Announce([1], Ticket(0x11), Ticket(0x12)));
        Assert.True(await client.IsSilentAsync(Short));

        await client.SendAsync(Announce([1], Ticket(0x13)));

        await AssertErrorAsync(client, "trop");
    }

    [Fact]
    public async Task Les_invitations_sont_plafonnees_par_adresse()
    {
        await using var harness = await ServerHarness.StartAsync(new RendezvousLimits { MaxInvitationsPerAddress = 2 });

        var client = await harness.ConnectAsync();

        for (byte seed = 1; seed <= 2; seed++)
        {
            await client.SendAsync(RendezvousWire.TicketRegister(Invitation(seed), [seed]));
            Assert.Equal(RendezvousKind.TicketAccepted, (await client.ReadFrameAsync())![0]);
        }

        await client.SendAsync(RendezvousWire.TicketRegister(Invitation(3), [3]));
        await AssertErrorAsync(client, "trop d'invitations");

        // La connexion tient : un refus de dépôt n'est pas une faute de protocole.
        await client.SendAsync(RendezvousWire.TicketRedeem(Invitation(1)));
        Assert.Equal(RendezvousKind.TicketPayload, (await client.ReadFrameAsync())![0]);

        // Et la place retirée se reprend.
        await client.SendAsync(RendezvousWire.TicketRegister(Invitation(3), [3]));
        Assert.Equal(RendezvousKind.TicketAccepted, (await client.ReadFrameAsync())![0]);
    }

    [Fact]
    public async Task Les_invitations_sont_plafonnees_au_total()
    {
        await using var harness = await ServerHarness.StartAsync(new RendezvousLimits { MaxInvitations = 1 });

        var client = await harness.ConnectAsync();
        await client.SendAsync(RendezvousWire.TicketRegister(Invitation(1), [1]));
        Assert.Equal(RendezvousKind.TicketAccepted, (await client.ReadFrameAsync())![0]);

        // Redéposer le même ticket remplace, sans compter double.
        await client.SendAsync(RendezvousWire.TicketRegister(Invitation(1), [9]));
        Assert.Equal(RendezvousKind.TicketAccepted, (await client.ReadFrameAsync())![0]);

        await client.SendAsync(RendezvousWire.TicketRegister(Invitation(2), [2]));
        await AssertErrorAsync(client, "trop d'invitations");
        Assert.Equal(1, harness.Server.Snapshot().PendingInvitations);
    }

    [Fact]
    public async Task Une_invitation_expiree_libere_sa_place()
    {
        await using var harness = await ServerHarness.StartAsync(new RendezvousLimits { MaxInvitations = 1 });

        var client = await harness.ConnectAsync();
        await client.SendAsync(RendezvousWire.TicketRegister(Invitation(1), [1]));
        Assert.Equal(RendezvousKind.TicketAccepted, (await client.ReadFrameAsync())![0]);

        harness.Clock.Advance(TimeSpan.FromHours(25));
        harness.Server.Sweep();
        Assert.Equal(0, harness.Server.Snapshot().PendingInvitations);

        await client.SendAsync(RendezvousWire.TicketRegister(Invitation(2), [2]));
        Assert.Equal(RendezvousKind.TicketAccepted, (await client.ReadFrameAsync())![0]);
    }

    [Fact]
    public async Task Un_relais_sans_partenaire_est_abandonne_apres_le_delai()
    {
        // Sans cela, n'importe qui immobilisait une socket pour toujours en
        // demandant un relais que personne ne viendrait rejoindre.
        await using var harness = await ServerHarness.StartAsync();

        var alone = await harness.ConnectAsync();
        await alone.SendAsync(RendezvousWire.RelayOpen(Ticket(0x11)));
        Assert.True(await alone.IsSilentAsync(Short));
        Assert.Equal(1, harness.Server.Snapshot().RelayWaiting);

        harness.Clock.Advance(RendezvousLimits.Default.RelayWaitTimeout + TimeSpan.FromSeconds(1));
        harness.Server.Sweep();

        await AssertErrorAsync(alone, "relais");
        Assert.True(await alone.IsClosedAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, harness.Server.Snapshot().RelayWaiting);
    }

    [Fact]
    public async Task Les_adresses_suivies_par_le_limiteur_sont_oubliees()
    {
        // Sans purge, le limiteur garde une entrée par adresse jamais vue,
        // pour toujours.
        await using var harness = await ServerHarness.StartAsync();

        var client = await harness.ConnectAsync();
        await client.SendAsync(RendezvousWire.MailboxOpen([Box(1)]));
        Assert.True(await client.IsSilentAsync(Short));
        Assert.Equal(1, harness.Server.Snapshot().TrackedAddresses);

        harness.Clock.Advance(TimeSpan.FromMinutes(2));
        harness.Server.Sweep();

        Assert.Equal(0, harness.Server.Snapshot().TrackedAddresses);
    }
}

/// <summary>Ce qui se passe quand plusieurs sessions se croisent sur une même clé.</summary>
public sealed class SharedKeyTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(400);

    private static byte[] Ticket(byte seed) => Enumerable.Repeat(seed, RendezvousTicket.SizeInBytes).ToArray();

    private static byte[] Box(byte seed) => Enumerable.Repeat(seed, RendezvousWire.MailboxAddressSize).ToArray();

    private static byte[] Announce(byte[] sealedCandidates, params byte[][] tickets)
        => RendezvousWire.Announce(new Announcement(tickets, sealedCandidates));

    /// <summary>Attend que le service ait constaté le départ d'une connexion.</summary>
    private static async Task WaitForConnectionsAsync(ServerHarness harness, int expected)
    {
        for (var i = 0; i < 100 && harness.Server.Snapshot().Connections != expected; i++)
            await Task.Delay(20);

        Assert.Equal(expected, harness.Server.Snapshot().Connections);
    }

    [Fact]
    public async Task Partir_ne_retire_pas_lattente_dun_autre_sur_le_meme_jeton()
    {
        // A et B se sont appariés sur T. C attend ensuite sur T. Le départ de A
        // retirait l'attente de C, et D ne trouvait plus personne.
        await using var harness = await ServerHarness.StartAsync();

        var a = await harness.ConnectAsync();
        var b = await harness.ConnectAsync();
        await a.SendAsync(Announce([1], Ticket(0x11)));
        Assert.True(await a.IsSilentAsync(Short));
        await b.SendAsync(Announce([2], Ticket(0x11)));
        Assert.Equal(RendezvousKind.Matched, (await a.ReadFrameAsync())![0]);
        Assert.Equal(RendezvousKind.Matched, (await b.ReadFrameAsync())![0]);

        var c = await harness.ConnectAsync();
        await c.SendAsync(Announce([3], Ticket(0x11)));
        Assert.True(await c.IsSilentAsync(Short));

        a.Dispose();
        await WaitForConnectionsAsync(harness, 2);
        Assert.Equal(1, harness.Server.Snapshot().PendingAnnouncements);

        var d = await harness.ConnectAsync();
        await d.SendAsync(Announce([4], Ticket(0x11)));

        Assert.Equal(new byte[] { 4 }, (await c.ReadFrameAsync())![1..]);
        Assert.Equal(new byte[] { 3 }, (await d.ReadFrameAsync())![1..]);
    }

    [Fact]
    public async Task Un_appariement_ne_se_produit_quune_fois_par_annonce()
    {
        // Les deux jetons d'une annonce désignent la même paire : les apparier
        // tous les deux faisait recevoir deux Matched et compter deux fois.
        await using var harness = await ServerHarness.StartAsync();

        var a = await harness.ConnectAsync();
        var b = await harness.ConnectAsync();
        await a.SendAsync(Announce([1], Ticket(0x11), Ticket(0x12)));
        Assert.True(await a.IsSilentAsync(Short));
        await b.SendAsync(Announce([2], Ticket(0x11), Ticket(0x12)));

        Assert.Equal(RendezvousKind.Matched, (await a.ReadFrameAsync())![0]);
        Assert.Equal(RendezvousKind.Matched, (await b.ReadFrameAsync())![0]);
        Assert.True(await a.IsSilentAsync(Short));
        Assert.True(await b.IsSilentAsync(Short));
        Assert.Equal(1, harness.Server.Snapshot().Matches);
    }

    [Fact]
    public async Task Une_boite_ouverte_par_deux_sessions_sert_les_deux()
    {
        // Deux clients sur la même machine, ou une reconnexion dont l'ancienne
        // session n'est pas encore tombée : la seconde ouverture écrasait la
        // première, qui ne recevait plus rien sans le savoir.
        await using var harness = await ServerHarness.StartAsync();

        var first = await harness.ConnectAsync();
        var second = await harness.ConnectAsync();
        await first.SendAsync(RendezvousWire.MailboxOpen([Box(1)]));
        await second.SendAsync(RendezvousWire.MailboxOpen([Box(1)]));
        Assert.True(await second.IsSilentAsync(Short));

        var sender = await harness.ConnectAsync();
        await sender.SendAsync(RendezvousWire.MailboxDeposit(Box(1), [7, 7]));

        Assert.Equal(new byte[] { 7, 7 }, (await first.ReadFrameAsync())![1..]);
        Assert.Equal(new byte[] { 7, 7 }, (await second.ReadFrameAsync())![1..]);
        Assert.Equal(1, harness.Server.Snapshot().OpenMailboxes);
    }

    [Fact]
    public async Task Une_boite_partagee_survit_au_depart_dune_des_sessions()
    {
        await using var harness = await ServerHarness.StartAsync();

        var first = await harness.ConnectAsync();
        var second = await harness.ConnectAsync();
        await first.SendAsync(RendezvousWire.MailboxOpen([Box(1)]));
        await second.SendAsync(RendezvousWire.MailboxOpen([Box(1)]));
        Assert.True(await second.IsSilentAsync(Short));

        second.Dispose();
        await WaitForConnectionsAsync(harness, 1);
        Assert.Equal(1, harness.Server.Snapshot().OpenMailboxes);

        var sender = await harness.ConnectAsync();
        await sender.SendAsync(RendezvousWire.MailboxDeposit(Box(1), [7]));

        Assert.Equal(new byte[] { 7 }, (await first.ReadFrameAsync())![1..]);

        first.Dispose();
        await WaitForConnectionsAsync(harness, 1);
        Assert.Equal(0, harness.Server.Snapshot().OpenMailboxes);
    }

    [Fact]
    public async Task Deux_demandes_de_relais_sont_pontees()
    {
        await using var harness = await ServerHarness.StartAsync();

        var a = await harness.ConnectAsync();
        var b = await harness.ConnectAsync();
        await a.SendAsync(RendezvousWire.RelayOpen(Ticket(0x11)));
        Assert.True(await a.IsSilentAsync(Short));
        await b.SendAsync(RendezvousWire.RelayOpen(Ticket(0x11)));

        Assert.Equal(RendezvousKind.RelayReady, (await a.ReadFrameAsync())![0]);
        Assert.Equal(RendezvousKind.RelayReady, (await b.ReadFrameAsync())![0]);

        await a.SendAsync(RendezvousWire.RelayData([9, 9, 9]));

        Assert.Equal(new byte[] { 9, 9, 9 }, (await b.ReadFrameAsync())![1..]);
        Assert.Equal(0, harness.Server.Snapshot().RelayWaiting);
    }

    [Fact]
    public async Task Deux_demandes_simultanees_sont_pontees()
    {
        // Les deux pairs passent au relais après le même budget de perçage,
        // donc à la même milliseconde. Chacun ne trouvait personne en attente,
        // chacun se garait, et le second écrasait le premier : « relais sans
        // partenaire » trente secondes plus tard. Vu avec le banc, deux fois
        // sur deux.
        await using var harness = await ServerHarness.StartAsync(
            RendezvousLimits.Default with { AnnouncementsPerMinute = 10_000 });

        for (var round = 0; round < 50; round++)
        {
            var a = await harness.ConnectAsync();
            var b = await harness.ConnectAsync();
            var ticket = Ticket((byte)round);

            await Task.WhenAll(
                a.SendAsync(RendezvousWire.RelayOpen(ticket)),
                b.SendAsync(RendezvousWire.RelayOpen(ticket)));

            Assert.Equal(RendezvousKind.RelayReady, (await a.ReadFrameAsync())![0]);
            Assert.Equal(RendezvousKind.RelayReady, (await b.ReadFrameAsync())![0]);

            a.Dispose();
            b.Dispose();
        }
    }
}

/// <summary>Ce que le journal dit, et surtout ce qu'il tait.</summary>
public sealed class LogTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(400);

    private static byte[] Ticket(byte seed) => Enumerable.Repeat(seed, RendezvousTicket.SizeInBytes).ToArray();

    private static byte[] Box(byte seed) => Enumerable.Repeat(seed, RendezvousWire.MailboxAddressSize).ToArray();

    private static byte[] Invitation(byte seed) => Enumerable.Repeat(seed, RendezvousWire.InvitationTicketSize).ToArray();

    private static byte[] Announce(byte[] sealedCandidates, params byte[][] tickets)
        => RendezvousWire.Announce(new Announcement(tickets, sealedCandidates));

    /// <summary>Fait passer un appariement, un dépôt d'invitation, une boîte et une remise.</summary>
    private static async Task ExerciseAsync(ServerHarness harness)
    {
        var a = await harness.ConnectAsync();
        var b = await harness.ConnectAsync();
        await a.SendAsync(Announce([1], Ticket(0x11)));
        Assert.True(await a.IsSilentAsync(Short));
        await b.SendAsync(Announce([2], Ticket(0x11)));
        Assert.Equal(RendezvousKind.Matched, (await a.ReadFrameAsync())![0]);
        Assert.Equal(RendezvousKind.Matched, (await b.ReadFrameAsync())![0]);

        await a.SendAsync(RendezvousWire.TicketRegister(Invitation(0x0a), [1]));
        Assert.Equal(RendezvousKind.TicketAccepted, (await a.ReadFrameAsync())![0]);
        await b.SendAsync(RendezvousWire.TicketRedeem(Invitation(0x0a)));
        Assert.Equal(RendezvousKind.TicketPayload, (await b.ReadFrameAsync())![0]);

        await a.SendAsync(RendezvousWire.MailboxOpen([Box(0x0b)]));

        for (var i = 0; i < 100 && harness.Server.Snapshot().OpenMailboxes is 0; i++)
            await Task.Delay(20);

        await b.SendAsync(RendezvousWire.MailboxDeposit(Box(0x0b), [1]));
        Assert.Equal(RendezvousKind.MailboxDelivery, (await a.ReadFrameAsync())![0]);
    }

    [Fact]
    public async Task Par_defaut_le_journal_ne_porte_ni_adresse_ni_fragment()
    {
        // Un journal est un fichier qui reste : y écrire des adresses et des
        // fragments de jetons ferait du service l'index qu'il promet de ne pas
        // tenir.
        await using var harness = await ServerHarness.StartAsync();

        await ExerciseAsync(harness);

        var log = harness.Log;

        Assert.Contains("appari", log);
        Assert.Contains("invitation", log);
        Assert.DoesNotContain("127.0.0.1", log);
        Assert.DoesNotContain("1111", log);
        Assert.DoesNotContain("0a0a", log);
        Assert.DoesNotContain("0b0b", log);
    }

    [Fact]
    public async Task En_mode_verbeux_les_adresses_apparaissent_mais_jamais_le_ticket_dinvitation()
    {
        await using var harness = await ServerHarness.StartAsync(verbose: true);

        await ExerciseAsync(harness);

        var log = harness.Log;

        Assert.Contains("127.0.0.1", log);
        Assert.Contains("1111", log);
        Assert.DoesNotContain("0a0a", log);
    }
}

/// <summary>Ce que le service dit de sa propre santé, et de ce qu'il refuse.</summary>
public sealed class HealthTests
{
    private static byte[] Box(byte seed) => Enumerable.Repeat(seed, RendezvousWire.MailboxAddressSize).ToArray();

    [Fact]
    public async Task Les_deux_boucles_se_declarent_vivantes_puis_mortes_a_larret()
    {
        var harness = await ServerHarness.StartAsync();

        // La réflexion démarre dans une tâche à part : on lui laisse le temps
        // d'entrer dans sa boucle, ce qui se compte en millisecondes.
        for (var i = 0; i < 100 && harness.Server.Healthy is false; i++)
            await Task.Delay(10);

        Assert.True(harness.Server.AcceptLoop.Alive);
        Assert.True(harness.Server.ReflectLoop.Alive);
        Assert.True(harness.Server.Healthy);
        Assert.NotNull(harness.Server.AcceptLoop.LastTurn);

        await harness.DisposeAsync();

        for (var i = 0; i < 100 && harness.Server.ReflectLoop.Alive; i++)
            await Task.Delay(10);

        Assert.False(harness.Server.AcceptLoop.Alive);
        Assert.False(harness.Server.ReflectLoop.Alive);
        Assert.False(harness.Server.Healthy);
    }

    [Fact]
    public void Un_service_jamais_lance_nest_pas_sain()
    {
        var idle = new RendezvousServer(0, new PeerDirectory("peers.txt", "pending.txt"), RendezvousLimits.Default, new ManualClock());

        Assert.False(idle.Healthy);
        Assert.Null(idle.AcceptLoop.LastTurn);
    }

    [Fact]
    public async Task Les_refus_sont_ventiles_par_motif()
    {
        // Un total seul ne dit pas quoi faire : un client qui ouvre des boîtes
        // en boucle et quelqu'un qui parcourt les tickets d'invitation
        // n'appellent pas la même réponse.
        await using var harness = await ServerHarness.StartAsync(new RendezvousLimits { AnnouncementsPerMinute = 1 });

        var client = await harness.ConnectAsync();
        await client.SendAsync(RendezvousWire.MailboxOpen([Box(1)]));
        await client.SendAsync(RendezvousWire.MailboxOpen([Box(2)]));

        var frame = await client.ReadFrameAsync();
        Assert.Equal(RendezvousKind.Error, frame![0]);

        var refusals = harness.Server.Snapshot().Refusals;

        Assert.Equal(1, refusals.Mailbox);
        Assert.Equal(0, refusals.Announce);
        Assert.Equal(0, refusals.Relay);
        Assert.Equal(0, refusals.Invitation);
        Assert.Equal(1, harness.Server.Snapshot().RateRefusals);
    }
}

/// <summary>Le relais coupé depuis la console.</summary>
public sealed class RelaySwitchTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(400);

    private static byte[] Ticket(byte seed) => Enumerable.Repeat(seed, RendezvousTicket.SizeInBytes).ToArray();

    [Fact]
    public async Task Relais_coupe_la_demande_recoit_une_erreur_et_la_session_continue()
    {
        await using var harness = await ServerHarness.StartAsync(new RendezvousLimits { RelayEnabled = false });

        var client = await harness.ConnectAsync();
        await client.SendAsync(RendezvousWire.RelayOpen(Ticket(0x11)));

        var frame = await client.ReadFrameAsync();

        Assert.Equal(RendezvousKind.Error, frame![0]);
        Assert.Contains("relais", System.Text.Encoding.UTF8.GetString(frame, 1, frame.Length - 1));
        Assert.Equal(0, harness.Server.Snapshot().RelayWaiting);

        // La connexion tient, et le refus n'a pas compté dans le limiteur.
        await client.SendAsync(RendezvousWire.MailboxOpen([new byte[RendezvousWire.MailboxAddressSize]]));
        Assert.True(await client.IsSilentAsync(Short));
        Assert.Equal(1, harness.Server.Snapshot().OpenMailboxes);
        Assert.Equal(0, harness.Server.Snapshot().Refusals.Relay);
    }

    [Fact]
    public async Task Le_relais_se_coupe_et_se_rouvre_a_chaud()
    {
        await using var harness = await ServerHarness.StartAsync();

        harness.Server.Limits = harness.Server.Limits with { RelayEnabled = false };

        var client = await harness.ConnectAsync();
        await client.SendAsync(RendezvousWire.RelayOpen(Ticket(0x11)));
        Assert.Equal(RendezvousKind.Error, (await client.ReadFrameAsync())![0]);

        harness.Server.Limits = harness.Server.Limits with { RelayEnabled = true };

        await client.SendAsync(RendezvousWire.RelayOpen(Ticket(0x12)));
        Assert.True(await client.IsSilentAsync(Short));
        Assert.Equal(1, harness.Server.Snapshot().RelayWaiting);
    }
}
