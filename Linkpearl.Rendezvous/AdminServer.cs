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
/// En clair sur la boucle locale. Le chiffrement et l'exposition publique sont
/// le travail d'un proxy inverse, que tout opérateur de VPS a déjà : les mettre
/// ici demanderait de gérer des certificats et leur renouvellement, pour refaire
/// moins bien ce qu'un proxy fait mieux.
///
/// <see cref="HttpListener"/> et non ASP.NET Core : la bibliothèque standard
/// suffit pour sept cents lignes de service, là où le cadre ajouterait une pile
/// entière et des dizaines de mégaoctets au binaire autonome.
/// </remarks>
public sealed class AdminServer(
    string bind, int adminPort, int servicePort, string token,
    RendezvousServer service, PeerDirectory directory, BanStore bans)
{
    /// <summary>La version, sans l'empreinte de commit que le SDK y accole.</summary>
    private static readonly string Version =
        (typeof(AdminServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
         ?? "inconnue").Split('+')[0];

    private readonly DateTime _started = DateTime.UtcNow;

    public async Task RunAsync(CancellationToken ct)
    {
        using var listener = new HttpListener();

        // « + » et non « 0.0.0.0 » : c'est la forme qu'attend HttpListener pour
        // dire « toutes les interfaces ».
        listener.Prefixes.Add($"http://{(bind is "0.0.0.0" or "*" ? "+" : bind)}:{adminPort}/");
        listener.Start();

        Console.WriteLine($"Console d'administration sur http://{bind}:{adminPort}/");

        if (bind is not "127.0.0.1" and not "localhost")
            Console.WriteLine("  Attention : elle est exposée hors de la machine, et le jeton voyage en clair.");

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
        var path = context.Request.Url?.AbsolutePath ?? "/";
        var method = context.Request.HttpMethod;

        // Seuls la page et la liste sont publiques. La liste l'est à dessein :
        // c'est ce que les clients téléchargent, et elle ne porte que des
        // empreintes lentes, inexploitables sans le nom.
        var open = path is "/" && method is "GET" || path is "/api/bans" && method is "GET";

        if (open is false && AdminToken.Matches(token, context.Request.Headers["Authorization"]) is false)
        {
            Respond(context, 401, "application/json", """{"error":"jeton absent ou invalide"}""");
            return;
        }

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
