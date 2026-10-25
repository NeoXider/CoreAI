# Optional Mirror Unity integration repair

Date: 2026-10-25. Model: `gpt-6.1-sol`; reasoning effort: `max`.
Checkout: `D:/Git/CoreAI`. Status: accepted by root final5 Windows EditMode; further runtime gates separate.

## Goal and boundaries

Repair the observed optional Mirror admission/remotes test failures and preserve NET001's
exactly-once world teardown, owned pending completion and subscriber failure isolation.
World-state replication, host mode, account policy and capacity/performance claims are outside scope.
The root owns Unity, builds, executable checks, dependency installation and Git. This worker changes
only the listed `Assets/CoreAIMirror` source/tests and this receipt; vendor Mirror is read-only.

## Evidence and failure families

The root's `artifacts/fix-wave-20261025/unity-full-editmode.xml` records
`CoreAI.Net.Mirror.Tests.dll`: **226 total, 201 passed, 25 failed**. These match the 25 Mirror
entries in `unity-failures.json`; no executable acceptance is inferred from this inspection.
Installed vendor source is the root's verification-only official Mirror v96.11.3.

| Failure family | Cases | Observed cause and required correction |
| --- | ---: | --- |
| `MirrorClientRemotesEndToEndEditModeTests` | 16 | `Listen(4)` requires both `listen` and a non-WebGL compile target. The fixture omitted the listen precondition; the first repair enabled it, but the active Editor assemblies also define `UNITY_WEBGL`, so Mirror still set capacity to zero. Preserve real Listen/handlers and restore offline capacity after Listen only in a WebGL-targeted Editor. Recheck clock, remotes and kick assertions together. |
| `MirrorProviderAdmissionFailureEditModeTests` | 5 | The same rejected transport connection prevents the provider/NetworkManager accept chain and changes the expected deferred drop. Repair the common fixture instead of weakening admission or notice ordering. |
| `NET001_TransportDrop_ThrowingCompletionDoesNotSkipWorldCleanup` | 3 | The fixture connects an actor-owned `OnClientEvent` signal before attaching the active world scheduler. Reuse `RbxRemoteEvent.AttachScheduler` before the connection; retain signal/chat/Player/session cleanup assertions. |
| `NET001_ThrowingPeerListeners_StillDisconnectTheActualWorldBeforeErrorsSurface(Dispose)` | 1 | The actual-world Player is removed, but the authenticator's admission remains: an early subscriber disposes the bridge and clears peer maps before `NotifyDisconnected` reaches its ordinary binding release. Preserve owned admission cleanup despite reentrant disposal without touching a replacement session. |

## Design answers and planned files

- Real goal: run the existing integration assertions through the vendor runtime's actual startup
  preconditions, then close any remaining framework-owned lifecycle defect.
- Invariants: trusted host-derived identity; replacement session safety; deferred kick notice;
  removal before callbacks; every owned completion and lifecycle subscriber attempted once;
  no extra transport drop, no later timeout completion, no new threading or blocking.
- Reuse: the existing `OfflineMirror` real Listen/Shutdown harness, remote scheduler attachment,
  bridge/session teardown and NET001 regressions cover this work. No dependency or parallel service.
- Completed writes: `Tests/EditMode/OfflineMirror.cs`, `Tests/EditMode/MirrorKickEditModeTests.cs`,
  `Tests/EditMode/MirrorBridgeAttachEditModeTests.cs`, `Runtime/MirrorNetworkBridge.cs`, package
  `README.md`, and this receipt.
- Public API/transport contract changes: none. Keep C# 9, explicit types, English prose,
  existing optional assembly constraints and metadata intact.
- Likely mistakes to avoid: hiding full-server logs, bypassing the real connection event,
  dropping scheduler/resource assertions, forgetting a replacement connection's admission,
  or treating source review as a passed Unity run.

## Coherent source checkpoint

- `OfflineMirror.StartServer` now explicitly enables listening before `Listen(4)`, as the real
  NetworkManager does. Each harness snapshots and restores the prior listen flag and capacity,
  including after Mirror Shutdown resets shared statics. In a WebGL-targeted Editor, the fixture
  sets its offline capacity after Listen, because Mirror's compile-time guard otherwise zeros it.
- The existing three failing NET001 transport-drop cases now attach the active world scheduler
  before connecting the actor's signal. All Player, signal, chat, binding, pending-callback and
  exactly-once assertions remain present.
- `NotifyDisconnected` retains the initial admission object and transport connection object.
  After peer listeners and pending completions, if reentrant disposal removed the binding, it
  forgets only that retained admission while no replacement binding or transport object owns the
  id. Ordinary binding release, subscriber isolation, request order and transport drops are unchanged.
- Two new cases exercise actual loopback connection admission and restoration from both a
  non-listening zero-capacity state and a configured listening state. A separate reused-id case
  intentionally has the host return the same immutable admission object for a new transport
  connection: original world teardown must finish once while that replacement admission survives.
- The README documents the departure/replacement admission behavior. No new host mode,
  replication, authentication provider or production capacity setting was introduced.

## Verification performed by this worker

