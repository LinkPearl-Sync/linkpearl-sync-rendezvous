using System.Security.Cryptography;
using Linkpearl.Core.Transport.Rendezvous;
using Xunit;

namespace Linkpearl.Rendezvous.Tests;

/// <summary>Une ronde de mise à jour, contre un faux GitHub et un faux systemd.</summary>
public sealed class UpdaterTests : IDisposable
{
    private readonly string _state = Path.Combine(Path.GetTempPath(), $"lprdv-update-{Guid.NewGuid():N}");
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ManualClock _clock = new();
    private readonly FakeReleases _releases = new();
    private readonly FakeInstallation _installation = new();

    public UpdaterTests() => Directory.CreateDirectory(_state);

    public void Dispose()
    {
        _key.Dispose();
        Directory.Delete(_state, recursive: true);
    }

    private sealed class FakeReleases : IReleaseSource
    {
        public Dictionary<string, byte[]> Files { get; } = [];
        public bool Down { get; set; }

        public Task<string> LatestTagAsync(CancellationToken ct)
            => Down ? throw new HttpRequestException("limite de débit") : Task.FromResult("v0.6.0");

        public Task<byte[]> DownloadAsync(string tag, string file, CancellationToken ct)
            => Files.TryGetValue(file, out var bytes) ? Task.FromResult(bytes) : throw new HttpRequestException("404");
    }

    /// <summary>
    /// RestartAsync lève tant que Restore n'a pas eu lieu quand RestartFails est
    /// vrai : la nouvelle version ne démarre pas, l'ancienne oui.
    /// </summary>
    private sealed class FakeInstallation : IInstallation
    {
        public byte[]? Staged { get; private set; }
        public bool Restored { get; private set; }
        public bool Healthy { get; set; } = true;
        public bool RestartFails { get; set; }
        public int Restarts { get; private set; }

        public void Stage(byte[] binary, byte[] unit) => Staged = binary;

        public Task RestartAsync(CancellationToken ct)
        {
            Restarts++;
            return RestartFails && Restored is false ? throw new InvalidOperationException("systemctl") : Task.CompletedTask;
        }

        public Task<bool> HealthyAsync(CancellationToken ct) => Task.FromResult(Healthy);

        public void Restore() => Restored = true;
    }

    /// <summary>Publie une release signée, vieille de <paramref name="age"/>. Le binaire servi peut différer de sa somme.</summary>
    private void Publish(string version, TimeSpan age, bool urgent = false, byte[]? binary = null)
    {
        byte[] unit = [4, 5];
        var manifest = new ReleaseManifest(
            new Version(version), (_clock.UtcNow - age).ToUnixTimeSeconds(), urgent,
            new Dictionary<string, string>
            {
                ["lprdv"] = Convert.ToHexStringLower(SHA256.HashData(new byte[] { 1, 2, 3 })),
                ["lprdv.service"] = Convert.ToHexStringLower(SHA256.HashData(unit)),
            }).ToBytes();

        _releases.Files["lprdv.release.json"] = manifest;
        _releases.Files["lprdv.release.json.sig"] = ReleaseManifest.Sign(manifest, _key);
        _releases.Files["lprdv"] = binary ?? [1, 2, 3];
        _releases.Files["lprdv.service"] = unit;
    }

    private Updater Updater(string? current = "0.5.2")
    {
        Version? version = current is not null && ReleaseVersion.TryParse(current, out var parsed) ? parsed : null;
        return new Updater(_releases, _installation, [ServiceConsensus.PublicPoint(_key)], _clock, version, _state, TextWriter.Null);
    }

    [Fact]
    public async Task Une_release_signee_de_plus_de_24_heures_est_installee()
    {
        Publish("0.6.0", TimeSpan.FromHours(25));

        Assert.Equal(UpdateOutcome.Installed, await Updater().RunAsync(CancellationToken.None));
        Assert.Equal(new byte[] { 1, 2, 3 }, _installation.Staged);
    }

    [Fact]
    public async Task Une_release_trop_recente_attend()
    {
        Publish("0.6.0", TimeSpan.FromHours(23));

        Assert.Equal(UpdateOutcome.Waiting, await Updater().RunAsync(CancellationToken.None));
        Assert.Null(_installation.Staged);
    }

    [Fact]
    public async Task Une_release_urgente_n_attend_pas()
    {
        Publish("0.6.0", TimeSpan.FromMinutes(5), urgent: true);

        Assert.Equal(UpdateOutcome.Installed, await Updater().RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Une_signature_d_une_autre_cle_est_refusee()
    {
        Publish("0.6.0", TimeSpan.FromHours(25));
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _releases.Files["lprdv.release.json.sig"] = ReleaseManifest.Sign(_releases.Files["lprdv.release.json"], other);

        Assert.Equal(UpdateOutcome.Rejected, await Updater().RunAsync(CancellationToken.None));
        Assert.Null(_installation.Staged);
    }

    [Fact]
    public async Task Un_binaire_qui_ne_correspond_pas_a_sa_somme_est_refuse()
    {
        Publish("0.6.0", TimeSpan.FromHours(25), binary: [9, 9, 9]);

        Assert.Equal(UpdateOutcome.Rejected, await Updater().RunAsync(CancellationToken.None));
        Assert.Null(_installation.Staged);
    }

    [Fact]
    public async Task Un_manifeste_plus_ancien_que_la_version_en_place_ne_fait_rien()
    {
        Publish("0.5.2", TimeSpan.FromDays(10));

        Assert.Equal(UpdateOutcome.UpToDate, await Updater("0.5.2").RunAsync(CancellationToken.None));
        Assert.Equal(UpdateOutcome.UpToDate, await Updater("0.6.0").RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Une_version_en_place_illisible_ne_met_rien_a_jour()
    {
        Publish("0.6.0", TimeSpan.FromHours(25));

        Assert.Equal(UpdateOutcome.Unversioned, await Updater(current: null).RunAsync(CancellationToken.None));
        Assert.Null(_installation.Staged);
    }

    [Fact]
    public async Task GitHub_injoignable_ne_fait_rien()
    {
        _releases.Down = true;

        Assert.Equal(UpdateOutcome.Unreachable, await Updater().RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Une_release_sans_manifeste_ne_fait_rien()
        => Assert.Equal(UpdateOutcome.Unreachable, await Updater().RunAsync(CancellationToken.None));

    [Fact]
    public async Task Une_version_muette_revient_en_arriere_et_n_est_plus_retentee()
    {
        Publish("0.6.0", TimeSpan.FromHours(25));
        _installation.Healthy = false;

        Assert.Equal(UpdateOutcome.RolledBack, await Updater().RunAsync(CancellationToken.None));
        Assert.True(_installation.Restored);
        Assert.Equal(2, _installation.Restarts);

        Assert.Equal(UpdateOutcome.Refused, await Updater().RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Une_version_plus_recente_que_la_refusee_est_retentee()
    {
        Publish("0.6.0", TimeSpan.FromHours(25));
        _installation.Healthy = false;
        await Updater().RunAsync(CancellationToken.None);

        _installation.Healthy = true;
        Publish("0.6.1", TimeSpan.FromHours(25));

        Assert.Equal(UpdateOutcome.Installed, await Updater().RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Un_redemarrage_qui_echoue_revient_en_arriere()
    {
        Publish("0.6.0", TimeSpan.FromHours(25));
        _installation.RestartFails = true;

        Assert.Equal(UpdateOutcome.RolledBack, await Updater().RunAsync(CancellationToken.None));
        Assert.True(_installation.Restored);
    }
}
