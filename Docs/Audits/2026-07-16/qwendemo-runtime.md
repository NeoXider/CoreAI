# Audit: QwenDemo runtime correctness (GenieDemo / SpellcraftDemo / QwenDemoShared)

> Date: 2026-07-16. Read-only audit of `Assets/CoreAI.Demos/QwenDemo/*` (all three files read in full; line numbers verified). Part of the 2026-07-16 audit wave; see `SUMMARY.md`.

## Confirmed problems

### 1. LLM turn requests are uncancellable — `CancellationToken.None` hardcoded (High)
- `QwenDemoShared.cs:134` — `await foreach (LlmStreamChunk ch in orchestrator.RunStreamingAsync(task, CancellationToken.None))`.
- `LlmMeter.RunAsync` is the actual model turn for every Submit/determinism path and hardcodes `None`. Both demos build a `_lifetimeCancellation` CTS (`GenieDemo.cs:53`, `SpellcraftDemo.cs:79`) and plumb it only into the readiness wait — never into the turn. `RunAsync`/`RunDeterminism` call `LlmMeter.RunAsync` with no token (`GenieDemo.cs:265`, `SpellcraftDemo.cs:492`, `:541`).
- Root cause of findings 2, 3, and 5.

### 2. Fire-and-forget Task outlives the MonoBehaviour; continuation not lifetime-bound (Medium)
- `GenieDemo.cs:257` — `_ = RunAsync(wish);`; `SpellcraftDemo.cs:452` — `_ = RunAsync(desc);`.
- `RunAsync` (`GenieDemo.cs:260` / `SpellcraftDemo.cs:536`) is discarded and its turn is uncancellable; after the await it writes `_last`/`_busy` and `Log()` with no `this == null` guard (Start guards at `GenieDemo.cs:104` / `SpellcraftDemo.cs:128`). Only managed fields are touched (no Unity API), so it fails quietly under normal exit. `destroyCancellationToken` is never used anywhere.

### 3. `RunDeterminism` is `async void` looping uncancellably over N LLM turns (Medium/High)
- `SpellcraftDemo.cs:460` — `private async void RunDeterminism(string desc, int n)`.
- Awaits up to `n` turns (line 492), each with `CancellationToken.None`; exiting play mode mid-loop cannot stop it. No `this == null` checks between awaits. `try/finally` (474/519) but no `catch` — relies on `LlmMeter` swallowing exceptions internally (`QwenDemoShared.cs:163-166`).

### 4. Tool delegates run on a worker thread and marshal to a destroyed pump after exit (Low)
- `GenieDemo.cs:158/193/221/242`, `SpellcraftDemo.cs:190/198` — `_pump.Enqueue(() => { ... StartCoroutine(...) });`.
- Because the turn can't cancel (finding 1), a delegate can run after teardown. `Enqueue` only touches an internal `ConcurrentQueue` (`QwenDemoShared.cs:417-423`) so nothing throws on a destroyed pump — work is silently dropped (Update at `:425` stops). `_pump` is never null-checked before use.

### 5. Domain-reload-disabled play-mode exit lets uncancellable tasks/tools run into edit mode (Medium)
- `QwenDemoShared.cs:134` + `GenieDemo.cs:257` + `SpellcraftDemo.cs:452,460`.
- With Reload Domain disabled, managed tasks are not torn down on exit, so the uncancellable turn continues in edit mode; its continuation and worker-thread delegates keep enqueuing onto a dead pump and writing fields on a destroyed component. This is the live version of the "exits play mode mid-request" hazard. Note: no mutable static state exists, so the `[RuntimeInitializeOnLoadMethod]`-reset concern does NOT apply here.

### 6. `_lifetimeCancellation` disposed but never observed by turn paths (Low)
- `GenieDemo.cs:120-124`, `SpellcraftDemo.cs:144-148` (Cancel+Dispose in OnDestroy). Correct for readiness, but `RunAsync`/`RunDeterminism` don't observe the token — a false sense of lifetime safety.

## Suggested fix

Wire `_lifetimeCancellation.Token` through `LlmMeter.RunAsync` (and observe it between determinism iterations). This single change closes findings 1, 2, 3, and 5.

## What is done well

- Off-thread Unity API discipline: tool delegates run on the MEAI worker thread; every GameObject/Transform/coroutine touch is marshaled to the main thread via `MainThreadPump` (`QwenDemoShared.cs:409-439`), documented and exception-guarded (433-436). No `ConfigureAwait(false)`/`Task.Run` anywhere, so streaming continuations stay on Unity's sync context.
- Readiness path is lifetime-safe: `Start` is `async void` but wraps the readiness await in `try/catch(OperationCanceledException)` and re-checks `this == null` (`GenieDemo.cs:100-107`, `SpellcraftDemo.cs:124-131`); the CTS is cancelled and disposed in OnDestroy.
- Thread-safe shared state: charges/mana/decision are guarded by locks (`_gate`/`_decGate`) across main-thread Update/OnGUI and worker-thread tools; `QwenToolTurnGuard` (`QwenDemoShared.cs:261-303`) cleanly prevents duplicate per-turn side effects. No mutable static state, so domain-reload-disabled re-entry starts clean.
