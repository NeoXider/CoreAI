# CoreAI Runtime Perf Audit — Iteration 2

> **Status (v6.3.4): HIGH-1 FIXED** — the guard `Execute` closure/delegate allocation is gone
> (`BeginGuard`/`EndGuard`, inlined VM calls). HIGH-2, MEDIUM-1, MEDIUM-2, HIGH-3 (seam boxing) and
> LOW-1 remain OPEN — deferred until a Play Mode GC capture (~5 timer mods) confirms the next target,
> per the recommended order.

READ-ONLY steady-state performance audit (no code changed, no git, no Play Mode).

**Baseline / scope.** v6.3.3 already made the instruction-guard hot path zero-allocation:
`LuaCsExecutionGuard` rents a pooled `GuardHook`, samples every 4 instructions, and uses a raw
`Stopwatch.GetTimestamp()` long instead of a per-call `Stopwatch`. That work is **done and not
re-audited**. This pass hunts for the *next* biggest per-frame / per-call cost with several mods
running 20 Hz timers.

**Steady-state model used for frequency estimates.** "Several mods at 20 Hz" = ~5 mods × 20 timer
fires/s = ~100 guarded timer calls/s, plus event dispatch. Each call crosses the Lua↔C# seam once.
The input pump (`RbxUserInputService.Step`) runs once per Unity frame (~60 Hz) regardless of mods.

---

## Headline conclusion

The v6.3.3 work removed the *guard-internal* per-call allocations, but the **call-marshalling
boundary around the guard still allocates on every single guarded call.** The seam is typed in
`System.Object`, so every `LuaValue` (a struct) boxes when it crosses, and every call builds fresh
argument/result arrays plus a capture closure. This is the clear next-biggest steady-state cost and
sits directly on the 20 Hz timer / event path. Categories 3 (LLM) and 4 (world apply) have far
smaller steady-state impact; the Rbx binding layer (category 2) is already clean.

Ranked findings below.

---

## HIGH-1 — Per-call closure + delegate allocation in the guard's `Execute`

**File:** `Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsExecutionGuard.cs:166-168`
(and the chunk overload at `:144-145`)

```csharp
return ExecuteGuarded(state,
    ct => state.CallAsync(new LuaValue(function), args.AsSpan(), ct).GetAwaiter().GetResult(),
    cancellationToken);
```

**What allocates.** The lambda `ct => ...` captures three locals (`state`, `function`, `args`), so
the C# compiler emits a **display-class instance + a `Func<CancellationToken,LuaValue[]>` delegate
per call**. `ExecuteGuarded` then invokes it once. The GuardHook pooling from v6.3.3 removed the
hook/Stopwatch/closure allocations *inside* `ExecuteGuarded`, but the `body` closure passed *into*
it was left allocating — it is the same pattern the pooling work was trying to kill, one frame up
the stack.

**How often.** Every guarded call: every timer fire, every event handler invocation, every
`mods_call`. ~100+ allocations/s (2 objects each) in the model above, entirely on the single-threaded
WebGL Boehm GC this codebase repeatedly calls out as the constraint.

**Fix proposal.** Eliminate the capture. Since `ExecuteGuarded` is private and only ever runs
`state.CallAsync(...)` or `state.ExecuteAsync(...)`, hoist those two shapes into `ExecuteGuarded`
itself (pass `state`, `function`/`closure`, `args` as explicit parameters and branch on which is
non-null), or store them on the pooled `GuardHook` and give the hook a cached, non-capturing
delegate. Either removes both per-call allocations. (Note: `state.CallAsync(...).GetAwaiter().
GetResult()` still allocates inside Lua-CSharp's async machinery — that is library-internal and out
of scope, but removing the closure is free and independent.)

**Cost/benefit.** ~2 heap objects/call removed for a small, self-contained refactor of one private
method. High benefit, low risk (private surface, behaviour identical). **Do this first.**

---

## HIGH-2 — Argument/result array churn + `LuaValue` boxing per call at the guard adapter

**File:** `Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsScriptExecutionGuard.cs:41-61`

```csharp
LuaValue[] luaArgs = new LuaValue[args.Length];              // alloc #1  (even length 0)
for (...) luaArgs[i] = LuaCsValueMarshaller.Unbox(args[i]);
LuaValue[] results = _inner.Execute(lua, function, ct, luaArgs);
object[] boxed = new object[results.Length];                 // alloc #2  (even length 0)
for (...) boxed[i] = LuaCsValueMarshaller.Box(results[i]);   // boxes each struct result
```

