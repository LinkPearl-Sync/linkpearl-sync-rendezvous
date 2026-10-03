using Linkpearl.Core.Transport.Rendezvous;
using Linkpearl.Rendezvous;
using Xunit;

namespace Linkpearl.Rendezvous.Tests;

/// <summary>Les boîtes réclamées : un seul détenteur, et personne pour écouter à sa place.</summary>
public sealed class MailboxClaimTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(400);

    private static byte[] Box(byte seed) => Enumerable.Repeat(seed, RendezvousWire.MailboxAddressSize).ToArray();

    private static async Task<bool[]> ClaimAsync(TestClient client, params byte[][] boxes)
    {
        await client.SendAsync(RendezvousWire.MailboxClaim(boxes));
        var frame = await client.ReadFrameAsync();

        Assert.NotNull(frame);
        Assert.Equal(RendezvousKind.MailboxClaimed, frame[0]);
        Assert.True(RendezvousWire.TryReadPresence(frame, out var held));
        return held;
    }

    [Fact]
    public async Task Une_boite_libre_est_a_qui_la_reclame_et_pas_a_un_second()
    {
        await using var harness = await ServerHarness.StartAsync();

        var owner = await harness.ConnectAsync();
        var intruder = await harness.ConnectAsync();

        Assert.Equal(new[] { true, true }, await ClaimAsync(owner, Box(1), Box(2)));

        // Rien n'est ajouté pour celui qui arrive après : ni la boîte prise,
        // ni sa place, alors que la boîte libre est bien à lui.
        Assert.Equal(new[] { false, true }, await ClaimAsync(intruder, Box(1), Box(3)));

        var sender = await harness.ConnectAsync();
        await sender.SendAsync(RendezvousWire.MailboxDeposit(Box(1), [5]));

        Assert.Equal(new byte[] { 5 }, (await owner.ReadFrameAsync())![1..]);
        Assert.True(await intruder.IsSilentAsync(Short));
    }

    [Fact]
    public async Task Reclamer_deux_fois_la_meme_boite_la_garde()
    {
        await using var harness = await ServerHarness.StartAsync();

        var owner = await harness.ConnectAsync();

        Assert.Equal(new[] { true }, await ClaimAsync(owner, Box(1)));
        Assert.Equal(new[] { true }, await ClaimAsync(owner, Box(1)));
        Assert.Equal(1, harness.Server.Snapshot().OpenMailboxes);
    }

    [Fact]
    public async Task Une_ouverture_ordinaire_ne_prend_pas_place_dans_une_boite_reclamee()
    {
        await using var harness = await ServerHarness.StartAsync();

        var owner = await harness.ConnectAsync();
        Assert.Equal(new[] { true }, await ClaimAsync(owner, Box(1)));

        var intruder = await harness.ConnectAsync();
        await intruder.SendAsync(RendezvousWire.MailboxOpen([Box(1)]));
        Assert.True(await intruder.IsSilentAsync(Short));

        var sender = await harness.ConnectAsync();
        await sender.SendAsync(RendezvousWire.MailboxDeposit(Box(1), [6]));

        Assert.Equal(new byte[] { 6 }, (await owner.ReadFrameAsync())![1..]);
        Assert.True(await intruder.IsSilentAsync(Short));
    }

    [Fact]
    public async Task Une_boite_deja_ouverte_par_un_autre_ne_se_reclame_pas()
    {
        await using var harness = await ServerHarness.StartAsync();

        var first = await harness.ConnectAsync();
        await first.SendAsync(RendezvousWire.MailboxOpen([Box(1)]));
        await ServerHarness.WaitUntilAsync(() => harness.Server.Snapshot().OpenMailboxes is 1);

        var claimer = await harness.ConnectAsync();
        Assert.Equal(new[] { false }, await ClaimAsync(claimer, Box(1)));

        // Le premier détenteur la tenait seul sans l'avoir réclamée : il peut
        // la réclamer à son tour.
        Assert.Equal(new[] { true }, await ClaimAsync(first, Box(1)));
    }

    [Fact]
    public async Task Une_boite_reclamee_se_libere_au_depart_de_son_detenteur()
    {
        await using var harness = await ServerHarness.StartAsync();

        var owner = await harness.ConnectAsync();
        Assert.Equal(new[] { true }, await ClaimAsync(owner, Box(1)));

        owner.Dispose();
        await ServerHarness.WaitUntilAsync(() => harness.Server.Snapshot().OpenMailboxes is 0);

        var next = await harness.ConnectAsync();
        Assert.Equal(new[] { true }, await ClaimAsync(next, Box(1)));
    }

    [Fact]
    public async Task Une_reclamation_compte_dans_le_limiteur_et_le_plafond_de_boites()
    {
        await using var harness = await ServerHarness.StartAsync(new RendezvousLimits { MaxMailboxesPerSession = 2 });

        var client = await harness.ConnectAsync();
        Assert.Equal(new[] { true, true }, await ClaimAsync(client, Box(1), Box(2)));

        await client.SendAsync(RendezvousWire.MailboxClaim([Box(3)]));
        var frame = await client.ReadFrameAsync();

        Assert.Equal(RendezvousKind.Error, frame![0]);

        // Une seconde erreur générique peut suivre, puis la fermeture.
        byte[]? next;
        while ((next = await client.ReadFrameAsync()) is not null)
            Assert.Equal(RendezvousKind.Error, next[0]);
    }

    [Fact]
    public async Task Une_boite_ordinaire_na_pas_plus_de_quatre_detenteurs()
    {
        await using var harness = await ServerHarness.StartAsync();

        var holders = new List<TestClient>();

        for (var i = 0; i < 5; i++)
        {
            var holder = await harness.ConnectAsync();
            await holder.SendAsync(RendezvousWire.MailboxOpen([Box(1)]));
            Assert.True(await holder.IsSilentAsync(TimeSpan.FromMilliseconds(100)));
            holders.Add(holder);
        }

        var sender = await harness.ConnectAsync();
        await sender.SendAsync(RendezvousWire.MailboxDeposit(Box(1), [8]));

        foreach (var holder in holders.Take(4))
            Assert.Equal(new byte[] { 8 }, (await holder.ReadFrameAsync())![1..]);

        Assert.True(await holders[4].IsSilentAsync(Short));
    }
}
