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

## 3. Clock sampling: shipped in v6.6.0, REVERTED in v6.8.0 — it was not risk-free

Sampling the clock every 64th fire instead of every fire (`ClockCheckEveryHooks = 64`) measured
3.67× → **3.21×**: a real but small win (~6%), exactly what §2 predicts, since it removes 76% of the
*in-hook* work and the in-hook work was never the bottleneck.

It was justified here as "free and risk-free … the timeout is enforced to within one sampling window
(~256 instructions), microseconds against budgets measured in seconds". **That claim was wrong**, and
a follow-up audit caught it: *the count hook does not fire during a host call*. It fires between VM
instructions only. So the window is 256 **instructions**, not 256 instructions' worth of *time* — and
a handler whose instructions are mostly expensive bindings can burn a whole frame budget while
reaching the sampling threshold zero times.

That is not a corner case; it is the main case. `SignalHandlerGuard` runs input handlers at
`timeoutMs = 200` every frame, and a body like `for i = 1, 50 do local p = Instance.new('Part'); p.Parent = workspace end`
is ~250–300 instructions — under one sampling window — yet costs ~250 ms of wall time. With sampling
the guard never checks the clock and the frame hangs; without it the same script is cut within 4
instructions of the deadline. The sampling defeated the timeout precisely where the timeout matters
most, so the 6% was reverted in both the main guard and the coroutine guard.

**How to get the 6% back safely:** check the deadline at the *host-call boundary* as well (the
wrapper already exists — `LuaCsRbxValues.Fn` and `LuaCsApiRegistry.CreateFunction`), which needs the
active `GuardHook` reachable from there, e.g. via a thread-static. One `GetTimestamp()` per host call
is negligible against the call itself, and it closes the hole completely — at which point batching
the in-hook clock read becomes sound.

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

## 5. Other findings (from the binding-layer and runtime audits)

Ordered by expected value. None of these were measured — unlike §1–§4 they are code-reading findings.
Items marked **[done]** were implemented in v6.8.0; the rest remain open.

1. **Every datatype crossing the Lua↔C# seam costs two heap allocations.** `LuaCsRbxValues.Box(object, …)`
   boxes the (struct) `RbxVector3`/`RbxCFrame`/… and then wraps that box in a `LuaCsRbxValueBox`.
   A generic `LuaCsRbxValueBox<T>` halves it and removes an unbox on every argument read. Box identity
   is already documented as non-semantic, so Lua behaviour is unchanged.
2. **[done]** The coroutine resume guard installed its hook with **count 1** — every instruction, 4× the
   main guard's rate — and used `Stopwatch.ElapsedMilliseconds`. Now fires every 4 instructions
   (charging 4 steps per fire, so the ceiling is unchanged) and uses the timestamp+ticks pattern with
   the same 64-fire clock sampling. Still allocates a `LuaFunction` + capture per resume; pooling it
   the way `LuaCsExecutionGuard.RentHook` does is left as a `TODO:` in the file.
3. **`LuaCsApiRegistry.CreateFunction` calls `DynamicInvoke`** (reflection) per call on the whole
   `unity_*` / `input_*` global surface, which mods poll per frame. **Open** — the largest remaining item.
4. **[done]** Per-property-write string concat in `TryWriteSpatial`: the capability description was built
   on every successful write and only ever consumed on failure. Split into `RequireWorldEditForWrite`,
   which builds it only when the check fails. Message text unchanged.
5. **Per-property-read dispatch** does up to five type probes ending in a `ClassCatalog.IsA` ancestry
   walk; the proxy is cached per instance, so a class-kind bitmask computed once at wrap time removes it.
   **Open.**
6. **Signal fire allocates an `object[]` per fire** (`Fire(params object[])`). **Open — and deliberately
   NOT solved by buffer reuse.** `Fire1`/`Fire2` backed by a per-signal reusable buffer were implemented,
   measured as correct under today's synchronous dispatch, and then reverted, because the reuse is
   incompatible with the direction this class is already committed to: the `TODO:` in
   `RbxScriptSignal.cs` replaces the `SupportsDispatch` split with the MVP2 scheduler and
   **deferred-dispatch** semantics. Deferred dispatch means the argument array outlives the `Fire` call,
   so a shared buffer would be overwritten by the next frame before the queued handler reads it —
   silently, as wrong `dt`/input values rather than an exception, and precisely when every signal (not
   just three) starts flowing through it. The saving was also small in context: the `LuaValue[]` **per
   handler** per fire is still allocated, so reuse removed one array out of N+1.

   **Requirement for the MVP2 scheduler:** argument arrays are pooled *there*, where the scheduler owns
   the lifetime and can return them after the deferred dispatch completes. That makes pooling an
   explicit, enforced lifetime instead of an unwritten "handlers must not retain" rule hanging off a
   public method.
7. **[done]** `Connect`/`Once`/`Wait`/`Disconnect` built a fresh `LuaFunction` + closure on every member
   read though they capture nothing; they are now static readonly instances.
8. **[done]** Empty-array allocations in `LuaCsScriptExecutionGuard.Invoke` — now `Array.Empty<T>()`.

Verified already tight, do **not** "optimize": enum wrappers (interned), `TryUnbox` and the `Read*Value`
helpers (allocation-free on success), `RbxScriptSignal.Dispatch` (reusable snapshot buffer),
`HasConnections` gating, `LuaCsModRuntime.Tick` (scratch reuse, no LINQ anywhere in `Scripting/LuaCs/`),
and the guard's pooled-hook/no-Stopwatch design.

## 6. Missing infrastructure

There is **no** microbenchmark for guard overhead in the repo — `Assets/CoreAIBenchmark/` holds only
LLM game-creation scenarios. The numbers above were produced ad hoc through the Editor. A checked-in
EditMode benchmark (fixed Lua loop, run with no hook / batch 4 / batch 64) would make this table
reproducible and would gate any future guard change.
