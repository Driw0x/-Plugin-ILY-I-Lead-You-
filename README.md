# ILY — I Lead You !

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

- `.ily_start` ou `!ily_start` — force la fin de la warmup sans attendre les
  ready; le freeze time est réglé à 15 secondes.
- `.info` ou `!info` — affiche dans le chat l'économie et les informations tactiques
  de votre équipe, les cartes envisagées et les commandes disponibles. Le
  récapitulatif reste dans l'historique du chat au lieu de disparaître comme
  un message au centre de l'écran; il est aussi envoyé au début du round et
  après une mise à jour.
- `.ready` ou `!ready` — indique que votre équipe est prête avant le match,
  pendant la warmup. Une personne de chaque équipe doit le faire; la warmup
  prend alors fin et la manche démarre avec 15 secondes de freeze time.
  `.ily_ready` et `!ily_ready` fonctionnent aussi. Ces commandes sont
  interceptées par ILY pour éviter la réponse ready de MatchZy.
- `.ily_tactic` ou `!ily_tactic` — ouvre le menu de vote tactique de votre
  équipe; le menu s'ouvre aussi automatiquement au début du round. Les choix
  sont privés et le résultat majoritaire est annoncé uniquement à votre équipe.

À chaque round, chaque équipe reçoit au hasard un budget identique pour ses
joueurs: Eco (800$), Lowbuy (2 500$), Force (3 500$) ou Full (5 000$). Le
budget des CT et des Terros est toujours différent et l'argent est attribué au
spawn pour permettre les achats. Les joueurs conservent les positions de spawn
par défaut définies par le jeu. Chaque équipe ne voit que sa propre économie
et sa tactique.

## Scénarios tactiques

Les choix et positions sont chargés par carte, équipe et économie:

```text
scenarios/
  de_inferno/
    CT/{Eco,Lowbuy,Force,Full}/tactics.json
    T/{Eco,Lowbuy,Force,Full}/tactics.json
```

Le nom du dossier de carte doit correspondre à `map` dans chaque tactique.
Le plugin charge la configuration de la carte courante, du side et de l'économie.
Chaque fichier contient une liste de tactiques avec un `id` utilisable dans
le menu, un `label`, une `map` et des positions (`slot`, `zone`, `instruction`).
Ces positions décrivent le plan tactique et ne modifient pas les points de
spawn du jeu. Les anciens champs `spawn` éventuellement présents dans les
JSON sont ignorés.

Le pool de cartes est organisé ainsi: cartes principales Inferno et Anubis;
choix Inferno, Anubis, Cache, Dust 2 et Ancient; autres cartes Overpass,
Vertigo, Mirage, etc.
