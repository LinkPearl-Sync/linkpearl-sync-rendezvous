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
internal sealed class FixedConsensus(byte[]? document) : IConsensusSource
{
    public byte[]? Document => document;
}

public sealed class AuthorityServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"lprdv-authority-{Guid.NewGuid():N}");
    private readonly ManualClock _clock = new();
    private readonly ScriptedProbe _probe = new();
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly AuthorityService _authority;

    public AuthorityServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _authority = new AuthorityService(
            AuthorityLedger.Load(Path.Combine(_dir, "authority.json"), _clock), _probe, _key, _clock)
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
}
