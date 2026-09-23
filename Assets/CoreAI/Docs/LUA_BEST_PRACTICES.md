# CoreAI Lua: Best Practices and Anti-Patterns

> See also [LUA_GAME_API.md](LUA_GAME_API.md), [LUA_SANDBOX_SECURITY.md](LUA_SANDBOX_SECURITY.md), [LUA_NATIVE_APIS.md](LUA_NATIVE_APIS.md).

## Principle

**Lua proposes changes; C# decides whether they are legal.** The Lua-CSharp sandbox cuts system APIs;
capability levels cut game APIs; validators in bindings cut specific values.

---

## ✅ How to Do It Correctly

### 1. Mechanics Through Logic Slots (Preferred)

The game stays in C#. Lua only overrides **declared** extension points:

```csharp
// Startup
slots.DeclareSlot("damage_formula");

// Combat tick
double dmg = slots.TryInvokeNumber("damage_formula", out double v, atk, def)
    ? v
    : DefaultDamage(atk, def);
```

```lua
logic_define("damage_formula", function(atk, def)
  return atk * 1.5 - def * 0.5
end)
```

Benefits: fail-open (broken overrides are removed), the C# default always exists, and the LLM surface is narrow.

### 2. Long-Lived Rules Through Mods

Wave directors, day/night, progression: `ILuaModRuntime.LoadMod` + `hooks_on` / `hooks_every`. Every
management call takes the calling `ActorContext` first (see [FIRST_MOD.md](FIRST_MOD.md)):

```csharp
modRuntime.LoadMod(host, "wave_director", luaCode,
    LuaCapabilities.Read | LuaCapabilities.WorldEdit);
modRuntime.EmitEvent(host, "wave_started", waveIndex.ToString());
```

Per-mod capability is **already enforced**: a read-only mod will not receive world-edit APIs.

For per-frame-ish logic, `hooks_on("tick", fn)` is a convenience alias for `hooks_every(0.05, fn)` —
prefer polling held input (`input_key`, `Gameplay` tier) from a timer/tick handler over
`input_key_down`/`input_key_up`, since a frame-edge check can be missed between timer ticks.

When one mod needs another mod's help: `events_emit`/`hooks_on` for a fire-and-forget notification
(broadcast, no reply), `mods_export`/`mods_get`/`mods_call` when a mod needs to read or call another
mod's state directly by id (see [LUA_GAME_API.md § Cross-mod Exports](LUA_GAME_API.md#cross-mod-exports)).

### 3. Custom Functions Through Typed Delegates

Typed `Func`/`Action`, with no reflection in Lua. The gameplay bindings are assembled inside
`LuaCsModRuntimeFactory`; a host that builds its own stack adds a binding group through
`LuaCsModStackOptions.AdditionalGameplayBindings` (the Unity `CoreAiModsLifetimeScope` does not expose
it yet). The binding pattern itself is a `LuaCsApiRegistry` of typed delegates
(see [LUA_NATIVE_APIS.md § Registering a Native API](LUA_NATIVE_APIS.md)):

```csharp
LuaCsApiRegistry registry = new();
registry.Register("health_get", new Func<string, double>(name =>
{
    Health h = GameObject.Find(name)?.GetComponent<Health>();
    return h != null ? h.Current : -1;
}));
registry.Register("health_set", new Action<string, double>((name, v) =>
{
    Health h = GameObject.Find(name)?.GetComponent<Health>();
    h?.Set(Mathf.Clamp((float)v, 0f, h.Max));
}));
```

Lua-CSharp marshals delegates directly; do not wrap them in `DynamicInvoke` yourself.

### 4. Custom World Commands Through `ICoreAiCustomWorldCommandHandler`

No CoreAI fork; the same MessagePipe -> main thread path:

