# Architecture audit — online readiness

- **Repository:** `D:\Git\CoreAI`
- **Branch / HEAD:** `feature/mvp2-multiplayer` @ `e3320bb0`
- **Date:** 2026-09-02
- **Scope:** read-only. No file outside this document and `PROGRESS.audit-arch.md` was modified.
- **Method:** every status below is decided from source that was opened and read. Documentation is
  cited only where the claim is about a plan or a recorded measurement, and is then labelled as such.
  Where a doc and the code disagree, the code wins and the disagreement is called out.

## 0. One-paragraph verdict

The seams for online play exist and are unusually well shaped: an engine-free `INetworkBridge`, an
engine-free `MutationEnvelope` plus operation ledger, an `InstanceAccessScope` ACL with an ACL
version for legacy compatibility, an actor-keyed chat factory, per-actor orchestration fairness, and
structured `RbxError` codes including `NOT_IMPLEMENTED` naming the rung. What is missing is the
*enforcement position* of those seams, and any transport at all. The ACL and the network codec live
in the Unity-referencing `CoreAI.Mods` assembly rather than in the engine-free registry, so they are
reachable only through the Lua binding boundary; the mutation envelope is applied at exactly one
production call site; no production code path ever removes an actor or fires `PlayerRemoving`; and
the token `Mirror` does not appear in any `.cs` file or `.asmdef` in the repository. MVP11 and MVP12
are therefore MISSING rather than partial, and MVP8 is a thin slice sized for remotes rather than the
full rung the plan requires as MVP11's dependency.

---

## 1. Inventory

### 1.1 The §5 "never client-authoritative" values

`dev-docs/MVP25_ONLINE_PLAN.md` §5 lists values a client may never own. There are no real clients
yet, so each row is judged on the only question that can be judged today: **is there a server-side
seam that would derive this value from an authenticated connection rather than from the caller, and
is that seam on the production path?**

| §5 value | Status | Evidence |
|---|---|---|
| authentication result | **MISSING** | `IActorIdentityProvider` (`Assets/CoreAI/Runtime/Core/Authority/IActorIdentityProvider.cs:9-13`) has one method, `GetActorContext(string roleId)`. It accepts no credential, connection or token and returns no failure value — there is no shape in which an admission decision could be expressed. The only implementation is the synthetic `LocalActorIdentityProvider` (`:27-108`). |
| durable actor id | **PARTIAL** | `ActorContext.ActorId` is durable and separate from `SessionId` (`Assets/CoreAI/Runtime/Core/Authority/ActorContext.cs:138-142`); forging is blocked by a `_trusted` flag only the private constructor sets, so `default(ActorContext)` is untrusted (`:118,135,159,209-215`). But production registers one unrestricted host actor with `ActorId == "local"` for every role (`Assets/CoreAiUnity/Runtime/Source/Composition/CoreServicesInstaller.cs:28-37`; `Assets/CoreAI/Runtime/Core/Authority/IActorIdentityProvider.cs:30`), so in the shipped composition all actors collapse to one. |
| `UserId` | **PARTIAL** | `RbxPlayer.UserId` is a server-side counter, not a client claim: `player.Initialize(actor, _nextUserId++)` (`Assets/CoreAIMods/Runtime/RbxApi/Instances/Networking/RbxPlayers.cs:37,73`). Correctly not client-supplied; also not durable across restarts. |
| session | **IMPLEMENTED (contract)** | `SessionId` keys cancellation only (`ActorContext.cs:141-142`) and `LocalActorIdentityProvider` mints a fresh GUID per construction (`IActorIdentityProvider.cs:50`), so reconnect cannot fork durable keys. Untested against a real reconnect because no transport exists. |
| role | **IMPLEMENTED** | `RoleId` is orthogonal to `ActorId` and defaults rather than deriving identity (`ActorContext.cs:131,144-145`). |
| capabilities | **PARTIAL** | `ActorGrantSet` is immutable and can only narrow (`ActorContext.cs:69-102,161-167`); `IssueRestricted` refuses to mint an unrestricted set (`:182-187`). The hole is `IsUnrestricted`: the ACL early-returns for it (`Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsRbxInstanceBindings.cs:387-401`) and production hands that grant to everyone (`CoreServicesInstaller.cs:28-29`). |
| ACL owner / scope | **PARTIAL** | `InstanceRecord.OwnerActorId` and `AccessScope` are server-side (`Assets/CoreAIMods/Runtime/RbxApi/Instances/InstanceRecord.cs:56-62,90-94`) with registry-resolved defaults (`Assets/CoreAIMods/Runtime/RbxApi/Instances/InstanceRegistry.cs:453-511`). But `SetAccessControl` is public on the registry and carries no authorization of its own (`InstanceRegistry.cs:594-595`), and the only authorization, `WorldAclAuthorizer.Demand`, is `internal` to `CoreAI.Mods` (`LuaCsRbxInstanceBindings.cs:374-481`). |
| player roster, connect/disconnect state | **MISSING** | `RbxPlayers.EnsureActor` is reached only from `EnsureNetworkActor`, which is called with the `SenderActorId` taken verbatim off the message (`Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsRbxApiBindings.cs:605-611,653-655`). `RbxPlayers.RemoveActor` has one production caller and it is the rollback branch of that same method (`LuaCsRbxApiBindings.cs:620-623`). `INetworkBridge.UnregisterActor` has **no** production caller outside the staged pass-through wrapper (`Assets/CoreAIMods/Runtime/Infrastructure/RbxWorldPackageContracts.cs:2000-2003`). There is no disconnect path; `PlayerRemoving` never fires in normal operation. |
| shared instance existence, ids, revisions, properties, destruction | **PARTIAL** | Ids are server-partitioned with a wire guard that raises `NOT_AUTHORITY` for locally-assigned ids (`Assets/CoreAIMods/Runtime/RbxApi/Instances/InstanceId.cs:71-100`). Revisions advance inside the registry lock (`InstanceRegistry.cs:313-322`) but are advanced straight from `RbxInstance` setters (`Assets/CoreAIMods/Runtime/RbxApi/Instances/RbxInstance.cs:71,93,154-156,508,528,539,697,711`), so any C# holder of an `RbxInstance` mutates and bumps the revision without an envelope or an ACL check. |
| operation ledger | **PARTIAL** | Implemented and bounded: `ApplyMutation` is the single serialized entry (`InstanceRegistry.cs:242-312`), keyed `(ActorId, OperationId)`, with replay-shape validation (`:335-347`) and FIFO eviction at `DefaultMutationReplayCapacityPerActor = 64` per actor (`:77,294-311`). The gap is reach: **one** production call site, `Assets/CoreAIMods/Runtime/Infrastructure/LuaCsGameToolExecutor.cs:264`. |
| mod source/version, load state, grants, quotas | **IMPLEMENTED (single-process)** | Mod access is owner-checked against the durable actor (`Assets/CoreAIMods/Runtime/LuaExecution/LuaCsModRuntime.cs:766-786`); quotas are per-actor with an emergency ceiling and name actor and reason on refusal (`:78-87,941-966`). |
| scheduler state | **IMPLEMENTED (single-process)** | Threads carry an owner mod id resolved to an actor, with a per-actor quota and a global emergency ceiling (`Assets/CoreAIMods/Runtime/RbxApi/Instances/Scheduling/ModScheduler.cs:29-30,1363-1382`). |
| chat history, memory, rate state | **IMPLEMENTED (contract), NOT DEFAULT** | `ActorKeyedInGameLlmChatServiceFactory` retains one service per durable actor, bounded at 256, and asserts a trusted context (`Assets/CoreAI/Runtime/Core/Features/Orchestration/InGameLlmChatServiceFactory.cs:22-25,49-79`); it is registered in production (`Assets/CoreAiUnity/Runtime/Source/Composition/CorePortableInstaller.cs:125-130`). But the flat `IInGameLlmChatService` registration resolves the *default* actor's service (`CorePortableInstaller.cs:131-136`), so any consumer still taking the plain interface shares one history. |
| cancellation scope | **IMPLEMENTED** | The queue derives the scope from `SessionId` + `RoleId`, not `RoleId` alone (`Assets/CoreAI/Runtime/Core/Features/Orchestration/QueuedAiOrchestrator.cs:998-1010`). Measured: 0 cross-actor cancellations in both arrival patterns (`dev-docs/MVP2_ACCEPTANCE_MANIFEST.md` §5.1 and §5.2 tables). |
| audit attribution / metrics | **IMPLEMENTED** | Metrics are actor-keyed with role carried alongside (`Assets/CoreAI/Runtime/Core/Features/Orchestration/InMemoryAiOrchestrationMetrics.cs:325-341`). |
| world package, backups, autosave ordering | **IMPLEMENTED (host-mediated)** | `save_world` and `load_world` derive the actor from the trusted provider and take no caller actor id (`Assets/CoreAIMods/Runtime/Infrastructure/RbxWorldPackageContracts.cs:384-388,439-443`). `load_world` cannot apply a package — it returns `player_confirmation_required` plus a one-use request id (`:444-452`). This is the strongest fail-closed boundary in the codebase. |
| server time | **MISSING (declared)** | `Workspace:GetServerTimeNow` is a declared planned member raising `NOT_IMPLEMENTED` for MVP2 (`Assets/CoreAIMods/Runtime/RbxApi/Instances/ClassCatalog.cs:410-412`). Loud, not silent — but there is no synchronized clock. |
| remote caller identity | **MISSING** | The sender of a `ClientToServer` event is whatever string the message carries; the receive path creates or looks up the `Player` from it (`LuaCsRbxApiBindings.cs:653-655`). Nothing binds `SenderActorId` to a connection. In loopback this is safe because the send path stamps the trusted id (`LuaCsRbxApiBindings.cs:500-502`); with a real transport it is a forgery primitive. |
| response correlation | **PARTIAL** | `RbxNetworkRequestResponder` is single-use and refuses double completion (`Assets/CoreAIMods/Runtime/RbxApi/Instances/Networking/INetworkBridge.cs:114-149`); the loopback resolves through a captured continuation, so cross-correlation is structurally impossible in-process. No wire correlation id and no timeout exist in the contract, because there is no wire. |
| rate counters | **PARTIAL** | Per-actor, per-group, per-second admission refusing with `BUDGET_EXCEEDED` and naming the actor (`Assets/CoreAIMods/Runtime/RbxApi/Instances/Networking/NullNetworkBridge.cs:199-235`). It is a property of `NullNetworkBridge`, not of `INetworkBridge` — the interface mandates no rate limiting (`INetworkBridge.cs:155-176`), so a second transport can legally ship without it. |
| filtering decisions | **MISSING** | No replication filter. `NetId` is declared and assigned only through `BindNetId` (`InstanceRecord.cs:74-75`; `InstanceRegistry.cs:690`); nothing computes a per-client visible set. |
| shared physics state, network ownership | **MISSING** | No network-ownership surface. `Workspace.Gravity` is a declared MVP8 stub (`ClassCatalog.cs:406-409`). |

