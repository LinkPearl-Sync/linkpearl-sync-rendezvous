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

/// <summary>Lève une fois, comme une panne réseau ponctuelle, puis rend un 404.</summary>
internal sealed class ThrowingOnceHandler : HttpMessageHandler
{
    private bool _thrown;

    public int Calls { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Calls++;

        if (_thrown is false)
        {
            _thrown = true;
            throw new InvalidOperationException("panne inattendue");
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

/// <summary>Un flux qui lève dès la première lecture, comme une connexion coupée.</summary>
internal sealed class ThrowingStream : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count) => throw new IOException("connexion perdue");

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => throw new IOException("connexion perdue");

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal sealed class FaultyHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new ThrowingStream()) });
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
    public async Task Une_exception_inattendue_n_arrete_pas_la_boucle_de_mise_a_jour()
    {
        var handler = new ThrowingOnceHandler();
        var path = Path.Combine(_dir, "geoip.mmdb");
        using var regions = new GeoIpRegions(path, _clock);
        var updater = new GeoIpUpdater(regions, path, new HttpClient(handler), _clock) { Log = TextWriter.Null };

        using var stopping = new CancellationTokenSource();

        // La première ronde échoue par une panne qui n'est ni HTTP, ni IO, ni
        // une annulation : la boucle doit l'avaler et continuer, plutôt que
        // de faire échouer toute la tâche attendue par Program.cs.
        var running = updater.RunAsync(stopping.Token);
        stopping.Cancel();

        await running;

        Assert.True(handler.Calls >= 1);
    }

    [Fact]
    public async Task Une_panne_reseau_pendant_le_telechargement_ne_laisse_pas_de_fichier_temporaire()
    {
        var path = Path.Combine(_dir, "geoip.mmdb");
        File.WriteAllBytes(path, Fixture);
        using var regions = new GeoIpRegions(path, _clock);
        _clock.Set(regions.BuildDate!.Value + GeoIpUpdater.RefreshAfter + TimeSpan.FromDays(1));

        var updater = new GeoIpUpdater(regions, path, new HttpClient(new FaultyHandler()), _clock) { Log = TextWriter.Null };

        await Assert.ThrowsAsync<IOException>(() => updater.RefreshIfNeededAsync(CancellationToken.None));

        Assert.False(File.Exists(path + ".tmp"));
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
