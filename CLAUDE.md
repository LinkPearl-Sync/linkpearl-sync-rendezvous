# Linkpearl, service de rendez-vous : conventions du dépôt

Le serveur de [Linkpearl](https://github.com/LinkPearl-Sync/linkpearl). Lire son `CLAUDE.md`
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
```
