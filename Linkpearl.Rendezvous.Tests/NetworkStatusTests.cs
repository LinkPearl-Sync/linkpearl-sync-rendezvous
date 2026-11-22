using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Linkpearl.Core.Transport.Rendezvous;
using Xunit;

namespace Linkpearl.Rendezvous.Tests;

/// <summary>L'état public du réseau : ce qu'il montre, et surtout ce qu'il tait.</summary>
public sealed class NetworkStatusTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 18, 0, 0, TimeSpan.Zero);

    private static TrackedService Service(
        string address, ServiceStanding standing, DateTimeOffset? since = null, int probes = 0, int successes = 0,
        double? availability = null)
        => new(address, "Libellé de " + address, standing, probes, successes,
            FirstSeen: Now.AddDays(-5),
            LastSuccess: Now.AddMinutes(-10),
            ProbationStart: standing is ServiceStanding.Probation ? since : null,
            ListedAt: standing is ServiceStanding.Listed ? since : null,
            DelistedAt: standing is ServiceStanding.Delisted ? since : null,
            Availability24h: availability);

    private static JsonObject Build(IReadOnlyList<TrackedService> services, ServiceConsensus? current = null)
        => JsonNode.Parse(Encoding.UTF8.GetString(
            NetworkStatus.Build(services, current, [0x04, 0xAA], Now)))!.AsObject();

    [Fact]
    public void Les_ecartes_et_les_candidats_ne_sont_jamais_nommes()
    {
        var status = Build(
        [
            Service("rdv.liste.ch:47900", ServiceStanding.Listed, Now.AddDays(-2), availability: 1.0),
            Service("rdv.ecarte.ch:47900", ServiceStanding.Vetoed),
            Service("rdv.inconnu.ch:47900", ServiceStanding.Candidate),
        ]);

        var text = status.ToJsonString();

        Assert.DoesNotContain("rdv.ecarte.ch", text);
        Assert.DoesNotContain("rdv.inconnu.ch", text);
        Assert.Equal(1, status["counts"]!["candidates"]!.GetValue<int>());
        Assert.Equal(1, status["counts"]!["listed"]!.GetValue<int>());
        Assert.Single(status["services"]!.AsArray());
    }

    [Fact]
    public void Un_service_en_probation_montre_son_avancee()
    {
        var status = Build([Service("rdv.p.ch:47900", ServiceStanding.Probation, Now.AddHours(-41), probes: 100, successes: 97)]);
        var service = status["services"]![0]!;

        Assert.Equal("probation", service["standing"]!.GetValue<string>());
        Assert.Equal(41, service["probation"]!["hours"]!.GetValue<int>());
        Assert.Equal(0.97, service["probation"]!["ratio"]!.GetValue<double>());
        Assert.Equal(Now.AddHours(-41).ToUnixTimeSeconds(), service["since"]!.GetValue<long>());
    }

    [Fact]
    public void Un_service_sans_sonde_recente_n_affiche_pas_de_disponibilite()
    {
        var service = Build([Service("rdv.s.ch:47900", ServiceStanding.Delisted, Now.AddHours(-3))])["services"]![0]!;

        Assert.Null(service["availability24h"]);
        Assert.Equal("delisted", service["standing"]!.GetValue<string>());
    }

    [Fact]
    public void L_autorite_n_apparait_qu_une_fois_une_liste_emise()
    {
        Assert.Null(Build([])["authority"]);

        var list = new ServiceConsensus(42, 1_790_000_000, 1_790_604_800, []);
        var authority = Build([], list)["authority"]!;

        Assert.Equal("04aa", authority["publicKey"]!.GetValue<string>());
        Assert.Equal(42u, authority["listVersion"]!.GetValue<uint>());
        Assert.Equal(1_790_604_800, authority["expires"]!.GetValue<long>());
    }

    [Fact]
    public void Le_document_porte_sa_version_et_son_heure()
    {
        var status = Build([]);

        Assert.Equal(1, status["version"]!.GetValue<int>());
        Assert.Equal(Now.ToUnixTimeSeconds(), status["generated"]!.GetValue<long>());
    }

    [Fact]
    public void L_etat_publie_porte_la_signature_de_l_autorite()
    {
        // L'audit : reseau.json, publié sur le site officiel avec la clé de
        // l'autorité et son empreinte, venait d'une réponse TCP en clair, que
        // tout observateur du trajet pouvait réécrire dans la forme attendue.
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var document = NetworkStatus.Build([Service("rdv.liste.ch:47900", ServiceStanding.Listed, Now)], null, [0x04], Now);

        var signed = NetworkStatus.Sign(document, key);

        Assert.True(NetworkStatus.TryVerify(signed, [ServiceConsensus.PublicPoint(key)], out var verified));
        Assert.Equal(document, verified);
        Assert.NotNull(JsonNode.Parse(signed)!["signature"]);
    }

    [Fact]
    public void Un_etat_retouche_ou_signe_par_une_autre_cle_est_refuse()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signed = NetworkStatus.Sign(
            NetworkStatus.Build([Service("rdv.liste.ch:47900", ServiceStanding.Listed, Now)], null, [0x04], Now), key);

        Assert.False(NetworkStatus.TryVerify(signed, [ServiceConsensus.PublicPoint(other)], out _));

        var tampered = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(signed).Replace("rdv.liste.ch", "rdv.pirate.ch"));
        Assert.False(NetworkStatus.TryVerify(tampered, [ServiceConsensus.PublicPoint(key)], out _));

        Assert.False(NetworkStatus.TryVerify(NetworkStatus.Build([], null, [0x04], Now), [ServiceConsensus.PublicPoint(key)], out _));
    }
}
