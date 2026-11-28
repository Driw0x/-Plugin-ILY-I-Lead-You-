using System.Net;
using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Extensions;
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

    private sealed record ChoiceMenu(CsTeam Team, string Title)
    {
        public List<(string Label, Action<CCSPlayerController> Select)> Options { get; } = [];
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
        public string Site { get; set; } = "";
        public List<TacticalPosition> Positions { get; set; } = [];
        public List<TacticalPosition> Intentions { get; set; } = [];
    }

    private sealed class TacticalPosition
    {
        public TacticalPosition() { }
        public int Slot { get; set; }
        public string Zone { get; set; } = "";
        public string Instruction { get; set; } = "";
    }

    private static readonly (EconomyTier Tier, int TerroristCash, int CounterTerroristCash)[] Economies =
    [
        (EconomyTier.Force, 3700, 3900),
        (EconomyTier.Lowbuy, 2800, 2950),
        (EconomyTier.Full, 5500, 5500),
        (EconomyTier.Eco, 800, 800)
    ];

    private readonly Dictionary<(string Map, CsTeam Team, EconomyTier Tier), List<TacticalOption>> _tacticCatalogs = [];
    private readonly Dictionary<CsTeam, Dictionary<ulong, string>> _tacticVotes = [];
    private readonly Dictionary<ulong, string> _siteVotes = [];
    private readonly Dictionary<ulong, string> _modeVotes = [];
    private readonly Dictionary<ulong, TacticalOption> _personalTactics = [];
    private readonly Dictionary<ulong, int> _personalZones = [];
    private readonly Dictionary<ulong, int> _intentVotes = [];
    private string? _selectedSite;
    private string? _selectedMode;
    private int? _selectedIntent;
    private bool _sessionActive;
    private int _configuredRoundCount = 5;
    private int _completedRounds;
    private bool _pauseRequested;
    private bool _paused;
    private readonly HashSet<CsTeam> _unpauseTeams = [];
    private bool? _previousAllTalk;
    private bool? _previousFullAllTalk;
    private int? _previousMaxRounds;
    private bool? _previousMatchCanClinch;
    private bool? _previousHalftime;
    private readonly Dictionary<CsTeam, TacticalOption> _selectedTactics = [];
    private readonly Dictionary<CsTeam, Dictionary<ulong, int>> _selectedRoles = [];
    private readonly Dictionary<ulong, ChoiceMenu> _openMenus = [];
    private readonly Dictionary<CsTeam, EconomyTier> _roundEconomies = [];
    private readonly Dictionary<CsTeam, ulong> _teamReadyPlayers = [];
    private bool _warmupSetupStarted;
    private bool _warmupSelectionForRound;
    private IlyRoundPhase _ilyRoundPhase;

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
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        RegisterEventHandler<EventPlayerTeam>(OnPlayerTeam);
        RegisterListener<Listeners.OnMapStart>(_ =>
        {
            RestoreVoiceChat();
            RestoreMatchRules();
            _ilyRoundPhase = IlyRoundPhase.Idle;
            _sessionActive = false;
            _completedRounds = 0;
            _pauseRequested = false;
            _paused = false;
            _unpauseTeams.Clear();
            ClearSelections();
            Server.NextFrame(StartWarmupWithoutMatchZy);
        });
        AddCommandListener("say", OnClientSay, HookMode.Pre);
        AddCommandListener("say_team", OnClientSay, HookMode.Pre);
        AddCommand("css_ily_help", "Show ILY commands.", OnHelpCommand);
        AddCommand("css_ily_info", "Show the current ILY round information.", OnInfoCommand);
        AddCommand("css_scenario", "Show the current ILY round information.", OnInfoCommand);
        AddCommand("css_ily_tactic", "Open your team's tactic vote menu.", OnTacticCommand);
        AddCommand("css_tactic", "Open your team's tactic vote menu.", OnTacticCommand);
        AddCommand("css_ily_role", "Choose your role in the selected tactic.", OnRoleCommand);
        AddCommand("css_role", "Choose your role in the selected tactic.", OnRoleCommand);
        AddCommand("css_ily_pick", "Select a numbered ILY menu option.", (player, command) =>
        {
            if (player is { IsValid: true }) SelectMenuOption(player, command.GetArg(1));
        });
        AddCommand("css_ily_ready", "Mark your team ready before starting the ILY match.", OnReadyCommand);
        AddCommand("css_ily_forcestart", "Start the ILY round with random choices where needed.", OnForceRoundStartCommand);
        AddCommand("css_ily_start", "Start the ILY selection phase.", OnStartCommand);
        AddCommand("css_ily_stop", "Stop the ILY session.", OnStopCommand);
        AddCommand("css_ily_config", "Choose the number of ILY rounds.", OnConfigCommand);
        AddCommand("css_ily_pause", "Pause ILY after the current round.", OnPauseCommand);
        AddCommand("css_ily_unpause", "Vote to resume ILY.", OnUnpauseCommand);
    }

    public override void OnAllPluginsLoaded(bool hotReload) =>
        Server.NextFrame(StartWarmupWithoutMatchZy);

    public override void Unload(bool hotReload)
    {
        CloseAllMenus();
        RestoreVoiceChat();
        RestoreMatchRules();
    }

    private HookResult OnPlayerDisconnect(EventPlayerDisconnect gameEvent, GameEventInfo info)
    {
        if (gameEvent.Userid is { } player) RemovePlayerChoices(player.SteamID);
        Server.NextFrame(ReevaluateTeamVotes);
        return HookResult.Continue;
    }

    private HookResult OnPlayerTeam(EventPlayerTeam gameEvent, GameEventInfo info)
    {
        if (gameEvent.Oldteam != gameEvent.Team && gameEvent.Userid is { IsValid: true } player)
        {
            RemovePlayerChoices(player.SteamID);
            Server.NextFrame(ReevaluateTeamVotes);
        }
        return HookResult.Continue;
    }

    private void RemovePlayerChoices(ulong steamId)
    {
        _openMenus.Remove(steamId);
        _siteVotes.Remove(steamId);
        _modeVotes.Remove(steamId);
        _intentVotes.Remove(steamId);
        _personalTactics.Remove(steamId);
        _personalZones.Remove(steamId);
        foreach (var votes in _tacticVotes.Values) votes.Remove(steamId);
        foreach (var roles in _selectedRoles.Values) roles.Remove(steamId);
    }

    private HookResult OnRoundPrestart(EventRoundPrestart gameEvent, GameEventInfo info)
    {
        if (!IsCurrentMapSupported() || !_sessionActive) return HookResult.Continue;
        if (_paused) return HookResult.Continue;

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
        ClearSelections();
        if (warmup)
        {
            _teamReadyPlayers.Clear();
        }

        SelectEconomies();
        return HookResult.Continue;
    }

    private void SelectEconomies()
    {
        var ctEconomyIndex = Random.Shared.Next(Economies.Length);
        var terroristEconomyIndex = ctEconomyIndex;
        if (Economies[ctEconomyIndex].Tier != EconomyTier.Eco)
        {
            terroristEconomyIndex = Random.Shared.Next(Economies.Length - 2);
            if (terroristEconomyIndex >= ctEconomyIndex) terroristEconomyIndex++;
        }

        _roundEconomies[CsTeam.CounterTerrorist] = Economies[ctEconomyIndex].Tier;
        _roundEconomies[CsTeam.Terrorist] = Economies[terroristEconomyIndex].Tier;
        ApplyEconomy(CsTeam.CounterTerrorist);
        ApplyEconomy(CsTeam.Terrorist);
    }

    private HookResult OnRoundStart(EventRoundStart gameEvent, GameEventInfo info)
    {
        if (!IsCurrentMapSupported() || !_sessionActive) return HookResult.Continue;

        ApplyEconomy(CsTeam.CounterTerrorist);
        ApplyEconomy(CsTeam.Terrorist);
        ShowScenarioToPlayers();
        var gameRules = GetGameRules();
        if (_ilyRoundPhase == IlyRoundPhase.Starting && gameRules?.WarmupPeriod == false)
        {
            _ilyRoundPhase = IlyRoundPhase.Playing;
        }

        if (_warmupSelectionForRound) AnnounceChoices();
        Server.NextFrame(OpenIncompleteMenus);
        return HookResult.Continue;
    }

    private HookResult OnRoundEnd(EventRoundEnd gameEvent, GameEventInfo info)
    {
        if (_ilyRoundPhase == IlyRoundPhase.Playing)
        {
            _completedRounds++;
            _ilyRoundPhase = IlyRoundPhase.Idle;
            _warmupSetupStarted = false;
            ClearSelections();
            if (_completedRounds >= _configuredRoundCount)
            {
                _sessionActive = false;
                _pauseRequested = false;
                _paused = false;
                _unpauseTeams.Clear();
                RestoreVoiceChat();
                RestoreMatchRules();
                Broadcast($"ILY: {_configuredRoundCount} manches terminées.");
                Server.NextFrame(() =>
                {
                    if (IsMatchZyInstalled()) Server.ExecuteCommand("css_match");
                    else StartWarmupWithoutMatchZy();
                });
            }
            else
            {
                _paused = _pauseRequested;
                _pauseRequested = false;
                _warmupSetupStarted = true;
                Server.NextFrame(() =>
                {
                    if (!_sessionActive) return;
                    Server.ExecuteCommand("mp_freezetime 0");
                    Server.ExecuteCommand("mp_warmup_pausetimer 1");
                    Server.ExecuteCommand("mp_warmup_start");
                    BeginRoundSelection();
                    if (_paused)
                    {
                        EnableVoiceChat();
                        Broadcast("ILY en pause. Choix ouverts; un CT et un Terro doivent utiliser .ily_unpause.");
                    }
                });
            }
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
        if (player is null || !player.IsValid)
        {
            return HookResult.Continue;
        }

        var message = command.ArgString.Trim();
        if (message.Length >= 2 && message[0] == '"' && message[^1] == '"')
        {
            message = message[1..^1].Trim();
        }
        if (message.StartsWith('!')) message = "." + message[1..];

        if (message.StartsWith(".ily_pick ", StringComparison.OrdinalIgnoreCase))
        {
            SelectMenuOption(player, message[".ily_pick ".Length..]);
            return HookResult.Handled;
        }

        if (message.Equals(".ily_help", StringComparison.OrdinalIgnoreCase))
        {
            OnHelpCommand(player, command);
            return HookResult.Handled;
        }

        if (message.Equals(".ily_info", StringComparison.OrdinalIgnoreCase) ||
            message.Equals(".scenario", StringComparison.OrdinalIgnoreCase))
        {
            OnInfoCommand(player, command);
            return HookResult.Handled;
        }

        if (!IsCurrentMapSupported()) return HookResult.Continue;

        if (message.Equals(".ily_ready", StringComparison.OrdinalIgnoreCase))
        {
            HandleReady(player);
            return HookResult.Handled;
        }

        if (message.Equals(".ily_tactic", StringComparison.OrdinalIgnoreCase) ||
            message.Equals(".tactic", StringComparison.OrdinalIgnoreCase))
        {
            OnTacticCommand(player, command);
            return HookResult.Handled;
        }

        if (message.Equals(".ily_role", StringComparison.OrdinalIgnoreCase) ||
            message.Equals(".role", StringComparison.OrdinalIgnoreCase))
        {
            OnRoleCommand(player, command);
            return HookResult.Handled;
        }

        if (message.Equals(".ily_start", StringComparison.OrdinalIgnoreCase))
        {
            OnStartCommand(player, command);
            return HookResult.Handled;
        }

        if (message.Equals(".ily_forcestart", StringComparison.OrdinalIgnoreCase))
        {
            OnForceRoundStartCommand(player, command);
            return HookResult.Handled;
        }

        if (message.Equals(".ily_stop", StringComparison.OrdinalIgnoreCase))
        {
            OnStopCommand(player, command);
            return HookResult.Handled;
        }

        if (message.Equals(".ily_config", StringComparison.OrdinalIgnoreCase))
        {
            OnConfigCommand(player, command);
            return HookResult.Handled;
        }

        if (message.Equals(".ily_pause", StringComparison.OrdinalIgnoreCase))
        {
            OnPauseCommand(player, command);
            return HookResult.Handled;
        }

        if (message.Equals(".ily_unpause", StringComparison.OrdinalIgnoreCase))
        {
            OnUnpauseCommand(player, command);
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
        if (!IsCurrentMapSupported() || !_sessionActive) return;
        if (player.Team != CsTeam.CounterTerrorist && player.Team != CsTeam.Terrorist)
        {
            player.PrintToChat("Seuls les joueurs CT ou Terros peuvent indiquer que leur équipe est prête.");
            return;
        }

        var gameRules = GetGameRules();
        if (_ilyRoundPhase != IlyRoundPhase.Idle || gameRules is null || !gameRules.WarmupPeriod)
        {
            player.PrintToChat("La commande .ily_ready est disponible avant le match, pendant le warmup.");
            return;
        }

        if (!IsTeamSelectionComplete(player.Team))
        {
            player.PrintToChat("Terminez tous les choix de votre équipe avant .ily_ready.");
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

        if (!_paused) StartMatchAfterReady();
    }

    private void OnForceRoundStartCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player is not { IsValid: true }) return;
        ForceRoundStart(player);
    }

    private void ForceRoundStart(CCSPlayerController player)
    {
        if (!IsCurrentMapSupported()) return;
        if (!_sessionActive || _paused || _ilyRoundPhase != IlyRoundPhase.Idle || GetGameRules()?.WarmupPeriod != true)
        {
            player.PrintToChat(_paused
                ? "ILY est en pause. Utilisez .ily_unpause avec les deux équipes avant .ily_forcestart."
                : ".ily_forcestart est disponible pendant les choix d'une manche ILY.");
            return;
        }

        var ctPlayers = GetHumanPlayers(CsTeam.CounterTerrorist);
        var tPlayers = GetHumanPlayers(CsTeam.Terrorist);
        var ctTactics = GetAvailableTactics(CsTeam.CounterTerrorist);
        var tTactics = GetAvailableTactics(CsTeam.Terrorist);
        var availableTTactics = tTactics.Where(tactic => tactic.Positions.Count >= tPlayers.Count).ToList();
        if (ctPlayers.Count == 0 || tPlayers.Count == 0 || ctTactics.Count == 0 || availableTTactics.Count == 0)
        {
            player.PrintToChat("Impossible de lancer: il faut des joueurs dans chaque équipe, des tactiques et assez de rôles T.");
            return;
        }

        _selectedSite ??= ctTactics[Random.Shared.Next(ctTactics.Count)].Site;
        if (_roundEconomies[CsTeam.CounterTerrorist] == EconomyTier.Lowbuy)
            _selectedMode ??= Random.Shared.Next(2) == 0 ? "zones" : "intention";

        var ctOptions = GetCurrentTactics(CsTeam.CounterTerrorist);
        if (IsIndividualCtMode())
        {
            foreach (var member in ctPlayers)
            {
                if (!_personalTactics.TryGetValue(member.SteamID, out var tactic) || !ctOptions.Contains(tactic))
                    _personalTactics[member.SteamID] = tactic = ctOptions[Random.Shared.Next(ctOptions.Count)];
                if (!_personalZones.TryGetValue(member.SteamID, out var zone) || !tactic.Positions.Any(position => position.Slot == zone))
                    _personalZones[member.SteamID] = tactic.Positions[Random.Shared.Next(tactic.Positions.Count)].Slot;
            }
        }
        else
        {
            if (!_selectedTactics.TryGetValue(CsTeam.CounterTerrorist, out var tactic) || !ctOptions.Contains(tactic))
                _selectedTactics[CsTeam.CounterTerrorist] = tactic = ctOptions[Random.Shared.Next(ctOptions.Count)];
            if (_selectedIntent is null || !tactic.Intentions.Any(position => position.Slot == _selectedIntent))
                _selectedIntent = tactic.Intentions[Random.Shared.Next(tactic.Intentions.Count)].Slot;
        }

        if (!_selectedTactics.TryGetValue(CsTeam.Terrorist, out var tTactic) || !availableTTactics.Contains(tTactic))
            _selectedTactics[CsTeam.Terrorist] = tTactic = availableTTactics[Random.Shared.Next(availableTTactics.Count)];
        if (!_selectedRoles.TryGetValue(CsTeam.Terrorist, out var roles))
            _selectedRoles[CsTeam.Terrorist] = roles = [];
        var usedSlots = new HashSet<int>();
        foreach (var member in tPlayers)
            if (!roles.TryGetValue(member.SteamID, out var slot) ||
                !tTactic.Positions.Any(position => position.Slot == slot) || !usedSlots.Add(slot))
                roles.Remove(member.SteamID);
        foreach (var member in tPlayers.Where(member => !roles.ContainsKey(member.SteamID)))
        {
            var available = tTactic.Positions.Where(position => !usedSlots.Contains(position.Slot)).ToList();
            var slot = available[Random.Shared.Next(available.Count)].Slot;
            roles[member.SteamID] = slot;
            usedSlots.Add(slot);
        }

        Broadcast("ILY: choix manquants complétés aléatoirement. Démarrage de la manche.");
        StartMatchAfterReady(true);
    }

    private bool IsTeamReady(CsTeam team)
    {
        if (!_teamReadyPlayers.TryGetValue(team, out var readyPlayerId) ||
            !IsTeamSelectionComplete(team))
        {
            return false;
        }

        var players = GetHumanPlayers(team);
        return players.Any(player => player.SteamID == readyPlayerId);
    }

    private bool IsTeamSelectionComplete(CsTeam team)
    {
        var players = GetHumanPlayers(team);
        if (players.Count == 0) return false;
        if (team == CsTeam.CounterTerrorist)
        {
            if (_selectedSite is null || (_roundEconomies.GetValueOrDefault(team) == EconomyTier.Lowbuy && _selectedMode is null))
                return false;
            return IsIndividualCtMode()
                ? players.All(player => _personalTactics.ContainsKey(player.SteamID) && _personalZones.ContainsKey(player.SteamID))
                : _selectedTactics.ContainsKey(team) && _selectedIntent is not null;
        }

        return _selectedTactics.ContainsKey(team) &&
            _selectedRoles.TryGetValue(team, out var roles) &&
            players.All(player => roles.ContainsKey(player.SteamID));
    }

    private bool IsIndividualCtMode() =>
        _roundEconomies.GetValueOrDefault(CsTeam.CounterTerrorist) is EconomyTier.Full or EconomyTier.Force ||
        _selectedMode == "zones";

    private void ClearSelections()
    {
        CloseAllMenus();
        _tacticVotes.Clear();
        _siteVotes.Clear();
        _modeVotes.Clear();
        _personalTactics.Clear();
        _personalZones.Clear();
        _intentVotes.Clear();
        _selectedSite = null;
        _selectedMode = null;
        _selectedIntent = null;
        _selectedTactics.Clear();
        _selectedRoles.Clear();
        _roundEconomies.Clear();
        _teamReadyPlayers.Clear();
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
                        if (catalog is null || !IsValidCatalog(catalog, team))
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

    private static bool IsValidCatalog(TacticCatalog catalog, CsTeam team) =>
        catalog.Tactics is { Count: > 0 } &&
        catalog.Tactics.All(option => option is not null &&
            !string.IsNullOrWhiteSpace(option.Id) &&
            !option.Id.Any(char.IsWhiteSpace) &&
            !string.IsNullOrWhiteSpace(option.Label) &&
            AreValidPositions(option.Positions) &&
            (team != CsTeam.CounterTerrorist ||
             (option.Site is "A" or "B" && AreValidPositions(option.Intentions)))) &&
        catalog.Tactics.Select(option => option.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() == catalog.Tactics.Count &&
        (team != CsTeam.CounterTerrorist ||
         (catalog.Tactics.Any(option => option.Site == "A") &&
          catalog.Tactics.Any(option => option.Site == "B")));

    private static bool AreValidPositions(List<TacticalPosition>? positions) =>
        positions is { Count: > 0 } &&
        positions.All(position =>
            position is not null &&
            position.Slot > 0 &&
            !string.IsNullOrWhiteSpace(position.Zone) &&
            !string.IsNullOrWhiteSpace(position.Instruction)) &&
        positions.Select(position => position.Slot).Distinct().Count() == positions.Count;

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

    private void OnStartCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!IsCurrentMapSupported()) return;

        if (_sessionActive)
        {
            player?.PrintToChat("La procédure ILY est déjà ouverte.");
            return;
        }

        var map = NormalizeMapName(Server.MapName);
        var missingCatalogs = (
            from team in new[] { (Side: CsTeam.CounterTerrorist, Folder: "CT"), (Side: CsTeam.Terrorist, Folder: "T") }
            from economy in Economies
            where !_tacticCatalogs.ContainsKey((map, team.Side, economy.Tier))
            select $"{team.Folder}/{economy.Tier}/tactics.json").ToArray();
        if (missingCatalogs.Length > 0)
        {
            var message = $"Démarrage ILY impossible. Catalogues manquants ou invalides : {string.Join(", ", missingCatalogs)}.";
            Logger.LogError("{Message}", message);
            player?.PrintToChat(message);
            return;
        }

        var gameRules = GetGameRules();
        if (gameRules is null) return;

        _sessionActive = true;
        _completedRounds = 0;
        _pauseRequested = false;
        _paused = false;
        _unpauseTeams.Clear();
        _warmupSetupStarted = true;
        ClearSelections();
        SaveMatchRules();
        Server.ExecuteCommand("mp_freezetime 0");
        if (IsMatchZyInstalled()) Server.ExecuteCommand("css_sleep");
        Server.ExecuteCommand("mp_warmup_pausetimer 1");
        if (!gameRules.WarmupPeriod)
        {
            Server.ExecuteCommand("mp_warmup_start");
        }
        SelectEconomies();
        Broadcast($"ILY: manche 1/{_configuredRoundCount}. Choisissez vos plans, puis une personne par équipe utilise .ily_ready.");
        Server.NextFrame(OpenIncompleteMenus);
    }

    private void OnConfigCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!IsCurrentMapSupported() || player is not { IsValid: true }) return;
        if (player.Team is not (CsTeam.CounterTerrorist or CsTeam.Terrorist)) return;
        if (_sessionActive)
        {
            player.PrintToChat("Le nombre de manches se choisit avant .ily_start.");
            return;
        }

        var menu = new ChoiceMenu(player.Team, $"ILY: {_configuredRoundCount} manches");
        foreach (var count in new[] { 5, 10, 15 })
            AddMenuOption(menu, $"{count} manches", voter =>
            {
                if (_sessionActive || !voter.IsValid) return;
                _configuredRoundCount = count;
                Broadcast($"ILY: prochaine session de {count} manches.");
            });
        ShowMenu(player, menu);
    }

    private void ShowMenu(CCSPlayerController player, ChoiceMenu menu)
    {
        if (!player.IsValid || player.Team != menu.Team || menu.Options.Count == 0) return;
        _openMenus[player.SteamID] = menu;

        var lines = menu.Options.Select((option, index) => $"{index + 1}. {option.Label}").ToArray();
        var hint = "Ouvrez la radio ILY et choisissez 1–4, ou tapez !ily_pick <numéro>.";
        player.PrintToChat($"ILY — {menu.Title}");
        foreach (var line in lines) player.PrintToChat(line);
        player.PrintToChat(hint);
        var html = string.Join("<br>", lines.Prepend(menu.Title).Append(hint).Select(WebUtility.HtmlEncode));
        void KeepVisible()
        {
            if (!player.IsValid || !_openMenus.TryGetValue(player.SteamID, out var current) || current != menu) return;
            player.PrintToCenterHtml(html, 10);
            AddTimer(9.0f, KeepVisible);
        }
        KeepVisible();
    }

    private static void AddMenuOption(ChoiceMenu menu, string title, Action<CCSPlayerController> selected) =>
        menu.Options.Add((title, selected));

    private void SelectMenuOption(CCSPlayerController player, string argument)
    {
        if (!_openMenus.TryGetValue(player.SteamID, out var menu) || player.Team != menu.Team)
        {
            player.PrintToChat("Aucun choix ouvert. Utilisez !ily_tactic ou !ily_role.");
            return;
        }
        if (!int.TryParse(argument, out var number) || number < 0 || number > menu.Options.Count)
        {
            player.PrintToChat($"Choisissez un numéro de 1 à {menu.Options.Count}.");
            return;
        }
        if (number == 0 && _sessionActive)
        {
            player.PrintToChat("Terminez votre choix avant de fermer le menu.");
            return;
        }

        _openMenus.Remove(player.SteamID);
        player.PrintToCenterHtml(" ", 1);
        if (number > 0) menu.Options[number - 1].Select(player);
    }

    private void CloseAllMenus() => _openMenus.Clear();

    private void CloseTeamMenus(CsTeam team)
    {
        foreach (var steamId in _openMenus.Where(entry => entry.Value.Team == team).Select(entry => entry.Key).ToArray())
            _openMenus.Remove(steamId);
    }

    private void RefreshTeamMenus(CsTeam team)
    {
        CloseTeamMenus(team);
        Server.NextFrame(() => OpenIncompleteMenus(team));
    }

    private void OnPauseCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!IsCurrentMapSupported() || player is not { IsValid: true } ||
            player.Team is not (CsTeam.CounterTerrorist or CsTeam.Terrorist)) return;
        if (!_sessionActive || _ilyRoundPhase != IlyRoundPhase.Playing ||
            _completedRounds + 1 >= _configuredRoundCount)
        {
            player.PrintToChat("La pause se demande pendant une manche ILY suivie d'une autre manche.");
            return;
        }
        if (_pauseRequested) return;
        _pauseRequested = true;
        Broadcast("ILY: pause demandée pour la fin de cette manche.");
    }

    private void OnUnpauseCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!IsCurrentMapSupported() || player is not { IsValid: true } ||
            player.Team is not (CsTeam.CounterTerrorist or CsTeam.Terrorist)) return;
        if (!_sessionActive || !_paused)
        {
            player.PrintToChat("ILY n'est pas en pause.");
            return;
        }

        if (!_unpauseTeams.Add(player.Team)) return;
        if (_unpauseTeams.Count < 2)
        {
            Broadcast($"ILY: équipe {player.Team} prête à reprendre. En attente de l'autre équipe.");
            return;
        }

        _paused = false;
        _unpauseTeams.Clear();
        RestoreVoiceChat();
        if (IsTeamReady(CsTeam.CounterTerrorist) && IsTeamReady(CsTeam.Terrorist))
            StartMatchAfterReady();
    }

    private void BeginRoundSelection()
    {
        if (!_sessionActive) return;
        ClearSelections();
        SelectEconomies();
        Broadcast($"ILY: manche {_completedRounds + 1}/{_configuredRoundCount}. Choisissez vos plans, puis une personne par équipe utilise .ily_ready.");
        Server.NextFrame(OpenIncompleteMenus);
    }

    private void EnableVoiceChat()
    {
        var allTalk = ConVar<bool>.Find("sv_alltalk");
        var fullAllTalk = ConVar<bool>.Find("sv_full_alltalk");
        if (allTalk is not null)
        {
            _previousAllTalk = allTalk.Value;
            allTalk.Value = true;
        }
        if (fullAllTalk is not null)
        {
            _previousFullAllTalk = fullAllTalk.Value;
            fullAllTalk.Value = true;
        }
    }

    private void RestoreVoiceChat()
    {
        if (_previousAllTalk is bool allTalk && ConVar<bool>.Find("sv_alltalk") is { } allTalkConVar)
            allTalkConVar.Value = allTalk;
        if (_previousFullAllTalk is bool fullAllTalk && ConVar<bool>.Find("sv_full_alltalk") is { } fullAllTalkConVar)
            fullAllTalkConVar.Value = fullAllTalk;
        _previousAllTalk = null;
        _previousFullAllTalk = null;
    }

    private void SaveMatchRules()
    {
        _previousMaxRounds = ConVar<int>.Find("mp_maxrounds")?.Value;
        _previousMatchCanClinch = ConVar<bool>.Find("mp_match_can_clinch")?.Value;
        _previousHalftime = ConVar<bool>.Find("mp_halftime")?.Value;
    }

    private static void ApplyMatchRules()
    {
        Server.ExecuteCommand("mp_maxrounds 100");
        Server.ExecuteCommand("mp_match_can_clinch 0");
        Server.ExecuteCommand("mp_halftime 0");
    }

    private void RestoreMatchRules()
    {
        if (_previousMaxRounds is int maxRounds) Server.ExecuteCommand($"mp_maxrounds {maxRounds}");
        if (_previousMatchCanClinch is bool matchCanClinch) Server.ExecuteCommand($"mp_match_can_clinch {(matchCanClinch ? 1 : 0)}");
        if (_previousHalftime is bool halftime) Server.ExecuteCommand($"mp_halftime {(halftime ? 1 : 0)}");
        _previousMaxRounds = null;
        _previousMatchCanClinch = null;
        _previousHalftime = null;
    }

    private void OnStopCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!_sessionActive) return;
        _sessionActive = false;
        _completedRounds = 0;
        _pauseRequested = false;
        _paused = false;
        _unpauseTeams.Clear();
        RestoreVoiceChat();
        RestoreMatchRules();
        _ilyRoundPhase = IlyRoundPhase.Idle;
        _warmupSetupStarted = false;
        _warmupSelectionForRound = false;
        ClearSelections();
        Broadcast("Session ILY arrêtée.");
        if (IsMatchZyInstalled()) Server.ExecuteCommand("css_match");
        else StartWarmupWithoutMatchZy();
    }

    private void StartMatchAfterReady(bool force = false)
    {
        if (_ilyRoundPhase != IlyRoundPhase.Idle || _paused ||
            !force && (!IsTeamReady(CsTeam.CounterTerrorist) || !IsTeamReady(CsTeam.Terrorist))) return;

        CloseAllMenus();
        _ilyRoundPhase = IlyRoundPhase.Starting;
        ApplyMatchRules();
        Server.ExecuteCommand("mp_freezetime 15");
        Server.ExecuteCommand("mp_warmup_end");
    }

    private static bool IsMatchZyInstalled() =>
        File.Exists(Path.Combine(Application.RootDirectory, "plugins", "MatchZy", "MatchZy.dll"));

    private void StartWarmupWithoutMatchZy()
    {
        if (!IsCurrentMapSupported() || IsMatchZyInstalled()) return;

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

    private void OpenTacticMenu(CCSPlayerController player)
    {
        if (!IsCurrentMapSupported()) return;
        if (!_sessionActive) return;

        var gameRules = GetGameRules();
        if (gameRules?.WarmupPeriod != true || _ilyRoundPhase != IlyRoundPhase.Idle || IsTeamReady(player.Team))
        {
            player.PrintToChat("Les choix sont fermés pour votre équipe.");
            return;
        }
        if (gameRules?.WarmupPeriod == true) _warmupSetupStarted = true;

        if (player.Team == CsTeam.Terrorist && _selectedTactics.ContainsKey(player.Team))
        {
            if (!_selectedRoles.TryGetValue(player.Team, out var roles) || !roles.ContainsKey(player.SteamID))
                OpenRoleMenu(player);
            else player.PrintToChat("Votre rôle est déjà choisi.");
            return;
        }

        if (player.Team == CsTeam.CounterTerrorist && _selectedSite is null)
        {
            OpenSiteMenu(player);
            return;
        }
        if (player.Team == CsTeam.CounterTerrorist &&
            _roundEconomies.GetValueOrDefault(player.Team) == EconomyTier.Lowbuy && _selectedMode is null)
        {
            OpenModeMenu(player);
            return;
        }
        if (player.Team == CsTeam.CounterTerrorist && IsIndividualCtMode())
        {
            OpenPersonalCtMenu(player);
            return;
        }
        if (player.Team == CsTeam.CounterTerrorist &&
            _selectedTactics.TryGetValue(player.Team, out var selected) && _selectedIntent is null)
        {
            OpenIntentMenu(player, selected);
            return;
        }
        if (player.Team == CsTeam.CounterTerrorist && _selectedTactics.ContainsKey(player.Team))
        {
            player.PrintToChat("Le plan commun CT est déjà choisi.");
            return;
        }

        var options = GetCurrentTactics(player.Team);
        if (options.Count == 0)
        {
            player.PrintToChat("Aucune tactique n'est configurée pour l'économie de votre équipe.");
            return;
        }

        var economy = _roundEconomies[player.Team];
        var menu = new ChoiceMenu(player.Team, $"Tactique {(economy == EconomyTier.Eco ? "Pistol round" : economy)}");

        foreach (var option in options)
        {
            AddMenuOption(menu, $"{option.Label} ({option.Id})",
                selectedPlayer => RecordTacticVote(selectedPlayer, option.Id));
        }

        ShowMenu(player, menu);
    }

    private List<TacticalOption> GetCurrentTactics(CsTeam team) =>
        team == CsTeam.CounterTerrorist
            ? GetAvailableTactics(team).Where(option => option.Site.Equals(_selectedSite, StringComparison.OrdinalIgnoreCase)).ToList()
            : GetAvailableTactics(team);

    private void OpenSiteMenu(CCSPlayerController player)
    {
        var menu = new ChoiceMenu(CsTeam.CounterTerrorist, "CT: site A ou B");
        foreach (var site in new[] { "A", "B" })
            AddMenuOption(menu, $"Site {site}", voter => RecordSiteVote(voter, site));
        ShowMenu(player, menu);
    }

    private void RecordSiteVote(CCSPlayerController player, string site)
    {
        if (!CanChoose(player.Team) || player.Team != CsTeam.CounterTerrorist || _selectedSite is not null) return;
        _siteVotes[player.SteamID] = site;
        ResolveSiteVote();
    }

    private void ResolveSiteVote()
    {
        if (TryMajority(CsTeam.CounterTerrorist, _siteVotes, out var winner))
        {
            _selectedSite = winner;
            BroadcastToTeam(CsTeam.CounterTerrorist, $"Site CT choisi: {winner}.");
            RefreshTeamMenus(CsTeam.CounterTerrorist);
        }
    }

    private void OpenModeMenu(CCSPlayerController player)
    {
        var menu = new ChoiceMenu(CsTeam.CounterTerrorist, "CT Lowbuy: mode");
        AddMenuOption(menu, "Zones individuelles", voter => RecordModeVote(voter, "zones"));
        AddMenuOption(menu, "Intention collective", voter => RecordModeVote(voter, "intention"));
        ShowMenu(player, menu);
    }

    private void RecordModeVote(CCSPlayerController player, string mode)
    {
        if (!CanChoose(player.Team) || player.Team != CsTeam.CounterTerrorist || _selectedMode is not null) return;
        _modeVotes[player.SteamID] = mode;
        ResolveModeVote();
    }

    private void ResolveModeVote()
    {
        if (TryMajority(CsTeam.CounterTerrorist, _modeVotes, out var winner))
        {
            _selectedMode = winner;
            BroadcastToTeam(CsTeam.CounterTerrorist, $"Mode Lowbuy CT: {winner}.");
            RefreshTeamMenus(CsTeam.CounterTerrorist);
        }
    }

    private bool TryMajority(CsTeam team, Dictionary<ulong, string> votes, out string winner)
    {
        winner = "";
        var players = GetHumanPlayers(team);
        if (players.Count == 0 || players.Any(member => !votes.ContainsKey(member.SteamID))) return false;
        var leading = players.GroupBy(member => votes[member.SteamID]).OrderByDescending(group => group.Count()).First();
        if (leading.Count() <= players.Count / 2)
        {
            votes.Clear();
            BroadcastToTeam(team, "Égalité: revote nécessaire.");
            RefreshTeamMenus(team);
            return false;
        }
        winner = leading.Key;
        return true;
    }

    private void OpenPersonalCtMenu(CCSPlayerController player)
    {
        if (_personalTactics.TryGetValue(player.SteamID, out var tactic))
        {
            OpenZoneMenu(player, tactic);
            return;
        }
        var menu = new ChoiceMenu(CsTeam.CounterTerrorist, $"CT site {_selectedSite}: approche");
        foreach (var option in GetCurrentTactics(player.Team))
            AddMenuOption(menu, option.Label, voter =>
            {
                if (!CanChoose(voter.Team) || voter.Team != CsTeam.CounterTerrorist || !GetCurrentTactics(voter.Team).Contains(option)) return;
                _personalTactics[voter.SteamID] = option;
                Server.NextFrame(() => OpenZoneMenu(voter, option));
            });
        ShowMenu(player, menu);
    }

    private void OpenZoneMenu(CCSPlayerController player, TacticalOption tactic)
    {
        var menu = new ChoiceMenu(CsTeam.CounterTerrorist, "CT: votre zone");
        foreach (var position in tactic.Positions)
            AddMenuOption(menu, position.Zone, voter =>
            {
                if (!CanChoose(voter.Team) || !_personalTactics.TryGetValue(voter.SteamID, out var current) || current != tactic) return;
                _personalZones[voter.SteamID] = position.Slot;
                voter.PrintToChat($"Votre zone: {position.Zone}. Communiquez votre choix pendant le freeze time.");
            });
        ShowMenu(player, menu);
    }

    private void OpenIntentMenu(CCSPlayerController player, TacticalOption tactic)
    {
        var menu = new ChoiceMenu(CsTeam.CounterTerrorist, "CT: intention commune");
        foreach (var position in tactic.Intentions)
            AddMenuOption(menu, position.Zone, voter => RecordIntentVote(voter, tactic, position.Slot));
        ShowMenu(player, menu);
    }

    private void RecordIntentVote(CCSPlayerController player, TacticalOption tactic, int slot)
    {
        if (!CanChoose(player.Team) || player.Team != CsTeam.CounterTerrorist ||
            !_selectedTactics.TryGetValue(player.Team, out var current) || current != tactic || _selectedIntent is not null) return;
        _intentVotes[player.SteamID] = slot;
        ResolveIntentVote(tactic);
    }

    private void ResolveIntentVote(TacticalOption tactic)
    {
        var players = GetHumanPlayers(CsTeam.CounterTerrorist);
        if (players.Count == 0 || players.Any(member => !_intentVotes.ContainsKey(member.SteamID))) return;
        var leading = players.GroupBy(member => _intentVotes[member.SteamID]).OrderByDescending(group => group.Count()).First();
        if (leading.Count() <= players.Count / 2)
        {
            _intentVotes.Clear();
            BroadcastToTeam(CsTeam.CounterTerrorist, "Égalité: revote de l'intention.");
            RefreshTeamMenus(CsTeam.CounterTerrorist);
            return;
        }
        _selectedIntent = leading.Key;
        CloseTeamMenus(CsTeam.CounterTerrorist);
        BroadcastToTeam(CsTeam.CounterTerrorist, $"Intention CT: {tactic.Intentions.First(position => position.Slot == _selectedIntent).Zone}.");
    }

    private bool CanChoose(CsTeam team) => _sessionActive && _ilyRoundPhase == IlyRoundPhase.Idle &&
        GetGameRules()?.WarmupPeriod == true && !IsTeamReady(team);

    private void RecordTacticVote(CCSPlayerController player, string tacticId)
    {
        if (!IsCurrentMapSupported() || !CanChoose(player.Team) || !player.IsValid ||
            _selectedTactics.ContainsKey(player.Team) ||
            (player.Team != CsTeam.CounterTerrorist && player.Team != CsTeam.Terrorist))
        {
            return;
        }

        var options = GetCurrentTactics(player.Team);
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
        ResolveTacticVote(player.Team, votes, reportProgress: true);
        ShowScenarioToPlayers();
    }

    private void ResolveTacticVote(CsTeam team, Dictionary<ulong, string> votes, bool reportProgress = false)
    {
        var teamPlayers = GetHumanPlayers(team);
        var voteCount = teamPlayers.Count(member => votes.ContainsKey(member.SteamID));
        if (teamPlayers.Count > 0 && voteCount == teamPlayers.Count)
        {
            var leadingVote = teamPlayers
                .GroupBy(member => votes[member.SteamID], StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count())
                .First();

            if (leadingVote.Count() > teamPlayers.Count / 2)
            {
                var winner = GetCurrentTactics(team).First(option =>
                    option.Id.Equals(leadingVote.Key, StringComparison.OrdinalIgnoreCase));
                _selectedTactics[team] = winner;
                _selectedRoles.Remove(team);

                BroadcastToTeam(team, $"Tactique retenue: {winner.Label}.");
                RefreshTeamMenus(team);
            }
            else
            {
                votes.Clear();
                BroadcastToTeam(team, "Égalité: revote nécessaire pour choisir la tactique.");
                RefreshTeamMenus(team);
            }
        }
        else if (reportProgress)
        {
            BroadcastToTeam(team, $"Vote enregistré ({voteCount}/{teamPlayers.Count}).");
        }
    }

    private void ReevaluateTeamVotes()
    {
        const CsTeam ct = CsTeam.CounterTerrorist;
        const CsTeam t = CsTeam.Terrorist;
        if (CanChoose(ct))
        {
            if (_selectedSite is null) ResolveSiteVote();
            else if (_roundEconomies.GetValueOrDefault(ct) == EconomyTier.Lowbuy && _selectedMode is null)
                ResolveModeVote();
            else if (!IsIndividualCtMode())
            {
                if (_selectedTactics.TryGetValue(ct, out var tactic))
                {
                    if (_selectedIntent is null) ResolveIntentVote(tactic);
                }
                else if (_tacticVotes.TryGetValue(ct, out var ctVotes))
                    ResolveTacticVote(ct, ctVotes);
            }
        }
        if (CanChoose(t) && !_selectedTactics.ContainsKey(t) && _tacticVotes.TryGetValue(t, out var tVotes))
            ResolveTacticVote(t, tVotes);
    }

    private void OnRoleCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!IsCurrentMapSupported()) return;

        if (player is { IsValid: true } && player.Team == CsTeam.Terrorist)
        {
            OpenRoleMenu(player);
        }
        else if (player is { IsValid: true } && player.Team == CsTeam.CounterTerrorist)
        {
            OpenTacticMenu(player);
        }
    }

    private void OpenRoleMenu(CCSPlayerController player)
    {
        if (!IsCurrentMapSupported()) return;

        if (!CanChoose(player.Team))
        {
            player.PrintToChat("Les choix sont fermés pour votre équipe.");
            return;
        }

        if (!_selectedTactics.TryGetValue(player.Team, out var tactic))
        {
            player.PrintToChat("Votez d'abord pour une tactique avec .ily_tactic.");
            return;
        }

        var menu = new ChoiceMenu(CsTeam.Terrorist, $"Rôle: {tactic.Label}");
        foreach (var position in tactic.Positions)
        {
            AddMenuOption(menu, position.Zone,
                selectedPlayer => RecordRole(selectedPlayer, position.Slot));
        }

        ShowMenu(player, menu);
    }

    private void RecordRole(CCSPlayerController player, int slot)
    {
        if (!IsCurrentMapSupported() || !player.IsValid || !_selectedTactics.TryGetValue(player.Team, out var tactic) ||
            !tactic.Positions.Any(position => position.Slot == slot))
        {
            return;
        }

        if (!CanChoose(player.Team)) return;

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
            OpenIncompleteMenus(team);
    }

    private void OpenIncompleteMenus(CsTeam team)
    {
        if (!CanChoose(team)) return;
        foreach (var player in GetHumanPlayers(team))
        {
            if (team == CsTeam.CounterTerrorist)
            {
                if (_selectedSite is null)
                {
                    if (!_siteVotes.ContainsKey(player.SteamID)) OpenSiteMenu(player);
                }
                else if (_roundEconomies.GetValueOrDefault(team) == EconomyTier.Lowbuy && _selectedMode is null)
                {
                    if (!_modeVotes.ContainsKey(player.SteamID)) OpenModeMenu(player);
                }
                else if (IsIndividualCtMode())
                {
                    if (!_personalZones.ContainsKey(player.SteamID)) OpenPersonalCtMenu(player);
                }
                else if (!_selectedTactics.ContainsKey(team))
                {
                    if (!_tacticVotes.TryGetValue(team, out var votes) || !votes.ContainsKey(player.SteamID))
                        OpenTacticMenu(player);
                }
                else if (_selectedIntent is null && !_intentVotes.ContainsKey(player.SteamID))
                    OpenIntentMenu(player, _selectedTactics[team]);
            }
            else if (!_selectedTactics.ContainsKey(team))
            {
                if (!_tacticVotes.TryGetValue(team, out var votes) || !votes.ContainsKey(player.SteamID))
                    OpenTacticMenu(player);
            }
            else if (!_selectedRoles.TryGetValue(team, out var roles) || !roles.ContainsKey(player.SteamID))
                OpenRoleMenu(player);
        }
    }

    private void AnnounceChoices()
    {
        AnnounceRoleInstructions(CsTeam.Terrorist);
        if (_selectedSite is not null) BroadcastToTeam(CsTeam.CounterTerrorist, $"Site CT: {_selectedSite}.");
        if (IsIndividualCtMode())
        {
            foreach (var player in GetHumanPlayers(CsTeam.CounterTerrorist))
                if (_personalTactics.TryGetValue(player.SteamID, out var tactic) &&
                    _personalZones.TryGetValue(player.SteamID, out var slot))
                {
                    var position = tactic.Positions.First(item => item.Slot == slot);
                    player.PrintToChat($"Votre plan: {tactic.Label} — {position.Zone}: {position.Instruction}");
                }
        }
        else if (_selectedTactics.TryGetValue(CsTeam.CounterTerrorist, out var collective) && _selectedIntent is int intent)
        {
            var position = collective.Intentions.First(item => item.Slot == intent);
            BroadcastToTeam(CsTeam.CounterTerrorist, $"Plan commun: {collective.Label} — {position.Zone}: {position.Instruction}");
        }
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

        var cash = GetBudget(tier, team);
        foreach (var player in GetPlayers(team))
        {
            ApplyEconomy(player, cash);
        }
    }

    private void ApplyEconomy(CCSPlayerController player)
    {
        if (_roundEconomies.TryGetValue(player.Team, out var tier))
        {
            var cash = GetBudget(tier, player.Team);
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

    private static int GetBudget(EconomyTier tier, CsTeam team)
    {
        var economy = Economies.First(entry => entry.Tier == tier);
        return team == CsTeam.Terrorist ? economy.TerroristCash : economy.CounterTerroristCash;
    }

    private void ShowScenarioToPlayers()
    {
        foreach (var player in Utilities.GetPlayers().Where(player => player.IsValid))
        {
            ShowScenario(player);
        }
    }

    private void OnHelpCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player is not { IsValid: true }) return;

        player.PrintToChat("ILY — commandes (. ou !)");
        player.PrintToChat(".ily_help : commandes | .ily_info : état de la partie");
        player.PrintToChat(".ily_config : nombre de manches | .ily_start : lancer | .ily_stop : arrêter");
        player.PrintToChat(".ily_tactic : choisir le plan | .ily_role : choisir le rôle ou la zone");
        player.PrintToChat("!ily_pick <numéro> : sélectionner une option sans la radio ILY");
        player.PrintToChat(".ily_ready : valider l'équipe | .ily_forcestart : lancer sans attendre les choix");
        player.PrintToChat(".ily_pause : pause permanente après la manche, choix et vocal ouverts");
        player.PrintToChat(".ily_unpause : reprendre après accord CT et T");
    }

    private void OnInfoCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (player is { IsValid: true }) ShowScenario(player);
    }

    private void ShowScenario(CCSPlayerController player)
    {
        if (!_sessionActive)
        {
            player.PrintToChat("Aucune partie ILY en cours. Lancez .ily_start pour commencer.");
            return;
        }

        var economy = _roundEconomies.TryGetValue(player.Team, out var tier)
            ? $"{(tier == EconomyTier.Eco ? "Pistol round" : tier)} ({GetBudget(tier, player.Team)}$)"
            : "En attente";
        player.PrintToChat($"ILY — manche {_completedRounds + 1}/{_configuredRoundCount}{(_paused ? " (en pause)" : "")}");
        player.PrintToChat($"Économie: {economy}");
        if (player.Team == CsTeam.CounterTerrorist || player.Team == CsTeam.Terrorist)
        {
            ShowTeamTactic(player, player.Team);
        }
        else
        {
            player.PrintToChat("Les informations tactiques sont réservées aux joueurs de chaque équipe.");
        }
    }

    private void ShowTeamTactic(CCSPlayerController player, CsTeam team)
    {
        if (!_sessionActive)
        {
            player.PrintToChat("Session ILY inactive. Lancez .ily_start pour commencer.");
            return;
        }

        if (team == CsTeam.CounterTerrorist)
        {
            player.PrintToChat($"Site CT: {_selectedSite ?? "à choisir"}.");
            if (_roundEconomies.GetValueOrDefault(team) == EconomyTier.Lowbuy)
                player.PrintToChat($"Mode Lowbuy: {_selectedMode ?? "à choisir"}.");
            if (IsIndividualCtMode() && _personalTactics.TryGetValue(player.SteamID, out var personal))
            {
                player.PrintToChat($"Votre approche: {personal.Label}.");
                if (_personalZones.TryGetValue(player.SteamID, out var zone))
                {
                    var position = personal.Positions.First(item => item.Slot == zone);
                    player.PrintToChat($"Votre zone: {position.Zone}: {position.Instruction}");
                }
            }
            else if (_selectedTactics.TryGetValue(team, out var collective) && _selectedIntent is int intent)
            {
                var position = collective.Intentions.First(item => item.Slot == intent);
                player.PrintToChat($"Plan commun: {collective.Label} — {position.Zone}: {position.Instruction}");
            }
            else if (_selectedSite is not null)
                player.PrintToChat($"Approches disponibles: {string.Join(" | ", GetCurrentTactics(team).Select(option => option.Label))}.");
            return;
        }

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
