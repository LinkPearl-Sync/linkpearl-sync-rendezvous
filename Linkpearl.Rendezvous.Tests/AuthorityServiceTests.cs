using System.Net;
using System.Security.Cryptography;
using Linkpearl.Core.Transport.Rendezvous;
using Xunit;

namespace Linkpearl.Rendezvous.Tests;

/// <summary>Une sonde scriptée : les services cités répondent depuis l'adresse donnée.</summary>
internal sealed class ScriptedProbe : IServiceProbe
{
    public Dictionary<string, IPAddress> Up { get; } = [];

    public Task<ProbeResult> ProbeAsync(RendezvousAddress at, CancellationToken ct)
        => Task.FromResult(Up.TryGetValue(ServiceConsensus.Canonical(at), out var address)
            ? new ProbeResult(true, address)
            : new ProbeResult(false, null));
}

/// <summary>Une source de liste fixe, pour tester le service de pages.</summary>
internal sealed class FixedConsensus(byte[]? document, byte[]? status = null, byte[]? documentV2 = null) : IConsensusSource
{
    public byte[]? Document => document;

    public byte[]? Status => status;

    public byte[]? DocumentV2 => documentV2;
}

/// <summary>Une géolocalisation scriptée : chaque adresse citée reçoit sa région.</summary>
internal sealed class ScriptedRegions : IRegionLookup
{
    public Dictionary<IPAddress, string> Known { get; } = [];

    public string? RegionOf(IPAddress address) => Known.GetValueOrDefault(address);
}

