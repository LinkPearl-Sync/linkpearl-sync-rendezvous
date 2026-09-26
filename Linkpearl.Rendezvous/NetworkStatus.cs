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
}
