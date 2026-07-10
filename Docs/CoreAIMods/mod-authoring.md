# CoreAI Lua mod authoring guide

> How to write a CoreAI Lua mod. Audience: the AI agent and human authors. Runtime VM: Lua-CSharp.
> Examples: `Assets/CoreAI.Demos/Mods/*.lua`.

## What a mod is
A mod is Lua source that the runtime runs ONCE through `LoadMod`. During that run it **registers hooks**;
afterwards the host drives those hooks. Persistent mods live across frames (and reloads). A one-off script
(`execute_lua`) runs once and is not persisted.

## Header
Start with a `@coreai` block so the mod is discoverable/manageable:
```lua
--[[@coreai
id: my_mod              -- stable slug = identity (rename = new mod). [a-z0-9_]
name: My Mod            -- display name (cosmetic)
version: 1.0.0          -- semver
active: false           -- seeded enabled/disabled
capabilities: All       -- e.g. All  |  All, Full
author: CoreAI
description: one line.
]]
```

## Always-available mod API (no capability tier needed)
| Function | Meaning |
|---|---|
| `hooks_on(event, fn)` | `fn(eventName, payload)` runs when the game or another mod emits `event`. |
| `hooks_every(seconds, fn)` | repeating timer; `seconds >= 0.05`. `"tick"`/`"update"`/`"frame"` hooks map to a ~20 Hz timer. |
| `events_emit(name, payload)` | emit an event to the game + other mods. `payload` is a **string**. |
| `store_set(key, value)` / `store_get(key)` | per-mod persistent string key/value (survives frames + reloads). |
| `mod_id()` | this mod's id. |
| `report(msg)` / `print(msg)` | diagnostic line back to the host (muted by default; host enables per mod). |

## Inter-mod API (cross-mod, plain-data only)
| Function | Meaning |
|---|---|
| `mods_export(name, value)` | publish a value OR function under this mod's id. |
| `mods_get(modId, name)` | read another mod's exported **plain data** (nil for a function export). |
| `mods_call(modId, name, ...)` | call another mod's exported **function** on its own state; returns a copied result. |
| `mods_list_exports(modId)` | list export names (introspection — the AI discovers callable APIs). |

**Hard rule (multiplayer-determinism seam):** only PLAIN DATA (numbers/strings/bools/tables) crosses the mod
boundary. Functions, closures, and live references never leave a mod's own state — `mods_call` runs the
function in the provider and copies back the result. Nesting is capped (`CrossModTableDepth = 4`), cross-call
depth is capped (`MaxCrossCallDepth = 8`). See `shared_stats_provider.lua` + `shared_stats_consumer.lua`.

## Capability tiers — gate the GAME bindings
The mod-core + inter-mod API above is always present. Tiers gate the **game** bindings:
- **Read** — query only.
- **WorldEdit** — `coreai_world_*` (spawn/change/destroy/scene/animation/sound) via the authoritative command channel.
- **LogicOverride** — `logic_*` formulas.
- **Full** — `unity_*` generic reflection (get/set fields, call methods on ANY component). Host/singleplayer-only,
  stripped on network clients. Opt-in ("Enable Full Lua Access"); NOT part of `All`.
A binding absent from your tier simply doesn't exist in the sandbox (calling it errors).

## Coroutines (work across frames, WebGL-safe)
`coroutine.create/resume/yield/status` are available. A coroutine lets a mod spread a sequence over time
without blocking — it yields, the host advances the frame, and you resume it next tick. Under Lua-CSharp this
is frame-pumped, so `coroutine.yield` works on WebGL too (a blocking wait would deadlock single-threaded WASM).
See `coroutine_countdown.lua`. Do NOT busy-wait; yield and resume from a timer/handler.

## Design rule: native/Lua boundary
C# owns per-frame hot loops (movement, camera, physics). Lua **tweaks parameters and reacts to discrete
events** — it does not run the hot loop. "Change a mechanic while playing" should DECLARE the change / emit a
command, not spin a transform every frame. Prefer routing world changes through events/commands (the
authoritative channel) over direct mutation — it stays deterministic and multiplayer-ready. See
`day_night_cycle.lua`.

## Sandbox & limits
- No `io`/`os`/`debug`; `load`/`loadstring`/`dofile`/`loadfile` are removed.
- An **instruction budget** (via Lua-CSharp `SetHook`) cuts a runaway handler (`while true do end`) on ALL
  platforms incl. WebGL — a buggy mod cannot hang a frame.
- Caps: per-handler steps/time, timer min interval `0.05 s`, exports/mod, dispatch per tick (no events dropped;
  serviced on later ticks), auto-unload after consecutive failures.

## Lua version note
Lua-CSharp targets **Lua 5.4** semantics. Watch: integer/float subtype, bitwise operators
(`&`, `|`, `~`, `<<`, `>>` are native, not a `bit` library), and partial stdlib coverage — a missing library
function errors, so keep to common `string`/`table`/`math` calls.

## Bundled mods — ship a game with ready-made mods
Drop `.lua` files (each with an `@coreai` header) into a **`Resources/CoreAIMods/`** folder. On the first
run `BundledModSeeder` (wired in `CoreAiModsInstaller`, runs before rehydrate) installs them into the
persistent store; `active: true` mods load immediately, `active: false` ones ship dormant (enable from the
Hub Mods tab). Two samples live in `Assets/CoreAIMods/Runtime/Resources/CoreAIMods/`
(`sample_welcome.lua`, `sample_camera_pulse.lua`).

Updates are version-driven and player-respectful:
- Bump the header `version:` and re-ship → the seeder **updates** an unmodified copy, keeping the player's
  enabled/disabled choice.
- If the player edited the mod, it is **not** overwritten — the entry is flagged `UpdateAvailable` for a
  manual update in the UI.
- A same-or-older version, or a mod the player authored under the same id, is left untouched.

Hosts can add more `IBundledModSource`s (StreamingAssets, Addressables, remote) alongside the Resources
one; see `Docs/CoreAIMods/mod-system.md` §3.

## Example mods (`Assets/CoreAI.Demos/Mods/`)
- `hello_world.lua` — minimal: one event hook, one timer, a persistent counter.
- `score_tracker.lua` — events + store persistence.
- `day_night_cycle.lua` — a live mechanic via timer + `events_emit` (native/Lua boundary).
- `coroutine_countdown.lua` — a coroutine yielding across ticks (WebGL-safe).
- `shared_stats_provider.lua` / `shared_stats_consumer.lua` — inter-mod `mods_export`/`mods_get`/`mods_call`.
- `full_mode_cube.lua` / `first_person_controller.lua` — Full-tier `unity_*` (reflection) examples.
