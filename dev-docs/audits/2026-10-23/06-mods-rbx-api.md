# CoreAI Rbx API audit

Audited SHA: `69d5bcc37502cd4bb464a3bf9ac0c53b1624b804`. HEAD, main and origin/main were checked locally and matched. Date: 2026-10-23. Model: gpt-6.1-sol; reasoning effort: max.

## Result and boundaries

Eight confirmed source defects: two P1 and six P2. No P0 is established. All eight are new relative to the relevant existing `TODO.md` entries inspected; root should deduplicate against the other current audits before creating tracker items. These are source findings, not runtime reproductions or test-pass claims.

The review prioritizes correctness, failure containment, lifecycle/disposal, ordering, bounded resources, mutation authority and configurable runtime seams. The human explicitly authorized this report despite AGENTS.md's usual no-report rule. Only this report was written. No source, tests, assets, PLAN/TODO files, git index or shared build outputs were changed by this auditor. The preexisting untracked ETFX metadata file was not touched. Root owns git.

Report 05 owns the general Lua VM and mod lifecycle; report 07 owns downleveling; report 10 owns Mirror transport. This report owns the Rbx-specific binding, Domain, codec and Unity defects below. References to those other areas describe call paths, not independent audits of their internals.

## Exact coverage

“Reviewed” below means source inspection of the named behavior, with selected ranges where stated. File enumeration, method-name search and source grep are not execution or full-file review.

### Instructions, entry documentation and assembly boundaries

Read `AGENTS.md`, `Docs/ARCHITECTURE_RULES.md`, `Assets/CoreAIMods/README.md`, `Assets/CoreAIMods/Runtime/RbxApi/Instances/README.md`, `Assets/CoreAI/Docs/RBX_API.md` (capabilities, conversions, clocks, classes and mutation-envelope sections), `dev-docs/REPLICATION_PHASE0.md`, and the relevant Rbx backlog/verification entries of `TODO.md`. Read the opening runtime/material contracts of `Assets/CoreAIMods/Runtime/RbxApi/Unity/TEXTURE_MATERIALS.md` and `PROCEDURAL_MATERIALS.md`. Read portable-runner usage/limitations in `tools/portable/LuaTests/README.md` and inspected fixture inclusions in both portable test csprojs.

Read all four exact asmdefs:

- `Assets/CoreAIMods/Runtime/RbxApi/Datatypes/CoreAI.RbxApi.Datatypes.asmdef`
- `Assets/CoreAIMods/Runtime/RbxApi/Instances/CoreAI.RbxApi.Instances.asmdef`
- `Assets/CoreAIMods/Runtime/RbxApi/Binding/CoreAI.RbxApi.Binding.asmdef`
- `Assets/CoreAIMods/Runtime/RbxApi/Unity/CoreAI.RbxApi.Unity.asmdef`

Datatypes has no assembly references; Instances references Datatypes only; both declare `noEngineReferences: true`. Unity references Datatypes. Binding references Core, Instances, Datatypes, Unity and InputSystem. This is a static boundary observation, not a compilation result. The Unity skill's file-only review guidance was used; its development/delegation workflow was superseded by the explicit self-audit restrictions.

### Runtime implementation reviewed

Files are listed explicitly. A bare filename following a full path uses that path's directory; test paths below are relative to the stated Tests root. No wildcard implies full coverage.

