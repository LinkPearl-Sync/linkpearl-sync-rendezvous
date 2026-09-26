using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Linkpearl.Core.Abstractions;
using Linkpearl.Core.Transport.Rendezvous;

namespace Linkpearl.Rendezvous;

/// <summary>Ce qu'une sonde a vu : joint ou non, et à quelle adresse.</summary>
public sealed record ProbeResult(bool Reached, IPAddress? Address);

public enum ServiceStanding
{
    Candidate,
    Probation,
    Listed,
    Delisted,
    Vetoed,
}

/// <summary>Un service suivi par l'autorité, tel que la console le montre.</summary>
public sealed record TrackedService(
    string Address, string Label, ServiceStanding Standing, int Probes, int Successes,
    DateTimeOffset FirstSeen, DateTimeOffset? LastSuccess, DateTimeOffset? ProbationStart, DateTimeOffset? ListedAt,
    DateTimeOffset? DelistedAt = null, double? Availability24h = null);

/// <summary>
/// Qui est candidat, qui est en probation, qui est listé.
/// </summary>
/// <remarks>
/// L'admission se passe du geste humain : c'est le temps qui juge. Les bornes
/// par famille et par jour n'empêchent pas un acteur patient de peupler une
/// part du cercle, elles bornent sa vitesse et sa concentration. Cela suffit
/// parce qu'un service du cercle ouvert ne voit jamais passer une clé.
///
/// Persisté après chaque ronde : un redémarrage ne doit pas remettre trois
/// jours de probation à zéro.
/// </remarks>
public sealed class AuthorityLedger
{
    public static readonly TimeSpan Probation = TimeSpan.FromHours(72);
    public const double RequiredRatio = 0.95;
    public static readonly TimeSpan DelistAfter = TimeSpan.FromHours(24);
    public static readonly TimeSpan ReturnWindow = TimeSpan.FromHours(72);
    public static readonly TimeSpan ForgetAfter = TimeSpan.FromDays(7);
    public const int MaxPerFamily = 2;
    public const int MaxAdmissionsPerDay = 5;

    /// <summary>Plafond de services suivis, pour qu'une vague de candidatures ne remplisse pas le disque.</summary>
    public const int MaxTracked = 1024;

    private static readonly TimeSpan Day = TimeSpan.FromDays(1);

    /// <summary>Sondes gardées pour la disponibilité publiée : vingt-quatre heures à une toutes les dix minutes.</summary>
    public const int HistorySize = 144;

    private readonly string _path;
    private readonly IClock _clock;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Service> _services = [];
    private readonly List<DateTimeOffset> _admissions = [];
    private uint _version;

    private AuthorityLedger(string path, IClock clock) => (_path, _clock) = (path, clock);

    public static AuthorityLedger Load(string path, IClock clock)
    {
        var ledger = new AuthorityLedger(path, clock);

        if (File.Exists(path))
            ledger.Read(File.ReadAllText(path));

        return ledger;
    }

    /// <summary>Suit une candidature, liée à l'adresse (ou au /64) qui l'a soumise.</summary>
    /// <remarks>
    /// Un service n'entre que s'il répond depuis cette adresse-là : sans ce
    /// lien, n'importe qui inscrirait au cercle ouvert le service d'un autre,
    /// ou l'alias d'un service d'ancrage, sans l'accord de son opérateur.
    /// </remarks>
    public bool Track(DirectoryEntry entry, string submitter)
    {
        if (KeyOf(entry.Address) is not { } key)
            return false;

        lock (_gate)
        {
            if (_services.ContainsKey(key) || _services.Count >= MaxTracked)
                return false;

            _services[key] = new Service { Address = key, Label = entry.Label, Submitter = submitter, FirstSeen = _clock.UtcNow };
            return true;
        }
    }

    public void Record(string address, ProbeResult result)
    {
        if (KeyOf(address) is not { } key)
            return;

        lock (_gate)
        {
            if (_services.TryGetValue(key, out var service) is false || service.Vetoed)
                return;

            var now = _clock.UtcNow;

            // Une réponse venue d'ailleurs que de l'adresse candidate ne compte
            // pas : ce n'est pas ce service-là qui s'est porté candidat.
            var reached = result is { Reached: true, Address: { } answered }
                && AddressBucket.Of(answered) == service.Submitter;

            if (reached && result.Address is { } reachedAt)
            {
                var previous = service.LastSuccess;
                service.LastSuccess = now;
                service.Family = ServiceConsensus.Family(reachedAt);

                // Un long silence en probation ne se rattrape pas au ratio : la
                // fenêtre grandirait sans fin. La probation recommence au retour.
                var interrupted = service.ProbationStart is not null
                    && previous is { } last && now - last >= DelistAfter;

                var fresh = service.ListedAt is null && service.ProbationStart is null && service.DelistedAt is null;

                if (interrupted || fresh)
                    StartProbation(service, now);
            }

            // Gardé pour tout service suivi, et pas seulement en probation : la
            // page publique montre la disponibilité récente des services listés.
            service.History.Add(reached);

            if (service.History.Count > HistorySize)
                service.History.RemoveAt(0);

            if (service.ProbationStart is not null)
            {
                service.Probes++;

                if (reached)
                    service.Successes++;
            }
        }
    }

