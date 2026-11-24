using System.Text.Json;
using System.Globalization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Menu;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace ILYLeadYou;

public sealed class ILYLeadYouPlugin : BasePlugin
{
    private enum EconomyTier
    {
        Eco,
        Lowbuy,
        Force,
        Full
    }

    private sealed class TacticCatalog
    {
        public TacticCatalog() { }
        public List<TacticalOption> Tactics { get; set; } = [];
    }

    private sealed class TacticalOption
    {
        public TacticalOption() { }
        public string Id { get; set; } = "";
        public string Label { get; set; } = "";
        public string Map { get; set; } = "";
        public List<TacticalPosition> Positions { get; set; } = [];
    }

    private sealed class TacticalPosition
    {
        public TacticalPosition() { }
        public int Slot { get; set; }
        public string Zone { get; set; } = "";
        public string Instruction { get; set; } = "";
        public TacticalSpawn? Spawn { get; set; }
    }

    private sealed class TacticalSpawn
    {
        public TacticalSpawn() { }
        public SpawnOrigin Origin { get; set; } = new();
        public SpawnAngles Angles { get; set; } = new();
    }

    private sealed class SpawnOrigin
    {
        public SpawnOrigin() { }
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
    }

    private sealed class SpawnAngles
    {
        public SpawnAngles() { }
        public float Pitch { get; set; }
        public float Yaw { get; set; }
        public float Roll { get; set; }
    }

    private readonly record struct SpawnLocation(float X, float Y, float Z);

    private static readonly (EconomyTier Tier, int Cash)[] Economies =
    [
        (EconomyTier.Force, 3500),
        (EconomyTier.Lowbuy, 2500),
        (EconomyTier.Full, 5000),
        (EconomyTier.Eco, 800)
    ];

    private readonly Dictionary<(CsTeam Team, EconomyTier Tier), List<TacticalOption>> _tacticCatalogs = [];
    private readonly Dictionary<CsTeam, Dictionary<ulong, string>> _tacticVotes = [];
    private readonly Dictionary<CsTeam, TacticalOption> _selectedTactics = [];
    private readonly Dictionary<CsTeam, Dictionary<int, TacticalPosition>> _spawnAssignments = [];
    private readonly Dictionary<CsTeam, EconomyTier> _roundEconomies = [];
    private readonly Dictionary<CsTeam, ulong> _teamReadyPlayers = [];

    public override string ModuleName => "ILY Lead You";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleAuthor => "Driw0x";
    public override string ModuleDescription => "Inferno 3v3 tactical scenario and economy manager";

    public override void Load(bool hotReload)
    {
        LoadTacticCatalogs();
        RegisterEventHandler<EventRoundPrestart>(OnRoundPrestart);
        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        AddCommandListener("say", OnClientSay, HookMode.Pre);
        AddCommandListener("say_team", OnClientSay, HookMode.Pre);
        AddCommand("css_scenario", "Display your team's scenario and economy.", OnScenarioCommand);
        AddCommand("css_ily_tactic", "Open your team's tactic vote menu.", OnTacticCommand);
        AddCommand("css_ily_ready", "Mark your team ready to end the freeze time.", OnReadyCommand);
        AddCommand("css_ily_position", "Show your current map position for scenario JSON.", OnPositionCommand);
        AddCommand("css_ily_start", "Force the scenario to start without waiting for six players.", OnForceStartCommand);
    }

    private HookResult OnRoundPrestart(EventRoundPrestart gameEvent, GameEventInfo info)
    {
        _tacticVotes.Clear();
        _selectedTactics.Clear();
        _spawnAssignments.Clear();
        _roundEconomies.Clear();
        _teamReadyPlayers.Clear();

        var ctEconomyIndex = Random.Shared.Next(Economies.Length);
        var terroristEconomyIndex = Random.Shared.Next(Economies.Length - 1);
        if (terroristEconomyIndex >= ctEconomyIndex)
        {
            terroristEconomyIndex++;
        }

        _roundEconomies[CsTeam.CounterTerrorist] = Economies[ctEconomyIndex].Tier;
        _roundEconomies[CsTeam.Terrorist] = Economies[terroristEconomyIndex].Tier;
        ApplyEconomy(CsTeam.CounterTerrorist);
        ApplyEconomy(CsTeam.Terrorist);

        return HookResult.Continue;
    }

