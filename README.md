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

Le rendez-vous **n'est une autorité qu'au pairage** (hors du rôle d'autorité du cercle ouvert,
plus bas, qui ne voit toujours ni clé ni nom). L'autorisation vient du carnet de pairs
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
`--rate` borne les trames par minute et par adresse (annonces, relais, invitations, boîtes), un
/64 comptant pour une adresse en IPv6. Un `settings.json` écrit depuis la console le surcharge.

Le journal dit ce qui se passe, jamais à qui : ni adresse IP, ni fragment de jeton ou de
boîte, parce qu'un journal est un fichier qui reste, relu et copié. `--verbose` rend le
détail, adresses comprises, pour diagnostiquer une soirée ; le ticket d'invitation
n'apparaît dans aucun des deux modes.

## Héberger un serveur

```sh
curl -fsSL https://linkpearl-sync.github.io/install.sh | sudo bash -s -- --public-address rdv.exemple.org --label "Mon service"
```

Le générateur <https://linkpearl-sync.github.io/heberger.html> écrit cette ligne. Le script
télécharge la dernière release, vérifie `lprdv.sha256` avant de rien poser, installe le
binaire, `lprdv.service`, `lprdv-update.service` et `lprdv-update.timer`, et écrit les options
dans le complément `/etc/systemd/system/lprdv.service.d/options.conf`. Options : `--port`,
`--public-address`, `--label`, `--no-announce`, `--no-firewall`, `--no-auto-update`,
`--auto-update`. Il est rejouable, et relancé sans option il garde celles déjà posées. Il ne
touche jamais à `/var/lib/lprdv`. Son détail est dans le README du site.

Chaque release publie `lprdv`, `lprdv.service`, `lprdv-update.service`, `lprdv-update.timer`,
`lprdv.sha256` (qui couvre le binaire et les trois unités), ainsi que `lprdv.release.json` et
sa signature `lprdv.release.json.sig`.

## Déployer la production

```sh
LPRDV_HOST=debian@203.0.113.7 LPRDV_KEY=~/.ssh/ma_cle ./deploy/deploy.sh
```

Le script compile un binaire autonome, le pose dans `/opt/lprdv/`, installe l'unité
`deploy/lprdv.service` et son complément `deploy/production.conf` (les options de
`rdv.linkpearl.eorzea.events`), redémarre le service, puis attend que `/healthz` réponde.
L'unité reste générique, car c'est elle que publie chaque version et que chacun installe :
les options propres à un serveur vont toujours dans un complément. Il lui
faut un compte distant avec sudo, et il est rejouable : le premier passage crée le compte
système `lprdv`, et chaque passage repose le binaire, l'unité et `production.conf`.

Le service tourne sous `lprdv`, sans capacité, avec le système de fichiers en lecture seule
sauf `/var/lib/lprdv`, où vit tout l'état, et redémarre seul s'il tombe. Un service lancé à
la main dans un terminal SSH s'arrête avec lui, et personne ne le voit : c'est ce qui est
arrivé au premier déploiement public. `journalctl -u lprdv` donne le journal.

### Mise à jour automatique

Les serveurs installés par `install.sh` (site) se mettent à jour seuls : `lprdv-update.timer`
lance `lprdv update` quinze minutes après le démarrage, puis chaque heure, décalé au hasard
jusqu'à une heure. Une release n'est installée que si son manifeste
`lprdv.release.json` porte une signature d'une clé de `ReleaseKeys.cs`, et seulement 24 heures
après sa signature, sauf si le message du tag annoté porte une ligne `urgent` seule
(`git tag -a vX.Y.Z -m vX.Y.Z -m urgent`). Une version qui ne répond pas sur `/healthz` dans
les 30 secondes est défaite (retour à `lprdv.previous`) et n'est plus retentée. Retirer une release pendant les 24 heures (la supprimer, ou la
passer en pré-version) suffit à ce que personne ne l'installe. La production n'a pas de
minuteur : `deploy.sh` la met à jour, et elle essuie chaque version la première.

La ronde ne pose que `lprdv` et `lprdv.service`, les deux fichiers que couvre le manifeste :
un changement de `lprdv-update.service` ou du minuteur demande de relancer `install.sh`. Avant
de rien poser, elle vérifie que le service répond sur `http://127.0.0.1:47901/healthz` : un
service arrêté par son opérateur n'est pas relancé, et un service lancé avec `--no-admin` ou
un autre `--admin-port` n'est jamais mis à jour. Une pré-version ne porte pas de manifeste et
n'est jamais installée. Un binaire compilé sans `-p:Version` se dit `0.0.0-dev` et n'est jamais
touché, pas plus qu'une version à suffixe comme en produit `git describe` hors d'un tag.
Couper : `systemctl disable --now lprdv-update.timer`.

La clé privée vit dans le secret `LPRDV_RELEASE_KEY` de l'environnement GitHub `release`
(tags `v*` seulement) et hors ligne chez le mainteneur. Elle est distincte de `directory.key`.

## Ce qui est sur le disque

