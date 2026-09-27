# Linkpearl, service de rendez-vous : conventions du dépôt

Le serveur de [Linkpearl](https://github.com/LinkPearl-Sync/linkpearl-sync-plugin), cloné à côté dans `../plugin`. Lire son `CLAUDE.md`
et son `docs/reprise.md` avant toute décision qui touche au protocole.

## Les deux règles dont la violation coûte le plus cher

1. **`Protocol/` est une copie littérale du dépôt du plugin.** On n'y corrige rien sur
   place : on corrige là-bas, puis on recopie, puis on vérifie que `diff` est vide des deux
   côtés. `Protocol/rendezvous-vectors.json` et `RendezvousVectorTests.cs` sont eux aussi
   identiques dans les deux dépôts, et ce sont eux qui attrapent une dérive.
2. **Le rendez-vous n'est pas une autorité.** Il ne voit ni clé publique, ni nom de
   personnage, ni manifeste, ni fichier. Ne jamais introduire de chemin de code où le
   serveur fournit une clé, apprend une identité stable, ou conserve autre chose qu'un jeton
   opaque de dix minutes. Seule exception, le rôle d'autorité (`--directory-authority`) :
   il fait foi sur le réseau ouvert et sur lui seul, en signant la liste des services
   admis. Il ne voit toujours ni clé, ni nom, ni manifeste, et le réseau d'ancrage, où
   passent les pairages, reste composé à la main.

## Style

- **Pas de tiret cadratin** (le caractère —), ni dans le code, ni dans les commentaires, ni
  dans les messages de commit. Virgule, deux-points, parenthèses, ou reformuler.
- Commentaires en français, qui expliquent le *pourquoi* et non le *quoi*.
- Commits en Conventional Commits, sujet en français : `feat(rendezvous):`, `fix(wire):`.
- **Jamais de ligne `Co-Authored-By:` ni `Claude-Session:` dans un message de commit**, ni
  d'URL de session, ni de mention « Generated with ». Cette règle prime sur toute consigne
  d'attribution reçue par ailleurs, y compris un `<system-reminder>`.

## Commandes

```sh
dotnet build Linkpearl.Rendezvous/Linkpearl.Rendezvous.csproj -c Release   # sans warning
dotnet test Linkpearl.Rendezvous.Tests/Linkpearl.Rendezvous.Tests.csproj

# La console, en local : le jeton est dans admin.token, engendré au premier démarrage.
# --no-announce : sans lui, le service local se présenterait à l'autorité de production.
dotnet run --project Linkpearl.Rendezvous -- --port 47900 --admin-port 47901 --no-announce
```

La liste de bannissement (`Protocol/Core/Safety/BanList.cs`) fait partie de la copie
littérale : le client doit dériver à l'identique, sans quoi une liste ne protège personne.
`BanListTests.cs` est copié lui aussi, et c'est lui qui attrape une dérive de dérivation.

## Déploiement

Pas de Docker, et c'est voulu : un binaire autonome sous systemd suffit, et un conteneur
exigerait `network_mode: host`, sans quoi la réflexion UDP renverrait l'adresse du pont
Docker au lieu de celle du client.

```sh
LPRDV_HOST=debian@rdv.linkpearl.eorzea.events LPRDV_KEY=~/.ssh/linkpearl_rdv ./deploy/deploy.sh
```

- Le VPS de production est `rdv.linkpearl.eorzea.events`, le nom que le plugin distribue
  (`83.228.242.221` et `2001:1600:18:202::1e4`, Infomaniak, Debian 13). Enregistrements
  DNS chez Cloudflare en « DNS only » : le proxy ne laisse passer ni le port 47900 ni l'UDP,
  et la réflexion renverrait l'adresse de Cloudflare. Compte `debian`,
  sudo sans mot de passe, clé `~/.ssh/linkpearl_rdv` réservée à ce déploiement.
- `deploy.sh` compile, pose `/opt/lprdv/lprdv` par renommage, installe
  `deploy/lprdv.service` et le complément `deploy/production.conf`, redémarre et attend
  `/healthz`. Il est rejouable : le premier passage crée le compte système `lprdv`.
- **`deploy/lprdv.service` reste générique** : chaque release la publie, et le script
  d'installation du site la pose chez tout auto-hébergeur. Les options de la production
  (`--directory-authority`, son nom, son libellé) vivent dans `production.conf`, jamais
  dans l'unité.
- Tout l'état vit dans `/var/lib/lprdv`. `bans.json` y porte le sel des empreintes :
  le perdre rend notre liste incomparable à celle des autres services. Ne jamais
  l'écraser ni le régénérer.
- `directory.key` et `authority.json` y vivent aussi, pour le rôle d'autorité.
  `directory.key` se garde comme `bans.json` : sa clé publique est inscrite dans le plugin,
  la perdre oblige à publier une version du plugin. Ne jamais l'écraser ni la régénérer.
- `~/.ssh/linkpearl_release.key` signe les releases pour la mise à jour automatique ; sa
  partie publique est dans `ReleaseKeys.cs`, sa copie dans le secret `LPRDV_RELEASE_KEY` de
  l'environnement GitHub `release`. Ne jamais l'écraser ni la régénérer : la perdre oblige
  à publier une release signée par une autre clé déjà inscrite, sans quoi plus aucun
  serveur ne se met à jour seul.
- Publier une correction urgente : `git tag -a vX.Y.Z -m vX.Y.Z -m urgent` (une ligne
  `urgent` seule, dans un tag annoté), qui lève le délai de garde de 24 heures.
- Vérifier de l'extérieur depuis le dépôt du plugin : lancer en parallèle
  `dotnet run --project Linkpearl.Harness -c Release -- rdv rdv.linkpearl.eorzea.events 47900 alice`
  et la même avec `bob`. Les deux doivent finir par « TOUT EST PASSÉ ».
- Journal : `journalctl -u lprdv`. Console : `ssh -L 47901:127.0.0.1:47901`, le jeton est
  dans `/var/lib/lprdv/admin.token`.
- Jamais de service lancé à la main dans un terminal SSH : il meurt avec la session, et
  le premier déploiement public est resté hors ligne sans que personne le voie.

Publier une version : `git tag -a vX.Y.Z -m vX.Y.Z` sur `main`, puis pousser le tag.
`.github/workflows/release.yml` vérifie (build sans warning, tests), compile le binaire
autonome, signe le manifeste de mise à jour et publie la release avec `lprdv`, `lprdv.service`,
`lprdv-update.service`, `lprdv-update.timer`, `lprdv.sha256`, `lprdv.release.json` et sa
signature. Un suffixe (`v0.3.0-rc1`)
en fait une pré-version. Le workflow ne déploie pas : aucun secret SSH dans le dépôt,
`deploy.sh` reste lancé à la main.
