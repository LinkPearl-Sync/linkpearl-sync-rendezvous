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
/// Signé en ECDSA P-256 au format P1363, comme la liste du réseau ouvert. La
/// vérification est écrite ici plutôt qu'empruntée à ServiceConsensus : celle-ci
/// est privée, dans Protocol/, copie littérale du plugin qu'on ne retouche pas.
/// </remarks>
public sealed record ReleaseManifest(Version Version, long Signed, bool Urgent, IReadOnlyDictionary<string, string> Files)
{
    public const string FileName = "lprdv.release.json";

    public const string SignatureName = "lprdv.release.json.sig";

    /// <summary>Ce que la mise à jour pose : le binaire et son unité. Exigé dans tout manifeste.</summary>
    public static readonly string[] Payload = ["lprdv", "lprdv.service"];

    /// <summary>
    /// Les unités de la mise à jour automatique, que pose install.sh.
    /// </summary>
    /// <remarks>
    /// Elles tournent en root, et seul lprdv.sha256, non signé, les couvrait :
    /// qui remplaçait les fichiers d'une release faisait exécuter ce qu'il
    /// voulait à chaque serveur installé ensuite. Signées désormais avec le
    /// reste, et facultatives à la lecture : un manifeste d'avant ne les
    /// porte pas, et doit rester lisible.
    /// </remarks>
    public static readonly string[] UpdateUnits = ["lprdv-update.service", "lprdv-update.timer"];

    /// <summary>Tout ce que la release signe : la charge utile et les unités de mise à jour.</summary>
    public static IEnumerable<string> SignedFiles => Payload.Concat(UpdateUnits);

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

            foreach (var name in UpdateUnits)
            {
                switch (files[name])
                {
                    case null:
                        continue;

                    case JsonValue value when value.TryGetValue<string>(out var sum) && sum.Length is 64:
                        sums[name] = sum.ToLowerInvariant();
                        break;

                    default:
                        rejection = $"somme de {name} malformée";
                        return false;
                }
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
