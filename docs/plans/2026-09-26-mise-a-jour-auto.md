# Mise à jour automatique des serveurs : plan d'implémentation

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** chaque serveur lprdv installé par `install.sh` se met à jour seul, vingt-quatre heures après la signature d'une release, en revenant à l'ancienne version si la nouvelle ne répond pas.

**Architecture:** le workflow de release signe un manifeste JSON (ECDSA P-256, P1363) qui porte la version, l'heure, le marqueur urgent et les sommes du binaire et de l'unité. Une sous-commande `lprdv update`, lancée en root par un minuteur systemd, lit la dernière release, vérifie le manifeste avec les clés inscrites dans le binaire en place, applique le délai de garde, pose la nouvelle version par renommage et revient en arrière si `/healthz` ne répond pas. `install.sh` installe et active le minuteur.

**Tech Stack:** C# / .NET 10, xUnit, `System.Security.Cryptography.ECDsa`, `System.Text.Json.Nodes`, `HttpClient`, systemd, bash, GitHub Actions.

**Spec:** `docs/specs/2026-09-26-mise-a-jour-auto-design.md`

## Global Constraints

- Jamais de tiret cadratin (U+2014), ni dans le code, ni dans les commentaires, ni dans les commits.
- Commentaires en français, qui expliquent le pourquoi.
- Commits en Conventional Commits, sujet en français. Jamais de `Co-Authored-By:`, `Claude-Session:`, URL de session ni « Generated with ».
- `dotnet build Linkpearl.Rendezvous/Linkpearl.Rendezvous.csproj -c Release` sans warning (TreatWarningsAsErrors).
- `Protocol/` est une copie littérale du plugin : on n'y touche pas.
- `release.key` n'entre jamais dans un dépôt. `directory.key` et `bans.json` ne sont jamais écrasés.
- Délai de garde : 24 heures, compté depuis `signed`. `urgent` le supprime.
- Le programme de mise à jour ne touche jamais `/var/lib/lprdv` ni `/etc/systemd/system/lprdv.service.d/`.
- Une version n'est installée que si elle est strictement supérieure à la version en place ; les versions ont exactement trois composants numériques.
- Signature : ECDSA P-256, SHA-256, `DSASignatureFormat.IeeeP1363FixedFieldConcatenation`, sur les octets exacts de `lprdv.release.json`.
- Tag, push de tag et `deploy.sh` : chacun en commande séparée (règles de permission).

## Review Focus

