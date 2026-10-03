using System.Security.Cryptography;
using System.Text;
using Linkpearl.Core.Transport.Rendezvous;
using Xunit;

namespace Linkpearl.Rendezvous.Tests;

/// <summary>Le manifeste d'une release : ce qu'il accepte, ce qu'il refuse, et sa signature.</summary>
public sealed class ReleaseManifestTests
{
    private static ReleaseManifest Sample(string version = "0.6.0", bool urgent = false) => new(
        new Version(version), 1_790_500_000, urgent,
        new Dictionary<string, string> { ["lprdv"] = new string('a', 64), ["lprdv.service"] = new string('b', 64) });

    [Theory]
    [InlineData("0.6.0", true)]
    [InlineData("v0.10.2", true)]
    [InlineData("1.0", false)]
    [InlineData("0.5.2-3-gabc", false)]
    [InlineData("0.6.0+abc", false)]
    [InlineData("", false)]
    public void Une_version_a_exactement_trois_composants(string text, bool valid)
        => Assert.Equal(valid, ReleaseVersion.TryParse(text, out _));

    [Fact]
    public void Les_versions_se_comparent_en_numerique()
    {
        ReleaseVersion.TryParse("0.10.0", out var newer);
        ReleaseVersion.TryParse("0.9.3", out var older);
        Assert.True(newer > older);
    }

    [Fact]
    public void Le_manifeste_fait_l_aller_retour()
    {
        Assert.True(ReleaseManifest.TryParse(Sample(urgent: true).ToBytes(), out var parsed, out _));
        Assert.Equal(new Version(0, 6, 0), parsed!.Version);
        Assert.True(parsed.Urgent);
        Assert.Equal(new string('b', 64), parsed.Files["lprdv.service"]);
    }

    [Theory]
    [InlineData("""{"version":"0.6","signed":1,"urgent":false,"files":{"lprdv":"aa","lprdv.service":"bb"}}""")]
    [InlineData("""{"version":"0.6.0","signed":1,"urgent":false,"files":{"lprdv":"aa"}}""")]
    [InlineData("""{"version":"0.6.0","urgent":false,"files":{"lprdv":"aa","lprdv.service":"bb"}}""")]
    [InlineData("pas du json")]
    public void Un_manifeste_incomplet_est_refuse(string json)
        => Assert.False(ReleaseManifest.TryParse(Encoding.UTF8.GetBytes(json), out _, out _));

    [Fact]
    public void Un_manifeste_d_avant_les_unites_de_mise_a_jour_se_lit_encore()
    {
        var json = """{"version":"0.6.0","signed":1,"urgent":false,"files":{"lprdv":"%a","lprdv.service":"%b"}}"""
            .Replace("%a", new string('a', 64)).Replace("%b", new string('b', 64));

        Assert.True(ReleaseManifest.TryParse(Encoding.UTF8.GetBytes(json), out var parsed, out _));
        Assert.False(parsed!.Files.ContainsKey("lprdv-update.service"));
    }

    [Fact]
    public void Les_unites_de_mise_a_jour_font_l_aller_retour()
    {
        var manifest = Sample() with
        {
            Files = new Dictionary<string, string>
            {
                ["lprdv"] = new string('a', 64), ["lprdv.service"] = new string('b', 64),
                ["lprdv-update.service"] = new string('c', 64), ["lprdv-update.timer"] = new string('d', 64),
            },
        };

        Assert.True(ReleaseManifest.TryParse(manifest.ToBytes(), out var parsed, out _));
        Assert.Equal(new string('c', 64), parsed!.Files["lprdv-update.service"]);
        Assert.Equal(new string('d', 64), parsed.Files["lprdv-update.timer"]);
    }

    [Fact]
    public void Une_somme_d_unite_malformee_est_refusee()
    {
        var json = """{"version":"0.6.0","signed":1,"urgent":false,"files":{"lprdv":"%a","lprdv.service":"%b","lprdv-update.timer":"court"}}"""
            .Replace("%a", new string('a', 64)).Replace("%b", new string('b', 64));

        Assert.False(ReleaseManifest.TryParse(Encoding.UTF8.GetBytes(json), out _, out _));
    }

    [Fact]
    public void Seule_une_cle_inscrite_verifie_la_signature()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var manifest = Sample().ToBytes();
        var signature = ReleaseManifest.Sign(manifest, key);

        Assert.True(ReleaseManifest.Verify(manifest, signature, [ServiceConsensus.PublicPoint(key)]));
        Assert.False(ReleaseManifest.Verify(manifest, signature, [ServiceConsensus.PublicPoint(other)]));

        manifest[^2] ^= 1;
        Assert.False(ReleaseManifest.Verify(manifest, signature, [ServiceConsensus.PublicPoint(key)]));
    }

    [Fact]
    public void Un_binaire_compile_sans_version_ne_se_lit_pas()
        // Les tests tournent sur un binaire compilé sans -p:Version, comme un
        // binaire fait à la main : la mise à jour automatique doit l'ignorer.
        => Assert.Null(ReleaseVersion.Current);
}
