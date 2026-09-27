using System.Net;
using Linkpearl.Core.Abstractions;
using MaxMind.Db;

namespace Linkpearl.Rendezvous;

/// <summary>
/// Le continent d'une adresse, lu dans la base DB-IP « IP to Country Lite ».
/// </summary>
/// <remarks>
/// Seule l'autorité s'en sert, et seulement pour les adresses de services,
/// déjà publiques : aucun client n'y passe. Une base absente ou trop vieille
/// ne rend aucune région plutôt qu'une région fausse, et les services
/// retombent dans le tirage global, comme avant les régions.
/// </remarks>
public sealed class GeoIpRegions(string path, IClock clock) : IRegionLookup, IDisposable
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(90);

    private static readonly HashSet<string> Continents = new(StringComparer.Ordinal) { "AF", "AS", "EU", "NA", "OC", "SA" };

    private readonly Lock _gate = new();
    private Reader? _reader = Open(path);

    public DateTimeOffset? BuildDate
    {
        get
        {
            lock (_gate)
                return _reader is { } reader ? new DateTimeOffset(reader.Metadata.BuildDate, TimeSpan.Zero) : null;
        }
    }

    public void Reload()
    {
        var fresh = Open(path);

        lock (_gate)
        {
            _reader?.Dispose();
            _reader = fresh;
        }
    }

    public string? RegionOf(IPAddress address)
    {
        lock (_gate)
        {
            if (_reader is not { } reader || clock.UtcNow - new DateTimeOffset(reader.Metadata.BuildDate, TimeSpan.Zero) > MaxAge)
                return null;

            var data = reader.Find<Dictionary<string, object>>(address);

            return data?.GetValueOrDefault("continent") is Dictionary<string, object> continent
                ? Retained(continent.GetValueOrDefault("code") as string)
                : null;
        }
    }

    /// <summary>Le code tel quel s'il désigne un continent habité, sinon null.</summary>
    /// <remarks>L'Antarctique n'a pas de joueurs : un service qui s'y dirait n'aurait pas de région.</remarks>
    public static string? Retained(string? code) => code is not null && Continents.Contains(code) ? code : null;

    public void Dispose()
    {
        lock (_gate)
            _reader?.Dispose();
    }

    private static Reader? Open(string path)
    {
        if (File.Exists(path) is false)
            return null;

        try
        {
            return new Reader(path);
        }
        catch (InvalidDatabaseException)
        {
            // Une base illisible vaut une base absente : pas de région, et le
            // prochain téléchargement la remplace.
            return null;
        }
    }
}
