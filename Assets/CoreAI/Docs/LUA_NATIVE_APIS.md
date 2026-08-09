# Lua-CSharp: Native Capabilities vs Our Implementation

> Runtime: [Lua-CSharp](https://github.com/nuskey8/Lua-CSharp) — a managed, AOT-safe Lua VM that
> runs under IL2CPP and WebGL. CoreAI targets the **Lua 5.2** language level: a Luau → Lua 5.2
> downlevel preprocessor (`LuauSourceGate`, default-on) runs before every compile so mods may write
> Roblox-style Luau syntax. It ships **bundled** as `Lua.dll` + `Lua.Annotations.dll` inside the
> CoreAI Mods package at `Assets/CoreAIMods/Plugins/`. There is no external Lua package to install.

Goal: expose host functionality to secured Lua scripts through the registry rather than opening the
whole CLR, and reuse what the VM already provides where it is safe.

## What Is Already Done Correctly

| Area | CoreAI solution | Why it is native / justified |
|---|---|---|
| **Secured environment** | `LuaCsSecureEnvironment` builds a curated global table (string/math/table subset, no io/os/load/debug) | Lua-CSharp lets the host own `LuaState.Environment`; we only add what is safe |
| **One-shot limits** | `LuaCsExecutionGuard` (instruction / time budget) | Hard step/time cap for untrusted AI scripts; yields back to Unity |
| **Frame coroutines** | `coroutine.yield()` in Lua + `LuaCsCoroutineHandle` ticking Resume | Standard Lua coroutine pattern; the handle only drives Resume per frame |
| **API registration** | `LuaCsApiRegistry.Register(name, delegate)` / `RegisterCallback` | Typed host delegates marshalled to `LuaFunction`; no CLR surface leaks |
| **Logic slots / mod hooks** | C# guarded invoke + `LuaCsExecutionGuard` | Errors surface host-side; scripts cannot silently swallow failures |
| **Optional module** | `#if COREAI_LUA` | Define to enable Lua; default build keeps stub/null DI surfaces |

## What Is Intentionally Not Exposed

| Capability | Reason |
|---|---|
| `io`, `os`, `debug`, `package`, `require`, `load`/`loadstring` | Files, processes, eval, introspection, module loading — all removed from the secured environment |
| Arbitrary metatables on host tables | Complicates escapes through `__index`/`__newindex`; world APIs are explicit functions |
| Raw CLR object access | Only registered host callbacks are visible; the Full tier (opt-in) is the sole reflection path |

## Registering a Native API

The native binding concept is `LuaCsApiRegistry`:

```csharp
var registry = new LuaCsApiRegistry();

// 1. Typed delegate — arguments are coerced to the delegate's parameter types,
//    the return value is marshalled back to a Lua value automatically.
registry.Register("forge_spawn",
    (string kind, double x, double y) => forge.Spawn(kind, (float)x, (float)y));

// 2. Custom callback — when you need full control over Lua arguments/returns.
registry.RegisterCallback("forge_count", (ctx, ct) =>
{
    string team = ctx.GetArgument<string>(0);
    return new ValueTask<int>(ctx.Return(forge.Count(team)));
});

// Applied to the secured LuaState when a script runs:
registry.ApplyToEnvironment(state); // writes each name into state.Environment
```

```lua
-- In Lua (5.2; Luau syntax is downleveled automatically):
forge_spawn("knight", -5, 0)
local n = forge_count("enemy")
```

Argument coercion (`LuaCsApiRegistry.CoerceArgument`) handles `string`, `bool`, `double`/`float`,
`int`/`long`, enums, `LuaTable`, and `LuaValue`; return values (numbers, strings, bools, dictionaries,
`IEnumerable`) are converted back to Lua values. Host exceptions become `LuaRuntimeException` prefixed
with the API name.

## Full Mode: Opt-in Reflection Tier

The Full tier (`unity_find`, `unity_get_member`, …) in `LuaCsFullUnityRuntimeBindings` is a curated
reflection wrapper (public members by default) gated behind explicit access modes and a
type/member denial policy (`IFullLuaAccessBlacklistPolicy`, see `LUA_ACCESS_MODES.md`). It stays
disabled on WebGL. Prefer registering explicit typed APIs over the Full tier for anything shipped.

## Lua Language Level (Lua-CSharp)

- CoreAI targets **Lua 5.2**: the bundled VM parses Lua 5.2 source. Luau-only constructs — type
  annotations, compound assignments (`+=`), `continue`, string interpolation, if-expressions, floor
  division `//`, Luau-only number literals — are rewritten to Lua 5.2 equivalents by the
  `LuauDownleveler` preprocessor before compilation (default-on; plain Lua 5.2 passes through
  byte-identically), so mod authors can write Roblox-style Luau.
- A curated subset of the standard library is exposed (`string`, `math`, `table`, `coroutine`, …);
  `io`/`os`/`debug`/`package` are withheld from the secured environment.
- Standard `coroutine.*` is available; long-lived scripts should `coroutine.yield()` across frames
  rather than busy-loop inside a one-shot chunk.

## Checklist for New Bindings

1. Prefer a **typed delegate** in `LuaCsApiRegistry.Register`; use `RegisterCallback` only when you
   need custom argument/return handling.
2. Keep host callbacks small and validating — treat every Lua argument as untrusted.
3. Do not widen the secured environment (no `io`/`os`/`debug`/`load`/`require`) without review.
4. Long-lived scripts should use `coroutine.yield()`, not a busy-loop in a one-shot chunk.
5. Reach for the Full reflection tier only behind an explicit opt-in access mode.

Detailed **✅/❌** guidance: [LUA_BEST_PRACTICES.md](LUA_BEST_PRACTICES.md).

## Related Documents

- [LUA_SANDBOX_SECURITY.md](LUA_SANDBOX_SECURITY.md) - security boundaries
- [LUA_GAME_API.md](LUA_GAME_API.md) - game API for scripts
- [LUA_BEST_PRACTICES.md](LUA_BEST_PRACTICES.md) - best practices and anti-patterns
- `LUA_ACCESS_MODES.md` - access modes (Read -> Full), blacklist policy