### 1.2 MVP8 gates (`MVP25_ONLINE_PLAN.md` §4.2)

| Gate | Status | Evidence |
|---|---|---|
| P8.1 player contexts and lifecycle | **PARTIAL** | `Players` is tree-backed (`Assets/CoreAIMods/Runtime/RbxApi/Instances/ServiceCatalog.cs:173`); `Player`/`Players` descriptors are registered (`Assets/CoreAIMods/Runtime/RbxApi/Instances/ClassCatalog.cs:357,383`); `PlayerAdded`/`PlayerRemoving` exist and are scheduler-bound (`RbxPlayers.cs:42-48`; `LuaCsRbxApiBindings.cs:238`); `LocalPlayer` is nil in server context (`LuaCsRbxApiBindings.cs:487-495`). **Fails the lifecycle half**: no production add/remove seam, so `PlayerRemoving` cannot fire exactly once — it cannot fire at all. |
| P8.2 Player/Humanoid behavior | **MISSING** | No `Humanoid` class, no controller adapter, no health/death/`MoveTo`, no values, no `leaderstats`. `RbxPlayer` carries `UserId` and `NetworkActorId` only (`RbxPlayers.cs:7-29`). |
| P8.3 physics services | **MISSING** | `Workspace.Gravity` and `workspace:Raycast` are declared unimplemented (`ClassCatalog.cs:405-409`); no `Touched`/`TouchEnded`. |
| P8.4 Debris / Tween / Collection | **MISSING (loudly)** | All three are registered stubs naming MVP8 (`ServiceCatalog.cs:174-176`); access raises `NOT_IMPLEMENTED` with the rung (`ServiceCatalog.cs:23`; `Assets/CoreAIMods/Runtime/RbxApi/Instances/RbxError.cs:106-110`). |
| P8.5 corpus | **PARTIAL** | The manifest records the Tier-A+B catalog at **17/20 = 85% (0 modified, 3 failing)**, measured 2026-09-01, against the MVP2 ≥30% gate (`dev-docs/MVP2_ACCEPTANCE_MANIFEST.md` §5 row G12, fixture ids frozen in §5 "G12 frozen fixture ids"). The MVP8 60% gate is a larger set including kill-brick, touch-pickup-with-leaderstats and door-tween fixtures, none of which can pass while P8.2–P8.4 are missing. |

### 1.3 MVP11 gates (`MVP25_ONLINE_PLAN.md` §4.3)

**No transport exists.** A repository-wide search for `Mirror` in `.cs` sources returns nothing; no
`.asmdef` declares a `MIRROR` versionDefine or a Mirror reference; the project scripting defines are
`DOTWEEN;COREAI_HAS_HUB;COREAI_LLM;COREAI_LUA` (`ProjectSettings/ProjectSettings.asset:839-845`).
Every MVP11 gate is therefore MISSING; the rows record what the contract already provides.