- Datatypes, arithmetic and clock implementations: `Assets/CoreAIMods/Runtime/RbxApi/Datatypes/RbxVector3.cs`, `RbxVector2.cs`, `RbxCFrame.cs`, `RbxColor3.cs`, `RbxUDim.cs`, `RbxRandom.cs`, `RbxTweenInfo.cs`, `IRbxClockSource.cs` and `IRbxMaterialProvider.cs`, all under that exact Datatypes directory.
- Hierarchy/lifecycle: `Assets/CoreAIMods/Runtime/RbxApi/Instances/RbxInstance.cs`, `RbxScriptSignal.cs`, `ModConnectionRegistry.cs`, `WorldAcl.cs`, `RbxDataModel.cs`, `DataModelBootstrap.cs`, `ServiceCatalog.cs`, `RbxMaterialService.cs`, `RbxMaterialVariant.cs` and `RbxClickDetector.cs`, all under that exact Instances directory. `InstanceRegistry.cs` was reviewed for construction/admissions, mutation/replay/envelope handling, revision delivery, create/restore, access control, scene membership and destruction (lines 1–926, 1002–1292, 1374–1467 and 1644–1805). Attribution and net/name lookup helpers outside those ranges were indexed, not audited in full.
- Physics: `Assets/CoreAIMods/Runtime/RbxApi/Instances/RbxRaycast.cs` and `RbxWorldPhysics.cs`, specifically filters, port contract, ray validation/forwarding, contact/teleport accounting and unregistration. The raycast call path was traced end to end.
- Tween paths: `Assets/CoreAIMods/Runtime/RbxApi/Instances/RbxTween.cs` (caller, creation/control, stepping/interpolation and terminal behavior) and `RbxTweenService.cs` (creation/ACL, host attachment, control authorization, conflict cancellation, goal validation, heartbeat writes, fault/drop/target cleanup and idle retention). `RbxDebris.cs` was searched for admission, timer ownership and destroy authorization; its heap implementation was not reviewed in full.
- Networking: `Assets/CoreAIMods/Runtime/RbxApi/Instances/Networking/RbxRemotes.cs` and `RbxNetworkRateLimiter.cs`. `RbxPlayers.cs` was reviewed only for signal construction, character signal accessors, registry subscriptions and unregistration/leave cleanup (not the full admission/profile/character state machine).
- Replication: `Assets/CoreAIMods/Runtime/RbxApi/Instances/Replication/ReplicationFilter.cs`, `ReplicationDirtySet.cs`, `ReplicationStream.cs`, `ReplicationMembers.cs`, `MutationIntent.cs`, `IntentGateway.cs`, `WriteGrant.cs` and `WriteGrantLedger.cs`. In the same directory, `ReplicationApplier.cs` was reviewed through batch sequencing, violation/resync handling, spawn/hydration, reconciliation, removal and patch dispatch (lines 1–870); its remaining member/reference-resolution helpers were indexed rather than fully reviewed.
- Scheduling: `Assets/CoreAIMods/Runtime/RbxApi/Instances/Scheduling/ModScheduler.cs`, specifically configuration/quotas and the reschedule, delay, signal queue, cancel/kill, phase pipeline/drain, dispatch, retirement and stale-work compaction paths (lines 1378–1544, 1639–1720, 1799–2149 and 2981–3102, plus method/constant indexing). Also read `Scheduling/IRbxTimeSource.cs` and `Scheduling/RbxSchedulerCompletion.cs`. The full scheduler is not claimed audited.
- Unity binding: `Assets/CoreAIMods/Runtime/RbxApi/Binding/InstanceGameObjectBinder.cs` (adoption, reverse lookup, materialization, hierarchy, transform/property sinks, tombstones, shape creation and teardown), `RbxWorldHost.cs`, `WorldInstanceAdapter.cs`, `UnityRbxPhysicsPort.cs`, `UnityClickPickSource.cs` and `UnityRbxCharacterMotor.cs`.
- Unity material/space boundary: `Assets/CoreAIMods/Runtime/RbxApi/Unity/RbxSpace.cs` and `RbxTextureMaterialProvider.cs`. `RbxProceduralMaterialProvider.cs` was reviewed for shared allocation/fallback/reset behavior; `RbxMaterialTextureCatalog.cs` for catalog merge and entry contracts. Catalog definitions, rendering shader math and texture assets were not audited.
- Lua-facing paths: `Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsRbxInstanceBindings.cs` (context/proxy lifetime, capability/ACL/envelope guards, member dispatch, clone/destroy, tween, humanoid and remote methods, sink copying, and MaterialVariant access); `LuaCsRbxValues.cs` (proxy/value wrappers, exception conversion and argument helpers); `LuaCsRbxDatatypeBindings.cs` (UDim constructors/Lerp/conversion and signal connect/marshal/rebinding); `LuaCsTweenPropertyHost.cs` (sample and direct-write paths); `LuaCsRbxApiBindings.cs` (registration, signal-handler ownership, encode/send, receive/admission, unregister cleanup, callback scheduling and disposal); `LuaCsRbxNetworkCodec.cs` (entire encoder/decoder).
- Surrounding call paths only: `Assets/CoreAIMods/Runtime/Infrastructure/LuaCsGameToolExecutor.cs` (actor/envelope overloads, lines 524–680), `LuaCsGameplayBindings.cs` (Rbx registration forwarding), `Assets/CoreAIMods/Runtime/LuaExecution/LuaCsModRuntime.cs` (`mods_call` plain-data copying and callee actor envelope, lines 4572–4637), and `Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsApiRegistry.cs` (registered native-function invocation). These integrations are read-only.

### Tests reviewed

Under `Assets/CoreAIMods/Tests/`:

