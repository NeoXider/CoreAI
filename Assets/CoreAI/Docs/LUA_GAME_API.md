# Lua as a Second Game Language

Lua scripts (LLM envelopes and long-lived mods) can read the world, change it, override gameplay
logic, and communicate with the game through events. The sandbox (`SecureLuaEnvironment`, see
[LUA_SANDBOX_SECURITY.md](LUA_SANDBOX_SECURITY.md)) is not weakened by this: only the binding surface
grows, and every group is gated by a capability level.

**Lua is an optional module:** define `COREAI_NO_LUA` or remove the MoonSharp package, and CoreAI
builds without Lua (stub bindings in DI). See [LUA_SANDBOX_SECURITY.md § Optional Module](LUA_SANDBOX_SECURITY.md).

**New to mods?** Start with [FIRST_MOD.md](FIRST_MOD.md) — "Your first Lua mod in 5 minutes".
**Best practices and anti-patterns:** [LUA_BEST_PRACTICES.md](LUA_BEST_PRACTICES.md).
**MoonSharp: native vs custom:** [MOONSHARP_NATIVE_APIS.md](MOONSHARP_NATIVE_APIS.md).
**Access modes (Read -> Full):** [LUA_ACCESS_MODES.md](LUA_ACCESS_MODES.md).

All bindings run on the Unity main thread.

## Capability Levels

`LuaCapabilities` (flags): `Read`, `Gameplay`, `WorldEdit`, `LogicOverride`, `Full`, `All`.

`AggregatingGameLuaRuntimeBindings` registers a function group only if its level is granted: a script
with `Read` physically has no world-editing functions in globals. By default (DI), `All` is granted
(without `Full`), preserving historical behavior. Full is enabled explicitly with **Enable Full Lua Access**
on `CoreAILifetimeScope` or per mod through `LoadMod`.

Per-mod: `LuaModRuntime.LoadMod(id, code, caps)` passes caps to `ICapabilityScopedLuaBindings`; a
restricted mod **cannot** expand the host tier.

Persistent mod `report()` output is muted by default. Hosts can expose
`LuaModRuntime.SetModReportLoggingEnabled(id, true)` for per-mod diagnostics when console output is
needed.

| Level | Opens |
|---|---|
| `Read` | `log_*`, versions, `coreai_world_exists/pos/find/list_prefabs/raycast` |
| `Gameplay` | `time_*` (including `time_set_scale`), `input_*` (keyboard/mouse, read-only) |
| `WorldEdit` | `coreai_world_spawn`, `coreai_world_change`, `coreai_world_set_color`, `coreai_world_destroy`, scenes, batches, transactions |
| `LogicOverride` | `logic_define/reset/list` |
| `Full` | `unity_find`, `unity_get/set_member`, `unity_call`, ... (reflection, opt-in) |

Mod APIs (`hooks_on`/`hooks_every`, `store_set`/`store_get`, `events_emit`, `mods_export`/`mods_get`/`mods_call`/`mods_list_exports`, `report`, `mod_id`) are **not gated by a capability tier** — every loaded mod gets them regardless of its `LuaCapabilities`, since they are the mod-runtime surface itself rather than a game binding. See [LUA_ACCESS_MODES.md](LUA_ACCESS_MODES.md).

## Stage 1 - Reading the World (Query API)

`CoreAiWorldQueryLuaBindings` exposes a slice of applied state; commands published by the same script
may not have applied yet:

```lua
if coreai_world_exists("Boss") then
  local p = coreai_world_pos("Boss")            -- {x=..., y=..., z=...} or nil
  local near = coreai_world_find("enemy")        -- names (case-insensitive contains), max 100
  local hit = coreai_world_raycast(p.x, p.y + 10, p.z, 0, -1, 0, 50)
  if hit then log_info(hit.name .. " at distance " .. hit.distance) end
end
local prefabs = coreai_world_list_prefabs()      -- keys from CoreAiPrefabRegistryAsset
```

## Stage 2 - Logic Slots (Changing Mechanics)

The game declares override points and calls `TryInvoke*` at the use site, falling back to the C# default
(`LuaLogicSlots`):

```csharp
slots.DeclareSlot("damage_formula");
// in combat:
double dmg = slots.TryInvokeNumber("damage_formula", out double v, atk, def) ? v : DefaultDamage(atk, def);
```

