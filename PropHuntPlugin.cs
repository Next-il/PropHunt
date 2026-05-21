using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Utils;

namespace PropHunt;

[MinimumApiVersion(80)]
public class PropHuntPlugin : BasePlugin
{
    public override string ModuleName        => "PropHunt";
    public override string ModuleVersion     => "1.0.0";
    public override string ModuleAuthor      => "ShiNxz";
    public override string ModuleDescription => "Sends !config commands for PropHunt game settings on map/round start";

    // Give seekers wallhack grenades
    public FakeConVar<bool> PhTAGrenade          = new("ph_ta_grenade",           "!config TAGrenade value",                                true);
    // Random select seekers at the start of the round
    public FakeConVar<bool> PhRandomSeekers      = new("ph_random_seekers",       "!config RandomSeekers value",                            false);
    // Move props to seeker team after death
    public FakeConVar<bool> PhMoveProps          = new("ph_move_props",           "!config MoveProps value",                                false);
    // Reroll hider props at the middle of the round
    public FakeConVar<bool> PhForceReroll        = new("ph_force_reroll",         "!config ForceReroll value",                              false);
    // Force hiders to taunt every minute
    public FakeConVar<bool> PhForceTaunt         = new("ph_force_taunt",          "!config ForceTaunt value",                               true);
    // After how many seconds since round start should the seekers spawn
    public FakeConVar<int>  PhSeekerRespawnTime  = new("ph_seeker_respawn_time",  "!config SeekerRespawnTime value",                        30);
    // How many times can a prop reroll
    public FakeConVar<int>  PhPropMaxRerolls     = new("ph_prop_max_rerolls",     "!config PropMaxRerolls value",                           3);
    // God mode for seekers (CT team)
    public FakeConVar<bool> PhSeekerGodMode      = new("ph_seeker_god_mode",      "Apply god mode to seekers (CT) on spawn",                true);
    // Infinite clip for seekers — Clip1 += 1 after each shot so seekers never run out (still must reload)
    public FakeConVar<bool> PhSeekerInfiniteAmmo = new("ph_seeker_infinite_ammo", "Infinite ammo for seekers (CT) — still must reload",     true);
    // HP penalty deducted from a seeker per shot that misses all players (0 = disabled)
    public FakeConVar<int>  PhSeekerHitMiss      = new("ph_seeker_hitmiss",       "HP deducted from seeker per missed shot (0 = disabled)", 0);
    // Spawn HP for seekers (0 = use game default)
    public FakeConVar<int>  PhSeekerHealth       = new("ph_seeker_health",        "Spawn HP for seekers (CT) (0 = game default)",           100);

    // Tracks seekers who fired this tick but whose bullet has not yet hit a player
    private readonly HashSet<ulong> _pendingMissFire = [];

