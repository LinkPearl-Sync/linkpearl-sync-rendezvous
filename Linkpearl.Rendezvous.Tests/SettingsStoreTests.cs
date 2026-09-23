using System.Text.Json.Nodes;
using Linkpearl.Rendezvous;
using Xunit;

namespace Linkpearl.Rendezvous.Tests;

/// <summary>Les réglages à chaud : validés en bloc, écrits d'un bloc, relus au démarrage.</summary>
public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"lprdv-settings-{Guid.NewGuid():N}");

    private string Path_ => System.IO.Path.Combine(_dir, "settings.json");

    public SettingsStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Sans_fichier_la_ligne_de_commande_fait_foi()
    {
        var defaults = new RendezvousLimits { AnnouncementsPerMinute = 17 };
        var log = new StringWriter();

        var loaded = new SettingsStore(Path_).Load(defaults, log);

        Assert.Same(defaults, loaded);
        Assert.Equal("", log.ToString());
    }

    [Fact]
    public void Le_fichier_surcharge_la_ligne_de_commande_et_survit_au_redemarrage()
    {
        var store = new SettingsStore(Path_);
        store.Save(new RendezvousLimits { AnnouncementsPerMinute = 120, RelayEnabled = false });

        var loaded = store.Load(new RendezvousLimits { AnnouncementsPerMinute = 60, MaxConnections = 99 }, TextWriter.Null);

        Assert.Equal(120, loaded.AnnouncementsPerMinute);
        Assert.False(loaded.RelayEnabled);

        // Ce que le fichier ne dit pas garde la valeur du fichier écrit, qui
        // porte tous les réglages : ici le plafond de connexions par défaut.
        Assert.Equal(RendezvousLimits.Default.MaxConnections, loaded.MaxConnections);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void Un_fichier_illisible_est_ignore_et_dit_sans_etre_reecrit()
    {
        File.WriteAllText(Path_, "{ pas du json");
        var log = new StringWriter();

        var loaded = new SettingsStore(Path_).Load(RendezvousLimits.Default, log);

        Assert.Same(RendezvousLimits.Default, loaded);
        Assert.Contains("illisibles", log.ToString());
        Assert.Equal("{ pas du json", File.ReadAllText(Path_));
    }

    [Fact]
    public void Un_fichier_hors_bornes_est_refuse_en_entier()
    {
        File.WriteAllText(Path_, """{"announcementsPerMinute": 120, "maxConnections": 0}""");
        var log = new StringWriter();

        var loaded = new SettingsStore(Path_).Load(RendezvousLimits.Default, log);

        Assert.Equal(RendezvousLimits.Default.AnnouncementsPerMinute, loaded.AnnouncementsPerMinute);
        Assert.Contains("maxConnections", log.ToString());
    }

    [Theory]
    [InlineData("""{"announcementsPerMinute": 0}""", "entre")]
    [InlineData("""{"maxConnections": 1000001}""", "entre")]
    [InlineData("""{"maxMailboxesPerSession": "seize"}""", "entier")]
    [InlineData("""{"maxMailboxesPerSession": 2.5}""", "entier")]
    [InlineData("""{"relayEnabled": "oui"}""", "true ou false")]
    [InlineData("""{"inconnu": 1}""", "inconnu")]
    public void Une_valeur_fautive_est_refusee_avec_son_motif(string json, string fragment)
    {
        var ok = SettingsStore.TryApply(RendezvousLimits.Default, (JsonObject)JsonNode.Parse(json)!, out var applied, out var why);

        Assert.False(ok);
        Assert.Contains(fragment, why);
        Assert.Same(RendezvousLimits.Default, applied);
    }

    [Fact]
    public void Un_document_partiel_ne_touche_que_ce_quil_nomme()
    {
        var current = new RendezvousLimits { AnnouncementsPerMinute = 60, MaxConnections = 500 };

        var ok = SettingsStore.TryApply(current, (JsonObject)JsonNode.Parse("""{"maxConnections": 700, "relayEnabled": false}""")!, out var applied, out _);

        Assert.True(ok);
        Assert.Equal(60, applied.AnnouncementsPerMinute);
        Assert.Equal(700, applied.MaxConnections);
        Assert.False(applied.RelayEnabled);
    }

    [Fact]
    public void Les_changements_se_racontent_sans_rien_dautre()
    {
        var before = RendezvousLimits.Default;
        var after = before with { AnnouncementsPerMinute = 120, RelayEnabled = false };

        var changes = SettingsStore.Changes(before, after);

        Assert.Equal(2, changes.Count);
        Assert.Contains("announcementsPerMinute 60 -> 120", changes);
        Assert.Contains("relayEnabled true -> false", changes);
    }
}