```lua
logic_define("damage_formula", function(atk, def) return atk * 1.5 - def end)
logic_list()      -- { {name="damage_formula", overridden=true}, ... }
logic_reset("damage_formula")
```

Fail-open: an override that throws or exceeds its budget (200 ms / 200k instructions) is removed
automatically, the game returns to the C# default, and the error is written to the log and `LastError`.

## Stage 3 - Persistent Mod Runtime

`LuaModRuntime` (DI singleton; `LuaModRuntimeTicker` ticks it every frame):

```csharp
modRuntime.LoadMod("night_director", luaCode, LuaCapabilities.Read | LuaCapabilities.WorldEdit);
modRuntime.EmitEvent("wave_started", "3");          // game -> mods
modRuntime.ModEventEmitted += (mod, evt, payload) => ...; // mods -> game
modRuntime.ReloadMod("night_director", newCode);
modRuntime.UnloadMod("night_director");
```

```lua
-- inside a mod (runs during LoadMod, registers hooks):
hooks_on("wave_started", function(evt, payload)
  coreai_world_spawn({ prefab="enemy.basic", name="wave_" .. payload, x=0, y=0, z=10 })
end)
hooks_every(2.0, function() ... end)   -- interval >= 0.05 s
hooks_on("tick", function() ... end)   -- alias for hooks_every(0.05, ...): per-frame-ish callback
store_set("kills", "42")               -- persistent per-mod k/v (strings)
local v = store_get("kills")
events_emit("director_ready", "")      -- to other mods and the game
log_info(mod_id())
```

`hooks_on("tick"/"update"/"frame", fn)` is not a real per-frame event — it is routed to a
`hooks_every(0.05, fn)` timer (20 Hz), since nothing emits those names as events. Prefer
`hooks_every` directly when you want an explicit interval.

`coreai_world_spawn` creates visible objects only when the host prefab registry contains the prefab
key/name. Check `coreai_world_list_prefabs()` first; a `report("spawn...")` call is only a log.

Budgets: every handler call gets 100 ms / 100k instructions; <= 64 handlers and <= 16 timers per
mod; event queue <= 256 (oldest evicted); 8 consecutive errors unload the mod automatically.
Storage: `FileLuaModStore` (`persistentDataPath/CoreAI/LuaMods`, <= 256 keys, value <= 64 KB).

Persistence boundary: `FileLuaModStore` persists per-mod `store_set` / `store_get` string values.
It does not automatically persist or autoload the set of currently loaded mod source chunks; hosts
that want mod autoload should persist selected mod sources separately and call `LoadMod` / `ReloadMod`
during startup. One-shot `execute_lua` rule-slot edits can be persisted by the host with
`ILuaScriptVersionStore`; the LiveMechanics demo does this for its own known slots.

## Stage 4 - Level Primitives and Transactions

Batches do not hit the `execute_lua` rate limit: one call publishes up to 100 commands:

```lua
coreai_world_spawn_batch({
  {prefab="wall", name="w1", x=0, y=0, z=0},
  {prefab="wall", name="w2", x=2, y=0, z=0},
})
coreai_world_grid("floor_tile", "cell", 0, 0, 9, 9, 1, 0)  -- 10x10 max (<= 100 cells), names cell_ix_iz
coreai_world_spawn({
  prefab="turret",
  name="turret_1",
  x=0,
  y=0,
  z=4,
  ry=90,
  scaleX=2,
  scaleY=1,
  scaleZ=3,
  parent="tower"
})
coreai_world_change("turret_1", { x=2, ry=180, scale=1.5, parent="none" })
coreai_world_set_color("boss", "#ff3300")

coreai_world_begin()      -- buffer instead of publishing
coreai_world_grid("trap", "t", 0, 0, 4, 4, 1, 0)
coreai_world_rollback()   -- changed our mind: nothing published
coreai_world_begin()
coreai_world_spawn({ prefab="chest", name="reward", x=5, y=0, z=5 })
coreai_world_commit()     -- publish everything together
```

`coreai_world_spawn` requires `prefab` and `name`. Position, rotation (`rx/ry/rz`), scale, per-axis
scale (`scaleX/scaleY/scaleZ`), and `parent` are optional. `scale` is a uniform fallback; per-axis
scale is preferred when meters matter. `coreai_world_change(name, {...})` applies only the supplied
fields, so omitted axes and fields stay unchanged.