- `EditMode/RbxApi/Datatypes/RbxDatatypeMiscEditModeTests.cs`: UDim construction/rounding and enum contracts; `EditMode/RbxApi/LuaBindings/RbxApiLuaBindingsEditModeTests.cs`: UDim offset range/coercion and datatype test cases.
- `EditMode/RbxApi/Instances/R6_5_CloneEditModeTests.cs`: base-property, archivable, backing-copy and failure cleanup cases; remaining clone cases indexed. `R6_2_DestroyEditModeTests.cs`, `InstanceHierarchyEditModeTests.cs` and `RbxScriptSignalEditModeTests.cs` in that directory were searched/indexed for hierarchy depth, signal ordering, destruction and pruning coverage.
- `EditMode/RbxApi/LuaBindings/RbxMaterialVariantLuaBindingsEditModeTests.cs`: create/write/source/clear contracts. `EditMode/RbxApi/Unity/RbxMaterialVariantRenderingEditModeTests.cs`: single-source cache reuse, live mutation, source arrival and real binder rendering cases.
- `EditMode/RbxApi/Acceptance/Mvp8PhysicsEditModeTests.cs`: ray validation, result/filter tests and fake port. `PlayMode/RbxApi/Mvp8PhysicsPlayModeTests.cs`: ray geometry, cap, filter and buffer-growth cases (lines 159–225). `EditMode/RbxApi/Binding/InstanceGameObjectBinderEditModeTests.cs`: ray helper (lines 65–95); shape/storage/physics coverage indexed.
- `EditMode/RbxApi/Networking/NetworkBridgeEditModeTests.cs`: server event scheduling/player argument and transport budget tests. `EditMode/RbxApi/LuaBindings/RbxSignalConnectionTeardownEditModeTests.cs`: production frame setup, unload/reload and proxy/property-signal destruction tests, particularly lines 380–623.
- `EditMode/RbxApi/Acceptance/RungZeroEnvelopeEditModeTests.cs`: explicit replay, bare-call refusal, codec size checks and hidden-reference test structure. `RungZeroEnvelopeProductionPathsEditModeTests.cs` in that directory: Tween WorldEdit/cross-actor checks, lines 255–338.
- `EditMode/RbxApi/Acceptance/Mvp8TweenServiceEditModeTests.cs`: failure/ACL/clone-related test names indexed, not the entire fixture reviewed.
- `EditMode/RbxApi/LuaBindings/RbxJsonContractEditModeTests.cs`: repeated-key allocation checks, encoding/decoding error paths and aggregate-entry limit cases, particularly lines 795–923.
- `EditMode/RbxApi/Replication/InstanceRegistryAuthorityEditModeTests.cs`: revision notification, gate release and throwing-subscriber cases. `ReplicationStreamPlanEditModeTests.cs` in that directory: initial spawn/member/order cases (lines 1–110). `EditMode/RbxApi/Networking/IntentGatewayEditModeTests.cs`: grants, sender identity, payload and rate admission cases (lines 1–170).

Exclusions: all unlisted runtime/test files and unlisted portions of the large files above were not deeply reviewed. In particular, this is not exhaustive review of Humanoid/Players/character factory, value-object serialization, collection/tag indexing, every scheduler state transition, camera/input adapters, HTTP policy/JSON service, every datatype enum/easing rule, the compatibility corpus, shader/asset quality, world-package restore, Mirror or general VM/lifecycle. No standalone audit-report or PLAN/TODO changes outside this owned report were made.

## Confirmed actionable findings

### RBX-001 — P1 — Unity raycasts square the requested range — new

**Evidence/call chain.** `Assets/CoreAIMods/Runtime/RbxApi/Binding/UnityRbxPhysicsPort.cs:90` multiplies the unnormalized direction by its length in metres; `:92` takes that product's magnitude and `:114` passes it as Unity's maximum distance. `Assets/CoreAIMods/Runtime/RbxApi/Unity/RbxSpace.cs:160` only flips Z, without normalization. A direction of length L therefore queries L² studs.

