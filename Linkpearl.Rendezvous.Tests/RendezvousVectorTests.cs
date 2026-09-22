using System.Text;
using System.Text.Json;
using Linkpearl.Core.Transport.Rendezvous;
using Xunit;

namespace Linkpearl.Core.Tests.Rendezvous;

/// <summary>
/// Le fil de rendez-vous, figé octet par octet.
/// </summary>
/// <remarks>
/// Le plugin et le serveur vivent dans deux dépôts, et chacun porte sa copie
/// des sources du format. Deux copies divergent toujours un jour, et la panne
/// qui s'ensuit est de celles qui coûtent une soirée : deux joueurs qui ne se
/// voient plus, sans message d'erreur qui dise pourquoi.
///
/// Ce fichier de vecteurs est identique dans les deux dépôts, et ce test est
/// identique lui aussi. Le premier côté qui touche à un octet du format casse
/// son propre test, avant d'avoir livré quoi que ce soit.
///
/// Ce qu'il atteste et rien de plus : la cohérence de format entre les deux
/// copies. Les vecteurs ont été produits par l'implémentation qu'ils testent,
/// donc ils n'attrapent pas une erreur de conception, seulement une dérive.
/// </remarks>
public class RendezvousVectorTests
{
    private static byte[] Repeat(byte seed, int length) => Enumerable.Repeat(seed, length).ToArray();

    private static byte[] TicketA => Repeat(0x11, RendezvousTicket.SizeInBytes);

    private static byte[] TicketB => Repeat(0x22, RendezvousTicket.SizeInBytes);

    private static byte[] Candidates => [0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03];

    private static byte[] AddressA => Repeat(0xA1, RendezvousWire.MailboxAddressSize);

    private static byte[] AddressB => Repeat(0xB2, RendezvousWire.MailboxAddressSize);

    private static byte[] Payload => Encoding.UTF8.GetBytes("charge utile scellee");

    private static readonly (string Name, Func<byte[]> Build)[] Frames =
    [
        ("annonce", () => RendezvousWire.Announce(new Announcement([TicketA, TicketB], Candidates))),
        ("apparie", () => RendezvousWire.Matched(Candidates)),
        ("relais-ouverture", () => RendezvousWire.RelayOpen(TicketA)),
        ("relais-pret", () => RendezvousWire.Simple(RendezvousKind.RelayReady)),
        ("relais-donnees", () => RendezvousWire.RelayData(Payload)),
        ("erreur", () => RendezvousWire.Error("jeton inconnu")),
        ("ticket-enregistrement", () => RendezvousWire.TicketRegister(TicketA, Payload)),
        ("ticket-retrait", () => RendezvousWire.TicketRedeem(TicketA)),
        ("ticket-charge", () => RendezvousWire.TicketPayload(Payload)),
        ("boite-ouverture", () => RendezvousWire.MailboxOpen([AddressA, AddressB])),
        ("boite-interrogation", () => RendezvousWire.MailboxQuery([AddressA, AddressB])),
        ("boite-presence", () => RendezvousWire.MailboxPresence([true, false, true])),
        ("boite-depot", () => RendezvousWire.MailboxDeposit(AddressA, Payload)),
        ("boite-remise", () => RendezvousWire.MailboxDelivery(Payload)),
    ];

    private static JsonElement Document()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "rendezvous-vectors.json");

        Assert.True(File.Exists(path), $"vecteurs introuvables : {path}");

        return JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
    }

    [Fact]
    public void Le_fichier_de_vecteurs_les_porte_tous()
    {
        // Garde-fou du garde-fou : un fichier tronqué ferait passer au vert un
        // test qui ne vérifierait plus rien.
        var names = Document().GetProperty("trames").EnumerateArray()
            .Select(entry => entry.GetProperty("nom").GetString())
            .ToList();

        Assert.Equal(Frames.Select(frame => frame.Name), names);
    }

    [Fact]
    public void Chaque_trame_se_reproduit_a_l_octet_pres()
    {
        var expected = Document().GetProperty("trames").EnumerateArray()
            .ToDictionary(entry => entry.GetProperty("nom").GetString()!, entry => entry.GetProperty("hex").GetString()!);

        foreach (var (name, build) in Frames)
            Assert.Equal(expected[name], Convert.ToHexStringLower(build()));
    }

    [Theory]
    [InlineData("tailleTicket", RendezvousTicket.SizeInBytes)]
    [InlineData("tailleAdresseBoite", RendezvousWire.MailboxAddressSize)]
    [InlineData("tailleTicketInvitation", RendezvousWire.InvitationTicketSize)]
    [InlineData("longueurTrameMax", RendezvousWire.MaxFrameLength)]
    [InlineData("ticketsParAnnonceMax", RendezvousWire.MaxTicketsPerAnnouncement)]
    [InlineData("candidatsScellesMax", RendezvousWire.MaxSealedCandidatesLength)]
    [InlineData("adressesInterrogeesMax", RendezvousWire.MaxQueriedAddresses)]
    [InlineData("depotMax", RendezvousWire.MaxDepositLength)]
    [InlineData("chargeTicketMax", RendezvousWire.MaxTicketPayloadLength)]
    public void Les_plafonds_sont_les_memes_des_deux_cotes(string name, int expected)
    {
        // Un plafond qui diverge ne casse pas le format : il fait refuser chez
        // l'un ce que l'autre accepte, ce qui est plus difficile à diagnostiquer
        // qu'une trame illisible.
        Assert.Equal(expected, Document().GetProperty("constantes").GetProperty(name).GetInt32());
    }

    [Fact]
    public void La_fenetre_du_jeton_est_la_meme_des_deux_cotes()
    {
        Assert.Equal(
            (int)RendezvousTicket.Window.TotalMinutes,
            Document().GetProperty("constantes").GetProperty("fenetreMinutes").GetInt32());
    }
}
