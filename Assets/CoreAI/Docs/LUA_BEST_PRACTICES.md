# CoreAI Lua: Best Practices and Anti-Patterns

> Current for v4.x. See also [LUA_GAME_API.md](LUA_GAME_API.md), [LUA_SANDBOX_SECURITY.md](LUA_SANDBOX_SECURITY.md), [MOONSHARP_NATIVE_APIS.md](MOONSHARP_NATIVE_APIS.md).

## Principle

**Lua proposes changes; C# decides whether they are legal.** The MoonSharp sandbox cuts system APIs;
capability levels cut game APIs; validators in bindings cut specific values.

---

## ✅ How to Do It Correctly

### 1. Mechanics Through Logic Slots (Preferred)

The game stays in C#. Lua only overrides **declared** extension points:

```csharp
// Startup
slots.DeclareSlot("damage_formula");

// Combat tick
double dmg = slots.TryInvokeNumber("damage_formula", out var v, atk, def)
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

Wave directors, day/night, progression: `LuaModRuntime.LoadMod` + `hooks_on` / `hooks_every`:

```csharp
modRuntime.LoadMod("wave_director", luaCode,
    LuaCapabilities.Read | LuaCapabilities.WorldEdit);
modRuntime.EmitEvent("wave_started", waveIndex.ToString());
```

Per-mod capability is **already enforced**: a read-only mod will not receive world-edit APIs.

For per-frame-ish logic, `hooks_on("tick", fn)` is a convenience alias for `hooks_every(0.05, fn)` —
prefer polling held input (`input_key`, `Gameplay` tier) from a timer/tick handler over
`input_key_down`/`input_key_up`, since a frame-edge check can be missed between timer ticks.

When one mod needs another mod's help: `events_emit`/`hooks_on` for a fire-and-forget notification
(broadcast, no reply), `mods_export`/`mods_get`/`mods_call` when a mod needs to read or call another
mod's state directly by id (see [LUA_GAME_API.md § Cross-mod Exports](LUA_GAME_API.md#cross-mod-exports)).

### 3. Custom Functions Through `GameLuaBindingsExtensibility`

Typed `Func`/`Action`, with no reflection in Lua:

```csharp
public sealed class HealthLuaBindings : IGameLuaRuntimeBindings
{
    public void RegisterGameplayApis(LuaApiRegistry registry)
    {
        registry.Register("health_get", new Func<string, double>(name =>
        {
            var h = GameObject.Find(name)?.GetComponent<Health>();
            return h != null ? h.Current : -1;
        }));
        registry.Register("health_set", new Action<string, double>((name, v) =>
        {
            var h = GameObject.Find(name)?.GetComponent<Health>();
            h?.Set(Mathf.Clamp((float)v, 0f, h.Max));
        }));
    }
}

// Before scene load / in early bootstrap:
GameLuaBindingsExtensibility.Register(
    new HealthLuaBindings(),
    LuaCapabilities.Gameplay);  // only scripts with Gameplay+
```

MoonSharp marshals delegates directly; do not wrap them in `DynamicInvoke` yourself.

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

// After DI resolve:
container.Resolve<CoreAiWorldCommandExecutor>()
    .RegisterCustomHandler(new HealWorldHandler());
```

From Lua (WorldEdit): publish an envelope through the existing sink or add a thin Lua wrapper in extension bindings.

### 5. Limit the LLM Surface

| Task | Minimum tier |
|---|---|
| Read the world only | `Read` |
| Change time scale / UI | `Read \| Gameplay` |
| Spawn / level edit | `Read \| WorldEdit` |
| Formulas / mods | `+ LogicOverride` |
| Arbitrary components | `+ Full` (dev / trusted builds only) |

Configuration: caps on `AggregatingGameLuaRuntimeBindings`, `LoadMod(..., caps)`, and the `CoreAILifetimeScope` inspector.

### 6. Host-Side Whitelists