One transaction per bindings instance; buffer overflow (256) triggers an automatic rollback with an error.
There is no undo for already applied commands (see TODO).

## Stage 5 - Events

The bus lives inside `LuaModRuntime`: `events_emit` is delivered to all other mods (on the next `Tick`)
and to the C# event `ModEventEmitted`; the game sends events to mods through `EmitEvent`. Game-side
subscription is directly on the DI singleton `LuaModRuntime` (a MessagePipe adapter can be written in one line if needed).

## Cross-mod Exports

Besides events (fire-and-forget, broadcast), a mod can publish variables and functions for other
mods to read or call directly by id:

| Function | Effect |
|---|---|
| `mods_export(name, valueOrFn)` | Publishes a value or function under `name` for other mods (<= 64 exports per mod) |
| `mods_get(modId, name)` | Reads another mod's exported value (throws if `name` is a function - use `mods_call`) |
| `mods_call(modId, fnName, ...)` | Calls another mod's exported function and returns its result |
| `mods_list_exports(modId)` | Lists the export names a mod currently publishes |

Values cross by copy, not by reference: `nil`/boolean/number/string/plain tables only, tables nest
at most 4 levels, and `mods_call` chains are capped at depth 8 — a mod can never mutate another
mod's live state through an export, and cross-mod call cycles fail with a clear error instead of a
stack overflow. Exports are dropped on `ReloadMod`/`UnloadMod`; a reloaded mod must call
`mods_export` again.

```lua
-- name: economy
-- description: Owns the gold total and exports a reader plus an add function.
local gold = tonumber(store_get("gold")) or 0
mods_export("gold", function() return gold end)
mods_export("add_gold", function(amount)
  gold = gold + amount
  store_set("gold", tostring(gold))
  return gold
end)
```

```lua
-- name: shop
-- description: Reads and spends gold from the economy mod.
hooks_on("item_bought", function(evt, price)
  local current = mods_call("economy", "gold")
  if current >= tonumber(price) then
    mods_call("economy", "add_gold", -tonumber(price))
    report("shop: purchase ok, gold now " .. mods_call("economy", "gold"))
  else
    report("shop: not enough gold")
  end
end)
```

Use `events_emit`/`hooks_on` when a mod just needs to announce something happened; use
`mods_export`/`mods_call` when a mod needs to read or drive another mod's state directly.

## Input (Gameplay tier)

Mods and scripts with the `Gameplay` capability can read the keyboard and mouse, so game logic
(piece steering, click handling, movement) can live entirely in Lua
(`CoreAiInputLuaRuntimeBindings`, read-only over `UnityEngine.Input`):

| Function | Returns |
|---|---|
| `input_key(name)` | `true` while the key is held |
| `input_key_down(name)` / `input_key_up(name)` | `true` only on the press/release frame |
| `input_mouse_button(i)` / `input_mouse_down(i)` | mouse button held / pressed (0 left, 1 right, 2 middle) |
| `input_mouse_x()` / `input_mouse_y()` | cursor position in screen pixels (origin bottom-left) |
| `input_axis(name)` | `Input.GetAxis` value; `0` when the axis is undefined |

Key names are `KeyCode` spellings, case-insensitive (`'a'`, `'space'`, `'return'`, `'leftarrow'`),
plus aliases `'left'/'right'/'up'/'down'` (arrows) and `'0'..'9'` (top-row digits). Frame-edge
checks are true for a single frame — a mod timer slower than the frame rate can miss them, so poll
held state from `hooks_every` timers (20 Hz is plenty) and reserve `input_key_down` for
`hooks_on('tick')` handlers:

```lua
hooks_every(0.05, function()
  if input_key('a') then move(-1) end
  if input_key('d') then move(1) end
end)
```

## Persistence & Sharing

By default a loaded mod lives only in memory. A host can make mods durable and shareable by wiring an
`ILuaModSourceStore` into `LuaModRuntime` (constructor parameter `sourceStore`; `autoPersistMods`
defaults to `true`). The source store keeps a mod's **source plus its `LuaModManifest`** (`id`, `name`,
`description`, `version`, `author`, `capabilities`, `active`, `entry`). This is separate from
`ILuaModStore` / `FileLuaModStore`, which persists per-mod `store_set`/`store_get` values, not the mod
itself.