1. **Version en place illisible** (binaire compilé à la main, qui se dit `1.0.0`, ou `0.5.2-3-gabc` par `git describe`) : `update` ne fait rien et le dit. Test `Une_version_en_place_illisible_ne_met_rien_a_jour` (Tâche 2).
2. **Manifeste d'une vieille release rattaché à un tag récent** : la version du manifeste, signée, fait foi, pas le nom du tag. Test `Un_manifeste_plus_ancien_que_la_version_en_place_ne_fait_rien` (Tâche 2).
3. **API GitHub en limite de débit ou en panne** : aucune exception ne sort, la ronde suivante réessaie. Test `GitHub_injoignable_ne_fait_rien` (Tâche 2).
4. **`systemctl restart` qui échoue** (et non seulement `/healthz` muet) : retour en arrière aussi. Test `Un_redemarrage_qui_echoue_revient_en_arriere` (Tâche 2).
5. **Mauvais secret dans le workflow** (clé qui n'est pas celle inscrite) : la release échoue avant publication, au lieu de publier un manifeste que personne n'acceptera. Test `Signer_avec_une_cle_non_inscrite_echoue` (Tâche 3).

---

### Task 1: Versions et manifeste signé

**Files:**
- Create: `Linkpearl.Rendezvous/ReleaseVersion.cs`
- Create: `Linkpearl.Rendezvous/ReleaseManifest.cs`
- Test: `Linkpearl.Rendezvous.Tests/ReleaseManifestTests.cs`

**Interfaces:**
- Produces:
  - `static class ReleaseVersion { static bool TryParse(string? text, out Version version); static Version? Current { get; } }`
  - `sealed record ReleaseManifest(Version Version, long Signed, bool Urgent, IReadOnlyDictionary<string, string> Files)` avec `const string FileName = "lprdv.release.json"`, `const string SignatureName = "lprdv.release.json.sig"`, `static readonly string[] Payload = ["lprdv", "lprdv.service"]`, `byte[] ToBytes()`, `static bool TryParse(byte[] json, out ReleaseManifest? manifest, out string? rejection)`, `static byte[] Sign(byte[] manifest, ECDsa key)`, `static bool Verify(byte[] manifest, byte[] signature, IEnumerable<byte[]> trustedPoints)`.

- [ ] **Step 1: Écrire les tests**

```csharp
using System.Security.Cryptography;
using System.Text;
using Linkpearl.Core.Transport.Rendezvous;
using Xunit;

namespace Linkpearl.Rendezvous.Tests;

/// <summary>Le manifeste d'une release : ce qu'il accepte, ce qu'il refuse, et sa signature.</summary>
public sealed class ReleaseManifestTests
{
    private static ReleaseManifest Sample(string version = "0.6.0", bool urgent = false) => new(
        new Version(version), 1_790_500_000, urgent,
        new Dictionary<string, string> { ["lprdv"] = new string('a', 64), ["lprdv.service"] = new string('b', 64) });

    [Theory]
    [InlineData("0.6.0", true)]
    [InlineData("v0.10.2", true)]
    [InlineData("1.0", false)]
    [InlineData("0.5.2-3-gabc", false)]
    [InlineData("0.6.0+abc", false)]
    [InlineData("", false)]
    public void Une_version_a_exactement_trois_composants(string text, bool valid)
        => Assert.Equal(valid, ReleaseVersion.TryParse(text, out _));

    [Fact]
    public void Les_versions_se_comparent_en_numerique()
    {
        ReleaseVersion.TryParse("0.10.0", out var newer);
        ReleaseVersion.TryParse("0.9.3", out var older);
        Assert.True(newer > older);
    }

    [Fact]
    public void Le_manifeste_fait_l_aller_retour()
    {
        Assert.True(ReleaseManifest.TryParse(Sample(urgent: true).ToBytes(), out var parsed, out _));
        Assert.Equal(new Version(0, 6, 0), parsed!.Version);
        Assert.True(parsed.Urgent);
        Assert.Equal(new string('b', 64), parsed.Files["lprdv.service"]);
    }

    [Theory]
    [InlineData("""{"version":"0.6","signed":1,"urgent":false,"files":{"lprdv":"aa","lprdv.service":"bb"}}""")]
    [InlineData("""{"version":"0.6.0","signed":1,"urgent":false,"files":{"lprdv":"aa"}}""")]
    [InlineData("""{"version":"0.6.0","urgent":false,"files":{"lprdv":"aa","lprdv.service":"bb"}}""")]
    [InlineData("pas du json")]
    public void Un_manifeste_incomplet_est_refuse(string json)
        => Assert.False(ReleaseManifest.TryParse(Encoding.UTF8.GetBytes(json), out _, out _));

    [Fact]
    public void Seule_une_cle_inscrite_verifie_la_signature()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var manifest = Sample().ToBytes();
        var signature = ReleaseManifest.Sign(manifest, key);

        Assert.True(ReleaseManifest.Verify(manifest, signature, [ServiceConsensus.PublicPoint(key)]));
        Assert.False(ReleaseManifest.Verify(manifest, signature, [ServiceConsensus.PublicPoint(other)]));

        manifest[^2] ^= 1;
        Assert.False(ReleaseManifest.Verify(manifest, signature, [ServiceConsensus.PublicPoint(key)]));
    }
}
```

- [ ] **Step 2: Les voir échouer**

Run: `dotnet test Linkpearl.Rendezvous.Tests/Linkpearl.Rendezvous.Tests.csproj --filter ReleaseManifestTests`
Expected: erreur de compilation, `ReleaseVersion` et `ReleaseManifest` inconnus.

- [ ] **Step 3: Écrire `ReleaseVersion.cs`**

```csharp
using System.Reflection;

namespace Linkpearl.Rendezvous;

/// <summary>Les versions publiées, telles que la mise à jour les compare.</summary>
/// <remarks>
/// Trois composants numériques exactement : <c>0.6</c> et <c>0.6.0</c> ne se
/// comparent pas comme on l'attend avec <see cref="Version"/>, et un suffixe
/// (<c>-rc1</c>, <c>-3-gabc</c>) désigne une pré-version ou un binaire compilé
/// à la main, que la mise à jour automatique n'a pas à toucher.
/// </remarks>
public static class ReleaseVersion
{
    public static bool TryParse(string? text, out Version version)
    {
        version = new Version();
        var trimmed = text?.Trim().TrimStart('v');

        if (string.IsNullOrEmpty(trimmed) || trimmed.Split('.').Length != 3 || Version.TryParse(trimmed, out var parsed) is false)
            return false;

        version = parsed;
        return true;
    }

    /// <summary>La version du binaire en place, ou null si elle ne se lit pas.</summary>
    /// <remarks>Le SDK accole « +commit » à la version informationnelle.</remarks>
    public static Version? Current { get; } = TryParse(
        typeof(ReleaseVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0],
        out var current) ? current : null;
}
```

- [ ] **Step 4: Écrire `ReleaseManifest.cs`**

```csharp
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Linkpearl.Rendezvous;

/// <summary>
/// Ce qu'une release promet : sa version, l'heure de sa signature, son urgence
/// et les sommes de ce qu'elle installe.
/// </summary>
/// <remarks>
/// Signé en ECDSA P-256 au format P1363, comme la liste du cercle ouvert. La
/// vérification est écrite ici plutôt qu'empruntée à ServiceConsensus : celle-ci
/// est privée, dans Protocol/, copie littérale du plugin qu'on ne retouche pas.
/// </remarks>
public sealed record ReleaseManifest(Version Version, long Signed, bool Urgent, IReadOnlyDictionary<string, string> Files)
{
    public const string FileName = "lprdv.release.json";

    public const string SignatureName = "lprdv.release.json.sig";

    /// <summary>Ce que la mise à jour pose : le binaire et son unité.</summary>
    public static readonly string[] Payload = ["lprdv", "lprdv.service"];

    public byte[] ToBytes()
    {
        var files = new JsonObject();

        foreach (var name in Files.Keys.Order(StringComparer.Ordinal))
            files[name] = Files[name];

        var document = new JsonObject
        {
            ["version"] = Version.ToString(3),
            ["signed"] = Signed,
            ["urgent"] = Urgent,
            ["files"] = files,
        };

        return Encoding.UTF8.GetBytes(document.ToJsonString());
    }

    public static bool TryParse(byte[] json, out ReleaseManifest? manifest, out string? rejection)
    {
        manifest = null;

        try
        {
            var root = JsonNode.Parse(json)?.AsObject();

            if (root is null
                || ReleaseVersion.TryParse(root["version"]?.GetValue<string>(), out var version) is false
                || root["signed"] is not JsonValue signed
                || root["urgent"] is not JsonValue urgent
                || root["files"] is not JsonObject files)
            {
                rejection = "manifeste incomplet";
                return false;
            }

            var sums = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var name in Payload)
            {
                if (files[name]?.GetValue<string>() is not { Length: 64 } sum)
                {
                    rejection = $"somme de {name} absente ou malformée";
                    return false;
                }

                sums[name] = sum.ToLowerInvariant();
            }

            manifest = new ReleaseManifest(version, signed.GetValue<long>(), urgent.GetValue<bool>(), sums);
            rejection = null;
            return true;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            rejection = "manifeste illisible";
            return false;
        }
    }

    public static byte[] Sign(byte[] manifest, ECDsa key)
        => key.SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    public static bool Verify(byte[] manifest, byte[] signature, IEnumerable<byte[]> trustedPoints)
    {
        foreach (var point in trustedPoints)
        {
            try
            {
                using var key = ECDsa.Create(new ECParameters
                {
                    Curve = ECCurve.NamedCurves.nistP256,
                    Q = new ECPoint { X = point[1..33], Y = point[33..] },
                });

                if (key.VerifyData(manifest, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                    return true;
            }
            catch (CryptographicException)
            {
                // Un point hors de la courbe ne vérifie rien : on passe au suivant.
            }
        }

        return false;
    }
}
```

- [ ] **Step 5: Les voir passer**

Run: `dotnet test Linkpearl.Rendezvous.Tests/Linkpearl.Rendezvous.Tests.csproj --filter ReleaseManifestTests`
Expected: PASS, 13 tests.

- [ ] **Step 6: Commit**

```bash
git add Linkpearl.Rendezvous/ReleaseVersion.cs Linkpearl.Rendezvous/ReleaseManifest.cs Linkpearl.Rendezvous.Tests/ReleaseManifestTests.cs
git commit -m "feat(mise à jour): versions et manifeste signé d'une release"
```

---

### Task 2: La décision de mise à jour

**Files:**
- Create: `Linkpearl.Rendezvous/Updater.cs`
- Test: `Linkpearl.Rendezvous.Tests/UpdaterTests.cs`

**Interfaces:**
- Consumes: `ReleaseManifest`, `ReleaseVersion` (Tâche 1), `IClock` (`Protocol/Core/Abstractions/IClock.cs`, namespace `Linkpearl.Core.Abstractions`), `ManualClock` (tests).
- Produces:
  - `enum UpdateOutcome { Unversioned, Unreachable, Rejected, UpToDate, Refused, Waiting, Installed, RolledBack }`
  - `interface IReleaseSource { Task<string> LatestTagAsync(CancellationToken ct); Task<byte[]> DownloadAsync(string tag, string file, CancellationToken ct); }`
  - `interface IInstallation { void Stage(byte[] binary, byte[] unit); Task RestartAsync(CancellationToken ct); Task<bool> HealthyAsync(CancellationToken ct); void Restore(); }`
  - `sealed class Updater(IReleaseSource source, IInstallation installation, IReadOnlyList<byte[]> trusted, IClock clock, Version? current, string stateDir, TextWriter log)` avec `static readonly TimeSpan Guard` et `Task<UpdateOutcome> RunAsync(CancellationToken ct)`.

- [ ] **Step 1: Écrire les tests**

```csharp
using System.Security.Cryptography;
using Linkpearl.Core.Transport.Rendezvous;
using Xunit;

namespace Linkpearl.Rendezvous.Tests;

/// <summary>Une ronde de mise à jour, contre un faux GitHub et un faux systemd.</summary>
public sealed class UpdaterTests : IDisposable
{
    private readonly string _state = Path.Combine(Path.GetTempPath(), $"lprdv-update-{Guid.NewGuid():N}");
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ManualClock _clock = new();
    private readonly FakeReleases _releases = new();
    private readonly FakeInstallation _installation = new();

    public UpdaterTests() => Directory.CreateDirectory(_state);

    public void Dispose()
    {
        _key.Dispose();
        Directory.Delete(_state, recursive: true);
    }

    private sealed class FakeReleases : IReleaseSource
    {
        public Dictionary<string, byte[]> Files { get; } = [];
        public bool Down { get; set; }

        public Task<string> LatestTagAsync(CancellationToken ct)
            => Down ? throw new HttpRequestException("limite de débit") : Task.FromResult("v0.6.0");

        public Task<byte[]> DownloadAsync(string tag, string file, CancellationToken ct)
            => Files.TryGetValue(file, out var bytes) ? Task.FromResult(bytes) : throw new HttpRequestException("404");
    }

    /// <summary>
    /// RestartAsync lève tant que Restore n'a pas eu lieu quand RestartFails est
    /// vrai : la nouvelle version ne démarre pas, l'ancienne oui.
    /// </summary>
    private sealed class FakeInstallation : IInstallation
    {
        public byte[]? Staged { get; private set; }
        public bool Restored { get; private set; }
        public bool Healthy { get; set; } = true;
        public bool RestartFails { get; set; }
        public int Restarts { get; private set; }

        public void Stage(byte[] binary, byte[] unit) => Staged = binary;

        public Task RestartAsync(CancellationToken ct)
        {
            Restarts++;
            return RestartFails && Restored is false ? throw new InvalidOperationException("systemctl") : Task.CompletedTask;
        }

        public Task<bool> HealthyAsync(CancellationToken ct) => Task.FromResult(Healthy);

        public void Restore() => Restored = true;
    }

    /// <summary>Publie une release signée, vieille de <paramref name="age"/>. Le binaire servi peut différer de sa somme.</summary>
    private void Publish(string version, TimeSpan age, bool urgent = false, byte[]? binary = null)
    {
        byte[] unit = [4, 5];
        var manifest = new ReleaseManifest(
            new Version(version), (_clock.UtcNow - age).ToUnixTimeSeconds(), urgent,
            new Dictionary<string, string>
            {
                ["lprdv"] = Convert.ToHexStringLower(SHA256.HashData(new byte[] { 1, 2, 3 })),
                ["lprdv.service"] = Convert.ToHexStringLower(SHA256.HashData(unit)),
            }).ToBytes();

        _releases.Files["lprdv.release.json"] = manifest;
        _releases.Files["lprdv.release.json.sig"] = ReleaseManifest.Sign(manifest, _key);
        _releases.Files["lprdv"] = binary ?? [1, 2, 3];
        _releases.Files["lprdv.service"] = unit;
    }

    private Updater Updater(string? current = "0.5.2")
    {
        Version? version = current is not null && ReleaseVersion.TryParse(current, out var parsed) ? parsed : null;
        return new Updater(_releases, _installation, [ServiceConsensus.PublicPoint(_key)], _clock, version, _state, TextWriter.Null);
    }

    [Fact]
    public async Task Une_release_signee_de_plus_de_24_heures_est_installee()
    {
        Publish("0.6.0", TimeSpan.FromHours(25));

        Assert.Equal(UpdateOutcome.Installed, await Updater().RunAsync(CancellationToken.None));
        Assert.Equal(new byte[] { 1, 2, 3 }, _installation.Staged);
    }

    [Fact]
    public async Task Une_release_trop_recente_attend()
    {
        Publish("0.6.0", TimeSpan.FromHours(23));

        Assert.Equal(UpdateOutcome.Waiting, await Updater().RunAsync(CancellationToken.None));
        Assert.Null(_installation.Staged);
    }

    [Fact]
    public async Task Une_release_urgente_n_attend_pas()
    {
        Publish("0.6.0", TimeSpan.FromMinutes(5), urgent: true);

        Assert.Equal(UpdateOutcome.Installed, await Updater().RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Une_signature_d_une_autre_cle_est_refusee()
    {
        Publish("0.6.0", TimeSpan.FromHours(25));
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _releases.Files["lprdv.release.json.sig"] = ReleaseManifest.Sign(_releases.Files["lprdv.release.json"], other);

        Assert.Equal(UpdateOutcome.Rejected, await Updater().RunAsync(CancellationToken.None));
        Assert.Null(_installation.Staged);
    }

    [Fact]
    public async Task Un_binaire_qui_ne_correspond_pas_a_sa_somme_est_refuse()
    {
        Publish("0.6.0", TimeSpan.FromHours(25), binary: [9, 9, 9]);

        Assert.Equal(UpdateOutcome.Rejected, await Updater().RunAsync(CancellationToken.None));
        Assert.Null(_installation.Staged);
    }

    [Fact]
    public async Task Un_manifeste_plus_ancien_que_la_version_en_place_ne_fait_rien()
    {
        Publish("0.5.2", TimeSpan.FromDays(10));

        Assert.Equal(UpdateOutcome.UpToDate, await Updater("0.5.2").RunAsync(CancellationToken.None));
        Assert.Equal(UpdateOutcome.UpToDate, await Updater("0.6.0").RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Une_version_en_place_illisible_ne_met_rien_a_jour()
    {
        Publish("0.6.0", TimeSpan.FromHours(25));

        Assert.Equal(UpdateOutcome.Unversioned, await Updater(current: null).RunAsync(CancellationToken.None));
        Assert.Null(_installation.Staged);
    }

    [Fact]
    public async Task GitHub_injoignable_ne_fait_rien()
    {
        _releases.Down = true;

        Assert.Equal(UpdateOutcome.Unreachable, await Updater().RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Une_release_sans_manifeste_ne_fait_rien()
        => Assert.Equal(UpdateOutcome.Unreachable, await Updater().RunAsync(CancellationToken.None));

    [Fact]
    public async Task Une_version_muette_revient_en_arriere_et_n_est_plus_retentee()
    {
        Publish("0.6.0", TimeSpan.FromHours(25));
        _installation.Healthy = false;

        Assert.Equal(UpdateOutcome.RolledBack, await Updater().RunAsync(CancellationToken.None));
        Assert.True(_installation.Restored);
        Assert.Equal(2, _installation.Restarts);

        Assert.Equal(UpdateOutcome.Refused, await Updater().RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Une_version_plus_recente_que_la_refusee_est_retentee()
    {
        Publish("0.6.0", TimeSpan.FromHours(25));
        _installation.Healthy = false;
        await Updater().RunAsync(CancellationToken.None);

        _installation.Healthy = true;
        Publish("0.6.1", TimeSpan.FromHours(25));

        Assert.Equal(UpdateOutcome.Installed, await Updater().RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Un_redemarrage_qui_echoue_revient_en_arriere()
    {
        Publish("0.6.0", TimeSpan.FromHours(25));
        _installation.RestartFails = true;

        Assert.Equal(UpdateOutcome.RolledBack, await Updater().RunAsync(CancellationToken.None));
        Assert.True(_installation.Restored);
    }
}
```

- [ ] **Step 2: Les voir échouer**

Run: `dotnet test Linkpearl.Rendezvous.Tests/Linkpearl.Rendezvous.Tests.csproj --filter UpdaterTests`
Expected: erreur de compilation, `Updater`, `IReleaseSource`, `IInstallation`, `UpdateOutcome` inconnus.

- [ ] **Step 3: Écrire `Updater.cs`**

```csharp
using System.Security.Cryptography;
using Linkpearl.Core.Abstractions;

namespace Linkpearl.Rendezvous;

public enum UpdateOutcome { Unversioned, Unreachable, Rejected, UpToDate, Refused, Waiting, Installed, RolledBack }

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

    public async Task<UpdateOutcome> RunAsync(CancellationToken ct)
    {
        if (current is null)
        {
            log.WriteLine("Version en place illisible (binaire compilé à la main ?) : pas de mise à jour automatique.");
            return UpdateOutcome.Unversioned;
        }

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

        installation.Stage(binary, unit);

        if (await RestartedAsync(ct).ConfigureAwait(false))
        {
            File.Delete(RefusedPath);
            log.WriteLine($"Version {version} installée (depuis {current.ToString(3)}).");
            return UpdateOutcome.Installed;
        }

        installation.Restore();
        await RestartedAsync(ct).ConfigureAwait(false);
        File.WriteAllText(RefusedPath, version);
        log.WriteLine($"Version {version} ne répond pas : retour à {current.ToString(3)}, elle ne sera pas retentée.");
        return UpdateOutcome.RolledBack;
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
```

- [ ] **Step 4: Les voir passer**

Run: `dotnet test Linkpearl.Rendezvous.Tests/Linkpearl.Rendezvous.Tests.csproj --filter UpdaterTests`
Expected: PASS, 12 tests.

- [ ] **Step 5: Commit**

```bash
git add Linkpearl.Rendezvous/Updater.cs Linkpearl.Rendezvous.Tests/UpdaterTests.cs
git commit -m "feat(mise à jour): la ronde de mise à jour, délai de garde et retour arrière"
```

---

### Task 3: GitHub, systemd, et les sous-commandes

**Files:**
- Create: `Linkpearl.Rendezvous/GitHubReleases.cs`
- Create: `Linkpearl.Rendezvous/SystemdInstallation.cs`
- Create: `Linkpearl.Rendezvous/ReleaseKeys.cs`
- Create: `Linkpearl.Rendezvous/ReleaseCommands.cs`
- Modify: `Linkpearl.Rendezvous/Program.cs` (tout en tête, avant `var port = 47900;`) et le texte de `--help`
- Test: `Linkpearl.Rendezvous.Tests/SystemdInstallationTests.cs`, `Linkpearl.Rendezvous.Tests/ReleaseCommandsTests.cs`

**Interfaces:**
- Consumes: `IReleaseSource`, `IInstallation`, `Updater`, `UpdateOutcome` (Tâche 2) ; `ReleaseManifest`, `ReleaseVersion` (Tâche 1) ; `ServiceConsensus.PublicPoint(ECDsa)` (Protocol) ; `SystemClock` (`Linkpearl.Rendezvous/SystemClock.cs`).
- Produces:
  - `sealed partial class GitHubReleases(HttpClient http, string repository = "LinkPearl-Sync/linkpearl-sync-rendezvous") : IReleaseSource`
  - `sealed class SystemdInstallation(string binaryPath = "/opt/lprdv/lprdv", string unitPath = "/etc/systemd/system/lprdv.service", int adminPort = 47901) : IInstallation`
  - `static class ReleaseKeys { static IReadOnlyList<byte[]> Trusted { get; } }`
  - `static class ReleaseCommands { static Task<int> UpdateAsync(TextWriter log); static byte[] Sign(string version, bool urgent, string directory, ECDsa key, IReadOnlyList<byte[]> trusted, IClock clock); static int SignFromEnvironment(string version, string directory, TextWriter log); }`
  - En ligne de commande : `lprdv update` (écrit « Version X à jour. » quand rien n'est à faire) et `lprdv release-sign VERSION DOSSIER`.

- [ ] **Step 1: Engendrer la clé de release (poste du mainteneur)**

Un programme jetable, dans le dossier scratchpad de la session, hors de tout dépôt :

```bash
mkdir -p "$SCRATCH/cle" && cd "$SCRATCH/cle" && dotnet new console -o . --force >/dev/null
cat > Program.cs <<'EOF'
using System.Security.Cryptography;
var path = args[0];
if (File.Exists(path)) { Console.Error.WriteLine("existe déjà : jamais écrasée"); return 1; }
using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
File.WriteAllBytes(path, key.ExportPkcs8PrivateKey());
File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
var q = key.ExportParameters(false).Q;
Console.WriteLine(Convert.ToHexStringLower([0x04, .. q.X!, .. q.Y!]));
return 0;
EOF
dotnet run -- ~/.ssh/linkpearl_release.key
```

Expected: une ligne de 130 caractères hexadécimaux commençant par `04`, à recopier à l'étape 4. `~/.ssh/linkpearl_release.key` ne quitte le poste que vers le secret GitHub (Tâche 4) et une sauvegarde hors ligne. Ajouter à la mémoire du projet une ligne sur cette clé, à côté de celle de `directory.key`.

- [ ] **Step 2: Écrire les tests**

`SystemdInstallationTests.cs` :

```csharp
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
        Assert.True(File.GetUnixFileMode(binary).HasFlag(UnixFileMode.OtherExecute));

        installation.Restore();
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(binary));
        Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(unit));
    }
}
```

`ReleaseCommandsTests.cs` :

```csharp
using System.Security.Cryptography;
using Linkpearl.Core.Transport.Rendezvous;
using Xunit;

namespace Linkpearl.Rendezvous.Tests;

/// <summary>La signature d'une release par le workflow.</summary>
public sealed class ReleaseCommandsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"lprdv-sign-{Guid.NewGuid():N}");
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public ReleaseCommandsTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(Path.Combine(_dir, "lprdv"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(_dir, "lprdv.service"), [4, 5]);
    }

    public void Dispose()
    {
        _key.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Signer_ecrit_un_manifeste_que_la_cle_inscrite_verifie()
    {
        var clock = new ManualClock();
        ReleaseCommands.Sign("0.6.0", urgent: true, _dir, _key, [ServiceConsensus.PublicPoint(_key)], clock);

        var manifest = File.ReadAllBytes(Path.Combine(_dir, ReleaseManifest.FileName));
        var signature = File.ReadAllBytes(Path.Combine(_dir, ReleaseManifest.SignatureName));
        Assert.True(ReleaseManifest.Verify(manifest, signature, [ServiceConsensus.PublicPoint(_key)]));
        Assert.True(ReleaseManifest.TryParse(manifest, out var parsed, out _));
        Assert.True(parsed!.Urgent);
        Assert.Equal(clock.UtcNow.ToUnixTimeSeconds(), parsed.Signed);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(new byte[] { 1, 2, 3 })), parsed.Files["lprdv"]);
    }

    [Fact]
    public void Signer_avec_une_cle_non_inscrite_echoue()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        Assert.Throws<InvalidOperationException>(
            () => ReleaseCommands.Sign("0.6.0", urgent: false, _dir, _key, [ServiceConsensus.PublicPoint(other)], new ManualClock()));
        Assert.False(File.Exists(Path.Combine(_dir, ReleaseManifest.FileName)));
    }

    [Fact]
    public void Signer_une_pre_version_echoue()
        => Assert.Throws<InvalidOperationException>(
            () => ReleaseCommands.Sign("0.6.0-rc1", urgent: false, _dir, _key, [ServiceConsensus.PublicPoint(_key)], new ManualClock()));

    [Fact]
    public void La_cle_de_release_inscrite_est_un_point_P256()
    {
        var point = Assert.Single(ReleaseKeys.Trusted);
        Assert.Equal(65, point.Length);
        Assert.Equal(0x04, point[0]);
    }
}
```

- [ ] **Step 3: Les voir échouer**

Run: `dotnet test Linkpearl.Rendezvous.Tests/Linkpearl.Rendezvous.Tests.csproj --filter "SystemdInstallationTests|ReleaseCommandsTests"`
Expected: erreur de compilation, `SystemdInstallation`, `ReleaseCommands`, `ReleaseKeys` inconnus.

- [ ] **Step 4: Écrire `ReleaseKeys.cs`**, avec le point de l'étape 1

```csharp
namespace Linkpearl.Rendezvous;

/// <summary>
/// Les clés dont une release doit porter la signature pour être installée
/// automatiquement.
/// </summary>
/// <remarks>
/// Distincte de directory.key : la fuite de l'une ne compromet pas l'autre. La
/// privée est un secret de l'environnement GitHub « release », que seuls les
/// tags v* atteignent, et une copie hors ligne chez le mainteneur. Changer de
/// clé : publier une release signée par l'ancienne qui inscrit la nouvelle ici.
/// </remarks>
public static class ReleaseKeys
{
    public static IReadOnlyList<byte[]> Trusted { get; } =
    [
        // Engendrée le 26 septembre 2026, ~/.ssh/linkpearl_release.key chez le mainteneur.
        Convert.FromHexString("POINT_HEX_DE_L_ETAPE_1"),
    ];
}
```

`POINT_HEX_DE_L_ETAPE_1` est remplacé par la sortie de l'étape 1 ; c'est la seule valeur du plan produite à l'exécution.

- [ ] **Step 5: Écrire `SystemdInstallation.cs`**

```csharp
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
```

- [ ] **Step 6: Écrire `GitHubReleases.cs`**

```csharp
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Linkpearl.Rendezvous;

/// <summary>Les releases publiées sur GitHub.</summary>
/// <remarks>
/// « latest » ignore les pré-versions : une release en rc n'est jamais
/// installée automatiquement. Sans jeton, l'API accepte soixante requêtes par
/// heure et par adresse ; une ronde par heure en fait une.
/// </remarks>
public sealed partial class GitHubReleases(HttpClient http, string repository = "LinkPearl-Sync/linkpearl-sync-rendezvous")
    : IReleaseSource
{
    [GeneratedRegex("^v[0-9]+\\.[0-9]+\\.[0-9]+$")]
    private static partial Regex ReleaseTag();

    public async Task<string> LatestTagAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repository}/releases/latest");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("lprdv", ReleaseVersion.Current?.ToString(3) ?? "0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var tag = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false))?["tag_name"]?.GetValue<string>();

        // Le tag finit dans une URL : on n'y admet que la forme d'une version.
        return tag is not null && ReleaseTag().IsMatch(tag)
            ? tag
            : throw new HttpRequestException($"tag de release inattendu : {tag}");
    }

    public async Task<byte[]> DownloadAsync(string tag, string file, CancellationToken ct)
    {
        using var response = await http.GetAsync($"https://github.com/{repository}/releases/download/{tag}/{file}", ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }
}
```

- [ ] **Step 7: Écrire `ReleaseCommands.cs`**

```csharp
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

        var files = ReleaseManifest.Payload.ToDictionary(
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
```

- [ ] **Step 8: Brancher dans `Program.cs`**, juste après le commentaire d'en-tête, avant `var port = 47900;`

```csharp
// Sous-commandes de la mise à jour, avant tout le reste : elles ne démarrent
// pas le service.
if (args is ["update"])
{
    Environment.ExitCode = await ReleaseCommands.UpdateAsync(Console.Out);
    return;
}

if (args is ["release-sign", var signedVersion, var signedDirectory])
{
    Environment.ExitCode = ReleaseCommands.SignFromEnvironment(signedVersion, signedDirectory, Console.Out);
    return;
}
```

Et dans le texte de `--help`, après les lignes de `--settings` :

```
        lprdv update  une ronde de mise à jour automatique, lancée en root
                      par lprdv-update.timer.
```

- [ ] **Step 9: Les voir passer, puis toute la suite**

Run: `dotnet test Linkpearl.Rendezvous.Tests/Linkpearl.Rendezvous.Tests.csproj --filter "SystemdInstallationTests|ReleaseCommandsTests"`
Expected: PASS, 5 tests.

Run: `dotnet build Linkpearl.Rendezvous/Linkpearl.Rendezvous.csproj -c Release && dotnet test Linkpearl.Rendezvous.Tests/Linkpearl.Rendezvous.Tests.csproj`
Expected: build sans warning ; suite entière verte (285 existants + 30 nouveaux = 315).

- [ ] **Step 10: Commit**

```bash
git add Linkpearl.Rendezvous/GitHubReleases.cs Linkpearl.Rendezvous/SystemdInstallation.cs Linkpearl.Rendezvous/ReleaseKeys.cs Linkpearl.Rendezvous/ReleaseCommands.cs Linkpearl.Rendezvous/Program.cs Linkpearl.Rendezvous.Tests/SystemdInstallationTests.cs Linkpearl.Rendezvous.Tests/ReleaseCommandsTests.cs
git commit -m "feat(mise à jour): lprdv update et release-sign, contre GitHub et systemd"
```

---

### Task 4: Unités, workflow signé, première release signée

**Files:**
- Create: `deploy/lprdv-update.service`, `deploy/lprdv-update.timer`
- Modify: `.github/workflows/release.yml` (job `release` : `environment`, étape de compilation, nouvelle étape de signature, `files`)
- Modify: `README.md` (section déploiement), `CLAUDE.md` (section déploiement)

**Interfaces:**
- Consumes: `lprdv release-sign VERSION DOSSIER` (Tâche 3).
- Produces: assets de release `lprdv`, `lprdv.service`, `lprdv-update.service`, `lprdv-update.timer`, `lprdv.sha256` (couvre les quatre), `lprdv.release.json`, `lprdv.release.json.sig`.

- [ ] **Step 1: Écrire les unités**

`deploy/lprdv-update.service` :

```ini
# Une ronde de mise à jour automatique de lprdv, déclenchée par
# lprdv-update.timer. En root : elle remplace /opt/lprdv/lprdv et l'unité, et
# redémarre le service. Elle ne touche ni /var/lib/lprdv, ni les compléments
# de /etc/systemd/system/lprdv.service.d/.
#
# La couper : systemctl disable --now lprdv-update.timer

[Unit]
Description=Mise à jour automatique du service de rendez-vous Linkpearl
After=network-online.target
Wants=network-online.target

[Service]
Type=oneshot
ExecStart=/opt/lprdv/lprdv update
StateDirectory=lprdv-update
# Durci autant que le travail le permet : il écrit dans /opt et /etc, et
# parle à systemd, donc ni ProtectSystem=strict ni sandbox réseau.
ProtectSystem=yes
ProtectHome=yes
PrivateTmp=yes
NoNewPrivileges=yes
RestrictAddressFamilies=AF_INET AF_INET6 AF_UNIX
LockPersonality=yes
```

`deploy/lprdv-update.timer` :

```ini
# Chaque heure, décalé au hasard jusqu'à une heure : le réseau ne redémarre
# pas à la même seconde quand une version sort de son délai de garde.

[Unit]
Description=Vérifie chaque heure s'il existe une nouvelle version de lprdv

[Timer]
OnBootSec=15min
OnUnitActiveSec=1h
RandomizedDelaySec=1h

[Install]
WantedBy=timers.target
```

- [ ] **Step 2: Modifier `release.yml`**

Sous `release:`, après `runs-on: ubuntu-latest` :

```yaml
    # La clé de signature n'existe que dans cet environnement, que seuls les
    # tags v* atteignent : ni une branche ni une pull request ne la lisent.
    environment: release
```

Dans l'étape « Compiler le binaire autonome », remplacer les trois dernières lignes (le commentaire sur l'unité, `cp deploy/lprdv.service out/` et `cd out && sha256sum …`) par :

```yaml
          # Les unités partent avec le binaire, sous la même somme : un script
          # d'installation n'a ainsi qu'une source à vérifier.
          cp deploy/lprdv.service deploy/lprdv-update.service deploy/lprdv-update.timer out/
          cd out && sha256sum lprdv lprdv.service lprdv-update.service lprdv-update.timer > lprdv.sha256 && cat lprdv.sha256
```

Nouvelle étape, juste avant « Publier la version » :

```yaml
      - name: Signer le manifeste
        # Une pré-version n'est jamais installée automatiquement : pas de manifeste.
        if: steps.tag.outputs.PRERELEASE == 'false'
        env:
          VERSION: ${{ steps.tag.outputs.VERSION }}
          TAG: ${{ github.ref_name }}
          LPRDV_RELEASE_KEY: ${{ secrets.LPRDV_RELEASE_KEY }}
        # « urgent » dans le message du tag annoté lève le délai de garde. Le
        # checkout ne rapporte pas l'objet du tag : on va le chercher.
        run: |
          git fetch --force origin "refs/tags/$TAG:refs/tags/$TAG"
          if git tag -l --format='%(contents)' "$TAG" | grep -qiw urgent; then export LPRDV_URGENT=true; fi
          ./out/lprdv release-sign "$VERSION" out
```

Dans `files:` de « Publier la version » :

```yaml
          files: |
            out/lprdv
            out/lprdv.service
            out/lprdv-update.service
            out/lprdv-update.timer
            out/lprdv.sha256
            out/lprdv.release.json
            out/lprdv.release.json.sig
```

Pour une pré-version, les deux derniers fichiers n'existent pas ; `softprops/action-gh-release` avertit sans échouer (`fail_on_unmatched_files` n'est pas activé dans ce workflow).

