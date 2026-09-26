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
/// a reçu. Une autorité du cercle ouvert en ouvre d'autres, vers les candidats
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
    /// Se porte candidat tout de suite, puis à chaque <paramref name="interval"/>,
    /// jusqu'à l'arrêt du service.
    /// </summary>
    public static async Task SubmitEveryAsync(
        RendezvousAddress to, string self, string label, TimeSpan interval, CancellationToken ct)
    {
        while (ct.IsCancellationRequested is false)
        {
            await SubmitAsync(to, self, label, ct).ConfigureAwait(false);

            try
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

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