The Lua method forwards the vector at `Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsRbxInstanceBindings.cs:1873`. `Assets/CoreAIMods/Runtime/RbxApi/Instances/RbxWorldPhysics.cs:191` rejects non-finite/zero/over-15,000-stud input, then `:213` passes that vector unchanged. The result at `:227` has no endpoint check. [Roblox's official raycasting guide](https://create.roblox.com/docs/workspace/raycasting) defines the range by direction magnitude.

**Trigger/impact.** In an otherwise empty world, request 10 studs toward a part whose near face is 15 studs away: the port searches 100 studs and accepts it. A 0.5-stud request searches 0.25 studs and misses eligible geometry. The 15,000-stud input cap becomes a 225,000,000-stud engine query. Built-player and WebGL gameplay use this adapter.

**Existing coverage.** `Assets/CoreAIMods/Tests/PlayMode/RbxApi/Mvp8PhysicsPlayModeTests.cs:159` tests a target inside the requested 40 studs, `:190` only the Domain cap, and `:203` an accepted target inside 100 studs. The fake at `Assets/CoreAIMods/Tests/EditMode/RbxApi/Acceptance/Mvp8PhysicsEditModeTests.cs:704` cannot exercise the conversion. None establishes correct endpoints.

**Fix/regression.** Convert the displacement once, e.g. `RbxSpace.ToUnity(directionStuds)`, then derive magnitude and normalized direction. Use the real port to test a sole target beyond the endpoint, an in-range sub-one-stud target, and a diagonal ray, at 0.28 and 1 metre per stud.

**Confidence:** high. Complete source call chain plus a small arithmetic probe; no Unity physics reproduction.

### RBX-002 — P2 — UDim2 interpolation overflows valid signed offsets — new

**Evidence/guards.** `Assets/CoreAIMods/Runtime/RbxApi/Datatypes/RbxUDim.cs:106` and `:108` subtract two `int` offsets before promotion. `Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsRbxDatatypeBindings.cs:1093` admits any finite truncated 32-bit offset, and `:1030` forwards Lerp directly to that implementation.

**Trigger/impact.** `UDim2.fromOffset(-1073741824, 0):Lerp(UDim2.fromOffset(1073741824, 0), 0.5)` should have midpoint offset zero; unchecked subtraction becomes -2,147,483,648 and the result is -2,147,483,648. Both endpoints and the mathematical result fit the supported range. Checked compilation would throw instead of interpolate. The same defect affects Y and the UDim2 tween interpolation path at `Assets/CoreAIMods/Runtime/RbxApi/Instances/RbxTween.cs:578`. The official [UDim2 source reference](https://raw.githubusercontent.com/Roblox/creator-docs/main/content/en-us/reference/engine/datatypes/UDim2.yaml) specifies independent linear interpolation of each axis.

**Existing coverage.** `Assets/CoreAIMods/Tests/EditMode/RbxApi/Datatypes/RbxDatatypeMiscEditModeTests.cs:129` covers offsets 0 to 11/5. `Assets/CoreAIMods/Tests/EditMode/RbxApi/LuaBindings/RbxApiLuaBindingsEditModeTests.cs:929` checks constructor bounds, not arithmetic overflow.

**Fix/regression.** Promote operands before subtraction and retain an explicit representable-result/rounding policy. Add engine-free and Lua midpoint cases for ±2³⁰ on both axes, alpha 0/1, reversed endpoints and the normal rounding case.

**Confidence:** high. A float32/int32 arithmetic probe reproduced the exact calculation; CoreAI itself was not executed.

### RBX-003 — P2 — Script-authored variant materials are retained without a lifetime bound — new

**Evidence/call chain.** `Assets/CoreAIMods/Runtime/RbxApi/Unity/RbxTextureMaterialProvider.cs:68` is a static dictionary; each new variant key allocates a native Material at `:215` and retains it at `:220`. Those materials carry `HideAndDontSave` at `:598`. The only dictionary/material clearing implementation is `ResetSharedCacheForTests` at `:401`; the call-site search found only tests.

Lua variant writes are permitted at `Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsRbxInstanceBindings.cs:3538`; part variant assignment reaches the sink at `:3059`. `Assets/CoreAIMods/Runtime/RbxApi/Binding/InstanceGameObjectBinder.cs:1691` requests the material. Its part destruction at `:649` removes part properties, not provider cache entries; host teardown at `Assets/CoreAIMods/Runtime/RbxApi/Binding/RbxWorldHost.cs:218` also releases no provider cache. [Unity documents explicit destruction for HideAndDontSave materials](https://docs.unity3d.com/ScriptReference/HideFlags.HideAndDontSave.html).

**Trigger/impact.** In a normal composition permitting material editing, reuse one part and a MaterialVariant, give the variant successive unique names, assign each name to the part, then destroy both. Every successful textured lookup leaves another material/key alive for the process lifetime, including across world replacements. Live-instance quotas and the 2,048-part tombstone bound do not bound this cache. Growth is source-established; native memory consumption was not measured.

**Existing coverage.** `Assets/CoreAIMods/Tests/EditMode/RbxApi/Unity/RbxMaterialVariantRenderingEditModeTests.cs:118` tests same-name reuse and distinct names; `:135` tests mutation in place. Setup/teardown at `:38`/`:57` resets the static cache, hiding production lifetime retention. The existing TODO about eagerly loading the finite packaged catalog is a different issue.

**Fix/regression.** Give variant materials an explicit world/provider owner and release/eviction policy that preserves handles still used by renderers. Test repeated rename/assign/destroy cycles with bounded live materials, plus scene/world teardown, without invoking a test-only reset as the behavior under test.

**Confidence:** high for retention and missing production release; device exhaustion threshold unmeasured. Requires a valid textured catalog/shader and permission to create/edit variants.

### RBX-004 — P2 — Independent worlds mutate the same cached variant material — new

**Evidence/guard gap.** The cache at `Assets/CoreAIMods/Runtime/RbxApi/Unity/RbxTextureMaterialProvider.cs:68` is process-wide, whereas `VariantSource` at `:125` is per provider. The key's equality at `Assets/CoreAIMods/Runtime/RbxApi/Datatypes/IRbxMaterialProvider.cs:34` contains only material name/value/variant text. A second source resolves the same key at `RbxTextureMaterialProvider.cs:183`, overwrites the existing material at `:198`, and returns it at `:204`.

**Trigger/impact.** Compose two binders/providers at the same stud scale. World A defines Brick variant “SharedName” with texture A; world B defines that name with texture B. Query A, then B. Both renderers hold the same Material, so B changes A's appearance. Subsequent writes can flip both. No threading is needed. The documented per-world `IRbxMaterialVariantSource` seam cannot isolate its configuration, contrary to architecture §2.1.

**Existing coverage.** `Assets/CoreAIMods/Tests/EditMode/RbxApi/Unity/RbxMaterialVariantRenderingEditModeTests.cs:118` distinguishes two names within one source; `:135` deliberately mutates one source's handle. There is no two-provider/different-source assertion in that fixture.

**Fix/regression.** Scope mutable variant entries to the source/provider/world; immutable plain-material sharing can stay separate. Test two actual providers with the same name and different maps/tiling, retain both returned handles, edit one source, and verify the other handle's texture/scale remains unchanged. Coordinate lifetime fixes with RBX-003, but this is a separate isolation defect.

**Confidence:** high, from the complete key/lookup/mutation path. No rendering execution or image comparison.

### RBX-005 — P2 — Clone drops ClickDetector and MaterialVariant properties — new

**Evidence/call chain.** `Assets/CoreAIMods/Runtime/RbxApi/Instances/RbxInstance.cs:679` delegates subclass copying to `CopyCustomStateTo`; the base hook at `:1182` is empty. `Assets/CoreAIMods/Runtime/RbxApi/Instances/RbxClickDetector.cs:11` and `RbxMaterialVariant.cs:9` define stateful subclasses without overriding it. Their factories are registered at `Assets/CoreAIMods/Runtime/RbxApi/Instances/ClassCatalog.cs:651` and `:692`.

Lua Clone at `Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsRbxInstanceBindings.cs:1374` uses that method; its later `CopyPartSinkState` at `:2524` only copies sink properties. No layer copies these fields. [Roblox's official Instance reference source](https://raw.githubusercontent.com/Roblox/creator-docs/main/content/en-us/reference/engine/classes/Instance.yaml) defines Clone as a copy of the instance and archivable descendants.

**Trigger/impact.** Clone a ClickDetector set to distance 8: its copy reads default 32. Clone a MaterialVariant with Brick base, custom maps and tile width 4: its copy reads Plastic, empty maps and tile width 1. This also occurs when cloning their containing hierarchy; templates silently change interaction range/appearance. The snapshot serializer has explicit MaterialVariant state, so no save/load loss is asserted here.

**Existing coverage.** `Assets/CoreAIMods/Tests/EditMode/RbxApi/Instances/R6_5_CloneEditModeTests.cs:27` and `:83` cover base metadata and part backing state; `Assets/CoreAIMods/Tests/EditMode/RbxApi/LuaBindings/RbxMaterialVariantLuaBindingsEditModeTests.cs:173` covers writing and reading the original, without Clone.

**Fix/regression.** Implement silent custom-state copying for these two classes, following existing Humanoid/value/model hooks. Clone a folder containing both through C# and Lua; compare every implemented writable property, fresh identities, nil root parent, no copied connections and no source mutation.

**Confidence:** high; constructors, factory, clone hook and Lua post-copy path inspected. No executable clone probe.

### RBX-006 — P2 — Destroy does not disconnect RemoteEvent's event connections — new

**Evidence/call chain.** `Assets/CoreAIMods/Runtime/RbxApi/Instances/Networking/RbxRemotes.cs:19` creates `OnServerEvent` directly; `:31` creates per-actor client signals outside the base table. Base Destroy at `Assets/CoreAIMods/Runtime/RbxApi/Instances/RbxInstance.cs:753` calls `DisconnectSignals` at `:1145`, which visits only the base/attribute tables. `RbxRemotes.cs:96` removes an actor's client signal only when explicitly called.

Lua obtains these signals at `Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsRbxInstanceBindings.cs:1083`; connect/track at `LuaCsRbxDatatypeBindings.cs:1468`/`:1474` retains the connection. `LuaCsRbxApiBindings.cs:2545` unregister cleanup removes RemoteFunction callbacks and part/motor state, without disconnecting a RemoteEvent. `Assets/CoreAIMods/Runtime/RbxApi/Instances/ModConnectionRegistry.cs:101` and `:206` track/prune connections, retaining them while `Connected` is true. This violates the [official Destroy connection-disposal contract](https://raw.githubusercontent.com/Roblox/creator-docs/main/content/en-us/reference/engine/classes/Instance.yaml).

**Trigger/impact.** A loaded mod creates a RemoteEvent, connects `OnServerEvent`, saves the connection, and destroys the remote. `connection.Connected` remains true; repeating connect/destroy keeps signal/callback entries live until explicit disconnect or mod teardown. Captured objects can also remain retained. UnreliableRemoteEvent inherits the same implementation. Existing incoming-event registry lookup at `LuaCsRbxApiBindings.cs:2458` refuses a destroyed remote, so this finding does **not** claim that ordinary network packets continue delivery after Destroy.

**Existing coverage.** `Assets/CoreAIMods/Tests/EditMode/RbxApi/Networking/NetworkBridgeEditModeTests.cs:219` tests event dispatch, while `Assets/CoreAIMods/Tests/EditMode/RbxApi/LuaBindings/RbxSignalConnectionTeardownEditModeTests.cs:541`/`:594` cover property-signal destruction. Actor-disconnect tests exercise another cleanup trigger.

**Fix/regression.** Register event signals in the Instance-owned teardown path or provide a subclass teardown hook. Test both event classes, server and per-actor client connections, direct and ancestor Destroy, idempotent Destroy and pending-invocation preservation. Assert false `Connected` and bounded connection-ledger retention.

**Confidence:** high. Complete destruction and surrounding unregister paths inspected; no Lua/VM test was executed.

### RBX-007 — P2 — Tween methods ignore an explicit envelope's target — new

**Evidence/call chain.** `Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsRbxInstanceBindings.cs:1530` checks only WorldEdit for TweenService:Create; `:1572`, `:1578` and `:1584` do so for Play/Pause/Cancel. None calls `RequireMutationTarget` (`:587`), and none appears in `IsMutatingMethod` (`:1924`). `Assets/CoreAIMods/Runtime/RbxApi/Instances/RbxTweenService.cs:128`/`:333` check actor ownership, without checking the binding's target constraint.

The public explicit-envelope executor at `Assets/CoreAIMods/Runtime/Infrastructure/LuaCsGameToolExecutor.cs:635` validates the envelope's target revision, then registers it at `:648`. `Assets/CoreAIMods/Runtime/Infrastructure/LuaCsGameplayBindings.cs:224` forwards it; `LuaCsRbxApiBindings.cs:3331` creates the envelope-bound context. That context would reject ordinary writes to any other target. Later tween ticks call `Assets/CoreAIMods/Runtime/RbxApi/Instances/RbxTween.cs:476`, reaching the direct sink writer at `Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsTweenPropertyHost.cs:233`.

**Trigger/impact.** Give an actor two writable parts A and B. Execute code through the explicit-envelope overload with a valid envelope for A, but create/play a tween of B. The call passes actor ACL and schedules B's mutation despite validating only A's revision. The envelope's one-target/optimistic-concurrency constraint is bypassed. This is **not** an established cross-actor ownership bypass, and there is no claim that a live Mirror intent route currently reaches it. A raw binding context without an envelope has a related gap, but the ordinary actor-scoped executor supplies an envelope; its plain ACL-world overload refuses an untrusted resolver before executing.

**Existing coverage.** `Assets/CoreAIMods/Tests/EditMode/RbxApi/Acceptance/RungZeroEnvelopeEditModeTests.cs:66` tests explicit replay on the correct target. `RungZeroEnvelopeProductionPathsEditModeTests.cs:255` and `:304` test Tween WorldEdit and caller ownership, not a mismatched target.

**Fix/regression.** Apply the context's envelope/target guard to the tween's target before creation/control and before committing scheduled behavior; define which target an explicit control envelope names. Through the real executor, use A's envelope to attempt creation/control on B, assert refusal and no scheduled mutation/extra Tween, then verify a correctly targeted call succeeds and duplicate operations remain idempotent.

**Confidence:** high for the omitted target check and reachable public overload. P2 reflects preserved actor ownership and the currently unwired client-intent transport. Rbx binding owner: this report; executor lifecycle cross-reference: report 05.

### RBX-008 — P1 — Outbound byte quota is enforced after unbounded serialization — new

**Evidence/call chain.** `Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsRbxNetworkCodec.cs:338` fully serializes the graph; `:339` allocates the full UTF-8 array; only `:340` checks the 65,536-byte limit. `WriteEnvelope` at `:1078` uses an unbounded StringWriter, writes the full graph at `:1084`, then copies it to a string at `:1086`.

`Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsRbxApiBindings.cs:2126` encodes before sending to the bridge; FireClient/FireAllClients and RemoteFunction responses also use this codec. The synchronous function wrapper at `Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsRbxValues.cs:82` has no intermediate cancellation/preallocation guard. Incoming size checks at `LuaCsRbxNetworkCodec.cs:383` are correctly early and do not guard this outbound path.

**Trigger/impact.** Send an array of 4,096 references to one 32,768-character ASCII string. It uses 4,097 counted entries, depth one and no cycle, but expands to at least 134,217,728 JSON content bytes before the 65,536-byte rejection. StringWriter/string/UTF-8 representations add further allocations. A script can force a large synchronous main-thread allocation and stall or exhaust a built player/WebGL heap before the quota or later VM accounting can respond. The allocation ordering is confirmed; an actual OOM, device crash or timing was not induced/measured.

**Existing coverage.** `Assets/CoreAIMods/Tests/EditMode/RbxApi/Acceptance/RungZeroEnvelopeEditModeTests.cs:245` checks eventual rejection of one oversized string. `Assets/CoreAIMods/Tests/EditMode/RbxApi/LuaBindings/RbxJsonContractEditModeTests.cs:795` checks allocations for a valid under-limit graph; `:876` covers cycles/aggregate count. Neither establishes bounded oversized-output work.

**Fix/regression.** Enforce the escaped UTF-8 byte budget while writing, including keys, delimiters and tags; stop as soon as the limit is crossed, with bounded buffering. Keep the depth/entry/cycle checks. Add an allocation-bound regression using a modest repeated-string graph, an escaped/multibyte boundary case and a spy bridge proving nothing is sent. Do not run an OOM-sized benchmark.

**Confidence:** high for late guard/amplification; crash threshold is an inferred consequence. Rbx codec owner: this report; VM-budget and transport-admission context belongs to reports 05/10.

## Existing TODOs and compatibility limits

These are recorded limits, not additional new findings:

- `TODO.md:996`, `:1846` and `dev-docs/REPLICATION_PHASE0.md`: world-state replication and IntentGateway transport/composition remain Phase 0. The inspected engine-free planner/applier does not establish socket-level join, resync, geometry/camera patches or production client authority. The known replica Player-minting composition gap is also already tracked.
- `TODO.md:1001`: weak keyed-signal re-homing is known; `:995` tracks destruction AncestryChanged argument validation; `:992` tracks tag cap enforcement outside the Instance API.
- `TODO.md:1105`/`:1109`: exposed remote-thread budget configuration and all-senders admission bounds remain known gaps. This review observed scheduler signal-generation/queue/owner limits and timer compaction, not multiplayer load guarantees.
- `TODO.md:1037`: Vector3.Lerp extreme-float overflow is already tracked; RBX-002 instead concerns integer-offset subtraction. `TODO.md:2133` records the finite eager material-catalog load; RBX-003 concerns an ever-growing script-authored variant cache.
- `Docs/ARCHITECTURE_RULES.md` §2 explicitly identifies process-wide RbxSpace scale as existing architectural debt. RBX-004 occurs even with the same scale in both worlds.
- The package uses Lua 5.2/double-only VM semantics with Luau rewriting, a modeled subset of Roblox classes/services and loud stubs. Minimal characters omit avatar rigs/animation/appearance. Mouse hover events and several GUI/physics/platform surfaces are documented backlog/unsupported behavior. No full Roblox/Luau or built-player parity claim is supported by this audit.
- Runtime space conversion and resource-backed material creation use no AssetDatabase primary path. A grep for threading/blocking/editor/file APIs under RbxApi found only a comment explaining an avoided editor-only URP API. This does not establish IL2CPP/AOT/WebGL success, shader inclusion or runtime performance. This slice writes no files; persistence Sync behavior was not exercised.

## Hypotheses and missing validation

No additional hypothesis is promoted to a defect:

- Cross-registry parenting lacks an obvious identity guard in the inspected Parent pipeline. Reachability through safe production composition and intended host-level contract were not established; multi-world misuse needs a focused test.
- Tween writes after an ACL change, exact CFrame Euler singularities/degenerate matrices, extremely small world scale and exceptional custom-binder behavior need policy/behavior tests before assigning further findings.
- Full signal cancellation/resumption, native resource reclamation, shutdown subscription cleanup and packet ordering across devices remain unexecuted. The inspected source guards are evidence of implementation intent only.
- Existing tests may reveal other failures when root runs them. No finding is dismissed because a test name exists.

## Exact checks actually run and output

1. `git rev-parse HEAD`, `git rev-parse main`, `git rev-parse origin/main`: all returned `69d5bcc37502cd4bb464a3bf9ac0c53b1624b804`.
2. Initial `git status --short`: untracked `Assets/Epic Toon FX/Upgrade/ETFX 2019.4.24f1 URP Upgrade.unitypackage.meta` and `dev-docs/audits/` already existed. A later `git diff --name-only` showed `PLAN.md` and `dev-docs/README.md` changing in the shared workspace; this auditor did not write them.
3. Read-only `rg --files`, `rg -n`, `Get-Content`, `Get-ChildItem` and Python `pathlib.read_text` inspections supplied the coverage above. Some exploratory rg commands used nonexistent executor paths/PowerShell literal glob paths and exited 1 with file-not-found/invalid-path messages; subsequent searches used discovered exact paths. No runtime conclusion depends on those failed searches.
4. Exact safe in-memory arithmetic probe (Python standard library only, no payload/fixture/generated file):

```powershell
python -c "import math,struct,ctypes; f=lambda x:struct.unpack('f',struct.pack('f',x))[0]; print('Ray range algebra:'); [print(f'L={l:g}, scale=.28, adapter distance studs={f(f(l)*f(f(l)*f(.28)))/f(.28):.9g}, expected={l:g}') for l in (.5,10,15000)]; a=-(2**30); b=2**30; delta=ctypes.c_int32(b-a).value; got=int(round(f(f(a)+f(f(delta)*f(.5))))); print(f'UDim2 offset: a={a}, b={b}, alpha=.5, unchecked_int_delta={delta}, implemented={got}, mathematical=0')"
```

Observed output:

```text
Ray range algebra:
L=0.5, scale=.28, adapter distance studs=0.25, expected=0.5
L=10, scale=.28, adapter distance studs=99.9999996, expected=10
L=15000, scale=.28, adapter distance studs=224999999, expected=15000
UDim2 offset: a=-1073741824, b=1073741824, alpha=.5, unchecked_int_delta=-2147483648, implemented=-2147483648, mathematical=0
```

This emulates the relevant arithmetic, not CoreAI/Unity execution. A second arithmetic-only command was run:

```powershell
python -c "n=4096; s=32768; print(f'Outbound expansion lower bound: {n} references * {s} ASCII characters = {n*s} JSON content bytes; aggregate entries = {n+1}; payload limit = 65536; no payload materialized.')"
```

Output: `Outbound expansion lower bound: 4096 references * 32768 ASCII characters = 134217728 JSON content bytes; aggregate entries = 4097; payload limit = 65536; no payload materialized.`

5. Official primary documentation was opened through the web tool on this audit date for ray range, UDim2 interpolation, Instance Clone/Destroy and Unity material lifetime; the direct links are beside the relevant findings. No external benchmark/model/server was called.
6. Final report reread completed, including reconsideration of failure paths and limiting guards. `git diff --check -- dev-docs/audits/2026-10-23/06-mods-rbx-api.md`: exit 0, empty output. Because this report is untracked, that command alone does not validate its added text. Supplemental `git diff --no-index --check -- NUL dev-docs/audits/2026-10-23/06-mods-rbx-api.md`: exit 1, empty diagnostics, with the new file compared to NUL; not reported as a passing tracked diff. The Python report-integrity check returned exit 0: eight unique finding headings, 43 fully qualified source/line references checked with zero nonexistent paths/out-of-range lines, zero trailing-whitespace lines and zero Cyrillic characters. Final scoped `git status --short -- dev-docs/audits/2026-10-23/06-mods-rbx-api.md 'Assets/Epic Toon FX/Upgrade/ETFX 2019.4.24f1 URP Upgrade.unitypackage.meta'` lists those two untracked files. `git diff --name-only -- Assets/CoreAIMods` returned no paths; final `git rev-parse HEAD` still matched the audited SHA.

## NOT RUN

No build or test suite; root coordinates all shared output. No Unity Editor/MCP, PlayMode, Mono/IL2CPP player, WebGL/browser, device, actual physics/material rendering, two-process network integration, socket fault/reorder, load/backpressure, native-memory profiler or performance check. No real LLM/model benchmark, install, fetch, checkout, staging, commit or server/service operation.

## Focused checks for root, serially

These are suggestions, not checks run by this auditor. Existing tests are baselines; new regressions specified above are required to demonstrate the defects and fixes. Reuse root's artifact strategy and run one command at a time.

```powershell
dotnet test tools/portable/Tests/CoreAI.Portable.Tests.csproj -c Release --artifacts-path artifacts/portable --filter "FullyQualifiedName~RbxDatatypeMiscEditModeTests|FullyQualifiedName~InstanceRegistryAuthorityEditModeTests|FullyQualifiedName~ReplicationStreamPlanEditModeTests|FullyQualifiedName~IntentGatewayEditModeTests" --logger "trx;LogFileName=audit06-rbx-domain.trx"
dotnet test tools/portable/LuaTests/CoreAI.Portable.LuaTests.csproj -c Release --artifacts-path artifacts/portable-lua --filter "FullyQualifiedName~R6_5_CloneEditModeTests|FullyQualifiedName~RbxMaterialVariantLuaBindingsEditModeTests|FullyQualifiedName~RbxSignalConnectionTeardownEditModeTests|FullyQualifiedName~RungZeroEnvelopeEditModeTests|FullyQualifiedName~RungZeroEnvelopeProductionPathsEditModeTests|FullyQualifiedName~RbxJsonContractEditModeTests" --logger "trx;LogFileName=audit06-rbx-lua.trx"
```

Expected baseline: no failed assertions; enumerate all TRX NotExecuted/refusal outcomes instead of treating a portable Unity refusal as a pass. For the next authorized Unity run, focus on `RbxMaterialVariantRenderingEditModeTests`, `InstanceGameObjectBinderCrossLayerEditModeTests` and `Mvp8PhysicsPlayModeTests`, augmented with the endpoint, cache-lifetime and two-world isolation regressions. The portable runner explicitly cannot validate Unity Physics/Material APIs.

## Handoff

CHANGED: this report only.

VERIFY OUTPUT: audited SHA unchanged; report-only tracked diff check exit 0; supplemental new-file comparison exit 1 with no whitespace diagnostics; Python report integrity exit 0 (8 unique findings, 43 valid full source references, no trailing whitespace/Cyrillic). Source files remain unchanged in the scoped git diff.

NOT DONE: fixes, executable regressions and all checks in NOT RUN.

CONCERNS: P1 ray range and encode-side allocation bounds; explicit target-envelope enforcement is separate from preserved actor ownership. Cross-report ownership is stated at RBX-007/008.

QUESTIONS: none. Runtime code was assessed under the project's main-thread model; alternative threading guarantees need explicit validation.