A file-backed implementation (`FileLuaModSourceStore`) lays each package out under
`persistentDataPath/CoreAI/Mods/<id>/` as `manifest.json` + `main.lua`. When no store is wired the
runtime uses `NullLuaModSourceStore` (in-memory only, exactly the historical behavior).

```csharp
// Auto-save on load/reload, mark dormant on unload, and auto-load active mods on startup.
var modRuntime = new LuaModRuntime(gameBindings, store, log, sourceStore: fileSourceStore);
int restored = modRuntime.RehydrateFromStore(LuaCapabilities.All);   // active mods reload
modRuntime.ForgetMod("greeter");                                      // delete the stored package
```

- **Auto-persist.** Every successful `LoadMod` / `ReloadMod` saves the source + manifest; `UnloadMod`
  flips the stored manifest to `Active = false` (dormant, not deleted). All store calls are
  best-effort — a store failure is logged and never aborts the load.
- **Rehydrate.** `RehydrateFromStore(hostGrant, allowFull = false)` re-loads every stored package whose
  manifest is `Active`, masking each mod's requested capabilities to `hostGrant` and stripping `Full`
  unless `allowFull` is set. Returns the count of mods reloaded.
- **Export / import.** `ExportMod(id)` returns a self-contained bundle `{"manifest":{...},"source":"..."}`
  (or `null` for an unknown id). `ImportMod(bundleJson, hostGrant, allowFull = false)` loads it on
  another host with the same capability masking. `ForgetMod(id)` permanently removes the stored package.
- **Security.** Persisted, rehydrated, imported, and copied mods are **never** granted `Full` unless the
  host explicitly opts in. A shared mod can only ever request capabilities; the host grant decides.

### Mod versioning (revision history + rollback)

Pass an `ILuaScriptVersionStore` into `LuaModRuntime` (constructor parameter `versionStore`; the Unity
installer wires the host's existing store automatically) to record a **revision per edit**, keyed by
`mod:<id>` so a mod's history shares the version store with one-shot `execute_lua` scripts without
colliding with a game-defined script slot. Each successful `LoadMod` / `ReloadMod` calls
`SeedOriginal` (establishing revision `0`) then `RecordSuccessfulExecution`, which appends a new revision
**only when the source actually changed** — a no-op reload does not grow the history. The persisted and
exported `LuaModManifest.Version` is then **auto-derived** as the revision count (`"1"` for a freshly
seeded mod, `"3"` after three distinct edits), so the host never manages it by hand. When no version
store is wired the runtime uses `NullLuaScriptVersionStore` (no history — the prior behavior).

```csharp
var modRuntime = new LuaModRuntime(gameBindings, store, log,
    sourceStore: fileSourceStore, versionStore: scriptVersions);
IReadOnlyList<LuaScriptRevision> history = modRuntime.ListModVersions("greeter"); // 0 = original
modRuntime.TryRevertMod("greeter", revisionIndex: 0, out string restored);        // roll back
```

`TryRevertMod(id, revisionIndex, out restored)` rolls a loaded mod back by **reloading** it from the
chosen revision's source (a non-destructive revert: the reload appends the restored source as the new
current revision and re-persists). If the restored source fails to reload, the live mod is left
untouched, exactly like `ReloadMod`.

### Runtime handler-error feedback

Load/reload errors propagate synchronously to whoever triggered them, but a hook or timer that throws
**later**, during `Tick`, only raises `ModHandlerErrored` (and counts toward host-side auto-unload). The
runtime now also buffers these Tick-time failures in a bounded ring (`MaxRetainedHandlerErrors`), readable
via `GetRecentHandlerErrors(modId = null)` and clearable via `ClearRecentHandlerErrors(modId = null)`, so
the agent can learn of them on a later turn through `manage_mods diagnostics` and repair the mod.

The `manage_mods` tool exposes the same flow to the agent: `export`, `import`, `forget`, `versions`,
`revert`, and `diagnostics` in addition to `load`, `reload`, `unload`, `list`, `get_source`. See
[FIRST_MOD.md](FIRST_MOD.md) for a worked walkthrough.