- [ ] **Step 3: Documenter**

`README.md`, à la fin de la section déploiement :

```markdown
### Mise à jour automatique

Les serveurs installés par `install.sh` (site) se mettent à jour seuls : `lprdv-update.timer`
lance `lprdv update` chaque heure. Une release n'est installée que si son manifeste
`lprdv.release.json` porte une signature d'une clé de `ReleaseKeys.cs`, et seulement 24 heures
après sa signature, sauf si le message du tag annoté contient `urgent`
(`git tag -a v0.6.1 -m "v0.6.1 urgent"`). Une version qui ne répond pas sur `/healthz` est
défaite et n'est plus retentée. Retirer une release pendant les 24 heures (la supprimer, ou la
passer en pré-version) suffit à ce que personne ne l'installe. La production n'a pas de
minuteur : `deploy.sh` la met à jour, et elle essuie chaque version la première.

La clé privée vit dans le secret `LPRDV_RELEASE_KEY` de l'environnement GitHub `release`
(tags `v*` seulement) et hors ligne chez le mainteneur. Elle est distincte de `directory.key`.
```

`CLAUDE.md`, section déploiement, après la puce sur `directory.key` :

```markdown
- `~/.ssh/linkpearl_release.key` signe les releases pour la mise à jour automatique ; sa
  partie publique est dans `ReleaseKeys.cs`, sa copie dans le secret `LPRDV_RELEASE_KEY` de
  l'environnement GitHub `release`. Ne jamais l'écraser ni la régénérer : la perdre oblige
  à publier une release signée par une autre clé déjà inscrite, sans quoi plus aucun
  serveur ne se met à jour seul.
- Publier une correction urgente : `git tag -a vX.Y.Z -m "vX.Y.Z urgent"`, qui lève le
  délai de garde de 24 heures.
```