    public override void Load(bool hotReload)
    {
        RegisterFakeConVars(GetType(), this);

        PhTAGrenade.ValueChanged         += (_, v) => SendConfig("TAGrenade",         v.ToString().ToLower());
        PhRandomSeekers.ValueChanged     += (_, v) => SendConfig("RandomSeekers",     v.ToString().ToLower());
        PhMoveProps.ValueChanged         += (_, v) => SendConfig("MoveProps",         v.ToString().ToLower());
        PhForceReroll.ValueChanged       += (_, v) => SendConfig("ForceReroll",       v.ToString().ToLower());
        PhForceTaunt.ValueChanged        += (_, v) => SendConfig("ForceTaunt",        v.ToString().ToLower());
        PhSeekerRespawnTime.ValueChanged += (_, v) => SendConfig("SeekerRespawnTime", v.ToString());
        PhPropMaxRerolls.ValueChanged    += (_, v) => SendConfig("PropMaxRerolls",    v.ToString());

        RegisterListener<Listeners.OnMapStart>(_ =>
        {
            AddTimer(3.0f, SendAllConfigs);
        });

        RegisterEventHandler<EventRoundStart>((_, _) =>
        {
            AddTimer(1.0f, BroadcastConfig);
            return HookResult.Continue;
        });

        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        RegisterEventHandler<EventWeaponFire>(OnWeaponFire);
        RegisterEventHandler<EventWeaponFireOnEmpty>(OnWeaponFireOnEmpty);
        RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt);
    }

    private HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || !player.IsValid || player.IsBot)
            return HookResult.Continue;

        if (player.TeamNum != (int)CsTeam.CounterTerrorist)
            return HookResult.Continue;

        AddTimer(0.5f, () =>
        {
            if (!player.IsValid) return;

            if (PhSeekerGodMode.Value)
                ApplyGodMode(player, true);

            if (PhSeekerHealth.Value > 0)
                ApplyHealth(player, PhSeekerHealth.Value);
        });

        return HookResult.Continue;
    }

    private HookResult OnWeaponFire(EventWeaponFire @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || !player.IsValid || player.IsBot)
            return HookResult.Continue;

        if (player.TeamNum != (int)CsTeam.CounterTerrorist)
            return HookResult.Continue;

        // Infinite ammo: undo the round just consumed so the clip never depletes
        if (PhSeekerInfiniteAmmo.Value)
        {
            var weapon = player.PlayerPawn.Value?.WeaponServices?.ActiveWeapon?.Value;
            if (weapon != null)
                weapon.Clip1 += 1;
        }

        // Miss penalty: register the shot as pending.
        // EventPlayerHurt will remove it if the bullet hits any player.
        // Whatever remains after one server frame is a clean miss.
        if (PhSeekerHitMiss.Value > 0)
        {
            _pendingMissFire.Add(player.SteamID);
            Server.NextFrame(() =>
            {
                if (!_pendingMissFire.Remove(player.SteamID)) return;
                ApplyMissPenalty(player);
            });
        }

        return HookResult.Continue;
    }

    // EventWeaponFireOnEmpty fires when the magazine is empty and the player dry-fires.
    // No bullet leaves the gun so no miss penalty — only handle the clip refill.
    private HookResult OnWeaponFireOnEmpty(EventWeaponFireOnEmpty @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || !player.IsValid || player.IsBot)
            return HookResult.Continue;

        if (!PhSeekerInfiniteAmmo.Value || player.TeamNum != (int)CsTeam.CounterTerrorist)
            return HookResult.Continue;

        var weapon = player.PlayerPawn.Value?.WeaponServices?.ActiveWeapon?.Value;
        if (weapon != null)
            weapon.Clip1 += 1;

        return HookResult.Continue;
    }

    // Any player taking damage means the attacker's bullet connected — cancel their pending miss.
    private HookResult OnPlayerHurt(EventPlayerHurt @event, GameEventInfo info)
    {
        var attacker = @event.Attacker;
        if (attacker != null && attacker.IsValid)
            _pendingMissFire.Remove(attacker.SteamID);

        return HookResult.Continue;
    }

    private void ApplyMissPenalty(CCSPlayerController player)
    {
        if (!player.IsValid) return;
        var pawn = player.PlayerPawn.Value;
        if (pawn == null || pawn.Health <= 0) return;

        pawn.Health = Math.Max(0, pawn.Health - PhSeekerHitMiss.Value);
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");
    }

    private static void ApplyGodMode(CCSPlayerController player, bool enabled)
    {
        if (player.PlayerPawn.Value == null) return;
        player.PlayerPawn.Value.TakesDamage = !enabled;
        Utilities.SetStateChanged(player.PlayerPawn.Value, "CBaseEntity", "m_bTakesDamage");
    }

    private static void ApplyHealth(CCSPlayerController player, int health)
    {
        var pawn = player.PlayerPawn.Value;
        if (pawn == null) return;
        pawn.Health = health;
        if (health > 100)
            pawn.MaxHealth = health;
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");
    }

    private void SendAllConfigs()
    {
        SendConfig("TAGrenade",         PhTAGrenade.Value.ToString().ToLower());
        SendConfig("RandomSeekers",     PhRandomSeekers.Value.ToString().ToLower());
        SendConfig("MoveProps",         PhMoveProps.Value.ToString().ToLower());
        SendConfig("ForceReroll",       PhForceReroll.Value.ToString().ToLower());
        SendConfig("ForceTaunt",        PhForceTaunt.Value.ToString().ToLower());
        SendConfig("SeekerRespawnTime", PhSeekerRespawnTime.Value.ToString());
        SendConfig("PropMaxRerolls",    PhPropMaxRerolls.Value.ToString());
    }

    private void BroadcastConfig()
    {
        Server.PrintToChatAll($" \x01-------- \x04PROP HUNT \x01--------");
        Server.PrintToChatAll($" \x01➜ TAGrenade: {BoolStr(PhTAGrenade.Value)}");
        Server.PrintToChatAll($" \x01➜ RandomSeekers: {BoolStr(PhRandomSeekers.Value)}");
        Server.PrintToChatAll($" \x01➜ MoveProps: {BoolStr(PhMoveProps.Value)}");
        Server.PrintToChatAll($" \x01➜ ForceReroll: {BoolStr(PhForceReroll.Value)}");
        Server.PrintToChatAll($" \x01➜ ForceTaunt: {BoolStr(PhForceTaunt.Value)}");
        Server.PrintToChatAll($" \x01➜ SeekerRespawnTime: \x04{PhSeekerRespawnTime.Value}s");
        Server.PrintToChatAll($" \x01➜ PropMaxRerolls: \x04{PhPropMaxRerolls.Value}");
        Server.PrintToChatAll($" \x01➜ SeekerGodMode: {BoolStr(PhSeekerGodMode.Value)}");
        Server.PrintToChatAll($" \x01➜ SeekerInfiniteAmmo: {BoolStr(PhSeekerInfiniteAmmo.Value)}");
        Server.PrintToChatAll($" \x01➜ SeekerHitMiss: \x04{(PhSeekerHitMiss.Value > 0 ? $"-{PhSeekerHitMiss.Value} HP/miss" : "Disabled")}");
        Server.PrintToChatAll($" \x01➜ SeekerHealth: \x04{(PhSeekerHealth.Value > 0 ? $"{PhSeekerHealth.Value} HP" : "Default")}");
    }

    private static string BoolStr(bool value) =>
        value ? "\x04Enabled" : "\x02Disabled";

    private static void SendConfig(string key, string value)
    {
        Server.ExecuteCommand($"say !config {key} {value}");
    }
}
