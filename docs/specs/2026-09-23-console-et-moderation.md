# Console d'administration et modération

Écrit le 23 septembre 2026. Conception de deux sous-systèmes du service de
rendez-vous, à lire après `docs/superpowers/specs/2026-09-22-federation-design.md`
du dépôt du plugin, dont elle applique les décisions de modération déjà prises.

## Ce que cela sert

**La console.** Un premier essai à deux approche. Quand il échouera, et il
échouera, la question sera de savoir si la cause est le NAT, le pairage, ou
autre chose. Aujourd'hui le service ne dit rien : il tient des compteurs que
personne ne lit. Les exposer transforme un échec muet en un diagnostic.

**La modération.** Le réseau doit pouvoir écarter quelqu'un qui s'en sert pour
diffuser des contenus illégaux. C'est le seul recours disponible : signaler à
l'éditeur du jeu exposerait tous les utilisateurs en même temps que l'auteur,
les mods violant ses conditions.

## Décisions arrêtées

### La console est du HTTP en clair, sur la boucle locale

`lprdv` n'écoute par défaut que sur `127.0.0.1`. Le chiffrement et l'exposition
publique sont le travail d'un proxy inverse, que tout opérateur de VPS a déjà.

La raison est étroite : sans TLS, le jeton d'administration voyagerait en clair.
Le mettre dans le service demanderait d'y gérer des certificats et leur
renouvellement, pour refaire moins bien ce qu'un proxy fait déjà.

`--admin-allow any` reste possible, et c'est alors un choix explicite de
l'opérateur, que la documentation déconseille.

**Corrigé à la mise en œuvre.** « N'écouter que sur `127.0.0.1` » s'est révélé
irréalisable tel quel : `HttpListener` apparie ses préfixes sur l'en-tête `Host`,
donc un préfixe lié à `127.0.0.1` rend 404 à tout proxy inverse, qui passe le nom
public, et même à `localhost`. Le premier essai derrière un proxy est tombé
exactement là. Le service écoute donc sur `+` et refuse par un 403 ce qui
n'arrive pas de la boucle locale. La protection est la même pour qui vient
d'ailleurs, à ceci près que le port répond au lieu de rester fermé.

### `HttpListener`, et non ASP.NET Core

La bibliothèque standard suffit pour servir une page et quelques points d'entrée
JSON. ASP.NET Core ajouterait une trentaine de mégaoctets au binaire autonome et
une pile entière à un service qui en fait sept cents lignes.

### Le jeton est engendré, jamais choisi

Au premier démarrage, le service écrit un jeton aléatoire de trente-deux octets
dans `admin.token`, en le journalisant une fois. Aucun mot de passe par défaut,
aucune valeur devinable, et rien à retenir. Le perdre se répare en supprimant le
fichier.

### Le bannissement porte sur le personnage, en dérivation lente

Décision reprise de la conception de fédération, et qui ne se rediscute pas ici :

- **sur le personnage et non sur la clé**, qui se régénère en une seconde, là où
  un personnage se paie en temps et en argent ;
- **en dérivation lente**, `argon2id` sur `nom@monde`, pour qu'une liste publiée
  soit vérifiable par qui a le nom en main et hors de prix à énumérer. Une liste
  d'empreintes rapides serait un annuaire de joueurs utilisant des mods, ce qui
  les exposerait pour une raison étrangère à ce qu'ils ont fait ;
- **appliquée par le client autant que par le service**, sans quoi un opérateur
  complaisant suffirait à offrir un refuge.

**Corrigé à la mise en œuvre.** La dérivation est `pbkdf2-sha256` à 600 000
itérations, et non `argon2id` : tout ce qui est cryptographique dans ce projet
passe par la bibliothèque standard, sans dépendance, et le client doit dériver à
l'identique depuis un autre dépôt. Le prix est que PBKDF2 se calcule bien sur
processeur graphique, là où argon2 y résisterait par sa consommation mémoire :
l'énumération reste chère, moins qu'avec lui. Les exemples ci-dessous gardent
`argon2id` pour mémoire de ce qui était visé.

### La vérification a lieu au pairage et à la pose, jamais à la détection

C'est ce qui rend la dérivation lente utilisable. À cent millisecondes par
vérification, contrôler les quarante joueurs d'une place bondée à chaque ronde
coûterait quatre secondes : intenable.

Or ce n'est pas nécessaire. On ne reçoit de fichiers que de gens qu'on a
explicitement acceptés. Deux points de contrôle suffisent donc, et ils sont
rares : **quand une demande de pairage arrive**, et **quand une apparence est sur
le point d'être posée**. Quelques vérifications, pas quarante par ronde.

### La liste n'est pas une preuve

Le service ne voit aucun contenu, par construction. Il ne peut donc jamais
vérifier une accusation. **Toute liste relève de la réputation.** La
documentation le dit, et l'interface le répète à l'opérateur au moment où il
ajoute une entrée.

## Ce que ce document ne traite pas

Le **chemin de signalement depuis le jeu**, qui demande une interface dans le
plugin. Le service reçoit et stocke les signalements ; les présenter à
l'utilisateur viendra après.

