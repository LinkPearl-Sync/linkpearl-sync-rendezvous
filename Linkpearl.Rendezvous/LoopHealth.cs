using Linkpearl.Core.Abstractions;

namespace Linkpearl.Rendezvous;

/// <summary>
/// Ce qu'une boucle de service dit d'elle-même : si elle tourne, et depuis
/// quand elle n'a rien fait.
/// </summary>
/// <remarks>
/// Une boucle d'acceptation ou de réflexion qui meurt sur une exception
/// laisse le processus debout et le port lié : de l'extérieur, le service
/// paraît vivant alors qu'il est sourd. La seule façon fiable de le savoir est
/// que la boucle elle-même pose un drapeau en entrant et le retire en sortant,
/// quelle que soit la raison de la sortie. L'horodatage du dernier tour
/// complète le drapeau : une boucle vivante mais qui n'a rien fait depuis des
/// heures se distingue d'une boucle qui vient de servir.
///
/// Le dernier tour est rangé en ticks sous <see cref="Interlocked"/> plutôt
/// qu'en <see cref="DateTimeOffset"/> : la structure fait seize octets, et une
/// lecture déchirée depuis la console donnerait une date absurde.
/// </remarks>
public sealed class LoopHealth(IClock clock)
{
    private int _alive;
    private long _lastTurnTicks;

    /// <summary>Vrai entre l'entrée dans la boucle et sa sortie, quelle qu'en soit la cause.</summary>
    public bool Alive => Volatile.Read(ref _alive) is 1;

    /// <summary>Le dernier tour achevé, ou null si la boucle n'a encore rien fait.</summary>
    public DateTimeOffset? LastTurn
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastTurnTicks);
            return ticks is 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    public void Enter()
    {
        Volatile.Write(ref _alive, 1);
        Turn();
    }

    public void Turn() => Interlocked.Exchange(ref _lastTurnTicks, clock.UtcNow.UtcTicks);

    public void Exit() => Volatile.Write(ref _alive, 0);
}
