# ILY — Lead You

Plugin CounterStrikeSharp pour jouer un scénario tactique 3 CT contre 3 Terros,
actuellement conçu autour d'Inferno. Les équipes choisissent leurs rôles et leur
plan; le plugin attribue à chaque équipe une économie aléatoire au début de
chaque round.

## Installation

1. Publier avec `dotnet publish -c Release -o publish`.
2. Copier le contenu complet de `publish/` dans
   `game/csgo/addons/counterstrikesharp/plugins/ILYLeadYou/`.
3. Charger le plugin et lancer `de_inferno` en 3v3.

Le serveur doit avoir CounterStrikeSharp et son runtime .NET 10 installés.

## Commandes joueurs

- `!forcestart` — termine immédiatement la warmup et lance le scénario, même
  si les six joueurs ne sont pas présents.
- `!scenario` — affiche dans le chat l'économie, les rôles CT, le vote, les
  cartes envisagées et les commandes disponibles. Le récapitulatif reste dans
  l'historique du chat au lieu de disparaître comme un message au centre de
  l'écran; il est aussi envoyé au début du round et après une mise à jour.
- CT: `!role t long-defensive` ou `!role t banana-aggressive` pour le rôle T;
  `!role apps` et `!role b-solo` pour les deux autres rôles. Chaque rôle est
  attribué à un seul CT. Le choix défensif permet de tenir ou reculer; le choix
  agressif permet de tenir ou avancer.
- Terros: `!tactic banana`, `!tactic second-mid` ou `!tactic mid-long`.
  Le plan majoritaire est annoncé lorsque les trois joueurs ont voté.

À chaque round, chaque équipe reçoit au hasard un budget identique pour ses
joueurs: Eco (800$), Lowbuy (2 500$), Force (3 500$) ou Full (5 000$). Les
joueurs restent libres de choisir leur équipement.

Le pool de cartes est organisé ainsi: cartes principales Inferno et Anubis;
choix Inferno, Anubis, Cache, Dust 2 et Ancient; autres cartes Overpass,
Vertigo, Mirage, etc.
