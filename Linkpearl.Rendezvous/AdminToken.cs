using System.Security.Cryptography;

namespace Linkpearl.Rendezvous;

/// <summary>
/// Le jeton qui ouvre la console d'administration.
/// </summary>
/// <remarks>
/// Engendré, jamais choisi : aucun mot de passe par défaut, aucune valeur
/// devinable, et rien à retenir. Le perdre se répare en supprimant le fichier,
/// puisque la seule chose qu'il protège est une console, pas des données.
/// </remarks>
public static class AdminToken
{
    private const int Length = 32;

    public static string LoadOrCreate(string path)
    {
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();

            if (existing.Length >= 32)
                return existing;
        }

        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(Length));
        File.WriteAllText(path, token + Environment.NewLine);

        // « ! » et non « is false » : c'est la forme que l'analyseur de
        // plateforme reconnaît comme une garde.
        if (!OperatingSystem.IsWindows())
        {
            // Le service tourne sur un VPS partagé aussi souvent qu'ailleurs :
            // un jeton lisible par tout le monde vaut un jeton public.
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        Console.WriteLine($"Jeton d'administration engendré dans {path} :");
        Console.WriteLine($"  {token}");

        return token;
    }

    /// <summary>
    /// Vrai si l'en-tête présente ce jeton.
    /// </summary>
    /// <remarks>
    /// Comparaison à temps constant : la console est joignable depuis la boucle
    /// locale, donc par tout processus de la machine, et un test qui s'arrête au
    /// premier caractère faux se devine octet par octet.
    /// </remarks>
    public static bool Matches(string expected, string? authorization)
    {
        if (authorization is null || authorization.StartsWith("Bearer ", StringComparison.Ordinal) is false)
            return false;

        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(authorization["Bearer ".Length..].Trim()),
            System.Text.Encoding.UTF8.GetBytes(expected));
    }
}