public sealed class AuthorityServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"lprdv-authority-{Guid.NewGuid():N}");
    private readonly ManualClock _clock = new();
    private readonly ScriptedProbe _probe = new();
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly AuthorityService _authority;
    private readonly ScriptedRegions _regions = new();

    public AuthorityServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _authority = new AuthorityService(
            AuthorityLedger.Load(Path.Combine(_dir, "authority.json"), _clock), _probe, _key, _clock, _regions)
        {
            Log = TextWriter.Null,
        };
    }

    public void Dispose()
    {
        _key.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task Une_candidature_est_sondee_puis_listee_apres_la_probation()
    {
        // La candidature vient de l'adresse même où le service répond.
        _authority.Ledger.Track(new DirectoryEntry("rdv.candidat.ch", "Candidat"), "203.0.113.7");
        _probe.Up["rdv.candidat.ch:47900"] = IPAddress.Parse("203.0.113.7");

        for (var round = 0; round <= 432; round++)
        {
            await _authority.RoundAsync(CancellationToken.None);
            _clock.Advance(AuthorityService.Interval);
        }

        Assert.True(ServiceConsensus.TryVerify(
            _authority.Document!, [_authority.PublicPoint], _clock.UtcNow.ToUnixTimeSeconds(), out var list, out var why), why);
        Assert.Equal("rdv.candidat.ch:47900", Assert.Single(list!.Entries).Address);
        Assert.Equal("Candidat", list.Entries[0].Label);
    }

    [Fact]
    public void Une_liste_inchangee_n_est_reemise_qu_au_bout_de_24_heures()
    {
        _authority.IssueIfNeeded();
        var first = _authority.Current!.Version;

        _clock.Advance(TimeSpan.FromHours(23));
        _authority.IssueIfNeeded();
        Assert.Equal(first, _authority.Current!.Version);

        _clock.Advance(TimeSpan.FromHours(1));
        _authority.IssueIfNeeded();
        Assert.True(_authority.Current!.Version > first);
    }

    [Fact]
    public async Task Une_candidature_recue_porte_l_adresse_qui_l_a_soumise()
    {
        var received = new TaskCompletionSource<(DirectoryEntry Entry, string Submitter)>();
        await using var harness = await ServerHarness.StartAsync(
            candidacy: (entry, submitter) => received.TrySetResult((entry, submitter)));
        using var client = await harness.ConnectAsync();

        await client.SendAsync(RendezvousWire.DirectorySubmit("rdv.candidat.ch", "Candidat"));
        var (entry, submitter) = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("rdv.candidat.ch", entry.Address);
        Assert.Equal("127.0.0.1", submitter);
    }

    [Fact]
    public async Task Une_candidature_sans_adresse_publique_prend_celle_de_l_envoi()
    {
        var received = new TaskCompletionSource<DirectoryEntry>();
        await using var harness = await ServerHarness.StartAsync(
            candidacy: (entry, _) => received.TrySetResult(entry));
        using var client = await harness.ConnectAsync();

        await client.SendAsync(RendezvousWire.DirectorySubmit("localhost:47950", "Sans adresse"));
        var entry = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("127.0.0.1:47950", entry.Address);
        Assert.Equal("Sans adresse", entry.Label);
    }

    [Fact]
    public async Task L_etat_public_suit_les_rondes()
    {
        _authority.Ledger.Track(new DirectoryEntry("rdv.candidat.ch", "Candidat"), "203.0.113.7");
        _probe.Up["rdv.candidat.ch:47900"] = IPAddress.Parse("203.0.113.7");

        await _authority.RoundAsync(CancellationToken.None);

        var status = System.Text.Json.Nodes.JsonNode.Parse(_authority.Status!)!;
        Assert.Equal("probation", status["services"]![0]!["standing"]!.GetValue<string>());
        Assert.NotNull(status["authority"]);
    }

    [Fact]
    public async Task Le_service_sert_l_etat_du_reseau_par_pages()
    {
        var status = RandomNumberGenerator.GetBytes(40_000);
        await using var harness = await ServerHarness.StartAsync(consensus: new FixedConsensus(null, status));
        using var client = await harness.ConnectAsync();
        var received = new List<byte>();

        for (var page = 0; page < 2; page++)
        {
            await client.SendAsync(RendezvousWire.NetworkStatusQuery(page));
            var frame = await client.ReadFrameAsync();

            Assert.True(RendezvousWire.TryReadNetworkStatusPage(frame!, out var index, out var pages, out var chunk, out var why), why);
            Assert.Equal(2, pages);
            received.AddRange(chunk);
        }

        Assert.Equal(status, received.ToArray());
    }

    [Fact]
    public async Task Sans_autorite_l_etat_du_reseau_est_refuse_poliment()
    {
        await using var harness = await ServerHarness.StartAsync();
        using var client = await harness.ConnectAsync();

        await client.SendAsync(RendezvousWire.NetworkStatusQuery(0));

        Assert.Equal(RendezvousKind.Error, (await client.ReadFrameAsync())![0]);
    }

    [Fact]
    public async Task Sans_autorite_le_service_refuse_poliment_la_demande()
    {
        await using var harness = await ServerHarness.StartAsync();
        using var client = await harness.ConnectAsync();

        await client.SendAsync(RendezvousWire.ConsensusQuery(0));
        var frame = await client.ReadFrameAsync();

        Assert.Equal(RendezvousKind.Error, frame![0]);
    }

    [Fact]
    public async Task Le_service_sert_la_liste_par_pages()
    {
        var document = RandomNumberGenerator.GetBytes(70_000);
        await using var harness = await ServerHarness.StartAsync(consensus: new FixedConsensus(document));
        using var client = await harness.ConnectAsync();
        var received = new List<byte>();

        for (var page = 0; page < 3; page++)
        {
            await client.SendAsync(RendezvousWire.ConsensusQuery(page));
            var frame = await client.ReadFrameAsync();

            Assert.True(RendezvousWire.TryReadConsensusPage(frame!, out var index, out var pages, out var chunk, out var why), why);
            Assert.Equal(page, index);
            Assert.Equal(3, pages);
            received.AddRange(chunk);
        }

        Assert.Equal(document, received.ToArray());
    }

    [Fact]
    public async Task Trop_de_pages_ferme_la_session()
    {
        await using var harness = await ServerHarness.StartAsync(consensus: new FixedConsensus(new byte[] { 1, 2, 3 }));
        using var client = await harness.ConnectAsync();

        for (var i = 0; i < RendezvousWire.MaxConsensusPages; i++)
        {
            await client.SendAsync(RendezvousWire.ConsensusQuery(0));
            Assert.Equal(RendezvousKind.ConsensusPage, (await client.ReadFrameAsync())![0]);
        }

        await client.SendAsync(RendezvousWire.ConsensusQuery(0));

        // Des erreurs d'abord (la boucle du service ajoute la sienne à celle du
        // gestionnaire), puis la fermeture, comme pour les pages de bans.
        var frames = new List<byte[]>();

        while (frames.Count < 4 && await client.ReadFrameAsync(TimeSpan.FromSeconds(5)) is { } frame)
            frames.Add(frame);

        Assert.NotEmpty(frames);
        Assert.All(frames, frame => Assert.Equal(RendezvousKind.Error, frame[0]));
        Assert.True(frames.Count < 4, "la connexion est restée ouverte");
    }

    [Fact]
    public async Task Un_service_liste_porte_sa_region_dans_la_v2_et_pas_dans_la_v1()
    {
        _authority.Ledger.Track(new DirectoryEntry("rdv.candidat.ch", "Candidat"), "203.0.113.7");
        _probe.Up["rdv.candidat.ch:47900"] = IPAddress.Parse("203.0.113.7");
        _regions.Known[IPAddress.Parse("203.0.113.7")] = "NA";

        for (var round = 0; round <= 432; round++)
        {
            await _authority.RoundAsync(CancellationToken.None);
            _clock.Advance(AuthorityService.Interval);
        }

        var now = _clock.UtcNow.ToUnixTimeSeconds();

        Assert.True(ServiceConsensus.TryVerify(_authority.DocumentV2!, [_authority.PublicPoint], now, out var v2, out var why), why);
        Assert.Equal("NA", Assert.Single(v2!.Entries).Region);

        Assert.True(ServiceConsensus.TryVerify(_authority.Document!, [_authority.PublicPoint], now, out var v1, out why), why);
        Assert.Null(Assert.Single(v1!.Entries).Region);
        Assert.Equal(v1.Version, v2.Version);
    }

    [Fact]
    public async Task Une_region_qui_change_fait_reemettre_la_liste()
    {
        _authority.Ledger.Track(new DirectoryEntry("rdv.candidat.ch", "Candidat"), "203.0.113.7");
        _probe.Up["rdv.candidat.ch:47900"] = IPAddress.Parse("203.0.113.7");

        for (var round = 0; round <= 432; round++)
        {
            await _authority.RoundAsync(CancellationToken.None);
            _clock.Advance(AuthorityService.Interval);
        }

        var before = _authority.Current!.Version;
        _regions.Known[IPAddress.Parse("203.0.113.7")] = "EU";
        await _authority.RoundAsync(CancellationToken.None);

        Assert.True(_authority.Current!.Version > before);
        Assert.Equal("EU", Assert.Single(_authority.Current.Entries).Region);
    }

    [Fact]
    public async Task Le_service_sert_la_v2_par_pages_et_toujours_la_v1()
    {
        var v1 = RandomNumberGenerator.GetBytes(40_000);
        var v2 = RandomNumberGenerator.GetBytes(40_000);
        await using var harness = await ServerHarness.StartAsync(consensus: new FixedConsensus(v1, documentV2: v2));
        using var client = await harness.ConnectAsync();
        var received = new List<byte>();

        for (var page = 0; page < 2; page++)
        {
            await client.SendAsync(RendezvousWire.ConsensusV2Query(page));
            var frame = await client.ReadFrameAsync();

            Assert.True(RendezvousWire.TryReadConsensusV2Page(frame!, out var index, out var pages, out var chunk, out var why), why);
            Assert.Equal(page, index);
            Assert.Equal(2, pages);
            received.AddRange(chunk);
        }

        Assert.Equal(v2, received.ToArray());

        await client.SendAsync(RendezvousWire.ConsensusQuery(0));
        Assert.True(RendezvousWire.TryReadConsensusPage((await client.ReadFrameAsync())!, out _, out _, out var first, out _));
        Assert.Equal(v1.AsSpan(0, first.Length).ToArray(), first);
    }

    [Fact]
    public async Task Sans_v2_le_service_refuse_poliment_la_demande_v2()
    {
        await using var harness = await ServerHarness.StartAsync(consensus: new FixedConsensus(new byte[] { 1 }));
        using var client = await harness.ConnectAsync();

        await client.SendAsync(RendezvousWire.ConsensusV2Query(0));

        Assert.Equal(RendezvousKind.Error, (await client.ReadFrameAsync())![0]);
    }
}
