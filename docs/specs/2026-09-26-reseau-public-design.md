# La page publique du réseau, et la console aux couleurs du site

Écrit le 26 septembre 2026. Conception d'un sous-système, à lire après
`plugin/docs/superpowers/specs/2026-09-26-cercle-ouvert-design.md`.

## Ce que la page doit servir

Trois publics, sur une seule page :

1. **Les joueurs**, par transparence : quels services composent le cercle ouvert,
   qui les tient, depuis quand, et quand la liste a été signée.
2. **Ceux qui hébergent** : où en est la probation de leur service.
3. **La santé du réseau** : la disponibilité récente de chaque service, les
   services sortis, le nombre de candidats.

## Décisions arrêtées

### Un instantané horaire, publié par le site

Le site est statique, servi par GitHub Pages. Le service n'a aucune page HTTP
publique : sa console n'écoute qu'en local. On garde les deux ainsi.

Le service gagne une trame publique sur son port habituel, qui rend l'état public
du réseau en JSON. Le workflow du site, qui tourne déjà toutes les heures pour
reprendre `repo.json`, l'interroge, écrit `reseau.json` à côté de la page, et la
page l'affiche.

Écarté : un JSON servi en direct par le VPS en HTTPS. Il ouvrirait une surface
publique nouvelle, un certificat à tenir et du CORS, exposerait le VPS à chaque
visite, et viderait la page quand le VPS tombe. Une heure de retard est sans
gravité pour une probation de soixante-douze heures.

### Ce qui n'est jamais publié

- **Les services écartés par le veto** n'apparaissent nulle part, pas même dans
  les compteurs.
- **L'adresse de soumission** d'une candidature et **la famille** (empreinte du
  /24 ou du /48) restent internes.
- **Les candidats jamais joints** ne sont que comptés. N'importe qui peut en
  inscrire un : le site officiel afficherait sinon un nom ou un libellé choisi
  par un inconnu. Un service n'apparaît qu'en probation, quand un vrai serveur
  répond depuis l'adresse candidate, ce qui rend l'abus coûteux, et le veto
  l'efface à la ronde suivante.

## Le document publié

```
{
  "version": 1,
  "generated": 1790440000,
  "authority": {
    "publicKey": "04f3…",
    "listVersion": 1790437861,
    "issued": 1790437861,
    "expires": 1791042661
  },
  "counts": { "listed": 3, "probation": 2, "candidates": 5, "delisted": 1 },
  "services": [
    {
      "address": "rdv.ami.ch:47900",
      "label": "Chez l'amie",
      "standing": "listed",
      "since": 1790300000,
      "lastSeen": 1790439400,
      "availability24h": 0.99
    },
    {
      "address": "rdv.nouveau.ch:47900",
      "label": "",
      "standing": "probation",
      "since": 1790290000,
      "lastSeen": 1790439400,
      "availability24h": 0.97,
      "probation": { "hours": 41, "ratio": 0.97 }
    }
  ]
}
```

- Les dates sont en secondes Unix.
- `standing` vaut `listed`, `probation` ou `delisted`. `since` est l'entrée dans
  cet état : `listedAt`, `probationStart`, ou la date de sortie.
- `availability24h` est le taux de sondes réussies sur les 144 dernières (vingt-
  quatre heures) ; absent quand aucune sonde n'a eu lieu sur cette fenêtre.
- `probation` n'existe que pour un service en probation : heures écoulées depuis
  son début, et taux de réussite cumulé.
- Les services sortis ne sont publiés que tant que le registre les garde, soit
  au plus sept jours sans réponse avant l'oubli.
- `authority` est absent tant qu'aucune liste n'a été émise.

## Côté service

### Historique de disponibilité

Le registre garde, pour chaque service suivi, le résultat de ses 144 dernières
sondes, qu'il soit candidat, en probation, listé ou sorti. Il est persisté dans
`authority.json` sous forme d'une chaîne de « 0 » et de « 1 » (le plus ancien en
tête), lisible à l'œil, pour qu'un redémarrage ne l'efface pas.
Un service écarté n'accumule plus rien.

### Deux trames

```
NetworkStatusQuery  0x19 | page (2, BE)
NetworkStatusPage   0x1A | page (2, BE) | pages (2, BE) | tranche du JSON (≤ 32 Kio)
```

