# The heap budget fails because every signal fire spawns a thread

`dev-docs/SCALE_CHARACTERIZATION.md` records `heapSlopeMegabytesPerMinuteMax: 1` and measures 4.36
MB/min at 20 actors, 19.5 at 200 — `FAIL(heap)` at every step of the staircase, with no network
involved. TODO `alloc-fix` has been open against that. This is where the bytes come from, measured
rather than reasoned.

## What was measured

Harness: `tools/ScaleHarness`, `--only N --repeats 1` on the host CoreCLR (diagnostic runs, not
evidence — `frozenWorkloadHonoured = false`). `GC.GetAllocatedBytesForCurrentThread` is exact there.

**1. Almost all of it is one phase.** The harness already recorded `AllocSignalsBytesPerFrame`
(Heartbeat → InputProcessing) and simply never printed it:

| | median per frame |
|---|---:|
| All phases | 105,600 B |
| Signals phase alone | 105,208 B |

**99.6%.** Everything else together is under 400 B.

**2. It is not the Lua body.** A diagnostic workload identical to the frozen one except
`heartbeatLoopIterations: 32 → 1` (`tools/ScaleHarness/diag-work1.workload.json`) cut the Lua work to
a third and moved the allocation by nothing at all:

| Workload | guarded steps/frame | alloc/frame |
|---|---:|---:|
| Frozen, `WORK=32` | 1886 | 103.1 KB |
| Diagnostic, `WORK=1` | 646 | 103.1 KB |

Byte-identical. Whatever allocates does not care how much Lua runs.

**3. It is a fixed cost per handler invocation.**

| Actors | alloc/frame | per actor |
|---:|---:|---:|
| 20 | 103.1 KB | 5.16 KB |
| 50 | 263.6 KB | 5.27 KB |

Linear, flat per actor. One Heartbeat handler per actor per frame → **~5.2 KB per signal handler
invocation.**

## The mechanism

`ModScheduler.SpawnSignal` (`ModScheduler.cs:446`) calls `CreateRecord` and resumes it. Every signal
fire therefore allocates a whole new `ThreadRecord` and its Lua thread:

```
RbxScriptSignal.FireCore
  → ModScheduler.EnqueueSignalInvocation
  → (drain) RbxScriptConnection.InvokePending
  → BuildSignalHandler's wrapper            (LuaCsRbxDatatypeBindings.cs:1014)
  → LuaCsRbxApiBindings.SpawnSignalHandler  (:650)
  → ModScheduler.SpawnSignal                (:446)  ← new ThreadRecord + Lua thread, EVERY fire
```

Twenty actors × one Heartbeat × 60 frames per second is 1,200 thread creations a second, for handlers
that in this workload never yield.

## Two wrong guesses, recorded so they are not made again

Both looked obviously right from reading the code and are worth about 1% between them:

- `RbxScriptSignal.FireCore` does `_connections.ToArray()` on **every** fire (`RbxScriptSignal.cs:241`)
  — the textbook per-fire garbage. Twenty actors with one connection each is ≈800 B/frame.
- `ModScheduler.BuildSignalChain` (`:907`) allocates a `string[]` per enqueue for a cascade diagnostic
  that is read only in the rare overflow case — ≈640 B/frame.

Fixing either would have produced a confident, measurable, useless change. The bisect is what
separated them from the real cost.

## What the fix has to preserve

Spawning is not gratuitous: Roblox runs each signal handler on its own coroutine so the handler may
yield (`task.wait`, `signal:Wait()`, a `RemoteFunction` round trip). The semantics must not change.

The available saving is that **the overwhelming majority of handlers return without ever yielding**. A
handler that completes synchronously could return its thread to a pool instead of leaving it to the
GC; one that yields keeps its thread exactly as today. That is a `ThreadRecord`/Lua-thread pool with a
"was it still suspended when the resume returned?" test — the same shape as the existing pooled
`GuardHook` in `LuaCsExecutionGuard`, which already solved this problem for the guard.

Risks to respect in that work: a pooled thread must be fully reset (locals, tombstone scope, owner
mod, cancellation) or state leaks between mods, which is a security boundary and not merely a bug; and
the emergency thread cap (`EmergencyMaxThreads`) counts records, so pooling must not let a runaway mod
hide behind reused records.

## Before / after gate

The number to move is `heapSlopeMegabytesPerMinute` at the frozen workload: **budget 1, currently 4.36
at 20 actors** (`SCALE_CHARACTERIZATION.md`; the single-repeat diagnostic runs above read 12, being
noisier and shorter). Re-measure with the frozen workload and full repeats, not with the diagnostic
file, and re-run the staircase in a Standalone player before any capacity claim — the host CoreCLR is
not the target runtime.

