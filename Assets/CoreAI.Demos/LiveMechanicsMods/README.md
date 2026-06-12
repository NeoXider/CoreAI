# Live Mechanics Mods Demos

Folder: `Assets/CoreAI.Demos/LiveMechanicsMods/`

This folder contains two chat-driven Lua mod demos. Both use `manage_mods`, persist successful mod
sources, and expose a runtime mod manager panel.

## Scenes

| Scene | Purpose |
|---|---|
| `LiveMechanicsModsChatDemo.unity` | Boss-rule sandbox based on `LiveMechanicsDemo`: quick mods for boss reward and attack interval. |
| `WaveAutoBattlerModsDemo.unity` | Full auto-battler: our hero fights scaling enemy waves, levels up, earns gold, and Lua mods change real combat rules. |

## Mod Manager Panel

- Toggle: `F10`.
- Active mods show name, id, description, capabilities, handler/timer counts and error count.
- `X` deactivates a mod. The source is not lost; it moves to the saved/unloaded list.
- Saved/unloaded mods can be activated again from the panel.
- Deactivated mods stay inactive across scene restarts until the user presses `Activate`.
- `Forget` removes a saved source from the demo list.
- Name and description come from Lua metadata comments:

```lua
-- name: Battle Scaler
-- description: Makes waves denser, enemies tougher, and rewards higher.
```

The generic `LuaModRuntime` still does not autoload arbitrary source by itself. These scenes are
host policies: they decide which saved sources are trusted enough to restore.

## Wave Auto-Battler

Scene: `Assets/CoreAI.Demos/LiveMechanicsMods/WaveAutoBattlerModsDemo.unity`

Runtime loop:

- The hero auto-attacks the front enemy.
- Enemies attack back as a group.
- Cleared waves grant gold and XP.
- Level-ups increase hero HP and attack.
- Later waves increase enemy count, HP and damage.

Lua slots available to mods:

| Slot | Args | Effect |
|---|---|---|
| `hero_damage` | `heroAttack, heroLevel, wave` | Hero hit damage. |
| `hero_attack_interval` | `heroLevel, wave` | Seconds between hero attacks. |
| `hero_regen` | `heroLevel, wave` | HP regenerated per second. |
| `enemy_count` | `wave` | Enemies spawned in the next wave. |
| `enemy_hp` | `wave` | Max HP per enemy in the next wave. |
| `enemy_damage` | `wave` | Damage per enemy attack. |
| `wave_reward` | `wave` | Gold gained after clearing a wave. |

Events emitted to mods:

| Event | Payload |
|---|---|
| `battle_tick` | `wave:heroLevel:enemyCount` |
| `wave_started` | `wave:enemyCount:enemyHp:enemyDamage` |
| `enemy_defeated` | `wave:remainingEnemyCount` |
| `wave_cleared` | `wave:reward:heroLevel` |
| `hero_level_up` | `heroLevel` |
| `hero_died` | `wave` |

Ready prompt buttons insert prompts into the chat input:

- Healer Aura: creates a regen + wave-cleared hook mod.
- Battle Scaler: changes enemy count, HP, damage and rewards.
- Modify Battle Scaler: asks the model to inspect and reload an existing mod.

## LiveMechanics Mods Chat

Scene: `Assets/CoreAI.Demos/LiveMechanicsMods/LiveMechanicsModsChatDemo.unity`

This is still useful as a small boss-rule sandbox. Prompt buttons insert ready requests for:

- Boss Reward 1000.
- Modifying the existing boss reward mod.
- Fast Attacks.

The mod manager panel works the same way as in the auto-battler scene.

## Persistence

Saved sources are stored through `ILuaScriptVersionStore`, normally backed by
`persistentDataPath/CoreAI/LuaScriptVersions`.

Scene prefixes:

```text
demo.live_mechanics.mods_chat.mod.
demo.wave_auto_battler.mods_chat.mod.
```
