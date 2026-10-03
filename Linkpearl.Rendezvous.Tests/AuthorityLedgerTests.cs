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
            Assert.True(ledger.Track(new DirectoryEntry(address, ""), AddressBucket.Of(Where(address))));

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

    private static double? AvailabilityOf(AuthorityLedger ledger, string address)
        => ledger.Snapshot().Single(service => service.Address == address).Availability24h;

    [Fact]
    public void La_disponibilite_glisse_sur_les_144_dernieres_sondes()
    {
        var ledger = Ledger("rdv.a.ch");
        Assert.Null(AvailabilityOf(ledger, "rdv.a.ch:47900"));

        Run(ledger, TimeSpan.FromHours(24), Up, "rdv.a.ch");
        Assert.Equal(1.0, AvailabilityOf(ledger, "rdv.a.ch:47900"));

        // Douze heures de silence : la moitié de la fenêtre.
        Run(ledger, TimeSpan.FromHours(12), Down, "rdv.a.ch");
        Assert.Equal(0.5, AvailabilityOf(ledger, "rdv.a.ch:47900"));
    }

    [Fact]
    public void La_disponibilite_survit_a_un_redemarrage()
    {
        var ledger = Ledger("rdv.a.ch");
        Run(ledger, TimeSpan.FromHours(2), Up, "rdv.a.ch");
        Run(ledger, TimeSpan.FromHours(2), Down, "rdv.a.ch");

        var reloaded = AuthorityLedger.Load(StatePath, _clock);

        Assert.Equal(0.5, AvailabilityOf(reloaded, "rdv.a.ch:47900"));
    }

    [Fact]
    public void Un_service_connu_change_son_libelle_depuis_son_adresse()
    {
        var ledger = AuthorityLedger.Load(StatePath, _clock);
        var submitter = AddressBucket.Of(Where("rdv.a.ch"));
        Assert.True(ledger.Track(new DirectoryEntry("rdv.a.ch", "Ancien"), submitter));

        // Déjà suivi : ce n'est pas une nouvelle candidature, seulement un nom.
        Assert.False(ledger.Track(new DirectoryEntry("rdv.a.ch", "Nouveau"), submitter));

        Assert.Equal("Nouveau", ledger.Snapshot().Single().Label);
        Assert.Equal("Nouveau", AuthorityLedger.Load(StatePath, _clock).Snapshot().Single().Label);
    }

    [Fact]
    public void Un_tiers_ne_renomme_pas_le_service_d_un_autre()
    {
        var ledger = AuthorityLedger.Load(StatePath, _clock);
        Assert.True(ledger.Track(new DirectoryEntry("rdv.a.ch", "Le vrai"), AddressBucket.Of(Where("rdv.a.ch"))));

        Assert.False(ledger.Track(new DirectoryEntry("rdv.a.ch", "Usurpé"), "192.0.2.50"));

        Assert.Equal("Le vrai", ledger.Snapshot().Single().Label);
    }

    [Fact]
    public void Un_service_propose_par_un_tiers_n_entre_jamais()
    {
        // La candidature vient d'une autre adresse que celle où le service
        // répond : quelqu'un inscrit le service d'un autre sans son accord.
        var ledger = AuthorityLedger.Load(StatePath, _clock);
        Assert.True(ledger.Track(new DirectoryEntry("rdv.a.ch", ""), "192.0.2.50"));

        Run(ledger, TimeSpan.FromMinutes(50), Up, "rdv.a.ch");
        Assert.Equal(ServiceStanding.Candidate, StandingOf(ledger, "rdv.a.ch:47900"));

        // Jamais joint depuis l'adresse qui l'a proposé : il est oublié.
        Run(ledger, TimeSpan.FromHours(80), Up, "rdv.a.ch");
        Assert.Empty(ledger.Listed());
        Assert.Empty(ledger.Snapshot());
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

        // Joint une fois : il a montré qu'un service vit à cette adresse.
        Run(ledger, Round, Up, "rdv.a.ch");
        Run(ledger, TimeSpan.FromDays(7) - Round, Down, "rdv.a.ch");
        Assert.Single(ledger.Snapshot());

        Run(ledger, Round, Down, "rdv.a.ch");
        Assert.Empty(ledger.Snapshot());
    }

    [Fact]
    public void Un_candidat_jamais_joint_est_oublie_en_une_heure()
    {
        // Sept jours de grâce pour une adresse morte laissaient n'importe qui
        // remplir le registre au rythme d'une candidature par heure.
        var ledger = Ledger("rdv.a.ch");

        Run(ledger, TimeSpan.FromMinutes(50), Down, "rdv.a.ch");
        Assert.Single(ledger.Snapshot());

        Run(ledger, TimeSpan.FromMinutes(20), Down, "rdv.a.ch");
        Assert.Empty(ledger.Snapshot());
    }

    [Fact]
    public void Un_reseau_ne_fait_pas_suivre_plus_de_quatre_services()
    {
        var ledger = AuthorityLedger.Load(StatePath, _clock);

        for (var i = 1; i <= AuthorityLedger.MaxTrackedPerNetwork; i++)
            Assert.True(ledger.Track(new DirectoryEntry($"rdv.s{i}.ch", ""), $"203.0.113.{i}"));

        Assert.False(ledger.Track(new DirectoryEntry("rdv.s9.ch", ""), "203.0.113.9"));
        Assert.True(ledger.Track(new DirectoryEntry("rdv.s9.ch", ""), "203.0.114.9"));

        // En IPv6, le /48 : deux /64 différents d'un même /48 comptent ensemble.
        for (var i = 1; i <= AuthorityLedger.MaxTrackedPerNetwork; i++)
            Assert.True(ledger.Track(new DirectoryEntry($"rdv.v{i}.ch", ""), $"2001:db8:5:{i:x}::/64"));

        Assert.False(ledger.Track(new DirectoryEntry("rdv.v9.ch", ""), "2001:db8:5:ff::/64"));
        Assert.True(ledger.Track(new DirectoryEntry("rdv.v9.ch", ""), "2001:db8:6:ff::/64"));
    }

    [Fact]
    public void Un_libelle_qui_porte_un_controle_ou_une_inversion_est_refuse()
    {
        var ledger = AuthorityLedger.Load(StatePath, _clock);

        Assert.False(ledger.Track(new DirectoryEntry("rdv.a.ch", "Deux\nlignes"), "198.51.100.1"));
        Assert.False(ledger.Track(new DirectoryEntry("rdv.a.ch", "abc\u202Edcba"), "198.51.100.1"));
        Assert.Empty(ledger.Snapshot());

        Assert.True(ledger.Track(new DirectoryEntry("rdv.a.ch", "Éorzéa, chez nous"), "198.51.100.1"));
        Assert.False(ledger.Track(new DirectoryEntry("rdv.a.ch", "Faux\u200Fnom"), "198.51.100.1"));
        Assert.Equal("Éorzéa, chez nous", ledger.Snapshot().Single().Label);
    }

    [Fact]
    public void Un_service_liste_qui_change_de_nom_repasse_en_probation()
    {
        var ledger = Ledger("rdv.a.ch");
        Run(ledger, TimeSpan.FromHours(73), Up, "rdv.a.ch");
        Assert.Equal(ServiceStanding.Listed, StandingOf(ledger, "rdv.a.ch:47900"));

        ledger.Track(new DirectoryEntry("rdv.a.ch", "Autre nom"), AddressBucket.Of(Where("rdv.a.ch")));

        Assert.Equal(ServiceStanding.Probation, StandingOf(ledger, "rdv.a.ch:47900"));
        Assert.Empty(ledger.Listed());
    }

    [Fact]
    public void Trois_services_du_meme_sous_reseau_n_en_listent_que_deux()
    {
        _where["rdv.a.ch"] = IPAddress.Parse("203.0.113.1");
        _where["rdv.b.ch"] = IPAddress.Parse("203.0.113.2");
        _where["rdv.c.ch"] = IPAddress.Parse("203.0.113.3");
        var ledger = Ledger("rdv.a.ch", "rdv.b.ch", "rdv.c.ch");

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
        Assert.False(ledger.Track(new DirectoryEntry("rdv.a.ch", ""), AddressBucket.Of(Where("rdv.a.ch"))));

        Run(ledger, TimeSpan.FromHours(80), Up, "rdv.a.ch");
        Assert.Empty(ledger.Listed());
        Assert.Equal(ServiceStanding.Vetoed, StandingOf(ledger, "rdv.a.ch:47900"));

        Assert.True(ledger.Lift("rdv.a.ch"));
        Assert.Equal(ServiceStanding.Candidate, StandingOf(ledger, "rdv.a.ch:47900"));
    }

    [Fact]
    public void Un_service_en_probation_qui_repond_s_admet_a_la_main()
    {
        var ledger = Ledger("rdv.a.ch");
        Run(ledger, TimeSpan.FromHours(2), Up, "rdv.a.ch");

        Assert.Equal(AdmitOutcome.Admitted, ledger.Admit("rdv.a.ch"));
        Assert.Equal("rdv.a.ch:47900", Assert.Single(ledger.Listed()).Address);
    }

    [Fact]
    public void Un_service_muet_a_la_derniere_sonde_ne_s_admet_pas()
    {
        var ledger = Ledger("rdv.a.ch");
        Run(ledger, TimeSpan.FromHours(2), Up, "rdv.a.ch");
        Run(ledger, Round, Down, "rdv.a.ch");

        Assert.Equal(AdmitOutcome.NotAnswering, ledger.Admit("rdv.a.ch"));
        Assert.Empty(ledger.Listed());
    }

    [Fact]
    public void Un_candidat_jamais_joint_ne_s_admet_pas()
    {
        var ledger = Ledger("rdv.a.ch");

        Assert.Equal(AdmitOutcome.NotOnProbation, ledger.Admit("rdv.a.ch"));
        Assert.Equal(AdmitOutcome.Unknown, ledger.Admit("rdv.inconnu.ch"));
    }

    [Fact]
    public void L_admission_a_la_main_garde_la_borne_par_reseau()
    {
        _where["rdv.a.ch"] = IPAddress.Parse("203.0.113.1");
        _where["rdv.b.ch"] = IPAddress.Parse("203.0.113.2");
        _where["rdv.c.ch"] = IPAddress.Parse("203.0.113.3");
        var ledger = Ledger("rdv.a.ch", "rdv.b.ch", "rdv.c.ch");
        Run(ledger, TimeSpan.FromHours(73), Up, "rdv.a.ch", "rdv.b.ch", "rdv.c.ch");

        Assert.Equal(AdmitOutcome.FamilyFull, ledger.Admit("rdv.c.ch"));
        Assert.Equal(2, ledger.Listed().Count);
    }

    [Fact]
    public void L_admission_a_la_main_garde_le_plafond_du_jour()
    {
        string[] six = ["rdv.a.ch", "rdv.b.ch", "rdv.c.ch", "rdv.d.ch", "rdv.e.ch", "rdv.f.ch"];
        var ledger = Ledger(six);
        Run(ledger, TimeSpan.FromHours(2), Up, six);

        foreach (var address in six[..5])
            Assert.Equal(AdmitOutcome.Admitted, ledger.Admit(address));

        Assert.Equal(AdmitOutcome.DailyCapReached, ledger.Admit("rdv.f.ch"));
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
    public void Un_registre_remis_a_zero_ne_fait_pas_regresser_la_version()
    {
        var before = Ledger().NextVersion();

        // L'opérateur retire le registre, comme le message d'erreur l'y invite.
        File.Delete(StatePath);
        _clock.Advance(TimeSpan.FromMinutes(1));

        Assert.True(Ledger().NextVersion() > before);
    }

    [Fact]
    public void Un_registre_illisible_arrete_le_demarrage_sans_etre_ecrase()
    {
        File.WriteAllText(StatePath, "{ pas du json");

        Assert.Throws<InvalidOperationException>(() => AuthorityLedger.Load(StatePath, _clock));
        Assert.Equal("{ pas du json", File.ReadAllText(StatePath));
    }

    [Fact]
    public void La_region_survit_a_un_redemarrage()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lprdv-ledger-{Guid.NewGuid():N}.json");
        var clock = new ManualClock();

        try
        {
            var ledger = AuthorityLedger.Load(path, clock);
            ledger.Track(new DirectoryEntry("rdv.candidat.ch", "Candidat"), "203.0.113.7");
            ledger.Record("rdv.candidat.ch", new ProbeResult(true, IPAddress.Parse("203.0.113.7")), "OC");
            ledger.Settle();

            var reloaded = AuthorityLedger.Load(path, clock);

            Assert.Equal("OC", Assert.Single(reloaded.Snapshot()).Region);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Un registre listé, dont la région est réécrite à la main comme un
    /// opérateur pourrait le faire, ou retirée comme dans un registre d'avant
    /// les régions.
    /// </summary>
    private AuthorityLedger ReloadedWithRegion(string? region)
    {
        var ledger = Ledger("rdv.a.ch");
        Run(ledger, TimeSpan.FromHours(72) + Round, Up, "rdv.a.ch");

        var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(StatePath))!.AsObject();
        var service = root["services"]!.AsArray().Single()!.AsObject();
        service.Remove("region");

        if (region is not null)
            service["region"] = region;

        File.WriteAllText(StatePath, root.ToJsonString());
        return AuthorityLedger.Load(StatePath, _clock);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("eu")]
    [InlineData("XX")]
    [InlineData("EUR")]
    public void Une_region_absente_ou_malformee_se_charge_sans_region_et_l_autorite_signe(string? region)
    {
        var reloaded = ReloadedWithRegion(region);

        Assert.Null(Assert.Single(reloaded.Snapshot()).Region);

        var listed = reloaded.Listed();
        Assert.Null(Assert.Single(listed).Region);

        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var list = new ServiceConsensus(reloaded.NextVersion(), 0, (long)ServiceConsensus.Lifetime.TotalSeconds, listed);
        Assert.NotEmpty(ServiceConsensus.SignV2(list, key));
    }
}
