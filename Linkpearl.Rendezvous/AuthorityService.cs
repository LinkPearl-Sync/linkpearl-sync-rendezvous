using System.Security.Cryptography;
using Linkpearl.Core.Abstractions;
using Linkpearl.Core.Transport.Rendezvous;

namespace Linkpearl.Rendezvous;

/// <summary>Ce qui détient la liste signée à servir, s'il y en a une.</summary>
public interface IConsensusSource
{
    byte[]? Document { get; }
}

/// <summary>
/// Le rôle d'autorité : sonder, juger, signer.
/// </summary>
/// <remarks>
/// Une autorité ne fait foi que sur le cercle ouvert, qui ne voit jamais passer
/// une clé : le pire qu'elle puisse y admettre est un service qui refuse ou
/// observe des métadonnées. Le cercle d'ancrage, où passent les pairages,
/// reste composé à la main par chaque utilisateur.
/// </remarks>
public sealed class AuthorityService(
    AuthorityLedger ledger, IServiceProbe probe, ECDsa key, IClock clock) : IConsensusSource
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    /// <summary>Même inchangée, la liste est resignée chaque jour, pour ne jamais approcher son expiration.</summary>
    public static readonly TimeSpan Reissue = TimeSpan.FromHours(24);

    private const int Parallelism = 16;

    private readonly Lock _gate = new();
    private ServiceConsensus? _current;
    private byte[]? _document;

    public TextWriter Log { get; init; } = Console.Out;

    public AuthorityLedger Ledger => ledger;

    public byte[] PublicPoint { get; } = ServiceConsensus.PublicPoint(key);

    public byte[]? Document
    {
        get
        {
            lock (_gate)
                return _document;
        }
    }

    public ServiceConsensus? Current
    {
        get
        {
            lock (_gate)
                return _current;
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        IssueIfNeeded();

        while (ct.IsCancellationRequested is false)
        {
            try
            {
                await RoundAsync(ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Une ronde ratée n'arrête pas l'autorité : la liste en place
                // reste servie, et la suivante réessaie.
                Log.WriteLine($"Ronde de sondes en échec ({e.GetType().Name}) : {e.Message}");
            }

            try
            {
                await Task.Delay(Interval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async Task RoundAsync(CancellationToken ct)
    {
        var targets = ledger.Snapshot()
            .Where(service => service.Standing is not ServiceStanding.Vetoed)
            .Select(service => service.Address)
            .ToList();

        await Parallel.ForEachAsync(
            targets,
            new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct },
            async (address, token) =>
            {
                if (RendezvousAddress.TryParse(address, out var at, out _))
                    ledger.Record(address, await probe.ProbeAsync(at, token).ConfigureAwait(false));
            }).ConfigureAwait(false);

        ledger.Settle();
        IssueIfNeeded();
    }

    public void IssueIfNeeded()
    {
        var listed = ledger.Listed();
        var now = clock.UtcNow.ToUnixTimeSeconds();

        lock (_gate)
        {
            if (_current is { } current && Same(current.Entries, listed) && now - current.Issued < (long)Reissue.TotalSeconds)
                return;

            var list = new ServiceConsensus(
                ledger.NextVersion(), now, now + (long)ServiceConsensus.Lifetime.TotalSeconds, listed);

            _document = ServiceConsensus.Sign(list, key);
            _current = list;
            Log.WriteLine($"Liste signée émise : version {list.Version}, {listed.Count} service(s).");
        }
    }

    private static bool Same(IReadOnlyList<ConsensusEntry> one, IReadOnlyList<ConsensusEntry> other)
        => one.Count == other.Count && one.Zip(other).All(pair =>
            pair.First.Address == pair.Second.Address
            && pair.First.Label == pair.Second.Label
            && pair.First.Family.AsSpan().SequenceEqual(pair.Second.Family));
}