    private HookResult OnRoundStart(EventRoundStart gameEvent, GameEventInfo info)
    {
        ApplyEconomy(CsTeam.CounterTerrorist);
        ApplyEconomy(CsTeam.Terrorist);
        ShowScenarioToPlayers();
        Server.NextFrame(OpenTacticMenus);
        return HookResult.Continue;
    }

    private HookResult OnPlayerSpawn(EventPlayerSpawn gameEvent, GameEventInfo info)
    {
        var player = gameEvent.Userid;
        if (player is not null && _roundEconomies.ContainsKey(player.Team))
        {
            Server.NextFrame(() =>
            {
                if (player.IsValid && _roundEconomies.ContainsKey(player.Team))
                {
                    ApplyEconomy(player);
                    if (GetGameRules()?.FreezePeriod == true &&
                        _selectedTactics.ContainsKey(CsTeam.CounterTerrorist) &&
                        _selectedTactics.ContainsKey(CsTeam.Terrorist))
                    {
                        if (_spawnAssignments.TryGetValue(player.Team, out var assignments) &&
                            assignments.TryGetValue(player.Slot, out var assignment))
                        {
                            TeleportToTacticalSpawn(player, assignment);
                        }
                        else
                        {
                            TryAssignTacticalSpawns();
                        }
                    }
                }
            });
        }

        return HookResult.Continue;
    }

    private HookResult OnClientSay(CCSPlayerController? player, CommandInfo command)
    {
        if (player is null || !player.IsValid)
        {
            return HookResult.Continue;
        }

        var message = command.ArgString.Trim();
        if (message.Length >= 2 && message[0] == '"' && message[^1] == '"')
        {
            message = message[1..^1].Trim();
        }

        if (message.Equals(".info", StringComparison.OrdinalIgnoreCase))
        {
            ShowScenario(player);
            return HookResult.Handled;
        }

        return HookResult.Continue;
    }

