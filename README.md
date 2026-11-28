# ILY — I Lead You !

Plugin CounterStrikeSharp pour des manches tactiques 3 CT contre 3 Terros sur Inferno.

## Installation et mise à jour serveur

1. Installer CounterStrikeSharp et son runtime .NET 10 sur le serveur.
2. Sur la machine de compilation, installer le SDK .NET 10 puis exécuter `dotnet publish -c Release -o publish`.
3. Copier le contenu publié dans `game/csgo/addons/counterstrikesharp/plugins/ILYLeadYou/`.
4. Installer [MultiAddonManager](https://github.com/Source2ZE/MultiAddonManager) et suivre la [procédure Workshop et NAS](workshop/README.md) pour tester les touches 1 à 4.

## Menus de sélection

ILY affiche les choix de votre équipe dans le HUD et le chat. Quand la liste s'affiche, ouvrez la radio native avec votre touche habituelle ou la commande client `radio`. Le panneau Workshop affiche quatre entrées numérotées et transmet votre choix à ILY. Les intitulés propres à la manche restent dans le HUD et le chat jusqu'au vote. Vous pouvez aussi choisir avec `!ily_pick <numéro>`, sans addon. `!ily_pick 0` ferme uniquement le menu de configuration.

## Déroulement et commandes

- `.ily_help` ou `!ily_help` : affiche les commandes ILY dans le chat.
- `.ily_info` ou `!ily_info` : affiche la manche, l'économie et les choix de votre équipe si une session ILY est en cours.
- `.ily_config` ou `!ily_config` : ouvre un menu pour choisir 5, 10 ou 15 manches avant le départ. Le défaut est 5 ; le choix reste actif jusqu'à modification ou rechargement du plugin.
- `.ily_start` ou `!ily_start` : si aucune session ILY n'est en cours et que les huit catalogues de la carte sont valides, met MatchZy en veille s'il est installé, démarre la warmup sans freeze time si nécessaire, tire les économies CT et T, puis ouvre les choix. Le Pistol round est commun aux deux équipes. Pour les autres manches, les deux équipes reçoivent des économies différentes. La partie attend les choix et le ready des deux équipes.
- CT : vote A/B, puis choix d'approche. Full et Force : chaque joueur choisit son approche et sa zone. Pistol round : approche et intention communes. Lowbuy : vote d'équipe entre zones individuelles et intention commune.
- T : vote du scénario adapté à l'économie, puis rôle unique choisi par chaque joueur. Les noms et instructions dépendent du scénario. Les catalogues fournis proposent trois rôles par scénario. Avec plus de trois joueurs T, la manche ne peut pas démarrer.
- `.ily_ready` ou `!ily_ready` : une personne par équipe valide une fois tous les choix terminés. Quand les deux équipes sont prêtes, la manche démarre avec 15 secondes de freeze time pour communiquer et s'organiser. Pendant une pause permanente, les ready sont enregistrés mais la manche attend les deux `.ily_unpause`.
- `.ily_forcestart` ou `!ily_forcestart` : lance la manche pendant la warmup, hors pause permanente, sans attendre les deux équipes. Les choix manquants sont tirés au hasard. Les rôles T déjà attribués sont conservés et chaque rôle revient à un seul joueur.
- `.ily_pause` ou `!ily_pause` : un CT ou un Terro demande une pause permanente à la fin de la manche en cours, sauf la dernière. Les nouveaux choix et le vocal all talk restent ouverts pendant cette pause.
- `.ily_unpause` ou `!ily_unpause` : un joueur CT et un joueur Terro doivent chacun l'utiliser pour reprendre. Le réglage vocal initial est rétabli ; les choix déjà faits restent actifs.
- `.ily_stop` ou `!ily_stop` : arrête immédiatement la session ILY, même avant la fin des 5, 10 ou 15 manches ; tous les joueurs peuvent l'utiliser.
- `.ily_tactic` ou `!ily_tactic` : rouvre l'étape de choix courante. `.ily_role` ou `!ily_role` : rouvre le choix d'un rôle T ou le choix CT courant (zone personnelle en Full, Force et Lowbuy « zones » ; intention commune en Eco et Lowbuy « intention »).
- `!tactic`, `!role` et `!scenario` sont des alias de `!ily_tactic`, `!ily_role` et `!ily_info`.

Les menus sont privés à l'équipe et une égalité de vote provoque un revote. Chaque manche ILY a de nouvelles économies et de nouveaux choix. Entre deux manches, la warmup reste ouverte jusqu'au ready des deux équipes ou à `.ily_forcestart`. `.ily_pause` maintient la partie en pause jusqu'aux deux `.ily_unpause`. MatchZy, s'il est installé, est mis en veille avec `css_sleep` dès `.ily_start`, puis relancé avec `css_match` à la fin ou à l'arrêt de la session. Sans MatchZy, ILY remet le serveur en warmup. `.ready`, `!ready`, `.r` et `!r` restent réservées à MatchZy.

Les budgets sont Pistol round (800 $ des deux côtés), Lowbuy (2 800 $ T / 2 950 $ CT), Force (3 700 $ T / 3 900 $ CT) et Full (5 500 $ des deux côtés). L'argent est remis au montant exact de l'économie choisie à chaque nouvelle manche et au spawn. Les positions de spawn restent celles du jeu.

## Scénarios

Les choix sont chargés par carte, équipe et économie depuis `scenarios/de_inferno/{CT,T}/{Eco,Lowbuy,Force,Full}/tactics.json`. Chaque tactique contient `id`, `label`, `map` et `positions` (`slot`, `zone`, `instruction`). Les catalogues CT ajoutent `site` (`A` ou `B`) et `intentions` avec les mêmes champs que `positions`. Les positions CT servent aux zones personnelles ; les intentions servent aux votes collectifs. Les positions T définissent les rôles. Les options suivent l'ordre du JSON. L'ancien champ `spawn` est ignoré.
