using Linkpearl.Core.Transport.Rendezvous;
using Linkpearl.Rendezvous;

// Service de rendez-vous. Il aide deux pairs à se trouver, et relaie des octets
// chiffrés quand la connexion directe échoue.
//
// Il ne voit ni clé publique, ni nom de personnage, ni manifeste, ni fichier :
// seulement des adresses IP et des jetons opaques qui tournent toutes les dix
// minutes. Voir docs/threat-model.md.

var port = 47900;
var rate = 60;
var adminPort = 47901;

for (var i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--port" && int.TryParse(args[i + 1], out var parsedPort))
        port = parsedPort;

    if (args[i] == "--rate" && int.TryParse(args[i + 1], out var parsedRate))
        rate = parsedRate;

    if (args[i] == "--admin-port" && int.TryParse(args[i + 1], out var parsedAdminPort))
        adminPort = parsedAdminPort;
}

if (args.Contains("--help"))
{
    Console.WriteLine("""
        Service de rendez-vous Linkpearl.

          lprdv [--port 47900] [--rate 60]

        --port  port TCP et UDP. 443 est un choix raisonnable pour les réseaux
                restrictifs, la charge utile étant de toute façon chiffrée de
                bout en bout par les pairs eux-mêmes.
        --rate  annonces par minute et par adresse, au-delà desquelles on refuse.

        --peers    fichier des services connus, une ligne « adresse  libellé »,
                   relu à chaud : ajouter un service est une ligne écrite, pas
                   un redémarrage.
        --pending  fichier des candidatures reçues. Les relire, et recopier
                   dans --peers celles qu'on accepte.

        --announce-to      annuaire auprès duquel se porter candidat. Répétable.
        --public-address   l'adresse sous laquelle les autres vous joignent, à
                           donner avec --announce-to.
        --label            le nom qui s'affichera dans les annuaires.

        --admin-port  port de la console d'administration. 47901 par défaut.
        --admin-bind  interface de la console. 127.0.0.1 par défaut, et il vaut
                      mieux l'y laisser : la console est en HTTP clair, donc le
                      jeton voyagerait en clair. Pour l'ouvrir, un proxy inverse
                      avec TLS devant, pas --admin-bind 0.0.0.0.
        --no-admin    n'ouvre pas de console du tout.
        --admin-token fichier du jeton. Engendré au premier démarrage, et
                      journalisé une fois : admin.token par défaut.
        --bans        fichier de la liste de bannissement. bans.json par défaut.
        """);
    return;
}

string[] ArgAll(string name)
{
    var found = new List<string>();

    for (var i = 0; i < args.Length - 1; i++)
        if (args[i] == name)
            found.Add(args[i + 1]);

    return [.. found];
}

string ArgString(string name, string fallback)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
}

// L'annuaire vit dans deux fichiers que l'opérateur écrit et relit. Rien n'est
// jamais ajouté à peers.txt par le service lui-même : une candidature attend
// dans pending.txt qu'un humain la déplace.
var directory = new PeerDirectory(ArgString("--peers", "peers.txt"), ArgString("--pending", "pending.txt"));

using var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopping.Cancel(); };

// La candidature part une fois, au démarrage, et le service n'attend rien en
// retour : c'est l'opérateur de l'annuaire qui décidera, ou pas.
foreach (var target in ArgAll("--announce-to"))
{
    if (RendezvousAddress.TryParse(target, out var to, out var why) is false)
    {
        Console.WriteLine($"--announce-to {target} ignoré : {why}");
        continue;
    }

    _ = Announcer.SubmitAsync(
        to,
        ArgString("--public-address", $"localhost:{port}"),
        ArgString("--label", ""),
        stopping.Token);
}

var service = new RendezvousServer(port, rate, directory);
var bans = new BanStore(ArgString("--bans", "bans.json"));

// La liste est chargée tout de suite, et non à la première requête : c'est ce
// qui écrit le sel au premier démarrage, et un sel qui naîtrait plus tard
// invaliderait les empreintes déjà publiées.
_ = bans.Current();

var running = new List<Task> { service.RunAsync(stopping.Token) };

if (args.Contains("--no-admin") is false)
{
    var token = AdminToken.LoadOrCreate(ArgString("--admin-token", "admin.token"));
    var admin = new AdminServer(
        ArgString("--admin-bind", "127.0.0.1"), adminPort, port, token, service, directory, bans);

    running.Add(admin.RunAsync(stopping.Token));
}

try
{
    // WhenAny et non WhenAll : si la console tombe, le service doit s'arrêter
    // aussi, plutôt que de continuer sans que personne puisse l'observer.
    await await Task.WhenAny(running);
}
catch (OperationCanceledException)
{
    Console.WriteLine("Arrêt.");
}
