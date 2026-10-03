using System.Security.Cryptography;
using System.Text;
using Linkpearl.Core.Abstractions;
using Linkpearl.Core.Transport.Rendezvous;

namespace Linkpearl.Rendezvous;

/// <summary>Les sous-commandes de la mise à jour : update, et release-sign pour le workflow.</summary>
public static class ReleaseCommands
{
    /// <summary><c>lprdv update</c> : une ronde, lancée en root par lprdv-update.timer.</summary>
    public static async Task<int> UpdateAsync(TextWriter log)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5), MaxResponseContentBufferSize = 200 * 1024 * 1024 };

        // StateDirectory= de l'unité le fournit ; à la main, le même chemin.
        var stateDir = Environment.GetEnvironmentVariable("STATE_DIRECTORY") ?? "/var/lib/lprdv-update";
        Directory.CreateDirectory(stateDir);

        var updater = new Updater(
            new GitHubReleases(http), new SystemdInstallation(), ReleaseKeys.Trusted, new SystemClock(),
            ReleaseVersion.Current, stateDir, log);

        var outcome = await updater.RunAsync(CancellationToken.None).ConfigureAwait(false);

        if (outcome is UpdateOutcome.UpToDate)
            log.WriteLine($"Version {ReleaseVersion.Current?.ToString(3)} à jour.");

        // Une signature ou une somme fausse est la seule issue qui mérite
        // l'attention de l'opérateur : elle seule fait échouer l'unité.
        return outcome is UpdateOutcome.Rejected ? 1 : 0;
    }

    /// <summary>
    /// Écrit le manifeste et sa signature dans <paramref name="directory"/>.
    /// </summary>
    /// <remarks>
    /// Vérifie la signature avec les clés inscrites avant d'écrire : un secret
    /// erroné fait échouer la release, au lieu de publier un manifeste que
    /// personne n'acceptera.
    /// </remarks>
    public static byte[] Sign(string version, bool urgent, string directory, ECDsa key, IReadOnlyList<byte[]> trusted, IClock clock)
    {
        if (ReleaseVersion.TryParse(version, out var parsed) is false)
            throw new InvalidOperationException($"version {version} : trois composants numériques, pas de pré-version");

        // Toutes les unités publiées, et pas seulement ce que la mise à jour
        // pose : install.sh vérifie les siennes contre ce manifeste signé.
        var files = ReleaseManifest.SignedFiles.ToDictionary(
            name => name,
            name => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, name)))));

        var manifest = new ReleaseManifest(parsed, clock.UtcNow.ToUnixTimeSeconds(), urgent, files).ToBytes();
        var signature = ReleaseManifest.Sign(manifest, key);

        if (ReleaseManifest.Verify(manifest, signature, trusted) is false)
            throw new InvalidOperationException(
                $"la clé de signature n'est pas inscrite dans ReleaseKeys (clé publique {Convert.ToHexStringLower(ServiceConsensus.PublicPoint(key))})");

        File.WriteAllBytes(Path.Combine(directory, ReleaseManifest.FileName), manifest);
        File.WriteAllBytes(Path.Combine(directory, ReleaseManifest.SignatureName), signature);
        return manifest;
    }

    /// <summary><c>lprdv release-sign VERSION DOSSIER</c>, la clé dans LPRDV_RELEASE_KEY (PKCS#8 en base64).</summary>
    public static int SignFromEnvironment(string version, string directory, TextWriter log)
    {
        var secret = Environment.GetEnvironmentVariable("LPRDV_RELEASE_KEY");

        if (string.IsNullOrWhiteSpace(secret))
        {
            log.WriteLine("LPRDV_RELEASE_KEY absente : rien n'est signé.");
            return 1;
        }

        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(Convert.FromBase64String(secret.Trim()), out _);
        var urgent = Environment.GetEnvironmentVariable("LPRDV_URGENT") == "true";

        try
        {
            log.WriteLine(Encoding.UTF8.GetString(Sign(version, urgent, directory, key, ReleaseKeys.Trusted, new SystemClock())));
            return 0;
        }
        catch (InvalidOperationException e)
        {
            log.WriteLine(e.Message);
            return 1;
        }
    }
}
