using System.Collections.Concurrent;

namespace Linkpearl.Rendezvous;

/// <summary>
/// Les places de connexion : au total, par seau d'adresse et par préfixe large.
/// </summary>
/// <remarks>
/// À part du service pour s'éprouver sans socket : un test ne peut pas se
/// connecter depuis deux /48 différents, et c'est pourtant la règle qui
/// compte le plus contre qui loue un bloc d'adresses.
/// </remarks>
public sealed class ConnectionSlots
{
    private readonly ConcurrentDictionary<string, int> _held = new(StringComparer.Ordinal);
    private int _total;

    public int Total => Volatile.Read(ref _total);

    /// <summary>Prend une place, ou rend faux sans rien garder si un plafond est atteint.</summary>
    /// <param name="bucket">Le seau ordinaire : l'adresse IPv4, ou le /64.</param>
    /// <param name="wide">Le /48 en IPv6, nul en IPv4.</param>
    public bool TryReserve(string bucket, string? wide, RendezvousLimits limits)
    {
        if (Interlocked.Increment(ref _total) > limits.MaxConnections)
        {
            Interlocked.Decrement(ref _total);
            return false;
        }

        if (_held.AddOrUpdate(bucket, 1, (_, held) => held + 1) > limits.MaxConnectionsPerAddress)
        {
            Release(bucket, wide: null);
            return false;
        }

        // Préfixé : un /48 et un /64 qui s'écriraient pareil ne doivent pas
        // partager un compteur.
        if (wide is not null && _held.AddOrUpdate(Key(wide), 1, (_, held) => held + 1) > limits.MaxConnectionsPerPrefix)
        {
            Release(bucket, wide);
            return false;
        }

        return true;
    }

    public void Release(string bucket, string? wide)
    {
        Interlocked.Decrement(ref _total);
        Drop(bucket);

        if (wide is not null)
            Drop(Key(wide));
    }

    /// <summary>Les places tenues sous une clé, pour les tests et la console.</summary>
    public int Held(string bucket) => _held.GetValueOrDefault(bucket);

    private static string Key(string wide) => $"large:{wide}";

    private void Drop(string key)
    {
        // L'entrée disparaît à zéro : sans cela, le dictionnaire garderait une
        // trace de chaque adresse jamais vue.
        while (_held.TryGetValue(key, out var held))
        {
            if (held <= 1
                ? _held.TryRemove(new KeyValuePair<string, int>(key, held))
                : _held.TryUpdate(key, held - 1, held))
                return;
        }
    }
}