## Fix (2026-09-05): signal handlers run on pooled runner threads

### What the research settled before any edit

| Question | Answer | Load-bearing? |
|---|---|---|
| What does Roblox guarantee about the coroutine a handler runs on? | Nothing about identity. The `task` docs say `task.spawn/defer/delay` return "the scheduled thread" and `task.cancel` "cancels a thread and closes it"; a signal handler's thread is never returned to Lua at all. The Roblox ecosystem's reference signal implementation (stravant's GoodSignal) keeps "the currently idle thread to run the next handler on" and only builds a new coroutine when a handler yields, i.e. `coroutine.running()` inside handlers repeats across fires until one yields. Luau ships `lua_resetthread` for exactly this reuse. | Yes: this is what makes reuse an allowed observable behaviour (`coroutine.running()` identity repeats; `coroutine.status` of a finished handler's thread reads `suspended`, not `dead`). If Roblox guaranteed a fresh thread per handler this would be a semantic change. |
| What does Lua-CSharp 0.5.6 allocate per coroutine, and can a thread be reset? | Measured on the shipped `Lua.dll`: `CreateCoroutine` + resume-to-completion = **1,112 B** (`LuaState` + `CoroutineCore` + async plumbing), because the VM already recycles the 7,112 B `ThreadCoreData` (256-slot `LuaStack` + call stack) through a static 64-entry `LinkedPool` when a coroutine finishes or errors (`CoroutineCore.ResumeAsyncCore` calls `Thread.Dispose()`). There is **no reset API**: `CoroutineCore.Function`/`isFirstCall`/`status` are internal and `Dispose()` throws while a call stack is non-empty. The only supported way to run several handlers on one VM thread is a Lua closure that loops `run(); yield()`. | Yes: it rules out "reset a dead thread" and dictates the trampoline. Had the VM exposed a reset, wrapper/record reuse alone would have been the fix. |
| Can the trampoline be C# code? | No. `CoroutineCore.YieldAsyncCore` throws "attempt to yield across a C#-call boundary" unless the yield's immediate caller is a `LuaClosure`. A C#-rooted trampoline died silently on its first yield (the probe reported 0 B/iter only because every later resume took the allocation-free "cannot resume dead coroutine" path). | Yes: the body is `local run, yield = ... return function() while true do run() yield() end end`, compiled once per mod state; `run` is a C# function. |
| "Only allocate a thread on first yield"? | Not possible in this VM: `YieldAsync` on a non-coroutine state throws, so a handler cannot start on the main state and be moved into a thread later. The runner pool reaches the same steady state (one parked thread per mod, zero per fire). | No. |
| What do comparable runtimes do? | Luau/Roblox: engine thread pool + `lua_resetthread` (resets even a yielded or errored thread); Lua 5.4: `lua_resetthread`/`lua_closethread`; community Roblox signals: free thread + yielder loop; C# async: state machines box only on first suspension. | No. |

Sources: `create.roblox.com/docs/reference/engine/libraries/task` and `.../coroutine` (read through the `Roblox/creator-docs` YAML), `devforum.roblox.com/t/thread-reuse-how-it-works-why-it-works/1999166`, stravant's GoodSignal gist, `luau-lang/luau` `VM/src/lstate.cpp`, `nuskey8/Lua-CSharp` v0.5.6 `LuaState.cs`, `Internal/CoroutineCore.cs`, `Internal/Pool.cs`, `Runtime/LuaStack.cs`.

### Where the 5.2 KB actually was (measured layer by layer on the host)

| Layer | B per handler |
|---|---:|
| VM coroutine, create + resume to completion (`ThreadCoreData` recycled by the VM) | 1,112 |
| `LuaCsCoroutineHandle` on top (own `LuaStack(8)`, `CancellationTokenSource`, per-resume hook closure + `Stopwatch`) | 1,632 |
| `IScriptCoroutine` seam (result boxing) | 1,752 |
| `LuaCsRbxScriptThreadFactory.Create` + `Resume` (`task.scheduled` bound closure, delegate, argument copies) | 2,256 |
| `ModScheduler.SpawnSignal` (record + argument copy) | 2,360 |
| plus an envelope-shaped resume (label string + `Guid`) | 2,568 |
| full real path in-process, one Heartbeat handler per mod, N=20, host-owned mods | 2,887 |
| the same with actor-attributed mods (harness shape) | +176 |

The rest of the harness's 5.2 KB/actor is remote traffic, `Instance.new`/`Destroy` churn and the persistent wait loops, amortised per frame; the thread chain was the single dominant term.

