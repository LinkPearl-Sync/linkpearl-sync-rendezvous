using System.Security.Cryptography;
using Linkpearl.Core.Transport.Rendezvous;
using Xunit;

namespace Linkpearl.Rendezvous.Tests;

/// <summary>La signature d'une release par le workflow.</summary>
public sealed class ReleaseCommandsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"lprdv-sign-{Guid.NewGuid():N}");
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public ReleaseCommandsTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(Path.Combine(_dir, "lprdv"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(_dir, "lprdv.service"), [4, 5]);
    }

    public void Dispose()
    {
        _key.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Signer_ecrit_un_manifeste_que_la_cle_inscrite_verifie()
    {
        var clock = new ManualClock();
        ReleaseCommands.Sign("0.6.0", urgent: true, _dir, _key, [ServiceConsensus.PublicPoint(_key)], clock);

        var manifest = File.ReadAllBytes(Path.Combine(_dir, ReleaseManifest.FileName));
        var signature = File.ReadAllBytes(Path.Combine(_dir, ReleaseManifest.SignatureName));
        Assert.True(ReleaseManifest.Verify(manifest, signature, [ServiceConsensus.PublicPoint(_key)]));
        Assert.True(ReleaseManifest.TryParse(manifest, out var parsed, out _));
        Assert.True(parsed!.Urgent);
        Assert.Equal(clock.UtcNow.ToUnixTimeSeconds(), parsed.Signed);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(new byte[] { 1, 2, 3 })), parsed.Files["lprdv"]);
    }

    [Fact]
    public void Signer_avec_une_cle_non_inscrite_echoue()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        Assert.Throws<InvalidOperationException>(
            () => ReleaseCommands.Sign("0.6.0", urgent: false, _dir, _key, [ServiceConsensus.PublicPoint(other)], new ManualClock()));
        Assert.False(File.Exists(Path.Combine(_dir, ReleaseManifest.FileName)));
    }

    [Fact]
    public void Signer_une_pre_version_echoue()
        => Assert.Throws<InvalidOperationException>(
            () => ReleaseCommands.Sign("0.6.0-rc1", urgent: false, _dir, _key, [ServiceConsensus.PublicPoint(_key)], new ManualClock()));

    [Fact]
    public void La_cle_de_release_inscrite_est_un_point_P256()
    {
        var point = Assert.Single(ReleaseKeys.Trusted);
        Assert.Equal(65, point.Length);
        Assert.Equal(0x04, point[0]);
    }
}
