using System.Net;
using System.Net.Sockets;

namespace Linkpearl.Rendezvous;

/// <summary>
/// La clé sous laquelle le limiteur et les plafonds comptent une adresse.
/// </summary>
/// <remarks>
/// Un abonné IPv6 reçoit un /64 entier, parfois un /56 : compter par adresse
/// lui donnerait des trillions de compteurs à lui seul, et un limiteur qu'on
/// contourne en changeant le dernier octet ne limite rien. Le /64 est le
/// préfixe qu'un particulier ne peut pas dépasser sans changer d'abonnement.
/// En IPv4, l'adresse elle-même reste la bonne unité : un NAT de foyer en a
/// une, et un /24 mettrait des centaines de foyers dans le même seau.
/// </remarks>
public static class AddressBucket
{
    public static string Of(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (address.AddressFamily is not AddressFamily.InterNetworkV6)
            return address.ToString();

        Span<byte> bytes = stackalloc byte[16];
        address.TryWriteBytes(bytes, out _);
        bytes[8..].Clear();

        return $"{new IPAddress(bytes)}/64";
    }

    /// <summary>
    /// Le préfixe large d'une adresse IPv6, son /48 ; nul en IPv4.
    /// </summary>
    /// <remarks>
    /// Un /64 par abonné rend le seau ordinaire juste pour un particulier,
    /// mais un hébergeur donne souvent un /48 entier, soit 65 536 /64 : un
    /// seul acteur y prenait autant de seaux qu'il voulait, et occupait
    /// toutes les places du service à lui seul. Le /48 est le préfixe qu'un
    /// registre attribue à un site ; au-delà, il faut un second réseau. Rien
    /// d'équivalent en IPv4, où un /24 réunit des centaines de foyers
    /// derrière un même opérateur.
    /// </remarks>
    public static string? Wide(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (address.AddressFamily is not AddressFamily.InterNetworkV6)
            return null;

        Span<byte> bytes = stackalloc byte[16];
        address.TryWriteBytes(bytes, out _);
        bytes[6..].Clear();

        return $"{new IPAddress(bytes)}/48";
    }
}