Rien de ce que le rendez-vous fait : les jetons, les attentes, les boîtes ouvertes et les
invitations déposées vivent en mémoire et disparaissent avec le processus. Redémarrer
n'efface donc aucune trace d'usage, puisqu'il n'y en a pas.

Ce qui est sur le disque, en revanche : `peers.txt` (l'annuaire, écrit par l'opérateur) et
`pending.txt` (les candidatures reçues, écrites par le service), `bans.json` pour la liste de
bannissement et son sel, `settings.json` pour les réglages changés depuis la console,
`admin.token` pour la console, et, pour une autorité, `directory.key` (sa clé de signature) et
`authority.json` (les services suivis, l'adresse d'où vient chaque candidature et les 144
dernières sondes de chacun).
Ceux que le service réécrit le sont d'un bloc, à côté puis renommés, pour qu'un arrêt du
processus laisse l'ancien contenu plutôt qu'un fichier tronqué. `admin.token` et
`directory.key` sont créés une fois, lisibles par leur seul propriétaire.

Sur un serveur mis à jour automatiquement, `/var/lib/lprdv-update` tient deux marqueurs :
`pending` (une version posée et pas encore confirmée par `/healthz`) et `refused` (la dernière
version défaite, jamais retentée). La version remplacée reste à côté, en
`/opt/lprdv/lprdv.previous` et `/etc/systemd/system/lprdv.service.previous`. Les options d'un
serveur vivent dans `/etc/systemd/system/lprdv.service.d/` (`options.conf` par `install.sh`,
`production.conf` par `deploy.sh`), que la mise à jour ne touche pas.

## La console

Une page, sur `http://127.0.0.1:47901/`, qui montre l'état du service (version, mémoire,
santé des deux boucles, connexions, relais, refus par motif), les compteurs et leur
historique sur trois minutes, une heure ou vingt-quatre heures, l'annuaire, la liste de
bannissement et les réglages, et qui permet d'approuver une candidature sans éditer un
fichier en SSH. La page ne charge aucune ressource tierce, pas même les polices du site : une
feuille venue d'ailleurs lirait le DOM de la console. Les polices servent si la machine les a,
sinon celles du système prennent le relais.

`GET /healthz` se passe de jeton, comme la liste : 200 si les boucles d'acceptation TCP et de
réflexion UDP tournent, 503 sinon, et rien d'autre dans le corps. C'est ce qu'un superviseur
regarde : un processus debout dont une boucle est morte paraît vivant et personne ne le
redémarre. Il obéit à la même règle d'adresse que le reste de la console.

```sh
lprdv --port 47900 --admin-port 47901          # console, machine locale seule
lprdv --port 47900 --admin-allow any           # console ouverte à tous, à éviter
lprdv --port 47900 --no-admin                  # pas de console du tout
```

Sans console, ou sur un autre port, ni `deploy.sh`, ni `install.sh`, ni la mise à jour
automatique ne peuvent vérifier le service.

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
celles qu'une page tierce lui ferait envoyer. Les mutations (`POST`, `PUT`, `DELETE`) sont
donc refusées par un 403 quand `Sec-Fetch-Site` dit `cross-site`, et par un 415 quand le corps
n'est pas en `application/json`, le seul type qu'un formulaire HTML ne sait pas produire. En ligne de
commande, envoyer ce `Content-Type` suffit.

## La modération

Le réseau doit pouvoir écarter quelqu'un qui s'en sert pour diffuser des contenus illégaux.
C'est le seul recours disponible : signaler à l'éditeur du jeu exposerait tous les
utilisateurs en même temps que l'auteur, les mods violant ses conditions.

Un bannissement porte **sur le personnage et non sur la clé**, qui se régénère en une
seconde là où un personnage se paie en temps et en argent. La liste ne contient jamais de
nom : seulement une empreinte lente de `nom@monde` sous un sel commun, servie à qui la demande
sur le port du service (trame `BanListQuery`, par pages de 64 entrées), et aussi par
`GET /api/bans` sur la console.

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

`GET /api/bans` et `GET /healthz` se passent de jeton, et à dessein : la liste est la même que
celle que les clients reçoivent sur le port du service, la santé est ce qu'un superviseur
interroge. Ils restent soumis à la règle d'adresse de la console (machine locale par défaut).
Tout le reste, la page comprise, exige le jeton, en `Authorization: Bearer <jeton>` pour la ligne
de commande ou en authentification basique pour le navigateur.

