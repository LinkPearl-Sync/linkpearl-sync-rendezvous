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

for (var i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--port" && int.TryParse(args[i + 1], out var parsedPort))
        port = parsedPort;

    if (args[i] == "--rate" && int.TryParse(args[i + 1], out var parsedRate))
        rate = parsedRate;
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

try
{
    await new RendezvousServer(port, rate, directory).RunAsync(stopping.Token);
}
catch (OperationCanceledException)
{
    Console.WriteLine("Arrêt.");
}
