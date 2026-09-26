using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Linkpearl.Core.Transport.Rendezvous;

namespace Linkpearl.Rendezvous;

/// <summary>Ce qui sait dire si un service répond.</summary>
public interface IServiceProbe
{
    Task<ProbeResult> ProbeAsync(RendezvousAddress at, CancellationToken ct);
}

/// <summary>
/// La sonde de l'autorité : une demande d'annuaire, une réponse bien formée.
/// </summary>
/// <remarks>
/// La seule connexion sortante d'un rendez-vous hors candidature, et seul le
/// rôle d'autorité l'ouvre. Elle ne mesure ni débit ni UDP et ne porte aucune
/// donnée d'utilisateur : elle vérifie qu'un service est là, rien d'autre.
///
/// Elle ne vise jamais une adresse non publique. Les candidatures viennent
/// d'inconnus : sans ce filtre, « 127.0.0.1:47901 » ferait sonder la console
/// de l'autorité par elle-même, et une adresse privée ferait d'elle un
/// scanner du réseau qui l'héberge.
/// </remarks>
public sealed class ServiceProbe : IServiceProbe
{
    public TimeSpan Patience { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Pour les tests seulement, qui sondent un service sur la boucle locale.</summary>
    public bool AllowPrivate { get; init; }

    public async Task<ProbeResult> ProbeAsync(RendezvousAddress at, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(Patience);

        try
        {
            IPAddress[] resolved = IPAddress.TryParse(at.Host, out var literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(at.Host, deadline.Token).ConfigureAwait(false);

            var target = resolved
                .Select(address => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address)
                .FirstOrDefault(address => AllowPrivate || IsPublic(address));

            if (target is null)
                return new ProbeResult(false, null);

            using var client = new TcpClient(target.AddressFamily);
            await client.ConnectAsync(target, at.Port, deadline.Token).ConfigureAwait(false);

            var stream = client.GetStream();
            await stream.WriteAsync(
                RendezvousWire.Frame(RendezvousWire.Simple(RendezvousKind.DirectoryQuery)), deadline.Token).ConfigureAwait(false);

            var header = new byte[4];
            await stream.ReadExactlyAsync(header, deadline.Token).ConfigureAwait(false);
            var length = BinaryPrimitives.ReadInt32BigEndian(header);

            if (length is <= 0 or > RendezvousWire.MaxFrameLength)
                return new ProbeResult(false, null);

            var body = new byte[length];
            await stream.ReadExactlyAsync(body, deadline.Token).ConfigureAwait(false);

            // Un refus poli compte : le service est là et parle le protocole.
            var wellFormed = body[0] == RendezvousKind.Error
                || (body[0] == RendezvousKind.DirectoryList && RendezvousWire.TryReadDirectory(body, out _, out _));

            return wellFormed ? new ProbeResult(true, target) : new ProbeResult(false, null);
        }
        catch (Exception e) when (e is SocketException or IOException or OperationCanceledException)
        {
            return new ProbeResult(false, null);
        }
    }

    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        var bytes = address.GetAddressBytes();

        if (address.AddressFamily == AddressFamily.InterNetwork)
            return (bytes[0] is 0 or 10 or 127 or >= 224
                || (bytes[0] == 100 && (bytes[1] & 0xC0) == 64)
                || (bytes[0] == 169 && bytes[1] == 254)
                || (bytes[0] == 172 && (bytes[1] & 0xF0) == 16)
                || (bytes[0] == 192 && bytes[1] == 168)) is false;

        // En IPv6, seul l'unicast global (2000::/3) est joignable d'Internet ;
        // tout le reste est local, lien local, multicast ou réservé.
        return address.AddressFamily == AddressFamily.InterNetworkV6
            && (bytes[0] & 0xE0) == 0x20
            && address.IsIPv6Multicast is false;
    }
}