    private void OnReadyCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player is { IsValid: true })
        {
            HandleReady(player);
        }
    }

    private void OnPositionCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player is not { IsValid: true })
        {
            return;
        }

        var pawn = player.PlayerPawn.Value;
        var sceneNode = pawn?.CBodyComponent?.SceneNode;
        if (pawn is null || !pawn.IsValid || sceneNode is null)
        {
            player.PrintToChat("Impossible de lire votre position actuelle.");
            return;
        }

        var origin = sceneNode.AbsOrigin;
        var angles = pawn.EyeAngles;
        player.PrintToChat($"Carte: {Server.MapName}");
        player.PrintToChat($"origin: {{\"x\":{FormatCoordinate(origin.X)},\"y\":{FormatCoordinate(origin.Y)},\"z\":{FormatCoordinate(origin.Z)}}}");
        player.PrintToChat($"angles: {{\"pitch\":{FormatCoordinate(angles.X)},\"yaw\":{FormatCoordinate(angles.Y)},\"roll\":{FormatCoordinate(angles.Z)}}}");
    }

    private void HandleReady(CCSPlayerController player)
    {
        if (player.Team != CsTeam.CounterTerrorist && player.Team != CsTeam.Terrorist)
        {
            player.PrintToChat("Seuls les joueurs CT ou Terros peuvent indiquer que leur équipe est prête.");
            return;
        }

        var gameRules = GetGameRules();
        if (gameRules is null || !gameRules.FreezePeriod)
        {
            player.PrintToChat("La commande !ily_ready est disponible pendant le freeze time.");
            return;
        }

        if (!_selectedTactics.ContainsKey(player.Team))
        {
            player.PrintToChat("Choisissez d'abord la tactique de votre équipe avec le menu !ily_tactic.");
            return;
        }

        _teamReadyPlayers[player.Team] = player.SteamID;
        var ctReady = IsTeamReady(CsTeam.CounterTerrorist);
        var terroristReady = IsTeamReady(CsTeam.Terrorist);
        Broadcast($"État ready — CT: {(ctReady ? "prêts" : "en attente")} | Terros: {(terroristReady ? "prêts" : "en attente")}.");

        if (!ctReady || !terroristReady)
        {
            return;
        }

        gameRules.GameStartTime = Server.CurrentTime;
        gameRules.FreezePeriod = false;
        Broadcast("Les deux équipes sont prêtes, la manche commence !");
    }

    private bool IsTeamReady(CsTeam team) =>
        _teamReadyPlayers.TryGetValue(team, out var readyPlayerId) &&
        GetHumanPlayers(team).Any(player => player.SteamID == readyPlayerId);

    private static CCSGameRules? GetGameRules() =>
        Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
            .FirstOrDefault()?.GameRules;

    private void LoadTacticCatalogs()
    {
        var pluginDirectory = Path.GetDirectoryName(typeof(ILYLeadYouPlugin).Assembly.Location);
        if (string.IsNullOrWhiteSpace(pluginDirectory))
        {
            Logger.LogError("Could not locate the plugin directory; tactic JSON files cannot be loaded.");
            return;
        }

        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        foreach (var (team, teamFolder) in new[]
                 {
                     (CsTeam.CounterTerrorist, "CT"),
                     (CsTeam.Terrorist, "T")
                 })
        {
            foreach (var economy in Economies)
            {
                var filePath = Path.Combine(
                    pluginDirectory,
                    "scenarios",
                    teamFolder,
                    economy.Tier.ToString(),
                    "tactics.json");

                if (!File.Exists(filePath))
                {
                    Logger.LogError("Tactic configuration is missing: {FilePath}", filePath);
                    continue;
                }

                try
                {
                    var catalog = JsonSerializer.Deserialize<TacticCatalog>(File.ReadAllText(filePath), jsonOptions);
                    if (catalog is null || !IsValidCatalog(catalog))
                    {
                        Logger.LogError("Tactic configuration is empty or invalid: {FilePath}", filePath);
                        continue;
                    }

                    _tacticCatalogs[(team, economy.Tier)] = catalog.Tactics;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
                {
                    Logger.LogError(exception, "Failed to load tactic configuration: {FilePath}", filePath);
                }
            }
        }
    }

    private static bool IsValidCatalog(TacticCatalog catalog) =>
        catalog.Tactics is { Count: > 0 } &&
        catalog.Tactics.All(option => option is not null &&
            !string.IsNullOrWhiteSpace(option.Id) &&
            !option.Id.Any(char.IsWhiteSpace) &&
            !string.IsNullOrWhiteSpace(option.Label) &&
            !string.IsNullOrWhiteSpace(option.Map) &&
            option.Positions is { Count: > 0 } &&
            option.Positions.All(position =>
                position is not null &&
                position.Slot > 0 &&
                !string.IsNullOrWhiteSpace(position.Zone) &&
                !string.IsNullOrWhiteSpace(position.Instruction) &&
                (position.Spawn is null || IsValidSpawn(position.Spawn))) &&
            option.Positions.Select(position => position.Slot).Distinct().Count() == option.Positions.Count) &&
        catalog.Tactics.All(option =>
            option.Positions.Where(position => position.Spawn is not null)
                .Select(position => GetSpawnLocation(position.Spawn!.Origin))
                .Distinct()
                .Count() == option.Positions.Count(position => position.Spawn is not null)) &&
        catalog.Tactics.Select(option => option.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() == catalog.Tactics.Count;

    private static bool IsValidSpawn(TacticalSpawn spawn) =>
        spawn.Origin is not null &&
        spawn.Angles is not null &&
        float.IsFinite(spawn.Origin.X) &&
        float.IsFinite(spawn.Origin.Y) &&
        float.IsFinite(spawn.Origin.Z) &&
        float.IsFinite(spawn.Angles.Pitch) &&
        float.IsFinite(spawn.Angles.Yaw) &&
        float.IsFinite(spawn.Angles.Roll);

    private static SpawnLocation GetSpawnLocation(SpawnOrigin origin) =>
        new(origin.X, origin.Y, origin.Z);

    private List<TacticalOption> GetAvailableTactics(CsTeam team) =>
        _roundEconomies.TryGetValue(team, out var economy) &&
        _tacticCatalogs.TryGetValue((team, economy), out var tactics)
            ? tactics
            : [];

    private void OnScenarioCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player is null)
        {
            return;
        }

        ShowScenario(player);
    }

    private void OnForceStartCommand(CCSPlayerController? player, CommandInfo command)
    {
        player?.PrintToChat("Démarrage forcé du scénario (!ily_start).");
        CounterStrikeSharp.API.Server.ExecuteCommand("mp_warmup_end");
    }
    
    private void OnTacticCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player is null || !player.IsValid ||
            (player.Team != CsTeam.CounterTerrorist && player.Team != CsTeam.Terrorist))
        {
            player?.PrintToChat("Cette commande est réservée aux CT et aux Terros.");
            return;
        }

        OpenTacticMenu(player);
    }

    private void OpenTacticMenus()
    {
        foreach (var team in new[] { CsTeam.CounterTerrorist, CsTeam.Terrorist })
        {
            foreach (var player in GetHumanPlayers(team))
            {
                OpenTacticMenu(player);
            }
        }
    }

    private void OpenTacticMenu(CCSPlayerController player)
    {
        if (GetGameRules()?.FreezePeriod != true)
        {
            player.PrintToChat("Le choix de tactique est disponible pendant le freeze time.");
            return;
        }

        var options = GetAvailableTactics(player.Team);
        if (options.Count == 0)
        {
            player.PrintToChat("Aucune tactique n'est configurée pour l'économie de votre équipe.");
            return;
        }

        var economy = _roundEconomies[player.Team];
        var menu = new CenterHtmlMenu($"Tactique {economy}", this)
        {
            PostSelectAction = PostSelectAction.Close
        };

        foreach (var option in options)
        {
            menu.AddMenuOption($"{option.Label} ({option.Id})",
                (selectedPlayer, _) => RecordTacticVote(selectedPlayer, option.Id));
        }

        menu.Open(player);
    }

    private void RecordTacticVote(CCSPlayerController player, string tacticId)
    {
        if (!player.IsValid ||
            (player.Team != CsTeam.CounterTerrorist && player.Team != CsTeam.Terrorist))
        {
            return;
        }

        if (GetGameRules()?.FreezePeriod != true)
        {
            player.PrintToChat("Le vote est fermé: le freeze time est terminé.");
            return;
        }

        var options = GetAvailableTactics(player.Team);
        var selectedOption = options.FirstOrDefault(option =>
            option.Id.Equals(tacticId, StringComparison.OrdinalIgnoreCase));
        if (selectedOption is null)
        {
            player.PrintToChat("Ce choix n'est plus disponible; rouvrez le menu avec !ily_tactic.");
            return;
        }

        if (!_tacticVotes.TryGetValue(player.Team, out var votes))
        {
            votes = [];
            _tacticVotes[player.Team] = votes;
        }

        votes[player.SteamID] = selectedOption.Id;
        var teamPlayers = GetHumanPlayers(player.Team);
        var voteCount = teamPlayers.Count(member => votes.ContainsKey(member.SteamID));
        if (teamPlayers.Count > 0 && voteCount == teamPlayers.Count)
        {
            var leadingVote = teamPlayers
                .GroupBy(member => votes[member.SteamID], StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count())
                .First();

            if (leadingVote.Count() > teamPlayers.Count / 2)
            {
                var winner = options.First(option =>
                    option.Id.Equals(leadingVote.Key, StringComparison.OrdinalIgnoreCase));
                var changed = !_selectedTactics.TryGetValue(player.Team, out var previous) ||
                    !previous.Id.Equals(winner.Id, StringComparison.OrdinalIgnoreCase);
                _selectedTactics[player.Team] = winner;
                if (changed)
                {
                    _spawnAssignments.Clear();
                    TryAssignTacticalSpawns();
                }

                BroadcastToTeam(player.Team, $"Tactique retenue: {winner.Label}.");
            }
            else
            {
                votes.Clear();
                _selectedTactics.Remove(player.Team);
                _spawnAssignments.Clear();
                BroadcastToTeam(player.Team, "Égalité: revote nécessaire pour choisir la tactique.");
            }
        }
        else
        {
            BroadcastToTeam(player.Team, $"Vote enregistré ({voteCount}/{teamPlayers.Count}).");
        }

        ShowScenarioToPlayers();
    }

    private bool TryAssignTacticalSpawns()
    {
        if (GetGameRules()?.FreezePeriod != true)
        {
            return false;
        }

        var teams = new[] { CsTeam.CounterTerrorist, CsTeam.Terrorist };
        if (teams.Any(team => !_selectedTactics.ContainsKey(team)))
        {
            return false;
        }

        var playersByTeam = teams.ToDictionary(team => team, GetPlayers);
        foreach (var team in teams)
        {
            var tactic = _selectedTactics[team];
            if (!tactic.Map.Equals(Server.MapName, StringComparison.OrdinalIgnoreCase))
            {
                BroadcastToTeam(team, $"Les positions de cette tactique sont prévues pour {tactic.Map}, pas {Server.MapName}.");
                Logger.LogWarning("Cannot place {Team} players: tactic {TacticId} is for map {TacticMap}, current map is {CurrentMap}.",
                    team, tactic.Id, tactic.Map, Server.MapName);
                return false;
            }

            var availableSpawnCount = tactic.Positions.Count(position => position.Spawn is not null);
            if (availableSpawnCount < playersByTeam[team].Count)
            {
                BroadcastToTeam(team, $"Positions insuffisantes: {availableSpawnCount} configurée(s) pour {playersByTeam[team].Count} joueur(s).");
                Logger.LogWarning("Not enough spawn points for {Team} tactic {TacticId}: {SpawnCount} points for {PlayerCount} players.",
                    team, tactic.Id, availableSpawnCount, playersByTeam[team].Count);
                return false;
            }
        }

        Dictionary<CsTeam, Dictionary<int, TacticalPosition>>? assignments = null;
        for (var attempt = 0; attempt < 64 && assignments is null; attempt++)
        {
            var occupiedPositions = new HashSet<SpawnLocation>();
            var candidateAssignments = new Dictionary<CsTeam, Dictionary<int, TacticalPosition>>();
            var teamOrder = teams.ToList();
            Shuffle(teamOrder);
            var validAssignment = true;

            foreach (var team in teamOrder)
            {
                if (playersByTeam[team].Count == 0)
                {
                    candidateAssignments[team] = [];
                    continue;
                }

                var candidates = _selectedTactics[team].Positions
                    .Where(position => position.Spawn is not null)
                    .ToList();
                Shuffle(candidates);

                var selectedPositions = new List<TacticalPosition>();
                foreach (var candidate in candidates)
                {
                    if (occupiedPositions.Add(GetSpawnLocation(candidate.Spawn!.Origin)))
                    {
                        selectedPositions.Add(candidate);
                    }

                    if (selectedPositions.Count == playersByTeam[team].Count)
                    {
                        break;
                    }
                }

                if (selectedPositions.Count != playersByTeam[team].Count)
                {
                    validAssignment = false;
                    break;
                }

                var players = playersByTeam[team].ToList();
                Shuffle(players);
                candidateAssignments[team] = players
                    .Select((player, index) => (PlayerSlot: player.Slot, Position: selectedPositions[index]))
                    .ToDictionary(assignment => assignment.PlayerSlot, assignment => assignment.Position);
            }

            if (validAssignment)
            {
                assignments = candidateAssignments;
            }
        }

        if (assignments is null)
        {
            Broadcast("Positions incompatibles: vérifiez qu'aucune position T et CT ne partage les mêmes coordonnées.");
            Logger.LogWarning("Could not assign unique spawn coordinates to both teams; check the selected tactic JSON files.");
            return false;
        }

        _spawnAssignments.Clear();
        foreach (var team in teams)
        {
            _spawnAssignments[team] = assignments[team];
            foreach (var player in playersByTeam[team])
            {
                if (assignments[team].TryGetValue(player.Slot, out var position))
                {
                    TeleportToTacticalSpawn(player, position);
                }
            }

            BroadcastToTeam(team, "Positions de départ attribuées aléatoirement sans doublon.");
        }

        return true;
    }

    private static void Shuffle<T>(List<T> values)
    {
        for (var index = values.Count - 1; index > 0; index--)
        {
            var otherIndex = Random.Shared.Next(index + 1);
            (values[index], values[otherIndex]) = (values[otherIndex], values[index]);
        }
    }

    private static bool TeleportToTacticalSpawn(CCSPlayerController player, TacticalPosition position)
    {
        if (!player.IsValid ||
            position.Spawn is not { Origin: not null, Angles: not null } spawn ||
            player.PlayerPawn.Value is not { IsValid: true } pawn)
        {
            return false;
        }

        pawn.Teleport(
            new Vector(spawn.Origin.X, spawn.Origin.Y, spawn.Origin.Z),
            new QAngle(spawn.Angles.Pitch, spawn.Angles.Yaw, spawn.Angles.Roll),
            new Vector(0, 0, 0));
        player.PrintToChat($"Votre position de départ: slot {position.Slot} - {position.Zone}.");
        return true;
    }

    private static List<CCSPlayerController> GetPlayers(CsTeam team) =>
        Utilities.GetPlayers()
            .Where(player => player.IsValid && player.Team == team)
            .ToList();

    private static List<CCSPlayerController> GetHumanPlayers(CsTeam team) =>
        GetPlayers(team)
            .Where(player => !player.IsBot && !player.IsHLTV)
            .ToList();

    private void ApplyEconomy(CsTeam team)
    {
        if (!_roundEconomies.TryGetValue(team, out var tier))
        {
            return;
        }

        var cash = Economies.First(economy => economy.Tier == tier).Cash;
        foreach (var player in GetPlayers(team))
        {
            ApplyEconomy(player, cash);
        }
    }

    private void ApplyEconomy(CCSPlayerController player)
    {
        if (_roundEconomies.TryGetValue(player.Team, out var tier))
        {
            var cash = Economies.First(economy => economy.Tier == tier).Cash;
            ApplyEconomy(player, cash);
        }
    }

    private static void ApplyEconomy(CCSPlayerController player, int cash)
    {
        if (player.InGameMoneyServices is not null)
        {
            player.InGameMoneyServices.Account = cash;
        }
    }

    private void ShowScenarioToPlayers()
    {
        foreach (var player in Utilities.GetPlayers().Where(player => player.IsValid))
        {
            ShowScenario(player);
        }
    }

    private void ShowScenario(CCSPlayerController player)
    {
        var economy = _roundEconomies.TryGetValue(player.Team, out var tier)
            ? $"{tier} ({Economies.First(entry => entry.Tier == tier).Cash}$)"
            : "En attente";
        player.PrintToChat("ILY — Inferno 3v3");
        player.PrintToChat($"Économie: {economy}");
        if (player.Team == CsTeam.CounterTerrorist || player.Team == CsTeam.Terrorist)
        {
            ShowTeamTactic(player, player.Team);
            player.PrintToChat("Commandes: !scenario | .info | !ily_tactic | !ily_ready | !ily_position | !ily_start");
        }
        else
        {
            player.PrintToChat("Les informations tactiques sont réservées aux joueurs de chaque équipe.");
            player.PrintToChat("Commandes: !scenario | .info | !ily_start");
        }

        player.PrintToChat("Cartes: Inferno, Anubis | Choix: Cache, Dust 2, Ancient | Autres: Overpass, Vertigo, Mirage...");
    }

    private void ShowTeamTactic(CCSPlayerController player, CsTeam team)
    {
        var options = GetAvailableTactics(team);
        if (options.Count == 0)
        {
            player.PrintToChat("Aucune tactique n'est configurée pour cette équipe et son économie.");
            return;
        }

        if (_selectedTactics.TryGetValue(team, out var selected))
        {
            player.PrintToChat($"Tactique choisie: {selected.Label} ({selected.Id})");
            foreach (var position in selected.Positions)
            {
                player.PrintToChat($"{position.Slot}. {position.Zone}: {position.Instruction}");
            }

            return;
        }

        var players = GetHumanPlayers(team);
        var voteCount = _tacticVotes.TryGetValue(team, out var votes)
            ? players.Count(member => votes.ContainsKey(member.SteamID))
            : 0;
        player.PrintToChat($"Tactique à choisir — votes: {voteCount}/{players.Count}");
        player.PrintToChat($"Choix pour l'économie actuelle: {string.Join(" | ", options.Select(option => $"{option.Id}: {option.Label}"))}");
    }

    private void BroadcastToTeam(CsTeam team, string message)
    {
        foreach (var player in GetPlayers(team))
        {
            player.PrintToChat(message);
        }
    }

    private void Broadcast(string message)
    {
        foreach (var player in Utilities.GetPlayers().Where(player => player.IsValid))
        {
            player.PrintToChat(message);
        }
    }

    private static string FormatCoordinate(float value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);
}