### The change

- `LuaCsRbxSignalRunner` (new): one parked Lua coroutine per mod state whose body is the loop above. `run()` executes the armed handler through `CallAsync` and flags completion; the loop parks at `yield()`. A handler that yields (`task.wait`, `signal:Wait`, `RemoteFunction`) suspends inside `run` exactly as on a dedicated thread and its wrapper stays a live scheduler thread until it returns. A handler that throws kills the coroutine in protected mode (the VM recycles its stack), so an errored runner is never reused. A mod that captured `coroutine.running()` and resumes the parked runner later gets a no-op (`run()` finds nothing armed and the loop parks again); `coroutine.close` does not exist in this Lua 5.2 VM.
- `LuaCsRbxScriptThreadFactory`: rents/recycles runners from a per-`LuaState` pool (`ConditionalWeakTable`, so a torn-down mod's runners die with its state; every mod load creates a fresh state) fixed to one mod id, at most 8 idle per state. Only callables captured by `Connect`/`Once` (`LuaCsRbxSchedulerCallable.Recyclable`) use runners; `task.spawn/defer/delay` threads are handed to Lua as `thread` values and stay dedicated.
- `LuaCsRbxScriptThread`: a **fresh wrapper per fire**; only the runner is reused. Every C#-side identity (the scheduler's record key, the mod's tracked-thread sets, RemoteFunction wait registrations) belongs to the wrapper, so no stale reference can alias a runner's next tenant. When the handler returns the wrapper detaches from the runner (a dead wrapper's `Kill`/status can never reach the runner again), reports `IsDead`, and hands the runner back — also after a handler that yielded and later returned, which is why yielding handlers got cheaper too.
- `LuaCsCoroutineHandle`: the per-resume budget hook is one reusable object per handle (the `LuaCsExecutionGuard.GuardHook` shape) instead of a fresh `LuaFunction` + closure + `Stopwatch` per resume; this helps every `task.wait` loop tick as well.
- `ModScheduler`: `ThreadRecord`s are pooled when the thread died inside the resume that started it (`Spawn`/`SpawnSignal`, state still `Running`, no completion or deferred entry) — the only ending in which no heap, queue or completion entry can reference the record. Yielded, faulted and killed records keep their GC lifetime. Pool bounded at 64.
- `LuaCsRbxApiBindings`: the per-resume envelope label is interned per mod instead of concatenated per resume.

### Per-field policy of a reused `ThreadRecord`

| Field | On rent (`Reset`) | On release (`Clear`) | Why |
|---|---|---|---|
| `Thread` | the new tenant's thread | `null` | the record key; a pooled record must not keep a dead thread alive |
| `OwnerModId` | the new owner | `null` | security boundary: quota attribution and `KillOwnedBy` read it |
| `State` | `Idle` | `Idle` | a record is released only while `Running`; a tenant never inherits `Waiting`/`Deferred`/… |
| `DeferredArguments` | `null` | `null` | release refuses a record that still has any |
| `CompletionWait` | `null` | `null` | release refuses a record that still has one |
| `ReadableTombstone` | `null`, then set by `SpawnSignal` from the current drain | `null` | the destroyed-instance read scope must never outlive its fire |
| `SignalWaitGeneration` | **kept, keeps counting** | kept | timeout entries match `(record, generation)`; a monotonic counter can never reproduce a value an earlier tenant used, so no stale entry can resume a later tenant |

The wrapper (`LuaCsRbxScriptThread`) is not reused at all. The runner's per-handler state is `pendingCallable`/`pendingArguments` (cleared before the handler runs and again after it returns), `IterationCompleted` (reset on arm) and the lifetime step budget (reset on rent); its `OwnerState` is fixed at construction and re-checked on every rent, and the mod id of its pool is fixed by the first rent.

### How the thread cap still bites

`EmergencyMaxThreads` and the per-actor quota count `_records`, and only live records are in `_records`: a pooled record left it when its thread died, and an idle runner has no record at all. A mod whose handlers yield holds one live record per parked handler exactly as before, so 4,096 of them trip the emergency ceiling on the next spawn (`EmergencyMaxThreads_StillTripsForAModHoldingManyLiveSignalHandlerThreads`); 4,106 handlers that finished leave zero live records and one pooled record (`PooledRecords_NeverCountTowardTheEmergencyCap`). Idle runners are bounded (8 per mod state) and die with the mod's state.

### Measured (host CoreCLR; `--only N`, diagnostic runs, not evidence)

| run | alloc/frame (median) | signals phase | heap slope MB/min | heap over the window | GCs gen0/1/2 | gate |
|---|---:|---:|---:|---|---|---|
| N=20 before, 1 repeat | 105,612 B | 105,208 B | 11.94 | 7.35 → 7.05 MB | 9/0/0 | FAIL(heap) |
| N=20 after, 1 repeat | 52,488 B | 52,096 B | −3.96 | 9.28 → 8.94 MB | 5/1/0 | **PASS 4 ms / 16 ms** |
| N=20 after, 3 repeats | 52,488 B | — | −3.08 / 5.33 / 5.27 | 12.32 → 11.68 MB (reps 2–3) | 5/0/0 | FAIL(heap) on the worst repeat |
| N=50 before, 1 repeat | 269,920 B | 269,304 B | 4.41 | 15.55 → 14.94 MB | 23/3/1 | FAIL(heap) |
| N=50 after, 1 repeat | 135,120 B | 134,472 B | 13.82 | 16.34 → 22.13 MB | 13/2/1 | FAIL(heap) |
| N=50 after, 3 repeats | 135,120 B | — | 13.69 / 10.08 / 10.25 | 24.4 → 28.4 MB (reps 2–3) | 12/1/0 | FAIL(heap) |

Reference: the frozen 3-repeat baseline in `SCALE_CHARACTERIZATION.md` read 4.36 (N=20) and 3.44 (N=50) for the same code as "before" above, which read 11.94 and 4.41 one run later — the slope metric varies ~3× between runs of identical code.

In-process through the real bindings (one Heartbeat handler per mod, N=20): 2,887 → **847 B per handler per frame**; with a `task.wait` inside every handler 3,167 → 1,055 B. Guarded steps per frame rose from 1,886 to 2,016 at N=20 (6.5 instructions of runner loop per fire). Frame medians moved 0.538 → 0.453 ms (N=20) and 1.144 → 1.037 ms (N=50).

The allocation rate, which is deterministic, halved at both N and is now under the 1 MB/min budget's own arithmetic at N=20 only if nothing else survives gen0 — and something does: at N=50 the after-window's heap climbs ~4 MB per 10 s across 12 gen0 collections and comes back down between repeats, i.e. medium-lived garbage promoted to gen1/gen2 and only reclaimed by the next gen2 (the before-run's window happened to end right after its gen2, 15.55 → 14.94 MB). The 300 chat requests per N=50 window (records, orchestrator continuations, provider tasks) are the obvious candidate; the thread churn this fix removed was gen0 garbage and never showed up there. The remaining 2.6 KB/actor/frame at N=20 is spread over remote traffic, instance churn, the wait loops and the per-resume mutation envelope (`ApplyServerGeneratedMutation` builds a `Guid` string, a `MutationEnvelope` and a scope per resume; `ResolveActorContext` builds a `LocalActorIdentityProvider` + `Guid` per resume for actor-attributed mods, +176 B measured): the next items, none of them thread churn.

### Tests

EditMode, executed on the host through a reflection runner over Unity's `nunit.framework.dll` because the editor lock was held: `ModSchedulerRecordPoolEditModeTests` (6) and `RbxSignalHandlerThreadReuseEditModeTests` (4) are new; `ModSchedulerEditModeTests` (26), `RbxRunServiceLuaBindingsEditModeTests`, `RbxTaskSchedulerLuaBindingsEditModeTests` (34) and `RbxSignalConnectionTeardownEditModeTests` are unchanged. Signal-order and deferred-drain coverage that still passes unweakened: `SignalDrain_QuotaSaturatedFirstSubscriber_StillInvokesLaterSubscriber`, `R4_8_TaskDeferDoesNotRunBeforeCurrentDrainFinishes`, `R4_8_TaskDeferRunsAfterTheCurrentResumptionPoint`, `R4_8_TaskDeferDrainsAfterEveryScriptResumePoint`, `DEV3_DeferredFaultDoesNotOrphanDifferentModSibling`, `Lua_R5_5_SignalsDrainAtEveryLiveFrameResumptionPoint`, `Lua_R5_4_DeferredMutationDoesNotReenterAndNewSignalUsesNextGeneration`, `Lua_R5_6_DeferredReentrancyCapIs10AndReportsChain`, `R5_4_SignalDrainRunsAfterDelayedResumptionAndBeforeHeartbeat`, `Lua_SignalHandlerFault_ReportsOwningModAndRunsQueuedSiblings`. `Lua_SignalHandler_TaskWaitUsesOwningSchedulerThread` fails on the host before and after the change alike (CoreCLR prints `0.35 − 0.1` as `0.24999999999999997`, Unity's Mono as `0.25`); tests that build a `GameObject` need the editor.