```csharp
public sealed class HealWorldHandler : ICoreAiCustomWorldCommandHandler
{
    public bool CanHandle(string action) =>
        string.Equals(action, "heal_player", StringComparison.OrdinalIgnoreCase);

    public bool TryExecute(CoreAiWorldCommandEnvelope env)
    {
        float amount = env.floatValue;
        Player.Instance.Heal(amount);
        return true;
    }
}

// On an executor your host constructs itself:
CoreAiWorldCommandExecutor executor = new(logger, prefabRegistry);
executor.RegisterCustomHandler(new HealWorldHandler());
```

`RegisterCustomHandler` lives on the concrete `CoreAiWorldCommandExecutor`. The default composition
(`WorldCommandsInstaller`) registers the executor only as `ICoreAiWorldCommandExecutor`, wrapped in
`AuditedWorldCommandExecutor`, so `container.Resolve<CoreAiWorldCommandExecutor>()` does not resolve and
the audited wrapper does not expose the inner instance. Until the composition offers a registration
seam, a custom handler only runs on an executor instance your host constructs itself.

From Lua (WorldEdit): publish an envelope through the existing sink or add a thin Lua wrapper in extension bindings.

### 5. Limit the LLM Surface

| Task | Minimum tier |
|---|---|
| Read the world only | `Read` |
| Change time scale / UI | `Read \| Gameplay` |
| Spawn / level edit | `Read \| WorldEdit` |
| Formulas / mods | `+ LogicOverride` |
| Arbitrary components | `+ Full` (dev / trusted builds only) |

Configuration: caps on the stack (`LuaCsModStackOptions.Capabilities`), `LoadMod(..., caps)`, and the
`CoreAiModsLifetimeScope` inspector (**Enable Full Lua Access**, **Enable Full Lua Private Access**).

### 6. Host-Side Whitelists

- Prefab spawn: `CoreAiPrefabRegistryAsset` (the **World Prefab Registry** on `CoreAiLuaWorldModule`)
- Load scene: `allowedScenes` on `CoreAiLuaWorldModule` is enforced by the world-command executor for
  every `load_scene` command; `allowedLuaScenes` on `CoreAiModsLifetimeScope` is checked by the Lua
  `coreai_world_load_scene` binding (a classic build binding, withheld by default)
- Full: `enableFullLuaAccess` on `CoreAiModsLifetimeScope` (off by default)

### 7. Full Mode: Diagnose Before Editing

Use Full as a targeted scene-inspection/edit tier, not as a replacement for game APIs. The Programmer
should first run a one-shot `execute_lua` diagnostic, return compact JSON/string through `Output`,
and only then load/reload a persistent mod:

```lua
local targets = unity_find_by_component("Light", 10)
if #targets == 0 then return '{"found":false}' end
local sun = unity_describe_object(targets[1].id)
return '{"found":true,"id":' .. sun.id .. ',"path":"' .. sun.path .. '"}'
```

For persistent mods, rediscover objects by name/tag/component inside the hook instead of storing
old instance ids forever; scene reloads invalidate object identity.

### 8. Lua-CSharp: Use Native Facilities

| Task | Native | Do not reinvent |
|---|---|---|
| Sandbox modules | secured `LuaState.Environment` (curated stdlib subset) | A custom Lua parser |
| Frame coroutines | `LuaCsCoroutineHandle` + `coroutine.yield()` | Busy-loop in a one-shot chunk |
| CLR callbacks | `LuaCsApiRegistry.Register(name, typedDelegate)` | `GetComponent` from Lua through reflection without Full tier |
| One-shot CPU limit | `LuaCsExecutionGuard` | Infinite `while true` with no limit |
| Memory budget | `LuaCsSecureEnvironment.MaxAllocatedBytesBudget` | Unbounded string/table growth |
| CLR objects in Lua (Full+) | targeted reflection bindings guarded by `IFullLuaAccessBlacklistPolicy` | Raw reflection on every call |

### 9. Logging

