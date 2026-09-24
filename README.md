# Linkpearl, service de rendez-vous

Le seul service de [Linkpearl](https://github.com/LinkPearl-Sync/linkpearl-sync-plugin), la
synchronisation pair à pair de l'apparence moddée dans Final Fantasy XIV.

Il aide deux pairs à se trouver, et relaie des octets chiffrés quand la connexion directe
échoue. **Il ne stocke ni ne redistribue le moindre fichier de mod.**

## Ce qu'il ne voit pas

Ni clé publique, ni nom de personnage, ni manifeste, ni fichier. Les pairs s'y annoncent
sous un jeton opaque dérivé d'un secret que le serveur ne connaît pas, et qui change toutes
les dix minutes. Un observateur ne peut pas relier deux fenêtres entre elles, donc pas
reconstituer un graphe de relations ni des horaires de présence.

Ce qu'il voit, et qui est irréductible sans relais systématique : les adresses IP, et quels
noms de personnage sont en ligne, une adresse de boîte dérivant du nom. C'est dit ici plutôt
que passé sous silence.

Le rendez-vous **n'est une autorité qu'au pairage**. L'autorisation vient du carnet de pairs
local de chaque joueur, mais la clé publique d'un pair y arrive par la boîte aux lettres du
service, en clair : un opérateur malveillant peut s'intercaler au premier contact, sans que
personne s'en aperçoive. Une fois la clé épinglée dans le carnet, il peut faire échouer une
connexion, jamais usurper une identité. Le modèle de confiance complet est dans
[`docs/protocol.md`](https://github.com/LinkPearl-Sync/linkpearl-sync-plugin/blob/main/docs/protocol.md)
du plugin.

## Lancer

```sh
dotnet run --project Linkpearl.Rendezvous -- --port 47900 --rate 60
dotnet test Linkpearl.Rendezvous.Tests/Linkpearl.Rendezvous.Tests.csproj
```

`--port` sert en TCP et en UDP. 443 est un choix raisonnable pour les réseaux restrictifs,
la charge utile étant de toute façon chiffrée de bout en bout par les pairs eux-mêmes.
`--rate` borne les annonces par minute et par adresse, un /64 comptant pour une adresse en
IPv6.

Le journal dit ce qui se passe, jamais à qui : ni adresse IP, ni fragment de jeton ou de
boîte, parce qu'un journal est un fichier qui reste, relu et copié. `--verbose` rend le
détail, adresses comprises, pour diagnostiquer une soirée ; le ticket d'invitation
n'apparaît dans aucun des deux modes.

## Déployer

```sh
LPRDV_HOST=debian@203.0.113.7 LPRDV_KEY=~/.ssh/ma_cle ./deploy/deploy.sh
```

Le script compile un binaire autonome, le pose dans `/opt/lprdv/`, installe l'unité
`deploy/lprdv.service` et redémarre le service, puis attend que `/healthz` réponde. Il lui
faut un compte distant avec sudo, et il est rejouable : le premier passage crée le compte
système `lprdv`, les suivants ne remplacent que le binaire.

Le service tourne sous `lprdv`, sans capacité, avec le système de fichiers en lecture seule
sauf `/var/lib/lprdv`, où vit tout l'état, et redémarre seul s'il tombe. Un service lancé à
la main dans un terminal SSH s'arrête avec lui, et personne ne le voit : c'est ce qui est
arrivé au premier déploiement public. `journalctl -u lprdv` donne le journal.

## Ce qui est sur le disque

Rien de ce que le rendez-vous fait : les jetons, les attentes, les boîtes ouvertes et les
invitations déposées vivent en mémoire et disparaissent avec le processus. Redémarrer
n'efface donc aucune trace d'usage, puisqu'il n'y en a pas.

Ce que l'opérateur configure, en revanche, est sur le disque : `peers.txt` et `pending.txt`
pour l'annuaire, `bans.json` pour la liste de bannissement, `admin.token` pour la console.
Ces fichiers se réécrivent d'un bloc, à côté puis renommés, pour qu'une coupure laisse
l'ancien contenu plutôt qu'un fichier tronqué. Le jeton est créé lisible par son seul
propriétaire.

## La console

Une page, sur `http://127.0.0.1:47901/`, qui montre l'état du service (version, mémoire,
santé des deux boucles, connexions, relais, refus par motif), les compteurs et leur
historique sur trois minutes, une heure ou vingt-quatre heures, l'annuaire, la liste de
bannissement et les réglages, et qui permet d'approuver une candidature sans éditer un
fichier en SSH.

`GET /healthz` est public, comme la liste : 200 si les boucles d'acceptation TCP et de
réflexion UDP tournent, 503 sinon, et rien d'autre dans le corps. C'est ce qu'un superviseur
regarde : un processus debout dont une boucle est morte paraît vivant et personne ne le
redémarre. Il obéit à la même règle d'adresse que le reste de la console.

```sh
lprdv --port 47900 --admin-port 47901          # console, machine locale seule
lprdv --port 47900 --admin-allow any           # console ouverte à tous, à éviter
lprdv --port 47900 --no-admin                  # pas de console du tout
```

Au premier démarrage, le service écrit un jeton aléatoire dans `admin.token` et journalise
le chemin du fichier, jamais le jeton lui-même : c'est là qu'on le lit. Le navigateur le
demande avant d'afficher quoi que ce soit, dans sa
propre boîte de dialogue : n'importe quel identifiant, et le jeton comme mot de passe. Il le
renvoie ensuite tout seul, donc la page n'a ni champ à remplir ni secret à stocker. Le perdre
se répare en supprimant le fichier.

**La page elle-même demande le jeton**, et pas seulement les données qu'elle affiche : une
console qui s'ouvre à qui la demande annonce ce qui tourne ici et invite à essayer. Un jeton
de trente-deux octets ne se devine pas, mais les essais répétés depuis une même adresse sont
freinés, pour qu'un robot ne remplisse pas le journal.

**La console est en HTTP clair, et ne sert que la machine locale.** Pour l'ouvrir depuis
l'extérieur, mettre un proxy inverse avec TLS sur la même machine, pas `--admin-allow any` :
sans TLS, le jeton voyagerait en clair. Gérer des certificats ici reviendrait à refaire moins
bien ce qu'un proxy fait déjà.

Le port est ouvert sur toutes les interfaces et c'est le service qui refuse, par un 403, tout
ce qui n'arrive pas de la boucle locale. Ce n'est pas le réglage qu'on aimerait écrire : un
préfixe `HttpListener` lié à `127.0.0.1` n'apparie que les requêtes dont l'en-tête `Host` vaut
littéralement `127.0.0.1`, et rendait donc 404 à tout proxy inverse, qui passe le nom public,
et même à `localhost`. Or être derrière un proxy est le déploiement prévu. Un pare-feu sur le
port de la console reste utile à qui veut la ceinture et les bretelles.

Aucun nom de personnage n'apparaît nulle part sur cette page, et ce n'est pas une précaution
d'affichage : le service n'en connaît aucun.

Le navigateur rejoue l'authentification basique sur toute requête vers la console, y compris
celles qu'une page tierce lui ferait envoyer. Les mutations (`POST`, `DELETE`) sont donc
refusées quand `Sec-Fetch-Site` dit `cross-site`, et quand le corps n'est pas en
`application/json`, le seul type qu'un formulaire HTML ne sait pas produire. En ligne de
commande, envoyer ce `Content-Type` suffit.

## La modération

Le réseau doit pouvoir écarter quelqu'un qui s'en sert pour diffuser des contenus illégaux.
C'est le seul recours disponible : signaler à l'éditeur du jeu exposerait tous les
utilisateurs en même temps que l'auteur, les mods violant ses conditions.

Un bannissement porte **sur le personnage et non sur la clé**, qui se régénère en une
seconde là où un personnage se paie en temps et en argent. La liste ne contient jamais de
nom : seulement une empreinte lente de `nom@monde` sous un sel commun, servie par
`GET /api/bans`, que n'importe qui peut télécharger.

```json
{
  "version": 1,
  "kdf": { "algorithm": "pbkdf2-sha256", "iterations": 600000 },
  "salt": "8d7f0768...",
  "updated": 1790119641,
  "entries": [ { "hash": "3b69f39f...", "reason": "contenu illegal", "since": 1790119641 } ]
}
```

Lente parce qu'une liste d'empreintes rapides serait un annuaire de joueurs utilisant des
mods, ce qui les exposerait pour une raison étrangère à ce qu'on leur reproche. Vérifier
quelqu'un dont on a le nom coûte une dérivation ; énumérer l'espace des noms coûte une
fortune. Le sel est engendré une fois et ne change jamais : en changer invaliderait toute la
liste, qu'il faudrait reconstruire depuis des noms que le service ne conserve pas.

Trois limites, écrites plutôt que laissées à croire résolues :

- **La liste n'est pas une preuve.** Le service ne voit aucun contenu, donc il ne peut
  vérifier aucune accusation. Toute liste relève de la réputation, et celui qui l'applique
  en répond.
- **Un banni continue d'ouvrir ses boîtes.** Une adresse de boîte est une empreinte rapide
  du nom, la liste porte une empreinte lente : les deux ne se comparent pas. Ce qui l'arrête
  est côté client, qui refuse de se pairer avec lui et de poser son apparence. C'est là que
  le mal se produit, donc c'est là que la protection vit.
- **PBKDF2 et non argon2.** Tout ce qui est cryptographique dans ce projet passe par la
  bibliothèque standard, sans dépendance, et le client doit dériver à l'identique depuis un
  autre dépôt. Le prix est que PBKDF2 se calcule bien sur processeur graphique, là où argon2
  y résisterait par sa consommation mémoire.

Le monde se donne par son nom (« Ragnarok ») ou par son identifiant numérique. La table des
noms est embarquée dans le service, tirée de la feuille `World` du jeu, et se maintient à la
main à l'ouverture d'un monde : l'identifiant reste accepté pour un monde qu'elle ne connaît
pas encore. « Vérifier » dérive l'empreinte d'un nom et dit si elle est listée, sans rien
écrire ni journaliser. Une liste exportée par un autre service se fusionne par empreinte,
à condition qu'il partage le même sel : sinon ses empreintes ne se comparent pas aux nôtres,
et l'import est refusé par un 409 qui le dit.

`GET /api/bans` et `GET /healthz` sont les seules choses publiques, et à dessein : la liste
est ce que les clients téléchargent, la santé ce qu'un superviseur interroge. Tout le
reste, la page comprise, exige le jeton, en `Authorization: Bearer <jeton>` pour la ligne
de commande ou en authentification basique pour le navigateur.

| Méthode et chemin | Effet |
|---|---|
| `GET /` | La page |
| `GET /healthz` | 200 si les deux boucles tournent, 503 sinon. Public |
| `GET /api/status` | Version, mémoire, santé, compteurs, refus par motif, annuaire, liste |
| `GET /api/history?range=3m\|1h\|24h` | Un point par minute : boîtes, appariements, octets relayés, refus |
| `POST /api/peers` | Approuve une candidature |
| `DELETE /api/peers` | Retire un pair connu, ou écarte une candidature |
| `GET /api/bans` | La liste, telle que les clients la téléchargent. Public |
| `GET /api/bans/export` | La même, en téléchargement `bans.json` |
| `POST /api/bans` | Ajoute une entrée, depuis un nom, un monde (nom ou numéro) et un motif |
| `POST /api/bans/verify` | Dit si un `nom@monde` est listé, sans rien écrire |
| `POST /api/bans/import` | Fusionne un `bans.json` sous le même sel, 409 sinon |
| `DELETE /api/bans` | Retire une entrée |
| `GET /api/worlds` | La table des mondes, id, nom, centre de données, région |
| `GET /api/settings` | Les réglages en vigueur et leurs bornes |
| `PUT /api/settings` | Change des réglages, à chaud et dans `settings.json` ; 400 hors bornes |

## Le format de fil est une copie

`Protocol/` contient cinq fichiers copiés **littéralement** depuis le dépôt du plugin :

| Ici | Là-bas |
|---|---|
| `Protocol/Core/Transport/Rendezvous/RendezvousWire.cs` | `Linkpearl/Core/Transport/Rendezvous/RendezvousWire.cs` |
| `Protocol/Core/Transport/Rendezvous/RendezvousTicket.cs` | `Linkpearl/Core/Transport/Rendezvous/RendezvousTicket.cs` |
| `Protocol/Core/Transport/Rendezvous/RendezvousAddress.cs` | `Linkpearl/Core/Transport/Rendezvous/RendezvousAddress.cs` |
| `Protocol/Core/Abstractions/IClock.cs` | `Linkpearl/Core/Abstractions/IClock.cs` |
| `Protocol/Core/Safety/BanList.cs` | `Linkpearl/Core/Safety/BanList.cs` |

Ils doivent rester identiques à l'octet près, et `diff` doit rester vide.

Deux copies divergent toujours un jour, et la panne qui s'ensuit est de celles qui coûtent
une soirée : deux joueurs qui ne se voient plus, sans message d'erreur qui dise pourquoi.
C'est pourquoi `Protocol/rendezvous-vectors.json` existe. Le fichier est le même dans les
deux dépôts, le test qui le lit est le même aussi, et le premier côté qui touche à un octet
du format casse son propre test avant d'avoir livré quoi que ce soit.

Ce que ces vecteurs attestent, et rien de plus : la cohérence de format entre les deux
copies. Ils ont été produits par l'implémentation qu'ils testent, donc ils n'attrapent pas
une erreur de conception, seulement une dérive.

La dérivation du jeton tournant, elle, est l'affaire du plugin et y reste testée. Le serveur
ne lit de `RendezvousTicket` que sa taille et sa fenêtre.
