using Linkpearl.Rendezvous;
using Xunit;

namespace Linkpearl.Rendezvous.Tests;

/// <summary>Le drapeau qu'une boucle pose en entrant et retire en sortant.</summary>
public sealed class LoopHealthTests
{
    [Fact]
    public void Morte_avant_dentrer_vivante_entre_les_deux_morte_apres()
    {
        var clock = new ManualClock();
        var loop = new LoopHealth(clock);

        Assert.False(loop.Alive);
        Assert.Null(loop.LastTurn);

        loop.Enter();

        Assert.True(loop.Alive);
        Assert.Equal(clock.UtcNow, loop.LastTurn);

        loop.Exit();

        // Le dernier tour reste lisible après la mort : c'est ce qui dit
        // depuis quand la boucle ne sert plus.
        Assert.False(loop.Alive);
        Assert.Equal(clock.UtcNow, loop.LastTurn);
    }

    [Fact]
    public void Chaque_tour_avance_lhorodatage()
    {
        var clock = new ManualClock();
        var loop = new LoopHealth(clock);
        loop.Enter();

        clock.Advance(TimeSpan.FromMinutes(5));
        loop.Turn();

        Assert.Equal(clock.UtcNow, loop.LastTurn);
    }
}