La **souscription à des listes tierces**. Un opérateur ne gère ici que la sienne.
Souscrire à celle d'autrui demande une signature et une politique de confiance,
qui feront l'objet d'une conception séparée si le besoin apparaît.

## La console

### Ce qu'elle montre

Une seule page, rafraîchie toutes les cinq secondes par un appel JSON.

| Section | Contenu |
|---|---|
| État | Temps depuis le démarrage, port, version |
| Trafic | Boîtes ouvertes, annonces en attente, appariements depuis le démarrage, octets relayés |
| Annuaire | Services connus, et candidatures en attente avec leur adresse et leur libellé |
| Bannissements | Les entrées, avec leur motif et leur date |

Aucun nom de personnage n'apparaît nulle part. Le service n'en connaît aucun :
il ne voit que des adresses de boîte, qui sont des empreintes. C'est une
propriété du système, pas une précaution d'affichage.

### Points d'entrée

| Méthode et chemin | Effet |
|---|---|
| `GET /` | La page, un seul fichier HTML embarqué |
| `GET /api/status` | Les compteurs et l'annuaire, en JSON |
| `POST /api/peers` | Approuve une candidature : la déplace de la file vers les pairs |
| `DELETE /api/peers` | Retire un pair connu, ou rejette une candidature |
| `GET /api/bans` | La liste, telle que les clients la téléchargent |
| `POST /api/bans` | Ajoute une entrée, depuis un `nom@monde` et un motif |
| `DELETE /api/bans` | Retire une entrée |

Tout sauf `GET /` et `GET /api/bans` exige l'en-tête `Authorization: Bearer <jeton>`.

**Corrigé à la mise en œuvre.** `GET /` l'exige aussi, en authentification basique :
une console qui s'affiche à qui la demande annonce ce qui tourne ici et invite à
essayer, et le premier déploiement public l'a rendue visible de tous. Le navigateur
demande alors le jeton avant d'afficher la page et le renvoie seul ensuite, ce qui a
aussi supprimé le champ de saisie et le stockage du secret dans la page. Le défi
n'accompagne que les navigations : sur un appel JSON, il ferait surgir la boîte de
dialogue au milieu d'un rafraîchissement.

`GET /api/bans` est public **à dessein** : c'est ce que les clients
téléchargent. Il ne révèle que des empreintes lentes, inexploitables sans le nom.

### Ce que la page n'est pas

Pas de cadre applicatif, pas de dépendance JavaScript, pas de construction. Un
fichier HTML embarqué dans le binaire, avec le peu de JavaScript nécessaire à un
`fetch` toutes les cinq secondes. Une console d'administration qui exigerait une
chaîne de construction serait une seconde chose à maintenir.

## Le format de la liste

Servi par `GET /api/bans`, et c'est ce que le client télécharge :

```json
{
  "version": 1,
  "kdf": { "algorithm": "argon2id", "iterations": 3, "memoryKiB": 65536, "parallelism": 1 },
  "salt": "2f8a1c...",
  "updated": 1790000000,
  "entries": [
    { "hash": "9d4b7e...", "reason": "contenu illegal", "since": 1789900000 }
  ]
}
```

Le sel est **commun à la liste** et non par entrée : sans cela, vérifier un nom
demanderait une dérivation par entrée, soit des secondes pour une liste de
cinquante. Avec un sel commun, c'est une dérivation puis une recherche.

Changer le sel invalide toute la liste et oblige à la reconstruire depuis les
noms, que le service ne conserve pas. **Le sel est donc engendré une fois et ne
change jamais**, et la conséquence est assumée : une liste publiée est
énumérable par qui y consacre des semaines de calcul.

Les motifs sont libres mais bornés à cent vingt caractères, et sans accent ni
ponctuation exotique : ils voyagent vers des clients qui les afficheront.

## Ce que le service en fait

**À la réception d'une candidature**, rien de nouveau : elle entre dans la file,
et l'opérateur l'approuve depuis la console au lieu d'éditer un fichier en SSH.

**À l'ouverture d'une boîte**, le service ne peut rien vérifier : il reçoit une
adresse de boîte, qui est une empreinte rapide du nom, et la liste porte une
empreinte lente. Les deux ne se comparent pas. **Un banni peut donc continuer
d'ouvrir ses boîtes**, et c'est une limite à écrire noir sur blanc plutôt qu'à
laisser croire résolue.

Ce qui l'arrête est côté client : le plugin refuse de se pairer avec lui et
refuse de poser son apparence. C'est là que le mal se produit, donc c'est là que
la protection doit vivre.

## Vérification

### Sous Linux, dans les tests du service

- le jeton est engendré une fois, relu au redémarrage, et refusé s'il est absent
  de la requête ;
- une requête sans jeton sur un point d'entrée protégé rend 401, et le corps ne
  révèle rien ;
- `GET /api/bans` répond sans jeton, et son format se relit ;
- approuver une candidature la retire de la file et l'ajoute aux pairs, dans un
  ordre tel qu'une coupure au milieu ne la perde pas ;
- une entrée de bannissement se calcule, s'ajoute, se retrouve et se retire ;
- le sel est stable d'un démarrage à l'autre.

### À la main

La page s'ouvre, montre des compteurs qui bougent quand deux clients s'apparient,
et approuver une candidature depuis le navigateur écrit bien dans `peers.txt`.
