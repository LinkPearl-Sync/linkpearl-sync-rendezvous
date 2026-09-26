using System.Security.Cryptography;
using Linkpearl.Core.Abstractions;

namespace Linkpearl.Rendezvous;

public enum UpdateOutcome { Unversioned, Unreachable, Rejected, UpToDate, Refused, Waiting, Stopped, Installed, RolledBack }

/// <summary>D'où viennent les releases : GitHub en service, un faux en test.</summary>
public interface IReleaseSource
{
    Task<string> LatestTagAsync(CancellationToken ct);

    Task<byte[]> DownloadAsync(string tag, string file, CancellationToken ct);
}

/// <summary>Où la release se pose : systemd en service, un faux en test.</summary>
public interface IInstallation
{
    void Stage(byte[] binary, byte[] unit);

    Task RestartAsync(CancellationToken ct);

    Task<bool> HealthyAsync(CancellationToken ct);

    void Restore();
}

/// <summary>
/// Une ronde de mise à jour automatique.
/// </summary>
/// <remarks>
/// Tirée, jamais poussée : rien ne commande à ce serveur, il va voir lui-même.
/// C'est le binaire en place, déjà de confiance, qui vérifie le suivant avec
/// les clés qu'il porte. Rien de ce qui échoue ici ne lève : la ronde suivante,
/// une heure plus tard, réessaie.
/// </remarks>
public sealed class Updater(
    IReleaseSource source, IInstallation installation, IReadOnlyList<byte[]> trusted, IClock clock,
    Version? current, string stateDir, TextWriter log)
{
    /// <summary>
    /// Le temps laissé au mainteneur pour retirer une release avant qu'elle
    /// parte partout ; la production, mise à jour à la main, l'essuie d'abord.
    /// </summary>
    public static readonly TimeSpan Guard = TimeSpan.FromHours(24);

    private string RefusedPath => Path.Combine(stateDir, "refused");

    /// <summary>
    /// La version posée et pas encore confirmée par /healthz. Une ronde tuée
    /// entre la pose et ce contrôle (redémarrage, disque plein) la laisse
    /// derrière elle : la suivante, lancée par le nouveau binaire qui se croit
    /// à jour, sait ainsi qu'elle doit encore vérifier.
    /// </summary>
    private string PendingPath => Path.Combine(stateDir, "pending");

    public async Task<UpdateOutcome> RunAsync(CancellationToken ct)
    {
        if (current is null)
        {
            log.WriteLine("Version en place illisible (binaire compilé à la main ?) : pas de mise à jour automatique.");
            return UpdateOutcome.Unversioned;
        }

        if (File.Exists(PendingPath) && await SettlePendingAsync(ct).ConfigureAwait(false) is { } settled)
            return settled;

        string tag;
        byte[] manifestBytes, signature;

        try
        {
            tag = await source.LatestTagAsync(ct).ConfigureAwait(false);
            manifestBytes = await source.DownloadAsync(tag, ReleaseManifest.FileName, ct).ConfigureAwait(false);
            signature = await source.DownloadAsync(tag, ReleaseManifest.SignatureName, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException && ct.IsCancellationRequested is false)
        {
            log.WriteLine($"Dernière release injoignable ou sans manifeste signé : {e.Message}");
            return UpdateOutcome.Unreachable;
        }

        if (ReleaseManifest.Verify(manifestBytes, signature, trusted) is false)
        {
            log.WriteLine($"Release {tag} : signature invalide, rien n'est installé.");
            return UpdateOutcome.Rejected;
        }

        if (ReleaseManifest.TryParse(manifestBytes, out var manifest, out var why) is false)
        {
            log.WriteLine($"Release {tag} : {why}, rien n'est installé.");
            return UpdateOutcome.Rejected;
        }

        // La version signée fait foi, pas le nom du tag : un vieux manifeste
        // rattaché à un tag récent ne fait pas revenir en arrière.
        if (manifest!.Version <= current)
            return UpdateOutcome.UpToDate;

        var version = manifest.Version.ToString(3);

        if (File.Exists(RefusedPath) && File.ReadAllText(RefusedPath).Trim() == version)
            return UpdateOutcome.Refused;

        var age = clock.UtcNow - DateTimeOffset.FromUnixTimeSeconds(manifest.Signed);

        if (age < Guard && manifest.Urgent is false)
        {
            log.WriteLine($"Version {version} signée il y a {age.TotalHours:0} h : installée après {Guard.TotalHours:0} h.");
            return UpdateOutcome.Waiting;
        }

        byte[] binary, unit;

        try
        {
            binary = await source.DownloadAsync(tag, "lprdv", ct).ConfigureAwait(false);
            unit = await source.DownloadAsync(tag, "lprdv.service", ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException && ct.IsCancellationRequested is false)
        {
            log.WriteLine($"Version {version} : téléchargement interrompu ({e.Message}).");
            return UpdateOutcome.Unreachable;
        }

        if (Matches(binary, manifest.Files["lprdv"]) is false || Matches(unit, manifest.Files["lprdv.service"]) is false)
        {
            log.WriteLine($"Version {version} : somme incorrecte, rien n'est installé.");
            return UpdateOutcome.Rejected;
        }

        // Un service arrêté par son opérateur ne doit pas être relancé, et un
        // service qui ne répond déjà plus (console sur un autre port, panne)
        // ne dirait rien de la nouvelle version : dans les deux cas, on ne pose rien.
        if (await installation.HealthyAsync(ct).ConfigureAwait(false) is false)
        {
            log.WriteLine($"Version {version} disponible, mais le service est arrêté ou ne répond pas : rien n'est posé.");
            return UpdateOutcome.Stopped;
        }

        File.WriteAllText(PendingPath, version);

        try
        {
            installation.Stage(binary, unit);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Le service tourne toujours sur l'ancien binaire : on remet les
            // fichiers sans redémarrer, et l'opérateur doit le savoir.
            log.WriteLine($"Version {version} : pose impossible ({e.Message}).");
            TryRestore();
            File.Delete(PendingPath);
            return UpdateOutcome.Rejected;
        }

        if (await RestartedAsync(ct).ConfigureAwait(false))
        {
            File.Delete(RefusedPath);
            File.Delete(PendingPath);
            log.WriteLine($"Version {version} installée (depuis {current.ToString(3)}).");
            return UpdateOutcome.Installed;
        }

        return await RollBackAsync(version, ct).ConfigureAwait(false);
    }

    /// <summary>Tranche une pose laissée en suspens ; null si la ronde peut continuer.</summary>
    private async Task<UpdateOutcome?> SettlePendingAsync(CancellationToken ct)
    {
        var version = File.ReadAllText(PendingPath).Trim();

        if (await installation.HealthyAsync(ct).ConfigureAwait(false))
        {
            File.Delete(PendingPath);
            log.WriteLine($"Version {version} confirmée après une ronde interrompue.");
            return null;
        }

        log.WriteLine($"La pose de {version} a été interrompue et le service ne répond pas : retour arrière.");
        return await RollBackAsync(version, ct).ConfigureAwait(false);
    }

    private async Task<UpdateOutcome> RollBackAsync(string version, CancellationToken ct)
    {
        if (TryRestore() is false)
            return UpdateOutcome.Rejected;

        await RestartedAsync(ct).ConfigureAwait(false);
        File.WriteAllText(RefusedPath, version);
        File.Delete(PendingPath);
        log.WriteLine($"Version {version} ne répond pas : retour à la précédente, elle ne sera pas retentée.");
        return UpdateOutcome.RolledBack;
    }

    private bool TryRestore()
    {
        try
        {
            installation.Restore();
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.WriteLine($"Retour à la version précédente impossible : {e.Message}");
            return false;
        }
    }

    private async Task<bool> RestartedAsync(CancellationToken ct)
    {
        try
        {
            await installation.RestartAsync(ct).ConfigureAwait(false);
            return await installation.HealthyAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            log.WriteLine($"Redémarrage impossible : {e.Message}");
            return false;
        }
    }

    private static bool Matches(byte[] content, string sum)
        => Convert.ToHexStringLower(SHA256.HashData(content)) == sum;
}
