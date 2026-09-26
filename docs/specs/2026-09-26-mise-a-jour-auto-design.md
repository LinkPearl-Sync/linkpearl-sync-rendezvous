# La mise à jour automatique des serveurs

Écrit le 26 septembre 2026. Premier de trois chantiers sur les versions :

1. **la mise à jour automatique des serveurs** (ce document) ;
2. les versions et la compatibilité : le serveur annonce sa version, une trame
   inconnue est refusée poliment au lieu de couper la connexion, le cercle
   ouvert exige une version minimale ;
3. la version minimale du plugin, portée par le même canal signé.

## Ce qu'on cherche

Un serveur tenu par un bénévole ne sera jamais garanti à jour. Le plus sûr
moyen qu'il le soit est qu'il se mette à jour seul, sans que son opérateur y
pense, et sans que cela donne à qui que ce soit le moyen d'exécuter du code
sur toutes les machines du réseau d'un coup.

## Décisions arrêtées

- **Activée par défaut, désactivable.** La plupart des opérateurs gardent les
  réglages par défaut : c'est ce qui fait le gros du travail.
- **Tirée, jamais poussée.** Chaque serveur va chercher la dernière version
  lui-même. Aucun serveur, pas même l'autorité, ne donne d'ordre aux autres.
- **Signée dans le workflow de release.** La clé de signature est un secret
  GitHub, rangé dans un environnement `release` que seuls les tags `v*`
  atteignent : ni une branche ni une pull request ne la lisent. Choix assumé :
  qui prend le compte GitHub peut signer. La signature protège du reste
  (miroir, réseau, fichier remplacé sur la release après coup).
- **Délai de garde de 24 heures.** Une release n'est installée
  automatiquement que 24 heures après sa signature. Pendant ce délai, la
  retirer (la supprimer, ou la passer en pré-version) suffit à ce qu'aucun
  serveur ne l'installe. Le VPS de production, mis à jour par `deploy.sh`,
  l'essuie en premier. Une release marquée urgente se passe du délai.
- **La version installée vérifie la suivante.** Le programme de mise à jour
  est une sous-commande du binaire en place, déjà de confiance ; la clé
  publique y est inscrite.

Écarté : la signature sur le poste du mainteneur (plus sûre, une commande de
plus par release), des vagues de déploiement (utiles à des centaines de
serveurs, pas à quelques dizaines), un script bash avec `openssl` (une
dépendance de plus, et un code de vérification sans tests).

## Le manifeste signé

Le workflow publie, à côté de `lprdv`, `lprdv.service` et `lprdv.sha256` :

```
lprdv.release.json       le manifeste
lprdv.release.json.sig   sa signature, ECDSA P-256, SHA-256, format IEEE P1363
```

```json
{
  "version": "0.6.0",
  "signed": 1790500000,
  "urgent": false,
  "files": {
    "lprdv": "05d92ae1…",
    "lprdv.service": "9e816a5b…"
  }
}
```

- `version` : la version publiée, sans le `v`. Le programme n'installe
  jamais une version inférieure ou égale à la sienne : rejouer un vieux
  manifeste signé ne fait pas revenir en arrière.
- `signed` : l'heure de signature, en secondes Unix. C'est elle qui compte
  pour le délai de garde, et non la date affichée par GitHub.
- `urgent` : vrai si le message du tag annoté porte une ligne `urgent` seule
  (`git tag -a v0.6.1 -m v0.6.1 -m urgent`). Ni un mot dans une phrase, ni
  un tag léger, qui reprendrait le message du commit.
- `files` : les sommes SHA-256 du binaire et de l'unité, les mêmes que dans
  `lprdv.sha256`, qui reste publié pour `install.sh` et le guide manuel.

La signature porte sur les octets exacts du fichier. Le même format que la
liste signée du cercle ouvert, et le même code : `ECDsa` en P1363.