- [ ] **Step 4: Créer l'environnement et le secret (DEMANDER LA CONFIRMATION DU MAINTENEUR AVANT)**

```bash
REPO=LinkPearl-Sync/linkpearl-sync-rendezvous
gh api -X PUT "repos/$REPO/environments/release" -F 'deployment_branch_policy[protected_branches]=false' -F 'deployment_branch_policy[custom_branch_policies]=true'
gh api -X POST "repos/$REPO/environments/release/deployment-branch-policies" -f name='v*' -f type=tag
base64 -w0 ~/.ssh/linkpearl_release.key | gh secret set LPRDV_RELEASE_KEY --env release --repo "$REPO"
gh api "repos/$REPO/environments/release/deployment-branch-policies" --jq '.branch_policies[] | "\(.type) \(.name)"'
gh secret list --env release --repo "$REPO"
```

Expected: `tag v*`, puis `LPRDV_RELEASE_KEY` dans la liste.

- [ ] **Step 5: Commit, fusion, release v0.6.0, déploiement de la production**

```bash
git add deploy/lprdv-update.service deploy/lprdv-update.timer .github/workflows/release.yml README.md CLAUDE.md
git commit -m "feat(mise à jour): unités du minuteur, manifeste signé par le workflow de release"
git checkout main && git merge --ff-only feat/mise-a-jour-auto && git push origin main
```

