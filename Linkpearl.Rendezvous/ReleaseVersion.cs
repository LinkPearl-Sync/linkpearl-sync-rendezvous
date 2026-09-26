using System.Reflection;

namespace Linkpearl.Rendezvous;

/// <summary>Les versions publiées, telles que la mise à jour les compare.</summary>
/// <remarks>
/// Trois composants numériques exactement : <c>0.6</c> et <c>0.6.0</c> ne se
/// comparent pas comme on l'attend avec <see cref="Version"/>, et un suffixe
/// (<c>-rc1</c>, <c>-3-gabc</c>) désigne une pré-version ou un binaire compilé
/// à la main, que la mise à jour automatique n'a pas à toucher.
/// </remarks>
public static class ReleaseVersion
{
    public static bool TryParse(string? text, out Version version)
    {
        version = new Version();
        var trimmed = text?.Trim().TrimStart('v');

        if (string.IsNullOrEmpty(trimmed) || trimmed.Split('.').Length != 3 || Version.TryParse(trimmed, out var parsed) is false)
            return false;

        version = parsed;
        return true;
    }

    /// <summary>La version du binaire en place, ou null si elle ne se lit pas.</summary>
    /// <remarks>Le SDK accole « +commit » à la version informationnelle.</remarks>
    public static Version? Current { get; } = TryParse(
        typeof(ReleaseVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0],
        out var current) ? current : null;
}
