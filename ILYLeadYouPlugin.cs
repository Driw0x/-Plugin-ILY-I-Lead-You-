using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Extensions;
using CounterStrikeSharp.API.Modules.Menu;
using CounterStrikeSharp.API.Modules.Timers;
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

    private static readonly (EconomyTier Tier, int Cash)[] Economies =
    [
        (EconomyTier.Force, 3500),
        (EconomyTier.Lowbuy, 2500),
        (EconomyTier.Full, 5000),
        (EconomyTier.Eco, 800)
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
    private readonly Dictionary<CsTeam, TacticalOption> _selectedTactics = [];
    private readonly Dictionary<CsTeam, Dictionary<ulong, int>> _selectedRoles = [];
    private readonly Dictionary<CsTeam, EconomyTier> _roundEconomies = [];
    private readonly Dictionary<CsTeam, ulong> _teamReadyPlayers = [];
    private bool _warmupSetupStarted;
    private bool _warmupSelectionForRound;
    private IlyRoundPhase _ilyRoundPhase;
    private CCSCustomHudLayout? _hud;

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
            _sessionActive = false;
            ClearSelections();
            _hud = null;
            Server.NextFrame(StartWarmupWithoutMatchZy);
        });
        RegisterListener<Listeners.OnClientPutInServer>(slot =>
        {
            var player = Utilities.GetPlayerFromSlot(slot);
            if (player is { IsValid: true } && _hud is { IsValid: true })
                _hud.SetHasClassForPlayer(player, "ily_panel", "visible", false);
        });
        AddCommandListener("say", OnClientSay, HookMode.Pre);
        AddCommandListener("say_team", OnClientSay, HookMode.Pre);
        AddCommand("css_ily_tactic", "Open your team's tactic vote menu.", OnTacticCommand);
        AddCommand("css_ily_role", "Choose your role in the selected tactic.", OnRoleCommand);
        AddCommand("css_ily_ready", "Mark your team ready before starting the ILY match.", OnReadyCommand);
        AddCommand("css_ily_start", "Force the scenario to start without waiting for six players.", OnForceStartCommand);
        AddCommand("css_ily_stop", "Stop the ILY session.", OnStopCommand);
        AddTimer(1.0f, RefreshHud, TimerFlags.REPEAT);
    }

    public override void OnAllPluginsLoaded(bool hotReload) =>
        Server.NextFrame(StartWarmupWithoutMatchZy);

    public override void Unload(bool hotReload)
    {
        if (_hud is { IsValid: true }) _hud.Remove();
    }

    private HookResult OnRoundPrestart(EventRoundPrestart gameEvent, GameEventInfo info)
    {
        if (!IsCurrentMapSupported() || !_sessionActive) return HookResult.Continue;

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
        var terroristEconomyIndex = Random.Shared.Next(Economies.Length - 1);
        if (terroristEconomyIndex >= ctEconomyIndex)
        {
            terroristEconomyIndex++;
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
            _ilyRoundPhase = IlyRoundPhase.Idle;
            _sessionActive = false;
            _warmupSetupStarted = false;
            ClearSelections();
            Server.NextFrame(() =>
            {
                if (IsMatchZyInstalled()) Server.ExecuteCommand("css_match");
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

        if (message.Equals(".ily_stop", StringComparison.OrdinalIgnoreCase))
        {
            OnStopCommand(player, command);
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
        if (gameRules is null || !gameRules.WarmupPeriod)
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

        StartMatchAfterReady();
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
                        if (catalog is null || !IsValidCatalog(catalog) ||
                            (team == CsTeam.CounterTerrorist &&
                             (catalog.Tactics.Any(option => option.Site is not ("A" or "B") || option.Intentions.Count == 0) ||
                              !new[] { "A", "B" }.All(site => catalog.Tactics.Any(option => option.Site == site)))))
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

        if (_sessionActive)
        {
            player?.PrintToChat("La procédure ILY est déjà ouverte.");
            return;
        }

        _sessionActive = true;
        _warmupSetupStarted = true;
        ClearSelections();
        SelectEconomies();
        Broadcast("ILY: choisissez vos plans, puis une personne par équipe utilise .ily_ready.");
        OpenIncompleteMenus();
    }

    private void OnStopCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!_sessionActive) return;
        var wasPlaying = _ilyRoundPhase != IlyRoundPhase.Idle;
        _sessionActive = false;
        _ilyRoundPhase = IlyRoundPhase.Idle;
        _warmupSetupStarted = false;
        _warmupSelectionForRound = false;
        ClearSelections();
        Broadcast("Session ILY arrêtée.");
        if (IsMatchZyInstalled() && wasPlaying) Server.ExecuteCommand("css_match");
        else if (!IsMatchZyInstalled()) StartWarmupWithoutMatchZy();
    }

    private void StartMatchAfterReady()
    {
        if (_ilyRoundPhase != IlyRoundPhase.Idle) return;

        _ilyRoundPhase = IlyRoundPhase.Starting;
        if (IsMatchZyInstalled()) Server.ExecuteCommand("css_sleep");
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

    private List<TacticalOption> GetCurrentTactics(CsTeam team) =>
        team == CsTeam.CounterTerrorist
            ? GetAvailableTactics(team).Where(option => option.Site.Equals(_selectedSite, StringComparison.OrdinalIgnoreCase)).ToList()
            : GetAvailableTactics(team);

    private void OpenSiteMenu(CCSPlayerController player)
    {
        var menu = new ChatMenu("CT: site A ou B") { PostSelectAction = PostSelectAction.Close };
        foreach (var site in new[] { "A", "B" })
            menu.AddMenuOption($"Site {site}", (voter, _) => RecordSiteVote(voter, site));
        menu.Open(player);
    }

    private void RecordSiteVote(CCSPlayerController player, string site)
    {
        if (!CanChoose(player.Team) || player.Team != CsTeam.CounterTerrorist || _selectedSite is not null) return;
        _siteVotes[player.SteamID] = site;
        if (TryMajority(CsTeam.CounterTerrorist, _siteVotes, out var winner))
        {
            _selectedSite = winner;
            BroadcastToTeam(player.Team, $"Site CT choisi: {winner}.");
            OpenIncompleteMenus(player.Team);
        }
    }

    private void OpenModeMenu(CCSPlayerController player)
    {
        var menu = new ChatMenu("CT Lowbuy: mode") { PostSelectAction = PostSelectAction.Close };
        menu.AddMenuOption("Zones individuelles", (voter, _) => RecordModeVote(voter, "zones"));
        menu.AddMenuOption("Intention collective", (voter, _) => RecordModeVote(voter, "intention"));
        menu.Open(player);
    }

    private void RecordModeVote(CCSPlayerController player, string mode)
    {
        if (!CanChoose(player.Team) || player.Team != CsTeam.CounterTerrorist || _selectedMode is not null) return;
        _modeVotes[player.SteamID] = mode;
        if (TryMajority(player.Team, _modeVotes, out var winner))
        {
            _selectedMode = winner;
            BroadcastToTeam(player.Team, $"Mode Lowbuy CT: {winner}.");
            OpenIncompleteMenus(player.Team);
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
            OpenIncompleteMenus(team);
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
        var menu = new ChatMenu($"CT site {_selectedSite}: approche") { PostSelectAction = PostSelectAction.Close };
        foreach (var option in GetCurrentTactics(player.Team))
            menu.AddMenuOption(option.Label, (voter, _) =>
            {
                if (!CanChoose(voter.Team) || voter.Team != CsTeam.CounterTerrorist || !GetCurrentTactics(voter.Team).Contains(option)) return;
                _personalTactics[voter.SteamID] = option;
                OpenZoneMenu(voter, option);
            });
        menu.Open(player);
    }

    private void OpenZoneMenu(CCSPlayerController player, TacticalOption tactic)
    {
        var menu = new ChatMenu("CT: votre zone") { PostSelectAction = PostSelectAction.Close };
        foreach (var position in tactic.Positions)
            menu.AddMenuOption(position.Zone, (voter, _) =>
            {
                if (!CanChoose(voter.Team) || !_personalTactics.TryGetValue(voter.SteamID, out var current) || current != tactic) return;
                _personalZones[voter.SteamID] = position.Slot;
                voter.PrintToChat($"Votre zone: {position.Zone}. Communiquez votre choix pendant le freeze time.");
            });
        menu.Open(player);
    }

    private void OpenIntentMenu(CCSPlayerController player, TacticalOption tactic)
    {
        var menu = new ChatMenu("CT: intention commune") { PostSelectAction = PostSelectAction.Close };
        foreach (var position in tactic.Intentions)
            menu.AddMenuOption(position.Zone, (voter, _) => RecordIntentVote(voter, tactic, position.Slot));
        menu.Open(player);
    }

    private void RecordIntentVote(CCSPlayerController player, TacticalOption tactic, int slot)
    {
        if (!CanChoose(player.Team) || player.Team != CsTeam.CounterTerrorist ||
            !_selectedTactics.TryGetValue(player.Team, out var current) || current != tactic || _selectedIntent is not null) return;
        _intentVotes[player.SteamID] = slot;
        var players = GetHumanPlayers(player.Team);
        if (players.Any(member => !_intentVotes.ContainsKey(member.SteamID))) return;
        var leading = players.GroupBy(member => _intentVotes[member.SteamID]).OrderByDescending(group => group.Count()).First();
        if (leading.Count() <= players.Count / 2)
        {
            _intentVotes.Clear();
            BroadcastToTeam(player.Team, "Égalité: revote de l'intention.");
            OpenIncompleteMenus(player.Team);
            return;
        }
        _selectedIntent = leading.Key;
        BroadcastToTeam(player.Team, $"Intention CT: {tactic.Intentions.First(position => position.Slot == _selectedIntent).Zone}.");
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
                    if (player.Team == CsTeam.CounterTerrorist) OpenIntentMenu(member, winner);
                    else OpenRoleMenu(member);
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

        if (player is { IsValid: true } && player.Team == CsTeam.Terrorist)
        {
            OpenRoleMenu(player);
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
                if (_selectedSite is null ||
                    (_roundEconomies.GetValueOrDefault(team) == EconomyTier.Lowbuy && _selectedMode is null) ||
                    (IsIndividualCtMode() && !_personalZones.ContainsKey(player.SteamID)) ||
                    (!IsIndividualCtMode() && !_selectedTactics.ContainsKey(team)))
                    OpenTacticMenu(player);
                else if (!IsIndividualCtMode() && _selectedIntent is null)
                    OpenIntentMenu(player, _selectedTactics[team]);
            }
            else if (!_selectedTactics.ContainsKey(team)) OpenTacticMenu(player);
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

    private void RefreshHud()
    {
        if (!IsCurrentMapSupported()) return;

        if (_hud is not { IsValid: true })
        {
            _hud = Utilities.CreateEntityByName<CCSCustomHudLayout>("custom_hud_layout");
            if (_hud is null) return;
            using var keyValues = new CEntityKeyValues();
            keyValues.SetString("layout", "panorama/layout/custom_game/ily_hud.vxml_c");
            _hud.DispatchSpawn(keyValues);
        }

        foreach (var player in Utilities.GetPlayers().Where(player => player.IsValid && !player.IsBot && !player.IsHLTV))
        {
            var team = player.Team;
            if (team != CsTeam.CounterTerrorist && team != CsTeam.Terrorist)
            {
                _hud.SetHasClassForPlayer(player, "ily_panel", "visible", false);
                continue;
            }

            if (!_sessionActive)
            {
                _hud.SetHasClassForPlayer(player, "ily_panel", "visible", false);
                continue;
            }

            var economy = _roundEconomies.TryGetValue(team, out var tier)
                ? $"{tier.ToString().ToUpperInvariant()}  ·  {Economies.First(entry => entry.Tier == tier).Cash:N0}$"
                : "Économie en attente";
            _hud.SetDialogVariableStringForPlayer(player, "ily_economy", "value", economy);

            var choices = new List<string>();
            var title = "Choix en cours";
            var status = IsTeamReady(team) ? "Équipe prête" : "Terminez les choix · .ily_ready";
            var rows = new List<(string Name, string Detail)>();
            if (team == CsTeam.CounterTerrorist)
            {
                if (_selectedSite is null)
                {
                    title = "CT · Site A ou B";
                    choices.AddRange(["Site A", "Site B"]);
                }
                else if (_roundEconomies.GetValueOrDefault(team) == EconomyTier.Lowbuy && _selectedMode is null)
                {
                    title = $"CT · Site {_selectedSite}";
                    choices.AddRange(["Zones individuelles", "Intention collective"]);
                }
                else if (IsIndividualCtMode())
                {
                    if (!_personalTactics.TryGetValue(player.SteamID, out var personal))
                    {
                        title = $"CT · Site {_selectedSite} · Approche";
                        choices.AddRange(GetCurrentTactics(team).Select(option => option.Label));
                    }
                    else if (!_personalZones.TryGetValue(player.SteamID, out var zoneSlot))
                    {
                        title = $"CT · {personal.Label}";
                        choices.AddRange(personal.Positions.Select(position => position.Zone));
                    }
                    else
                    {
                        var position = personal.Positions.First(item => item.Slot == zoneSlot);
                        title = $"CT · Site {_selectedSite} · {personal.Label}";
                        rows.Add((position.Zone, position.Instruction));
                    }
                }
                else if (!_selectedTactics.TryGetValue(team, out var collective))
                {
                    title = $"CT · Site {_selectedSite} · Approche";
                    choices.AddRange(GetCurrentTactics(team).Select(option => option.Label));
                }
                else if (_selectedIntent is null)
                {
                    title = $"CT · {collective.Label} · Intention";
                    choices.AddRange(collective.Intentions.Select(position => position.Zone));
                }
                else
                {
                    var position = collective.Intentions.First(item => item.Slot == _selectedIntent);
                    title = $"CT · Site {_selectedSite} · {collective.Label}";
                    rows.Add((position.Zone, position.Instruction));
                }
            }
            else if (!_selectedTactics.TryGetValue(team, out var tactic))
            {
                title = "T · Scénario";
                choices.AddRange(GetAvailableTactics(team).Select(option => option.Label));
            }
            else
            {
                title = $"T · {tactic.Label}";
                _selectedRoles.TryGetValue(team, out var roles);
                foreach (var position in tactic.Positions)
                {
                    var assigned = GetHumanPlayers(team).FirstOrDefault(member =>
                        roles is not null && roles.TryGetValue(member.SteamID, out var slot) && slot == position.Slot);
                    rows.Add(($"{position.Zone} · {assigned?.PlayerName ?? "libre"}", position.Instruction));
                }
                if (roles is null || !roles.ContainsKey(player.SteamID))
                    choices.AddRange(tactic.Positions.Select(position => position.Zone));
            }

            if (choices.Count > 0) status = "Votez dans le chat: !1, !2…";
            _hud.SetDialogVariableStringForPlayer(player, "ily_tactic", "value", title);
            _hud.SetDialogVariableStringForPlayer(player, "ily_status", "value", status);
            for (var index = 0; index < 4; index++)
                _hud.SetDialogVariableStringForPlayer(player, $"ily_option_{index + 1}", "value",
                    index < choices.Count ? $"!{index + 1}  {choices[index]}" : "");
            for (var index = 0; index < 3; index++)
            {
                _hud.SetDialogVariableStringForPlayer(player, $"ily_role_{index + 1}", "value",
                    index < rows.Count ? rows[index].Name : "");
                _hud.SetDialogVariableStringForPlayer(player, $"ily_detail_{index + 1}", "value",
                    index < rows.Count ? rows[index].Detail : "");
            }

            _hud.SetHasClassForPlayer(player, "ily_panel", "visible", true);
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
            player.PrintToChat("Commandes: .info | .ily_start | .ily_ready | .ily_tactic | .ily_role (T) | .ily_stop");
        }
        else
        {
            player.PrintToChat("Les informations tactiques sont réservées aux joueurs de chaque équipe.");
            player.PrintToChat("Commandes: .info | .ily_start | .ily_stop");
        }

        player.PrintToChat("Cartes: Inferno, Anubis | Choix: Cache, Dust 2, Ancient | Autres: Overpass, Vertigo, Mirage...");
    }

    private void ShowTeamTactic(CCSPlayerController player, CsTeam team)
    {
        if (!_sessionActive)
        {
            player.PrintToChat("Session ILY inactive. Lancez .ily_start pendant la warmup.");
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
