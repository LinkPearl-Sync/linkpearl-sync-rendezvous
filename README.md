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

## Le format de fil est une copie

`Protocol/` contient trois fichiers copiés **littéralement** depuis le dépôt du plugin :

| Ici | Là-bas |
|---|---|
| `Protocol/Core/Transport/Rendezvous/RendezvousWire.cs` | `Linkpearl/Core/Transport/Rendezvous/RendezvousWire.cs` |
| `Protocol/Core/Transport/Rendezvous/RendezvousTicket.cs` | `Linkpearl/Core/Transport/Rendezvous/RendezvousTicket.cs` |
| `Protocol/Core/Transport/Rendezvous/RendezvousAddress.cs` | `Linkpearl/Core/Transport/Rendezvous/RendezvousAddress.cs` |
| `Protocol/Core/Abstractions/IClock.cs` | `Linkpearl/Core/Abstractions/IClock.cs` |

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
