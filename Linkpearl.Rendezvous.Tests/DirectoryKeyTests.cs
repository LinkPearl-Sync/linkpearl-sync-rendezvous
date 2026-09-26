using System.Security.Cryptography;
using Linkpearl.Core.Transport.Rendezvous;
using Xunit;

namespace Linkpearl.Rendezvous.Tests;

/// <summary>La clé qui signe la liste du cercle ouvert : engendrée une fois, jamais remplacée.</summary>
public sealed class DirectoryKeyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"lprdv-key-{Guid.NewGuid():N}");

    public DirectoryKeyTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string KeyPath => Path.Combine(_dir, "directory.key");

    [Fact]
    public void Une_cle_engendree_se_relit_a_l_identique()
    {
        using var first = DirectoryKey.LoadOrCreate(KeyPath, TextWriter.Null);
        using var second = DirectoryKey.LoadOrCreate(KeyPath, TextWriter.Null);

        Assert.Equal(ServiceConsensus.PublicPoint(first), ServiceConsensus.PublicPoint(second));
    }

    [Fact]
    public void La_cle_n_est_lisible_que_par_son_proprietaire()
    {
        using var key = DirectoryKey.LoadOrCreate(KeyPath, TextWriter.Null);

        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(KeyPath));
    }

    [Fact]
    public void Une_cle_illisible_n_est_jamais_remplacee()
    {
        File.WriteAllBytes(KeyPath, new byte[] { 1, 2, 3 });

        Assert.Throws<InvalidOperationException>(() => DirectoryKey.LoadOrCreate(KeyPath, TextWriter.Null));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(KeyPath));
    }

    [Fact]
    public void Le_journal_donne_la_cle_publique_et_jamais_la_privee()
    {
        var log = new StringWriter();
        using var key = DirectoryKey.LoadOrCreate(KeyPath, log);

        Assert.Contains(Convert.ToHexStringLower(ServiceConsensus.PublicPoint(key)), log.ToString());
        Assert.DoesNotContain(Convert.ToHexStringLower(key.ExportParameters(true).D!), log.ToString());
    }
}
