using System.Net;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;

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

    private enum CtRole
    {
        T,
        Apps,
        BSolo
    }

    private enum TerroristPlan
    {
        BananaSlow,
        SecondMidAppsA,
        MidLongA
    }

    private sealed record RoleAssignment(CtRole Role, string? SubRole);

    private static readonly (EconomyTier Tier, int Cash)[] Economies =
    [
        (EconomyTier.Force, 3500),
        (EconomyTier.Lowbuy, 2500),
        (EconomyTier.Full, 5000),
        (EconomyTier.Eco, 800)
    ];

    private readonly Dictionary<ulong, RoleAssignment> _ctRoles = [];
    private readonly Dictionary<ulong, TerroristPlan> _terroristVotes = [];
    private readonly Dictionary<CsTeam, EconomyTier> _roundEconomies = [];
    private readonly Dictionary<CsTeam, ulong> _teamReadyPlayers = [];
    private TerroristPlan? _winningPlan;

    public override string ModuleName => "ILY Lead You";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleAuthor => "Driw0x";
    public override string ModuleDescription => "Inferno 3v3 tactical scenario and economy manager";

    public override void Load(bool hotReload)
    {
        RegisterEventHandler<EventRoundPrestart>(OnRoundPrestart);
        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        AddCommandListener("say", OnClientSay, HookMode.Pre);
        AddCommandListener("say_team", OnClientSay, HookMode.Pre);
        AddCommand("css_scenario", "Display the current scenario, roles, votes and economy.", OnScenarioCommand);
        AddCommand("css_role", "Choose a CT role: t [long-defensive|banana-aggressive], apps, or b-solo.", OnRoleCommand);
        AddCommand("css_tactic", "Vote: banana, second-mid, or mid-long.", OnTacticCommand);
        AddCommand("css_ily_start", "Force the scenario to start without waiting for six players.", OnForceStartCommand);
    }

    private HookResult OnRoundPrestart(EventRoundPrestart gameEvent, GameEventInfo info)
    {
        _ctRoles.Clear();
        _terroristVotes.Clear();
        _roundEconomies.Clear();
        _teamReadyPlayers.Clear();
        _winningPlan = null;

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

        if (message.Equals(".ready", StringComparison.OrdinalIgnoreCase))
        {
            HandleReady(player);
            return HookResult.Handled;
        }

        return HookResult.Continue;
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
            player.PrintToChat("La commande .ready est disponible pendant le freeze time.");
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
        GetPlayers(team).Any(player => player.SteamID == readyPlayerId);

    private static CCSGameRules? GetGameRules() =>
        Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
            .FirstOrDefault()?.GameRules;

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
    
    private void OnRoleCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!IsPlayingFor(player, CsTeam.CounterTerrorist))
        {
            return;
        }

        if (command.ArgCount < 2)
        {
            player!.PrintToChat("Usage: !role t [long-defensive|banana-aggressive] | apps | b-solo");
            return;
        }

        var roleName = command.GetArg(1).ToLowerInvariant();
        var subRole = command.ArgCount > 2 ? command.GetArg(2).ToLowerInvariant() : null;
        if (!TryParseRole(roleName, subRole, out var assignment))
        {
            player!.PrintToChat("Role invalide. T: !role t long-defensive|banana-aggressive; autres: !role apps|b-solo");
            return;
        }

        var playerId = player!.SteamID;
        if (_ctRoles.Any(entry => entry.Key != playerId && entry.Value.Role == assignment.Role))
        {
            player.PrintToChat("Ce rôle est déjà attribué à un autre CT.");
            return;
        }

        _ctRoles[playerId] = assignment;
        BroadcastToTeam(CsTeam.CounterTerrorist, $"{WebUtility.HtmlEncode(player.PlayerName)} choisit {FormatRole(assignment)}.");
        ShowScenarioToPlayers();
    }

    private void OnTacticCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!IsPlayingFor(player, CsTeam.Terrorist))
        {
            return;
        }

        if (command.ArgCount < 2 || !TryParsePlan(command.GetArg(1), out var plan))
        {
            player!.PrintToChat("Vote: !tactic banana | second-mid | mid-long");
            return;
        }

        _terroristVotes[player!.SteamID] = plan;
        var teamPlayers = GetPlayers(CsTeam.Terrorist);
        if (teamPlayers.Count >= 3 && teamPlayers.All(member => _terroristVotes.ContainsKey(member.SteamID)))
        {
            var leadingPlan = _terroristVotes.Values
                .GroupBy(vote => vote)
                .OrderByDescending(group => group.Count())
                .First();

            if (leadingPlan.Count() > teamPlayers.Count / 2)
            {
                _winningPlan = leadingPlan.Key;
                BroadcastToTeam(CsTeam.Terrorist, $"Plan Terro retenu: {FormatPlan(_winningPlan.Value)}.");
            }
            else
            {
                _terroristVotes.Clear();
                BroadcastToTeam(CsTeam.Terrorist, "Égalité: revote nécessaire pour choisir le plan Terro.");
            }
        }
        else
        {
            BroadcastToTeam(CsTeam.Terrorist, $"Vote enregistré ({_terroristVotes.Count}/{teamPlayers.Count}).");
        }

        ShowScenarioToPlayers();
    }

    private static bool TryParseRole(string roleName, string? subRole, out RoleAssignment assignment)
    {
        assignment = roleName switch
        {
            "t" when subRole == "long-defensive" => new RoleAssignment(CtRole.T, "Long défensif — tenir la zone ou reculer"),
            "t" when subRole == "banana-aggressive" => new RoleAssignment(CtRole.T, "Banane agressive — tenir la zone ou avancer"),
            "apps" when subRole is null => new RoleAssignment(CtRole.Apps, null),
            "b-solo" when subRole is null => new RoleAssignment(CtRole.BSolo, null),
            _ => null!
        };
        return assignment is not null;
    }

    private static bool TryParsePlan(string choice, out TerroristPlan plan)
    {
        plan = choice.ToLowerInvariant() switch
        {
            "banana" => TerroristPlan.BananaSlow,
            "second-mid" => TerroristPlan.SecondMidAppsA,
            "mid-long" => TerroristPlan.MidLongA,
            _ => (TerroristPlan)(-1)
        };
        return Enum.IsDefined(plan);
    }

    private bool IsPlayingFor(CCSPlayerController? player, CsTeam team)
    {
        if (player is null || !player.IsValid || player.Team != team)
        {
            player?.PrintToChat(team == CsTeam.CounterTerrorist
                ? "Cette commande est réservée aux CT."
                : "Cette commande est réservée aux Terros.");
            return false;
        }

        return true;
    }

    private static List<CCSPlayerController> GetPlayers(CsTeam team) =>
        Utilities.GetPlayers()
            .Where(player => player.IsValid && player.Team == team)
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
        if (player.Team == CsTeam.CounterTerrorist)
        {
            var roles = string.Join(" | ", Enum.GetValues<CtRole>().Select(role =>
                _ctRoles.Values.FirstOrDefault(assignment => assignment.Role == role) is { } selected
                    ? FormatRole(selected)
                    : $"{FormatRoleName(role)}: à choisir"));
            player.PrintToChat($"Rôles CT: {roles}");
            player.PrintToChat("Commandes: !scenario | !ily_start | CT: !role t long-defensive|banana-aggressive, !role apps, !role b-solo");
        }
        else if (player.Team == CsTeam.Terrorist)
        {
            var plan = _winningPlan is { } winner
                ? FormatPlan(winner)
                : $"vote en cours ({_terroristVotes.Count}/3)";
            player.PrintToChat($"Plan Terro: {plan}");
            player.PrintToChat("Commandes: !scenario | !ily_start | Terros: !tactic banana|second-mid|mid-long");
        }
        else
        {
            player.PrintToChat("Les informations tactiques sont réservées aux joueurs de chaque équipe.");
            player.PrintToChat("Commandes: !scenario | !ily_start");
        }

        player.PrintToChat("Cartes: Inferno, Anubis | Choix: Cache, Dust 2, Ancient | Autres: Overpass, Vertigo, Mirage...");
    }

    private static string FormatRole(RoleAssignment assignment) =>
        assignment.Role switch
        {
            CtRole.T => $"T — {assignment.SubRole}",
            CtRole.Apps => "Apps",
            CtRole.BSolo => "B solo",
            _ => assignment.Role.ToString()
        };

    private static string FormatRoleName(CtRole role) =>
        role switch
        {
            CtRole.T => "T",
            CtRole.Apps => "Apps",
            CtRole.BSolo => "B solo",
            _ => role.ToString()
        };

    private static string FormatPlan(TerroristPlan plan) =>
        plan switch
        {
            TerroristPlan.BananaSlow => "Banane slow + CPL",
            TerroristPlan.SecondMidAppsA => "2nd mid vers Apps A",
            TerroristPlan.MidLongA => "Mid vers long A",
            _ => plan.ToString()
        };

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
