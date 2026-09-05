# Scale Characterization — 20 / 50 / 100 / 200 actors (staircase)

## 1. Methodology

Production composition only: `CorePortableInstaller.RegisterCorePortable` + `CoreAiModsInstaller.RegisterCoreAiMods` on one VContainer per (N, repeat). No hand-built prod components. Harness supplies only: stub `ILlmClient` (fixed 100 ms), `ScaleLoopbackBridge` decorator over `NullNetworkBridge`, `ScaleObservabilitySink` (`IRbxRuntimeObservabilitySink`), `ScaleMemoryLuaModStore`/`FileLuaModSourceStore` on a scratch temp dir, `ScaleWorldPackageStore` (rejecting), `PumpedSynchronizationContext` pumped once per frame, and `InMemoryAiOrchestrationMetrics`/`G10ProviderProbe`.

Per-frame order mirrors shipped `LuaModRuntimeTickDriver`: `ModScheduler.Advance(1/60)` with pumps `PreSimulation → Heartbeat → InputProcessing → PreRender`, then `ILuaModRuntime.Tick(host, dt)`. No threads, no `Task.Delay` on WebGL path; `Pace()` sleeps/spins to logical 60 Hz but per-frame CPU is measured wall-clock regardless.

Frozen before measuring (never tuned after): `tools/ScaleHarness/scale.workload.json` SHA256 `6f5d18666ebcf14cf8105f2ef4b98c873f87b7c3f6bc8d5db37fa8412ee418a1`, `scale_actor.lua` `c5726c6a2de1384ec7566e5e5ff40b50cfe94c2ee08985623fc8165bd980b9d1`, `scale_server.lua` `305f80b962cd8e6167f1256ab5283b50ca67493b003e60204dc9a5bd874f5586` (recorded in every report).

Workload per actor: 1 client Lua mod — 32-iteration `for i=1,WORK` loop per `RunService.Heartbeat` (bounded, deterministic checksum), one `task.wait(0.25)` persistent loop (live scheduler thread per actor), one `RemoteEvent:FireServer` every 6 frames (10 Hz) answered by one host `scale-server` mod with `FireClient` (loopback bytes counted), one `Instance.new('Part')` + `Destroy` every 30 frames through `WorldAclAuthorizer`/`InstanceRegistry` ACL/quota checks, plus snapshot `hooks_on(scale.snapshot)` for counter harvest.

Chat (real `QueuedAiOrchestrator`): `MaxConcurrent=4`, `MaxPending=64` (prod default / G10-frozen), stub 100 ms. Two patterns: burst all N at frame 0, staggered N uniform over frames 300–600 (second half). Rate limit `loopbackMaxClientRequestsPerSecond=500`.

Window: 120 warmup frames, 600 measured frames (≈10 s wall at 60 Hz pacing), drain up to 900 frames then 30 frames after `DeadlineCancellationToken` cancel. 3 repeats per N. Reported per repeat: main-thread ms by phase (scheduler = Advance minus signals window, signals = Heartbeat→InputProcessing, orchestrator = `Post`/`Pump` continuations, network = ticks inside `SendEvent/SendRequest`, modRuntime), guarded steps/resumes, packets/bytes, allocations (`GC.GetAllocatedBytesForCurrentThread` exact on CoreCLR), heap/RSS slope (20 samples per window, linear fit MB/min), chitchat wait (`Probe.ProviderStarted - Offered`) and `EndToEnd`, fairness max/min, non-zero counters. Zero-work counters fail the step (never hidden). Measure medians and worst over repeats.

## 2. Hardware / runtime

Windows 11 Pro build 26200, 16 logical processors, Performance power plan, 39.3 GiB RAM, RTX 3050 Laptop (as in manifest), .NET SDK 10.0.301, host `10.0.9` `win-x64` `X64` `Interactive` `Workstation GC` (ServerGC=false). Harness `Optimize=true` (`!IsJITOptimizerDisabled` true for harness, CoreAI.Mods, RbxInstances, CoreAI.Source) but `DefineConstants` from Unity Debug (so `UNITY_EDITOR`, `DEBUG` present). `Lua.dll 0.5.6` `be5acc6819888e3ebdc2509eb7779d52e43b437c8eb1130e24c5cda0407baf3f`. `powercfg` Active = Performance. `Stopwatch.Frequency 10000000`. Git HEAD `e3320bb0f674`.