## LLM Tools (Programmer Role)

| Tool | Purpose |
|---|---|
| `execute_lua` | One-shot Lua in the sandbox (same bindings and limits as the envelope pipeline) |
| `manage_mods` | `list`, `get_source`, `load`, `reload`, `unload`, `export`, `import`, `forget`, `versions`, `revert`, `diagnostics` for `LuaModRuntime` |

`manage_mods` does not let the model expand the capability tier; the host sets the tier when registering the tool.
When `enableFullLuaAccess` is on, mods loaded through the built-in Programmer `manage_mods` tool receive
the same Full grant; otherwise they get `LuaCapabilities.All` without Full.
Read-only introspection: `LuaModsLlmTool(..., allowModManagement: false)`.

## Full Mode (`unity_*`)

Opt-in through **Enable Full Lua Access** on `CoreAILifetimeScope` or `LoadMod(..., caps | Full)`.
Policy is **allow-all by default**; hosts can inject `IFullLuaAccessBlacklistPolicy` to deny component types
or specific members.

```lua
-- One-shot diagnostic: inspect first, then decide what mod/edit to make.
local matches = unity_find_by_component("Light", 10)
if #matches == 0 then return '{"found":false}' end
local sun = unity_describe_object(matches[1].id)
return '{"found":true,"name":"' .. sun.name .. '","path":"' .. sun.path .. '"}'
```

Scene discovery and hierarchy:

```lua
local all = unity_list_objects(100)                  -- {id,name,path,tag,layer,active,...}
local enemies = unity_find_all("Enemy", 50)          -- name/path contains, case-insensitive
local players = unity_find_by_tag("Player", 10)
local movers = unity_find_by_component("Mover", 20)
local desc = unity_describe_object(enemies[1].id)    -- transform, components, parent/children count
local children = unity_get_children(desc.id)
```

Transform and hierarchy edits:

```lua
local id = unity_find("MovingPlatform")
local t = unity_get_transform(id)
unity_set_position(id, t.position.x + 2, t.position.y, t.position.z)
unity_set_rotation_euler(unity_find("Directional Light"), 45, 90, 0)
unity_set_scale(id, 2, 1, 2)
unity_parent(id, unity_find("LevelRoot"), true)
```

Spawn, delete, hierarchy, colour, and common transform control are intentionally available without
Full through `WorldEdit`: use `coreai_world_spawn`, `coreai_world_change`,
`coreai_world_set_color`, and `coreai_world_destroy` when object names and prefab keys are known.
Full remains for diagnostics and reflection-only cases.

Demo: `Assets/CoreAI.Demos/FullAccess/`. For production, targeted bindings are preferable, or the
future migration to MoonSharp `UserData.RegisterType` (see MOONSHARP_NATIVE_APIS.md).

## Host Configuration (Unity)

On `CoreAILifetimeScope`:

| Field | Effect |
|---|---|
| `worldPrefabRegistry` | Whitelist prefab-id for spawn |
| `luaAllowedScenes` | Whitelist scene names for `coreai_world_load_scene` (empty = any scene from Build Settings) |
| `enableFullLuaAccess` | Adds `Full` to the aggregator capability |

## Extending the Game API

### Custom Lua Functions

`GameLuaBindingsExtensibility.Register(bindings, requiredCapabilities)` before scene startup.
Examples: [LUA_BEST_PRACTICES.md § Custom Functions](LUA_BEST_PRACTICES.md).

### Custom World Commands (Without Editing CoreAI)

`CoreAiWorldCommandExecutor.RegisterCustomHandler(ICoreAiCustomWorldCommandHandler)`: the action goes
into the same pipeline as LLM/Lua world commands. Example in LUA_BEST_PRACTICES.md.

## Related Documents

- [LUA_BEST_PRACTICES.md](LUA_BEST_PRACTICES.md) - how to do it / how **not** to do it
- [LUA_SANDBOX_SECURITY.md](LUA_SANDBOX_SECURITY.md) - security and checklists
- [MOONSHARP_NATIVE_APIS.md](MOONSHARP_NATIVE_APIS.md) - native MoonSharp APIs
- [LUA_ACCESS_MODES.md](LUA_ACCESS_MODES.md) - access modes
- Demo: `Assets/CoreAI.Demos/README.md`
