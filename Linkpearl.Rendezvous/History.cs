namespace Linkpearl.Rendezvous;

/// <summary>
/// Vingt-quatre heures de trafic, un point par minute, en mémoire seulement.
/// </summary>
/// <remarks>
/// Un anneau de 1440 minutes et rien sur le disque : un historique écrit
/// serait un fichier qui reste, et ce service promet de ne conserver aucune
/// trace d'usage. Ce qu'il montre disparaît avec le processus, comme tout
/// ce qu'il fait.
///
/// Les compteurs du service sont cumulés depuis le démarrage ; ce qui
/// intéresse une courbe est ce qui s'est passé pendant la minute. Les
/// appariements, les octets relayés et les refus sont donc rangés en écart
/// depuis le relevé précédent, et les boîtes ouvertes, qui sont une jauge,
/// en maximum sur la minute. Chaque case porte sa minute : une case dont la
/// minute ne correspond pas est une minute sans relevé, rendue à zéro plutôt
/// que comme le trafic d'il y a un jour.
/// </remarks>
public sealed class History
{
    public const int Capacity = 24 * 60;

    /// <summary>Une minute de trafic. <see cref="Minute"/> compte en minutes Unix.</summary>
    public readonly record struct Point(long Minute, int OpenMailboxes, long Matches, long RelayedBytes, long Refusals)
    {
        public long At => Minute * 60;
    }

    private readonly Lock _gate = new();
    private readonly Point[] _ring = new Point[Capacity];

    // Les cumuls au dernier relevé, pour en faire des écarts.
    private long _matches;
    private long _relayedBytes;
    private long _refusals;

    /// <summary>Range un relevé dans la minute de <paramref name="now"/>.</summary>
    public void Record(DateTimeOffset now, RendezvousServer.Counters counters)
    {
        var minute = now.ToUnixTimeSeconds() / 60;
        var refusals = counters.RateRefusals + counters.RefusedConnections;

        lock (_gate)
        {
            ref var slot = ref _ring[(int)(minute % Capacity)];

            if (slot.Minute != minute)
                slot = new Point(minute, 0, 0, 0, 0);

            slot = slot with
            {
                OpenMailboxes = Math.Max(slot.OpenMailboxes, counters.OpenMailboxes),
                Matches = slot.Matches + Math.Max(0, counters.Matches - _matches),
                RelayedBytes = slot.RelayedBytes + Math.Max(0, counters.RelayedBytes - _relayedBytes),
                Refusals = slot.Refusals + Math.Max(0, refusals - _refusals),
            };

            _matches = counters.Matches;
            _relayedBytes = counters.RelayedBytes;
            _refusals = refusals;
        }
    }

    /// <summary>
    /// Les <paramref name="minutes"/> dernières minutes, la courante comprise,
    /// de la plus ancienne à la plus récente. Une minute sans relevé vaut zéro.
    /// </summary>
    public IReadOnlyList<Point> Points(DateTimeOffset now, int minutes)
    {
        minutes = Math.Clamp(minutes, 1, Capacity);

        var last = now.ToUnixTimeSeconds() / 60;
        var points = new Point[minutes];

        lock (_gate)
        {
            for (var i = 0; i < minutes; i++)
            {
                var minute = last - minutes + 1 + i;
                var slot = _ring[(int)(minute % Capacity)];

                points[i] = slot.Minute == minute ? slot : new Point(minute, 0, 0, 0, 0);
            }
        }

        return points;
    }
}
