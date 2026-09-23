using Linkpearl.Core.Abstractions;

namespace Linkpearl.Rendezvous;

/// <summary>
/// L'horloge du système, seule implémentation de <see cref="IClock"/> hors des tests.
/// </summary>
/// <remarks>
/// Le service ne lit l'heure que par cette interface : c'est ce qui permet à
/// un test de faire expirer une attente ou une invitation sans attendre pour
/// de vrai, donc sans devenir intermittent.
/// </remarks>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