- Prefab spawn: `CoreAiPrefabRegistryAsset`
- Load scene: `luaAllowedScenes` on `CoreAILifetimeScope`
- Full: `enableFullLuaAccess` (off by default)

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

### 8. MoonSharp: Use Native Facilities

| Task | Native | Do not reinvent |
|---|---|---|
| Sandbox modules | `CoreModules.Preset_HardSandbox` | A custom Lua parser |
| Frame coroutines | `CreateCoroutine` + `coroutine.yield()` | Busy-loop in a one-shot chunk |
| CLR callbacks | `registry.Register(name, typedDelegate)` | `GetComponent` from Lua through reflection without Full tier |
| One-shot CPU limit | `IDebugger` / `LuaExecutionGuard` | Infinite `while true` with no limit |
| Preemptive yield | `AutoYieldCounter` + drain `YieldRequest` | - |
| CLR objects in Lua (Full+) | `UserData.RegisterType<T>()` (roadmap) | Raw reflection on every call |

### 9. Logging

In CoreAiUnity, use **`IGameLogger`** / `GameLogFeature`, not `Debug.Log*` in runtime code
(exception: `UnityGameLogSink`, which is a sink).

### 10. Tests

- EditMode: `SecureLuaSandboxEditModeTests`, `LuaModRuntimeEditModeTests`, binding tests
- PlayMode: `LuaCoroutineRunnerPlayModeTests`, FastNoLlm integrations
- CI: `moonsharp` / `COREAI_NO_LUA` matrix

---

## ❌ How Not to Do It

### Security

| Anti-pattern | Why it is bad |
|---|---|
| Enable `Preset_Default` / `LoadMethods` / `IO` / `Debug` | Files, eval, introspection |
| `UserData.RegistrationPolicy.Automatic` | Any CLR type in Lua (MoonSharp docs: **never**) |
| Full mode in production multiplayer without review | Any script can touch any component |
| Trust `pcall` in Lua instead of a C# guard | `ErrorHandling` is intentionally disabled; the host must catch errors |
| Skip `luaAllowedScenes` in public chat mode | The LLM can request any scene from Build Settings |
| Weaken `StripRiskyGlobals` "for convenience" | package/load/collectgarbage are escape vectors |

### Game Architecture

| Anti-pattern | Why it is bad |
|---|---|
| Put all mechanics in Lua from day one | No C# default, harder debugging and shipping |
| `logic_define` on a slot the game did not declare | Runtime error; slots only through `DeclareSlot` |
| One 500-line `execute_lua` every frame | Rate limit, latency, LLM context; use mods |
| Store game state only in `store_set` | Strings, 64 KB cap; critical state belongs in C# |
| `GetComponent` / reflection every frame through the Full API | GC and perf; cache ids, use slots |

### MoonSharp / Perf

| Anti-pattern | Why it is bad |
|---|---|
| `DynamicInvoke` + `ToObject` on every binding call | Loses typed marshalling (old `LuaApiRegistry` anti-pattern) |
| Mix `AutoYieldCounter` and debugger step limits without understanding them | Different semantics; see MOONSHARP_NATIVE_APIS.md |
| `renderer.material.color = ...` in a tight loop | Material instances; use `MaterialPropertyBlock` |
| `string.rep(1, 1e9)` without a cap | Allocation bomb; capped in `SecureLuaEnvironment` |

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
- [ ] Custom bindings are registered with the correct `requiredCapabilities`
- [ ] Slots are declared in C# before `logic_define`
- [ ] Escape tests / EditMode sandbox tests pass
- [ ] `COREAI_NO_LUA` build is checked if Lua is optional
- [ ] Programmer prompt lists **only** real APIs

---

## Demos and Examples

| Scene | Path |
|---|---|
| Lua mods + logic slots | `Assets/CoreAI.Demos/LuaMods/` |
| World command pipeline | `Assets/CoreAI.Demos/WorldCommands/` |
| Live LLM -> mechanics | `Assets/CoreAI.Demos/LiveMechanics/` |
| Full reflection | `Assets/CoreAI.Demos/FullAccess/` |

