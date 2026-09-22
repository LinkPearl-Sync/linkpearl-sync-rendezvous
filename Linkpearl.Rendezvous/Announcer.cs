using System.Net.Sockets;
using Linkpearl.Core.Transport.Rendezvous;

namespace Linkpearl.Rendezvous;

/// <summary>
/// La candidature d'un service auprès d'un annuaire.
/// </summary>
/// <remarks>
/// La seule connexion sortante qu'un service ouvre de lui-même, une fois au
/// démarrage. Hors de là il ne consulte jamais un autre annuaire, ne vérifie
/// jamais un autre service, et ne propage jamais ce qu'il a reçu.
///
/// L'échec est silencieux et sans reprise : l'annuaire est peut-être éteint, ou
/// son opérateur peut ne pas vouloir de nous. Insister ne changerait rien et
/// remplirait son journal.
/// </remarks>
public static class Announcer
{
    public static async Task SubmitAsync(
        RendezvousAddress to, string self, string label, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(to.Host, to.Port, ct).ConfigureAwait(false);

            var frame = RendezvousWire.Frame(RendezvousWire.DirectorySubmit(self, label));
            await client.GetStream().WriteAsync(frame, ct).ConfigureAwait(false);

            // Rien à lire en retour : l'annuaire ne répond pas, pour que cette
            // trame ne serve pas à sonder sa file d'attente.
            Console.WriteLine($"Candidature déposée auprès de {to}.");
        }
        catch (Exception e)
        {
            Console.WriteLine($"Candidature auprès de {to} impossible : {e.Message}");
        }
    }
}
