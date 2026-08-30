# MVP2 scheduler core plan

## Scope and invariants

`ModScheduler` is deterministic Domain code in `CoreAI.RbxApi.Instances`. It owns logical scheduling only: no Unity, Lua VM, UniTask, wall clock, or blocking work. Every thread record has a non-empty owner mod ID. One main-thread driver call to `Advance(deltaSeconds)` advances one logical frame.

## Phase pipeline

The scheduler stores the full pipeline as one ordered `PipelineStage[]`; `Advance` only interprets that table. Delayed work is therefore structurally fixed between `PostSimulation` and `Heartbeat`.

| Order | Core stage | Observable phase/work |
|---:|---|---|
| 1 | deferred drain | Work queued before the frame runs to its first yield. The drain uses a snapshot; work deferred by that work waits for the next drain. |
| 2 | phase | `PreAnimation(delta)` |
| 3 | deferred drain | End of the `PreAnimation` resume point. |
| 4 | phase | `PreSimulation(delta)` |
| 5 | deferred drain | End of the `PreSimulation` resume point. |
| 6 | phase | `PostSimulation(delta)` |
| 7 | deferred drain | End of the `PostSimulation` resume point. |
| 8 | delayed resumption | Snapshot of eligible `task.wait` and `task.delay` threads, globally ordered by deadline then insertion sequence. |
| 9 | deferred drain | End of the delayed-thread resume point. |
| 10 | phase | `Heartbeat(delta)` |
| 11 | deferred drain | End of the `Heartbeat` resume point. |
| 12 | phase | `PreRender(delta)` |
| 13 | deferred drain | End of the `PreRender` resume point. |

Target host mapping remains roadmap §5.2.3:

| Unity frame event | Roblox phase boundary | Adapter responsibility (follow-up) |
|---|---|---|
| `FixedUpdate` (0..n) | `PreAnimation`, legacy `Stepped`, then `PreSimulation` before physics | Capture fixed-step timing and translate the scheduler phase notification. |
| `Update` | `PostSimulation`, delayed resumption, then `Heartbeat` | Supply scaled frame delta and preserve delayed-before-Heartbeat ordering. |
| `LateUpdate` | `PreRender`, legacy `RenderStepped` | Emit only for rendering/client topology. |

This step implements the logical order through one `Advance` call. Splitting notifications across Unity callbacks, aliases, topology gating, and the existing `RbxRunService` pump is deliberately deferred.

## Data structures

- A binary min-heap for waits and a separate binary min-heap for delays. Heap keys are `(deadline, earliestFrame, sequence)`, giving deterministic FIFO ties and `O(log n)` insertion/removal of the next due item instead of scanning every timer every frame.
- Wait entries retain schedule time so resumption receives actual scaled elapsed seconds. Delay entries retain the original argument array.
- Timer eligibility is stage-relative. Work scheduled before the active frame's delayed slot uses the current frame as `earliestFrame`; work scheduled at or after that slot, or between frames, uses the next frame. Deadline checks still govern positive durations.
- The delayed slot first snapshots all eligible heap entries, then resumes that fixed batch. A timer created by a resumed timer cannot re-enter the active batch.
- A FIFO deferred queue is drained by snapshot count. A nested `Defer` cannot re-enter the current drain and becomes eligible at the next pipeline drain.
- A reference-keyed thread-record dictionary is the ownership ledger and single source of scheduling state. Canceled/killed records are removed from queues, heaps, and completion waits.

## Ports and ownership

| Port/type | Responsibility | Implementation owner |
|---|---|---|
| `IRbxScriptThread` | Status, resume-to-next-yield, permanent kill | Later LuaCs adapter over `IScriptCoroutine`. Test fake now. |
| `IRbxScriptThreadFactory` | Convert an opaque callable into a thread for a required owner mod ID | Later LuaCs adapter. Test fake now. |
| `IRbxTimeSource` | Monotonic scaled scheduler time advanced only from injected frame deltas | Engine-free accumulating implementation now; future host may supply an equivalent source. Test fake now. |
| `RbxSchedulerCompletion` | Nonblocking terminal token with resume arguments or a structured fault | Host adapters complete it from callbacks/async operations in later rungs. |

The scheduler records ownership independently of the VM handle. `KillOwnedBy(modId)` removes and kills only matching records. A `BUDGET_EXCEEDED` resume result kills that thread and emits `ThreadFaulted(modId, RbxError)`; the existing consecutive-error/quarantine policy can count that event and call `KillOwnedBy` without affecting other mods.

## Cancellation and failure model

- `Cancel` accepts only a live thread owned by this scheduler. Null, foreign, running, or dead threads raise `BAD_ARGUMENT`; canceling a waiting/deferred/delayed/completion-waiting thread removes all pending work and kills it.
- `KillOwnedBy` is the teardown/quarantine operation and returns the number killed. Unknown but valid mod IDs return zero, matching idempotent teardown behavior.
- Resume failures are `RbxError` values. The scheduler kills and unregisters the failed thread, then emits `ThreadFaulted`. With no fault subscriber it rethrows, so a failure is never silently lost. With a subscriber, the frame continues and other mods still run.
- Completion cancellation kills its waiting caller. Completion faults use the same structured thread-fault path.

## Deliberately deferred

- LuaCs adapter and all `task.*`/legacy global bindings.
- Unity callback wiring and changes to the current `RbxRunService` pump.
- Clock APIs, wall clocks, `timeScale`, `DateTime`, server time, and budget wall clocks.
- General deferred signal dispatch and removal of the `SupportsDispatch` split.
- `signal:Wait`, `WaitForChild`, lifecycle-signal dispatch, and loopback remotes.
- Budget accounting policy and quarantine thresholds; this core provides per-thread fault attribution and per-mod kill hooks only.

## Deviations from roadmap §5.2.2

| Sketch | Implemented core | Justification |
|---|---|---|
| Scheduler under a new RobloxApi scheduling assembly/path | Existing `CoreAI.RbxApi.Instances/Scheduling` folder and assembly | Explicit task architecture; preserves inward-only dependencies and the existing fitness contract. |
| `IScriptCoroutine` in public signatures | `IRbxScriptThread` plus `IRbxScriptThreadFactory` | `IScriptCoroutine` belongs to `CoreAI.Mods`; referencing it would invert the assembly dependency. |
| `Spawn/Defer/Delay(callable, args)` | Adds required `ownerModId` | Ownership cannot be inferred safely in Domain code and is required for targeted budget kill/quarantine. |
| `ScheduleWait` returns `double` synchronously | Returns `void`; actual elapsed is passed to the thread's later `Resume(elapsed)` | A scheduling call cannot synchronously return a value produced on a future frame. The Lua adapter will expose the resumed value as the Lua call result. |
| `ScheduleWaitUntil(..., Task)` | `ScheduleWaitUntil(..., RbxSchedulerCompletion)` | `Task` is banned in this engine-free Domain assembly; the completion token also works on WebGL without threads or blocking. |
| `RunPhase(phase, delta)` | `Advance(delta)` executes the entire canonical pipeline once | Explicit task requirement; one stage table prevents callers from reordering or omitting phases. |
| `ThreadFaulted(string, string)` | `ThreadFaulted(string, RbxError)` | Keeps stable error code, fix hint, and future mod/script context intact instead of flattening the error contract. |
