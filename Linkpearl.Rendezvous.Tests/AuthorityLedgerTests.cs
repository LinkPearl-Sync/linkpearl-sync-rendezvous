using System.Net;
using Linkpearl.Core.Transport.Rendezvous;
using Xunit;

namespace Linkpearl.Rendezvous.Tests;

/// <summary>Probation, sortie, retour, oubli et bornes, sous horloge simulée.</summary>
public sealed class AuthorityLedgerTests : IDisposable
{
    private static readonly TimeSpan Round = TimeSpan.FromMinutes(10);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"lprdv-ledger-{Guid.NewGuid():N}");
    private readonly ManualClock _clock = new();
    private readonly Dictionary<string, IPAddress> _where = [];

    public AuthorityLedgerTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string StatePath => Path.Combine(_dir, "authority.json");

    private AuthorityLedger Ledger(params string[] addresses)
    {
        var ledger = AuthorityLedger.Load(StatePath, _clock);

        foreach (var address in addresses)
            Assert.True(ledger.Track(new DirectoryEntry(address, "")));

        return ledger;
    }

    /// <summary>L'adresse vue pour un service : un /24 à lui, sauf si le test l'a réglée.</summary>
    private IPAddress Where(string address)
    {
        if (_where.TryGetValue(address, out var at) is false)
            _where[address] = at = IPAddress.Parse($"198.51.{_where.Count + 1}.1");

        return at;
    }

    /// <summary>Une sonde toutes les dix minutes pendant <paramref name="duration"/>.</summary>
    private void Run(AuthorityLedger ledger, TimeSpan duration, Func<string, bool> reached, params string[] addresses)
    {
        for (var elapsed = TimeSpan.Zero; elapsed < duration; elapsed += Round)
        {
            foreach (var address in addresses)
                ledger.Record(address, reached(address) ? new ProbeResult(true, Where(address)) : new ProbeResult(false, null));

            ledger.Settle();
            _clock.Advance(Round);
        }
    }

    private static bool Up(string _) => true;

    private static bool Down(string _) => false;

    private static ServiceStanding StandingOf(AuthorityLedger ledger, string address)
        => ledger.Snapshot().Single(service => service.Address == address).Standing;

    [Fact]
    public void Un_candidat_joignable_entre_apres_72_heures()
    {
        var ledger = Ledger("rdv.a.ch");

        Run(ledger, TimeSpan.FromHours(72), Up, "rdv.a.ch");
        Assert.Empty(ledger.Listed());

        Run(ledger, Round, Up, "rdv.a.ch");
        Assert.Equal("rdv.a.ch:47900", Assert.Single(ledger.Listed()).Address);
    }

    [Fact]
    public void Un_candidat_sous_95_pour_cent_n_entre_pas()
    {
        var ledger = Ledger("rdv.a.ch");
        var probe = 0;

        // Une sonde sur dix échoue : 90 %.
        Run(ledger, TimeSpan.FromHours(80), _ => ++probe % 10 != 0, "rdv.a.ch");

        Assert.Empty(ledger.Listed());
        Assert.Equal(ServiceStanding.Probation, StandingOf(ledger, "rdv.a.ch:47900"));
    }

    [Fact]
    public void Un_service_liste_sort_apres_24_heures_de_silence()
    {
        var ledger = Ledger("rdv.a.ch");
        Run(ledger, TimeSpan.FromHours(72) + Round, Up, "rdv.a.ch");

        // Le dernier succès date de la ronde d'admission : 24 heures pile
        // tombent sur la dernière ronde de cette série-ci, pas sur la suivante.
        Run(ledger, TimeSpan.FromHours(24) - Round, Down, "rdv.a.ch");
        Assert.Single(ledger.Listed());

        Run(ledger, Round, Down, "rdv.a.ch");
        Assert.Empty(ledger.Listed());
        Assert.Equal(ServiceStanding.Delisted, StandingOf(ledger, "rdv.a.ch:47900"));
    }

    [Fact]
    public void Un_retour_dans_les_72_heures_reprend_sa_place()
    {
        var ledger = Ledger("rdv.a.ch");
        Run(ledger, TimeSpan.FromHours(72) + Round, Up, "rdv.a.ch");
        Run(ledger, TimeSpan.FromHours(24) + Round, Down, "rdv.a.ch");
        Run(ledger, TimeSpan.FromHours(48), Down, "rdv.a.ch");

        Run(ledger, Round, Up, "rdv.a.ch");

        Assert.Single(ledger.Listed());
    }

    [Fact]
    public void Un_retour_plus_tard_recommence_la_probation()
    {
        var ledger = Ledger("rdv.a.ch");
        Run(ledger, TimeSpan.FromHours(72) + Round, Up, "rdv.a.ch");
        Run(ledger, TimeSpan.FromHours(24) + Round, Down, "rdv.a.ch");
        Run(ledger, TimeSpan.FromHours(73), Down, "rdv.a.ch");

        Run(ledger, Round, Up, "rdv.a.ch");

        Assert.Empty(ledger.Listed());
        Assert.Equal(ServiceStanding.Probation, StandingOf(ledger, "rdv.a.ch:47900"));
    }

    [Fact]
    public void Un_candidat_sans_reponse_pendant_7_jours_est_oublie()
    {
        var ledger = Ledger("rdv.a.ch");

        Run(ledger, TimeSpan.FromDays(7), Down, "rdv.a.ch");
        Assert.Single(ledger.Snapshot());

        Run(ledger, Round, Down, "rdv.a.ch");
        Assert.Empty(ledger.Snapshot());
    }

    [Fact]
    public void Trois_services_du_meme_sous_reseau_n_en_listent_que_deux()
    {
        var ledger = Ledger("rdv.a.ch", "rdv.b.ch", "rdv.c.ch");
        _where["rdv.a.ch"] = IPAddress.Parse("203.0.113.1");
        _where["rdv.b.ch"] = IPAddress.Parse("203.0.113.2");
        _where["rdv.c.ch"] = IPAddress.Parse("203.0.113.3");

        Run(ledger, TimeSpan.FromHours(73), Up, "rdv.a.ch", "rdv.b.ch", "rdv.c.ch");

        Assert.Equal(2, ledger.Listed().Count);
    }

    [Fact]
    public void Six_candidats_murs_le_meme_jour_n_en_listent_que_cinq()
    {
        string[] six = ["rdv.a.ch", "rdv.b.ch", "rdv.c.ch", "rdv.d.ch", "rdv.e.ch", "rdv.f.ch"];
        var ledger = Ledger(six);

        Run(ledger, TimeSpan.FromHours(73), Up, six);
        Assert.Equal(5, ledger.Listed().Count);

        Run(ledger, TimeSpan.FromHours(24), Up, six);
        Assert.Equal(6, ledger.Listed().Count);
    }

    [Fact]
    public void Un_service_ecarte_sort_et_ne_revient_pas_seul()
    {
        var ledger = Ledger("rdv.a.ch");
        Run(ledger, TimeSpan.FromHours(72) + Round, Up, "rdv.a.ch");

        Assert.True(ledger.Veto("rdv.a.ch"));
        Assert.Empty(ledger.Listed());
        Assert.False(ledger.Track(new DirectoryEntry("rdv.a.ch", "")));

        Run(ledger, TimeSpan.FromHours(80), Up, "rdv.a.ch");
        Assert.Empty(ledger.Listed());
        Assert.Equal(ServiceStanding.Vetoed, StandingOf(ledger, "rdv.a.ch:47900"));

        Assert.True(ledger.Lift("rdv.a.ch"));
        Assert.Equal(ServiceStanding.Candidate, StandingOf(ledger, "rdv.a.ch:47900"));
    }

    [Fact]
    public void Ecarter_un_service_inconnu_ne_fait_rien()
        => Assert.False(Ledger().Veto("rdv.inconnu.ch"));

    [Fact]
    public void L_etat_survit_a_un_redemarrage()
    {
        var ledger = Ledger("rdv.a.ch");
        Run(ledger, TimeSpan.FromHours(72) + Round, Up, "rdv.a.ch");
        var version = ledger.NextVersion();

        var reloaded = AuthorityLedger.Load(StatePath, _clock);

        Assert.Single(reloaded.Listed());
        Assert.Equal(version + 1, reloaded.NextVersion());
    }

    [Fact]
    public void Un_registre_illisible_arrete_le_demarrage_sans_etre_ecrase()
    {
        File.WriteAllText(StatePath, "{ pas du json");

        Assert.Throws<InvalidOperationException>(() => AuthorityLedger.Load(StatePath, _clock));
        Assert.Equal("{ pas du json", File.ReadAllText(StatePath));
    }
}