In CoreAiUnity, use **`IGameLogger`** / `GameLogFeature`, not `Debug.Log*` in runtime code
(exception: `UnityGameLogSink`, which is a sink).

### 10. Tests

- EditMode: `LuaCsSecureSandboxEditModeTests`, `LuaCsModRuntimeEditModeTests`, binding tests
  (coroutine budgets: `RbxScriptContextEditModeTests`, `RbxHeartbeatBudgetKillEditModeTests`)
- PlayMode: FastNoLlm integrations (for example `CoreAiDemoScenesSmokePlayModeTests`)
- CI: default (Lua disabled) / `COREAI_LUA` opt-in matrix

---

## ❌ How Not to Do It

### Security

| Anti-pattern | Why it is bad |
|---|---|
| Open `io` / `os` / `debug` / `package` / `load` in `LuaCsSecureEnvironment` | Files, eval, introspection |
| Full mode without `IFullLuaAccessBlacklistPolicy` | Any CLR type/member reachable from Lua |
| Full mode in production multiplayer without review | Any script can touch any component |
| Trust `pcall` in Lua instead of a C# guard | A script can swallow its own errors; the host must still catch and report failures |
| Skip the scene whitelists (`allowedLuaScenes`, `allowedScenes`) in public chat mode | The LLM can request any scene from Build Settings |
| Weaken `StripRiskyGlobals` "for convenience" | package/load/collectgarbage are escape vectors |

### Game Architecture

| Anti-pattern | Why it is bad |
|---|---|
| Put all mechanics in Lua from day one | No C# default, harder debugging and shipping |
| `logic_define` on a slot the game did not declare | Runtime error; slots only through `DeclareSlot` |
| One 500-line `execute_lua` every frame | Rate limit, latency, LLM context; use mods |
| Store game state only in `store_set` | Strings, 64 KB cap; critical state belongs in C# |
| `GetComponent` / reflection every frame through the Full API | GC and perf; cache ids, use slots |

### Lua-CSharp / Perf

| Anti-pattern | Why it is bad |
|---|---|
| `DynamicInvoke` + `ToObject` on every binding call | Loses typed marshalling; use `LuaCsApiRegistry.Register` with a typed delegate instead |
| Mix instruction-count limits and time-based timeouts without understanding their semantics | Different failure modes; see LUA_NATIVE_APIS.md |
| `renderer.material.color = ...` in a tight loop | Material instances; use `MaterialPropertyBlock` |
| `string.rep(1, 1e9)` without a cap | Allocation bomb; capped in `LuaCsSecureEnvironment` |

### LLM / Context

| Anti-pattern | Why it is bad |
|---|---|
| Put all mod source in the system prompt | `MaxResultSummaryLength` / `MaxErrorMessageLength` caps; use `manage_mods get_source` |
| Infinite repair loop | Rate limiter + `MaxLuaRepairRetries`; do not disable without a reason |
| Prompt "use any Unity API" | The model will invent nonexistent globals |

---

## Pre-Ship Checklist

- [ ] Capability tier is minimal for the scenario
- [ ] Full is off (or deliberately enabled with an audit)
- [ ] Prefab + scene whitelists are configured
- [ ] Custom bindings check the `LuaCapabilities` they are registered with
- [ ] Slots are declared in C# before `logic_define`
- [ ] Escape tests / EditMode sandbox tests pass
- [ ] Both the default no-Lua build and the `COREAI_LUA` build are checked
- [ ] Programmer prompt lists **only** real APIs

---

## Demos and Examples

| Scene | Path |
|---|---|
| Lua mods + logic slots | `Assets/CoreAI.Demos/LuaMods/` |
| World command pipeline | `Assets/CoreAI.Demos/WorldCommands/` |
| Live LLM -> mechanics | `Assets/CoreAI.Demos/LiveMechanics/` |
| Full reflection | `Assets/CoreAI.Demos/FullAccess/` |
