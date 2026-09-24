using Linkpearl.Core.Safety;
using Linkpearl.Core.Transport.Rendezvous;
using Xunit;

namespace Linkpearl.Rendezvous.Tests;

public sealed class BanListServiceTests
{
    private static async Task<(int Pages, BanList List)> PageAsync(TestClient client, int page)
    {
        await client.SendAsync(RendezvousWire.BanListQuery(page));
        var frame = await client.ReadFrameAsync();

        Assert.NotNull(frame);
        Assert.True(RendezvousWire.TryReadBanListData(frame, out var index, out var pages, out var json, out var why), why);
        Assert.Equal(page, index);
        Assert.True(BanList.TryParse(json, out var list, out why), why);
        return (pages, list!);
    }

    [Fact]
    public async Task Une_liste_vide_tient_en_une_page_et_porte_son_sel()
    {
        await using var harness = await ServerHarness.StartAsync();
        using var client = await harness.ConnectAsync();

        var (pages, list) = await PageAsync(client, 0);

        Assert.Equal(1, pages);
        Assert.Empty(list.Entries);
        Assert.Equal(harness.Bans.Current().Salt, list.Salt);
    }

    [Fact]
    public async Task Une_liste_de_70_entrees_tient_en_deux_pages()
    {
        await using var harness = await ServerHarness.StartAsync();

        // Import plutôt qu'Add : Add dérive en PBKDF2 à 600 000 itérations, et
        // soixante-dix dérivations rendraient le test lent pour rien.
        var current = harness.Bans.Current();
        var entries = Enumerable.Range(0, 70)
            .Select(i => new BanEntry(Enumerable.Repeat((byte)i, 32).ToArray(), $"motif {i}", i))
            .ToList();
        Assert.True(harness.Bans.Import(new BanList(current.Salt, current.Parameters, entries).ToJson(0)).Ok);

        using var client = await harness.ConnectAsync();

        var (pages, first) = await PageAsync(client, 0);
        var (_, second) = await PageAsync(client, 1);

        Assert.Equal(2, pages);
        Assert.Equal(RendezvousWire.BanListPageEntries, first.Entries.Count);
        Assert.Equal(70 - RendezvousWire.BanListPageEntries, second.Entries.Count);
    }

    [Fact]
    public async Task Une_page_hors_de_la_liste_recoit_une_erreur_et_la_connexion_reste()
    {
        await using var harness = await ServerHarness.StartAsync();
        using var client = await harness.ConnectAsync();

        await client.SendAsync(RendezvousWire.BanListQuery(5));
        var frame = await client.ReadFrameAsync();

        Assert.Equal(RendezvousKind.Error, frame![0]);

        // Toujours là : une page de trop n'est pas une faute du client.
        var (pages, _) = await PageAsync(client, 0);
        Assert.Equal(1, pages);
    }

    [Fact]
    public async Task Au_dela_du_plafond_de_pages_la_connexion_est_fermee()
    {
        await using var harness = await ServerHarness.StartAsync();
        using var client = await harness.ConnectAsync();

        for (var i = 0; i < RendezvousWire.MaxBanListPages; i++)
            await PageAsync(client, 0);

        await client.SendAsync(RendezvousWire.BanListQuery(0));

        // Des erreurs d'abord, pour que le client sache pourquoi (la boucle du
        // service ajoute la sienne à celle du gestionnaire), puis la fermeture.
        var frames = new List<byte[]>();

        while (frames.Count < 4 && await client.ReadFrameAsync(TimeSpan.FromSeconds(5)) is { } frame)
            frames.Add(frame);

        Assert.NotEmpty(frames);
        Assert.All(frames, frame => Assert.Equal(RendezvousKind.Error, frame[0]));
        Assert.True(frames.Count < 4, "la connexion est restée ouverte");
    }
}
