using Linkpearl.Core.Abstractions;
using Linkpearl.Core.Transport.Rendezvous;

namespace Linkpearl.Rendezvous;

/// <summary>
/// Les services que celui-ci connaît, et les candidatures en attente.
/// </summary>
/// <remarks>
/// L'annuaire publie, il n'interroge personne et ne propage rien. Sa liste
/// vient d'un fichier écrit par l'opérateur, une ligne par service :
///
///     rdv.ami.ch        Chez l'amie
///     rdv.autre.ch:443  Ailleurs
///
/// La première espace sépare l'adresse du libellé, donc une adresse n'en
/// contient jamais. Une adresse fautive qui en porterait une se lirait comme
/// une adresse plus courte suivie d'un libellé, et rien ne permet de les
/// distinguer : c'est le prix d'un format qu'un opérateur écrit à la main.
///
/// Le fichier est relu à chaud, sur son horodatage : ajouter un service doit
/// être une ligne écrite et pas un redémarrage, sans quoi personne ne le fera.
///
/// Une candidature n'entre jamais dans cette liste. Elle tombe dans une file
/// que l'opérateur lit et approuve en déplaçant la ligne. C'est ce qui empêche
/// l'annuaire de devenir une autorité par accumulation.
/// </remarks>
public sealed class PeerDirectory(string peersPath, string pendingPath, IClock? clock = null)
{
    private readonly IClock _clock = clock ?? new SystemClock();

    /// <summary>Une candidature par adresse source et par heure.</summary>
    /// <remarks>
    /// Sans ce frein, la file se remplit toute seule et l'opérateur cesse de la
    /// lire, ce qui revient à ne pas l'avoir.
    /// </remarks>
    private static readonly TimeSpan SubmitInterval = TimeSpan.FromHours(1);

    private const int MaxPending = 256;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _lastSubmit = [];

    /// <summary>Les sources de candidatures encore freinées.</summary>
    public int TrackedSubmitters
    {
        get
        {
            lock (_gate)
                return _lastSubmit.Count;
        }
    }

    private List<DirectoryEntry> _known = [];
    private DateTime _knownStamp = DateTime.MinValue;

    public IReadOnlyList<DirectoryEntry> Known()
    {
        lock (_gate)
            return KnownLocked();
    }

    public IReadOnlyList<DirectoryEntry> Pending() => Read(pendingPath);

    /// <summary>Enregistre une candidature. Rend faux quand elle est écartée.</summary>
    public bool Submit(string sourceAddress, DirectoryEntry entry)
    {
        if (RendezvousAddress.TryParse(entry.Address, out _, out _) is false)
            return false;

        lock (_gate)
        {
            var now = _clock.UtcNow;

            // Une source dont le frein est levé n'a plus à être retenue : sans
            // cette purge, chaque adresse jamais vue resterait ici pour toujours.
            foreach (var (source, when) in _lastSubmit.ToList())
            {
                if (now - when >= SubmitInterval)
                    _lastSubmit.Remove(source);
            }

            if (_lastSubmit.ContainsKey(sourceAddress))
                return false;

            var pending = Read(pendingPath);

            if (KnownLocked().Any(known => known.Address == entry.Address)
                || pending.Any(waiting => waiting.Address == entry.Address))
                return false;

            // Les plus anciennes cèdent la place : une file pleine ne doit pas
            // fermer la porte à qui arrive aujourd'hui.
            if (pending.Count >= MaxPending)
                pending.RemoveAt(0);

            pending.Add(entry);
            Write(pendingPath, pending);

            _lastSubmit[sourceAddress] = now;
            return true;
        }
    }

    /// <summary>Approuve une candidature : elle passe de la file aux pairs.</summary>
    /// <remarks>
    /// L'ordre compte : on écrit d'abord dans les pairs, puis on retire de la
    /// file. Une coupure entre les deux laisse une ligne en double, que
    /// l'opérateur voit et corrige ; l'ordre inverse la perdrait sans trace.
    /// </remarks>
    public bool Approve(string address)
    {
        lock (_gate)
        {
            var pending = Read(pendingPath);
            var found = pending.Find(entry => entry.Address == address);

            if (found is null)
                return false;

            var known = KnownLocked().ToList();

            if (known.Exists(entry => entry.Address == address) is false)
            {
                known.Add(found);
                WriteKnownLocked(known);
            }

            pending.RemoveAll(entry => entry.Address == address);
            Write(pendingPath, pending);
            return true;
        }
    }

    /// <summary>Écarte une candidature sans l'approuver.</summary>
    public bool Reject(string address)
    {
        lock (_gate)
        {
            var pending = Read(pendingPath);

            if (pending.RemoveAll(entry => entry.Address == address) is 0)
                return false;

            Write(pendingPath, pending);
            return true;
        }
    }

    /// <summary>Retire un service de la liste des connus.</summary>
    public bool Forget(string address)
    {
        lock (_gate)
        {
            var known = KnownLocked().ToList();

            if (known.RemoveAll(entry => entry.Address == address) is 0)
                return false;

            WriteKnownLocked(known);
            return true;
        }
    }

    /// <summary>Écrit les pairs connus, et remet le cache d'accord avec eux.</summary>
    /// <remarks>
    /// Le cache se relit sur l'horodatage du fichier. Sans le remettre ici, une
    /// écriture suivie d'une lecture dans la même graduation d'horloge
    /// montrerait encore l'état d'avant.
    /// </remarks>
    private void WriteKnownLocked(List<DirectoryEntry> entries)
    {
        Write(peersPath, entries);
        _known = entries;
        _knownStamp = File.GetLastWriteTimeUtc(peersPath);
    }

    private List<DirectoryEntry> KnownLocked()
    {
        var stamp = File.Exists(peersPath) ? File.GetLastWriteTimeUtc(peersPath) : DateTime.MinValue;

        if (stamp != _knownStamp)
        {
            _known = Read(peersPath);
            _knownStamp = stamp;
        }

        return _known;
    }

    private static List<DirectoryEntry> Read(string path)
    {
        var entries = new List<DirectoryEntry>();

        if (File.Exists(path) is false)
            return entries;

        foreach (var line in File.ReadAllLines(path))
        {
            var trimmed = line.Trim();

            if (trimmed.Length is 0 || trimmed[0] is '#')
                continue;

            var cut = trimmed.IndexOfAny([' ', '\t']);
            var address = cut < 0 ? trimmed : trimmed[..cut];
            var label = cut < 0 ? "" : trimmed[(cut + 1)..].Trim();

            // Une ligne illisible est ignorée, les autres restent : un fichier
            // à demi valide vaut mieux qu'un service muet.
            if (RendezvousAddress.TryParse(address, out _, out _) is false)
                continue;

            if (label.Length > RendezvousWire.MaxDirectoryLabelLength)
                label = label[..RendezvousWire.MaxDirectoryLabelLength];

            entries.Add(new DirectoryEntry(address, label));

            if (entries.Count >= RendezvousWire.MaxDirectoryEntries)
                break;
        }

        return entries;
    }

    private static void Write(string path, IReadOnlyList<DirectoryEntry> entries)
        => File.WriteAllLines(path, entries.Select(entry => $"{entry.Address}  {entry.Label}"));
}
