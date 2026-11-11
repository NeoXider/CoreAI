# P19 performance baseline — 2026-11

Status: measurement half of P19 ("Honest performance: measurement, budgets, optimization",
`Docs/CoreAIMods/ROBLOX_API_ROADMAP.md`). No production code was changed to produce anything here. What is
measured below is engine-free (plain .NET and the Mono CLI bundled with Unity); every Unity row is empty
until someone runs the commands in [Commands](#8-commands). Optimization follows the ranked list in
[section 7](#7-ranked-optimization-candidates), never a guess.

## 1. Method

Four instruments, each reporting the runtime, machine, scene and commit next to its numbers:

| Instrument | Where | What it measures | Runs without Unity |
|---|---|---|---|
| Guarded-VM throughput | `tools/vmbench` (existing) | the guard hook's cost curve over hook batch sizes, on a mirror of the hook body | yes |
| Portable benchmarks | `tools/perfbench` (new) | micro-benchmarks of the engine-free hot paths and the P19 reference scenes run headless, all against the production sources `tools/portable/LuaTier` compiles from `Assets/` unmodified and the shipped `Lua.dll` | yes |
| Allocation budgets | `Assets/CoreAIMods/Tests/EditMode/Performance` (new), run portably by `tools/perfbench/Tests` | zero-garbage budgets on paths that already meet them, each with a negative twin | yes (portable leg) |
| Unity harness | `Assets/CoreAIMods/Tests/PlayMode/Performance` (new) | the same reference scenes with the GameObject binder, rendering and the engine profiler counters, in the Editor and in players | no |

Rules every number follows:

- **Time** is a median of 7 timed trials after a warm-up (micro-benchmarks), or the nearest-rank p50/p95/p99
  over 600 measured frames after 120 warm-up frames (scenes). Nearest rank, so every percentile is an
  observed frame.
- **Garbage** on CoreCLR is exact: `GC.GetAllocatedBytesForCurrentThread` around the measured call or frame.
  Mono does not count per-thread allocations, so Mono rows carry time only. Allocation *types* come from
  CoreCLR's sampled `GCAllocationTick` events (`--alloc-trace`): they rank types, the exact bytes come from
  the counter.
- **Unity garbage** is the profiler's `GC Allocated In Frame` where the build records it (Editor,
  development players); a release player gets an estimate (heap growth over frames without a collection),
  and the JSON names which one it is (`gcAllocatedSource`). A counter a build does not provide is listed in
  `unavailable` and written as `null`, never as zero.
- A regression is reported, not averaged away. Nothing in a README may claim a speed without a row here.

### The headless scene frame

`PerfModWorld` (`Assets/CoreAIMods/Tests/PlayMode/Performance/Scenarios/PerfModWorld.cs`) composes the world the
way `CoreAiModsInstaller` composes it around a scene host — `LuaCsRbxApiBindings` over the host's registry,
DataModel and part sink, then `LuaCsModRuntimeFactory.Create` — with a counting
`IRbxRuntimeObservabilitySink` as the Lua counters. One frame is one `ModScheduler.Advance(1/60)`, as in
`LuaModRuntimeTickDriver`. Headless, the binder is `InMemoryInstanceBackingBinder` and the part sink
`InMemoryPartPropertySink`; in Unity the same class runs over `RbxWorldHost` (the GameObject binder).

Two deliberate differences from a production scene, both stated in the class:

- Mods load as a restricted local actor (`LocalActorIdentityProvider`, no grants), because issuing the host
  actor is reserved to the composition root and the harness must work from an assembly that is not its
  friend. For the same reason the runtime's legacy `Tick` (`hooks_every` timers, `events_emit` queue) is not
  driven per frame; no reference scene uses those APIs, and the micro-benchmarks measure them separately.
- `MaxRegisteredInstancesPerActor` is raised to the desktop ceiling (16,384), because the default per-actor
  quota (2,048) binds the host too and a 16,384-Part scene is a host-built world (finding N6).

## 2. Machine, runtime and commit of the numbers below

| Fact | Value |
|---|---|
| Machine | ASUS TUF Gaming A15 FA506NC, AMD Eng Sample 100-000000561-40_Y (8 cores, 16 threads, 3.0 GHz max), 39.3 GiB RAM, Windows 11 Pro 26200, power plan Performance, on AC (battery 79%) |
| Load | **contended**: four other agents were building and testing in the same working tree; total CPU read 70-100% throughout (`Get-Counter`, `Win32_Processor.LoadPercentage` = 100 at the start). Absolute times are pessimistic and noisy; ratios inside one run and every byte count are not affected |
| CoreCLR | .NET 8.0.21 x64, workstation concurrent GC, tiered PGO, `Optimize=true` |
| Mono | the Mono 6.13.0 CLI bundled with Unity 6000.6.0f1, **x86** (`MonoBleedingEdge/bin/mono-bdwgc.exe` = Boehm GC, `mono.exe` = SGen). Unity's Editor and Mono players embed the Boehm build (`EmbedRuntime/mono-2.0-bdwgc.dll`); a 64-bit Standalone Mono player was not run |
| Commit | HEAD `654bb3186f290e442bc2e9313c95a62742e796c6` (2026-11-11), plus the other agents' uncommitted edits in the tree when the production assemblies were built (76-85 changed paths, mostly input focus, UI focus and docs; see `uncommittedPaths` in the JSON). The commits after the 7.56.0 release are documentation only |
| Lua.dll | SHA-256 `be5acc6819888e3ebdc2509eb7779d52e43b437c8eb1130e24c5cda0407baf3f` (the same binary as `tools/vmbench/RESULTS.md`) |
| Time | 2026-11-11 05:28-06:30 UTC |
| Raw reports | `artifacts/perfbench/perfbench-coreclr.json`, `perfbench-mono-boehm.json`, `perfbench-*-partwrite.json` (local, `artifacts/` is ignored) |

## 3. Reference scenes

Defined once in `PerfScenarioCatalog` (`Scenarios/PerfScenario.cs`) and shared by every harness:

| Id | Contents |
|---|---|
| `idle-world` | empty Rbx world, mod stack composed, no mods |
| `sample-mods` | every bundled sample mod in `Resources/CoreAIMods` loaded and running (14 at this commit, including the uncommitted `coreai_player_controls`) |
| `parts-2048-static` | 2,048 anchored Parts in a 128-wide grid, no script |
| `parts-2048-heartbeat` | 1,792 anchored Parts + 256 Parts that 4 mods create and move every frame from `RunService.Heartbeat` (`part.Position = Vector3.new(...)`, 64 Parts per handler) |
| `parts-16384-static` | 16,384 anchored Parts |
| `parts-16384-heartbeat` | 16,128 anchored Parts + the same 256 movers |

The 16,384 scenes are exactly the desktop registered-instance ceiling (`LuaCsModRuntime.EmergencyMaxRegisteredInstances`),
so they hold no folder; they cannot exist in a WebGL player, whose ceiling is 4,032 (finding N6). The Unity
camera looks straight down at the whole grid, so render counters count every Part.

## 4. Numbers measured so far

### 4.1 Guarded-VM throughput (`tools/vmbench`, VM instructions per second)

Same benchmark, same `Lua.dll`, same machine as `tools/vmbench/RESULTS.md` (2026-08-31, quiet machine);
2026-11-11 is contended. Median of 5 trials.

| Hook batch | CoreCLR 08-31 | CoreCLR 11-11 | Mono SGen 08-31 | Mono SGen 11-11 | **Mono Boehm 11-11** |
|---:|---:|---:|---:|---:|---:|
| raw (no hook) | 140,737,125 | 57,787,789 | 12,567,486 | 8,736,126 | 14,404,591 |
| 1 | 7,018,465 | 4,107,917 | 40,481 | 25,708 | 1,469,060 |
| 4 (production guard) | 24,430,913 | 14,615,700 | 158,240 | 93,771 | **5,273,375** |
| 16 | 54,344,408 | 33,175,116 | 610,482 | 326,866 | 8,671,088 |
| 64 | 87,019,997 | 52,996,626 | 2,099,306 | 1,420,465 | 12,223,295 |
| 256 | 101,624,531 | 55,531,488 | 5,555,744 | 4,518,458 | 15,454,143 |
| coroutine handle (count 1, no heap read) | — | 5,649,830 | 1,810,833 | 1,523,829 | 1,402,951 |

| Primitive (ns per call) | CoreCLR 11-11 | Mono SGen 11-11 | Mono Boehm 11-11 |
|---|---:|---:|---:|
| `Stopwatch.GetTimestamp()` | 28.3 | 119.3 | 67.1 |
| `GC.GetTotalMemory(false)` | 65.6 | 14,110.9 | **19.2** |

**The 158 k "Mono" figure in `RESULTS.md` is the SGen collector.** `GC.GetTotalMemory(false)`, which the
production guard reads on every hook fire, costs 14 µs on SGen and 19 ns on Boehm. On the Boehm CLI the
production batch 4 keeps 36.6% of raw throughput (5.27 M of 14.4 M), not 1.26%. Unity's runtime is Boehm, so
the premise of spike S1 ("the guard keeps 1.26% of raw Mono throughput") is probably an artefact of the CLI
collector; it must be re-measured in a 64-bit Standalone Mono player before the adaptive batch (plan D8 a) is
sized on it. On Boehm the larger guard tax is now the scheduler-thread hook, which fires on every instruction
(1.40 M/s, 9.7% of raw): finding H2 and rank 5 below.

To build vmbench today, its two project files need one change (not made here, not in this lease): their
`Microsoft.Bcl.TimeProvider` hint path names `Assets/Packages/Microsoft.Bcl.TimeProvider.10.0.9`, which no longer
exists; the package is `8.0.1`. The runs above copied the 8.0.1 dll next to the built binary (and, for
CoreCLR, deleted `vmbench.deps.json` so the host probes the folder).

### 4.2 Micro-benchmarks (`tools/perfbench`), production code paths

CoreCLR ns/op and exact bytes/op; Mono Boehm ns/op. Contended machine, commit above.

| Group | Case | CoreCLR ns/op | CoreCLR B/op | Mono Boehm ns/op |
|---|---|---:|---:|---:|
| datatypes | Vector3 add + scale + Magnitude | 2.2 | 0 | 101.6 |
| datatypes | CFrame * CFrame | 15.7 | 0 | 223.8 |
| datatypes | CFrame * Vector3 | 15.7 | 0 | 71.5 |
| datatypes | CFrame.Inverse | 4.3 | 0 | 106.3 |
| datatypes | CFrame.GetComponents | 18.1 | 72 | 206.0 |
| datatypes | CFrame.ToString | 1,797.8 | 936 | 18,191.7 |
| signal | Fire, no connections | 1.0 | 0 | 6.9 |
| signal | Fire + deferred drain, 1 C# connection (one Advance(0) per 32 fires included) | 252.3 | 142.8 | 1,447.3 |
| signal | Heartbeat frame, 1 Lua handler (`n = n + 1`) | 15,213.7 | 1,624 | 43,086.5 |
| signal | Heartbeat frame, 8 Lua handlers | 67,822.8 | 6,608 | 242,410.1 |
| signal | Heartbeat frame, 64 Lua handlers | 461,876.0 | 46,480 | 1,650,167.3 |
| part-write | Heartbeat frame, 256 Parts moved by 4 handlers | 1,629,393 | 48,816 | 7,925,811 |
| marshalling | `LuaCsValueMarshaller.Box(number)` | 18.6 | 40 | 95.8 |
| marshalling | `LuaCsValueMarshaller.Unbox(double)` | 18.7 | 0 | 56.0 |
| marshalling | `IScriptExecutionGuard.Invoke(fn, 1 arg)` → 1 result | 906.1 | 240 | 4,139.7 |
| guard | `LuaCsExecutionGuard.Execute(empty function)` | 786.9 | 72 | 3,700.1 |
| guard | `LuaCsExecutionGuard.Execute(numeric loop)`, per iteration (8 instructions) | 424.1 | 0 | 2,712.9 |
| guard | `LuaCsCoroutineHandle.Resume`, immediate yield | 1,144.0 | 64 | 9,261.2 |
| guard | `LuaCsCoroutineHandle.Resume(numeric loop)`, per iteration | 900.1 | 0 | 6,844.3 |
| scheduler | Advance, idle world | 1,204.5 | 856 | 6,353.2 |
| scheduler | Advance, 2,048 static Parts | 1,299.6 | 856 | 6,712.8 |
| scheduler | Advance, 16,384 static Parts | 1,212.3 | 856 | 6,433.3 |
| scheduler | Advance, 64 threads parked in `task.wait(3600)` | 1,211.5 | 856 | 6,714.0 |
| scheduler | Advance, 200 threads parked | 1,934.8 | 856 | 7,011.6 |
| scheduler | Advance, 64 threads resumed every frame (`task.wait()`) | 246,927.6 | 22,360 | 1,352,947.2 |
| scheduler | Advance, 200 threads resumed every frame | 1,513,473.2 | 68,056 | 5,723,153.8 |
| dispatch | `events_emit` → `hooks_on` handler via `LuaCsModRuntime.Tick` | 2,945.1 | 600 | 14,672.0 |
| dispatch | `hooks_every` timer via `LuaCsModRuntime.Tick` | 3,040.6 | 448 | 10,642.0 |
| edit-gate | world capture (`RbxWorldPackageSerializer.Capture`), 2,048 Parts | 293.4 ms | 278.5 MB | 1,022.4 ms |
| edit-gate | world encode (`WritePackage`), 2,048 Parts | 283.8 ms | 293.2 MB | 571.1 ms |
| edit-gate | world capture, 16,384 Parts | 1,924.0 ms | 2,220.8 MB | 4,988.1 ms |
| edit-gate | world encode, 16,384 Parts | **fails**: `world.json` is 19,555,897 B, limit 16,777,216 | — | fails (19,588,665 B) |
| llm | SSE content delta parse (`ParseSseLine`) | 16,676.9 | 7,944 | 24,786.6 |

Derived (CoreCLR unless stated):

- Production guard throughput: 18.9 M instructions/s through `LuaCsExecutionGuard` (batch 4); 8.9 M/s through a
  scheduler thread's `LuaCsCoroutineHandle` (count 1). Mono Boehm: 2.95 M/s and 1.17 M/s.
- One Lua Heartbeat handler fire: 7.2 µs and **713 B** ((46,480 − 856) / 64); Mono Boehm 25.7 µs.
- One `task.wait()` resume: 7.6 µs and **336 B**; Mono Boehm 28.6 µs.
- One `part.Position = Vector3.new(...)` write from Lua: about 6.2 µs and **176 B**.
- One AI edit's pre-mutation backup at 2,048 Parts (capture + encode): 577 ms and 572 MB of garbage on the main
  thread; 1.59 s on Mono Boehm.
- The SSE row builds a fresh tool-call accumulator per call (`ParseSseLineForTests`); about 13% of its bytes are
  that accumulator, the rest (reader buffer, a `JObject` tree with line-info annotations) is per streamed token.
- Idle scheduler frame cost does not depend on the Part count (1.20 / 1.30 / 1.21 µs at 0 / 2,048 / 16,384).

### 4.3 Reference scenes, headless (scheduler frame only)

600 measured frames after 120 warm-up frames, back to back (not paced). `frameMs` is `ModScheduler.Advance`.

| Scene | CoreCLR p50 / p95 / p99 / max ms | CoreCLR B/frame (p50) | Mono Boehm p50 / p95 / p99 / max ms | Lua steps/frame | resumes/frame |
|---|---|---:|---|---:|---:|
| idle-world | 0.001 / 0.002 / 0.003 / 0.021 | 856 | 0.004 / 0.007 / 0.009 / 0.117 | 0 | 0 |
| sample-mods | 0.817 / 2.486 / 10.327 / 32.128 | 26,240 (max 42,408) | 1.589 / 2.700 / 3.631 / 34.314 | 1,543 | 12 |
| parts-2048-static | 0.001 / 0.002 / 0.003 / 0.024 | 856 | 0.005 / 0.009 / 0.011 / 0.073 | 0 | 0 |
| parts-2048-heartbeat | 1.412 / 3.392 / 13.838 / 70.442 | 48,816 | 3.915 / 7.000 / 8.859 / 43.107 | 4,916 | 4 |
| parts-16384-static | 0.001 / 0.007 / 0.010 / 0.042 | 856 | 0.005 / 0.009 / 0.014 / 0.048 | 0 | 0 |
| parts-16384-heartbeat | 1.331 / 2.094 / 5.882 / 37.384 | 48,816 | 3.524 / 5.073 / 6.294 / 29.366 | 4,916 | 4 |

All 14 sample mods loaded with no error; gen-0 collections during the 600 frames: 0 (idle, static), 2
(sample-mods), 3 (Heartbeat scenes). The tails (p99, max) are where the contended machine and collections
show; re-run on a quiet machine before quoting them.

### 4.4 Where the bytes come from (CoreCLR allocation ticks, then the source)

| Path | Bytes | Types sampled | Source |
|---|---:|---|---|
| idle frame | 856 / frame | `List<PendingRequest>` (43%), `SortedSet<…CompletionWaitEntry>` enumerator `Stack` + `Node[]` (~52%), boxed `Single` (3%) | `LuaCsRbxHttpServiceAdapter.DrainPendingCompletions` is a `PhaseReached` subscriber that allocates two lists per phase, six phases a frame (`LuaCsRbxHttpServiceAdapter.cs:591`, `:606`, subscribed at `:115`); `ModScheduler.PromoteCompletedWaits` enumerates a `SortedDictionary` (its enumerator allocates even when empty) at each of the 8 resumption points (`ModScheduler.cs:2390`, field `:583`); `PumpPreRender` passes the float delta to `RbxRunService.FireRenderPhase(object)`, boxing it before any listener check (`LuaCsRbxApiBindings.cs:3068`, `RbxRunService.cs:109`) |
| Heartbeat handler fire | 713 / fire | `LuaCsRbxScriptThread`, `object[]`, `MutationEnvelopeScope`, `Task<LuaValue[]>`, `CreateValueCallback<LuaState,RunnerPool>`, `Action<object>`, `SignalInvocation`, boxed `LuaValue`, `string[]` | a fresh thread wrapper per fire (known, `SCALE_CHARACTERIZATION.md` §12); `Fire(params object[])` plus two argument copies (`ModScheduler.cs:1719` `CopyArguments`, `:1193`) and the handler's `new object[]` with one boxed `LuaValue` per argument (`LuaCsRbxDatatypeBindings.cs:1526-1529`); `new SignalInvocation` and `BuildSignalChain`'s `string[]` per queued invocation (`ModScheduler.cs:1717-1720`, `:2372-2384`); a delegate per fire from the method group in `_runnerPools.GetValue(ownerState, CreateRunnerPool)` (`LuaCsRbxSchedulerAdapter.cs:364`, C# 9 does not cache it); a `MutationEnvelopeScope` per resume (`InstanceRegistry.cs:655`); an `Action<object>` per resume from the method group in `cancellationToken.Register(CancelResumeFromCaller, _cts)` (`LuaCsCoroutineHandle.cs:362`); a `Task<LuaValue[]>` in the runner's call into the handler (inside Lua-CSharp or the `RunPendingAsync` await, not pinned further) |
| `task.wait()` resume | 336 / resume | `object[]`, `Action<object>`, `WaitEntry`, `MutationEnvelopeScope`, boxed `Double`, `LuaValue[]` | argument copies; the same `Register` method-group delegate (`LuaCsCoroutineHandle.cs:362`); a `WaitEntry` per wait (`ModScheduler.cs:351`); the envelope scope; the elapsed time boxed as the yield result; the result array (`LuaCsCoroutineHandle.cs:659`) |
| Part property write from Lua | 176 / write | `Func<LuaValue>`, closure class, `LuaCsRbxValueBox`, boxed `RbxVector3` | the `() => …` lambda passed to `ApplyServerGeneratedMutation` on every member write (`LuaCsRbxInstanceBindings.cs:799`), and the userdata box of every `Vector3.new` |
| guarded call, empty function | 72 / call | `Task<LuaValue[]>` | `state.CallAsync(...).GetAwaiter().GetResult()` in `LuaCsExecutionGuard.Execute` (`:793`); the guard's own state is pooled |
| coroutine resume, no values | 64 / resume | `Action<object>` | `LuaCsCoroutineHandle.cs:362` only |
| var-args seam call (1 arg, 1 result) | 240 / call | `LuaValue[]`, `Task<LuaValue[]>`, boxed `LuaValue`, `object[]` | `LuaCsScriptExecutionGuard.Invoke` (`:52`, `:62-66`) |
| legacy event dispatch | 600 / event (with its Tick) | `MutationEnvelopeScope`, `Task<LuaValue[]>`, `string`, `Func<bool>`, `object[]`, `List<ApplyAiGameCommand>`, `TransactionFrame` | `LuaCsModRuntime.cs:4165` `ToArray`, `:4184`/`:4192` `params object[]`, `:4209-4221` a string concatenation and a closure per invocation, the transaction frame per call |
| world capture | 136 KB / Part | `RbxEnumItem` dictionaries and arrays, `ValueTuple<string,int>[]`, `int[]`, strings | `RbxWorldPackageSerializer.ValidatePart` builds the whole Roblox enum registry (`RbxEnumRegistry.CreateWithBuiltins()`) for every Part (`RbxWorldPackageSerializer.cs:1607`, and `:1640` per MaterialVariant), plus `CFrame.GetComponents()` and two `float[]` per Part (`:1616-1622`); it runs in `Capture` (`:150`), `WritePackage` (`:680`), `ReadPackage` (`:762`) and `RestoreFresh` (`:782`) |

### 4.5 Allocation budgets in the test suite

`HotPathAllocationBudgetEditModeTests` (15 tests) plus `PerfHarnessContractTests` (12 tests) — 27 passed, 0
failed, 0 not executed (`dotnet test tools/perfbench/Tests/...`, TRX `artifacts/perfbench/testresults/perf-tests.trx`).
Zero budgets that hold today, each with an allocating twin that must fail the same budget and print the
measured bytes: Vector3 arithmetic; CFrame multiply / point / Inverse / Lerp; `RbxScriptSignal.Fire` with no
connections; `RbxRunService.Step` with no listener; `InstanceRegistry.ProcessPreSimulation` over 2,048 Parts
and 8 Models; `LuaCsValueMarshaller.Unbox(double)`. Two production siblings are pinned as caught by the same
budget: `CFrame.GetComponents` (72 B) and `LuaCsValueMarshaller.Box` (40 B).

In Unity the budgets report Ignored with the reason: Unity's Mono answers 0 from
`GC.GetAllocatedBytesForCurrentThread`, and `AllocationMeter` checks the counter against a known allocation
before every measurement, so a budget can never pass because nothing was counted.

Paths that do **not** meet a zero budget today, so they have no test yet (bytes from §4.2): idle scheduler
frame 856 B/frame; signal fire into a C# connection 142.8 B/fire; Lua handler fire 713 B; task resume 336 B;
Part write 176 B; guarded call 72 B; coroutine resume 64 B; var-args seam 240 B; legacy event 600 B; timer
448 B; SSE delta about 6.9 KB. Each fix should land with the budget it earns.

### 4.6 Terrain chunk build: mesher, sky exposure, edits (2026-11-11)

Group `terrain` of `tools/perfbench` (`TerrainCases.cs`): the P4 reference world (256 x 64 x 256 cells, 675
chunks, rolling ground over rock with sealed caves, a sea; one shaft opened into a cave so a cave is lit from the
side). Engine-free cost only; the Unity half of a chunk build (mesh upload, MeshCollider cooking) is the P4
capture's. `--only terrain`, Release, commit 9dbc685 plus the uncommitted terrain work, contended machine (other
Unity editors compiling).

| Case | CoreCLR µs/op | CoreCLR B/op | Mono Boehm µs/op |
|---|---:|---:|---:|
| mesh one chunk (face culling + the grass-block face rule), per chunk | 145.6 | 0 | 349.6 |
| sky exposure: light one chunk's window (gather, classify, flood), per chunk | 164.5 | 0 | 300.0 |
| sky exposure: light one chunk and every face corner, per chunk (64 surface chunks) | 192.5 | 0 | 358.4 |
| edit next to a lit cave: the write, then re-mesh and light every chunk it queues | 1,467.1 | 0 | 3,401.2 |
| edit on an open plain: the same | 914.8 | 4,163 (the store allocating and releasing a chunk) | 1,415.9 |

Unity, the P4 capture (`P4TerrainPlayModeTests.P4_Capture_ReferenceWorld_FrameTimeAndDrawCallBound`, Editor,
Mono, the presenter's default budget of 6 ms a frame), same contended machine:

| Build | Frames to mesh the reference world | Wall time | Chunks per frame | Draw-call bound | Mean frame after |
|---|---:|---:|---:|---:|---:|
| before (snapshot 18:10) | 93 | 2,534 ms | 7.3 | 1,476 | 0.40 ms |
| sky exposure + grass-block rule (pmG) | 220 | 2,275 ms | 3.1 | 1,638 | 1.53 ms |

Derived:

- The sky exposure about doubles the engine-free cost of a chunk build on CoreCLR (about 0.2 ms on top of 0.1 ms).
  On the Editor's Mono it costs about as much as the rest of the chunk build: the budget now meshes 3.1 chunks a
  frame instead of 7.3, so a streamed world appears in about twice the frames while every frame stays inside the
  6 ms budget. An IL2CPP player sits between the two runtimes; WebGL is IL2CPP without threads, so expect the
  Mono-like ratio there. The draw-call bound grows 11% because grass blocks add an earth submesh to the chunks
  whose sides show. The mean frame after meshing moved with the machine's load (0.91 to 1.53 ms in three runs of
  the same build) and is not a measurement of this change.
- A chunk with no shaded cell within one cell of it (the open world) skips the flood and reports every corner as
  fully exposed without a lookup; a window with no open cell (deep rock) skips it too.
- An edit relights only chunks a shaded cell within reach of the changed cells can affect (a walk from the
  changed cells through non-blocking cells): a block placed on open ground relights nothing; carving a sealed
  cavity relights nothing; breaking a wall of a lit cave relit the chunks around it (a light-only refresh: colours
  only, no geometry upload, no collider) in the same `ProcessDirty` call, up to 32 past the time budget.
- Shader cost: no keyword, so no new variant on any platform (WebGL included); the four Rbx lit shaders read one
  more vertex attribute (COLOR) and one more interpolator, and the fragment does one select and a few multiplies.
  Terrain vertices grow from 24 to 28 bytes (the colour); Parts' meshes are unchanged.

## 5. Verified findings

File and line references are against HEAD `654bb318`. Other agents had uncommitted edits in some of the cited
files while this was written (`LuaCsRbxApiBindings.cs`, `LuaCsRbxInstanceBindings.cs`,
`InstanceGameObjectBinder.cs`, `RbxTextureMaterialProvider.cs`, `CoreAiModsInstaller.cs`,
`AiAttachmentUnityExtensions.cs`); none of those edits changes a status below.

### 5.1 The 2026-07-24 audit (`dev-docs/BACKLOG.md`, "Performance", v6.3.2)

| # | Finding | Status | Evidence (current code) |
|---|---|---|---|
| H1 | Per-guarded-call allocation storm | **partly fixed** | Fixed: the hook is pooled and re-armed (`LuaCsExecutionGuard.cs:599-604`, `:1040-1049`, `:1144-1184`), the clock is `Stopwatch.GetTimestamp()` (`:1168-1173`), the own token source is pooled (`:1079-1158`). Still present: `LuaCsScriptExecutionGuard.Invoke` allocates `new LuaValue[args.Length]` (`:52`), an `object[]` and a box per result (`:62-66`) — 240 B per 1-arg call; the VM call itself returns through a `Task<LuaValue[]>` (72 B per guarded call, `LuaCsExecutionGuard.cs:793`) |
| H2 | Per-instruction guard overhead | **partly fixed** | `LuaCsExecutionGuard` fires every 4 instructions (`:589`, `:864`). Every scheduler thread, signal runner and main chunk still runs under `LuaCsCoroutineHandle`'s hook at count 1 (`LuaCsCoroutineHandle.cs:366`), paying a clock read per instruction (`:873`) and a heap read per millisecond plus one per resume (`:701-702`, `:783`): 8.9 M instr/s on CoreCLR, 1.17 M on Mono Boehm, against 18.9 M / 2.95 M through the batch-4 guard |
| M1 | Per-event-dispatch churn | **present** | `handlers.ToArray()` per event (`LuaCsModRuntime.cs:4165`), `params object[]` per handler (`:4184`, `:4192`); also new since the audit: a string concatenation and a closure per invocation for the mutation envelope (`:4209-4221`). 600 B per event |
| M2 | Var-args marshalling boxes every LuaValue | **present** | `LuaCsValueMarshaller.Box` is `return value;` into `object` (`LuaCsValueMarshaller.cs:95-98`), used by every `LuaCsScriptCallContext` read (`LuaCsScriptCallContext.cs:54`, `:116`); `RegisterVarArgs` allocates a call context per call and a `LuaValue[]` per return (`LuaCsApiRegistry.cs:78-99`). 40 B per boxed number |
| M3 | SSE parses a full JObject per token | **fixed for content and reasoning deltas** (2026-11-11, §11) | `SseContentDeltaReader` reads the whitelisted `choices[0].delta.content` / reasoning shape in place and declines everything else to the unchanged full parse (`MeaiOpenAiChatClient.ParseSseLine`). Tool-call fragments, usage, finish and error chunks still take the `Substring` + `JObject.Load` path |
| M4 | Camera capture stalls the main thread | **partly fixed** | The render target is pooled (`RenderTexture.GetTemporary`, `AiAttachmentUnityExtensions.cs:180`, `:207`) and the readback texture cached (`:262`); the synchronous `ReadPixels` (`:241`) and `EncodeToJPG` (`:307`) stay on the main thread, no `AsyncGPUReadback`. Not measured (needs Unity) |
| L1 | `RbxCFrame.GetComponents()`/`ToString()` allocate | **present** | `RbxCFrame.cs:271`, `:611-613`; 72 B and 936 B per call. Internal operators avoid it (`:544`, `:589`), but the world capture calls it for every Part (`RbxWorldPackageSerializer.cs:1616`) |
| — | "Verified still holding" list of that audit | **holds** | binder per-aspect apply and the reused property block (`InstanceGameObjectBinder.cs:1244-1300`, `:1817`); `RbxScriptSignal.Fire` takes no snapshot copy (`RbxScriptSignal.cs:372-393`); input gated on listeners; `RbxRunService.Step` boxes nothing with no listener (budget test) |

### 5.2 Found while reading and measuring the hot paths

| # | Finding | Status | Evidence |
|---|---|---|---|
| N1 | The pre-edit world capture rebuilds the enum registry per Part | **present, measured** | `RbxWorldPackageSerializer.cs:1607`, `:1640`: 136 KB and about 140 µs per Part, in capture, encode, read and restore. 2,048 Parts: capture 293 ms + encode 284 ms, 572 MB garbage (CoreCLR); 1.02 s + 0.57 s on Mono Boehm |
| N2 | A 16,384-Part world cannot be backed up, so every gated AI edit is refused | **present, measured** | `WritePackage` fails: `world.json` is 19.6 MB against `MaximumEntryBytes` 16 MB (`RbxWorldPackageSerializer.cs:29`); `ConfirmedWorldMutationGate.ExecuteAsync` refuses the mutation for every trigger but forget and the pre-load autosave (`LuaCsGameToolExecutor.cs:137-190`). The capture itself takes 1.9 s (CoreCLR) / 5.0 s (Mono Boehm) first |
| N3 | Whole-world capture and a durable write before every AI edit | **present** | `ConfirmedWorldMutationGate.ExecuteAsync` (`LuaCsGameToolExecutor.cs:118-195`) runs `CaptureCurrent` synchronously on the main thread (`CoreAiModsInstaller.cs:167-176` → `RbxWorldPackageContracts.cs:1651-1661`) and `CreateAutoAsync` → `WritePackage` (`FileRbxWorldPackageStore.cs:1009`, `:1360`) before `execute_lua` (`LuaCsGameToolExecutor.cs:668`) and `manage_mods` (`LuaModsLlmTool.cs:236`). Cost measured through N1 |
| N4 | The idle frame is not garbage-free | **present, measured** | 856 B per frame in every world, from three sources (§4.4). The P19 DoD needs 0 B |
| N5 | Per handler fire and per resume allocations | **present, measured** | 713 B per Heartbeat handler fire, 336 B per `task.wait()` resume (§4.4) |
| N6 | Instance ceilings decide which scenes can exist | **present** | Per-actor quota 2,048 binds the host too (`LuaCsModRuntime.cs:176`, `:1686-1694`); the world ceiling is 16,384 on desktop and 4,032 on WebGL (`:177`, `:197-209`) and a host can only lower it (`:896-897`). The 16,384 scenes fit exactly; nothing else can be created in them; on WebGL they fail to build |
| N7 | Per-Part rendering: one GameObject, renderer, collider and property block | **present** (static) | `InstanceGameObjectBinder.cs:1531-1541` (MeshFilter, MeshRenderer, Box/Sphere collider per Part), `:1817-1821` (a `MaterialPropertyBlock` per renderer). A renderer with a property block is not SRP-batched. Batches at 16,384 Parts: Unity harness |
| N8 | Material textures load eagerly | **present** (static) | the first material request runs `RbxTextureMaterialProvider.EnsureSharedCache` (`RbxTextureMaterialProvider.cs:596-675`): it loads the catalog asset, which references every texture, and builds every material; `Runtime/RbxApi/Unity/Resources` holds 119 MB on disk (138 image files). Texture memory: Unity harness (`Texture Memory`, development builds) |
| N9 | `ProcessPreSimulation` visits every instance | **fixed** | it visits live Models only (`InstanceRegistry.cs:927-937`, M1-36). Measured: idle frame 1.20 / 1.30 / 1.21 µs at 0 / 2,048 / 16,384 Parts; 0 B over 2,048 Parts and 8 Models (budget test) |
| N10 | Part writes allocate a closure each | **present, measured** | `LuaCsRbxInstanceBindings.cs:799`: 176 B per `part.Position = Vector3.new(...)` with the Vector3 box; 45 KB per frame in the Heartbeat scenes |
| N11 | The Mono guard premise of S1 | **corrected** | §4.1: the 158 k figure is SGen; on the Boehm collector Unity uses, batch 4 runs 5.27 M instr/s (33x) on the same day. Player confirmation pending |
| N12 | The Unity-generated projects cannot build in this checkout | **environment** | `Library/` is absent, so every generated `CoreAI.*.csproj` fails in `CoreAI.Core.csproj` (226 errors, Newtonsoft and package references) before any test project; see §8.6 for the compile gate used instead |

## 6. What is not measured yet

- **Every Unity row**: frame time with rendering, batches and draw calls, the binder's per-Part cost, texture
  memory, garbage per frame through the engine profiler, IL2CPP, WebGL, a 64-bit Standalone Mono player.
  The harness for them exists (§8); nothing was run here, because this task may not start Unity.
- A quiet-machine re-run of `tools/vmbench` and `tools/perfbench` (everything above is contended).
- The camera capture stall (M4) and the chat overlay (an idle Hub/chat UI is not part of `idle-world`).
- The Block world reference scene (P19 lists it; it does not exist yet).
- Roblox on the same Lua micro-benchmarks (`dev-docs/LUA_VM_BENCHMARK_PLAN.md`).
- The durable file write of the autosave (only its encode is measured).

## 7. Ranked optimization candidates

Ranked by the measured cost in the reference scenes and in an AI edit, then by how cheap the fix is. Each
pass records before and after on the same rows (§4.2, §4.3) and lands the budget it earns (§4.5).

| Rank | Candidate | Evidence for the rank | Where | Expected effect |
|---:|---|---|---|---|
| 1 | Validate world packages against one shared Material enum; no `GetComponents`/`float[]` per Part | the largest single hitch measured: 577 ms and 572 MB per AI edit at 2,048 Parts (CoreCLR), 1.59 s on Mono Boehm; 136 KB per Part, linear in the world | `RbxWorldPackageSerializer.cs:1593-1645` | capture and encode drop to the tree walk and the JSON; also speeds load and restore |
| 2 | Let a large world keep its backup: split or stream `world.json`, or raise the entry limit with a WebGL-aware bound, and stop capturing the whole world before every edit (with P3) | correctness: at 16,384 Parts every gated edit is refused after a 1.9 s capture | `RbxWorldPackageSerializer.cs:29`, `:678-730`; `LuaCsGameToolExecutor.cs:118-195` | AI edits work at the scene size P19 targets |
| 3 | Allocation-free Part member writes: no closure per write (`ApplyServerGeneratedMutation` with state instead of a lambda), and no box for the Vector3 value | the largest steady cost in the Heartbeat scenes: 176 B and about 6 µs per write, 45 KB and about 1.6 ms of a 1.65 ms frame at 256 writes (CoreCLR); about 4x the time on Mono Boehm | `LuaCsRbxInstanceBindings.cs:799`, `:612-625`; the Vector3 userdata box | Heartbeat scenes towards 0 B per frame (P19 DoD) and a shorter scheduler frame |
| 4 | Allocation-free signal fire and resume: cache the `Register` and `GetValue` delegates, pool `SignalInvocation` and the chain, stop copying arguments twice, reuse the thread wrapper and the envelope scope | 713 B per handler fire, 336 B per task resume, linear in handlers and threads (§4.2) | `LuaCsCoroutineHandle.cs:362`; `LuaCsRbxSchedulerAdapter.cs:364`; `ModScheduler.cs:1193`, `:1717-1720`, `:2372-2384`; `LuaCsRbxDatatypeBindings.cs:1526-1529`; `InstanceRegistry.cs:655` | the first two are one-line delegate caches (128 B of every resume) |
| 5 | Zero-garbage idle frame | 856 B per frame in every world; the cheapest item and a P19 DoD blocker | `LuaCsRbxHttpServiceAdapter.cs:591`, `:606`; `ModScheduler.cs:2390`; `LuaCsRbxApiBindings.cs:3068` | 0 B idle; then add the idle-frame budget to §4.5 |
| 6 | Batch the scheduler-thread hook with the allocation bound intact (plan D8 a) | on Boehm the count-1 handle hook is the guard tax that is left: 1.17 M vs 2.95 M instr/s through batch 4, raw about 14 M (Mono x86 CLI); size it from a player run, not from the SGen figure | `LuaCsCoroutineHandle.cs:366`, `:858-885` | 2-3x Lua throughput on every handler and thread, if the player confirms the Boehm curve |
| 7 | SRP-Batcher-compatible Part rendering (no per-renderer property block, shared materials per colour or instancing) | static only (N7); rank is provisional until the 16,384-Part batch count exists, and may move to the top | `InstanceGameObjectBinder.cs:1757-1826` | batches no longer proportional to Parts |
| 8 | Lazy material textures (with P8) | static only (N8): 119 MB of texture files reachable from the first material request | `RbxTextureMaterialProvider.cs:596-675` | memory and first-Part hitch; needs the Unity memory rows |
| 9 | ~~A light SSE delta parser for the common `choices[0].delta.content` shape~~ **done** (§11) | 7,528 → 368 B and about 5x less time per content token (CoreCLR) | `SseContentDeltaReader.cs` | the remaining streaming costs are ranked in §11 |
| 10 | The legacy dispatch and var-args seam | 600 B per event, 448 B per timer, 240 B per seam call; these APIs are being replaced by Roblox idioms, so fix only if they stay | `LuaCsModRuntime.cs:4143-4272`; `LuaCsScriptExecutionGuard.cs:47-70`; `LuaCsApiRegistry.cs:78-99` | — |
| 11 | Camera capture without the synchronous readback | on demand; not measured | `AiAttachmentUnityExtensions.cs:207-307` | no frame hitch per vision capture |

## 8. Commands

Every command runs from the repository root. Put the commit in every Unity report
(`COREAI_PERF_COMMIT` or `-coreaiPerfCommit`): a player cannot ask git.

### 8.1 Portable benchmarks (no Unity)

```powershell
dotnet build -c Release tools/perfbench/CoreAI.PerfBench.csproj -f net8.0 --artifacts-path artifacts/perfbench
dotnet artifacts/perfbench/bin/CoreAI.PerfBench/release_net8.0/CoreAI.Mods.Tests.dll `
  --label "<machine, quiet>" --json artifacts/perfbench/perfbench-coreclr.json

dotnet build -c Release tools/perfbench/CoreAI.PerfBench.csproj -f netstandard2.1 --artifacts-path artifacts/perfbench
& "C:\Program Files\Unity\Hub\Editor\6000.6.0f1\Editor\Data\MonoBleedingEdge\bin\mono-bdwgc.exe" `
  artifacts/perfbench/bin/CoreAI.PerfBench/release_netstandard2.1/CoreAI.Mods.Tests.dll `
  --label "<machine>, Mono Boehm" --json artifacts/perfbench/perfbench-mono-boehm.json
```

Options: `--only <text>` (micro-benchmark filter), `--scenarios <ids|all|none>`, `--no-micro`, `--frames`,
`--warmup`, `--trials`, `--quick` (never evidence), `--alloc-trace <case text> --alloc-ops <n>` (sampled
allocation types, CoreCLR only). The executable is named `CoreAI.Mods.Tests` for the friend-assembly grants.

### 8.2 Allocation budgets and harness contract (no Unity)

```powershell
dotnet build -c Release tools/perfbench/Tests/CoreAI.PerfBench.Tests.csproj --artifacts-path artifacts/perfbench
dotnet test tools/perfbench/Tests/CoreAI.PerfBench.Tests.csproj -c Release --artifacts-path artifacts/perfbench --no-build `
  --filter "FullyQualifiedName~HotPathAllocationBudgetEditModeTests|FullyQualifiedName~PerfHarnessContractTests"
```

They also run in the CI Lua-tier leg (`portable-lua`): the eleven `<Compile>` lines are linked into
`tools/portable/LuaTests/CoreAI.Portable.LuaTests.csproj` (2026-11-11), which adds 27 passed tests to that
job's count. `SseContentDeltaFastPathEditModeTests` (Core) runs in `tools/perfbench/Tests`; it is not yet
linked into `tools/portable/Tests/CoreAI.Portable.Tests.csproj` (one `<Compile>` line).

### 8.3 Editor (PlayMode, explicit fixtures)

Fixture `CoreAI.Tests.PlayMode.Performance.ModsPerformancePlayModeTests`, category `Performance`, tests
`IdleWorld`, `SampleMods`, `Parts2048Static`, `Parts2048Heartbeat`, `Parts16384Static`, `Parts16384Heartbeat`.
They are `[Explicit]`: select them by name. Do not pass `-nographics` (the render counters would read 0).

```powershell
$env:COREAI_PERF_JSON = "D:\Git\CoreAI\artifacts\perf\editor-playmode.json"
$env:COREAI_PERF_COMMIT = (git rev-parse HEAD)
$env:COREAI_PERF_LABEL = "Editor 6000.6.0f1 PlayMode"
& "C:\Program Files\Unity\Hub\Editor\6000.6.0f1\Editor\Unity.exe" -batchmode -projectPath D:\Git\CoreAI `
  -runTests -testPlatform PlayMode `
  -testFilter "CoreAI.Tests.PlayMode.Performance.ModsPerformancePlayModeTests" `
  -testResults D:\Git\CoreAI\artifacts\perf\editor-playmode.xml -logFile D:\Git\CoreAI\artifacts\perf\editor-playmode.log
```

`COREAI_PERF_FRAMES` and `COREAI_PERF_WARMUP` change the frame counts. The JSON is rewritten after every test
and each test also prints its row to the test output. The ordinary PlayMode gate runs only
`PerfHarnessContractTests` from this assembly (engine-free, under a second).

### 8.4 Standalone players (Mono and IL2CPP)

Option A, the test runner builds and runs a development player with the tests (scripting backend from the
Player settings; switch it to IL2CPP for the second row):

```powershell
& "C:\Program Files\Unity\Hub\Editor\6000.6.0f1\Editor\Unity.exe" -batchmode -projectPath D:\Git\CoreAI `
  -runTests -testPlatform StandaloneWindows64 `
  -testFilter "CoreAI.Tests.PlayMode.Performance.ModsPerformancePlayModeTests" `
  -testResults D:\Git\CoreAI\artifacts\perf\standalone-mono.xml -logFile D:\Git\CoreAI\artifacts\perf\standalone-mono.log
```

The rows are in the result XML's test output and in the player log between `COREAI_PERF_JSON_BEGIN` and
`COREAI_PERF_JSON_END`.

Option B, the runner component in an ordinary build: add the scripting define `COREAI_PERF_SCENARIOS` to the
build profile (it is the only thing that compiles `CoreAI.Mods.PerformanceScenarios` into a non-test player),
tick Development Build (release players do not record most counters; the report says which ones it lacked),
build any scene, then:

```powershell
.\CoreAI.exe -coreaiPerf all -coreaiPerfOut D:\perf\standalone-mono.json -coreaiPerfCommit <sha> `
  -coreaiPerfLabel "Standalone Mono x64 development" -coreaiPerfQuit -screen-width 1920 -screen-height 1080
```

Do not pass `-batchmode` to the player. `CoreAiPerfScenarioRunner` starts itself after the first scene loads
when `-coreaiPerf` or `COREAI_PERF_SCENARIOS` is present; without either, the component does nothing.

### 8.5 WebGL

Build as in option B (define `COREAI_PERF_SCENARIOS`, Development Build), serve it, keep the tab in the
foreground (a hidden tab is throttled), open

```
http://localhost:<port>/index.html?coreaiPerf=idle-world,sample-mods,parts-2048-static,parts-2048-heartbeat&coreaiPerfFrames=600&coreaiPerfCommit=<sha>&coreaiPerfLabel=WebGL+development
```

and copy the JSON between `COREAI_PERF_JSON_BEGIN` and `COREAI_PERF_JSON_END` from the browser console. The
16,384 scenes exceed the WebGL instance ceiling and report `build failed`. `-runTests -testPlatform WebGL`
runs the explicit fixtures in a browser instead.

### 8.6 Compile gate for the Unity-side files without a Library folder

```powershell
dotnet build -c Release tools/portable/CoreAI.RbxApi.Instances/CoreAI.RbxApi.Instances.csproj --artifacts-path artifacts/perfbench
dotnet build -c Release tools/portable/CoreAI.LuauDownlevel/CoreAI.LuauDownlevel.csproj --artifacts-path artifacts/perfbench
dotnet build -c Release tools/perfbench/UnityCompileCheck/CoreAI.Mods.PerformanceTests/CoreAI.Mods.PerformanceTests.csproj `
  --artifacts-path artifacts/perfbench-unitycheck -p:UnityLibraryDir=<a Library compiled by 6000.6.0f1> `
  -p:PortableBinDir=D:/Git/CoreAI/artifacts/perfbench/bin
```

It compiles `CoreAI.RbxApi.Unity`, `CoreAI.RbxApi.Binding`, the Lua-tier slice of `CoreAI.Mods` and
`CoreAI.Source`, `CoreAI.Mods.PerformanceScenarios` and `CoreAI.Mods.PerformanceTests` from `Assets/`
unmodified against the installed editor's UnityEngine modules, without `UNITY_EDITOR`. With an editor-opened
checkout, the Unity-generated projects are the gate instead.

### 8.7 The table the Unity runs fill

| Scene | Build | frame p50 / p95 / p99 ms | main thread p50 ms | GC B/frame p50 (source) | batches p50 | draw calls p50 | Lua steps/frame | commit |
|---|---|---|---|---|---|---|---|---|
| each of the six | Editor | | | | | | | |
| each of the six | Standalone Mono (dev) | | | | | | | |
| each of the six | Standalone IL2CPP (dev) | | | | | | | |
| `idle-world` … `parts-2048-heartbeat` | WebGL (dev) | | | | | | | |

## 9. Seams the runtime would need for better counters

The harness uses only what the runtime exposes. The smallest additions that would sharpen the rows:

- **Per-resume cost of scheduler threads.** `ILuaCsGuardObserver` reports every `LuaCsExecutionGuard` call,
  but the coroutine handles (scheduler threads, signal runners) report only totals through
  `IRbxRuntimeObservabilitySink`. An optional observer passed through `LuaCsRbxScriptThreadFactory` (or
  `LuaCsCoroutineBudgetSettings`) receiving `(steps, elapsedTicks, trip)` per resume would give the
  per-resume cost and the instructions-per-resume histogram that sizes the adaptive batch (D8 a).
- **Edit-gate timing.** `ConfirmedWorldMutationGate` has `MutationStarting` and `AfterMutationAsync` but no
  capture or encode timing; an observer receiving capture ms, encode ms and package bytes would let a live
  world report its own backup cost.
- **Binder work per frame.** `InstanceGameObjectBinder.BoundCount` exists; materializations, pose writes and
  appearance writes per frame do not. Three counters on the binder would separate binder time from Lua time
  in the Unity rows.
- **Texture residency.** `RbxTextureMaterialProvider` could expose the number and bytes of textures it has
  loaded; the engine's `Texture Memory` counter exists only in development builds.
- Phase timing needs nothing new: `ModScheduler.PhaseReached` already marks every phase boundary (the scale
  harness times phases from it).

## 11. Follow-up: the streamed content-delta fast path (2026-11-11)

`Assets/CoreAI/Runtime/Core/Features/Llm/SseContentDeltaReader.cs` reads the chunk shape that carries
almost every token: one choice, a `delta` with `content` and/or a reasoning field (`reasoning_content`,
`reasoning`, `reasoningContent`, `reasoning_text`, first non-empty wins as before), and only keys that do not
change the result (`id`, `object`, `created`, `model`, `system_fingerprint`, `service_tier`, `obfuscation`,
`usage: null`, `index`, `role`, `refusal: null`, `logprobs: null`, `finish_reason: null`). Everything else —
tool calls, usage objects, finish reasons, errors, progress frames, unknown or duplicate keys, a second
choice, surrogate or unknown escapes, raw control characters, non-integer or zero-prefixed numbers, comments,
trailing text, empty content without reasoning — is declined and parsed by the unchanged full path.
`BuildContentDeltaUpdate` repeats the full path's steps for an accepted chunk (latch the wire id, content
then reasoning or reasoning alone, served model, response and message id).

Measured on the same contended machine, CoreCLR .NET 8.0.21, commit `654bb318` plus uncommitted work:

| Case (`tools/perfbench`, group `llm`) | Before (full parse) | After (fast path) |
|---|---|---|
| one content token in a streamed attempt, time | 12.9-16.6 µs | 2.7-3.4 µs |
| one content token in a streamed attempt, bytes | 7,528 B | 368 B (the update, its content list, the text and the test's result-list slot) |

Proof of equivalence: `SseContentDeltaFastPathEditModeTests` (6 tests) runs 11 accepted and 33 declined
shapes and two multi-line turns through both paths and compares every update (text, role, model, finish
reason, wire or synthetic response and message ids, contents, additional properties) or the identical
failure; the budget `ContentDelta_StaysWithinItsAllocationBudget` (512 B per token) has its twin
`ContentDelta_FullParseTwin_BreaksTheBudget` (the full parse of the same line). The existing streaming
fixtures pass unchanged with the fast path on: 366 of 366 (`MeaiOpenAiChatClientSse` 139, `SmartToolCallingChatClient`
72, `RetryingStreamingLlmClientDecorator` 38, `MeaiOpenAiChatClientStreamHotPath` 33, `CoreHotPathAudit` 26,
`StreamingAndPrompts` 22, `AiOrchestratorStreaming` 20, `MeaiOpenAiChatClientErrorRedaction` 8,
`TransportHeaders` 5, `WebGlDelay` 3), run with
`dotnet test tools/portable/Tests/CoreAI.Portable.Tests.csproj -c Release --filter "FullyQualifiedName~MeaiOpenAiChatClient|FullyQualifiedName~SmartToolCallingChatClient|FullyQualifiedName~CoreHotPathAudit|FullyQualifiedName~StreamingAndPrompts|FullyQualifiedName~AiOrchestratorStreaming|FullyQualifiedName~RetryingStreamingLlmClient"`.

### The rest of the streamed turn, measured (group `llm-turn`, in-memory SSE body, CoreCLR, exact bytes)

| Case | Bytes per turn | Per unit |
|---|---:|---|
| 200 content tokens + finish + usage, 8 KB reads | 211,832 | about 1,060 B per token including the per-turn fixed cost |
| the same turn, one SSE event per read (how a network delivers tokens) | 239,800 | about 140 B more per read |
| one tool call whose arguments arrive in 100 fragments | 897,808 | about 8.9 KB per fragment |

Remaining streaming costs, ranked (none fixed: each changes the loop's structure or its cancellation and
timeout semantics, which is not a cheap change):

1. **Tool-call argument fragments take the full parse**: about 8.9 KB per fragment (`delta.tool_calls` is
   declined by the fast path; `ExtractDeltaUpdateCore` → `SseToolCallAccumulator.Feed`). A second whitelisted
   shape (`tool_calls[0]` with `index` and `function.arguments` only) would remove most of it; the
   accumulator's completion and execute-as-you-stream rules need their own equivalence corpus first.
2. **A string per SSE line**: `ReadUtf8LinesFromStreamAsync` materializes every line (`lineBuilder.ToString()`,
   `MeaiOpenAiChatClient.cs:791`), about 370 B per token for a typical OpenAI chunk; parsing from the reused
   char buffer would need a span-based `ParseSseLine`.
3. **Per-read timeout bookkeeping**: `AwaitWithTimeoutBudgetAsync` (`:847-870`) builds a linked
   `CancellationTokenSource` (with its token array) and the read's `Task<int>` per `ReadAsync`, about 140 B per
   read, which is per token on a network stream.
4. **Per-turn fixed cost**: about 50-60 KB per turn (an 8 KB byte buffer and a 16 KB char buffer per stream,
   `:767-768`, the request body through `JTokenWriter`/`JsonSerializer`).
5. The update objects themselves (`ChatResponseUpdate`, `TextContent`, the contents list): the MEAI contract,
   about 250 B per token, not avoidable here.

Not measured in this pass: `SmartToolCallingChatClient` per roundtrip (its fixtures need an `ICoreAISettings`
fake the benchmark does not have yet) and usage accounting beyond the one usage chunk per turn above.

## 10. Files

- `tools/perfbench/` — `CoreAI.PerfBench.csproj`, `Program.cs`, `BenchEnvironment.cs`, `MicroBench.cs`,
  `MicroCases.cs`, `ScenarioFrames.cs`, `AllocationTrace.cs`, `StreamingCases.cs`; `Tests/CoreAI.PerfBench.Tests.csproj`;
  `UnityCompileCheck/` (props and six projects).
- `Assets/CoreAI/Runtime/Core/Features/Llm/SseContentDeltaReader.cs` (production, §11) and
  `Assets/CoreAI/Tests/EditMode/Performance/SseContentDeltaFastPathEditModeTests.cs` (assembly `CoreAI.Core.Tests`).
- `Assets/CoreAIMods/Tests/EditMode/Performance/` — `AllocationMeter.cs`,
  `HotPathAllocationBudgetEditModeTests.cs` (assembly `CoreAI.Mods.Tests`).
- `Assets/CoreAIMods/Tests/PlayMode/Performance/` — `CoreAI.Mods.PerformanceTests.asmdef`,
  `ModsPerformancePlayModeTests.cs`, `PerfHarnessContractTests.cs`.
- `Assets/CoreAIMods/Tests/PlayMode/Performance/Scenarios/` — `CoreAI.Mods.PerformanceScenarios.asmdef`
  (define constraint `UNITY_INCLUDE_TESTS || COREAI_PERF_SCENARIOS`) and the scenario, sampler, report, runner
  and options files.

Two asmdefs were unavoidable: files under `Tests/PlayMode/Performance` would otherwise compile into the
Editor-only EditMode assembly `CoreAI.Mods.Tests`, and the player runner has to compile in a build that
contains no test assemblies, which no existing non-production assembly allows.