**What allocates, per call:**
1. `new LuaValue[args.Length]` — a fresh array every call. For a 0-arg timer this is still
   `new LuaValue[0]` (a real heap alloc — `new T[0]` is **not** interned; only `Array.Empty<T>()` is).
2. `new object[results.Length]` — same, plus for length 0 a wasted heap alloc.
3. `LuaCsValueMarshaller.Box(results[i])` (`LuaCsValueMarshaller.cs:94-97`) returns the `LuaValue`
   struct as `object` → **one box per returned value**. A handler returning nothing avoids the box
   but still pays the two arrays.

**How often.** Every guarded call (same population as HIGH-1). A typical timer returns 0 values →
2 zero-length arrays/call; a handler returning a value → 2 arrays + N boxes.

**Fix proposal.**
- Guard the zero-length cases with `Array.Empty<LuaValue>()` / `Array.Empty<object>()`.
- Reuse a thread-local scratch `LuaValue[]` for `luaArgs` sized to the call (the args are consumed
  synchronously before return, so a per-thread reusable buffer is safe — same reasoning the v6.3.3
  hook pool used).
- The result boxing is forced by the `object[]` return type of `IScriptExecutionGuard.Invoke`. For
  the timer/event path the runtime discards the result entirely (`InvokeGuarded` ignores the return
  value — `LuaCsModRuntime.cs:1054`), so a non-boxing overload (`InvokeVoid` that skips building
  `boxed[]`) would remove alloc #2 and every result box on the hottest path.

**Cost/benefit.** Removing the discarded-result array+boxing on the timer/event path is pure win
(the value is thrown away today). Reusing the arg buffer is medium effort. High cumulative benefit.

---

## HIGH-3 (architectural) — `System.Object` seam boxes every `LuaValue` that crosses

**Files:** `LuaCsValueMarshaller.cs:94-97` (`Box`), `LuaCsScriptCallContext.cs:37-40,71-74`
(`GetArgument`, `GetKind`), call sites in `LuaCsModRuntime.RegisterModApis`
(`mods_export` arg read `:1389`, `mods_call` arg reads `:1445`, `mods_get`/`mods_call` marshalling
`:1419,:1464`).

