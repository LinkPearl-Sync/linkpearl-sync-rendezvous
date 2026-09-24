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
   opaque de dix minutes.

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
dotnet run --project Linkpearl.Rendezvous -- --port 47900 --admin-port 47901
```

La liste de bannissement (`Protocol/Core/Safety/BanList.cs`) fait partie de la copie
littérale : le client doit dériver à l'identique, sans quoi une liste ne protège personne.
`BanListTests.cs` est copié lui aussi, et c'est lui qui attrape une dérive de dérivation.

## Déploiement

Pas de Docker, et c'est voulu : un binaire autonome sous systemd suffit, et un conteneur
exigerait `network_mode: host`, sans quoi la réflexion UDP renverrait l'adresse du pont
Docker au lieu de celle du client.

```sh
LPRDV_HOST=debian@83.228.242.221 LPRDV_KEY=~/.ssh/linkpearl_rdv ./deploy/deploy.sh
```

- Le VPS de production est `83.228.242.221` (Infomaniak, Debian 13). Compte `debian`,
  sudo sans mot de passe, clé `~/.ssh/linkpearl_rdv` réservée à ce déploiement.
- `deploy.sh` compile, pose `/opt/lprdv/lprdv` par renommage, installe
  `deploy/lprdv.service`, redémarre et attend `/healthz`. Il est rejouable : le premier
  passage crée le compte système `lprdv`.
- Tout l'état vit dans `/var/lib/lprdv`. `bans.json` y porte le sel des empreintes :
  le perdre rend notre liste incomparable à celle des autres services. Ne jamais
  l'écraser ni le régénérer.
- Vérifier de l'extérieur depuis le dépôt du plugin : lancer en parallèle
  `dotnet run --project Linkpearl.Harness -c Release -- rdv 83.228.242.221 47900 alice`
  et la même avec `bob`. Les deux doivent finir par « TOUT EST PASSÉ ».
- Journal : `journalctl -u lprdv`. Console : `ssh -L 47901:127.0.0.1:47901`, le jeton est
  dans `/var/lib/lprdv/admin.token`.
- Jamais de service lancé à la main dans un terminal SSH : il meurt avec la session, et
  le premier déploiement public est resté hors ligne sans que personne le voie.

Publier une version : `git tag -a vX.Y.Z -m vX.Y.Z` sur `main`, puis pousser le tag.
`.github/workflows/release.yml` vérifie (build sans warning, tests), compile le binaire
autonome et publie la release avec `lprdv` et `lprdv.sha256`. Un suffixe (`v0.3.0-rc1`)
en fait une pré-version. Le workflow ne déploie pas : aucun secret SSH dans le dépôt,
`deploy.sh` reste lancé à la main.