Puis, chacune seule : `git tag -a v0.6.0 -m v0.6.0`, `git push origin v0.6.0`, `LPRDV_HOST=debian@rdv.linkpearl.eorzea.events LPRDV_KEY=~/.ssh/linkpearl_rdv ./deploy/deploy.sh`.

- [ ] **Step 6: Vérifier la release**

```bash
gh run list --workflow release.yml --limit 1
gh release view v0.6.0 --json assets -q '.assets[].name'
curl -fsSL https://github.com/LinkPearl-Sync/linkpearl-sync-rendezvous/releases/download/v0.6.0/lprdv.release.json
```

Expected: run `success` ; sept assets ; un manifeste `{"version":"0.6.0","signed":…,"urgent":false,"files":{…}}`. L'étape « Signer le manifeste » a vérifié la signature contre `ReleaseKeys`, sinon elle aurait échoué.

---

### Task 5: Le site installe le minuteur

**Files (dépôt `../site`, nouvelle branche `feat/mise-a-jour-auto`):**
- Modify: `install.sh`
- Modify: `heberger.html`
- Modify: `.github/workflows/install.yml`
- Modify: `README.md`, `expert.html` (étapes « Update » et « Mettre à jour »)

**Interfaces:**
- Consumes: assets `lprdv-update.service`, `lprdv-update.timer` et `lprdv.sha256` qui les couvre (Tâche 4) ; `lprdv update` qui écrit « Version X à jour. » (Tâche 3).