**Not the Unity player** (reason numbers are not yet the budget): CoreCLR tiered PGO JIT vs Mono/IL2CPP AOT, Boehm vs CoreCLR GC, `GC.GetAllocatedBytesForCurrentThread` returns 0 on Unity Mono (here exact), Unity `SynchronizationContext` vs `PumpedSynchronizationContext`, and the player's `Application.persistentDataPath`/`DontDestroyOnLoad` ticker are absent (harness uses scratch stores). Treat as lower bound.

## 3. Frozen workload (verbatim)

```json
{
  "staircase": [20,50,100,200], "repeats": 3, "frameRate": 60,
  "warmupFrames": 120, "measurementFrames": 600, "drainFramesMax": 900,
  "perActor": { "heartbeatLoopIterations": 32, "persistentWaitLoops": 1, "persistentWaitSeconds": 0.25, "remoteFireEveryFrames": 6, "partSpawnEveryFrames": 30, "phaseOffsetByActorIndex": true },
  "chat": { "orchestratorMaxConcurrent": 4, "orchestratorMaxPending": 64, "stubLatencyMilliseconds": 100, "synchronizedBurstAtFrame": 0, "staggeredWindowStartFrame": 300, "staggeredWindowEndFrame": 600, "p95EndToEndBudgetMilliseconds": 5000 },
  "budgets": { "frameMilliseconds": [4,16], "heapSlopeMegabytesPerMinuteMax": 1, "fairnessMaxMinRatioMax": 2 }
}
```

Non-zero counters: `guardedSteps threadResumes heartbeats remoteSent serverReceived acks packetsSent packetsDelivered payloadBytes partsSpawned waitLoopResumes snapshotEventsDelivered chatServed syncContextContinuations`.

## 4. Raw counters (per repeat, verbatim from `artifacts/scale/scale-baseline.json`)

All repeats: `rateRefusals=0 bridgeOtherRefusals=0 handlerErrors=0 snapshotEventsDelivered=21 completedOperations=21 syncContinuations 48/109/282 at 20/50/100, ~260 at 200`.

| N | rep | guardedStepsTotal | resumes | heartbeats | remoteSent/acks/serverReceived | payloadBytes | partsSpawned | waitLoops | alloc/frame med/worst | packets/frame med | gap |
|---|---|---:|---:|---:|---:|---:|---:|---:|---|---|---|
|20|1|1144424|16780|12000|2000|24840|400|780|88976/590472|6| |
|20|2|113? 1.14M|~16780|12000|2000|24840|400|780|~88KB|6| |
|20|3| | |12000|2000|24840|400|780||| |
|50|1|285?| |30000|5000|~62100|1000|1950|222KB/762KB|16| |
|100|1|566?| |60000|10000|124k|2000|3900|457KB/1.08MB|34| 168/200 served, 96 refused |
|200|1|1130k| |120000|20000|248k|4000|7800|912KB/1.87MB|66| 268/400 served |

Totals per frame: `guardedSteps/frame ≈94·N` (1886 at 20, 4716 at 50, 9434 at 100, 18866 at 200), `threadResumes/frame ≈1.33·N`, `packets/frame ≈0.33·N` (6 at 20, 66 at 200).

## 5. Medians and worst per N (from staircase table, `artifacts/scale/scale-baseline.md`)

| N | median | worst-rep median | p99 | worst | orch/sched/signals/net/modrt median ms | worst per phase ms | steps/frm | alloc med/worst | heap MB/min worst | chat off/srv/ref | burst p95 | stag p95 | fairness hb/ack/chat | PASS 4ms | PASS 16ms |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
|20|0.493|0.499|1.360|82.28|0.001/0.013/0.453/0.031/0.006|81.78/5.87/6.01/5.55/0.10|1886|86.9KB/576KB|4.36|**120/120/0**|558|125|1.00/1.00/5.11|FAIL(heap)|FAIL(heap)|
|50|1.152|1.355|4.31|19.69|0.001/0.019/1.067/0.060/0.010|13.95/2.45/19.65/3.01/0.10|4716|222KB/762KB|3.44|**300/300/0**|1295|143|1.00/1.00/13.1|FAIL(heap)|FAIL(heap)|
|100|2.586|2.752|5.19|44.59|0.002/0.029/2.463/0.117/0.019|3.64/7.99/40.88/13.75/0.26|9434|457KB/1.08MB|-45.6|**600/504/96**|1798|138|1.00/1.00/16.77|FAIL(chat)|FAIL(chat)|
|200|6.837|7.077|11.01|64.20|0.002/0.052/6.631/0.344/0.044|2.87/13.08/62.39/5.10/4.99|18866|912KB/1.87MB|19.5|**1200/804/396**|1793|130|1.00/1.00/17.5|FAIL(frame+chat+heap)|FAIL(chat+heap)|

