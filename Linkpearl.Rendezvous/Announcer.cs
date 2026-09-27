using System.Net;
using System.Net.Sockets;
using Linkpearl.Core.Transport.Rendezvous;

namespace Linkpearl.Rendezvous;

/// <summary>
/// La candidature d'un service auprès d'un annuaire.
/// </summary>
/// <remarks>
/// La seule connexion sortante d'un service ordinaire : une candidature au
/// démarrage, puis une par jour. Hors de là il ne consulte jamais un autre
/// annuaire, ne vérifie jamais un autre service, et ne propage jamais ce qu'il
/// a reçu. Une autorité du réseau ouvert en ouvre d'autres, vers les candidats
/// qu'elle sonde (voir <see cref="ServiceProbe"/>).
///
/// Renvoyée chaque jour parce qu'une autorité oublie un service resté
/// injoignable une semaine, et qu'une file pleine évince les plus anciennes
/// candidatures : sans cela, il faudrait redémarrer le service pour qu'il se
/// représente. Une fois par jour ne remplit aucun journal, et l'annuaire en
/// refuse de toute façon plus d'une par heure et par adresse.
/// </remarks>
public static class Announcer
{
    /// <summary>Intervalle entre deux candidatures.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    /// <summary>
    /// Délai avant de retenter une candidature qui n'est pas partie.
    /// </summary>
    /// <remarks>
    /// Une autorité se présente à elle-même avant d'écouter, et un service
    /// démarre parfois avant son réseau : attendre l'intervalle entier le
    /// tiendrait hors du réseau un jour de plus. Cinq minutes ne pèsent sur
    /// personne, l'annuaire n'étant de toute façon pas joint.
    /// </remarks>
    public static readonly TimeSpan Retry = TimeSpan.FromMinutes(5);

    /// <summary>L'autorité du réseau ouvert, celle que le plugin connaît.</summary>
    public const string OfficialAuthority = "rdv.linkpearl.eorzea.events";

    /// <summary>
    /// Les annuaires auprès desquels se présenter.
    /// </summary>
    /// <remarks>
    /// Par défaut, l'autorité officielle : un service qui tourne sert à tous, et
    /// le réseau ouvert ne grandit que si l'on n'a rien à faire pour y entrer.
    /// Des annuaires donnés la remplacent, et le refus l'emporte sur tout.
    /// </remarks>
    public static IReadOnlyList<string> Targets(IReadOnlyList<string> given, bool optOut)
        => optOut ? [] : given.Count > 0 ? given : [OfficialAuthority];

    /// <summary>
    /// L'hôte d'une candidature qui laisse l'annuaire retenir l'adresse d'envoi.
    /// </summary>
    /// <remarks>
    /// C'était déjà ce qui partait sans <c>--public-address</c>, et personne ne
    /// fait joindre son service par un autre sous ce nom-là : les services
    /// déjà déployés en profitent sans mise à jour.
    /// </remarks>
    public const string Unspecified = "localhost";

    /// <summary>
    /// L'adresse retenue pour une candidature reçue de <paramref name="sender"/>.
    /// </summary>
    /// <remarks>
    /// Seule une adresse IPv4 remplace l'hôte laissé en blanc : une adresse
    /// IPv6 littérale ne s'écrit pas dans une <see cref="RendezvousAddress"/>.
    /// C'est pourquoi une telle candidature part en IPv4.
    /// </remarks>
    public static DirectoryEntry Resolve(DirectoryEntry entry, IPAddress sender)
    {
        if (RendezvousAddress.TryParse(entry.Address, out var address, out _) is false
            || IsUnspecified(address.Host) is false)
            return entry;

        if (sender.IsIPv4MappedToIPv6)
            sender = sender.MapToIPv4();

        return sender.AddressFamily is AddressFamily.InterNetwork
            ? entry with { Address = $"{sender}:{address.Port}" }
            : entry;
    }

    private static bool IsUnspecified(string host)
        => host.Equals(Unspecified, StringComparison.OrdinalIgnoreCase)
           || IPAddress.TryParse(host, out var literal) && (IPAddress.IsLoopback(literal) || literal.Equals(IPAddress.Any));

    /// <summary>
    /// Se porte candidat tout de suite, puis à chaque <paramref name="interval"/>,
    /// jusqu'à l'arrêt du service.
    /// </summary>
    public static async Task SubmitEveryAsync(
        RendezvousAddress to, string self, string label, TimeSpan interval, CancellationToken ct,
        TimeSpan? retry = null)
    {
        while (ct.IsCancellationRequested is false)
        {
            var sent = await SubmitAsync(to, self, label, ct).ConfigureAwait(false);

            try
            {
                await Task.Delay(sent ? interval : retry ?? Retry, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <returns>Vrai si la candidature est partie.</returns>
    public static async Task<bool> SubmitAsync(
        RendezvousAddress to, string self, string label, CancellationToken ct)
    {
        try
        {
            // Laissée en blanc, l'adresse sera lue sur la connexion : elle doit
            // donc partir en IPv4, la seule que l'annuaire sache écrire.
            using var client = RendezvousAddress.TryParse(self, out var own, out _) && IsUnspecified(own.Host)
                ? new TcpClient(AddressFamily.InterNetwork)
                : new TcpClient();
            await client.ConnectAsync(to.Host, to.Port, ct).ConfigureAwait(false);

            var frame = RendezvousWire.Frame(RendezvousWire.DirectorySubmit(self, label));
            await client.GetStream().WriteAsync(frame, ct).ConfigureAwait(false);

            // Rien à lire en retour : l'annuaire ne répond pas, pour que cette
            // trame ne serve pas à sonder sa file d'attente.
            Console.WriteLine($"Candidature déposée auprès de {to}.");
            return true;
        }
        catch (Exception e)
        {
            Console.WriteLine($"Candidature auprès de {to} impossible : {e.Message}");
            return false;
        }
    }
}
