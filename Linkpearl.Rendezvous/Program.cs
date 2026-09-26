using Linkpearl.Core.Transport.Rendezvous;
using Linkpearl.Rendezvous;

// Service de rendez-vous. Il aide deux pairs à se trouver, et relaie des octets
// chiffrés quand la connexion directe échoue.
//
// Il ne voit ni clé publique, ni nom de personnage, ni manifeste, ni fichier :
// seulement des adresses IP et des jetons opaques qui tournent toutes les dix
// minutes. Ce qu'il apprend et ce qu'il refuse d'apprendre sont écrits dans le
// README (« Ce qu'il ne voit pas ») et dans la conception du plugin,
// docs/superpowers/specs/2026-09-22-linkpearl-design.md dans son dépôt.

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
        --verbose  journal détaillé, adresses et fragments de jetons compris.
                   Par défaut le journal ne dit que ce qui se passe, jamais à qui.

        --peers    fichier des services connus, une ligne « adresse  libellé »,
                   relu à chaud : ajouter un service est une ligne écrite, pas
                   un redémarrage.
        --pending  fichier des candidatures reçues. Les relire, et recopier
                   dans --peers celles qu'on accepte.

        --announce-to      annuaire auprès duquel se porter candidat, au démarrage
                           puis chaque jour. Répétable.
        --public-address   l'adresse sous laquelle les autres vous joignent, à
                           donner avec --announce-to.
        --label            le nom qui s'affichera dans les annuaires.

        --directory-authority  tient le rôle d'autorité du cercle ouvert : sonde
                               les candidats, signe et sert la liste. Désactivé
                               par défaut.
        --directory-key        clé de signature de l'autorité (directory.key).
                               Engendrée au premier démarrage ; ne jamais
                               l'écraser ni la régénérer.
        --authority-state      registre des probations (authority.json).

        --admin-port  port de la console d'administration. 47901 par défaut.
        --admin-allow qui la console sert. « local » par défaut, et il vaut mieux
                      l'y laisser : elle est en HTTP clair, donc le jeton
                      voyagerait en clair. Pour l'ouvrir, un proxy inverse avec
                      TLS sur la même machine, pas --admin-allow any.
                      Un proxy local passe, quel que soit le nom qu'il présente.
        --no-admin    n'ouvre pas de console du tout.
        --admin-token fichier du jeton. Engendré au premier démarrage, à lire
                      dans le fichier : admin.token par défaut.
        --bans        fichier de la liste de bannissement. bans.json par défaut.
        --settings    fichier des réglages changés depuis la console. settings.json
                      par défaut ; s'il existe, il surcharge --rate et les plafonds.
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
var clock = new SystemClock();
var directory = new PeerDirectory(ArgString("--peers", "peers.txt"), ArgString("--pending", "pending.txt"), clock);

using var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopping.Cancel(); };

// La candidature part au démarrage puis chaque jour, et le service n'attend
// rien en retour : c'est l'annuaire, ou son autorité, qui décidera.
foreach (var target in ArgAll("--announce-to"))
{
    if (RendezvousAddress.TryParse(target, out var to, out var why) is false)
    {
        Console.WriteLine($"--announce-to {target} ignoré : {why}");
        continue;
    }

    _ = Announcer.SubmitEveryAsync(
        to,
        ArgString("--public-address", $"localhost:{port}"),
        ArgString("--label", ""),
        Announcer.Interval,
        stopping.Token);
}

// La ligne de commande donne le départ ; settings.json, écrit depuis la
// console, la surcharge s'il existe. Sans fichier, rien ne change.
var settings = new SettingsStore(ArgString("--settings", "settings.json"));
var limits = settings.Load(new RendezvousLimits { AnnouncementsPerMinute = rate }, Console.Out);
var bans = new BanStore(ArgString("--bans", "bans.json"));

// L'autorité n'est qu'un rôle de ce même service, désactivé par défaut : un
// service autohébergé n'en a pas l'usage, et le plugin n'accepterait de toute
// façon que la liste signée par une clé qu'il connaît.
AuthorityService? authority = null;

if (args.Contains("--directory-authority"))
{
    var key = DirectoryKey.LoadOrCreate(ArgString("--directory-key", "directory.key"));
    var ledger = AuthorityLedger.Load(ArgString("--authority-state", "authority.json"), clock);
    authority = new AuthorityService(ledger, new ServiceProbe(), key, directory, clock);
    Console.WriteLine($"Autorité du cercle ouvert, clé publique {Convert.ToHexStringLower(authority.PublicPoint)}.");
}

var service = new RendezvousServer(port, directory, limits, clock, verbose: args.Contains("--verbose"))
{
    Bans = bans,
    Consensus = authority,
};

// La liste est chargée tout de suite, et non à la première requête : c'est ce
// qui écrit le sel au premier démarrage, et un sel qui naîtrait plus tard
// invaliderait les empreintes déjà publiées.
_ = bans.Current();

var running = new List<Task> { service.RunAsync(stopping.Token) };

if (authority is not null)
    running.Add(authority.RunAsync(stopping.Token));

if (args.Contains("--no-admin") is false)
{
    var token = AdminToken.LoadOrCreate(ArgString("--admin-token", "admin.token"));
    var admin = new AdminServer(
        ArgString("--admin-allow", "local") is not "any", adminPort, port, token, service, directory, bans, settings, clock);

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