Sur le modèle exact de `ConsensusQuery` et `ConsensusPage` : tranches brutes de
32 Kio, seize pages au plus, et un plafond de pages servies par connexion. Seule
une autorité répond ; un service ordinaire renvoie l'erreur « ce service ne publie
pas l'état du réseau ». Les trames passent par `Protocol/`, recopié du plugin, avec
leurs vecteurs figés dans `rendezvous-vectors.json`.

Le JSON est recalculé à chaque ronde de sondes, pas à chaque demande : servir une
page ne coûte qu'une copie.

## Côté site

### Le script

`scripts/reseau.py`, en Python standard, sans dépendance :

1. ouvre `rdv.linkpearl.eorzea.events:47900` en TCP, demande les pages une à une,
   et recolle le JSON ;
2. vérifie sa forme : `version` à 1, `services` une liste, chaque `address` et
   `label` des chaînes bornées, chaque `standing` dans les trois valeurs admises ;
3. écrit `reseau.json` dans le dossier publié ;
4. en cas d'échec (VPS injoignable, erreur, forme invalide), reprend le
   `reseau.json` actuellement servi par <https://linkpearl-sync.github.io/>, pour
   que la page garde son dernier instantané plutôt que de se vider. Sans l'un ni
   l'autre, il n'écrit rien, et la page le dit.

Il tourne dans `.github/workflows/pages.yml`, à côté de la reprise de `repo.json`,
et hérite de son passage horaire. `reseau.json` n'est pas commité.

### La page

`reseau.html`, en anglais et en français comme `expert.html`, avec les styles du
site (la nuit, Fredoka, Nunito, les cartes à bord lumineux) :

- en tête, le nombre de services listés, en probation et candidats, et l'heure de
  l'instantané ;
- les services listés : libellé, adresse, listé depuis, disponibilité sur 24 h ;
- les services en probation : une barre des heures écoulées sur 72, et le taux
  face aux 95 % requis ;
- les services sortis récemment ;
- un encart « Rejoindre le cercle ouvert », avec la commande
  `lprdv --announce-to rdv.linkpearl.eorzea.events --public-address <votre adresse>`
  et ce qui se passe ensuite ;
- l'empreinte de la clé de l'autorité et l'expiration de la liste signée.

Tous les textes venus du réseau (adresses, libellés) sont insérés par
`textContent`, jamais comme HTML. `index.html` et `expert.html` y renvoient.

## La console aux couleurs du site

Dans `AdminPage.cs`, on remplace l'habillage et rien d'autre :

- les couleurs du site : fond de nuit en dégradé (`--deep`, `--field`), texte
  `--ink` et `--mute`, lueur `--glow`, orange `--pom` pour les boutons d'action ;
- les polices Fredoka (titres), Nunito (texte) et JetBrains Mono (adresses,
  empreintes), chargées depuis Google Fonts comme sur le site, avec un repli
  système si la machine n'a pas de réseau ;
- les cartes arrondies à bord lumineux, les pastilles, les tableaux du site.

La structure HTML, les identifiants et le JS restent ; seules les classes et la
feuille de style changent. La console reste locale et sombre, sans thème clair,
comme le site.

## Pannes, et ce qu'on en dit

| Situation | Effet | Ce que la page dit |
|---|---|---|
| Le VPS ne répond pas au déploiement | L'instantané précédent reste publié | L'heure de l'instantané, qui vieillit |
| Aucun instantané n'a jamais été pris | Pas de `reseau.json` | « État du réseau indisponible pour l'instant » |
| L'autorité n'a pas encore émis de liste | `authority` absent | « Aucune liste signée émise » |
| Aucun service listé | Tableau vide | « Le cercle ouvert est vide : chacun passe par ses services configurés » |

## Vérification

### Service

- le JSON public ne contient ni les services écartés, ni les candidats jamais
  joints, ni l'adresse de soumission, ni la famille ;
- `availability24h` glisse : 144 sondes réussies puis 72 échouées donnent 0,5 ;
- l'historique survit à un redémarrage ;
- les pages font l'aller-retour, et un service ordinaire refuse poliment ;
- vecteurs figés identiques dans les deux dépôts.

### Site

- le script, lancé contre une autorité locale, écrit un `reseau.json` valide ;
  contre un port fermé, il reprend l'instantané publié ;
- la page s'affiche avec un `reseau.json` fabriqué : listés, en probation,
  sortis, vide, absent ;
- une capture d'écran de la page et de la console.
