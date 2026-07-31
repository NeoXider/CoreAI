# Script-engine abstraction seam (MVP0)

All interpreter access in `Assets/CoreAIMods/Runtime` goes through engine-neutral contracts so the VM
(currently Lua-CSharp v0.5.6) can be swapped by reimplementing one folder. The regression tripwire is
`ScriptingSeamHonestyEditModeTests`: no `using Lua` may appear in runtime source outside
`Runtime/Scripting`.

## Contracts — `CoreAI.Scripting` (`Runtime/Scripting/`)

| Contract | Role |
|---|---|
| `IScriptEngine` | Factory/facade: states, registries, guards, coroutines, chunk execution. |
| `IScriptState` | Opaque handle to one sandboxed environment (one mod = one state). |
| `IValueMarshaller` | Single CLR↔script conversion authority (scalars, tables, portable deep copy, kinds). |
| `IScriptFunctionRegistry` | Host callback registration: typed `Delegate` + var-args (`ScriptCallContext` → `ScriptCallResult`). |
| `IScriptTable` | Host-projected read view over a table argument. |
| `IScriptCoroutine` | Budgeted host-driven coroutine (`Resume` → `ScriptResumeResult`). |
| `IExecutionBudget` / `ExecutionBudget` | Steps / wall-clock / GC-allocation caps (best-effort enforcement). |
| `IScriptExecutionGuard` | Runs callables under a budget; raw script values in/out. |
| `ScriptRuntimeException`, `ScriptExecutionErrors.IsMemoryBudgetTrip` | Seam-level errors; type-based memory-trip classification (`IScriptMemoryBudgetTrip` marker). |

## Adapter layer — `Runtime/Scripting/LuaCs/`

| Adapter | Wraps / implements |
|---|---|
| `LuaCsScriptEngine` | `IScriptEngine` over `LuaCsSecureEnvironment` (owns all `LuaState` creation). |
| `LuaCsScriptState` | `IScriptState` over `LuaState`. |
| `LuaCsValueMarshaller` | `IValueMarshaller`; consolidates the former registry/runtime/logic-slots conversions. |
| `LuaCsApiRegistry` | `IScriptFunctionRegistry` (kept name; Lua-typed `RegisterCallback` overloads remain the engine-specific escape hatch). |
| `LuaCsScriptTable` / `LuaCsScriptCallContext` | `IScriptTable` / `ScriptCallContext` over `LuaTable` / `LuaFunctionExecutionContext`. |
| `LuaCsScriptExecutionGuard` | `IScriptExecutionGuard` over `LuaCsExecutionGuard` (per-instruction hook). |
| `LuaCsScriptCoroutine` | `IScriptCoroutine` over `LuaCsCoroutineHandle`. |
| `LuaCsSecureEnvironment`, `LuaCsExecutionGuard`, `LuaCsCoroutineHandle`, `LuaCsCoroutineRunner` | Concrete sandbox/hardening (moved from `Runtime/Sandbox`, namespaces unchanged). |
| `LuaCsFullUnityRuntimeBindings.Marshalling.cs` | VM-specific partial of the Full-tier reflection binder (Unity math/color table coercions). |

## Rules

- Composition root: `LuaCsModRuntimeFactory` creates the one `LuaCsScriptEngine`; nothing else touches
  `LuaState` directly.
- Consumers (`LuaCsModRuntime`, `LuaCsLogicSlots`, `LuaCsGameToolExecutor`, `LuaCsAiEnvelopeProcessor`,
  all gameplay binders) depend only on `CoreAI.Scripting`; binder registration signatures take
  `IScriptFunctionRegistry`.
- `ILuaCsGameRuntimeBindings` (concrete `LuaCsApiRegistry` parameter) is the frozen compatibility shape
  for external scene/demo bindings — `LuaCsApiRegistry` implements the seam, so those impls keep working.
- Mod-facing Lua behavior is contract: any adapter change must keep the existing EditMode suite green
  and the marshaller truth-table tests (`ScriptEngineSeamEditModeTests`) byte-compatible.
- A second engine = new folder beside `LuaCs/` implementing the contracts + a factory switch; nothing
  above the seam changes.
