# Lua as a Second Game Language

Lua scripts (LLM envelopes and long-lived mods) can read the world, change it, override gameplay
logic, and communicate with the game through events. The sandbox (`SecureLuaEnvironment`, see
[LUA_SANDBOX_SECURITY.md](LUA_SANDBOX_SECURITY.md)) is not weakened by this: only the binding surface
grows, and every group is gated by a capability level.

**Lua is an optional module:** define `COREAI_NO_LUA` or remove the MoonSharp package, and CoreAI
builds without Lua (stub bindings in DI). See [LUA_SANDBOX_SECURITY.md § Optional Module](LUA_SANDBOX_SECURITY.md).

**Best practices and anti-patterns:** [LUA_BEST_PRACTICES.md](LUA_BEST_PRACTICES.md).
**MoonSharp: native vs custom:** [MOONSHARP_NATIVE_APIS.md](MOONSHARP_NATIVE_APIS.md).
**Access modes (Read -> Full):** [LUA_ACCESS_MODES_AUDIT.md](LUA_ACCESS_MODES_AUDIT.md).

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
| `Gameplay` | `time_*` (including `time_set_scale`) |
| `WorldEdit` | `coreai_world_spawn/move/rotate/set_transform/destroy/...`, batches, transactions, `set_props`, `parent` |
| `LogicOverride` | `logic_define/reset/list`, mod APIs (`hooks_*`, `store_*`, `events_emit`) |
| `Full` | `unity_find`, `unity_get/set_member`, `unity_call`, ... (reflection, opt-in) |

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
  coreai_world_spawn("enemy.basic", "wave_" .. payload, 0, 0, 10)
end)
hooks_every(2.0, function() ... end)   -- interval >= 0.05 s
store_set("kills", "42")               -- persistent per-mod k/v (strings)
local v = store_get("kills")
events_emit("director_ready", "")      -- to other mods and the game
log_info(mod_id())
```

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
coreai_world_move("turret_1", 2, 0, 4)
coreai_world_rotate("turret_1", 0, 90, 0)
coreai_world_set_transform("turret_1", 2, 0, 4, 0, 180, 0, 1.5)
coreai_world_parent("turret_1", "tower")                     -- "" or "none" = detach
coreai_world_set_props("boss", {scale=2.5, color="#ff3300"}) -- whitelist: scale, color

coreai_world_begin()      -- buffer instead of publishing
coreai_world_grid("trap", "t", 0, 0, 4, 4, 1, 0)
coreai_world_rollback()   -- changed our mind: nothing published
coreai_world_begin()
coreai_world_spawn("chest", "reward", 5, 0, 5)
coreai_world_commit()     -- publish everything together
```

One transaction per bindings instance; buffer overflow (256) triggers an automatic rollback with an error.
There is no undo for already applied commands (see TODO).

## Stage 5 - Events

The bus lives inside `LuaModRuntime`: `events_emit` is delivered to all other mods (on the next `Tick`)
and to the C# event `ModEventEmitted`; the game sends events to mods through `EmitEvent`. Game-side
subscription is directly on the DI singleton `LuaModRuntime` (a MessagePipe adapter can be written in one line if needed).

## LLM Tools (Programmer Role)

| Tool | Purpose |
|---|---|
| `execute_lua` | One-shot Lua in the sandbox (same bindings and limits as the envelope pipeline) |
| `manage_mods` | `list`, `get_source`, `load`, `reload`, `unload` for `LuaModRuntime` |

`manage_mods` does not let the model expand the capability tier; the host sets the tier when registering the tool.
When `enableFullLuaAccess` is on, mods loaded through the built-in Programmer `manage_mods` tool receive
the same Full grant; otherwise they get `LuaCapabilities.All` without Full.
Read-only introspection: `LuaModsLlmTool(..., allowModManagement: false)`.

## Full Mode (`unity_*`)

Opt-in through **Enable Full Lua Access** on `CoreAILifetimeScope` or `LoadMod(..., caps | Full)`.
Policy is **allow-all** (type blacklist is Planned; see the audit).

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

Spawn, delete, hierarchy, and common transform control are intentionally available without Full
through `WorldEdit`: use `coreai_world_spawn`, `coreai_world_destroy`, `coreai_world_parent`,
`coreai_world_move`, `coreai_world_rotate`, and `coreai_world_set_transform` when object names and
prefab keys are known. Full remains for diagnostics and reflection-only cases.

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
- [LUA_ACCESS_MODES_AUDIT.md](LUA_ACCESS_MODES_AUDIT.md) - access modes
- Demo: `Assets/CoreAI.Demos/README.md`
