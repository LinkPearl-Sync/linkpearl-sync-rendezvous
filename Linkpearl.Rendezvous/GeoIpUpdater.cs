using System.IO.Compression;
using System.Net;
using Linkpearl.Core.Abstractions;
using MaxMind.Db;

namespace Linkpearl.Rendezvous;

/// <summary>
/// Retélécharge la base GeoIP quand elle a plus d'un mois.
/// </summary>
/// <remarks>
/// DB-IP publie une base par mois, sans compte ni clé. Le fichier du mois
/// n'existe pas encore les premiers jours : on retente alors le précédent.
/// Un fichier reçu n'écrase la base en place qu'une fois relu avec succès.
/// </remarks>
public sealed class GeoIpUpdater(GeoIpRegions regions, string path, HttpClient http, IClock clock)
{
    public static readonly TimeSpan RefreshAfter = TimeSpan.FromDays(32);
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    private const long MaxBytes = 200L * 1024 * 1024;

    public TextWriter Log { get; init; } = Console.Out;

    public static Uri SourceFor(DateTimeOffset month)
        => new($"https://download.db-ip.com/free/dbip-country-lite-{month:yyyy-MM}.mmdb.gz");

    public async Task RunAsync(CancellationToken ct)
    {
        while (ct.IsCancellationRequested is false)
        {
            try
            {
                await RefreshIfNeededAsync(ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Une mise à jour ratée n'arrête pas le service : la base en
                // place reste servie, et la prochaine ronde réessaie.
                Log.WriteLine($"Base GeoIP non renouvelée ({e.GetType().Name}) : {e.Message}");
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

    public async Task<bool> RefreshIfNeededAsync(CancellationToken ct)
    {
        if (regions.BuildDate is { } built && clock.UtcNow - built < RefreshAfter)
            return false;

        foreach (var month in new[] { clock.UtcNow, clock.UtcNow.AddMonths(-1) })
        {
            using var response = await http.GetAsync(SourceFor(month), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.NotFound)
                continue;

            response.EnsureSuccessStatusCode();

            var temporary = path + ".tmp";

            try
            {
                await using (var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                await using (var gzip = new GZipStream(body, CompressionMode.Decompress))
                await using (var file = File.Create(temporary))
                {
                    var buffer = new byte[81920];
                    long total = 0;
                    int read;

                    while ((read = await gzip.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        total += read;

                        if (total > MaxBytes)
                            throw new InvalidDataException($"base GeoIP de plus de {MaxBytes} octets");

                        await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    }
                }

                if (Readable(temporary) is false)
                    throw new InvalidDataException("base GeoIP reçue illisible");
            }
            catch (InvalidDataException e)
            {
                // Un fichier tronqué, corrompu ou démesuré ne remplace jamais
                // une base qui fonctionne : on la garde jusqu'au prochain essai.
                File.Delete(temporary);
                Log.WriteLine($"{e.Message}, la base en place est gardée.");
                return false;
            }
            catch
            {
                // Toute autre panne (réseau coupé, disque plein) ne doit pas
                // laisser un fichier à moitié écrit derrière elle : on nettoie
                // avant de laisser l'erreur remonter à RunAsync.
                File.Delete(temporary);
                throw;
            }

            File.Move(temporary, path, overwrite: true);
            regions.Reload();
            Log.WriteLine($"Base GeoIP renouvelée : {SourceFor(month)}.");
            return true;
        }

        Log.WriteLine("Base GeoIP introuvable pour ce mois comme pour le précédent.");
        return false;
    }

    private static bool Readable(string file)
    {
        try
        {
            using var reader = new Reader(file);
            return true;
        }
        catch (Exception e) when (e is InvalidDatabaseException or IOException)
        {
            return false;
        }
    }
}
