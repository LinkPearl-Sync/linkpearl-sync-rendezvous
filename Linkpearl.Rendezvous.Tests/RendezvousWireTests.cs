using Linkpearl.Core.Transport.Rendezvous;
using Xunit;

namespace Linkpearl.Core.Tests.Rendezvous;

public class RendezvousWireTests
{
    private static byte[] Ticket(byte seed) => Enumerable.Repeat(seed, RendezvousTicket.SizeInBytes).ToArray();

    [Fact]
    public void Une_annonce_fait_l_aller_retour()
    {
        var original = new Announcement([Ticket(1), Ticket(2)], [9, 8, 7]);

        Assert.True(RendezvousWire.TryReadAnnounce(RendezvousWire.Announce(original), out var parsed, out var why), why);
        Assert.Equal(2, parsed!.Tickets.Count);
        Assert.Equal(Ticket(1), parsed.Tickets[0]);
        Assert.Equal(new byte[] { 9, 8, 7 }, parsed.SealedCandidates);
    }

    [Fact]
    public void Une_annonce_sans_jeton_est_refusee()
    {
        Assert.False(RendezvousWire.TryReadAnnounce([RendezvousKind.Announce, 0], out _, out var why));
        Assert.NotNull(why);
    }

    [Fact]
    public void Trop_de_jetons_est_refuse()
    {
        // Un client qui annoncerait mille jetons ferait du serveur un index
        // gratuit : le plafond est une mesure anti-abus, pas une élégance.
        var frame = new byte[2 + (200 * RendezvousTicket.SizeInBytes) + 2];
        frame[0] = RendezvousKind.Announce;
        frame[1] = 200;

        Assert.False(RendezvousWire.TryReadAnnounce(frame, out _, out var why));
        Assert.Contains("jetons", why!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(10)]
    public void Une_annonce_tronquee_est_refusee_sans_lever(int length)
    {
        Assert.False(RendezvousWire.TryReadAnnounce(new byte[length], out _, out _));
    }

    [Fact]
    public void Un_bloc_de_candidats_hors_bornes_est_refuse()
    {
        var frame = new byte[2 + RendezvousTicket.SizeInBytes + 2];
        frame[0] = RendezvousKind.Announce;
        frame[1] = 1;
        frame[^2] = 0xFF;
        frame[^1] = 0xFF;

        Assert.False(RendezvousWire.TryReadAnnounce(frame, out _, out var why));
        Assert.NotNull(why);
    }

    [Fact]
    public void Le_prefixe_de_longueur_delimite_la_trame()
    {
        var framed = RendezvousWire.Frame([1, 2, 3]);

        Assert.Equal(7, framed.Length);
        Assert.Equal(3, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(framed));
    }
}
