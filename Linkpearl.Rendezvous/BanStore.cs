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

    /// <summary>
    /// Dérive l'empreinte d'un personnage et dit si elle est listée, sans rien garder.
    /// </summary>
    /// <remarks>
    /// Pour qu'un opérateur réponde « est-il banni chez moi ? » sans risquer
    /// d'ajouter par mégarde, et sans que le nom passe par le journal ni le
    /// disque : ce qui entre ici en ressort en empreinte, rien d'autre.
    /// </remarks>
    public (string Hash, bool Listed) Check(string name, ushort world)
    {
        lock (_gate)
        {
            var list = LoadLocked();
            var hash = BanList.Derive(name, world, list.Salt, list.Parameters);

            return (Convert.ToHexStringLower(hash), list.Entries.Any(entry => CryptographicOperations.FixedTimeEquals(entry.Hash, hash)));
        }
    }

    /// <summary>
    /// Fusionne une liste publiée par un autre service dans celle-ci.
    /// </summary>
    /// <remarks>
    /// Seulement sous le même sel et le même coût de dérivation : deux listes
    /// sous des sels différents portent des empreintes qui ne se comparent
    /// pas, et les mêler ferait une liste où la moitié des entrées ne bannit
    /// personne. Les entrées déjà présentes gardent leur motif et leur date
    /// d'ici ; les nouvelles arrivent avec les leurs. Le fichier est réécrit
    /// d'un bloc, ou pas du tout.
    /// </remarks>
    public ImportOutcome Import(string json)
    {
        if (BanList.TryParse(json, out var imported, out var why) is false || imported is null)
            return ImportOutcome.Unreadable(why ?? "liste illisible");

        lock (_gate)
        {
            var list = LoadLocked();

            if (CryptographicOperations.FixedTimeEquals(list.Salt, imported.Salt) is false || list.Parameters != imported.Parameters)
                return ImportOutcome.Foreign();

            var entries = list.Entries.ToList();
            var added = 0;

            foreach (var entry in imported.Entries)
            {
                if (entries.Any(known => CryptographicOperations.FixedTimeEquals(known.Hash, entry.Hash)))
                    continue;

                entries.Add(new BanEntry(entry.Hash, Sanitize(entry.Reason), entry.Since));
                added++;
            }

            if (entries.Count > BanList.MaxEntries)
                return ImportOutcome.Unreadable($"la fusion dépasserait le plafond de {BanList.MaxEntries} entrées");

            if (added > 0)
                SaveLocked(new BanList(list.Salt, list.Parameters, entries));

            return ImportOutcome.Merged(added, entries.Count);
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
        AtomicFile.WriteAllText(path, list.ToJson(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        _list = list;
        _stamp = File.GetLastWriteTimeUtc(path);
    }
}

/// <summary>Ce qu'un import a fait, ou pourquoi il n'a rien fait.</summary>
/// <remarks>
/// Un sel étranger n'est pas une liste illisible : la première se refuse
/// avec un conflit que l'opérateur comprend (« pas la même liste »), la
/// seconde avec une erreur de format. Les deux ne se corrigent pas pareil.
/// </remarks>
public sealed record ImportOutcome(int Added, int Total, bool ForeignSalt, string? Rejection)
{
    public bool Ok => Rejection is null && ForeignSalt is false;

    public static ImportOutcome Merged(int added, int total) => new(added, total, false, null);

    public static ImportOutcome Foreign() => new(0, 0, true, null);

    public static ImportOutcome Unreadable(string why) => new(0, 0, false, why);
}
