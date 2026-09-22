# Linkpearl, service de rendez-vous

Le seul service de [Linkpearl](https://github.com/LinkPearl-Sync/linkpearl), la
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

Le rendez-vous **n'est pas une autorité**. L'autorisation vient du carnet de pairs local de
chaque joueur, et la clé publique vient du code d'invitation. Un rendez-vous malveillant
peut faire échouer une connexion, jamais usurper une identité.

## Lancer

```sh
dotnet run --project Linkpearl.Rendezvous -- --port 47900 --rate 60
dotnet test Linkpearl.Rendezvous.Tests/Linkpearl.Rendezvous.Tests.csproj
```

`--port` sert en TCP et en UDP. 443 est un choix raisonnable pour les réseaux restrictifs,
la charge utile étant de toute façon chiffrée de bout en bout par les pairs eux-mêmes.
`--rate` borne les annonces par minute et par adresse.

## La console

Une page, sur `http://127.0.0.1:47901/`, qui montre les compteurs, l'annuaire et la liste de
bannissement, et qui permet d'approuver une candidature sans éditer un fichier en SSH.

```sh
lprdv --port 47900 --admin-port 47901          # console, machine locale seule
lprdv --port 47900 --admin-allow any           # console ouverte à tous, à éviter
lprdv --port 47900 --no-admin                  # pas de console du tout
```

Au premier démarrage, le service écrit un jeton aléatoire dans `admin.token` et le
journalise une fois. C'est lui que la page demande. Le perdre se répare en supprimant le
fichier.

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

`GET /api/bans` est public à dessein : c'est ce que les clients téléchargent. Tout le reste
exige l'en-tête `Authorization: Bearer <jeton>`.

| Méthode et chemin | Effet |
|---|---|
| `GET /` | La page |
| `GET /api/status` | Les compteurs et l'annuaire |
| `POST /api/peers` | Approuve une candidature |
| `DELETE /api/peers` | Retire un pair connu, ou écarte une candidature |
| `GET /api/bans` | La liste, telle que les clients la téléchargent |
| `POST /api/bans` | Ajoute une entrée, depuis un nom, un monde et un motif |
| `DELETE /api/bans` | Retire une entrée |

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
