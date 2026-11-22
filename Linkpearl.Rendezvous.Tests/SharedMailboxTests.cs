using Linkpearl.Core.Transport.Rendezvous;
using Xunit;

namespace Linkpearl.Rendezvous.Tests;

/// <summary>
/// Les boîtes partagées, celles d'admission : personne ne les prend pour soi seul.
/// </summary>
/// <remarks>
/// Le constat de l'audit : un porteur du code d'un groupe réclamait d'avance
/// ses boîtes d'admission, que les modérateurs ouvrent ensemble sans
/// exclusivité. Il recevait seul chaque candidature, sans que personne le sache.
/// </remarks>
public sealed class SharedMailboxTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(400);

    private static byte[] Box(byte seed) => Enumerable.Repeat(seed, RendezvousWire.MailboxAddressSize).ToArray();

    private static async Task<bool[]> BitsAsync(TestClient client)
    {
        var frame = await client.ReadFrameAsync();

        Assert.NotNull(frame);
        Assert.Equal(RendezvousKind.MailboxClaimed, frame[0]);
        Assert.True(RendezvousWire.TryReadPresence(frame, out var bits));
        return bits;
    }

    [Fact]
    public async Task Une_reclamation_ne_ferme_pas_une_boite_partagee_aux_moderateurs()
    {
        await using var harness = await ServerHarness.StartAsync();

        var squatter = await harness.ConnectAsync();
        await squatter.SendAsync(RendezvousWire.MailboxClaim([Box(1)]));
        Assert.Equal(new[] { true }, await BitsAsync(squatter));

        var moderator = await harness.ConnectAsync();
        await moderator.SendAsync(RendezvousWire.SharedMailboxOpen([Box(1)]));
        Assert.Equal(new[] { true }, await BitsAsync(moderator));

        var candidate = await harness.ConnectAsync();
        await candidate.SendAsync(RendezvousWire.SharedMailboxDeposit(Box(1), [9]));
        Assert.Equal(new[] { true }, await BitsAsync(candidate));

        Assert.Equal(new byte[] { 9 }, (await moderator.ReadFrameAsync())![1..]);
        Assert.True(await squatter.IsSilentAsync(Short));
    }

    [Fact]
    public async Task Un_depot_ordinaire_ne_va_jamais_dans_une_boite_partagee()
    {
        // Sans cette séparation, ouvrir en partage la boîte personnelle d'un
        // autre suffisait à lire les demandes qui lui sont adressées.
        await using var harness = await ServerHarness.StartAsync();

        var intruder = await harness.ConnectAsync();
        await intruder.SendAsync(RendezvousWire.SharedMailboxOpen([Box(2)]));
        Assert.Equal(new[] { true }, await BitsAsync(intruder));

        var owner = await harness.ConnectAsync();
        await owner.SendAsync(RendezvousWire.MailboxClaim([Box(2)]));
        Assert.Equal(new[] { true }, await BitsAsync(owner));

        var requester = await harness.ConnectAsync();
        await requester.SendAsync(RendezvousWire.MailboxDeposit(Box(2), [7]));

        Assert.Equal(new byte[] { 7 }, (await owner.ReadFrameAsync())![1..]);
        Assert.True(await intruder.IsSilentAsync(Short));
    }

    [Fact]
    public async Task Un_depot_partage_sans_detenteur_le_dit()
    {
        await using var harness = await ServerHarness.StartAsync();

        var candidate = await harness.ConnectAsync();
        await candidate.SendAsync(RendezvousWire.SharedMailboxDeposit(Box(3), [1]));

        Assert.Equal(new[] { false }, await BitsAsync(candidate));
    }

    [Fact]
    public async Task Une_meme_adresse_reseau_ne_tient_que_deux_places_dans_une_boite_partagee()
    {
        // Le registre partagé n'a pas d'exclusivité : sans cette borne, un
        // seul réseau en remplissait toutes les places et revenait à l'écarter.
        await using var harness = await ServerHarness.StartAsync();

        var bits = new List<bool>();

        for (var i = 0; i < 3; i++)
        {
            var holder = await harness.ConnectAsync();
            await holder.SendAsync(RendezvousWire.SharedMailboxOpen([Box(4)]));
            bits.AddRange(await BitsAsync(holder));
        }

        Assert.Equal(new[] { true, true, false }, bits);
    }
}
