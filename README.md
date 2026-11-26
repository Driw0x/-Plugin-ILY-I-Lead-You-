# ILY — I Lead You !

Plugin CounterStrikeSharp pour une manche tactique 3 CT contre 3 Terros sur Inferno.

## Installation et mise à jour serveur

1. Installer CounterStrikeSharp et son runtime .NET 10 sur le serveur.
2. Exécuter `dotnet publish -c Release -o publish`.
3. Copier le contenu complet de `publish/` dans `game/csgo/addons/counterstrikesharp/plugins/ILYLeadYou/`.
4. Recharger le plugin ou redémarrer le serveur, puis lancer `de_inferno` en 3v3.

## HUD Workshop

Le HUD affiche l'économie, les choix numérotés et le plan retenu. Chaque CT voit son approche et sa zone personnelles en Full, Force ou Lowbuy « zones ». En Eco ou Lowbuy « intention », toute l'équipe CT voit le plan commun. Tous les T voient le scénario et les rôles attribués. Le HUD se masque avec le chat, le menu d'achat et le scoreboard.

Les choix s'effectuent dans le menu de chat avec `!1`, `!2`, etc. Les touches numériques seules nécessiteraient des `bind` client, non utilisés ici.

Les ressources compilées sont dans `hud/compiled/panorama/`. Copier ces fichiers dans `game/csgo_addons/ily_hud/panorama/` de l'addon Workshop. Pour modifier le HUD, copier `hud/panorama/` dans `content/csgo_addons/ily_hud/panorama/`, compiler XML et CSS avec `game/bin/win64/resourcecompiler.exe -i`, remplacer les ressources compilées de l'addon et publier une mise à jour du même item Workshop. L'ID Workshop reste le même. Le distribuer avec MultiAddonManager (`mm_client_extra_addons <ID Workshop>`). Les joueurs reconnectent pour charger la version publiée.

Le plugin référence `panorama/layout/custom_game/ily_hud.vxml_c`. Sans l'addon client, `.info` reste disponible dans le chat.

## Déroulement et commandes

- `.ily_start` ou `!ily_start` : tire des économies différentes pour CT et T, puis ouvre les choix pendant la warmup.
- CT : vote A/B, puis choix d'approche. Full et Force : chaque joueur choisit son approche et sa zone. Eco : approche et intention communes. Lowbuy : vote d'équipe entre zones individuelles et intention commune.
- T : vote du scénario adapté à l'économie, puis rôle unique choisi par chaque joueur. Les noms et instructions des rôles dépendent du scénario.
- `.ily_ready` ou `!ily_ready` : une personne par équipe valide une fois tous les choix terminés. Quand les deux équipes sont prêtes, la manche démarre avec 15 secondes de freeze time pour communiquer et s'organiser.
- `.ily_stop` ou `!ily_stop` : arrête la session ILY en cas de bug ; tous les joueurs peuvent l'utiliser. Il n'y a pas de `.ily_pause`.
- `.ily_tactic` ou `!ily_tactic` : rouvre l'étape de choix courante. `.ily_role` ou `!ily_role` : rouvre le choix d'un rôle T.
- `.info` ou `!info` : affiche l'économie et les choix de votre équipe dans le chat.

Les menus sont privés à l'équipe et une égalité de vote provoque un revote. La manche ILY se termine après un round. MatchZy, s'il est installé, est mis en veille avec `css_sleep` lorsque les deux équipes sont prêtes, puis relancé avec `css_match` à la fin du round. Sans MatchZy, ILY remet le serveur en warmup. `.ready`, `!ready`, `.r` et `!r` restent réservées à MatchZy.

Les budgets sont Eco (800 $), Lowbuy (2 500 $), Force (3 500 $) et Full (5 000 $). Ils sont attribués au spawn. Les positions de spawn restent celles du jeu.

## Scénarios

Les choix sont chargés par carte, équipe et économie depuis `scenarios/de_inferno/{CT,T}/{Eco,Lowbuy,Force,Full}/tactics.json`. Chaque tactique contient `id`, `label`, `map` et `positions` (`slot`, `zone`, `instruction`). Les catalogues CT ajoutent `site` (`A` ou `B`) et `intentions` avec les mêmes champs que `positions`. Les positions CT servent aux zones personnelles ; les intentions servent aux votes collectifs. Les positions T définissent les rôles. Les options suivent l'ordre du JSON. L'ancien champ `spawn` est ignoré.
