using Linkpearl.Core.Safety;
using Xunit;

namespace Linkpearl.Core.Tests.Safety;

/// <summary>
/// La liste de bannissement, telle que le service la publie et que le client
/// la vérifie. Les deux doivent dériver à l'identique, sans quoi la liste ne
/// protège personne.
/// </summary>
public class BanListTests
{
    private static readonly byte[] Salt = Convert.FromHexString("00112233445566778899aabbccddeeff");

    [Fact]
    public void Un_nom_banni_est_reconnu()
    {
        var hash = BanList.Derive("Ysolde Aldebrand", 21, Salt, BanParameters.Default);
        var list = new BanList(Salt, BanParameters.Default, [new BanEntry(hash, "contenu illegal", 0)]);

        Assert.True(list.Contains("Ysolde Aldebrand", 21));
    }

    [Fact]
    public void Un_nom_absent_ne_l_est_pas()
    {
        var list = new BanList(Salt, BanParameters.Default, []);

        Assert.False(list.Contains("Ysolde Aldebrand", 21));
    }

    [Fact]
    public void Le_monde_fait_partie_de_l_identite()
    {
        // Deux homonymes sur deux mondes sont deux personnes différentes, et
        // bannir l'une ne doit pas atteindre l'autre.
        var hash = BanList.Derive("Ysolde Aldebrand", 21, Salt, BanParameters.Default);
        var list = new BanList(Salt, BanParameters.Default, [new BanEntry(hash, "", 0)]);

        Assert.True(list.Contains("Ysolde Aldebrand", 21));
        Assert.False(list.Contains("Ysolde Aldebrand", 36));
    }

    [Fact]
    public void La_casse_et_les_espaces_de_bord_ne_comptent_pas()
    {
        // Le nom vient de l'opérateur, qui le recopie à la main depuis un
        // signalement. Une majuscule de trop ne doit pas rendre le bannissement
        // sans effet.
        var hash = BanList.Derive("Ysolde Aldebrand", 21, Salt, BanParameters.Default);
        var list = new BanList(Salt, BanParameters.Default, [new BanEntry(hash, "", 0)]);

        Assert.True(list.Contains("  ysolde aldebrand  ", 21));
    }

    [Fact]
    public void Un_sel_different_donne_une_empreinte_differente()
    {
        // Sans cela, une liste publiée servirait de table précalculée pour une
        // autre.
        var other = new byte[Salt.Length];
        Salt.CopyTo(other, 0);
        other[0] ^= 0xFF;

        Assert.NotEqual(
            BanList.Derive("Ysolde Aldebrand", 21, Salt, BanParameters.Default),
            BanList.Derive("Ysolde Aldebrand", 21, other, BanParameters.Default));
    }

    [Fact]
    public void La_liste_fait_l_aller_retour_par_son_format_publie()
    {
        var hash = BanList.Derive("Ysolde Aldebrand", 21, Salt, BanParameters.Default);
        var list = new BanList(Salt, BanParameters.Default, [new BanEntry(hash, "contenu illegal", 1789900000)]);

        Assert.True(BanList.TryParse(list.ToJson(1790000000), out var back, out var why), why);
        Assert.True(back!.Contains("Ysolde Aldebrand", 21));
        Assert.Equal("contenu illegal", back.Entries[0].Reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("pas du json")]
    [InlineData("{}")]
    [InlineData("{\"version\":99,\"salt\":\"00\",\"entries\":[]}")]
    public void Une_liste_illisible_est_refusee_sans_lever(string text)
    {
        // Elle vient du réseau : un service hostile ou une version future ne
        // doit pas faire tomber le client.
        Assert.False(BanList.TryParse(text, out _, out var why));
        Assert.NotNull(why);
    }

    [Fact]
    public void Une_deriviation_coute_assez_cher_pour_decourager_l_enumeration()
    {
        // Le but n'est pas la lenteur en soi : c'est qu'énumérer l'espace des
        // noms de FFXIV pour fabriquer un annuaire de joueurs utilisant des
        // mods soit hors de prix, alors que vérifier une personne qu'on a
        // devant soi reste gratuit.
        var watch = System.Diagnostics.Stopwatch.StartNew();
        BanList.Derive("Ysolde Aldebrand", 21, Salt, BanParameters.Default);
        watch.Stop();

        Assert.True(watch.ElapsedMilliseconds >= 20,
            $"dérivation trop rapide : {watch.ElapsedMilliseconds} ms");
    }
}
