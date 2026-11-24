# ILY — Lead You

Plugin CounterStrikeSharp pour jouer un scénario tactique 3 CT contre 3 Terros,
actuellement conçu autour d'Inferno. À chaque round, chaque équipe reçoit une
économie et vote pour une tactique adaptée à celle-ci.

## Installation

1. Publier avec `dotnet publish -c Release -o publish`.
2. Copier le contenu complet de `publish/` dans
   `game/csgo/addons/counterstrikesharp/plugins/ILYLeadYou/`.
3. Charger le plugin et lancer `de_inferno` en 3v3.

Le serveur doit avoir CounterStrikeSharp et son runtime .NET 10 installés.

## Commandes joueurs

- `!ily_start` — termine immédiatement la warmup et lance le scénario, même
  si les six joueurs ne sont pas présents.
- `!scenario` — affiche dans le chat l'économie et les informations tactiques
  de votre équipe, les cartes envisagées et les commandes disponibles. Le
  récapitulatif reste dans l'historique du chat au lieu de disparaître comme
  un message au centre de l'écran; il est aussi envoyé au début du round et
  après une mise à jour.
- `.info` — réaffiche ces informations dans le chat à tout moment.
- `!ily_ready` — indique que votre équipe est prête. Une personne de chaque
  équipe doit l'indiquer après avoir choisi sa tactique, pendant le freeze
  time, pour lancer la manche.
- `!ily_tactic` — ouvre le menu de vote tactique de votre équipe; le menu
  s'ouvre aussi automatiquement au début du round. Les choix sont privés et le
  résultat majoritaire est annoncé uniquement à votre équipe.

À chaque round, chaque équipe reçoit au hasard un budget identique pour ses
joueurs: Eco (800$), Lowbuy (2 500$), Force (3 500$) ou Full (5 000$). Le
budget des CT et des Terros est toujours différent et l'argent est attribué au
spawn pour permettre les achats. Les joueurs conservent les positions de spawn
par défaut définies par le jeu. Chaque équipe ne voit que sa propre économie
et sa tactique.

## Scénarios tactiques

Les choix et positions sont chargés depuis un fichier par équipe et économie:

```text
scenarios/
  CT/{Eco,Lowbuy,Force,Full}/tactics.json
  T/{Eco,Lowbuy,Force,Full}/tactics.json
```

Chaque fichier contient une liste de tactiques avec un `id` utilisable dans
le menu, un `label`, une `map` et des positions (`slot`, `zone`, `instruction`).
Ces positions décrivent le plan tactique et ne modifient pas les points de
spawn du jeu. Les anciens champs `spawn` éventuellement présents dans les
JSON sont ignorés.

Le pool de cartes est organisé ainsi: cartes principales Inferno et Anubis;
choix Inferno, Anubis, Cache, Dust 2 et Ancient; autres cartes Overpass,
Vertigo, Mirage, etc.
