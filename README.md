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
- `!ily_position` — affiche en privé la carte, l'origine et l'orientation de
  votre position actuelle, à reporter dans un scénario JSON.

À chaque round, chaque équipe reçoit au hasard un budget identique pour ses
joueurs: Eco (800$), Lowbuy (2 500$), Force (3 500$) ou Full (5 000$). Le
budget des CT et des Terros est toujours différent et l'argent est attribué au
spawn pour permettre les achats. Chaque équipe ne voit que sa propre économie
et sa tactique.

## Scénarios tactiques

Les choix et positions sont chargés depuis un fichier par équipe et économie:

```text
scenarios/
  CT/{Eco,Lowbuy,Force,Full}/tactics.json
  T/{Eco,Lowbuy,Force,Full}/tactics.json
```

Chaque fichier contient une liste de tactiques avec un `id` utilisable dans
le menu, un `label`, une `map` et des positions (`slot`, `zone`, `instruction`,
`spawn`). La position `spawn` est `null` dans les exemples jusqu'à ce que vous
la renseigniez. Elle doit contenir l'origine (`x`, `y`, `z`) et l'orientation
(`pitch`, `yaw`, `roll`) obtenues avec `!ily_position`. Les valeurs sont des
coordonnées réelles du jeu; ne copiez pas de coordonnées d'exemple au hasard.

Pour relever et ajouter un point:

1. Sur la carte indiquée par `map` (par exemple `de_inferno`), placez-vous
   exactement où vous voulez faire apparaître un joueur et regardez dans la
   direction souhaitée.
2. Tapez `!ily_position`, puis copiez les valeurs privées affichées.
3. Dans le `tactics.json` de l'équipe et de l'économie concernés, remplacez le
   `"spawn": null` de la position choisie par `"spawn": { "origin": { "x": X,
   "y": Y, "z": Z }, "angles": { "pitch": PITCH, "yaw": YAW, "roll": ROLL } }`
   en remplaçant chaque valeur par celle obtenue.

Au choix des deux tactiques, le plugin mélange les joueurs et les points
disponibles, puis attribue les points sans remise: aucun joueur ne reçoit le
même point qu'un autre, y compris entre CT et Terros. Il faut donc au moins
autant de points renseignés que de joueurs dans chaque équipe et des origines
différentes entre les points des deux tactiques retenues. En cas de carte
incorrecte, de coordonnées dupliquées ou de points insuffisants, le plugin
refuse la téléportation et indique le problème. Rechargez le plugin après avoir
modifié les JSON.

Le pool de cartes est organisé ainsi: cartes principales Inferno et Anubis;
choix Inferno, Anubis, Cache, Dust 2 et Ancient; autres cartes Overpass,
Vertigo, Mirage, etc.
