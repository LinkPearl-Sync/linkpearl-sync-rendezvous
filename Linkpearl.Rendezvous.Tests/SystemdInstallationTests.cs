using Xunit;

namespace Linkpearl.Rendezvous.Tests;

/// <summary>La pose et la remise d'une version, sans systemd : seulement les fichiers.</summary>
public sealed class SystemdInstallationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"lprdv-install-{Guid.NewGuid():N}");

    public SystemdInstallationTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Poser_puis_remettre_rend_les_fichiers_d_origine()
    {
        var binary = Path.Combine(_dir, "lprdv");
        var unit = Path.Combine(_dir, "lprdv.service");
        File.WriteAllBytes(binary, [1]);
        File.WriteAllBytes(unit, [2]);
        var installation = new SystemdInstallation(binary, unit);

        installation.Stage([7, 7], [8, 8]);
        Assert.Equal(new byte[] { 7, 7 }, File.ReadAllBytes(binary));
        Assert.Equal(new byte[] { 8, 8 }, File.ReadAllBytes(unit));
        if (!OperatingSystem.IsWindows())
            Assert.True(File.GetUnixFileMode(binary).HasFlag(UnixFileMode.OtherExecute));

        installation.Restore();
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(binary));
        Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(unit));
    }
}
