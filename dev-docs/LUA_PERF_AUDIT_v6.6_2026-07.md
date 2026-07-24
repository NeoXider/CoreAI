# Lua runtime performance audit — v6.6.0 (2026-07-24)

Measured in the Editor (Mono, `execute_code`), workload `local x=0 for i=1,2000000 do x=x+i end`,
checksum-verified identical across every run.

## 1. The headline correction: the Roblox comparison was measured WITHOUT the guard

`dev-docs/LUA_VM_BENCHMARK_PLAN.md` compared Luau against a **bare** `LuaState.RunAsync` — no
execution guard installed. Real mod code never runs that way: every handler runs under
`LuaCsExecutionGuard`. Measured cost of that layer:

| Path | Time | vs raw |
|---|---|---|
| Raw `RunAsync`, no hook | 244 ms | 1.00× |
| Production path, guard armed (`HookInstructionBatch = 4`) | 895 ms | **3.67×** |

**So the published gap against Luau understates reality by ~3.7×.** The VM difference (Luau's C++
register VM vs a managed C# interpreter) is real and was measured correctly, but it is not the whole
story — our own guard is the larger *controllable* factor.

## 2. Where the guard's cost actually is

The hook fires every `HookInstructionBatch = 4` VM instructions and, per fire, reads the wall clock
and the managed heap size. Measured cost of those two primitives on this machine:

| Primitive | Cost |
|---|---|
| `Stopwatch.GetTimestamp()` | 43.9 ns |
| `GC.GetTotalMemory(false)` | 13.7 ns |

Note this **contradicts the assumption written in the guard's own doc comment**, which treats the
heap read as the expensive one. The clock is ~3× more expensive.

But neither dominates. Working back from the measurement: ~609 ms of guard overhead over roughly
1 M hook fires is **~600 ns per fire** — an order of magnitude more than the work *inside* the hook.
The cost is the **hook invocation itself**: Lua-CSharp dispatches the count hook through an async
`LuaFunction` call (`LuaVirtualMachine.ExecutePerInstructionHook`), and that call boundary is the
expense.

**Consequence: the lever is the NUMBER of fires, not the work per fire.**

## 3. What was changed in v6.6.0

Sampling the clock every 64th fire instead of every fire (`ClockCheckEveryHooks = 64`). Measured:
3.67× → **3.21×**. A real but small win (~6%), which is exactly what §2 predicts — it removes 76% of
the *in-hook* work, and the in-hook work was never the bottleneck. Kept because it is free and
risk-free: the step budget is still charged on every fire, and the timeout is now enforced to within
one sampling window (~256 instructions) against budgets measured in seconds.

## 4. The big win, and why it was NOT shipped

Raising the batch was measured directly:

| `HookInstructionBatch` | Guarded time | Overhead |
|---|---|---|
| 4 (shipped) | 884 ms | 3.21× |
| 64 (experiment) | 322 ms | **1.21×** |

**A 2.65× speedup of all guarded Lua execution is available.** It was reverted rather than shipped,
because the small batch is load-bearing for the allocation-bomb backstop: `s = s .. s` doubles the
heap in ~4 instructions, so a fixed 64-instruction window permits up to ~16 doublings between heap
samples — from 1 MB that is 65 GB, i.e. OOM long before the first sample. A wide fixed batch trades
a sandbox guarantee for speed, which is not an acceptable trade to make silently.

### Proposed follow-up: adaptive batch

Re-arm the hook's count from inside the hook, sized by the remaining headroom to the allocation
budget:

```
safeDoublings = floor(log2(budget / max(allocated, floor)))
window        = clamp(ConcatIterationInstructions * safeDoublings, 4, 64)
```

This keeps the existing invariant — the heap cannot pass the budget between two samples, so peak
heap stays within ~2× budget — while letting the overwhelming majority of mods (which never approach
256 MB) run at a wide batch. Open questions to resolve before implementing:

1. Does Lua-CSharp permit `LuaState.SetHook` from **inside** a hook? (Unverified — this is the main
   feasibility risk.)
2. The step budget must be charged by the batch **actually in force**, not the const, or the step
   ceiling silently inflates by up to 16× (the `LuaCs_ModsCall_*_CannotDisarmHandlerGuard` tests use
   a 5000-step budget and would be the ones to catch this).
3. `EndGuard`'s restore path must re-arm the enclosing hook with *its* window, not the inner call's.

Required new test before shipping: a `s = s .. s` bomb started at several different live-heap sizes
must still trip below OOM, plus both disarm tests and `LuaCs_RunawayHandler_IsCutAndSurvivesOneTrip`
staying green.

## 5. Other findings (from the binding-layer and runtime audits, not yet actioned)

Ordered by expected value. None of these were measured — unlike §1–§4 they are code-reading findings.

1. **Every datatype crossing the Lua↔C# seam costs two heap allocations.** `LuaCsRbxValues.Box(object, …)`
   boxes the (struct) `RbxVector3`/`RbxCFrame`/… and then wraps that box in a `LuaCsRbxValueBox`.
   A generic `LuaCsRbxValueBox<T>` halves it and removes an unbox on every argument read. Box identity
   is already documented as non-semantic, so Lua behaviour is unchanged.
2. **The coroutine resume guard installs its hook with count 1** — every instruction, 4× the main
   guard's rate — and allocates a `Stopwatch` + `LuaFunction` + closure per resume
   (`LuaCsSecureEnvironment.ResumeWithPerResumeGuard`). Should reuse the main guard's pooled-hook and
   timestamp-ticks patterns.
3. **`LuaCsApiRegistry.CreateFunction` calls `DynamicInvoke`** (reflection) per call on the whole
   `unity_*` / `input_*` global surface, which mods poll per frame.
4. **Per-property-write string concat** in `TryWriteSpatial`: the message is built on every successful
   write and only ever consumed on failure.
5. **Per-property-read dispatch** does up to five type probes ending in a `ClassCatalog.IsA` ancestry
   walk; the proxy is cached per instance, so a class-kind bitmask computed once at wrap time removes it.
6. **Signal fire allocates** an `object[]` (`Fire(params object[])`) plus a `LuaValue[]` per handler per
   fire — ~4 allocations per connected signal per frame.
7. **`Connect`/`Once`/`Wait`/`Disconnect` build a fresh `LuaFunction` + closure on every member read**,
   though they capture nothing; they can be statics.
8. **Empty-array allocations** in `LuaCsScriptExecutionGuard.Invoke` (`new LuaValue[0]` / `new object[0]`
   instead of `Array.Empty<T>()`) on every guarded invocation.

Verified already tight, do **not** "optimize": enum wrappers (interned), `TryUnbox` and the `Read*Value`
helpers (allocation-free on success), `RbxScriptSignal.Dispatch` (reusable snapshot buffer),
`HasConnections` gating, `LuaCsModRuntime.Tick` (scratch reuse, no LINQ anywhere in `Scripting/LuaCs/`),
and the guard's pooled-hook/no-Stopwatch design.

## 6. Missing infrastructure

There is **no** microbenchmark for guard overhead in the repo — `Assets/CoreAIBenchmark/` holds only
LLM game-creation scenarios. The numbers above were produced ad hoc through the Editor. A checked-in
EditMode benchmark (fixed Lua loop, run with no hook / batch 4 / batch 64) would make this table
reproducible and would gate any future guard change.
