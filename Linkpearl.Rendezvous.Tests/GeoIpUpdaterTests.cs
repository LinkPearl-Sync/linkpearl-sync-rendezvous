using System.IO.Compression;
using System.Net;
using Xunit;

namespace Linkpearl.Rendezvous.Tests;

/// <summary>Un serveur HTTP scripté : chaque URL citée rend son contenu, les autres un 404.</summary>
internal sealed class ScriptedHandler : HttpMessageHandler
{
    public Dictionary<string, byte[]> Pages { get; } = [];

    public List<string> Asked { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Asked.Add(request.RequestUri!.ToString());

        return Task.FromResult(Pages.TryGetValue(request.RequestUri.ToString(), out var body)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
            : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

public sealed class GeoIpUpdaterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"lprdv-geoip-{Guid.NewGuid():N}");
    private readonly ManualClock _clock = new();
    private readonly ScriptedHandler _web = new();

    public GeoIpUpdaterTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static byte[] Gzipped(byte[] raw)
    {
        using var output = new MemoryStream();

        using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
            gzip.Write(raw);

        return output.ToArray();
    }

    private static byte[] Fixture => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "GeoIP2-Country-Test.mmdb"));

    [Fact]
    public void L_adresse_de_la_base_suit_le_mois()
        => Assert.Equal(
            "https://download.db-ip.com/free/dbip-country-lite-2026-09.mmdb.gz",
            GeoIpUpdater.SourceFor(new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero)).ToString());

    [Fact]
    public async Task Une_base_absente_est_telechargee_et_rechargee()
    {
        _clock.Set(new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero));
        _web.Pages[GeoIpUpdater.SourceFor(_clock.UtcNow).ToString()] = Gzipped(Fixture);

        var path = Path.Combine(_dir, "geoip.mmdb");
        using var regions = new GeoIpRegions(path, _clock);
        var updater = new GeoIpUpdater(regions, path, new HttpClient(_web), _clock) { Log = TextWriter.Null };

        Assert.True(await updater.RefreshIfNeededAsync(CancellationToken.None));
        Assert.NotNull(regions.BuildDate);
    }

    [Fact]
    public async Task Le_mois_precedent_sert_quand_le_courant_n_est_pas_encore_publie()
    {
        _clock.Set(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        _web.Pages[GeoIpUpdater.SourceFor(_clock.UtcNow.AddMonths(-1)).ToString()] = Gzipped(Fixture);

        var path = Path.Combine(_dir, "geoip.mmdb");
        using var regions = new GeoIpRegions(path, _clock);
        var updater = new GeoIpUpdater(regions, path, new HttpClient(_web), _clock) { Log = TextWriter.Null };

        Assert.True(await updater.RefreshIfNeededAsync(CancellationToken.None));
        Assert.Equal(2, _web.Asked.Count);
    }

    [Fact]
    public async Task Un_telechargement_corrompu_ne_remplace_pas_la_base_en_place()
    {
        var path = Path.Combine(_dir, "geoip.mmdb");
        File.WriteAllBytes(path, Fixture);
        using var regions = new GeoIpRegions(path, _clock);
        _clock.Set(regions.BuildDate!.Value + GeoIpUpdater.RefreshAfter + TimeSpan.FromDays(1));
        _web.Pages[GeoIpUpdater.SourceFor(_clock.UtcNow).ToString()] = Gzipped([1, 2, 3]);

        var updater = new GeoIpUpdater(regions, path, new HttpClient(_web), _clock) { Log = TextWriter.Null };

        Assert.False(await updater.RefreshIfNeededAsync(CancellationToken.None));
        Assert.Equal(Fixture, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Un_gzip_illisible_ne_remplace_pas_la_base_en_place()
    {
        var path = Path.Combine(_dir, "geoip.mmdb");
        File.WriteAllBytes(path, Fixture);
        using var regions = new GeoIpRegions(path, _clock);
        _clock.Set(regions.BuildDate!.Value + GeoIpUpdater.RefreshAfter + TimeSpan.FromDays(1));
        _web.Pages[GeoIpUpdater.SourceFor(_clock.UtcNow).ToString()] = [0x1F, 0x8B, 0x00, 0x42];

        var updater = new GeoIpUpdater(regions, path, new HttpClient(_web), _clock) { Log = TextWriter.Null };

        Assert.False(await updater.RefreshIfNeededAsync(CancellationToken.None));
        Assert.Equal(Fixture, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Une_base_recente_n_est_pas_retelechargee()
    {
        var path = Path.Combine(_dir, "geoip.mmdb");
        File.WriteAllBytes(path, Fixture);
        using var regions = new GeoIpRegions(path, _clock);
        _clock.Set(regions.BuildDate!.Value.AddDays(1));

        var updater = new GeoIpUpdater(regions, path, new HttpClient(_web), _clock) { Log = TextWriter.Null };

        Assert.False(await updater.RefreshIfNeededAsync(CancellationToken.None));
        Assert.Empty(_web.Asked);
    }
}