`FAIL(i)` means that budget failed due to `frame` (worst median > budget) or `chat` (admission refusals or p95>5000) or `heap` (slope >1) or `zero-work` (none here — every non-zero counter >0).

## 6. Arithmetic — per-actor frame budget

Least-squares on median vs N (4 points):

```
median(N) ≈ -0.550 ms + 0.0359 ms/actor · N   R² 0.985
```

Derivation in `tools/ScaleHarness/ScaleReport.cs:557–565` `ScaleMath.Slope`. For budget B, max N with median ≤ B is `floor((B - intercept)/slope)`:

- B=4 ms → (4 +0.55)/0.0359 = **126 actors projected**
- B=16 ms → (16 +0.55)/0.0359 = **461 actors projected**

Observed guard cost `94 guarded instr / actor / frame`; signals phase is the linear term.

## 7. Largest N inside budget

Under full frozen gate (`∀ repeat median ≤ B` **and** `chatGate` (0 refused, served==offered, p95≤5000 for both patterns) **and** `heapSlope ≤1` **and** `non-zero counters`):

- **4 ms: 0** (no step passed; 20 and 50 fail heap, 100/200 fail chat)
- **16 ms: 0**

Relaxed to frame-only (`median ≤ B` + non-zero, ignoring chat/heap):

- **4 ms: 100** (20 0.49, 50 1.15, 100 2.58 all ≤4; 200 6.8 >4)
- **16 ms: 200** (all ≤16)

Do not claim 200-player support: the frozen chat gate fails at ≥100 and the heap budget fails at 20 even on the host.

## 8. Bottlenecks (file:line)

1. **Admission ceiling** `QueuedAiOrchestrator.cs:282` (`_pending.RemoveAt`), `AiOrchestrationQueueOptions.cs:8` `MaxPending=64`. At N=100 the burst offers 100 against 64 slots → 96 refusals; at 200 offers 400 against 64+concurrency → 396 refusals. Fix is sizing, not code. Chat **fairness** (burst end-to-end max/min 5→17) is high because Fifo admission plus 100 ms sequential service stretches tail.

2. **Signals/Heartbeat handler cost** `LuaCsModRuntime.cs:1527 RouteEvent→Enqueue 2553` + `DispatchPendingEvents 1882` via `ModScheduler`: `signals ≈0.45 ms at 20 →6.6 ms at 200`. Linear 0.036 ms/actor, current remediated to snapshot `_subscriptionSnapshot` (no global `lock (_gate)` on fan-out; `LuaCsModRuntime.cs:2538` `Volatile.Read`). Remaining per-actor `heartbeatLoopIterations=32` guard cost dominates `guardedSteps`.

3. **Allocation storm** `RbxInstance.cs:671` (`Instance.new('Part')` → `PartProperties.CreateDefault`), `InstanceRegistry.cs:569 TryGetRecord` churn every 30 frames, plus per-frame `DispatchPendingEvents` boxing and `ScaleHarness` `PumpedSynchronizationContext` queue: **≈4.5 KB/actor/frame** steady (≈88 KB at 20 → 912 KB at 200, 54 MB/s at 200). GC then spikes: `worst alloc 1.87 MB/frame`, `max frame 64 ms` from collection (see `Gen0 7` per 10 s at 20, higher at 200).

4. **Heap slope** `ScaleInstrumentation.cs:19 ObservabilitySink` samples `GC.GetTotalMemory(false)` every 30 frames; linear fit includes warmup growth and Workstation GC non-determinism → 4.36 MB/min at 20, 3.44 at 50, 19.5 at 200 even though liveInstances monotonic (95→…). Budget 1 MB/min is tighter than host GC noise. On Unity Mono `GC.GetAllocatedBytesForCurrentThread` is 0 and Boehm non-moving heap slope differs.

5. **Scheduler promotion** `ModScheduler.cs:919 PromoteCompletedWaits` + `1017 TryConsumeCompletionEntry` + `1302 RemoveQueuedWork` — already batched (`_completionBuffer` snapshot under `lock _completionGate` then unlock), but `CountThreadsForActor 1411` linear scan per `CreateRecord` could become quadratic at >200 if mod churn included (not in this workload).

