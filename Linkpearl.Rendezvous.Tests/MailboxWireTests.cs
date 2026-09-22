using Linkpearl.Core.Transport.Rendezvous;
using Xunit;

namespace Linkpearl.Core.Tests.Rendezvous;

public class MailboxWireTests
{
    private static byte[] Address(byte seed)
        => Enumerable.Repeat(seed, RendezvousWire.MailboxAddressSize).ToArray();

    [Fact]
    public void Une_interrogation_fait_l_aller_retour()
    {
        var frame = RendezvousWire.MailboxQuery([Address(1), Address(2), Address(3)]);

        Assert.True(RendezvousWire.TryReadAddresses(frame, out var addresses, out var why), why);
        Assert.Equal(3, addresses.Count);
        Assert.Equal(Address(2), addresses[1]);
    }

    [Fact]
    public void Trop_d_adresses_interrogees_est_refuse()
    {
        // Sans plafond, un client transformerait le rendez-vous en service
        // d'énumération des joueurs en ligne.
        var frame = new byte[2 + (200 * RendezvousWire.MailboxAddressSize)];
        frame[0] = RendezvousKind.MailboxQuery;
        frame[1] = 200;

        Assert.False(RendezvousWire.TryReadAddresses(frame, out _, out var why));
        Assert.Contains("adresses", why!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    public void Une_interrogation_tronquee_est_refusee_sans_lever(int length)
    {
        Assert.False(RendezvousWire.TryReadAddresses(new byte[length], out _, out _));
    }

    [Fact]
    public void La_presence_se_lit_bit_a_bit_dans_l_ordre_de_la_question()
    {
        var expected = new[] { true, false, false, true, true, false, false, false, true };

        Assert.True(RendezvousWire.TryReadPresence(RendezvousWire.MailboxPresence(expected), out var read));
        Assert.Equal(expected, read);
    }

    [Fact]
    public void Une_presence_vide_se_lit_sans_erreur()
    {
        Assert.True(RendezvousWire.TryReadPresence(RendezvousWire.MailboxPresence([]), out var read));
        Assert.Empty(read);
    }

    [Fact]
    public void Un_depot_porte_l_adresse_et_la_charge()
    {
        var frame = RendezvousWire.MailboxDeposit(Address(7), [1, 2, 3]);

        Assert.Equal(RendezvousKind.MailboxDeposit, frame[0]);
        Assert.Equal(Address(7), frame[1..(1 + RendezvousWire.MailboxAddressSize)]);
        Assert.Equal(new byte[] { 1, 2, 3 }, frame[(1 + RendezvousWire.MailboxAddressSize)..]);
    }
}
