using System.Security.Cryptography;
using Linkpearl.Core.Safety;

namespace Linkpearl.Rendezvous;

/// <summary>
/// La liste de bannissement du service, sur disque.
/// </summary>
/// <remarks>
/// Le service ne conserve aucun nom de personnage : il dérive l'empreinte au
/// moment où l'opérateur ajoute l'entrée, et n'en garde que le résultat. Se
/// tromper d'entrée se répare donc en la retirant et en la ressaisissant, pas
/// en la relisant.
///
/// Le sel est engendré une fois et ne change jamais. En changer invaliderait
/// toute la liste, qu'il faudrait reconstruire depuis des noms que le service
/// n'a pas gardés.
/// </remarks>
public sealed class BanStore(string path)
{
    private const int SaltLength = 32;

    private readonly Lock _gate = new();

    private BanList? _list;
    private DateTime _stamp = DateTime.MinValue;

    /// <summary>La liste courante, relue si le fichier a changé sous nos pieds.</summary>
    public BanList Current()
    {
        lock (_gate)
            return LoadLocked();
    }

    /// <summary>Ce que les clients téléchargent.</summary>
    public string Json()
    {
        lock (_gate)
            return LoadLocked().ToJson(UpdatedLocked());
    }

    /// <summary>
    /// Ajoute un personnage. Rend son empreinte, ou null s'il y figurait déjà.
    /// </summary>
    public string? Add(string name, ushort world, string reason)
    {
        lock (_gate)
        {
            var list = LoadLocked();
            var hash = BanList.Derive(name, world, list.Salt, list.Parameters);

            if (list.Entries.Any(entry => CryptographicOperations.FixedTimeEquals(entry.Hash, hash)))
                return null;

            var entries = list.Entries.ToList();
            entries.Add(new BanEntry(hash, Sanitize(reason), DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

            SaveLocked(new BanList(list.Salt, list.Parameters, entries));
            return Convert.ToHexStringLower(hash);
        }
    }

    /// <summary>Retire une entrée par son empreinte.</summary>
    public bool Remove(string hashHex)
    {
        lock (_gate)
        {
            var list = LoadLocked();
            var kept = list.Entries
                .Where(entry => Convert.ToHexStringLower(entry.Hash).Equals(hashHex, StringComparison.OrdinalIgnoreCase) is false)
                .ToList();

            if (kept.Count == list.Entries.Count)
                return false;

            SaveLocked(new BanList(list.Salt, list.Parameters, kept));
            return true;
        }
    }

    /// <summary>
    /// Nettoie un motif avant qu'il ne parte vers des clients.
    /// </summary>
    /// <remarks>
    /// Il s'affichera chez des tiers, dans une interface de jeu qui ne rend pas
    /// tous les glyphes : on garde l'ASCII lisible et on jette le reste, plutôt
    /// que de laisser un opérateur coller une ligne qui s'affichera en carrés.
    /// </remarks>
    private static string Sanitize(string reason)
    {
        var kept = new string([.. reason.Where(c => c is >= ' ' and <= '~')]).Trim();

        if (kept.Length > BanList.MaxReasonLength)
            kept = kept[..BanList.MaxReasonLength];

        return kept.Length is 0 ? "sans motif" : kept;
    }

    private long UpdatedLocked()
        => File.Exists(path) ? new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeSeconds() : 0;

    private BanList LoadLocked()
    {
        var stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;

        if (_list is not null && stamp == _stamp)
            return _list;

        if (File.Exists(path))
        {
            if (BanList.TryParse(File.ReadAllText(path), out var parsed, out var why) && parsed is not null)
            {
                _list = parsed;
                _stamp = stamp;
                return _list;
            }

            // Une liste illisible n'est pas réécrite : l'opérateur doit pouvoir
            // la réparer. On sert une liste vide en attendant, ce qui ne bannit
            // personne à tort.
            Console.WriteLine($"Liste de bannissement illisible ({why}), liste vide servie en attendant.");

            _list = new BanList(RandomNumberGenerator.GetBytes(SaltLength), BanParameters.Default, []);
            _stamp = stamp;
            return _list;
        }

        // Premier démarrage : le sel naît ici, et il est écrit tout de suite.
        // Le garder en mémoire jusqu'à la première entrée le ferait changer à
        // chaque redémarrage, et les empreintes déjà publiées ne vaudraient plus rien.
        var fresh = new BanList(RandomNumberGenerator.GetBytes(SaltLength), BanParameters.Default, []);
        SaveLocked(fresh);
        return fresh;
    }

    private void SaveLocked(BanList list)
    {
        File.WriteAllText(path, list.ToJson(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        _list = list;
        _stamp = File.GetLastWriteTimeUtc(path);
    }
}
