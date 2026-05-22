using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.UserMessages;
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
    // Infinite reserve ammo for seekers (sv_infinite_ammo 2 behavior) — clip depletes normally, reserve stays topped up
    public FakeConVar<bool> PhSeekerInfiniteAmmo = new("ph_seeker_infinite_ammo", "Infinite reserve ammo for seekers (CT) — clip still depletes", true);
    // HP penalty deducted from a seeker per shot that misses all players (0 = disabled)
    public FakeConVar<int>  PhSeekerHitMiss      = new("ph_seeker_hitmiss",       "HP deducted from seeker per missed shot (0 = disabled)", 0);
    // Spawn HP for seekers (0 = use game default)
    public FakeConVar<int>  PhSeekerHealth       = new("ph_seeker_health",        "Spawn HP for seekers (CT) (0 = game default)",           100);

    // Tracks seekers who fired this tick but whose bullet has not yet hit a player
    private readonly HashSet<ulong> _pendingMissFire = [];

    // Previous-tick button mask per seeker — used to detect rising-edge knife attacks
    // (left- and right-click) that EventWeaponFire doesn't reliably surface.
    private readonly Dictionary<ulong, ulong> _prevButtons = [];

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

        // On hot-reload the map is already live, so OnMapStart won't fire — push configs now.
        if (hotReload)
            AddTimer(1.0f, SendAllConfigs);

        RegisterEventHandler<EventRoundStart>((_, _) =>
        {
            AddTimer(1.0f, BroadcastConfig);
            return HookResult.Continue;
        });

        // New match (e.g. after mp_restartgame / !rs) — push configs again so the
        // PropHunt mod picks them up without needing a full map change.
        RegisterEventHandler<EventBeginNewMatch>((_, _) =>
        {
            AddTimer(1.0f, SendAllConfigs);
            return HookResult.Continue;
        });

        RegisterListener<Listeners.OnTick>(OnTick);

        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        RegisterEventHandler<EventPlayerTeam>(OnPlayerTeam);
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        RegisterEventHandler<EventWeaponFire>(OnWeaponFire);
        RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt);
    }

    private HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player != null)
        {
            _prevButtons.Remove(player.SteamID);
            _pendingMissFire.Remove(player.SteamID);
        }
        return HookResult.Continue;
    }

    private HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || !player.IsValid || player.IsBot)
            return HookResult.Continue;

        // Defer to next tick — PropHunt may shuffle teams (e.g. RandomSeekers) AFTER
        // the spawn event fires. Re-verify team membership at apply time so we never
        // attach seeker-only effects (god mode, lockdown) to a player who has since
        // been moved to the prop team.
        AddTimer(0.5f, () =>
        {
            if (!player.IsValid) return;
            if (player.TeamNum != (int)CsTeam.CounterTerrorist) return;

            if (PhSeekerGodMode.Value)
                ApplyGodMode(player, true);

            if (PhSeekerHealth.Value > 0)
                ApplyHealth(player, PhSeekerHealth.Value);

            if (PhSeekerRespawnTime.Value > 0)
                ApplySeekerLockdown(player, PhSeekerRespawnTime.Value);
        });

        return HookResult.Continue;
    }

    // If a seeker leaves CT, drop any lingering god mode so their new role
    // (prop / spectator) takes damage normally.
    private HookResult OnPlayerTeam(EventPlayerTeam @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || !player.IsValid || player.IsBot)
            return HookResult.Continue;

        if (@event.Team != (int)CsTeam.CounterTerrorist)
        {
            var pawn = player.PlayerPawn.Value;
            if (pawn != null && !pawn.TakesDamage)
                ApplyGodMode(player, false);
        }

        return HookResult.Continue;
    }

    private HookResult OnWeaponFire(EventWeaponFire @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || !player.IsValid || player.IsBot)
            return HookResult.Continue;

        if (player.TeamNum != (int)CsTeam.CounterTerrorist)
            return HookResult.Continue;

        var weapon = player.PlayerPawn.Value?.WeaponServices?.ActiveWeapon?.Value;

        // Knife attacks are handled in OnTick via button-edge detection.
        // EventWeaponFire is unreliable for melee — the secondary stab (right-click) doesn't
        // fire it at all, and the primary swing's hit lands well after Server.NextFrame.
        if (IsKnife(weapon))
            return HookResult.Continue;

        // sv_infinite_ammo 2 behavior: top up the reserve so reloads always succeed.
        // The clip depletes naturally — the player still has to reload.
        if (PhSeekerInfiniteAmmo.Value && weapon != null && weapon.ReserveAmmo.Length > 0)
        {
            weapon.ReserveAmmo[0] = 999;
            Utilities.SetStateChanged(weapon, "CBasePlayerWeapon", "m_pReserveAmmo");
        }

        // Miss penalty: register the shot as pending.
        // EventPlayerHurt will remove it if the bullet hits any player.
        // Whatever remains after one server frame is a clean miss.
        if (PhSeekerHitMiss.Value > 0)
            QueueMissCheck(player, knife: false);

        return HookResult.Continue;
    }

    // Knife attacks need their own detection path: EventWeaponFire is unreliable for melee,
    // so we watch the player's button mask for a rising edge on Attack / Attack2.
    private void OnTick()
    {
        if (PhSeekerHitMiss.Value <= 0)
            return;

        const ulong IN_ATTACK  = 1UL;     // PlayerButtons.Attack
        const ulong IN_ATTACK2 = 2048UL;  // PlayerButtons.Attack2

        foreach (var player in Utilities.GetPlayers())
        {
            if (!player.IsValid || player.IsBot) continue;
            if (player.TeamNum != (int)CsTeam.CounterTerrorist) continue;

            var pawn = player.PlayerPawn.Value;
            if (pawn == null || pawn.Health <= 0) continue;

            ulong buttons = (ulong)player.Buttons;
            _prevButtons.TryGetValue(player.SteamID, out var prev);
            _prevButtons[player.SteamID] = buttons;

            if (!IsKnife(pawn.WeaponServices?.ActiveWeapon?.Value))
                continue;

            bool primaryEdge   = (buttons & IN_ATTACK)  != 0 && (prev & IN_ATTACK)  == 0;
            bool secondaryEdge = (buttons & IN_ATTACK2) != 0 && (prev & IN_ATTACK2) == 0;

            if (primaryEdge || secondaryEdge)
                QueueMissCheck(player, knife: true);
        }
    }

    // For guns, the bullet/hit pipeline completes within one server frame.
    // For knives, the swing animation takes ~200–500 ms before damage lands,
    // so we wait long enough for EventPlayerHurt to clear the pending state.
    private void QueueMissCheck(CCSPlayerController player, bool knife)
    {
        if (!_pendingMissFire.Add(player.SteamID))
            return;

        if (knife)
        {
            AddTimer(0.6f, () =>
            {
                if (!_pendingMissFire.Remove(player.SteamID)) return;
                ApplyMissPenalty(player);
            });
        }
        else
        {
            Server.NextFrame(() =>
            {
                if (!_pendingMissFire.Remove(player.SteamID)) return;
                ApplyMissPenalty(player);
            });
        }
    }

    private static bool IsKnife(CBasePlayerWeapon? weapon)
    {
        var name = weapon?.DesignerName;
        return name != null && (name.Contains("knife") || name.Contains("bayonet"));
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

        int newHp = pawn.Health - PhSeekerHitMiss.Value;
        if (newHp <= 0)
        {
            // CommitSuicide(force:true) still respects TakesDamage in practice —
            // with god mode on, the kill silently no-ops and the seeker pins at 1 HP.
            // Drop god mode for the kill; respawn re-applies it via OnPlayerSpawn.
            ApplyGodMode(player, false);
            player.CommitSuicide(false, true);
            return;
        }
        pawn.Health = newHp;
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

    private void ApplySeekerLockdown(CCSPlayerController player, int seconds)
    {
        Freeze(player);
        Blind(player, seconds);

        AddTimer(seconds, () =>
        {
            if (!player.IsValid) return;
            // Only unfreeze if they're still a seeker — if they were moved to props
            // mid-lockdown, the new pawn already has a default MoveType.
            if (player.TeamNum != (int)CsTeam.CounterTerrorist) return;
            Unfreeze(player);
        });
    }

    private static void Freeze(CCSPlayerController player)
    {
        var pawn = player.PlayerPawn.Value;
        if (pawn == null) return;
        pawn.MoveType = MoveType_t.MOVETYPE_OBSOLETE;
        Schema.SetSchemaValue(pawn.Handle, "CBaseEntity", "m_nActualMoveType", (int)MoveType_t.MOVETYPE_OBSOLETE);
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_MoveType");
    }

    private static void Unfreeze(CCSPlayerController player)
    {
        var pawn = player.PlayerPawn.Value;
        if (pawn == null) return;
        pawn.MoveType = MoveType_t.MOVETYPE_WALK;
        Schema.SetSchemaValue(pawn.Handle, "CBaseEntity", "m_nActualMoveType", (int)MoveType_t.MOVETYPE_WALK);
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_MoveType");
    }

    // Black screen "Fade" UserMessage — same approach SimpleAdmin/cs2-admin use for !blind.
    private static void Blind(CCSPlayerController player, float holdSeconds)
    {
        var fade = UserMessage.FromPartialName("Fade");
        fade.SetInt("duration",  Convert.ToInt32(0.2f * 512));
        fade.SetInt("hold_time", Convert.ToInt32(holdSeconds * 512));
        // FADE_IN (0x0001) | FFADE_PURGE (0x0010)
        fade.SetInt("flags", 0x0001 | 0x0010);
        // Black, fully opaque (RGBA packed)
        fade.SetInt("color", 0 | (0 << 8) | (0 << 16) | (255 << 24));
        fade.Send(player);
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

    // CS2 chat doesn't reliably honor \x02 (DarkRed) or \x04 (Green) directly before
    // a letter — the control char leaks through as a visible glyph and eats the first
    // character (e.g. "Enabled" → "Nnabled", "Disabled" → "-isabled").
    // ChatColors.Lime (\x06) and ChatColors.Red (\x07) render cleanly.
    private static string BoolStr(bool value) =>
        value ? $"{ChatColors.Lime}Enabled{ChatColors.Default}" : $"{ChatColors.Red}Disabled{ChatColors.Default}";

    private static void SendConfig(string key, string value)
    {
        Server.ExecuteCommand($"say !config {key} {value}");
    }
}
