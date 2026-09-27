using System.Net;

namespace Linkpearl.Rendezvous;

/// <summary>Le continent d'une adresse de service, ou null.</summary>
/// <remarks>
/// Déduit par l'autorité, jamais déclaré par l'opérateur : un service qui se
/// dirait seul dans une région creuse y prendrait tous les relais.
/// </remarks>
public interface IRegionLookup
{
    string? RegionOf(IPAddress address);
}
