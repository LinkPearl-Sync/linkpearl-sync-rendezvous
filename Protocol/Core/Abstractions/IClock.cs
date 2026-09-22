namespace Linkpearl.Core.Abstractions;

/// <summary>
/// L'heure, injectée.
/// </summary>
/// <remarks>
/// Le noyau ne lit jamais l'horloge système, et un test d'architecture le fait
/// respecter. Une fenêtre anti-rejeu ou un ticket de rendez-vous qui liraient
/// l'heure directement ne seraient pas testables de manière déterministe, et
/// c'est exactement la sorte de test qui devient intermittent six mois plus
/// tard. L'implémentation système vit dans Integration/, hors du noyau.
/// </remarks>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
