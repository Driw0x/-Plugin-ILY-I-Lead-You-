using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Plugin;
using CounterStrikeSharp.API.Core.Plugin.Host;
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

    private enum IlyRoundPhase { Idle, Starting, Playing }

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
    }

    private static readonly (EconomyTier Tier, int Cash)[] Economies =
    [
        (EconomyTier.Force, 3500),
        (EconomyTier.Lowbuy, 2500),
        (EconomyTier.Full, 5000),
        (EconomyTier.Eco, 800)
    ];

    private readonly Dictionary<(string Map, CsTeam Team, EconomyTier Tier), List<TacticalOption>> _tacticCatalogs = [];
    private readonly Dictionary<CsTeam, Dictionary<ulong, string>> _tacticVotes = [];
    private readonly Dictionary<CsTeam, TacticalOption> _selectedTactics = [];
    private readonly Dictionary<CsTeam, Dictionary<ulong, int>> _selectedRoles = [];
    private readonly Dictionary<CsTeam, EconomyTier> _roundEconomies = [];
    private readonly Dictionary<CsTeam, ulong> _teamReadyPlayers = [];
    private readonly IPluginManager _pluginManager;
    private bool _warmupSetupStarted;
    private bool _warmupSelectionForRound;
    private IlyRoundPhase _ilyRoundPhase;

    public ILYLeadYouPlugin(IPluginManager pluginManager) => _pluginManager = pluginManager;

    public override string ModuleName => "I Lead You";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleAuthor => "Driw0x";
    public override string ModuleDescription => "3v3 tactical scenario and economy manager";

    public override void Load(bool hotReload)
    {
        LoadTacticCatalogs();
        RegisterEventHandler<EventRoundPrestart>(OnRoundPrestart);
        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        RegisterListener<Listeners.OnMapStart>(_ =>
        {
            _ilyRoundPhase = IlyRoundPhase.Idle;
            Server.NextFrame(StartWarmupWithoutMatchZy);
        });
        AddCommandListener("say", OnClientSay, HookMode.Pre);
        AddCommandListener("say_team", OnClientSay, HookMode.Pre);
        AddCommand("css_ily_tactic", "Open your team's tactic vote menu.", OnTacticCommand);
        AddCommand("css_ily_role", "Choose your role in the selected tactic.", OnRoleCommand);
        AddCommand("css_ily_ready", "Mark your team ready before starting the ILY match.", OnReadyCommand);
        AddCommand("css_ily_start", "Force the scenario to start without waiting for six players.", OnForceStartCommand);
    }

    public override void OnAllPluginsLoaded(bool hotReload) =>
        Server.NextFrame(StartWarmupWithoutMatchZy);

    private HookResult OnRoundPrestart(EventRoundPrestart gameEvent, GameEventInfo info)
    {
        if (!IsCurrentMapSupported()) return HookResult.Continue;

        var warmup = GetGameRules()?.WarmupPeriod == true;
        if (_warmupSetupStarted)
        {
            if (!warmup)
            {
                _warmupSelectionForRound = true;
                _warmupSetupStarted = false;
            }

            ApplyEconomy(CsTeam.CounterTerrorist);
            ApplyEconomy(CsTeam.Terrorist);
            return HookResult.Continue;
        }

        _warmupSelectionForRound = false;
        _tacticVotes.Clear();
        _selectedTactics.Clear();
        _selectedRoles.Clear();
        _roundEconomies.Clear();
        if (warmup)
        {
            _teamReadyPlayers.Clear();
        }

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
        if (!IsCurrentMapSupported()) return HookResult.Continue;

        ApplyEconomy(CsTeam.CounterTerrorist);
        ApplyEconomy(CsTeam.Terrorist);
        ShowScenarioToPlayers();
        var gameRules = GetGameRules();
        var warmup = gameRules?.WarmupPeriod == true;
        if (_ilyRoundPhase == IlyRoundPhase.Starting && gameRules?.WarmupPeriod == false)
        {
            _ilyRoundPhase = IlyRoundPhase.Playing;
        }

        if (_warmupSelectionForRound)
        {
            AnnounceRoleInstructions();
            Server.NextFrame(OpenIncompleteMenus);
        }
        else if (warmup)
        {
            Server.NextFrame(OpenIncompleteMenus);
        }
        else
        {
            Server.NextFrame(OpenTacticMenus);
        }
        return HookResult.Continue;
    }

    private HookResult OnRoundEnd(EventRoundEnd gameEvent, GameEventInfo info)
    {
        if (_ilyRoundPhase == IlyRoundPhase.Playing)
        {
            _ilyRoundPhase = IlyRoundPhase.Idle;
            _warmupSetupStarted = false;
            Server.NextFrame(() =>
            {
                if (IsMatchZyLoaded()) Server.ExecuteCommand("css_match");
                else StartWarmupWithoutMatchZy();
            });
        }

        return HookResult.Continue;
    }

    private HookResult OnPlayerSpawn(EventPlayerSpawn gameEvent, GameEventInfo info)
    {
        if (!IsCurrentMapSupported()) return HookResult.Continue;

        var player = gameEvent.Userid;
        if (player is not null && _roundEconomies.ContainsKey(player.Team))
        {
            Server.NextFrame(() =>
            {
                if (player.IsValid && _roundEconomies.ContainsKey(player.Team))
                {
                    ApplyEconomy(player);
                }
            });
        }

        return HookResult.Continue;
    }

    private HookResult OnClientSay(CCSPlayerController? player, CommandInfo command)
    {
        if (player is null || !player.IsValid || !IsCurrentMapSupported())
        {
            return HookResult.Continue;
        }

        var message = command.ArgString.Trim();
        if (message.Length >= 2 && message[0] == '"' && message[^1] == '"')
        {
            message = message[1..^1].Trim();
        }

        if (message.Equals(".info", StringComparison.OrdinalIgnoreCase) ||
            message.Equals("!info", StringComparison.OrdinalIgnoreCase))
        {
            ShowScenario(player);
            return HookResult.Handled;
        }

        if (message.Equals("!ily_ready", StringComparison.OrdinalIgnoreCase) ||
            message.Equals(".ily_ready", StringComparison.OrdinalIgnoreCase))
        {
            HandleReady(player);
            return HookResult.Handled;
        }

        if (message.Equals(".ily_tactic", StringComparison.OrdinalIgnoreCase))
        {
            OnTacticCommand(player, command);
            return HookResult.Handled;
        }

        if (message.Equals(".ily_role", StringComparison.OrdinalIgnoreCase))
        {
            OnRoleCommand(player, command);
            return HookResult.Handled;
        }

        if (message.Equals(".ily_start", StringComparison.OrdinalIgnoreCase))
        {
            OnForceStartCommand(player, command);
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

    private void HandleReady(CCSPlayerController player)
    {
        if (!IsCurrentMapSupported()) return;

        if (player.Team != CsTeam.CounterTerrorist && player.Team != CsTeam.Terrorist)
        {
            player.PrintToChat("Seuls les joueurs CT ou Terros peuvent indiquer que leur équipe est prête.");
            return;
        }

        var gameRules = GetGameRules();
        if (gameRules is null || !gameRules.WarmupPeriod)
        {
            player.PrintToChat("La commande .ily_ready est disponible avant le match, pendant le warmup.");
            return;
        }

        if (!_selectedTactics.ContainsKey(player.Team) ||
            !_selectedRoles.TryGetValue(player.Team, out var roles) ||
            GetHumanPlayers(player.Team).Any(member => !roles.ContainsKey(member.SteamID)))
        {
            player.PrintToChat("Choisissez d'abord une tactique et un rôle pour chaque joueur de votre équipe.");
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

        StartMatchAfterReady();
    }

    private bool IsTeamReady(CsTeam team)
    {
        if (!_teamReadyPlayers.TryGetValue(team, out var readyPlayerId) ||
            !_selectedTactics.ContainsKey(team) ||
            !_selectedRoles.TryGetValue(team, out var roles))
        {
            return false;
        }

        var players = GetHumanPlayers(team);
        return players.Any(player => player.SteamID == readyPlayerId) &&
            players.All(player => roles.ContainsKey(player.SteamID));
    }

    private static CCSGameRules? GetGameRules() =>
        Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
            .FirstOrDefault()?.GameRules;

    private bool IsCurrentMapSupported() =>
        !string.IsNullOrWhiteSpace(ModuleDirectory) &&
        Directory.Exists(Path.Combine(ModuleDirectory, "scenarios", NormalizeMapName(Server.MapName)));

    private void LoadTacticCatalogs()
    {
        var pluginDirectory = ModuleDirectory;
        if (string.IsNullOrWhiteSpace(pluginDirectory))
        {
            Logger.LogError("Could not locate the plugin directory; tactic JSON files cannot be loaded.");
            return;
        }

        var scenariosDirectory = Path.Combine(pluginDirectory, "scenarios");
        if (!Directory.Exists(scenariosDirectory))
        {
            Logger.LogError("Scenario directory does not exist: {Directory}", scenariosDirectory);
            return;
        }

        string[] mapDirectories;
        try
        {
            mapDirectories = Directory.GetDirectories(scenariosDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Logger.LogError(exception, "Failed to read scenario directory: {Directory}", scenariosDirectory);
            return;
        }

        if (mapDirectories.Length == 0)
        {
            Logger.LogError("No map scenario directories found in: {Directory}", scenariosDirectory);
            return;
        }

        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        foreach (var mapDirectory in mapDirectories)
        {
            var mapName = new DirectoryInfo(mapDirectory).Name;
            foreach (var (team, teamFolder) in new[]
                 {
                     (CsTeam.CounterTerrorist, "CT"),
                     (CsTeam.Terrorist, "T")
                 })
            {
                foreach (var economy in Economies)
                {
                    var filePath = Path.Combine(
                        mapDirectory,
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
                            Logger.LogError("Tactic configuration is empty or invalid for map {Map}: {FilePath}", mapName, filePath);
                            continue;
                        }

                        var normalizedMapName = NormalizeMapName(mapName);
                        _tacticCatalogs[(normalizedMapName, team, economy.Tier)] = catalog.Tactics;
                        Logger.LogInformation(
                            "Loaded {TacticCount} tactics for {Map}/{Team}/{Economy}.",
                            catalog.Tactics.Count,
                            normalizedMapName,
                            team,
                            economy.Tier);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
                    {
                        Logger.LogError(exception, "Failed to load tactic configuration: {FilePath}", filePath);
                    }
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
            option.Positions is { Count: > 0 } &&
            option.Positions.All(position =>
                position is not null &&
                position.Slot > 0 &&
                !string.IsNullOrWhiteSpace(position.Zone) &&
                !string.IsNullOrWhiteSpace(position.Instruction)) &&
            option.Positions.Select(position => position.Slot).Distinct().Count() == option.Positions.Count) &&
        catalog.Tactics.Select(option => option.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() == catalog.Tactics.Count;

    private List<TacticalOption> GetAvailableTactics(CsTeam team) =>
        _roundEconomies.TryGetValue(team, out var economy) &&
        _tacticCatalogs.TryGetValue((NormalizeMapName(Server.MapName), team, economy), out var tactics)
            ? tactics
            : [];

    private static string NormalizeMapName(string mapName)
    {
        var normalized = mapName.Replace('\\', '/').TrimEnd('/');
        var lastSeparator = normalized.LastIndexOf('/');
        if (lastSeparator >= 0)
        {
            normalized = normalized[(lastSeparator + 1)..];
        }

        if (normalized.EndsWith(".bsp", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^4];
        }

        return normalized.ToLowerInvariant();
    }

    private void OnForceStartCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!IsCurrentMapSupported()) return;

        if (GetGameRules()?.WarmupPeriod != true)
        {
            player?.PrintToChat("La commande .ily_start est disponible pendant le warmup.");
            return;
        }

        player?.PrintToChat("Démarrage forcé du scénario avec 15 secondes de freeze time.");
        StartMatchAfterReady();
    }

    private void StartMatchAfterReady()
    {
        if (_ilyRoundPhase != IlyRoundPhase.Idle) return;

        _ilyRoundPhase = IlyRoundPhase.Starting;
        if (IsMatchZyLoaded()) Server.ExecuteCommand("css_sleep");
        Server.ExecuteCommand("mp_freezetime 15");
        Server.ExecuteCommand("mp_warmup_end");
    }

    private bool IsMatchZyLoaded() =>
        _pluginManager.GetLoadedPlugins().Any(plugin =>
            plugin.State == PluginState.Loaded && plugin.Plugin.ModuleName == "MatchZy");

    private void StartWarmupWithoutMatchZy()
    {
        if (!IsCurrentMapSupported() || IsMatchZyLoaded()) return;

        Server.ExecuteCommand("mp_warmup_pausetimer 1");
        Server.ExecuteCommand("mp_warmup_start");
    }

    private void OnTacticCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!IsCurrentMapSupported()) return;

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
        if (!IsCurrentMapSupported()) return;

        var gameRules = GetGameRules();
        if (gameRules?.WarmupPeriod != true && gameRules?.FreezePeriod != true)
        {
            player.PrintToChat("Le choix de tactique est disponible pendant le warmup ou le freeze time.");
            return;
        }

        if (gameRules?.WarmupPeriod == true)
        {
            _warmupSetupStarted = true;
        }

        var options = GetAvailableTactics(player.Team);
        if (options.Count == 0)
        {
            player.PrintToChat("Aucune tactique n'est configurée pour l'économie de votre équipe.");
            return;
        }

        var economy = _roundEconomies[player.Team];
        var menu = new ChatMenu($"Tactique {economy}")
        {
            PostSelectAction = PostSelectAction.Close,
            ExitButton = true
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
        if (!IsCurrentMapSupported() || !player.IsValid ||
            (player.Team != CsTeam.CounterTerrorist && player.Team != CsTeam.Terrorist))
        {
            return;
        }

        var gameRules = GetGameRules();
        if (gameRules?.WarmupPeriod != true && gameRules?.FreezePeriod != true)
        {
            player.PrintToChat("Le vote est fermé: warmup et freeze time terminés.");
            return;
        }

        var options = GetAvailableTactics(player.Team);
        var selectedOption = options.FirstOrDefault(option =>
            option.Id.Equals(tacticId, StringComparison.OrdinalIgnoreCase));
        if (selectedOption is null)
        {
            player.PrintToChat("Ce choix n'est plus disponible; rouvrez le menu avec .ily_tactic.");
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
                _selectedTactics[player.Team] = winner;
                _selectedRoles.Remove(player.Team);

                BroadcastToTeam(player.Team, $"Tactique retenue: {winner.Label}.");
                foreach (var member in teamPlayers)
                {
                    OpenRoleMenu(member);
                }
            }
            else
            {
                votes.Clear();
                _selectedTactics.Remove(player.Team);
                _selectedRoles.Remove(player.Team);
                BroadcastToTeam(player.Team, "Égalité: revote nécessaire pour choisir la tactique.");
            }
        }
        else
        {
            BroadcastToTeam(player.Team, $"Vote enregistré ({voteCount}/{teamPlayers.Count}).");
        }

        ShowScenarioToPlayers();
    }

    private void OnRoleCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!IsCurrentMapSupported()) return;

        if (player is { IsValid: true } &&
            (player.Team == CsTeam.CounterTerrorist || player.Team == CsTeam.Terrorist))
        {
            OpenRoleMenu(player);
        }
    }

    private void OpenRoleMenu(CCSPlayerController player)
    {
        if (!IsCurrentMapSupported()) return;

        var gameRules = GetGameRules();
        if (gameRules?.WarmupPeriod != true && gameRules?.FreezePeriod != true)
        {
            player.PrintToChat("Le choix du rôle est disponible pendant le warmup ou le freeze time.");
            return;
        }

        if (!_selectedTactics.TryGetValue(player.Team, out var tactic))
        {
            player.PrintToChat("Votez d'abord pour une tactique avec .ily_tactic.");
            return;
        }

        var menu = new ChatMenu($"Rôle: {tactic.Label}")
        {
            PostSelectAction = PostSelectAction.Close,
            ExitButton = true
        };
        foreach (var position in tactic.Positions)
        {
            menu.AddMenuOption($"{position.Slot}. {position.Zone}",
                (selectedPlayer, _) => RecordRole(selectedPlayer, position.Slot));
        }

        menu.Open(player);
    }

    private void RecordRole(CCSPlayerController player, int slot)
    {
        if (!IsCurrentMapSupported() || !player.IsValid || !_selectedTactics.TryGetValue(player.Team, out var tactic) ||
            !tactic.Positions.Any(position => position.Slot == slot))
        {
            return;
        }

        var gameRules = GetGameRules();
        if (gameRules?.WarmupPeriod != true && gameRules?.FreezePeriod != true)
        {
            return;
        }

        if (!_selectedRoles.TryGetValue(player.Team, out var roles))
        {
            roles = [];
            _selectedRoles[player.Team] = roles;
        }

        if (roles.Any(role => role.Key != player.SteamID && role.Value == slot))
        {
            player.PrintToChat("Ce rôle est déjà pris. Choisissez-en un autre avec .ily_role.");
            return;
        }

        roles[player.SteamID] = slot;
        var position = tactic.Positions.First(item => item.Slot == slot);
        BroadcastToTeam(player.Team, $"{player.PlayerName} choisit le rôle {slot}: {position.Zone}.");
        if (_warmupSelectionForRound)
        {
            BroadcastToTeam(player.Team, $"{player.PlayerName} — {position.Zone}: {position.Instruction}");
        }
    }

    private void OpenIncompleteMenus()
    {
        foreach (var team in new[] { CsTeam.CounterTerrorist, CsTeam.Terrorist })
        {
            foreach (var player in GetHumanPlayers(team))
            {
                if (!_selectedTactics.ContainsKey(team))
                {
                    OpenTacticMenu(player);
                }
                else if (!_selectedRoles.TryGetValue(team, out var roles) || !roles.ContainsKey(player.SteamID))
                {
                    OpenRoleMenu(player);
                }
            }
        }
    }

    private void AnnounceRoleInstructions()
    {
        AnnounceRoleInstructions(CsTeam.CounterTerrorist);
        AnnounceRoleInstructions(CsTeam.Terrorist);
    }

    private void AnnounceRoleInstructions(CsTeam team)
    {
        if (!_selectedTactics.TryGetValue(team, out var tactic) ||
            !_selectedRoles.TryGetValue(team, out var roles))
        {
            return;
        }

        foreach (var player in GetHumanPlayers(team))
        {
            if (roles.TryGetValue(player.SteamID, out var slot))
            {
                var position = tactic.Positions.First(item => item.Slot == slot);
                BroadcastToTeam(team, $"{player.PlayerName} — {position.Zone}: {position.Instruction}");
            }
        }
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
        player.PrintToChat("ILY — 3v3");
        player.PrintToChat($"Économie: {economy}");
        if (player.Team == CsTeam.CounterTerrorist || player.Team == CsTeam.Terrorist)
        {
            ShowTeamTactic(player, player.Team);
            player.PrintToChat("Commandes: .info / !info | .ily_ready / !ily_ready | .ily_tactic / !ily_tactic | .ily_role / !ily_role | .ily_start / !ily_start");
        }
        else
        {
            player.PrintToChat("Les informations tactiques sont réservées aux joueurs de chaque équipe.");
            player.PrintToChat("Commandes: .info / !info | .ily_start / !ily_start");
        }

        player.PrintToChat("Cartes: Inferno, Anubis | Choix: Cache, Dust 2, Ancient | Autres: Overpass, Vertigo, Mirage...");
    }

    private void ShowTeamTactic(CCSPlayerController player, CsTeam team)
    {
        var options = GetAvailableTactics(team);
        if (options.Count == 0)
        {
            var sideFolder = team == CsTeam.CounterTerrorist ? "CT" : "T";
            var economyFolder = _roundEconomies.TryGetValue(team, out var economy)
                ? economy.ToString()
                : "Eco inconnue";
            player.PrintToChat(
                $"Aucune tactique pour {NormalizeMapName(Server.MapName)}/{sideFolder}/{economyFolder}.");
            return;
        }

        if (_selectedTactics.TryGetValue(team, out var selected))
        {
            player.PrintToChat($"Tactique choisie: {selected.Label} ({selected.Id})");
            foreach (var position in selected.Positions)
            {
                var assigned = _selectedRoles.TryGetValue(team, out var roles)
                    ? GetHumanPlayers(team).FirstOrDefault(member =>
                        roles.TryGetValue(member.SteamID, out var slot) && slot == position.Slot)
                    : null;
                player.PrintToChat($"{position.Slot}. {position.Zone} ({assigned?.PlayerName ?? "libre"}): {position.Instruction}");
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

}