- [ ] **Step 1: Test CI d'abord.** Dans `install.yml`, à la fin de l'étape « Le service tourne avec ses options », ajouter :

```yaml
          systemctl is-enabled lprdv-update.timer
          # La version tout juste installée est la dernière : la ronde doit le dire.
          sudo /opt/lprdv/lprdv update | tee update.txt
          grep -q 'à jour' update.txt
```

Et une étape après « Relancé sans option, il met à jour et garde tout » :

```yaml
      - name: Sans mise à jour automatique, le minuteur est coupé et le reste
        # Une commande niée ne fait pas échouer bash -e : on teste par if.
        run: |
          sudo bash install.sh --no-auto-update --no-firewall
          if systemctl is-enabled --quiet lprdv-update.timer; then echo "minuteur encore actif"; exit 1; fi
          sudo bash install.sh --no-firewall
          if systemctl is-enabled --quiet lprdv-update.timer; then echo "minuteur réactivé à tort"; exit 1; fi
```

Commiter l'essai seul, pousser la branche. Expected : le workflow échoue sur `systemctl is-enabled lprdv-update.timer` (le script ne l'installe pas encore).

- [ ] **Step 2: Modifier `install.sh`**

En-tête, après `--no-firewall` :

```
#   --no-auto-update       ne pas se mettre à jour seul (lprdv-update.timer)
```

