# MoonSharp / Lua: Native Capabilities vs Our Implementation

> Audit 2026-06-12. Sources: [moonsharp.org/sandbox](https://www.moonsharp.org/sandbox.html), [coroutines](https://www.moonsharp.org/coroutines.html), [objects/UserData](https://www.moonsharp.org/objects.html), [hardwire](https://www.moonsharp.org/hardwire.html).

Goal: avoid duplicating what MoonSharp and Lua already provide out of the box, and use recommended APIs where they are safe.

## What Is Already Done Correctly

| Area | CoreAI solution | Why it is native / justified |
|---|---|---|
| **Sandbox modules** | `CoreModules.Preset_HardSandbox \| Coroutine` | Official MoonSharp preset: string/math/table/bit32 without io/os/load/debug |
| **StripRiskyGlobals** | Manual `Nil` for load/require/package/collectgarbage, `string.rep` cap | The preset does not fully remove package and collectgarbage; `string.rep` is one VM instruction, so the step limiter cannot react in time: **a custom cap is required** |
| **One-shot limits** | `InstructionLimitDebugger` + `IDebugger.GetAction` | MoonSharp has no built-in hard step limit; the debugger API is the documented approach |
| **Frame coroutines** | `Script.CreateCoroutine` + `coroutine.yield()` in Lua | Standard Lua/MoonSharp pattern; `LuaCoroutineRunner` only ticks Resume |
| **Kill coroutine** | `Coroutine.AutoYieldCounter = 1` + `_disposed` | MoonSharp has no ForceKill; AutoYieldCounter is the recommended mechanism (see docs) |
| **YieldRequest** | Drain loop in `LuaCoroutineHandle.Resume` | Required for preemptive yield (AutoYieldCounter) |
| **API registration** | `globals[name] = clrDelegate` in `LuaApiRegistry` | MoonSharp marshals typed `Func`/`Action` without DynamicInvoke |
| **Logic slots / mod hooks** | C# `InvokeGuarded` + `LuaExecutionGuard` | Errors are host-side; `pcall` in Lua is intentionally not enabled (see below) |
| **Optional module** | `#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA` | Build without MoonSharp: stub/null in DI |

## What Is Intentionally Not Enabled from MoonSharp

| Module / API | Reason |
|---|---|
| `ErrorHandling` (pcall/xpcall) | SoftSandbox; scripts can swallow errors and bypass C# fail-open; the host catches errors |
| `Metatables` | Complicates escapes through `__index`/`__newindex`; world APIs are explicit functions |
| `LoadMethods`, `IO`, `OS_System`, `Debug` | Files, processes, eval, introspection |
| `Dynamic`, `Json` (MoonSharp) | Extra surface; JSON goes through the C# envelope |
| `Preset_SoftSandbox` / `Preset_Default` | Too broad for untrusted AI scripts |

## Where Custom Implementation Is Justified

| Custom code | MoonSharp alternative | Verdict |
|---|---|---|
| `LuaModRuntime` hooks_on / hooks_every / store | None in MoonSharp | **Keep**: game mod runtime |
| `LuaLogicSlots` | None | **Keep**: "slot + C# default" contract |
| World command envelopes | None | **Keep**: main-thread execution + validation |
| `InstructionLimitDebugger` on every Resume | `AutoYieldCounter` | **Keep debugger** for hard failure after N steps without yield; AutoYieldCounter gives a cooperative slice, not a throw |
| `CoreAiFullUnityLuaRuntimeBindings` (reflection) | `UserData.RegisterType<T>()` | **Planned migration**: see below |

## Full Mode: UserData Instead of Reflection (Planned)

Current Full tier (`unity_find`, `unity_get_member`, ...) is a custom reflection wrapper.

**MoonSharp recommendation** for CLR interop:

```csharp
UserData.RegistrationPolicy = InteropRegistrationPolicy.Manual; // never Automatic
UserData.RegisterType<Transform>(InteropAccessMode.LazyOptimized);
UserData.RegisterType<Rigidbody>(...);

// In Lua:
local go = unity_find("Player")  -- UserData.Create(go)
go.transform.position = Vector3(1, 2, 3)  -- if Transform is registered
```

Pros: typed marshalling, `[MoonSharpUserData]`, `[MoonSharpHide]`, hardwire for IL2CPP, no `MethodInfo.Invoke` on the hot path.

Cons: every type must be registered explicitly (or generated); type blacklist is a separate policy (see `LUA_ACCESS_MODES_AUDIT.md` Planned).

**Intermediate step (current):** reflection API with Type/Member caching. It works for opt-in Full, but it is not idiomatic MoonSharp.

## Performance (Brief)

1. **IDebugger on every instruction** (`IsPauseRequested` + `StepIn`) is expensive for hot coroutines. Alternative for time-slicing: only `AutoYieldCounter` without debugger (not a hard limit). Current choice: exact hard limit is more important than FPS for AI scripts.
2. **`LuaApiRegistry`** after the audit: direct delegate assignment to globals (without a DynamicInvoke wrapper).
3. **`set_color` through `renderer.material`** is a Unity API issue, not MoonSharp; for frequent calls, use `MaterialPropertyBlock` (see PERF review).

## Lua 5.x vs MoonSharp

- MoonSharp is a Lua 5.2-like dialect, not 100% LuaJIT/Lua 5.4.
- `bit32` is available (`Preset_HardSandbox`); verify the `#` operator / `goto` against the package version in the project.
- Standard `coroutine.*` is available when `CoreModules.Coroutine` is enabled.

## Checklist for New Bindings

1. Prefer a **typed delegate** (`Func<...>`, `Action<...>`) in `LuaApiRegistry.Register`.
2. For CLR objects, use **`UserData.RegisterType`** + `[MoonSharpHide]` on dangerous members, not reflection (Full tier is a temporary exception).
3. Do not add CoreModules without review, especially Metatables, ErrorHandling, or LoadMethods.
4. Long-lived scripts should use **`Script.CreateCoroutine` + yield**, not a busy-loop in a one-shot chunk.
5. Tail-call from a C# callback into Lua only through `DynValue.NewTailCallReq` (rare; see MoonSharp coroutine caveats).

Detailed **✅/❌** guidance: [LUA_BEST_PRACTICES.md](LUA_BEST_PRACTICES.md).

## Related Documents

- [LUA_SANDBOX_SECURITY.md](LUA_SANDBOX_SECURITY.md) - security boundaries
- [LUA_GAME_API.md](LUA_GAME_API.md) - game API for scripts
- [LUA_BEST_PRACTICES.md](LUA_BEST_PRACTICES.md) - best practices and anti-patterns
- `LUA_ACCESS_MODES_AUDIT.md` - access modes (Read -> Full), planned blacklist