    /// <summary>Sorties, oublis, retours et admissions, après une ronde de sondes.</summary>
    public void Settle()
    {
        lock (_gate)
        {
            var now = _clock.UtcNow;

            foreach (var service in _services.Values)
            {
                if (service.ListedAt is { } listed && now - (service.LastSuccess ?? listed) >= DelistAfter)
                {
                    service.ListedAt = null;
                    service.DelistedAt = now;
                }
            }

            foreach (var forgotten in _services.Values
                         .Where(service => service.Vetoed is false && service.ListedAt is null
                             && now - (service.LastSuccess ?? service.FirstSeen) >= ForgetAfter)
                         .Select(service => service.Address).ToList())
                _services.Remove(forgotten);

            foreach (var service in _services.Values)
            {
                if (service.Vetoed || service.ListedAt is not null || service.ProbationStart is not null
                    || service.DelistedAt is not { } gone || service.LastSuccess is not { } seen || seen <= gone)
                    continue;

                if (now - gone > ReturnWindow)
                    StartProbation(service, now);
                else if (FamilyCount(service.Family) < MaxPerFamily)
                {
                    service.ListedAt = now;
                    service.DelistedAt = null;
                }
            }

            _admissions.RemoveAll(admitted => now - admitted >= Day);

            foreach (var service in _services.Values
                         .Where(service => service.ProbationStart is not null && service.Vetoed is false)
                         .OrderBy(service => service.ProbationStart)
                         .ToList())
            {
                if (_admissions.Count >= MaxAdmissionsPerDay)
                    break;

                if (now - service.ProbationStart!.Value < Probation || service.Probes == 0
                    || (double)service.Successes / service.Probes < RequiredRatio
                    || FamilyCount(service.Family) >= MaxPerFamily)
                    continue;

                service.ListedAt = now;
                service.ProbationStart = null;
                _admissions.Add(now);
            }

            SaveLocked();
        }
    }

    public bool Veto(string address)
    {
        if (KeyOf(address) is not { } key)
            return false;

        lock (_gate)
        {
            if (_services.TryGetValue(key, out var service) is false)
                return false;

            service.Vetoed = true;
            service.ListedAt = null;
            service.ProbationStart = null;
            SaveLocked();
            return true;
        }
    }

    public bool Lift(string address)
    {
        if (KeyOf(address) is not { } key)
            return false;

        lock (_gate)
        {
            if (_services.TryGetValue(key, out var service) is false || service.Vetoed is false)
                return false;

            // Rétabli, il repart de zéro : l'opérateur l'avait écarté pour une
            // raison, la probation doit se refaire sous ses yeux.
            _services[key] = new Service
            {
                Address = key, Label = service.Label, Submitter = service.Submitter, FirstSeen = _clock.UtcNow,
            };
            SaveLocked();
            return true;
        }
    }

    public IReadOnlyList<ConsensusEntry> Listed()
    {
        lock (_gate)
            return _services.Values
                .Where(service => service.ListedAt is not null)
                .OrderBy(service => service.Address, StringComparer.Ordinal)
                .Select(service => new ConsensusEntry(service.Address, service.Label, service.Family!))
                .ToList();
    }

    public IReadOnlyList<TrackedService> Snapshot()
    {
        lock (_gate)
            return _services.Values
                .OrderBy(service => service.Address, StringComparer.Ordinal)
                .Select(service => new TrackedService(
                    service.Address, service.Label, StandingOf(service), service.Probes, service.Successes,
                    service.FirstSeen, service.LastSuccess, service.ProbationStart, service.ListedAt,
                    service.DelistedAt,
                    service.History.Count is 0 ? null : (double)service.History.Count(ok => ok) / service.History.Count))
                .ToList();
    }

    public uint NextVersion()
    {
        lock (_gate)
        {
            // Jamais en dessous de l'heure : un registre retiré ou perdu ferait
            // sinon repartir la version à 1, et chaque client refuserait les
            // listes suivantes tant que la sienne vaut encore. Les secondes
            // Unix tiennent dans un uint jusqu'en 2106.
            _version = Math.Max(_version + 1, (uint)_clock.UtcNow.ToUnixTimeSeconds());
            SaveLocked();
            return _version;
        }
    }

    private static ServiceStanding StandingOf(Service service) => service switch
    {
        { Vetoed: true } => ServiceStanding.Vetoed,
        { ListedAt: not null } => ServiceStanding.Listed,
        { ProbationStart: not null } => ServiceStanding.Probation,
        { DelistedAt: not null } => ServiceStanding.Delisted,
        _ => ServiceStanding.Candidate,
    };