| Gate | Status | Evidence |
|---|---|---|
| N11.1 admission | **MISSING** | No credential input in `IActorIdentityProvider.cs`. `EnsureNetworkActor` creates a `Player` on first inbound message with no admission step (`LuaCsRbxApiBindings.cs:605-627`). |
| N11.2 identity integrity | **MISSING** | `SenderActorId` is trusted verbatim by `DeliverNetworkEvent` (`LuaCsRbxApiBindings.cs:653-655`). The durable/session split reconnect needs exists in the type (`ActorContext.cs:138-142`) but no reconnect path exercises it. |
| N11.3 real RemoteEvent traffic | **MISSING** | Only `NullNetworkBridge` implements the contract (`NullNetworkBridge.cs:22`); it reports `Topology => Solo` (`:73`) and delivers in-process (`:177-197`). Route validation and rate limiting are present (`:199-235,265-296`); **oversize payload rejection is not** — `RbxErrorCode.PayloadTooLarge` is declared and named `PAYLOAD_TOO_LARGE` (`RbxError.cs:22,87`) but is thrown nowhere in production. |
| N11.4 RemoteFunction correlation | **PARTIAL (in-process only)** | Single-use responder refusing double completion (`INetworkBridge.cs:114-149`); loopback callback failure is converted rather than lost (`NullNetworkBridge.cs:164-174`). No timeout is expressed in the bridge contract. |
| N11.5 player teardown | **MISSING** | See the roster row in §1.1; `UnregisterActor` is dead code in production. |
| N11.6 contexts and clock | **PARTIAL** | Context enforcement exists: `RequireNetworkSide` raises `NOT_AUTHORITY` naming actor, member and side (`LuaCsRbxInstanceBindings.cs:141-158`), and `LocalPlayer` is nil server-side (`LuaCsRbxApiBindings.cs:487-492`). Synchronized server time is MISSING (`ClassCatalog.cs:410-412`). |
| N11.7 optional transport boundary | **PARTIAL — the testable half passes** | `INetworkBridge` lives in the engine-free assembly and a fitness test asserts it (`Assets/CoreAIMods/Tests/EditMode/RbxApi/Networking/NetworkBridgeEditModeTests.cs:28`); solo composition falls back to `NullNetworkBridge` when nothing is registered (`Assets/CoreAIMods/Runtime/Composition/CoreAiModsInstaller.cs:169-170,317-318`). The Mirror-present half cannot be evaluated. |

### 1.4 MVP12 gates (`MVP25_ONLINE_PLAN.md` §4.4)

| Gate | Status | Evidence |
|---|---|---|
| R12.1 filtering | **MISSING** | No filter, whitelist or per-client view. `NetId` is reserved only (`InstanceRecord.cs:74-75`). |
| R12.2 canonical authority | **MISSING** | `ClientWritePolicy` does not exist in code; the identifier appears only in `Docs/CoreAIMods/ROBLOX_API_ROADMAP.md:158,843,862`. `NOT_AUTHORITY` exists as a code (`RbxError.cs:21,86`) and is raised for context and id-partition violations, not for client writes. |
| R12.3 mutation intents | **PARTIAL — the envelope exists, its position does not** | `ApplyMutation` gives idempotency, optimistic revision and actor binding (`InstanceRegistry.cs:242-312`); `LuaCsGameToolExecutor` refuses an envelope whose actor differs from the trusted context (`LuaCsGameToolExecutor.cs:235-250`). But it is applied at one call site (`:264`), and other production entry points reach the same registry without it — see §4.2. |
| R12.4 late join | **PARTIAL (serializer only)** | One canonical serializer for disk and `ExportSnapshot` is a stated W3.2 property (`Docs/CoreAIMods/WORLD_PACKAGE.md`, "Format and runtime boundary" and "Capture and `ExportSnapshot` project the live DataModel…"), and confirmed session replacement exists (`Docs/CoreAIMods/WORLD_PACKAGE.md`, "Production session replacement"; `Assets/CoreAIMods/Runtime/Infrastructure/RbxWorldPackageContracts.cs:994-1030`). There is no delta stream, no ordered revision channel and no resync policy. |
| R12.5 physics boundary | **MISSING** | No physics replication surface at all. |
| R12.6 churn and scale | **NOT MEASURED** | The manifest's reference-machine CPU/RAM/GPU row is literally `**NOT MEASURED**` and so is the RSS ceiling (`dev-docs/MVP2_ACCEPTANCE_MANIFEST.md` §1 and §7). The one real-provider 20-actor run is recorded as **FAILED** (§5.2). |

---

## 2. Engine-agnosticism: can a different transport replace Mirror without touching Lua/RbxApi?

**Answer: yes for the byte boundary, no for everything the plan actually needs from a transport.**

### 2.1 The seam, and why it is real

`INetworkBridge` is a byte-level port with no Unity and no Mirror types in its signature
(`Assets/CoreAIMods/Runtime/RbxApi/Instances/Networking/INetworkBridge.cs:155-176`). Its message
types carry `InstanceId`, an enum direction/reliability, two actor-id strings and a `byte[]`
(`:31-57,60-82`). It lives in `CoreAI.RbxApi.Instances`, whose `asmdef` declares
`"noEngineReferences": true` and references only `CoreAI.RbxApi.Datatypes`
(`Assets/CoreAIMods/Runtime/RbxApi/Instances/CoreAI.RbxApi.Instances.asmdef`). `CoreAI.RbxApi.Datatypes`
references nothing and is likewise engine-free
(`Assets/CoreAIMods/Runtime/RbxApi/Datatypes/CoreAI.RbxApi.Datatypes.asmdef`). `CoreAI.Core` is the
same shape (`Assets/CoreAI/Runtime/Core/CoreAI.Core.asmdef`, `"noEngineReferences": true`,
`"references": []`).

This is enforced, not merely declared. Fitness tests assert the asmdef flag, the absence of any
`UnityEngine` using/qualified reference in the domain sources, and the inward-only reference list:

- `Assets/CoreAIMods/Tests/EditMode/RbxApi/Instances/RbxApiInstancesArchitectureFitnessEditModeTests.cs:28,51-55,87-110`
- `Assets/CoreAIMods/Tests/EditMode/RbxApi/Datatypes/RbxDatatypesFitnessEditModeTests.cs:28,41-51,59`
- `Assets/CoreAIMods/Tests/EditMode/RbxApi/Networking/NetworkBridgeEditModeTests.cs:28` asserts
  `INetworkBridge` ships in the same assembly as `RbxInstance`.

Composition resolves the bridge optionally and falls back, so a host can substitute an
implementation with no change to `CoreAI.Mods`
(`Assets/CoreAIMods/Runtime/Composition/CoreAiModsInstaller.cs:169-170` and `:317-318`:
`c.ResolveOrDefault<INetworkBridge>() ?? new NullNetworkBridge()`). Session replacement wraps
whatever bridge is supplied without knowing its type (`StagedNetworkBridge`,
`Assets/CoreAIMods/Runtime/Infrastructure/RbxWorldPackageContracts.cs:1972-2041`).

**No leak was found.** No Mirror or `UnityEngine` type appears in `CoreAI.Core`,
`CoreAI.RbxApi.Datatypes` or `CoreAI.RbxApi.Instances`. `CoreAI.RbxApi.Unity` is the declared Unity
adapter and correctly sits outside the engine-free set
(`Assets/CoreAIMods/Runtime/RbxApi/Unity/CoreAI.RbxApi.Unity.asmdef`, `noEngineReferences: false`).

### 2.2 Where the seam stops being sufficient

1. **Serialization is above the seam and in the Unity assembly.** `LuaCsRbxNetworkCodec` — which
   turns Lua values into the `byte[]` the bridge carries — is `internal` to `CoreAI.Mods`
   (`Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsRbxNetworkCodec.cs:46`) and emits UTF-8 JSON via
   Newtonsoft (`:97-112`). A transport therefore cannot negotiate its own encoding, cannot delta-code,
   and pays JSON cost per message. For MVP12 batching this is the wrong side of the boundary.