La clé privée est engendrée une fois par `lprdv release-key --create
release.key` sur le poste du mainteneur, qui la verse dans le secret
`LPRDV_RELEASE_KEY` (PKCS#8 en base64) et en garde une copie hors ligne. La
clé publique est inscrite dans `ReleaseKeys.cs`. Elle est distincte de
`directory.key` : la fuite de l'une ne compromet pas l'autre. Changer de clé
demande une release signée par l'ancienne qui inscrit la nouvelle.

## Le programme de mise à jour

`lprdv update`, lancé en root par `lprdv-update.service`, que déclenche
`lprdv-update.timer` toutes les heures, avec `RandomizedDelaySec=1h` pour que
le réseau ne redémarre pas à la même seconde. Une ronde :

1. Lire `https://api.github.com/repos/LinkPearl-Sync/linkpearl-sync-rendezvous/releases/latest`,
   qui ignore les pré-versions. Sans réponse : ne rien faire, la ronde
   suivante réessaiera.
2. Télécharger `lprdv.release.json` et sa signature ; vérifier la signature
   avec les clés de `ReleaseKeys`. Invalide : s'arrêter et le dire au journal.
3. S'arrêter sans bruit si `version` n'est pas supérieure à la version en
   place, ou si `signed` date de moins de 24 heures et `urgent` est faux.
4. Télécharger `lprdv` et `lprdv.service`, vérifier leurs sommes contre
   `files`. Fausses : s'arrêter.
5. Garder le binaire en place sous `/opt/lprdv/lprdv.previous`, poser le
   nouveau par renommage, poser l'unité, `systemctl daemon-reload`,
   `systemctl restart lprdv`.
6. Attendre `/healthz` jusqu'à 30 secondes. Sans réponse : remettre
   `lprdv.previous`, remettre l'unité précédente, redémarrer, et écrire au
   journal que la version a été refusée. Une version refusée n'est plus
   retentée tant qu'une plus récente n'est pas publiée
   (`/var/lib/lprdv-update/refused`).

Il ne touche jamais à `/var/lib/lprdv` ni aux compléments
`/etc/systemd/system/lprdv.service.d/`. Les versions sont comparées en
numérique, composant par composant (`0.10.0` > `0.9.3`).

L'unité `lprdv-update.service` est un `Type=oneshot` durci autant que le
permet son travail : écrire dans `/opt/lprdv`, `/etc/systemd/system` et
`/var/lib/lprdv-update`, joindre GitHub, parler à systemd.

## Installation et désactivation

- `lprdv-update.service` et `lprdv-update.timer` sont publiés avec chaque
  release, sous la somme de `lprdv.sha256`.
- `install.sh` les installe et active le minuteur. `--no-auto-update` les
  installe sans l'activer, et désactive un minuteur déjà actif. Relancé sans
  option, il garde le choix déjà fait.
- `heberger.html` gagne une case « Mettre à jour automatiquement », cochée.
- Après coup : `systemctl disable --now lprdv-update.timer`.
- Les serveurs déjà installés n'ont le minuteur qu'après avoir relancé une
  fois la commande d'installation. Rien ne peut le leur imposer.
- `deploy.sh` n'installe pas le minuteur : la production reste mise à jour à
  la main, et c'est elle qui essuie chaque version pendant le délai de garde.

## Pannes, et ce qui se passe

| Situation | Effet |
|---|---|
| GitHub injoignable, ou limite de débit de l'API atteinte | Rien ; la ronde suivante réessaie |
| Signature invalide ou absente (release d'avant ce chantier) | Rien d'installé, une ligne au journal |
| Somme fausse | Rien d'installé, une ligne au journal |
| La nouvelle version ne répond pas | Retour à l'ancienne, version notée comme refusée |
| La release est retirée pendant le délai de garde | Personne ne l'installe |
| Coupure pendant la pose | Le renommage est atomique : l'ancien ou le nouveau binaire, jamais un mélange |
| Ronde tuée entre la pose et /healthz (redémarrage, kill) | Le marqueur `/var/lib/lprdv-update/pending` fait vérifier la ronde suivante, qui défait la version si elle ne répond pas |
| Pose impossible (disque plein) | Fichiers remis, pas de redémarrage, l'unité échoue pour prévenir l'opérateur |
| Service arrêté par son opérateur, ou console qui ne répond pas | Rien n'est posé ni relancé |
| Binaire compilé sans `-p:Version` | Il se dit `0.0.0-dev` : illisible, jamais mis à jour automatiquement |

## Vérification

- Tests xUnit : comparaison de versions, vérification du manifeste (bonne
  clé, mauvaise clé, octet modifié), délai de garde et marqueur urgent,
  refus d'une version inférieure ou déjà refusée, décision de retour arrière,
  avec un faux GitHub et un faux systemd.
- Le workflow `install.yml` du site installe avec le minuteur, puis lance
  `lprdv update` à la main contre la vraie release : il doit dire que la
  version est déjà en place.
- La première release signée est vérifiée à la main : manifeste, signature,
  et une mise à jour depuis la version précédente sur une machine jetable.
