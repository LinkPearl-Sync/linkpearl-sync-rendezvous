using Linkpearl.Core.Abstractions;

namespace Linkpearl.Rendezvous.Tests;

/// <summary>Une horloge que le test fait avancer à la main.</summary>
public sealed class ManualClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    public void Advance(TimeSpan by) => UtcNow += by;
}