2. **The ACL is not on the transport's side of the boundary.** `WorldAclAuthorizer` is `internal
   static` inside `LuaCsRbxInstanceBindings.cs:374-481`, i.e. in `CoreAI.Mods`. A transport living in
   its own assembly literally cannot call it. This is the concrete mechanism behind §5's warning that
   "the current Lua-binding-only ACL is insufficient".
3. **The contract omits everything MVP11/MVP12 must gate.** `INetworkBridge` has no admission hook,
   no connection handle, no disconnect event, no payload-size bound, no rate policy, no timeout and
   no topology transition. Rate limiting exists only as an implementation detail of the loopback
   (`NullNetworkBridge.cs:199-235`). A second transport can satisfy the interface and provide none of
   it, which would silently regress the loopback's guarantees.
4. **Actor registration is inbound-driven, not admission-driven.** `RegisterActor` is called from the
   *receive* path (`LuaCsRbxApiBindings.cs:615`), so under any real transport the first packet
   creates the actor. Admission must move above the bridge.

**Recommended shape (matches the reference the plan names):** NeoxiderTools isolates Mirror behind a
dedicated assembly plus a contracts assembly with a `MIRROR` versionDefine
(`D:\Git\NeoxiderTools\Assets\Neoxider\Scripts\Network\Neo.Network.asmdef` references
`Mirror`, `Mirror.Components` under `versionDefines: [{ define: "MIRROR", name:
"com.mirrornetworking.mirror" }]`;
`D:\Git\NeoxiderTools\Assets\Neoxider\Scripts\NetworkContracts\Neo.Network.Contracts.asmdef` has
`"references": []`). CoreAI should add `CoreAI.Net.Mirror` as a separate optional assembly with the
same versionDefine, depending on `CoreAI.RbxApi.Instances` and nothing in `CoreAI.Mods`. That keeps
solo free of a hard Mirror dependency, which is owner decision 2's recommendation.

---

## 3. Scalability risks for 100–200 actors

### 3.1 Measured facts (from the repository, not estimated)

| Fact | Value | Source |
|---|---|---|
| Guarded VM throughput, production batch 4 | 148 374 – 158 240 instructions/s | `dev-docs/MVP2_ACCEPTANCE_MANIFEST.md` §8 |
| Instructions available in a 4 ms frame, **across all actors** | 589 | `dev-docs/MVP2_ACCEPTANCE_MANIFEST.md` §8, §9 |
| Batch 256 speedup | 35.1× (≈20 600 instructions per 4 ms frame) | `dev-docs/MVP2_ACCEPTANCE_MANIFEST.md` §8, §9 |
| Calibrated benchmark Lua body | 580 guarded instructions | `dev-docs/MVP2_ACCEPTANCE_MANIFEST.md` §5.1 |
| G10 real-provider run, 20 actors | **FAILED**: staggered 10/40 served (0.25), burst 23/40 (0.575); p95 end-to-end 72.0 s / 52.4 s | `dev-docs/MVP2_ACCEPTANCE_MANIFEST.md` §5.2 |
| Backend parallelism during that run | 1 | `dev-docs/MVP2_ACCEPTANCE_MANIFEST.md` §3 |
| Cross-actor cancellations / admission failures in that run | 0 / 0 | `dev-docs/MVP2_ACCEPTANCE_MANIFEST.md` §5.2 |
| Reference machine CPU/RAM/GPU, RSS ceiling | **NOT MEASURED** | `dev-docs/MVP2_ACCEPTANCE_MANIFEST.md` §1, §7 |
| Measurement caveat | numbers came from Unity's bundled x86 Mono CLI; must be re-confirmed on the 64-bit Standalone player | `dev-docs/MVP2_ACCEPTANCE_MANIFEST.md` §8 note 3 |

Two consequences follow directly from those numbers and are **not** estimates: at batch 4 a 4 ms
frame holds fewer than 30 guarded instructions per actor at 20 actors, so the frame gate is
arithmetically impossible (manifest §9); and the 20-actor chat gate failed on backend capacity, not
on CoreAI scheduling — the queue admitted and isolated correctly (manifest §5.2).

### 3.2 Code-level risks (my analysis, with file:line)

| # | Risk | Evidence | Cost shape |
|---|---|---|---|
| S1 | **Global pending-queue cap of 64 shared by all actors.** `MaxPending` defaults to 64 and is a single global counter checked as `_pending.Count + _streamPending.Count` | `Assets/CoreAI/Runtime/Core/Features/Orchestration/AiOrchestrationQueueOptions.cs:18`; `QueuedAiOrchestrator.cs:63-65,828,929` | At 200 actors this is 0.32 pending slots per actor; a synchronized burst rejects most actors with `AiOrchestrationQueueFullException`. Fairness does not help — admission happens before fairness. |
| S2 | **Dispatch is O(actors) + O(pending) per slot.** `SelectNextActorIdLocked` scans every actor queue state; `FindNextTaskIndexLocked` / `FindNextStreamIndexLocked` then linearly scan the pending lists with an ordinal string compare per element | `QueuedAiOrchestrator.cs:314-328,350-374` | Small in absolute terms today (bounded by 64 pending), but it runs under the single `_lock` that also guards enqueue, cancel and completion (`:265,452,524,618,678,814,915`), so it is the process-wide serialization point for all actor traffic. |
| S3 | **Per-actor scheduler quota is an O(live-threads) scan on every thread creation, with a delegate call per record.** | `Assets/CoreAIMods/Runtime/RbxApi/Instances/Scheduling/ModScheduler.cs:1374` calling `CountThreadsForActor` (`:1411-1424`), which calls `ResolveActorId` (`:1426-1430`) per record | O(N) per spawn → O(N²) per burst of spawns. Bounded by `EmergencyMaxThreads = 4096` (`:30`), so worst case is ~4096 comparisons per `task.spawn`. |
| S4 | **`EmergencyMaxThreads = 4096` is the real per-actor limit at scale, not `MaxThreadsPerActor = 256`.** | `ModScheduler.cs:29-30,1363-1372` | At 200 actors the global ceiling allows ~20 threads each; the advertised per-actor quota of 256 is unreachable. The refusal names the ceiling, so it is loud — but capacity planning must use 4096/N. |
| S5 | **Mod load quota is an O(loaded-mods) scan with two string normalizations per record.** | `Assets/CoreAIMods/Runtime/LuaExecution/LuaCsModRuntime.cs:950-959` | Bounded by `EmergencyMaxMods = 256` (`:80`). Same shape as S4: at 200 actors the global 256 ceiling allows ~1 mod each, while `DefaultMaxMods` is 32 and `BenchmarkMaxMods` is 200 (`:78-79`). |
| S6 | **Every registered instance is visited each PreSimulation.** `ProcessPreSimulation` iterates `_byId.Values` and type-tests each record for `RbxModel` | `Assets/CoreAIMods/Runtime/RbxApi/Instances/InstanceRegistry.cs:324-333`, driven per frame from `Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsRbxApiBindings.cs:849` | O(all instances) per frame regardless of dirtiness. The world package format permits 100 000 instances (`Docs/CoreAIMods/WORLD_PACKAGE.md`, "Validation and limits"), so this is a per-frame 100 k-element scan in the worst legal world. |
| S7 | **Broadcast fan-out is O(registered actors) per `FireAllClients`, executed synchronously inside the send call.** | `LuaCsRbxApiBindings.cs:660-666`; `NullNetworkBridge.SendEvent` drains immediately (`NullNetworkBridge.cs:134-135,177-197`) | At 200 actors one broadcast is 200 signal fires on the calling thread inside the same frame. There is no batching and no per-tick coalescing — the thing MVP12's "batch dirty state" requires. |
| S8 | **Per-actor `RbxScriptSignal` allocated per remote and never pruned.** `GetOnClientEvent` creates and caches a signal keyed by actor id; nothing removes entries | `Assets/CoreAIMods/Runtime/RbxApi/Instances/Networking/RbxRemotes.cs:10,26-41` (no removal anywhere in the file) | Unbounded growth of `remotes × actors`, and it survives disconnect because there is no disconnect path (§1.1). Reconnect churn with new actor ids is a leak. |
| S9 | **Remote payloads are JSON text with no byte cap.** `EncodeArguments` builds a `JArray`, renders it to a `string`, then UTF-8 encodes; `DecodeArguments` materializes the whole string before parsing | `Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsRbxNetworkCodec.cs:97-112,114-140` | Structural caps exist — depth 64 and 100 000 aggregate entries (`:49-50,58-69`) — but 100 000 entries is megabytes of JSON per message, allocated twice (string + tokens) per hop. |
| S10 | **Subscription snapshot is rebuilt in full on every subscribe.** `PublishSubscriptionSnapshotLocked` allocates a new dictionary and a new `Mod[]` per event key | `Assets/CoreAIMods/Runtime/LuaExecution/LuaCsModRuntime.cs:2591-2601`, called from `RegisterSubscription` (`:2577-2589`) | O(total subscriptions) allocation per subscribe. Load-time only, but at 200 actors starting mods simultaneously it is a quadratic burst. The dispatch path itself is correct and lock-free (`:2536-2557`), which was the v1 O(n²) defect and is fixed. |
| S11 | **String-keyed ordinal dictionaries on every hot path.** actor→queue state, actor→rate window, actor→signal, actor→chat service, mod→actor | `QueuedAiOrchestrator.cs:354,367`; `NullNetworkBridge.cs:41-44,209-222`; `RbxRemotes.cs:10`; `InGameLlmChatServiceFactory.cs:27-28`; `InstanceRegistry.cs:81-83` | Correct and safe, but every message pays string hashing plus `ResolveActorId` trimming (`ModScheduler.cs:1426-1430` allocates on `Trim()` when the input has whitespace). An interned actor handle would remove this class of cost before the 100–200 measurement. |
| S12 | **One process-wide lock guards all mutation and all revision advance.** `_mutationGate` is taken by `ApplyMutation`, `AdvanceRevision`, `MarkDetached` and `RetainedMutationOperationCount` | `InstanceRegistry.cs:88,242-312,313-322,208-217,219-228` | Every property write in the world serializes through it via `RbxInstance` setters (`RbxInstance.cs:71,93,154-156,508,528,539,697,711`). At 100–200 actors with replication this is the first contention point, and it is held across the *entire user operation* in `ApplyMutation` (`:286` runs `operation()` inside the lock). |

**Not a risk (verified fixed):** the plan's ceiling table lists "Events broadcast to every mod —
`EmitEvent` iterates `_mods.Values` under `lock (_gate)`". That is no longer true: routing reads a
`Volatile.Read` snapshot outside any lock and touches only subscribers
(`LuaCsModRuntime.cs:2536-2557`).

---

## 4. Security boundary gaps once real clients connect

### 4.1 Forged actor ids

`DeliverNetworkEvent` reads `message.SenderActorId` and passes it straight to `EnsureNetworkActor`,
which creates a `Player` and registers the actor with the bridge
(`Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsRbxApiBindings.cs:653-655,605-627`). The trusted id
is stamped only on the *send* side (`:500-502`). Under a real transport the receive side must derive
the actor from the connection; today nothing in the `INetworkBridge` contract even exposes a
connection to derive it from (`INetworkBridge.cs:155-176`). **This is the single highest-severity
gap.**

Aggravating factor: `EnsureNetworkActor` *creates* identity on receipt, so an unauthenticated peer
that can put bytes on the bridge can also allocate `Player` objects and registry records
(`RbxPlayers.cs:66-78`) — an unbounded-allocation path with no admission in front of it.

### 4.2 Client-authoritative writes / ACL bypass

The ACL is enforced in exactly one layer: the Lua binding context
(`LuaCsRbxInstanceBindings.cs:209-334` calling `WorldAclAuthorizer.Demand` at `:217,225,233,239,255,259,265,274,307,311,321,326,330`).
It is `internal` to `CoreAI.Mods` (`:374-376`). Consequences:

1. **Three production entry points bypass the mutation envelope.** `ApplyMutation` has one caller
   (`LuaCsGameToolExecutor.cs:264`). The plain `execute_lua` overload runs without an envelope
   (`Assets/CoreAIMods/Runtime/LuaExecution/LuaTool.cs:95-105` → `LuaCsGameToolExecutor.cs:208-225`);
   the MCP `execute_lua` tool calls the same envelope-free executor
   (`Assets/CoreAIMcp/Runtime/Tools/ExecuteLuaMcpTool.cs:28,57`); and mod code loaded through
   `manage_mods` mutates through `LuaCsRbxModContext` with `_mutationEnvelope == null`, in which case
   `RequireMutationTarget` returns immediately (`LuaCsRbxInstanceBindings.cs:336-341`).
2. **The envelope form is opt-in on composition.** `LuaTool` only exposes `ExecuteMutationAsync` when
   an `IActorIdentityProvider` was injected (`LuaTool.cs:69-90`); otherwise the tool is the
   envelope-free `ExecuteAsync`.
3. **The ACL itself is opt-in per world.** `WorldAclAuthorizer.Demand` returns immediately when
   `!registry.IsWorldAclEnabled` (`LuaCsRbxInstanceBindings.cs:379-382`), and legacy worlds load with
   `WorldAclVersion == null` (`InstanceRegistry.cs:138-141`; serializer restores whatever the package
   carried, `Assets/CoreAIMods/Runtime/RbxApi/Instances/InstanceTreeSerializer.cs:194`). The manifest
   states this explicitly: "Legacy worlds whose ACL version is missing or `null` remain in
   compatibility mode: they do **not** receive cross-actor mutation/destruction refusal"
   (`dev-docs/MVP2_ACCEPTANCE_MANIFEST.md`, "G2 scope").
4. **Unrestricted grants skip almost all of it.** `Demand` early-returns for
   `Grants.IsUnrestricted`, checking only that host-protected singletons are not destroyed or
   reparented (`LuaCsRbxInstanceBindings.cs:387-401`) — and production issues exactly that grant to
   every role by default (`CoreServicesInstaller.cs:28-37`).
5. **A mod with no recorded attribution silently becomes the host.** `LuaCsRbxModContext`'s
   convenience constructor resolves the actor from registry attribution, and falls back to
   `CoreServicesInstaller.DefaultLocalHostIdentityProvider` when there is none
   (`LuaCsRbxInstanceBindings.cs:88-105`); `IsHost` is then `Grants.IsUnrestricted`
   (`:115`). Fail-open by default on the ownership lookup is the wrong direction for a rung whose
   whole point is per-actor authority.

### 4.3 Replay of operation ids

Correct where it applies, with one documented and honest sharp edge. Duplicates return the first
result; a replay whose target or expected revision differs is refused
(`InstanceRegistry.cs:258-271,335-347`). Eviction is FIFO at 64 entries per actor
(`:77,294-311`), and the XML doc on `ApplyMutation` states the consequence plainly: once evicted, a
state-changing replay is rejected as stale but "a true no-op whose target revision never advanced may
execute again" (`:232-241`). At 100–200 actors issuing bursts, 64 entries is roughly one frame of
history — the window must be sized from measurement before MVP12, not left at the constant.

### 4.4 Oversize payloads

**No byte limit anywhere.** `RbxErrorCode.PayloadTooLarge` and its wire name exist
(`RbxError.cs:22,87`) and are raised by no production code. The codec bounds structure (depth 64,
100 000 aggregate entries) but not size (`LuaCsRbxNetworkCodec.cs:49-50,58-69`), and `DecodeArguments`
materializes the entire payload as a `string` before validating anything (`:114-127`). N11.3 requires
"malformed/oversize payloads are refused"; malformed is covered (`:134-139`), oversize is not.

### 4.5 Rate limits

Present in the loopback only, and reasonable there: per actor, per rate group (reliable /
unreliable / RemoteFunction), 500/s default, refusing with `BUDGET_EXCEEDED` naming actor, limit and
group (`NullNetworkBridge.cs:24,199-235`). Two gaps: the limit is not part of `INetworkBridge`, so a
new transport need not implement it; and the counters are per-actor dictionaries created on demand
(`:209-214`) with entries removed only by `UnregisterActor` (`:101`) — which nothing calls (§1.1), so
rate state accumulates for the process lifetime.

There is a second, unrelated limiter on `execute_lua` (`LuaTool.cs:171-181`) which is global per tool
instance, not per actor.

### 4.6 What is already correctly fail-closed

Worth recording so it is not undone: `load_world` cannot apply a package and returns only an
expiring, one-use request id for host/player confirmation
(`RbxWorldPackageContracts.cs:444-452`; policy in `Docs/CoreAIMods/WORLD_PACKAGE.md`, "Production
session replacement"). MCP host-admin mod management refuses anything but an unrestricted
composition actor (`Assets/CoreAIMcp/Runtime/Server/CoreAiMcpServer.cs:154-161,468-475`). Mod
management is owner-checked and names the owner in the refusal
(`LuaCsModRuntime.cs:766-786`). Locally-assigned instance ids cannot cross the wire
(`InstanceId.cs:82-99`).

---

## 5. AI-first fitness

**Can an AI agent build a game end-to-end through production tools? Mostly yes for authoring,
no for the online product promise.**

### 5.1 What works

- **Tool surface is complete for authoring.** `execute_lua` (`LuaTool.cs:19,69-90`), `manage_mods`
  with `load/reload/unload/import/forget/revert/list/get_source/export/versions/diagnostics`
  (`Assets/CoreAIMods/Runtime/LuaExecution/LuaModsLlmTool.cs:118,198-249`), `save_world`
  (`RbxWorldPackageContracts.cs:361-395`) and `load_world` (`:416-453`). The same tools are exposed
  over MCP for external agents (`Assets/CoreAIMcp/Runtime/Tools/ExecuteLuaMcpTool.cs:28`,
  `Assets/CoreAIMcp/Runtime/Tools/ManageModsMcpTool.cs:50`).
- **Errors are structured and name the rung.** `RbxErrorCode` has 14 stable SCREAMING_SNAKE wire
  names (`RbxError.cs:9-25,72-88`), and `NotImplemented(feature, phase, workaround)` is the standard
  constructor (`:106-110`). Unimplemented services are registered as stubs carrying their MVP
  (`ServiceCatalog.cs:174-176`: TweenService/CollectionService/Debris → "MVP8"; DataStoreService →
  "MVP9"; UserInputService/ContextActionService → "MVP10"), and unimplemented members are declared
  per class with a workaround hint (`ClassCatalog.cs:406-418`). `RbxApiStubException.NotImplemented`
  does the same for datatypes (`Assets/CoreAIMods/Runtime/RbxApi/Datatypes/RbxApiStubException.cs:27-31`).
  Deliberately-unplanned surfaces are also declared rather than silent
  (`ServiceCatalog.cs:183-186`, PathfindingService/MarketplaceService "no planned MVP").
- **No caller-supplied actor id on any tool.** `save_world`, `load_world` and `manage_mods` all call
  `GetActorContext(_roleId)` and assert `IsTrusted`
  (`RbxWorldPackageContracts.cs:384,439`; `LuaModsLlmTool.cs:207-211`). `mod_id` is caller-supplied
  but is authorized against the actor (`LuaCsModRuntime.cs:766-786`), which closes the ceiling the
  plan recorded at `LuaModsLlmTool.cs:37,60,185`.
- **Mutating tool calls are wrapped in a confirmed pre-mutation autosave.** Every `execute_lua` and
  every mutating `manage_mods` action goes through `ConfirmedWorldMutationGate` with a deterministic
  trigger (`LuaCsGameToolExecutor.cs:144,277-290`; `LuaModsLlmTool.cs:213-225`; policy in
  `Docs/CoreAIMods/WORLD_PACKAGE.md`, "Persistence status").

### 5.2 Gaps

1. **The mutation-envelope tool is undocumented to the model.** When the actor-scoped surface is
   composed, `execute_lua` gains three required parameters — `operation_id`, `target_instance_id`,
   `expected_revision` (`LuaTool.cs:108-117`) — but `ExecuteLuaDescription` is reused verbatim and
   never mentions them, nor how to obtain a target id or read a revision
   (`LuaTool.cs:21-33,75-79`). Nothing in the Rbx skill text explains the protocol either. An agent
   must guess a revision it has no API to read; the failure mode is a stale-revision refusal
   (`InstanceRegistry.cs:279-284`) that reads as a bug rather than as a protocol.
2. **No Lua-visible revision or ownership surface.** `Revision`, `OwnerActorId` and `AccessScope`
   are C# properties (`InstanceRecord.cs:90-107`) with no binding in
   `LuaCsRbxInstanceBindings.BuildMethods`. An agent therefore cannot query "may I write this?" or
   "what revision is this at?" before acting; it can only attempt and read the denial.
3. **The two `execute_lua` surfaces disagree.** In-game may be enveloped; MCP never is
   (`ExecuteLuaMcpTool.cs:57` calls the plain `ILuaExecutor.ExecuteAsync`). An external agent and an
   in-game agent operating on the same world get different concurrency semantics.
4. **The MVP8 stubs an AI agent will hit first are the most common Roblox idioms.** `Debris`,
   `TweenService`, `CollectionService`, `Humanoid`, `Touched`, `Raycast` and `Workspace.Gravity` are
   all unavailable (`ServiceCatalog.cs:174-176`; `ClassCatalog.cs:405-409`). They fail loudly, which
   is right, but a "build me a game" prompt cannot complete without them.
5. **`AIService` is reserved, so a mod cannot drive an agent from Lua**
   (`ServiceCatalog.cs:181-182`). For a product where each player has an AI agent, the in-world
   scripting surface for that agent does not exist yet.

### 5.3 The multiplayer demo

`Assets/CoreAI.Demos/MultiplayerFoundation/` contains a controller, a scenario, a Hub page, an editor
scene builder and `MultiplayerFoundationDemo.unity`. It proves the *authorization* foundation only:
it constructs synthetic actors via `LocalActorIdentityProvider`
(`Assets/CoreAI.Demos/MultiplayerFoundation/Scripts/MultiplayerFoundationDemoScenario.cs:319`),
clamps to 2–20 actors (`:176-179,212`), requires a trusted unrestricted host context (`:205-209`),
and runs cross-actor mod and world refusal proofs plus distinct-chat verification (`:229-236`). It
contains **no** reference to `INetworkBridge`, remotes or `Players` — the networking contract has no
demo, and per the memory rule that every module needs a live-verified demo, MVP11 starts with that
debt already on the books.

---

## 6. Recommended order for the remaining rungs

The plan's dependency order is `verified MVP1+MVP2 → {MVP3 world package, full MVP8} → MVP11 →
MVP12` (`MVP25_ONLINE_PLAN.md` §3), one rung per release. Nothing in the code contradicts that
order, and W3.1–W3.5 appear substantially landed (HEAD commit `e3320bb0` message; `Docs/CoreAIMods/WORLD_PACKAGE.md`
records W3.5 acceptance as still open pending a real browser save→reload run). Two corrections follow
from this audit:

- **Insert a rung-zero.** Three defects are *not* MVP8/11/12 scope but will make each of those rungs
  unjudgeable if carried forward: the ACL lives in the wrong assembly, the mutation envelope has one
  call site, and there is no disconnect path. Fixing them is MVP2 stabilization under §1's P3/P5
  entry gates, and cheap now versus after a transport exists.
- **Do not begin MVP11 until owner decision 1 is made.** It is a hard blocker, not a preference (§6.4).

### 6.0 Rung zero — close the MVP2 entry gates (before MVP8)

| # | First task | Acceptance | Negative twin |
|---|---|---|---|
| 0.1 | Move authorization into the engine-free registry: promote `WorldAclDecision` + `WorldAclAuthorizer` (`LuaCsRbxInstanceBindings.cs:365-481`) into `CoreAI.RbxApi.Instances` beside `InstanceRegistry`, and make `SetAccessControl` (`InstanceRegistry.cs:594-595`) refuse an unauthorized caller. | A test in the engine-free assembly authorizes a write/destroy without referencing `CoreAI.Mods`; the existing Lua-path ACL tests still pass unchanged. | A caller holding only an `InstanceRegistry` reference cannot change `OwnerActorId`/`AccessScope` or destroy another actor's instance; the attempt is refused naming actor and reason. |
| 0.2 | Route **all** production mutation through `ApplyMutation`: give the plain `execute_lua` path and the MCP path a server-generated envelope, or reject them in ACL-versioned worlds. | Every production Lua entry point increments `RetainedMutationOperationCount` (`InstanceRegistry.cs:219-228`) for a world mutation; count > 0 for MCP `execute_lua` too. | A mutation submitted outside an envelope in an ACL-versioned world is refused; a duplicate operation id applies once. |
| 0.3 | Add a disconnect/teardown seam: one method that unregisters the actor from the bridge, fires `PlayerRemoving` once, releases the chat service (`InGameLlmChatServiceFactory.cs:82-106`), kills owned threads and drops rate/signal state (`RbxRemotes.cs:26-41`, `NullNetworkBridge.cs:92-102`). | Connect→disconnect in the loopback fires `PlayerAdded` once and `PlayerRemoving` once; post-teardown `ActorIds`, `_rateWindows`, `_clientSignals` and the actor's scheduler threads are all empty. | A second disconnect is idempotent and fires nothing; a still-connected actor's state is untouched. |

### 6.1 MVP8 — full Players and gameplay services

First tasks, in order:

1. **Player lifecycle over the rung-zero seam** (P8.1). *Acceptance:* solo exposes one synthetic
   client player, server context returns `LocalPlayer == nil`
   (`LuaCsRbxApiBindings.cs:487-492` already satisfies half of this), and `GetPlayers`,
   `TryGetByActorId` and `GetLocalPlayer` agree after add and after remove. *Negative twin:* invoking
   the signal directly does not count; a removed player is not discoverable by any lookup; a
   non-nil server `LocalPlayer` fails.
2. **Humanoid + character adapter at 0.28 m/stud** (P8.2). *Acceptance:* damage, `MoveTo`, death and
   `leaderstats` run through the real controller adapter at both scales. *Negative twin:* unsupported
   states raise their declared loud stub; a repeated `Died` or a host-controller mutation outside the
   adapter fails.
3. **Debris / TweenService / CollectionService** (P8.4) — replace the three stubs at
   `ServiceCatalog.cs:174-176`. *Acceptance:* each performs non-zero work through production
   composition. *Negative twin:* an unsupported tween type fails loudly; a cancelled or destroyed
   tween never later reports success; an untagged object stays absent from tag queries.
4. **Physics services** (P8.3) — `Touched`/`TouchEnded`, `Raycast`, per-body gravity replacing
   `ClassCatalog.cs:405-409`. *Negative twin:* a non-contacting body fires nothing and the host
   scene's global gravity is unchanged.
5. **Corpus to 60%** (P8.5) using the frozen ids already listed in
   `dev-docs/MVP2_ACCEPTANCE_MANIFEST.md` §5 plus the three named MVP8 fixtures. *Negative twin:*
   corrupted twins of kill-brick / touch-pickup / door-tween fail with the expected diagnostic.

### 6.2 MVP11 — authenticated transport

Blocked on owner decision 1. First tasks:

1. **Widen the identity port before writing any socket code.** Add a credential/connection-bearing
   admission method to `IActorIdentityProvider` (`IActorIdentityProvider.cs:9-13`) returning an
   explicit success/failure, keeping `GetActorContext` for the resolved case. *Acceptance:* a valid
   credential yields exactly one durable actor, session, `Player` and server-created `ActorContext`
   before any gameplay access. *Negative twin:* missing, invalid, expired and replayed credentials
   are refused **before** player creation, chat, mods, remotes or world access; anonymous fallback
   fails the gate.
2. **Extend `INetworkBridge` with the things a transport must not be free to omit**: a connection
   handle on inbound messages, a disconnect event, a declared max payload size, and a rate policy.
   *Acceptance:* `NullNetworkBridge` still passes the whole solo manifest after the widening.
   *Negative twin:* a message whose declared sender does not match its connection handle is dropped
   before reaching `EnsureNetworkActor`; a payload over the declared size is refused with
   `PAYLOAD_TOO_LARGE` (the code already exists at `RbxError.cs:22,87` and is currently unused).
3. **Ship `CoreAI.Net.Mirror` as a separate optional assembly** with a `MIRROR` versionDefine,
   modelled on `D:\Git\NeoxiderTools\Assets\Neoxider\Scripts\Network\Neo.Network.asmdef`.
   *Acceptance:* with Mirror present every N11 gate uses the Mirror path and packet counters are
   non-zero. *Negative twin:* with Mirror absent the complete solo manifest still passes through
   `NullNetworkBridge`; a Mirror type appearing in `CoreAI.Core`, `CoreAI.RbxApi.Datatypes` or
   `CoreAI.RbxApi.Instances` fails the existing fitness tests
   (`RbxApiInstancesArchitectureFitnessEditModeTests.cs:87-110`).
4. **RemoteFunction correlation and timeout** (N11.4). *Negative twin:* a forged, replayed, late or
   other-connection response does not complete a request; an unbounded wait fails.
5. **Synchronized server time** (N11.6) replacing `ClassCatalog.cs:410-412`. *Negative twin:* client
   time masquerading as server time fails; the tolerance must be frozen before the run.

### 6.3 MVP12 — filtered, server-authoritative replication

1. **One central inbound intent boundary** — authentication, rate, ACL, revision, envelope — reusing
   the rung-zero registry-level authorizer. *Acceptance:* a permitted intent is rebound to the
   authenticated actor, applies once at the expected revision and emits the authoritative result.
   *Negative twin:* forged actor/owner/role, unauthorized target or property, stale revision,
   duplicate operation id, malformed payload and over-rate intent each leave canonical state
   unchanged.
2. **Implement the filter and bind `NetId`** (`InstanceRecord.cs:74-75`, `InstanceRegistry.cs:690`).
   *Negative twin:* filtered containers and properties stay absent, and later deltas do not bypass
   the filter.
3. **`ClientWritePolicy` behind the authority-resolver seam**, `RobloxParity` default, `Strict`
   rejecting with `NOT_AUTHORITY`. Per owner decision 3, do **not** implement `Open` direct-write
   forwarding. *Negative twin:* a direct client write that changes canonical state, survives
   reconciliation or reaches another client fails.
4. **Move serialization below the bridge** so delta batching is possible: relocate the codec out of
   `CoreAI.Mods` (`LuaCsRbxNetworkCodec.cs:46`) and add per-tick coalescing to replace the
   synchronous per-actor fan-out at `LuaCsRbxApiBindings.cs:660-666`.
5. **Late join from the MVP3 snapshot** (R12.4). *Negative twin:* duplicate, out-of-order or missing
   deltas trigger deterministic ignore or resync, never silent divergence; a second snapshot mapper
   or different ids fails.
6. **Churn and scale last** (R12.6), with budgets frozen before the run.

### 6.4 Owner decisions from §7 that block work

| # | Decision | Blocking status |
|---|---|---|
| 1 | **Authentication owner and credential** | **HARD BLOCKER for MVP11.** `IActorIdentityProvider` cannot be widened without knowing whether the input is a host-signed token, a Mirror authenticator payload or an external IdP assertion (`IActorIdentityProvider.cs:9-13`). Every N11 gate depends on it. Also blocks the reconnect and simultaneous-session policy that `SessionId` exists to serve (`ActorContext.cs:141-142`). |
| 2 | **Mirror packaging** | **BLOCKS the MVP11 assembly layout**, i.e. task 6.2.3. Cheap to decide; the NeoxiderTools pattern is a working precedent. Recommendation stands: keep solo free of a hard Mirror dependency. |
| 3 | **`Open` write policy** | **BLOCKS MVP12 R12.2 design.** Not blocking today because `ClientWritePolicy` does not exist in code at all — the identifier appears only in `Docs/CoreAIMods/ROBLOX_API_ROADMAP.md:158,843,862`. Deciding now avoids building a policy enum that must then be removed. |
| 4 | **Release promise (first authenticated host/client vs a hard 20-client milestone)** | **BLOCKS the MVP11 manifest.** The recorded G10 real-provider result is a failure at 20 actors (`dev-docs/MVP2_ACCEPTANCE_MANIFEST.md` §5.2), and the manifest's own arithmetic shows 12–25 backend lanes would be needed for the current workload. No concurrency number can be promised until the staircase is run. |
| 5 | **Physics and streaming scope** | **BLOCKS MVP12 R12.5 scope**, not its start. Nothing in the code presumes either way; there is no network-ownership surface to remove. |

One further decision the plan does not list but the code now forces: **the guard batch.** The manifest
establishes that batch 4 makes the 4 ms frame gate arithmetically impossible and that batch 256 is
35.1× faster, while noting the measurement was taken on Unity's bundled x86 Mono CLI
(`dev-docs/MVP2_ACCEPTANCE_MANIFEST.md` §8, §9). The observability seam that would let this be
re-measured through production composition is itself listed there as prerequisite work. Any online
frame budget inherits this, so it should be sequenced before, not during, MVP11.

---

## 7. Top findings by risk

1. **Inbound `SenderActorId` is trusted verbatim and creates identity** —
   `Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsRbxApiBindings.cs:653-655,605-627`.
2. **The world ACL is `internal` to the Unity assembly, so no transport can enforce it** —
   `Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsRbxInstanceBindings.cs:374-481`.
3. **The mutation envelope has one production call site** —
   `Assets/CoreAIMods/Runtime/Infrastructure/LuaCsGameToolExecutor.cs:264`; bypassed by plain
   `execute_lua` (`Assets/CoreAIMods/Runtime/LuaExecution/LuaTool.cs:95-105`), MCP `execute_lua`
   (`Assets/CoreAIMcp/Runtime/Tools/ExecuteLuaMcpTool.cs:57`) and mod code
   (`LuaCsRbxInstanceBindings.cs:336-341`).
4. **No disconnect path; `PlayerRemoving` never fires and `UnregisterActor` is dead code** —
   `LuaCsRbxApiBindings.cs:620-623`; `Assets/CoreAIMods/Runtime/RbxApi/Instances/Networking/NullNetworkBridge.cs:92-102`.
5. **Production issues one unrestricted host actor to every role, collapsing all per-actor
   guarantees** — `Assets/CoreAiUnity/Runtime/Source/Composition/CoreServicesInstaller.cs:28-37`,
   with the ACL early-return at `LuaCsRbxInstanceBindings.cs:387-401`.
6. **`PAYLOAD_TOO_LARGE` is declared and never raised; remote payloads have no byte cap** —
   `Assets/CoreAIMods/Runtime/RbxApi/Instances/RbxError.cs:22,87`;
   `Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsRbxNetworkCodec.cs:49-50,114-127`.
7. **`InstanceRegistry._mutationGate` serializes every mutation *and* holds the lock across the whole
   user operation** — `Assets/CoreAIMods/Runtime/RbxApi/Instances/InstanceRegistry.cs:86,290,313-322`;
   revisions bump from raw setters at `Assets/CoreAIMods/Runtime/RbxApi/Instances/RbxInstance.cs:71,93,154-156`.
8. **Global orchestration caps do not scale with actor count**: `MaxPending = 64` shared by all
   actors (`Assets/CoreAI/Runtime/Core/Features/Orchestration/AiOrchestrationQueueOptions.cs:18`),
   `EmergencyMaxThreads = 4096` and `EmergencyMaxMods = 256` global
   (`Assets/CoreAIMods/Runtime/RbxApi/Instances/Scheduling/ModScheduler.cs:29-30`;
   `Assets/CoreAIMods/Runtime/LuaExecution/LuaCsModRuntime.cs:78-80`).
9. **Per-actor state leaks with no eviction**: per-actor remote signals never pruned
   (`Assets/CoreAIMods/Runtime/RbxApi/Instances/Networking/RbxRemotes.cs:10,26-41`) and rate windows
   removed only by the uncalled `UnregisterActor` (`NullNetworkBridge.cs:41-44,101`).
10. **The enveloped `execute_lua` gives an AI three required parameters it is never told about and
    cannot compute** — `Assets/CoreAIMods/Runtime/LuaExecution/LuaTool.cs:21-33,108-117`; no
    Lua-visible revision or ownership surface exists (`InstanceRecord.cs:90-107` has no binding in
    `LuaCsRbxInstanceBindings`).

Honourable mentions (correct, and worth protecting): the engine-free asmdef boundary and its fitness
tests; `load_world`'s confirmation-only contract; the actor-keyed chat factory; subscription-routed
event fan-out; `NOT_IMPLEMENTED` naming the rung.
