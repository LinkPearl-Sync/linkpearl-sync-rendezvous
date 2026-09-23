using System.Collections.Concurrent;
using Linkpearl.Core.Abstractions;

namespace Linkpearl.Rendezvous;

/// <summary>
/// Fait attendre celui qui enchaîne les essais ratés sur la console.
/// </summary>
/// <remarks>
/// Un jeton de trente-deux octets ne se devine pas, donc ce délai ne protège
/// pas le secret : il évite qu'un proxy mal réglé ou un robot ne remplisse le
/// journal et ne consomme la machine à raison de mille essais par seconde. Il
/// croît avec les échecs et s'efface au premier succès.
///
/// Le registre est borné : un robot qui change d'adresse à chaque essai ferait
/// sinon grossir le service sans fin. Plein, il oublie d'abord les entrées
/// périmées ; toujours plein, il oublie tout, ce qui ne coûte qu'un délai de
/// moins à des adresses qu'on ne reverra pas.
/// </remarks>
public sealed class FailureTracker(IClock clock)
{
    public const int FailuresBeforeSlowing = 5;

    public const int Capacity = 1024;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(500);

    private const int MaxSteps = 10;

    private readonly ConcurrentDictionary<string, (int Failures, DateTimeOffset Since)> _failures =
        new(StringComparer.Ordinal);

    public int Tracked => _failures.Count;

    /// <summary>Note un échec, et rend le délai à imposer avant de répondre.</summary>
    public TimeSpan Record(string origin)
    {
        var now = clock.UtcNow;

        if (_failures.Count >= Capacity)
            Prune(now);

        var state = _failures.AddOrUpdate(
            origin,
            _ => (1, now),
            (_, previous) => now - previous.Since > Window ? (1, now) : (previous.Failures + 1, previous.Since));

        if (state.Failures <= FailuresBeforeSlowing)
            return TimeSpan.Zero;

        return Step * Math.Min(state.Failures - FailuresBeforeSlowing, MaxSteps);
    }

    /// <summary>Un succès efface l'ardoise de cette adresse.</summary>
    public void Clear(string origin) => _failures.TryRemove(origin, out _);

    private void Prune(DateTimeOffset now)
    {
        foreach (var (origin, state) in _failures)
        {
            if (now - state.Since > Window)
                _failures.TryRemove(new KeyValuePair<string, (int, DateTimeOffset)>(origin, state));
        }

        if (_failures.Count >= Capacity)
            _failures.Clear();
    }
}
