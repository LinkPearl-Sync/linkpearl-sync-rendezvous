using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Linkpearl.Core.Transport.Rendezvous;

namespace Linkpearl.Rendezvous;

/// <summary>
/// L'état public du réseau, tel que la page du site le montre.
/// </summary>
/// <remarks>
/// Ce qu'il tait compte autant que ce qu'il montre. Les services écartés
/// n'y figurent nulle part. Les candidats jamais joints ne sont que comptés :
/// n'importe qui peut en inscrire un, et le site officiel afficherait sinon un
/// nom choisi par un inconnu. Ni l'adresse de soumission ni la famille ne
/// sortent du registre.
/// </remarks>
public static class NetworkStatus
{
    public const int Version = 1;

    public static byte[] Build(
        IReadOnlyList<TrackedService> services, ServiceConsensus? current, byte[] publicPoint, DateTimeOffset now)
    {
        var published = new JsonArray();

        foreach (var service in services)
        {
            var (standing, since) = service.Standing switch
            {
                ServiceStanding.Listed => ("listed", service.ListedAt),
                ServiceStanding.Probation => ("probation", service.ProbationStart),
                ServiceStanding.Delisted => ("delisted", service.DelistedAt),
                _ => (null, null),
            };

            if (standing is null)
                continue;

            var entry = new JsonObject
            {
                ["address"] = service.Address,
                ["label"] = service.Label,
                ["standing"] = standing,
            };

            if (since is { } entered)
                entry["since"] = entered.ToUnixTimeSeconds();

            if (service.LastSuccess is { } seen)
                entry["lastSeen"] = seen.ToUnixTimeSeconds();

            if (service.Availability24h is { } availability)
                entry["availability24h"] = Math.Round(availability, 2);

            if (service.Standing is ServiceStanding.Probation && service.ProbationStart is { } start)
            {
                var probation = new JsonObject { ["hours"] = (int)(now - start).TotalHours };

                if (service.Probes > 0)
                    probation["ratio"] = Math.Round((double)service.Successes / service.Probes, 2);

                entry["probation"] = probation;
            }

            published.Add(entry);
        }

        var document = new JsonObject
        {
            ["version"] = Version,
            ["generated"] = now.ToUnixTimeSeconds(),
            ["counts"] = new JsonObject
            {
                ["listed"] = services.Count(service => service.Standing is ServiceStanding.Listed),
                ["probation"] = services.Count(service => service.Standing is ServiceStanding.Probation),
                ["candidates"] = services.Count(service => service.Standing is ServiceStanding.Candidate),
                ["delisted"] = services.Count(service => service.Standing is ServiceStanding.Delisted),
            },
            ["services"] = published,
        };

        if (current is not null)
            document["authority"] = new JsonObject
            {
                ["publicKey"] = Convert.ToHexStringLower(publicPoint),
                ["listVersion"] = current.Version,
                ["issued"] = current.Issued,
                ["expires"] = current.Expires,
            };

        return Encoding.UTF8.GetBytes(document.ToJsonString());
    }

    /// <summary>La fin d'un document signé : <c>,"signature":"</c>, 128 chiffres hexadécimaux, <c>"}</c>.</summary>
    private static readonly byte[] SignaturePrefix = ",\"signature\":\""u8.ToArray();

    private const int SignatureHexLength = 128;

    /// <summary>
    /// Le document, avec en dernier champ la signature de ses propres octets par la clé de l'autorité.
    /// </summary>
    /// <remarks>
    /// L'état du réseau voyage en TCP nu, et le site le publie avec la clé de
    /// l'autorité et son empreinte : sans signature, un observateur du trajet
    /// le réécrivait à sa guise. La signature porte sur les octets exacts du
    /// document sans elle, et s'ajoute comme dernier champ : aucune forme
    /// canonique du JSON à reproduire chez qui vérifie, et un lecteur qui
    /// l'ignore lit le même document qu'avant. Même clé et même format que la
    /// liste signée (ECDSA P-256 sur SHA-256, IEEE P1363).
    /// </remarks>
    public static byte[] Sign(byte[] document, ECDsa key)
    {
        var signature = Convert.ToHexStringLower(
            key.SignData(document, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

        return [.. document.AsSpan(0, document.Length - 1), .. SignaturePrefix, .. Encoding.ASCII.GetBytes(signature), (byte)'"', (byte)'}'];
    }

    /// <summary>Vrai si l'une des clés a signé ce document ; <paramref name="document"/> est alors le document sans sa signature.</summary>
    public static bool TryVerify(byte[] signed, IEnumerable<byte[]> trustedPoints, out byte[] document)
    {
        document = [];
        var tail = SignaturePrefix.Length + SignatureHexLength + 2;

        if (signed.Length <= tail
            || signed.AsSpan(signed.Length - tail, SignaturePrefix.Length).SequenceEqual(SignaturePrefix) is false
            || signed[^2] != '"' || signed[^1] != '}')
            return false;

        byte[] signature;

        try
        {
            signature = Convert.FromHexString(Encoding.ASCII.GetString(signed, signed.Length - SignatureHexLength - 2, SignatureHexLength));
        }
        catch (FormatException)
        {
            return false;
        }

        byte[] unsigned = [.. signed.AsSpan(0, signed.Length - tail), (byte)'}'];

        foreach (var point in trustedPoints)
        {
            try
            {
                using var key = ECDsa.Create(new ECParameters
                {
                    Curve = ECCurve.NamedCurves.nistP256,
                    Q = new ECPoint { X = point[1..33], Y = point[33..] },
                });

                if (key.VerifyData(unsigned, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                {
                    document = unsigned;
                    return true;
                }
            }
            catch (CryptographicException)
            {
                // Un point hors de la courbe ne vérifie rien : on passe au suivant.
            }
        }

        return false;
    }
}
