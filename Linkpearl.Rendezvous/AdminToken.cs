using System.Security.Cryptography;
using System.Text;

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

    public static string LoadOrCreate(string path, TextWriter? log = null)
    {
        log ??= Console.Out;

        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();

            if (existing.Length >= 32)
                return existing;
        }

        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(Length));

        // Le service tourne sur un VPS partagé aussi souvent qu'ailleurs : un
        // jeton lisible par tout le monde vaut un jeton public. Le mode est
        // donné à la création même du fichier, et non posé après coup : entre
        // les deux, il aurait existé un instant sous le masque par défaut.
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };

        // « ! » et non « is false » : c'est la forme que l'analyseur de
        // plateforme reconnaît comme une garde.
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        using (var stream = new FileStream(path, options))
        using (var writer = new StreamWriter(stream))
        {
            writer.WriteLine(token);
        }

        if (!OperatingSystem.IsWindows())
        {
            // Un fichier trop court qui existait déjà a gardé son ancien mode :
            // celui-ci le remet d'équerre, et ne coûte rien sinon.
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        // Le chemin, et pas le jeton : un journal est relu par des yeux qui
        // n'ont pas à ouvrir la console, et copié dans des rapports.
        log.WriteLine($"Jeton d'administration engendré dans {path}. Le lire là pour ouvrir la console.");

        return token;
    }

    /// <summary>
    /// Vrai si l'en-tête présente ce jeton, en « Bearer » ou en « Basic ».
    /// </summary>
    /// <remarks>
    /// Les deux formes portent la même chose. « Bearer » sert aux appels en
    /// ligne de commande. « Basic » sert au navigateur : c'est lui qui permet de
    /// protéger la page elle-même, puisque le navigateur demande le mot de passe
    /// avant de l'afficher et le renvoie ensuite tout seul. Un champ dans la
    /// page ne peut pas faire cela : il suppose la page déjà servie.
    ///
    /// L'identifiant n'est pas regardé : il n'y a qu'un secret, et exiger en
    /// plus un nom donnerait l'illusion de deux.
    ///
    /// Comparaison à temps constant : un test qui s'arrête au premier caractère
    /// faux se devine octet par octet.
    /// </remarks>
    public static bool Matches(string expected, string? authorization)
    {
        if (authorization is null)
            return false;

        var presented = Presented(authorization);

        if (presented is null)
            return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(expected));
    }

    private static string? Presented(string authorization)
    {
        if (authorization.StartsWith("Bearer ", StringComparison.Ordinal))
            return authorization["Bearer ".Length..].Trim();

        if (authorization.StartsWith("Basic ", StringComparison.Ordinal) is false)
            return null;

        try
        {
            var pair = Encoding.UTF8.GetString(Convert.FromBase64String(authorization["Basic ".Length..].Trim()));
            var cut = pair.IndexOf(':', StringComparison.Ordinal);

            return cut < 0 ? null : pair[(cut + 1)..];
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