    private static void StartProbation(Service service, DateTimeOffset now)
    {
        service.ProbationStart = now;
        service.Probes = 0;
        service.Successes = 0;
        service.DelistedAt = null;
    }

    private int FamilyCount(byte[]? family)
        => family is null ? 0 : _services.Values.Count(
            service => service.ListedAt is not null && service.Family is { } other && other.AsSpan().SequenceEqual(family));

    private static string? KeyOf(string address)
        => RendezvousAddress.TryParse(address, out var parsed, out _) ? ServiceConsensus.Canonical(parsed) : null;

    private void SaveLocked()
    {
        var services = new JsonArray();

        foreach (var service in _services.Values)
            services.Add(new JsonObject
            {
                ["address"] = service.Address,
                ["label"] = service.Label,
                ["submitter"] = service.Submitter,
                ["firstSeen"] = service.FirstSeen.ToUnixTimeSeconds(),
                ["lastSuccess"] = Seconds(service.LastSuccess),
                ["probationStart"] = Seconds(service.ProbationStart),
                ["listedAt"] = Seconds(service.ListedAt),
                ["delistedAt"] = Seconds(service.DelistedAt),
                ["probes"] = service.Probes,
                ["successes"] = service.Successes,
                ["family"] = service.Family is null ? null : Convert.ToHexStringLower(service.Family),
                ["vetoed"] = service.Vetoed,
                ["history"] = string.Concat(service.History.Select(ok => ok ? '1' : '0')),
            });

        var admissions = new JsonArray();

        foreach (var admitted in _admissions)
            admissions.Add(admitted.ToUnixTimeSeconds());

        var root = new JsonObject { ["version"] = _version, ["admissions"] = admissions, ["services"] = services };
        AtomicFile.WriteAllText(_path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static JsonNode? Seconds(DateTimeOffset? at) => at is { } value ? JsonValue.Create(value.ToUnixTimeSeconds()) : null;

    private static DateTimeOffset? Time(JsonNode? node)
        => node is null ? null : DateTimeOffset.FromUnixTimeSeconds(node.GetValue<long>());

    private static JsonNode Need(JsonObject parent, string name)
        => parent[name] ?? throw new FormatException($"champ « {name} » absent");

    private void Read(string text)
    {
        try
        {
            var root = (JsonNode.Parse(text) ?? throw new FormatException("document vide")).AsObject();
            _version = Need(root, "version").GetValue<uint>();

            foreach (var admitted in Need(root, "admissions").AsArray())
                _admissions.Add(DateTimeOffset.FromUnixTimeSeconds((admitted ?? throw new FormatException("admission nulle")).GetValue<long>()));

            foreach (var node in Need(root, "services").AsArray())
            {
                var item = (node ?? throw new FormatException("service nul")).AsObject();
                var service = new Service
                {
                    Address = Need(item, "address").GetValue<string>(),
                    Label = Need(item, "label").GetValue<string>(),
                    Submitter = Need(item, "submitter").GetValue<string>(),
                    FirstSeen = DateTimeOffset.FromUnixTimeSeconds(Need(item, "firstSeen").GetValue<long>()),
                    LastSuccess = Time(item["lastSuccess"]),
                    ProbationStart = Time(item["probationStart"]),
                    ListedAt = Time(item["listedAt"]),
                    DelistedAt = Time(item["delistedAt"]),
                    Probes = Need(item, "probes").GetValue<int>(),
                    Successes = Need(item, "successes").GetValue<int>(),
                    Family = item["family"] is { } family ? Convert.FromHexString(family.GetValue<string>()) : null,
                    Vetoed = Need(item, "vetoed").GetValue<bool>(),
                };

                // Absent d'un registre d'avant la page publique : l'historique
                // repart alors de zéro, sans que rien d'autre ne change.
                if (item["history"] is { } history)
                    service.History.AddRange(history.GetValue<string>().TakeLast(HistorySize).Select(bit => bit == '1'));

                _services[service.Address] = service;
            }
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException)
        {
            // Jamais réécrit : il porte des jours de probation, et le remettre
            // à zéro en silence ferait attendre trois jours de plus à tout le
            // cercle sans que personne sache pourquoi.
            throw new InvalidOperationException(
                $"{_path} illisible : le corriger ou le retirer à la main avant de redémarrer.", e);
        }
    }

    private sealed class Service
    {
        public required string Address { get; init; }
        public required string Label { get; init; }
        public required string Submitter { get; init; }
        public required DateTimeOffset FirstSeen { get; init; }
        public DateTimeOffset? LastSuccess { get; set; }
        public DateTimeOffset? ProbationStart { get; set; }
        public DateTimeOffset? ListedAt { get; set; }
        public DateTimeOffset? DelistedAt { get; set; }
        public int Probes { get; set; }
        public int Successes { get; set; }
        public byte[]? Family { get; set; }
        public bool Vetoed { get; set; }
        public List<bool> History { get; } = [];
    }
}