| Méthode et chemin | Effet |
|---|---|
| `GET /` | La page |
| `GET /healthz` | 200 si les deux boucles tournent, 503 sinon. Public |
| `GET /api/status` | Version, mémoire, santé, compteurs, refus par motif, annuaire, liste ; pour une autorité, la clé publique, la version émise et les services suivis |
| `GET /api/history?range=3m\|1h\|24h` | Un point par minute : boîtes, appariements, octets relayés, refus |
| `POST /api/peers` | Approuve une candidature |
| `DELETE /api/peers` | Retire un pair connu, ou écarte une candidature |
| `GET /api/bans` | La liste, telle que les clients la téléchargent. Public |
| `GET /api/bans/export` | La même, en téléchargement `bans.json` |
| `POST /api/bans` | Ajoute une entrée, depuis un nom, un monde (nom ou numéro) et un motif ; 409 si déjà listée |
| `POST /api/bans/verify` | Dit si un `nom@monde` est listé, sans rien écrire |
| `POST /api/bans/import` | Fusionne un `bans.json` sous le même sel, 409 sinon |
| `DELETE /api/bans` | Retire une entrée |
| `GET /api/worlds` | La table des mondes, id, nom, centre de données, région |
| `GET /api/settings` | Les réglages en vigueur et leurs bornes |
| `PUT /api/settings` | Change des réglages, à chaud et dans `settings.json` ; 400 hors bornes |
| `POST /api/authority/veto` | Écarte un service du cercle ouvert ; 404 s'il n'est pas suivi |
| `DELETE /api/authority/veto` | Rétablit un service écarté, qui repart en candidat |

## Le cercle ouvert

Avec `--directory-authority`, le service tient en plus le rôle d'autorité du cercle ouvert.
Il est désactivé par défaut : un service autohébergé n'en a pas l'usage, et le plugin
n'accepterait de toute façon que la liste signée par une clé qu'il connaît.

L'autorité sonde toutes les dix minutes les candidats reçus, jamais une
adresse non publique. Une candidature est liée à l'adresse qui l'a envoyée : le service
n'entre que s'il répond depuis cette adresse (ou ce /64), pour que personne n'inscrive le
service d'un autre. `peers.txt` reste l'annuaire manuel et n'est pas sondé. Un candidat entre dans la liste
après 72 heures à au moins 95 % de sondes réussies, en sort après 24 heures de silence,
reprend sa place s'il revient dans les 72 heures, et est oublié après 7 jours sans
réponse. Au plus deux services par /24 ou /48, et cinq admissions par jour. La liste est
signée par `directory.key` (`--directory-key`), valable sept jours, et resignée chaque
jour même inchangée ; les probations vivent dans `authority.json` (`--authority-state`).

La console montre la clé publique, la version émise, et chaque service suivi avec son
état et son taux de réussite. « Écarter » retire un service tout de suite ; l'admission se
passe de geste humain, le retrait non.

L'autorité sert, sur son port habituel et non sur la console, la liste signée
(`ConsensusQuery`) et l'état public du réseau (`NetworkStatusQuery` 0x19, réponse
`NetworkStatusPage` 0x1A, par tranches de 32 Kio et seize pages au plus). Un service ordinaire
répond qu'il ne le publie pas. Cet état, recalculé à chaque ronde de sondes, est relevé chaque
heure par le site et affiché sur <https://linkpearl-sync.github.io/reseau.html> : les services
en probation, listés ou sortis, avec leur disponibilité sur 24 heures. Jamais un service
écarté, ni l'adresse d'où vient une candidature, ni la famille ; les candidats jamais joints
n'y sont que comptés.

Tout service se porte candidat par défaut auprès de l'autorité officielle
(`rdv.linkpearl.eorzea.events`), au démarrage puis chaque jour, et retente après cinq minutes
une candidature qui n'a pas pu partir. C'est la seule connexion sortante d'un service
ordinaire. `--announce-to` remplace cette cible et peut se répéter, `--no-announce` supprime
toute candidature, `--label` donne le nom affiché (64 octets UTF-8 au plus). Sans
`--public-address`, l'autorité retient l'adresse IPv4 d'où part la candidature, avec le port
de `--port` : la candidature part donc en IPv4, seule famille qu'une adresse littérale sache
écrire, et une machine joignable seulement en IPv6 doit donner `--public-address`.

## Le format de fil est une copie

`Protocol/` contient six fichiers copiés **littéralement** depuis le dépôt du plugin :

| Ici | Là-bas |
|---|---|
| `Protocol/Core/Transport/Rendezvous/RendezvousWire.cs` | `Linkpearl/Core/Transport/Rendezvous/RendezvousWire.cs` |
| `Protocol/Core/Transport/Rendezvous/ServiceConsensus.cs` | `Linkpearl/Core/Transport/Rendezvous/ServiceConsensus.cs` |
| `Protocol/Core/Transport/Rendezvous/RendezvousTicket.cs` | `Linkpearl/Core/Transport/Rendezvous/RendezvousTicket.cs` |
| `Protocol/Core/Transport/Rendezvous/RendezvousAddress.cs` | `Linkpearl/Core/Transport/Rendezvous/RendezvousAddress.cs` |
| `Protocol/Core/Abstractions/IClock.cs` | `Linkpearl/Core/Abstractions/IClock.cs` |
| `Protocol/Core/Safety/BanList.cs` | `Linkpearl/Core/Safety/BanList.cs` |

Ils doivent rester identiques à l'octet près, et `diff` doit rester vide. De même,
`Protocol/rendezvous-vectors.json` est la copie de
`Linkpearl.Core.Tests/Fixtures/rendezvous-vectors.json`, et `RendezvousVectorTests.cs` et
`BanListTests.cs` sont copiés à l'identique de leurs pendants du plugin.

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
