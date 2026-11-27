# Test du vote radio ILY

Le [panneau radio](ily_radio/resource/ui/radiopanel.txt) utilise le format natif de CS2, comme [le panneau CS2KZ](https://github.com/KZGlobalTeam/cs2kz-metamod/blob/master/menu/english.txt). Les chiffres 1 à 4 envoient `!ily_pick 1` à `!ily_pick 4` par le chat. Le plugin valide le choix en attente et le traite comme un vote ou un choix de rôle. Le joueur ouvre lui-même la radio. L'addon remplace la ressource du panneau radio pour les clients qui le chargent.

## Publier le panneau Workshop

Le VPK prêt à téléverser est dans [steam-upload/ily_radio.vpk](steam-upload/ily_radio.vpk). Il contient uniquement `resource/ui/radiopanel.txt`. Après un changement du panneau, régénérez-le depuis la racine avec [Source 2 Viewer CLI](https://github.com/ValveResourceFormat/ValveResourceFormat/releases/tag/21.2).

```bash
Source2Viewer-CLI -i workshop/ily_radio --vpk_create workshop/steam-upload/ily_radio.vpk
Source2Viewer-CLI -i workshop/steam-upload/ily_radio.vpk --vpk_list
```

Créez un item CS2 de test avec un compte Steam autorisé à publier. Utilisez [`workshop_build_item` de SteamCMD](https://partner.steamgames.com/doc/features/workshop/implementation#SteamCmd) pour téléverser le VPK déjà empaqueté. Le filtre de l'outil de publication de CS2 omet `resource/ui` lors du réempaquetage.

1. Copiez le VPK dans un dossier de téléversement séparé, sans autre fichier. Créez le VDF ci-dessous en indiquant les chemins absolus.
2. Ouvrez SteamCMD et exécutez `login VOTRE_COMPTE`, puis `workshop_build_item /chemin/absolu/ily_radio.vdf`. SteamCMD écrit l'ID créé dans le VDF. Acceptez les conditions Workshop depuis le compte qui publie si Steam le demande.
3. Dans le dossier de téléversement, renommez `ily_radio.vpk` en `<ID_WORKSHOP>.vpk`. Relancez `workshop_build_item` avec le même VDF. MultiAddonManager cherche un VPK nommé d'après l'ID. Choisissez une visibilité que les joueurs de test et le serveur peuvent télécharger.

```text
"workshopitem"
{
    "appid" "730"
    "publishedfileid" "0"
    "contentfolder" "/chemin/absolu/vers/le/dossier-du-vpk"
    "previewfile" "/chemin/absolu/vers/un-apercu.jpg"
    "visibility" "1"
    "title" "ILY - Vote radio"
    "description" "Panneau radio pour les choix tactiques ILY."
}
```

Sur le NAS, ajoutez l'ID à la liste `mm_client_extra_addons` dans `/opt/cs2/data/game/csgo/cfg/multiaddonmanager/multiaddonmanager.cfg`. Gardez les autres IDs utiles, séparés par des virgules. Le panneau est téléchargé par les clients à la connexion. Un autre addon qui remplace le même panneau peut entrer en conflit.

```text
mm_client_extra_addons "<ID_WORKSHOP>"
```

## Récupérer et installer la branche sur le NAS

Depuis un compte autorisé à utiliser Docker et à écrire dans le répertoire des plugins, exécutez ce bloc. Il prépare la publication avant l'arrêt du conteneur et conserve l'ancienne version hors du répertoire scanné par CounterStrikeSharp.

```bash
(
    set -eu
    cd /opt/cs2-build/-Plugin-ILY-I-Lead-You-
    git fetch origin
    if git show-ref --verify --quiet refs/heads/codex/menu-vote-radio; then
        git switch codex/menu-vote-radio
    else
        git switch --track origin/codex/menu-vote-radio
    fi
    git pull --ff-only origin codex/menu-vote-radio

    sortie=$(mktemp -d)
    docker run --rm --user "$(id -u):$(id -g)" -e DOTNET_CLI_HOME=/tmp \
        -v "$PWD:/src" -v "$sortie:/out" -w /src \
        mcr.microsoft.com/dotnet/sdk:10.0 dotnet publish -c Release -o /out
    test -s "$sortie/ILYLeadYou.dll"

    plugin=/opt/cs2/data/game/csgo/addons/counterstrikesharp/plugins/ILYLeadYou
    sauvegarde=/opt/cs2/ily-backups/ILYLeadYou-$(date +%Y%m%d-%H%M%S)
    mkdir -p /opt/cs2/ily-backups
    docker stop -t 30 cs2-matchzy
    if [ -d "$plugin" ]; then mv "$plugin" "$sauvegarde"; fi
    mkdir -p "$plugin"
    cp -a "$sortie/." "$plugin/"
    docker start cs2-matchzy
    docker logs --since 2m cs2-matchzy
    printf 'Sauvegarde éventuelle : %s\n' "$sauvegarde"
)
```

## Vérifier dans CS2

1. Reconnectez un client avec ses binds habituels. Hors session, tapez `!ily_config` et ouvrez la radio. Le titre doit être **ILY - Choix tactique**. Testez les chiffres 1 à 3, qui correspondent à 5, 10 et 15 manches. Le chiffre 4 doit être refusé pour ce menu.
2. Lancez `!ily_start` sur Inferno. Vérifiez les choix de tactique et de rôle dans les deux équipes. Fermez la radio, puis vérifiez que les chiffres servent de nouveau à sélectionner les armes. Un vote partagé doit ramener la liste de choix et un rôle T déjà pris doit être refusé.
3. Si le HUD et `!ily_pick 1` fonctionnent mais que le panneau ILY ne s'affiche pas, vérifiez le téléchargement Workshop. Si le panneau s'affiche et que le chiffre n'envoie rien, le test échoue côté client même si le plugin compile.

Pour revenir en arrière, retirez l'ID de `mm_client_extra_addons`. Arrêtez le conteneur, déplacez le plugin de test hors du dossier `plugins/`, puis remettez la sauvegarde affichée à la place de `plugins/ILYLeadYou`. Relancez `docker start cs2-matchzy` et reconnectez les clients.
