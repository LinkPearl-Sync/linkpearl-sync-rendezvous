using Linkpearl.Rendezvous;
using Xunit;

namespace Linkpearl.Rendezvous.Tests;

/// <summary>L'anneau d'une journée : un point par minute, des écarts et non des cumuls.</summary>
public sealed class HistoryTests
{
    private static RendezvousServer.Counters Counters(int mailboxes, long matches, long relayed, long rateRefusals, long refusedConnections = 0)
        => new(mailboxes, 0, 0, 0, 0, matches, 0, relayed, 0, refusedConnections, rateRefusals, 0, 0, 0,
            new RendezvousServer.Refusals(0, 0, 0, 0, refusedConnections));

    [Fact]
    public void Les_cumuls_deviennent_des_ecarts_par_minute()
    {
        var clock = new ManualClock();
        var history = new History();

        history.Record(clock.UtcNow, Counters(mailboxes: 2, matches: 10, relayed: 1000, rateRefusals: 1));

        clock.Advance(TimeSpan.FromMinutes(1));
        history.Record(clock.UtcNow, Counters(mailboxes: 5, matches: 13, relayed: 1500, rateRefusals: 1, refusedConnections: 2));

        var points = history.Points(clock.UtcNow, 2);

        Assert.Equal(2, points.Count);
        Assert.Equal(10, points[0].Matches);
        Assert.Equal(1000, points[0].RelayedBytes);
        Assert.Equal(1, points[0].Refusals);

        Assert.Equal(5, points[1].OpenMailboxes);
        Assert.Equal(3, points[1].Matches);
        Assert.Equal(500, points[1].RelayedBytes);
        Assert.Equal(2, points[1].Refusals);
    }

    [Fact]
    public void Plusieurs_releves_dans_la_meme_minute_se_cumulent_et_la_jauge_garde_son_maximum()
    {
        // Le balayage passe toutes les dix secondes : six relevés par minute,
        // qui doivent faire un seul point.
        var clock = new ManualClock();
        var history = new History();

        history.Record(clock.UtcNow, Counters(mailboxes: 3, matches: 1, relayed: 0, rateRefusals: 0));
        clock.Advance(TimeSpan.FromSeconds(10));
        history.Record(clock.UtcNow, Counters(mailboxes: 7, matches: 2, relayed: 0, rateRefusals: 0));
        clock.Advance(TimeSpan.FromSeconds(10));
        history.Record(clock.UtcNow, Counters(mailboxes: 4, matches: 4, relayed: 0, rateRefusals: 0));

        var point = Assert.Single(history.Points(clock.UtcNow, 1));

        Assert.Equal(7, point.OpenMailboxes);
        Assert.Equal(4, point.Matches);
    }

    [Fact]
    public void Une_minute_sans_releve_vaut_zero_et_non_le_trafic_de_la_veille()
    {
        var clock = new ManualClock();
        var history = new History();

        history.Record(clock.UtcNow, Counters(mailboxes: 9, matches: 9, relayed: 9, rateRefusals: 9));

        // Un jour plus tard, la même case de l'anneau est réutilisée.
        clock.Advance(TimeSpan.FromMinutes(History.Capacity));

        var points = history.Points(clock.UtcNow, 1);

        Assert.Equal(0, points[0].OpenMailboxes);
        Assert.Equal(0, points[0].Matches);
        Assert.Equal(clock.UtcNow.ToUnixTimeSeconds() / 60 * 60, points[0].At);
    }

    [Fact]
    public void La_fenetre_demandee_est_bornee_a_une_journee_et_ordonnee()
    {
        var clock = new ManualClock();
        var history = new History();

        var points = history.Points(clock.UtcNow, 10_000);

        Assert.Equal(History.Capacity, points.Count);
        Assert.True(points[0].Minute < points[^1].Minute);
        Assert.Equal(clock.UtcNow.ToUnixTimeSeconds() / 60, points[^1].Minute);
    }
}