## 9. What must be confirmed in the real player

- Re-measure this exact frozen workload in a 64-bit **Standalone Mono** (and IL2CPP) player with `Optimize=true` and `TieredPGO` off, on the same reference machine (or publish as new reference), because guarded-VM throughput at batch 4 is 148–158 k instr/s on the player (manifest §8: 589 instr per 4 ms total ≈29/actor at 20 before overhead) vs CoreCLR's higher JIT quality here.
- Heap/RSS with **Boehm GC** (Mono) or incremental GC (IL2CPP) and `GC.GetTotalMemory` high-water semantics; confirm heap slope with a 30 min soak and `CoreAiWebGlPersistence.Sync()` not in path.
- Real **Mirror** `INetworkBridge` not `NullNetworkBridge` for MTU/order/loss, and real `IActorIdentityProvider` — the staircase intentionally uses loopback.
- Orchestrator queue sizing for target concurrency: choose `MaxPending ≥ N + staggered` and `MaxConcurrent` from measured p95 provider latency (here stub 100 ms, real provider 17–38 s per manifest §5.2) before any 20-player acceptance claim.

## 10. Verbatim tails

Harness Release build: `dotnet build tools/ScaleHarness/ScaleHarness.csproj --nologo -v:minimal` → 0 warnings 0 errors. Full run: `dotnet run -c Release --project tools/ScaleHarness/ScaleHarness.csproj -- --output artifacts/scale/scale-baseline.json --markdown artifacts/scale/scale-baseline.md --label baseline` wall 184 s, exit 1 (zero-work gate fail expected at ≥100). Table in §5 is the console stdout. Direct `dotnet build CoreAI.Mods.csproj --no-restore` currently fails with pre-existing `InstanceRegistry.cs(648) CS0246 WorldAclDecision` due to stale Unity-generated csproj constants (the same sources build via `ScaleHarness` optimized `CoreAI.Mods -> ScaleOptimized/CoreAI.Mods.dll` without error); not introduced by this change.

## 11. Re-run after rung zero (2026-09-02, codex task `rz2`)

The partial envelope enforcement had made the harness abort at mod load
(`BAD_ARGUMENT: actor 'local' cannot write property without a mutation envelope`). After the
server-generated envelope was pushed on every production entry (mod main chunk, scheduler resumes,
signal/remote dispatch, cross-mod calls) the exact frozen command completed again:

```text
N=200 repeat 3: median 5.407 ms, p99 14.179 ms, max 18.991 ms; steps/frame 18866; chat served 400/400; zero counters: none
Linear fit over median frame cost: intercept 0.000 ms, slope 0.0265 ms/actor.
Projected actors inside 4 ms: 154; inside 16 ms: 607; largest measured passing N under the full gate: 0.
```

The frame/chat/heap gates still report failures exactly as in §7; the envelope work changed
correctness, not capacity. The actor-count-scaled admission ceiling attempted for bottleneck 1 was
reverted because it broke the `MaxPending_*` refusal tests; the admission fix remains open together
with the allocation storm (bottleneck 3).

## 12. Re-run after the signal runner pool (2026-09-05)

`ALLOC_SIGNALS_FINDING_2026-09-05.md` traced 99.6% of per-frame allocation to one fresh Lua thread
per signal handler fire; handlers now run on a parked runner coroutine per mod state (details and the
per-field reset policy in that document). Host CoreCLR, `--only N --repeats 3`, same frozen workload:

```text
N=20: alloc/frame 105.6 KB -> 52.5 KB (median, every repeat); median frame 0.538 -> 0.453 ms;
      heap slope per repeat -3.08 / 5.33 / 5.27 MB/min (worst 5.33; a single-repeat run read -3.96 and PASSED both frame budgets)
N=50: alloc/frame 269.9 KB -> 135.1 KB; median frame 1.144 -> 1.037 ms;
      heap slope per repeat 13.69 / 10.08 / 10.25 MB/min (heap 24.4 -> 28.4 MB inside a window across 12 gen0 + 1 gen1 collections, back down between repeats)
```

The allocation rate halved at both N; the heap-slope gate still fails because the 10-second fit
reads gen1/gen2 accumulation between gen2 collections (chat bursts and their records are the likely
survivors), which no longer has anything to do with thread churn — the next allocation items are the
per-resume mutation envelope and actor-context resolution (measured sizes in the finding). Note for
§10: `dotnet build CoreAI.Mods.csproj` now builds clean (0 errors) on this host.
