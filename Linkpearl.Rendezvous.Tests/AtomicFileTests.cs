using Linkpearl.Rendezvous;
using Xunit;

namespace Linkpearl.Rendezvous.Tests;

/// <summary>L'écriture qui remplace un fichier d'un bloc, ou pas du tout.</summary>
public sealed class AtomicFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"lprdv-atomic-{Guid.NewGuid():N}");

    public AtomicFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Le_contenu_est_remplace_et_rien_ne_traine()
    {
        var path = Path.Combine(_dir, "peers.txt");

        AtomicFile.WriteAllText(path, "premier\n");
        AtomicFile.WriteAllText(path, "second\n");

        Assert.Equal("second\n", File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFiles(_dir));
    }

    [Fact]
    public void Les_lignes_secrivent_de_la_meme_facon()
    {
        var path = Path.Combine(_dir, "pending.txt");

        AtomicFile.WriteAllLines(path, ["a  A", "b  B"]);

        Assert.Equal(["a  A", "b  B"], File.ReadAllLines(path));
        Assert.Equal([path], Directory.GetFiles(_dir));
    }

    [Fact]
    public void Un_temporaire_abandonne_est_ecrase_par_lecriture_suivante()
    {
        // Une coupure entre l'écriture et le renommage laisse un .tmp : il ne
        // doit ni bloquer la suivante ni être pris pour le fichier lui-même.
        var path = Path.Combine(_dir, "bans.json");
        File.WriteAllText(path + ".tmp", "reste d'une coupure");

        AtomicFile.WriteAllText(path, "{}");

        Assert.Equal("{}", File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFiles(_dir));
    }
}
