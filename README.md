# PropHunt Plugin

A CounterStrikeSharp plugin for CS2 that configures and manages PropHunt game settings. It sends `!config` commands to the PropHunt game mode on map/round start, applies seeker-specific mechanics (god mode, infinite ammo, miss penalty, custom HP), and broadcasts the active configuration to all players each round.

**Author:** ShiNxz  
**Requires:** CounterStrikeSharp API v80+

---

## How it works

- **CT team = Seekers** — players hunting the props.
- **T team = Props** — players disguised as objects.

On map start the plugin sends all `!config` values to the PropHunt game mode (3-second delay to ensure the mode is loaded). On every round start it broadcasts the active configuration in chat.

---

## ConVars

### PropHunt game-mode settings
These are forwarded to the PropHunt game mode via `!config` commands.

| ConVar | Type | Default | Description |
|---|---|---|---|
| `ph_ta_grenade` | bool | `true` | Give seekers a wallhack grenade (TAGrenade) |
| `ph_random_seekers` | bool | `false` | Randomly select seekers at the start of each round |
| `ph_move_props` | bool | `false` | Move dead props to the seeker team |
| `ph_force_reroll` | bool | `false` | Force props to reroll their disguise mid-round |
| `ph_force_taunt` | bool | `true` | Force props to taunt (make a sound) every minute |
| `ph_seeker_respawn_time` | int | `30` | Seconds after round start before seekers spawn |
| `ph_prop_max_rerolls` | int | `3` | How many times a prop can reroll their disguise |

### Seeker mechanics
These are applied directly by the plugin — no `!config` forwarding needed.

| ConVar | Type | Default | Description |
|---|---|---|---|
| `ph_seeker_god_mode` | bool | `true` | Give seekers god mode (invulnerable to T team bullets) on spawn |
| `ph_seeker_infinite_ammo` | bool | `true` | Seekers never run out of ammo — clip is silently refilled after each shot. Seekers must still reload. |
| `ph_seeker_hitmiss` | int | `0` | HP deducted from a seeker per shot that misses all players. `0` = disabled. |
| `ph_seeker_health` | int | `100` | HP assigned to seekers on spawn. `0` = use the game's default. |

---

## ConVar details

### `ph_seeker_god_mode`
Applies `TakesDamage = false` to the seeker's pawn 0.5 s after spawn, making them invulnerable to all incoming damage. They can still lose HP via `ph_seeker_hitmiss` since that penalty is applied directly to the health value and bypasses the damage system.

### `ph_seeker_infinite_ammo`
After each shot (`EventWeaponFire`) the active weapon's `Clip1` is incremented by 1, cancelling out the round just consumed. This gives unlimited shots without touching `sv_cheats`. Seekers still need to reload when the clip runs dry — dry-fire events (`EventWeaponFireOnEmpty`) also trigger a top-up so the gun never stays stuck on 0.

### `ph_seeker_hitmiss`
When a seeker fires and the bullet does **not** hit any player, this many HP is deducted from the seeker. Detection uses a `EventWeaponFire` + `EventPlayerHurt` correlation pattern:

1. `EventWeaponFire` registers the shot as pending.
2. If `EventPlayerHurt` fires for that seeker as attacker within the same server frame, the shot is cleared (it hit someone).
3. After one server frame (`Server.NextFrame`), any shot still pending is counted as a miss and the penalty is applied.

This correctly ignores prop/wall hits, wallbang noise, and shotgun partial-pellet scenarios.

> **Note:** works independently of `ph_seeker_god_mode`. God mode blocks incoming game damage; the miss penalty writes directly to `pawn.Health` and is not affected by `TakesDamage`.

### `ph_seeker_health`
Applies `pawn.Health` (and `MaxHealth` if > 100) 0.5 s after the seeker spawns. Set to `0` to let the game assign its default HP.

---

## Knife guarantee

On every spawn the plugin checks each player's inventory 0.5 s after they are alive. If they have no knife or bayonet, one is given automatically via `GiveNamedItem("weapon_knife")`. This handles cases where the PropHunt game mode does not assign a default melee weapon.

---

## Round-start chat broadcast

At the start of each round the plugin prints the full active configuration to all players:

```
-------- PROP HUNT --------
➜ TAGrenade: Enabled
➜ RandomSeekers: Disabled
➜ MoveProps: Disabled
➜ ForceReroll: Disabled
➜ ForceTaunt: Enabled
➜ SeekerRespawnTime: 30s
➜ PropMaxRerolls: 3
➜ SeekerGodMode: Enabled
➜ SeekerInfiniteAmmo: Enabled
➜ SeekerHitMiss: Disabled
➜ SeekerHealth: 100 HP
```

---

## Example server config

```
// PropHunt plugin settings
ph_ta_grenade 1
ph_random_seekers 0
ph_move_props 0
ph_force_reroll 0
ph_force_taunt 1
ph_seeker_respawn_time 30
ph_prop_max_rerolls 3

// Seeker mechanics
ph_seeker_god_mode 1
ph_seeker_infinite_ammo 1
ph_seeker_hitmiss 5        // -5 HP per missed shot
ph_seeker_health 150       // seekers spawn with 150 HP
```
