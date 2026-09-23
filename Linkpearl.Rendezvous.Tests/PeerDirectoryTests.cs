using Linkpearl.Core.Transport.Rendezvous;
using Linkpearl.Rendezvous;
using Xunit;

namespace Linkpearl.Rendezvous.Tests;

/// <summary>
/// L'annuaire : un fichier écrit par l'opérateur, relu à chaud, et une file de
/// candidatures qu'il approuve à la main.
/// </summary>
public sealed class DirectoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lprdv-" + Guid.NewGuid().ToString("N"));

    private string PeersPath => Path.Combine(_root, "peers.txt");

    private string PendingPath => Path.Combine(_root, "pending.txt");

    private PeerDirectory New()
    {
        System.IO.Directory.CreateDirectory(_root);
        return new PeerDirectory(PeersPath, PendingPath);
    }

    public void Dispose()
    {
        if (System.IO.Directory.Exists(_root))
            System.IO.Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Un_fichier_absent_donne_un_annuaire_vide()
    {
        // Un service sans pair déclaré doit répondre, pas échouer.
        Assert.Empty(New().Known());
    }

    [Fact]
    public void Le_fichier_est_relu_sans_redemarrage()
    {
        // Sans cela, ajouter un service imposerait un redémarrage, et personne
        // ne le ferait.
        var directory = New();

        File.WriteAllText(PeersPath, "rdv.ami.ch  Chez l'amie\n");
        Assert.Single(directory.Known());

        // L'horodatage doit changer pour que la relecture se déclenche.
        File.SetLastWriteTimeUtc(PeersPath, DateTime.UtcNow.AddSeconds(1));
        File.WriteAllText(PeersPath, "rdv.ami.ch  Chez l'amie\nrdv.autre.ch:443  Ailleurs\n");
        File.SetLastWriteTimeUtc(PeersPath, DateTime.UtcNow.AddSeconds(2));

        Assert.Equal(2, directory.Known().Count);
    }

    [Fact]
    public void Une_ligne_illisible_est_ignoree_sans_perdre_les_autres()
    {
        // Un fichier à demi valide vaut mieux qu'un service muet.
        var directory = New();

        File.WriteAllText(PeersPath, "# un commentaire\n\nhttp://rdv.exemple.ch  mauvais\nrdv.bon.ch  Bon\n");

        var entry = Assert.Single(directory.Known());
        Assert.Equal("rdv.bon.ch", entry.Address);
        Assert.Equal("Bon", entry.Label);
    }

    [Fact]
    public void Une_adresse_sans_libelle_est_acceptee()
    {
        var directory = New();

        File.WriteAllText(PeersPath, "rdv.ami.ch\n");

        Assert.Equal("", Assert.Single(directory.Known()).Label);
    }

    [Fact]
    public void Une_candidature_entre_dans_la_file_et_pas_dans_l_annuaire()
    {
        var directory = New();

        Assert.True(directory.Submit("198.51.100.7", new DirectoryEntry("rdv.nouveau.ch", "Nouveau")));

        Assert.Empty(directory.Known());
        Assert.Single(directory.Pending());
    }

    [Fact]
    public void Une_seconde_candidature_de_la_meme_source_est_refusee()
    {
        // Une par adresse et par heure : sans cela, la file se remplit toute
        // seule et l'opérateur ne la lit plus.
        var directory = New();

        Assert.True(directory.Submit("198.51.100.7", new DirectoryEntry("rdv.a.ch", "A")));
        Assert.False(directory.Submit("198.51.100.7", new DirectoryEntry("rdv.b.ch", "B")));
    }

    [Fact]
    public void Une_candidature_deja_connue_est_ignoree()
    {
        var directory = New();
        File.WriteAllText(PeersPath, "rdv.connu.ch  Connu\n");

        Assert.False(directory.Submit("198.51.100.7", new DirectoryEntry("rdv.connu.ch", "Connu")));
        Assert.Empty(directory.Pending());
    }

    [Fact]
    public void Une_candidature_illisible_est_ecartee()
    {
        var directory = New();

        Assert.False(directory.Submit("198.51.100.7", new DirectoryEntry("rdv exemple.ch", "Mauvais")));
        Assert.Empty(directory.Pending());
    }

    [Fact]
    public void La_file_survit_a_un_redemarrage()
    {
        // Une seule candidature doit suffire : la reperdre obligerait le
        // candidat à recommencer sans savoir pourquoi.
        New().Submit("198.51.100.7", new DirectoryEntry("rdv.nouveau.ch", "Nouveau"));

        Assert.Single(New().Pending());
    }

    [Fact]
    public void Les_compteurs_partent_de_zero_et_suivent_l_annuaire()
    {
        // Tenus même sans personne pour les lire : la console d'administration
        // viendra plus tard, et les reconstituer après coup demanderait de
        // retoucher chaque chemin de code.
        var directory = New();
        var server = new RendezvousServer(0, directory, RendezvousLimits.Default, new ManualClock());

        var before = server.Snapshot();
        Assert.Equal(0, before.KnownPeers);
        Assert.Equal(0, before.PendingSubmissions);
        Assert.Equal(0, before.Matches);

        File.WriteAllText(PeersPath, "rdv.ami.ch  Chez l'amie\n");
        directory.Submit("198.51.100.7", new DirectoryEntry("rdv.nouveau.ch", "Nouveau"));

        var after = server.Snapshot();
        Assert.Equal(1, after.KnownPeers);
        Assert.Equal(1, after.PendingSubmissions);
    }
}

/// <summary>Ce que l'annuaire retient des sources de candidatures.</summary>
public sealed class SubmitterMemoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lprdv-" + Guid.NewGuid().ToString("N"));

    public SubmitterMemoryTests() => System.IO.Directory.CreateDirectory(_root);

    public void Dispose() => System.IO.Directory.Delete(_root, recursive: true);

    [Fact]
    public void Les_sources_anciennes_sont_oubliees()
    {
        // Le frein d'une candidature par heure gardait chaque adresse pour
        // toujours : un jour de candidatures venues de partout, et le
        // dictionnaire ne désenfle plus.
        var clock = new ManualClock();
        var directory = new PeerDirectory(Path.Combine(_root, "peers.txt"), Path.Combine(_root, "pending.txt"), clock);

        Assert.True(directory.Submit("198.51.100.7", new DirectoryEntry("rdv.a.ch", "A")));
        Assert.Equal(1, directory.TrackedSubmitters);

        clock.Advance(TimeSpan.FromHours(2));
        Assert.True(directory.Submit("198.51.100.8", new DirectoryEntry("rdv.b.ch", "B")));

        Assert.Equal(1, directory.TrackedSubmitters);
    }
}
