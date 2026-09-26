using System.Diagnostics;

namespace Linkpearl.Rendezvous;

/// <summary>La pose d'une version sur une machine réelle, sous systemd.</summary>
/// <remarks>
/// Tout se fait par renommage : une coupure en plein travail laisse l'ancien
/// fichier ou le nouveau, jamais un mélange. La version remplacée reste à côté
/// sous « .previous », pour y revenir si la nouvelle ne répond pas.
/// </remarks>
public sealed class SystemdInstallation(
    string binaryPath = "/opt/lprdv/lprdv", string unitPath = "/etc/systemd/system/lprdv.service", int adminPort = 47901)
    : IInstallation
{
    private const UnixFileMode Executable =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private const UnixFileMode Readable =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    public void Stage(byte[] binary, byte[] unit)
    {
        File.Copy(binaryPath, binaryPath + ".previous", overwrite: true);

        if (File.Exists(unitPath))
            File.Copy(unitPath, unitPath + ".previous", overwrite: true);

        Replace(binaryPath, binary, Executable);
        Replace(unitPath, unit, Readable);
    }

    public void Restore()
    {
        Replace(binaryPath, File.ReadAllBytes(binaryPath + ".previous"), Executable);

        if (File.Exists(unitPath + ".previous"))
            Replace(unitPath, File.ReadAllBytes(unitPath + ".previous"), Readable);
    }

    public async Task RestartAsync(CancellationToken ct)
    {
        await SystemctlAsync("daemon-reload", ct).ConfigureAwait(false);
        await SystemctlAsync("restart lprdv", ct).ConfigureAwait(false);
    }

    public async Task<bool> HealthyAsync(CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };

        for (var attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                using var response = await http.GetAsync($"http://127.0.0.1:{adminPort}/healthz", ct).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                    return true;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && ct.IsCancellationRequested is false)
            {
                // Pas encore levé : on attend la seconde suivante.
            }

            await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        }

        return false;
    }

    private static void Replace(string path, byte[] content, UnixFileMode mode)
    {
        var staging = path + ".new";
        File.WriteAllBytes(staging, content);

        // « ! » et non « is false » : c'est la forme que l'analyseur de plateforme reconnaît comme une garde.
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(staging, mode);

        File.Move(staging, path, overwrite: true);
    }

    private static async Task SystemctlAsync(string arguments, CancellationToken ct)
    {
        using var process = Process.Start(new ProcessStartInfo("systemctl", arguments) { UseShellExecute = false })
            ?? throw new InvalidOperationException("systemctl introuvable");
        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"systemctl {arguments} a échoué ({process.ExitCode})");
    }
}
