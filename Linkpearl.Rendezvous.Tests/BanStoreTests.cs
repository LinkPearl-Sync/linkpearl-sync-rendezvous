using Linkpearl.Core.Safety;
using Linkpearl.Rendezvous;
using Xunit;

namespace Linkpearl.Rendezvous.Tests;

/// <summary>
/// Le magasin de bannissements : un fichier, un sel qui ne change jamais, et
/// des empreintes dont le nom d'origine n'est pas conservé.
/// </summary>
public sealed class BanStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"lprdv-bans-{Guid.NewGuid():N}");

    private string Path_ => System.IO.Path.Combine(_dir, "bans.json");

    public BanStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Le_sel_ne_change_pas_dun_demarrage_a_lautre()
    {
        var premier = new BanStore(Path_).Current().Salt;
        var second = new BanStore(Path_).Current().Salt;

        Assert.Equal(premier, second);
    }

    [Fact]
    public void Une_entree_sajoute_se_retrouve_et_se_retire()
    {
        var store = new BanStore(Path_);

        var hash = store.Add("Nom Fictif", 42, "contenu illegal");

        Assert.NotNull(hash);
        Assert.True(store.Current().Contains("Nom Fictif", 42));

        // La normalisation vaut des deux côtés : un opérateur qui recopie un nom
        // à la main met les majuscules où il veut.
        Assert.True(store.Current().Contains("nom fictif", 42));
        Assert.False(store.Current().Contains("Nom Fictif", 43));

        Assert.True(store.Remove(hash!));
        Assert.False(store.Current().Contains("Nom Fictif", 42));
        Assert.False(store.Remove(hash!));
    }

    [Fact]
    public void Le_meme_personnage_ne_sajoute_pas_deux_fois()
    {
        var store = new BanStore(Path_);

        Assert.NotNull(store.Add("Nom Fictif", 42, "premier"));
        Assert.Null(store.Add("Nom Fictif", 42, "second"));
        Assert.Single(store.Current().Entries);
    }

    [Fact]
    public void Une_entree_survit_au_redemarrage()
    {
        new BanStore(Path_).Add("Nom Fictif", 42, "contenu illegal");

        Assert.True(new BanStore(Path_).Current().Contains("Nom Fictif", 42));
    }

    [Fact]
    public void Le_motif_est_borne_et_reduit_a_lascii_lisible()
    {
        var store = new BanStore(Path_);
        store.Add("Nom Fictif", 42, new string('x', 400) + "\u0000中");

        var reason = store.Current().Entries[0].Reason;

        Assert.Equal(BanList.MaxReasonLength, reason.Length);
        Assert.All(reason, c => Assert.InRange(c, ' ', '~'));
    }

    [Fact]
    public void Un_motif_vide_devient_lisible()
    {
        var store = new BanStore(Path_);
        store.Add("Nom Fictif", 42, "   ");

        Assert.Equal("sans motif", store.Current().Entries[0].Reason);
    }

    [Fact]
    public void Ce_qui_est_servi_se_relit_comme_une_liste()
    {
        var store = new BanStore(Path_);
        store.Add("Nom Fictif", 42, "contenu illegal");

        Assert.True(BanList.TryParse(store.Json(), out var relue, out var why), why);
        Assert.True(relue!.Contains("Nom Fictif", 42));
    }

    [Fact]
    public void Lecriture_ne_laisse_pas_de_fichier_temporaire()
    {
        // Écrite à côté puis renommée : une coupure au milieu laisse l'ancienne
        // liste intacte plutôt qu'un fichier à moitié écrit que personne ne relit.
        var store = new BanStore(Path_);
        store.Add("Nom Fictif", 42, "contenu illegal");
        store.Add("Autre Nom", 43, "contenu illegal");

        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        Assert.True(new BanStore(Path_).Current().Contains("Autre Nom", 43));
    }

    [Fact]
    public void Une_liste_illisible_ne_bannit_personne_et_nest_pas_ecrasee()
    {
        File.WriteAllText(Path_, "ceci n'est pas du JSON");

        var store = new BanStore(Path_);

        Assert.Empty(store.Current().Entries);
        Assert.Equal("ceci n'est pas du JSON", File.ReadAllText(Path_));
    }
}

/// <summary>Vérifier sans rien garder, et fusionner sans mélanger deux sels.</summary>
public sealed class BanStoreImportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"lprdv-bans-{Guid.NewGuid():N}");

    private string Path_ => System.IO.Path.Combine(_dir, "bans.json");

    public BanStoreImportTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Verifier_dit_si_le_personnage_est_liste_sans_rien_ecrire()
    {
        var store = new BanStore(Path_);
        var added = store.Add("Nom Fictif", 42, "contenu illegal");
        var before = File.ReadAllText(Path_);

        var (hash, listed) = store.Check("nom fictif", 42);
        var (_, other) = store.Check("Nom Fictif", 43);

        Assert.True(listed);
        Assert.Equal(added, hash);
        Assert.False(other);
        Assert.Equal(before, File.ReadAllText(Path_));
    }

    [Fact]
    public void Une_liste_sous_le_meme_sel_se_fusionne_par_empreinte()
    {
        var mine = new BanStore(Path_);
        mine.Add("Nom Fictif", 42, "ici");

        // Une seconde instance sur une copie du fichier : même sel, puis une
        // entrée en plus et un motif différent sur l'entrée commune.
        var otherPath = System.IO.Path.Combine(_dir, "autre.json");
        File.Copy(Path_, otherPath);
        var theirs = new BanStore(otherPath);
        theirs.Add("Autre Nom", 43, "ailleurs");

        var outcome = mine.Import(theirs.Json());

        Assert.True(outcome.Ok);
        Assert.Equal(1, outcome.Added);
        Assert.Equal(2, outcome.Total);
        Assert.True(mine.Current().Contains("Autre Nom", 43));
        Assert.Equal("ici", mine.Current().Entries[0].Reason);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));

        // Réimporter la même liste n'ajoute rien.
        Assert.Equal(0, mine.Import(theirs.Json()).Added);
    }

    [Fact]
    public void Une_liste_sous_un_autre_sel_est_refusee_sans_rien_changer()
    {
        var mine = new BanStore(Path_);
        mine.Add("Nom Fictif", 42, "ici");
        var before = File.ReadAllText(Path_);

        var foreign = new BanStore(System.IO.Path.Combine(_dir, "etranger.json"));
        foreign.Add("Autre Nom", 43, "ailleurs");

        var outcome = mine.Import(foreign.Json());

        Assert.False(outcome.Ok);
        Assert.True(outcome.ForeignSalt);
        Assert.Equal(before, File.ReadAllText(Path_));
    }

    [Fact]
    public void Une_liste_illisible_est_refusee_avec_son_motif()
    {
        var outcome = new BanStore(Path_).Import("{\"version\": 99}");

        Assert.False(outcome.Ok);
        Assert.False(outcome.ForeignSalt);
        Assert.NotNull(outcome.Rejection);
    }
}