Variables, après `firewall=1` : `auto_update=""`.
Options, dans le `case` : `--no-auto-update) auto_update=0; configured=1; shift ;;`
Boucle de téléchargement :

```bash
for file in lprdv lprdv.service lprdv-update.service lprdv-update.timer lprdv.sha256; do
```

Après `install -m 0644 "$work/lprdv.service" /etc/systemd/system/lprdv.service` :

```bash
install -m 0644 "$work/lprdv-update.service" /etc/systemd/system/lprdv-update.service
install -m 0644 "$work/lprdv-update.timer" /etc/systemd/system/lprdv-update.timer
# Sans option, on garde le choix déjà fait ; au premier passage, actif.
if [ -z "$auto_update" ]; then
  if [ "$configured" -eq 0 ] && [ -f "$DROPIN" ] && ! systemctl is-enabled --quiet lprdv-update.timer; then
    auto_update=0
  else
    auto_update=1
  fi
fi
```

Après `systemctl restart lprdv` :

```bash
if [ "$auto_update" -eq 1 ]; then
  systemctl enable --now --quiet lprdv-update.timer
else
  systemctl disable --now --quiet lprdv-update.timer 2>/dev/null || true
fi
```

Dans le message final, après la ligne sur `/var/lib/lprdv` et son sel, avant `EOF` :

