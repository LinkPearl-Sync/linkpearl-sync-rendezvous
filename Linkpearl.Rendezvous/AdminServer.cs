using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Linkpearl.Core.Transport.Rendezvous;

namespace Linkpearl.Rendezvous;

/// <summary>
/// La console d'administration : une page et quelques points d'entrée JSON.
/// </summary>
/// <remarks>
/// En clair, et servie à la machine locale seule. Le chiffrement et l'exposition
/// publique sont le travail d'un proxy inverse, que tout opérateur de VPS a
/// déjà : les mettre ici demanderait de gérer des certificats et leur
/// renouvellement, pour refaire moins bien ce qu'un proxy fait mieux.
///
/// <see cref="HttpListener"/> et non ASP.NET Core : la bibliothèque standard
/// suffit pour sept cents lignes de service, là où le cadre ajouterait une pile
/// entière et des dizaines de mégaoctets au binaire autonome.
///
/// Le préfixe est sur « + », donc le port est ouvert sur toutes les interfaces,
/// et c'est <see cref="Serves"/> qui refuse ce qui ne vient pas de la machine.
/// Un préfixe lié à 127.0.0.1 semblerait plus sûr, mais HttpListener apparie ses
/// préfixes sur l'en-tête Host : il rendait alors 404 à tout proxy inverse, qui
/// passe le nom public, et même à « localhost ». Or être derrière un proxy est
/// précisément le déploiement prévu.
/// </remarks>
public sealed class AdminServer(
    bool localOnly, int adminPort, int servicePort, string token,
    RendezvousServer service, PeerDirectory directory, BanStore bans)
{
    /// <summary>La version, sans l'empreinte de commit que le SDK y accole.</summary>
    private static readonly string Version =
        (typeof(AdminServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
         ?? "inconnue").Split('+')[0];

    private const int FailuresBeforeSlowing = 5;

    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, (int Failures, DateTime Since)> _failures =
        new(StringComparer.Ordinal);

    private readonly DateTime _started = DateTime.UtcNow;

    public async Task RunAsync(CancellationToken ct)
    {
        using var listener = new HttpListener();

        listener.Prefixes.Add($"http://+:{adminPort}/");
        listener.Start();

        Console.WriteLine($"Console d'administration sur http://127.0.0.1:{adminPort}/");

        Console.WriteLine(localOnly
            ? "  Le port est ouvert sur toutes les interfaces, mais seule la machine locale est servie."
            : "  Attention : elle répond à tout le monde, et le jeton voyage en clair. Mettre un proxy avec TLS devant.");

        using var stop = ct.Register(listener.Close);

        while (ct.IsCancellationRequested is false)
        {
            HttpListenerContext context;

            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                return;
            }

            // Servie en dehors de la boucle d'acceptation : une requête lente ne
            // doit pas empêcher la suivante d'être prise.
            _ = Task.Run(() => HandleSafelyAsync(context), ct);
        }
    }

    private async Task HandleSafelyAsync(HttpListenerContext context)
    {
        try
        {
            await HandleAsync(context).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // Rien du détail ne part vers le client : un message d'exception
            // porte des chemins de fichiers et des noms de types.
            Console.WriteLine($"Console : requête en échec ({e.GetType().Name}).");

            try
            {
                Respond(context, 500, "application/json", """{"error":"erreur interne"}""");
            }
            catch (Exception)
            {
                // La réponse est peut-être déjà partie, ou la connexion fermée.
            }
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        if (Serves(localOnly, context.Request.RemoteEndPoint?.Address) is false)
        {
            Respond(context, 403, "application/json", """{"error":"console réservée à la machine locale"}""");
            return;
        }

        var path = context.Request.Url?.AbsolutePath ?? "/";
        var method = context.Request.HttpMethod;

        // Une seule chose est publique, et à dessein : la liste de
        // bannissement, que les clients téléchargent. Elle ne porte que des
        // empreintes lentes, inexploitables sans le nom. Tout le reste, la page
        // comprise, demande le jeton : une console qui s'affiche à qui la
        // demande annonce au monde ce qui tourne ici, et invite à essayer.
        var open = path is "/api/bans" && method is "GET";

        if (open is false && AdminToken.Matches(token, context.Request.Headers["Authorization"]) is false)
        {
            await SlowDownAsync(context).ConfigureAwait(false);

            // Sans cet en-tête, le navigateur affiche une page d'erreur au lieu
            // de demander le mot de passe, et la console devient inatteignable
            // autrement qu'en ligne de commande. Avec, sur un appel JSON, il
            // ouvrirait sa boîte de dialogue au milieu d'un rafraîchissement :
            // on ne le pose donc que sur une navigation.
            if (IsNavigation(context.Request))
                context.Response.AddHeader("WWW-Authenticate", "Basic realm=\"Linkpearl\", charset=\"UTF-8\"");

            Respond(context, 401, "application/json", """{"error":"jeton absent ou invalide"}""");
            return;
        }

        _failures.TryRemove(Origin(context), out _);

        switch (path, method)
        {
            case ("/", "GET"):
                Respond(context, 200, "text/html; charset=utf-8", AdminPage.Html);
                return;

            case ("/api/status", "GET"):
                Respond(context, 200, "application/json", Status());
                return;

            case ("/api/bans", "GET"):
                Respond(context, 200, "application/json", bans.Json());
                return;

            case ("/api/peers", "POST"):
            {
                var body = await BodyAsync(context).ConfigureAwait(false);
                var address = body?["address"]?.GetValue<string>() ?? "";

                var approved = directory.Approve(address);

                Respond(context, approved ? 200 : 404, "application/json", Result(approved, address));
                return;
            }

            case ("/api/peers", "DELETE"):
            {
                var body = await BodyAsync(context).ConfigureAwait(false);
                var address = body?["address"]?.GetValue<string>() ?? "";
                // Le même bouton sert aux deux listes : l'opérateur retire « ce
                // service », sans avoir à dire s'il était candidat ou connu.
                var rejected = directory.Reject(address);
                var forgotten = directory.Forget(address);
                var removed = rejected || forgotten;

                Respond(context, removed ? 200 : 404, "application/json", Result(removed, address));
                return;
            }

            case ("/api/bans", "POST"):
            {
                var body = await BodyAsync(context).ConfigureAwait(false);
                var name = body?["name"]?.GetValue<string>() ?? "";
                var world = body?["world"]?.GetValue<int>() ?? 0;
                var reason = body?["reason"]?.GetValue<string>() ?? "";

                if (name.Trim().Length is 0 || world is <= 0 or > ushort.MaxValue)
                {
                    Respond(context, 400, "application/json", """{"error":"nom ou monde manquant"}""");
                    return;
                }

                var hash = bans.Add(name, (ushort)world, reason);

                Respond(context, hash is null ? 409 : 200, "application/json",
                    hash is null
                        ? """{"error":"ce personnage figure déjà dans la liste"}"""
                        : new JsonObject { ["hash"] = hash }.ToJsonString());
                return;
            }

            case ("/api/bans", "DELETE"):
            {
                var body = await BodyAsync(context).ConfigureAwait(false);
                var hash = body?["hash"]?.GetValue<string>() ?? "";
                var removed = bans.Remove(hash);

                Respond(context, removed ? 200 : 404, "application/json", Result(removed, hash));
                return;
            }

            default:
                Respond(context, 404, "application/json", """{"error":"point d'entrée inconnu"}""");
                return;
        }
    }

    private string Status()
    {
        var counters = service.Snapshot();
        var list = bans.Current();

        var known = new JsonArray();

        foreach (var entry in directory.Known())
            known.Add(Peer(entry));

        var pending = new JsonArray();

        foreach (var entry in directory.Pending())
            pending.Add(Peer(entry));

        var banned = new JsonArray();

        foreach (var entry in list.Entries)
        {
            banned.Add(new JsonObject
            {
                ["hash"] = Convert.ToHexStringLower(entry.Hash),
                ["reason"] = entry.Reason,
                ["since"] = entry.Since,
            });
        }

        // Aucun nom de personnage ici, et ce n'est pas une précaution
        // d'affichage : le service n'en connaît aucun, il ne voit que des
        // adresses de boîte, qui sont des empreintes.
        var document = new JsonObject
        {
            ["version"] = Version,
            ["port"] = servicePort,
            ["uptimeSeconds"] = (long)(DateTime.UtcNow - _started).TotalSeconds,
            ["openMailboxes"] = counters.OpenMailboxes,
            ["pendingAnnouncements"] = counters.PendingAnnouncements,
            ["matches"] = counters.Matches,
            ["relayedBytes"] = counters.RelayedBytes,
            ["known"] = known,
            ["pending"] = pending,
            ["bans"] = banned,
        };

        return document.ToJsonString();
    }

    /// <summary>Si cette requête est une page demandée par un navigateur.</summary>
    /// <remarks>
    /// Sec-Fetch-Mode le dit sans ambiguïté sur les navigateurs récents, et
    /// l'en-tête Accept sert de repli pour les autres. Une requête en ligne de
    /// commande n'est ni l'un ni l'autre, et n'a que faire d'un défi.
    /// </remarks>
    private static bool IsNavigation(HttpListenerRequest request)
        => request.Headers["Sec-Fetch-Mode"] is "navigate"
           || request.Headers["Sec-Fetch-Mode"] is null
              && request.Headers["Accept"]?.Contains("text/html", StringComparison.Ordinal) is true;

    /// <summary>
    /// Fait attendre celui qui enchaîne les essais ratés.
    /// </summary>
    /// <remarks>
    /// Un jeton de trente-deux octets ne se devine pas, donc ce délai ne protège
    /// pas le secret : il évite qu'un proxy mal réglé ou un robot ne remplisse
    /// le journal et ne consomme la machine à raison de mille essais par
    /// seconde. Il croît avec les échecs et s'efface au premier succès.
    /// </remarks>
    private async Task SlowDownAsync(HttpListenerContext context)
    {
        var origin = Origin(context);
        var now = DateTime.UtcNow;

        var state = _failures.AddOrUpdate(
            origin,
            _ => (1, now),
            (_, previous) => now - previous.Since > FailureWindow ? (1, now) : (previous.Failures + 1, previous.Since));

        if (state.Failures <= FailuresBeforeSlowing)
            return;

        var penalty = Math.Min(state.Failures - FailuresBeforeSlowing, 10) * 500;

        await Task.Delay(penalty).ConfigureAwait(false);
    }

    private static string Origin(HttpListenerContext context)
        => context.Request.RemoteEndPoint?.Address.ToString() ?? "inconnu";

    /// <summary>Si cette adresse a droit à une réponse.</summary>
    /// <remarks>
    /// Un proxy inverse sur la même machine se présente en boucle locale, donc
    /// le déploiement prévu passe. Ce qui vient d'ailleurs est refusé avant
    /// même de lire le jeton : une console est une surface de plus, et rien
    /// n'oblige à la présenter au monde entier.
    /// </remarks>
    public static bool Serves(bool localOnly, IPAddress? remote)
    {
        if (localOnly is false)
            return true;

        if (remote is null)
            return false;

        // Une connexion IPv4 arrivée sur une prise à double pile se présente en
        // ::ffff:127.0.0.1, que IsLoopback ne reconnaît pas telle quelle.
        var address = remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote;

        return IPAddress.IsLoopback(address);
    }

    private static JsonObject Peer(DirectoryEntry entry)
        => new() { ["address"] = entry.Address, ["label"] = entry.Label };

    private static string Result(bool done, string subject)
        => new JsonObject { ["done"] = done, ["subject"] = subject }.ToJsonString();

    private static async Task<JsonObject?> BodyAsync(HttpListenerContext context)
    {
        using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
        var text = await reader.ReadToEndAsync().ConfigureAwait(false);

        try
        {
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void Respond(HttpListenerContext context, int status, string type, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);

        context.Response.StatusCode = status;
        context.Response.ContentType = type;
        context.Response.ContentLength64 = bytes.Length;
        context.Response.OutputStream.Write(bytes);
        context.Response.Close();
    }
}
