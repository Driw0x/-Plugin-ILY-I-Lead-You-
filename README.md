# ILY — I Lead You !

Plugin CounterStrikeSharp pour des manches tactiques 3 CT contre 3 Terros sur Inferno.

## Installation et mise à jour serveur

1. Installer CounterStrikeSharp et son runtime .NET 10 sur le serveur.
2. Exécuter `dotnet publish -c Release -o publish`.
3. Copier le contenu complet de `publish/` dans `game/csgo/addons/counterstrikesharp/plugins/ILYLeadYou/`.
4. Recharger le plugin ou redémarrer le serveur, puis lancer `de_inferno` en 3v3.

## Menus de sélection

Les choix ILY s'affichent au centre de l'écran avec leurs intitulés adaptés à l'équipe, à l'économie et à l'étape. Comme dans CS2-CustomVotes, ce sont des `CenterHtmlMenu` de CounterStrikeSharp : ouvrez le chat et saisissez `!1`, `!2`, etc. `!7` revient à la page précédente, `!8` passe à la suivante et `!9` ferme le menu. Les menus affichent jusqu'à cinq choix par page. Les touches numériques seules nécessitent des `bind` client ; aucun addon Workshop n'est nécessaire pour ce menu.

## Déroulement et commandes

- `.ily_config` ou `!ily_config` : ouvre un menu pour choisir 5, 10 ou 15 manches avant le départ. Le défaut est 5 ; le choix reste actif jusqu'à modification ou rechargement du plugin.
- `.ily_start` ou `!ily_start` : met immédiatement MatchZy en veille s'il est installé, démarre la session et tire des économies différentes pour CT et T, puis ouvre les choix pendant la warmup.
- CT : vote A/B, puis choix d'approche. Full et Force : chaque joueur choisit son approche et sa zone. Eco : approche et intention communes. Lowbuy : vote d'équipe entre zones individuelles et intention commune.
- T : vote du scénario adapté à l'économie, puis rôle unique choisi par chaque joueur. Les noms et instructions des rôles dépendent du scénario.
- `.ily_ready` ou `!ily_ready` : une personne par équipe valide une fois tous les choix terminés. Quand les deux équipes sont prêtes, la manche démarre avec 15 secondes de freeze time pour communiquer et s'organiser.
- `.ily_forcestart` ou `!ily_forcestart` : lance la manche pendant la warmup sans attendre les deux équipes. Les choix manquants sont tirés au hasard ; les rôles T déjà attribués sont conservés et chaque rôle revient à un seul joueur.
- `.ily_pause` ou `!ily_pause` : un CT ou un Terro demande une pause à la fin de la manche en cours, sauf la dernière. La manche suivante attend en warmup, sans nouveaux choix ; les deux équipes peuvent alors se parler en vocal dans CS2.
- `.ily_unpause` ou `!ily_unpause` : un joueur CT et un joueur Terro doivent chacun l'utiliser pour reprendre les choix de la manche suivante. Le réglage vocal initial est rétabli.
- `.ily_stop` ou `!ily_stop` : arrête immédiatement la session ILY, même avant la fin des 5, 10 ou 15 manches ; tous les joueurs peuvent l'utiliser.
- `.ily_tactic` ou `!ily_tactic` : rouvre l'étape de choix courante. `.ily_role` ou `!ily_role` : rouvre le choix d'un rôle T ou le choix CT courant (zone personnelle en Full, Force et Lowbuy « zones » ; intention commune en Eco et Lowbuy « intention »).
- `.info` ou `!info` : affiche l'économie et les choix de votre équipe dans le chat.

Les menus sont privés à l'équipe et une égalité de vote provoque un revote. Chaque manche ILY a de nouvelles économies et de nouveaux choix. MatchZy, s'il est installé, est mis en veille avec `css_sleep` dès `.ily_start`, puis relancé avec `css_match` à la fin ou à l'arrêt de la session. Sans MatchZy, ILY remet le serveur en warmup. `.ready`, `!ready`, `.r` et `!r` restent réservées à MatchZy.

Les budgets sont Eco (800 $), Lowbuy (2 500 $), Force (3 500 $) et Full (5 000 $). Ils sont attribués au spawn. Les positions de spawn restent celles du jeu.

## Scénarios

Les choix sont chargés par carte, équipe et économie depuis `scenarios/de_inferno/{CT,T}/{Eco,Lowbuy,Force,Full}/tactics.json`. Chaque tactique contient `id`, `label`, `map` et `positions` (`slot`, `zone`, `instruction`). Les catalogues CT ajoutent `site` (`A` ou `B`) et `intentions` avec les mêmes champs que `positions`. Les positions CT servent aux zones personnelles ; les intentions servent aux votes collectifs. Les positions T définissent les rôles. Les options suivent l'ordre du JSON. L'ancien champ `spawn` est ignoré.