- Read the full XML's Mirror assembly result and its 17 `NET001_` cases: original receipt has
  13 NET001 passes and four failures. Inspected the full failure messages/stacks, runtime log
  `Server full` evidence, installed vendor startup/shutdown/connect source and actual leased diff.
- `git diff --check --` the five changed package files and this receipt: exit 0.
- Direct read-only scans: zero trailing-whitespace/Cyrillic-prose findings in those files and zero
  `var` declarations among added C# lines. Existing assembly references already include CoreAI.Core;
  no assembly or metadata edit is needed for the typed admission snapshot.
- No test, build, Unity, MCP, installation or Git mutation was run. Source evidence is not an
  executable pass. No open design question remains.

## Follow-up from the root's final3 Unity receipt

- `artifacts/fix-wave-20261025/unity-editmode-final3.xml` records **229 Mirror cases: 206 passed,
  23 failed, zero skipped**. All **17 NET001 cases passed**, including actual world/resource
  cleanup and reentrant disposal. The root corrected this worker's new fixed-provider signature
  to `TryAdmit(in ActorCredential, string)` before that run; this correction is preserved.
- The remaining failures are the same 16 client/remotes and five provider/admission cases plus
  both new startup/restoration cases. The log still contains `Server full` at the real transport
  connect event. This proves that enabling `listen` alone did not complete the fixture repair.
- Exact additional cause: vendor `NetworkServer.Listen` tests `listen && !Utils.IsWebGL`
  (`Assets/Mirror/Core/NetworkServer.cs:157`), and otherwise writes `maxConnections = 0` at line
  180. `Utils.IsWebGL` is a constant selected by `UNITY_WEBGL`, with no Editor exception
  (`Assets/Mirror/Core/Tools/Utils.cs:54`). Both current generated `Mirror.csproj` and
  `CoreAI.Net.Mirror.Tests.csproj` define **UNITY_WEBGL and UNITY_EDITOR**. Vendor Initialize
  does not reset the listen flag; all inspected fixture callers use `OfflineMirror.StartServer`.
- The follow-up changes only the fixture and its two existing regression cases: under
  `UNITY_EDITOR && UNITY_WEBGL`, set the in-memory capacity to four after vendor Listen.
  Startup asserts active/listening/capacity and retains exact before-Listen, after-vendor-Listen
  and final state in its failure message. The regressions also assert those startup preconditions
  before sending a real transport connect event and verify the saved settings after disposal.
- This is not WebGL server support or a production capacity-policy change. Real vendor
  initialization, handler registration, connect dispatch and Shutdown still run. A WebGL player
  server/socket gate remains outside this offline test fixture. No vendor or further production
  source change was made in this follow-up. This checkpoint required fresh root execution;
  the accepted result is recorded below.

## Accepted root-owned EditMode receipt

Direct XML readback of `artifacts/fix-wave-20261025/unity-editmode-final5.xml` confirms
**7,930 cases: 7,917 passed, zero failed, 13 skipped**. Its companion process receipt selects
`StandaloneWindows64` and EditMode; `unity-editmode-final5-exit.json` records **exit code 0**.
The raw top-level NUnit label is `Skipped:Ignored` because of the retained skips; it is not an
unfiltered all-tests-pass claim. The runner retains the two environmental exclusion patterns
`!BenchmarkLegacyReportEvidenceEditModeTests.RealReports_` and
`!CoreAiLuaWorldModuleEditModeTests.MigratedDemo_.*FullAccessDemo`; neither excludes Mirror cases.

The same XML positively contains `CoreAI.Net.Mirror.Tests.dll`: **229 total, 229 passed,
zero failed, zero skipped**. All **17 NET001 cases passed**. Both startup/restoration variants
and `Disconnect_ReentrantDispose_PreservesReplacementAdmissionOnAReusedConnectionId` are
present and passed. This is fresh acceptance of the optional Mirror EditMode integration scope,
including the repaired fixture, actual-world cleanup and replacement admission guard.
The original 25 failures and intermediate 23 failures above remain negative historical evidence.

Current accepted-source readback, SHA-256:

| File | SHA-256 |
| --- | --- |
| `Runtime/MirrorNetworkBridge.cs` | `fb7fda70a1be1bf7f46c6c1744f972c31ad5d2dee0c3bf5217cbf889d548f028` |
| `Tests/EditMode/OfflineMirror.cs` | `be20c7d47c9a3b0ed43280dc0f007ed278a053865d953c7debc356e4b4502fe9` |
| `Tests/EditMode/MirrorBridgeAttachEditModeTests.cs` | `c2a85183057a0598b3adca79b0144b7815cfda1531feaf860443a7d4a51363d7` |
| `Tests/EditMode/MirrorKickEditModeTests.cs` | `95a3188edf603b0497a5edf5aa26edd53bf3f4b74d357f8c9e6b19ab1e554714` |
| `README.md` | `8d4b6ceb864730fc60cb16f294bad1c46f20f056aa827fed7cb2f093e42d1176` |

Paths in this table are relative to `Assets/CoreAIMirror`. This worker changed only its two
receipts during acceptance readback, with C# frozen. Root Windows PlayMode is still pending;
real sockets, separate processes, built players, WebGL/browser/device and load/performance
remain separate unverified gates. No publication claim follows from this EditMode receipt.
