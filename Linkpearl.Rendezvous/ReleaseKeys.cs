namespace Linkpearl.Rendezvous;

/// <summary>
/// Les clés dont une release doit porter la signature pour être installée
/// automatiquement.
/// </summary>
/// <remarks>
/// Distincte de directory.key : la fuite de l'une ne compromet pas l'autre. La
/// privée est un secret de l'environnement GitHub « release », que seuls les
/// tags v* atteignent, et une copie hors ligne chez le mainteneur. Changer de
/// clé : publier une release signée par l'ancienne qui inscrit la nouvelle ici.
/// </remarks>
public static class ReleaseKeys
{
    public static IReadOnlyList<byte[]> Trusted { get; } =
    [
        // Engendrée le 26 septembre 2026, ~/.ssh/linkpearl_release.key chez le mainteneur.
        Convert.FromHexString(
            "0415b4c82a4789ad28d050978666ac9784cdf42e0ae7fc216e1648b0d969ad8089fde3a8c360752564a532c1e290f37e13970f5ce57f3c200e11470e938355f941"),
    ];
}
