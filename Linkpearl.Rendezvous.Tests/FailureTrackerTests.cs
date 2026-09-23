using Linkpearl.Rendezvous;
using Xunit;

namespace Linkpearl.Rendezvous.Tests;

/// <summary>Le frein des essais ratés sur la console : il ralentit, et il reste borné.</summary>
public sealed class FailureTrackerTests
{
    [Fact]
    public void Le_delai_croit_avec_les_echecs_et_sefface_au_succes()
    {
        var tracker = new FailureTracker(new ManualClock());

        for (var i = 0; i < FailureTracker.FailuresBeforeSlowing; i++)
            Assert.Equal(TimeSpan.Zero, tracker.Record("198.51.100.7"));

        Assert.True(tracker.Record("198.51.100.7") > TimeSpan.Zero);

        tracker.Clear("198.51.100.7");

        Assert.Equal(TimeSpan.Zero, tracker.Record("198.51.100.7"));
    }

    [Fact]
    public void Les_echecs_anciens_ne_comptent_plus()
    {
        var clock = new ManualClock();
        var tracker = new FailureTracker(clock);

        for (var i = 0; i <= FailureTracker.FailuresBeforeSlowing; i++)
            tracker.Record("198.51.100.7");

        clock.Advance(FailureTracker.Window + TimeSpan.FromSeconds(1));

        Assert.Equal(TimeSpan.Zero, tracker.Record("198.51.100.7"));
    }

    [Fact]
    public void Le_registre_reste_borne()
    {
        // Un robot qui change d'adresse à chaque essai ne doit pas faire
        // grossir le service indéfiniment.
        var clock = new ManualClock();
        var tracker = new FailureTracker(clock);

        for (var i = 0; i < FailureTracker.Capacity + 100; i++)
            tracker.Record($"2001:db8::{i:x}");

        Assert.InRange(tracker.Tracked, 1, FailureTracker.Capacity);
    }

    [Fact]
    public void Plein_il_oublie_dabord_les_entrees_perimees()
    {
        var clock = new ManualClock();
        var tracker = new FailureTracker(clock);

        for (var i = 0; i < FailureTracker.Capacity; i++)
            tracker.Record($"2001:db8::{i:x}");

        clock.Advance(FailureTracker.Window + TimeSpan.FromSeconds(1));
        tracker.Record("198.51.100.7");

        Assert.Equal(1, tracker.Tracked);
    }
}