```
Mise à jour automatique : $(if [ "$auto_update" -eq 1 ]; then echo "active, chaque heure"; else echo "coupée"; fi).
```

Note : `--no-auto-update` met `configured=1` ; donné seul, il réécrit donc le complément d'options avec les valeurs par défaut. La page et le README disent de relancer la commande complète du générateur.

- [ ] **Step 3: `heberger.html`**

Après la case `firewall` du formulaire :

```html
    <label class="check"><input type="checkbox" id="autoupdate" checked><span><b data-t="autoUpdate">Update automatically</b><span class="small" data-t="autoUpdateHint"></span></span></label>
```

Dans `TEXT.en` : `autoUpdate: "Update automatically", autoUpdateHint: "Signed releases only, 24 hours after publication. Back to the previous version if the new one doesn't answer.",`
Dans `TEXT.fr` : `autoUpdate: "Mettre à jour automatiquement", autoUpdateHint: "Versions signées seulement, 24 heures après leur publication. Retour à la précédente si la nouvelle ne répond pas.",`
Dans `build()`, après la ligne qui pousse `--no-firewall` : `if (!$("autoupdate").checked) args.push("--no-auto-update");`
Dans le texte `after` (en et fr), remplacer « without options, it keeps the ones already set » / « sans option, elle garde celles déjà posées » par « run the full command again to change an option » / « relancer la commande complète pour changer une option ».

- [ ] **Step 4: Documenter**

`README.md` du site, dans la puce `install.sh` : « Il installe aussi `lprdv-update.timer`, la mise à jour automatique, sauf avec `--no-auto-update`. »
`expert.html`, étape « Update » : phrase d'ouverture « Servers installed with the generator update themselves, 24 hours after each signed release. » ; étape « Mettre à jour » : « Les serveurs installés par le générateur se mettent à jour seuls, 24 heures après chaque release signée. »

- [ ] **Step 5: CI verte, capture, publication**

```bash
bash -n install.sh
git add install.sh heberger.html .github/workflows/install.yml README.md expert.html
git commit -m "feat(héberger): la mise à jour automatique, installée et activée par défaut"
git push origin feat/mise-a-jour-auto
gh run list --workflow install.yml --limit 1
```

Expected: `success`. Capture de `heberger.html` (outil `shot.js` du scratchpad) : la case apparaît, et décochée elle ajoute `--no-auto-update` à la commande. Puis `git checkout main && git merge --ff-only feat/mise-a-jour-auto && git push origin main`, attendre `pages.yml`, et vérifier que `curl -fsSL https://linkpearl-sync.github.io/install.sh | diff - install.sh` est vide.

---

### Task 6: Première mise à jour de bout en bout

**Files:** aucun, sauf correctif si l'essai échoue.

- [ ] **Step 1: Publier v0.6.1**, une fois la Tâche 5 publiée : un commit réel sur `main` du rendez-vous (par exemple le passage de ce plan en « fait »), puis, chacune seule, `git tag -a v0.6.1 -m v0.6.1`, `git push origin v0.6.1`, `deploy.sh`.

- [ ] **Step 2: 24 heures plus tard, essai sur machine jetable.** Ajouter provisoirement une étape à `install.yml` du site, et la lancer par `gh workflow run install.yml` :

```yaml
      - name: Mise à jour de 0.6.0 vers la dernière
        run: |
          sudo systemctl disable --now lprdv-update.timer lprdv || true
          sudo LPRDV_RELEASES=https://github.com/LinkPearl-Sync/linkpearl-sync-rendezvous/releases/download/v0.6.0 bash install.sh --no-firewall
          sudo /opt/lprdv/lprdv update | tee maj.txt
          grep -q 'installée (depuis 0.6.0)' maj.txt
          systemctl is-active lprdv
          test -f /opt/lprdv/lprdv.previous
```

Expected: « Version 0.6.1 installée (depuis 0.6.0). », service actif, `lprdv.previous` présent. Retirer ensuite l'étape provisoire.
