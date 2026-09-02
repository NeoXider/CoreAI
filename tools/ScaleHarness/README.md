# Scale staircase harness (20 / 50 / 100 / 200 actors)

Measures the SAME frozen per-actor workload at every step of the staircase through production
composition (`RegisterCorePortable` + `RegisterCoreAiMods`): the real `QueuedAiOrchestrator`
admission over a fixed-latency scripted provider, the real `ModScheduler` advanced at a fixed logical
frame delta, one real Lua mod per actor plus one host server mod, real RemoteEvent traffic through the
production `NullNetworkBridge` loopback, and the real ACL/quota checks on the instance registry and
scheduler. Nothing production-relevant is constructed by hand; the harness only supplies the provider
stub, an observing bridge decorator, the observability sink, and in-memory stores.

The workload is frozen in `scale.workload.json` (+ `scale_actor.lua`, `scale_server.lua`). Their
SHA-256 hashes are stamped into every report. Do not edit them after a measurement; add a new file
and a new label instead.

## Run

```powershell
dotnet run -c Release --project tools/ScaleHarness/ScaleHarness.csproj -- `
  --output artifacts/scale/scale-baseline.json `
  --markdown artifacts/scale/scale-baseline.md `
  --label baseline
```

`--quick` (1 x 20 actors, short window) and `--only <N>` / `--repeats <k>` are smoke overrides; a
report produced with any of them carries `frozenWorkloadHonoured = false` and is not evidence.

The console prints the staircase table; the JSON keeps every per-frame sample so the numbers can be
re-analysed without re-running.

## Runtime caveat

The harness runs on the 64-bit host CoreCLR (`dotnet` 10) with the CoreAI assemblies compiled with
`Optimize=true` under the Unity-generated Debug configuration (its DefineConstants include
`UNITY_EDITOR`, `DEBUG`). It is NOT the Unity player: Mono/IL2CPP JIT quality, the Boehm GC, and the
Unity synchronization context differ. `GC.GetAllocatedBytesForCurrentThread` is exact on CoreCLR but
returns 0 on Unity Mono. Treat every number as a lower bound on the player's cost and confirm the
final budget in the Standalone Mono / IL2CPP player (see `dev-docs/SCALE_CHARACTERIZATION.md`).

## What each phase means

- `orchestrator`: chat submissions through `IAiOrchestrationService.RunTaskAsync` plus every
  continuation pumped on the main-thread synchronization context that frame.
- `scheduler`: `ModScheduler.Advance` minus the Heartbeat signal window (deferred drains, wait/delay
  resumes, PreSimulation/PreRender pumps, input pump).
- `signals`: the window from the Heartbeat phase boundary to the InputProcessing boundary, i.e. the
  Heartbeat fan-out and the deferred signal drain where every Lua Heartbeat, OnServerEvent and
  OnClientEvent handler runs (guarded instructions counted separately).
- `network`: time spent inside the loopback bridge `SendEvent`/`SendRequest` (encode is on the
  caller side; decode + signal enqueue is inside). Nested inside `signals`.
- `modRuntime`: `ILuaModRuntime.Tick` (timers and named mod-runtime events).