**What allocates.** `LuaValue` is a struct; the whole scripting seam (`IValueMarshaller`,
`ScriptCallContext`, `IScriptExecutionGuard`) is typed in `object`. So `Box`, `GetArgument`,
`ToScriptValue`, `ToScriptArgument` each **box the struct on the heap** every time a value crosses.
On the per-call path this compounds HIGH-2: args box on the way in (via the runtime's `object[] args`),
results box on the way out. `mods_call`/`mods_get` additionally round-trip through
`ToPortable`→`FromPortable` (`LuaCsValueMarshaller.cs:318-378`), which builds
`List<KeyValuePair<object,object>>` trees and boxes every scalar — acceptable for cross-mod copies
(rare, bounded depth 4), but note it is the heaviest single marshal.

**How often.** Every arg and every result of every guarded call, plus every `call.GetArgument`
inside a binding. This is the dominant *cumulative* boxing source, but removing it means changing the
seam's generic contract — much larger than HIGH-1/HIGH-2.

**Fix proposal.** Longer-term: make the seam generic over the VM value type (or add `LuaValue`-typed
fast-path overloads on `IScriptExecutionGuard`/`ScriptCallContext` that the Lua-CSharp adapters
implement without boxing, keeping the `object` methods as the slow fallback). Short-term, HIGH-1 and
HIGH-2 already remove the bulk of the *avoidable* per-call boxing without touching the contract.

**Cost/benefit.** Highest cumulative allocation reduction but highest effort/risk (public seam
redesign). Recommend deferring until HIGH-1/HIGH-2 are measured; they may bring the path close enough
to target that the seam rewrite is not worth the churn.

---

## MEDIUM-1 — `handlers.ToArray()` snapshot per dispatched event

**File:** `Assets/CoreAIMods/Runtime/LuaExecution/LuaCsModRuntime.cs:1019-1021`

```csharp
handlerSnapshot = mod.Handlers.TryGetValue(evt.Key, out List<object> handlers)
    ? handlers.ToArray()          // heap alloc every dispatched event
    : Array.Empty<object>();
```

**What allocates.** A fresh `object[]` copy of the handler list **per dequeued event**, taken under
`_gate`. The snapshot exists to survive a handler calling `hooks_on()` mid-dispatch (re-entrant
mutation). For an event-driven mod (e.g. one `events_emit` per tick fanned to N mods) this is one
array alloc per (mod, event) pair per tick.

**How often.** Once per event actually dispatched. Zero for pure-timer mods; scales with event
traffic. Bounded by `DefaultMaxEventsDispatchedPerTickGlobal = 256`/tick worst case.

**Fix proposal.** Mirror the exact pattern `RbxScriptSignal.Dispatch` already uses
(`RbxScriptSignal.cs:121-165`): keep a reusable scratch buffer per mod (or per runtime) and a
`_firing` re-entrancy flag; copy into the shared buffer in the common non-nested case, fall back to a
fresh copy only for the rare nested dispatch. Alternatively a per-handler-list version counter to
skip the copy when no mutation occurred since last dispatch.

**Cost/benefit.** Removes one alloc per event on the dispatch path for modest effort; the re-entrant
buffer pattern is already proven in this same codebase.

---

## MEDIUM-2 — `params object[]` allocation for event handler args

**File:** `Assets/CoreAIMods/Runtime/LuaExecution/LuaCsModRuntime.cs:1037` (call site),
`:1045` (signature)

```csharp
InvokeGuarded(mod, fn, evt.Key, evt.Value);   // -> new object[2] per event handler invocation
```

`InvokeGuarded(Mod, object, params object[])` — the two-arg event call allocates a `new object[2]`
at the call site every invocation. (The timer call `InvokeGuarded(mod, timer.Fn)` at `:989` compiles
to `Array.Empty<object>()` — no alloc — so only the event path is affected.) `evt.Key`/`evt.Value`
are already strings, so no additional boxing there.

**How often.** Once per event handler invocation (same population as MEDIUM-1's inner loop).

**Fix proposal.** Add a non-`params` overload `InvokeGuarded(Mod, object, string, string)` for the
event path (and keep the `params` one for timers/general use), or reuse a per-mod 2-slot scratch
array. Combined with HIGH-2's arg-buffer reuse this removes the last per-event array on the C# side.

**Cost/benefit.** Small, localised; removes one alloc per event. Do alongside MEDIUM-1.

---

## MEDIUM-3 — `JsonUtility.ToJson` + envelope/command alloc per `coreai_world_*` command

**File:** `Assets/CoreAIMods/Runtime/WorldBindings/LuaCsWorldRuntimeBindings.cs:591-599`
(inside `Publish`)

```csharp
string json = JsonUtility.ToJson(env, false);     // string alloc per command
ApplyAiGameCommand command = new() { ... };        // class alloc per command
```

**What allocates.** Each world command builds a `CoreAiWorldCommandEnvelope` (via the static
factories, e.g. `CoreAiWorldCommandEnvelope.Spawn/Change`), serializes it to a JSON string via
`JsonUtility.ToJson`, and wraps it in an `ApplyAiGameCommand`. All three are heap allocations per
command.

**How often.** NOT strictly per-frame — only when a mod actually calls a `coreai_world_*` API. But a
timer mod that animates the world by calling `coreai_world_change` every tick hits this at its timer
rate (up to 20 Hz), and `coreai_world_grid`/`coreai_world_spawn_batch` build an intermediate
`List<CoreAiWorldCommandEnvelope>` (`:258,:320`) plus one JSON string *per cell* (up to
`MaxBatchSize = 100`).

**Important mitigating context.** The **preferred per-frame movement path is the Rbx
`IPartPropertySink`** (`InstanceGameObjectBinder.SetCFrame/SetPosition`, category 2), which is
**already allocation-free** (struct `PartProperties`, cached components, reused
`MaterialPropertyBlock`). The `coreai_world_*` string-command channel is the agent/LLM-authored
command path, not the intended hot animation path. So this is a real cost only for mods that misuse
the world-command channel for per-frame animation.

**Fix proposal.** Low priority given the zero-alloc alternative exists. If per-frame world commands
must be cheap, consider a binary/struct command envelope for the in-process sink path (bypassing the
JSON string, which exists for cross-boundary/serialized transport) — but only if profiling shows mods
actually drive animation this way. Otherwise document that per-frame animation should use the Rbx
part API, not `coreai_world_change`.

**Cost/benefit.** Medium benefit only in the misuse case; the clean path already exists. Treat as
"guardrail / documentation" rather than a code fix unless profiling flags it.

---

## LOW-1 — LLM streaming: O(n²) full-buffer rescans + per-chunk `Substring` churn

**File:** `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/MeaiLlmClient.cs`
(`CompleteStreamingAsync` and helpers)

**What allocates / costs.** On many chunks the loop calls `iterationVisible.ToString()` /
`rawIterationText.ToString()` on the *entire accumulated buffer* and then `full.Substring(scanStart)`
(e.g. `:736, :962, :1017, :1025, :1430`). Because the buffer grows with every streamed token, this is
**O(n) work per chunk → O(n²) over a full response**, each pass allocating a fresh string of the whole
visible text so far. `SplitForLiveUiStreaming` (`:1725-1747`) additionally yields one `Substring` per
UI sub-chunk, and tool-call detection re-runs `JsonConvert.DeserializeObject<Dictionary<...>>`
per candidate (`:1903, :2166`).

**How often.** Per streamed chunk during an LLM turn — i.e. LLM-token timescale (tens of ms), NOT
per Unity frame. Per the task, this category is explicitly lower priority than the per-frame paths,
and correctly so: even the quadratic scan is small in absolute terms next to network/token latency.

**Fix proposal.** Track a running `scanStart` offset and scan the *delta* since the last chunk
instead of re-`ToString()`-ing the whole buffer; keep the incremental parser state across chunks
rather than re-parsing the full accumulated text. Cache the tool-call JSON probe result. Only worth
doing if long streamed responses show up in a GC profile.

**Cost/benefit.** Real but off the per-frame critical path; low steady-state priority. Defer behind
all HIGH/MEDIUM items.

---

## Categories that are already clean (no meaningful steady-state cost found)

- **Rbx binding layer (category 2) — clean.** `InstanceGameObjectBinder`
  (`RbxApi/Binding/InstanceGameObjectBinder.cs`) is well-optimised: `PartProperties` is a struct
  passed by `in`; renderer/collider are cached at build (`CacheVisualComponents`) so per-frame
  transform/appearance writes skip `GetComponent` scans; the `MaterialPropertyBlock` is reused off
  the entry (`:602`); primitive meshes/material are static-cached (`EnsurePrimitiveCache`). No
  per-frame LINQ, no `GameObject.Find`, no per-frame boxing.
- **`RobloxSpace` conversions — clean.** `RbxApi/Unity/RobloxSpace.cs` is pure struct math
  (`Vector3`/`Quaternion` value types), no allocation.
- **`InstanceId` / dictionary keys — clean.** `InstanceId` is a `readonly struct` implementing
  `IEquatable<InstanceId>` and `GetHashCode` (`RbxApi/Instances/InstanceId.cs:13,35-39`), so the
  `Dictionary<InstanceId,...>` lookups in the binder do not box.
- **`InstanceRegistry` — no per-frame path.** Its `foreach` loops (`:251, :329`) are in tree/query
  operations, not a per-frame `Step`/`Update`.
- **Input pump (`RbxUserInputService.Step`) — already tuned.** `RbxApi/Instances/RbxUserInputService.cs`
  reuses `_pollBuffer`/`_currentKeys`/`_previousKeys` (cleared, not reallocated), gates every
  `InputObject` construction behind `signal.HasConnections`, and interns the mouse-button type-name
  strings + the `gameProcessedEvent` box (`:28-30`). `UnityNewInputSource.CollectPressedKeyCodes`
  iterates a static value-tuple `KeyMap` array with no per-frame allocation. The only residual is the
  `List.Contains` membership test over held keys in `StepKeys` (`:156,:167`), which is O(held-keys)
  and negligible (a handful of keys); not worth changing.
- **`RbxScriptSignal.Dispatch` — already uses the reusable-buffer + re-entrancy-flag pattern**
  (`:121-165`) that MEDIUM-1 recommends copying into the mod event dispatcher.

---

## Recommended order of work

1. **HIGH-1** (remove the `body` closure in `LuaCsExecutionGuard.Execute`) — smallest change, private
   surface, ~2 objects/call gone. Do first, measure.
2. **HIGH-2** (zero-length `Array.Empty`, reusable arg buffer, and a void/non-boxing invoke overload
   for the discarded-result timer/event path).
3. **MEDIUM-1 + MEDIUM-2** together (reusable handler snapshot buffer + non-`params` event overload).
4. Re-profile. Only if still short of target, consider **HIGH-3** (seam de-boxing — large, public
   contract change).
5. **MEDIUM-3** is a documentation/guardrail item (the zero-alloc Rbx path already exists);
   **LOW-1** (LLM streaming) is off the per-frame path and lowest priority.

All estimates are read from the code (cited `new[]`/closure/`ToArray`/`Box`/`ToString`/`Substring`
constructs), not from a live profiler; the ordering assumes the "several mods at 20 Hz" steady state.
A GC allocation profile in Play Mode with 5 timer mods would confirm the per-call object count and is
the natural next step before committing to HIGH-3.
