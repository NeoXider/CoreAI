# Replication core, phase 0 — engine-free, registry-to-registry (2026-09-10)

Status: landed in 7.39.0 as the layer UNDER MVP11/MVP12, not those rungs. Nothing in production
constructs these types — no composition root builds a `ReplicationDirtySet`, no bytes cross a
socket, and an admitted client still receives no join snapshot over the wire. Everything below is
proven registry-to-registry in one process (`ReplicatedWorldHarness`). Rung status lives in
`TODO.md` ("MVP2.5 rungs"); this note records what the code does, so the plans that predate it
(`MVP25_BUILD_PLAN_2026-09-04.md` MVP12 file list, `MVP25_ONLINE_PLAN.md` §4.4) are read against
the tree rather than against their own forecast. `MVP2_PHASE1_CORRECTION.md` is the failure mode
this note exists to avoid: types built, claims made, nothing wired.

## What exists

All engine-free, in `Assets/CoreAIMods/Runtime/RbxApi/Instances/Replication/` unless stated.

| Piece | Where | What it does |
|---|---|---|
| Source | `InstanceRegistry.RevisionAdvanced` (`../InstanceRegistry.cs`) | Every setter reports the member it changed — `Name`, `Parent`, `Value`, `PrimaryPart`, `WorldPivot`, `Attribute:<name>`, `Tag:<name>` per `ReplicationMembers` — with the record's revision, raised once the mutation gate is released; `Unregistered` reports a removal. A registry is `Authoritative` by default or a `Replica` (`RegistryAuthority`); a replica's local write marks divergence and raises nothing. |
| Dirty set | `ReplicationDirtySet.cs` | Subscribes itself to the registry. One entry per instance per step; the newest revision wins whatever order it arrives in; a removal is never downgraded to a change. `Pending` is unfiltered, `DeltasFor(actor)` goes through the guarded filter. `UnobservedInstanceCount` counts what the registry already held when the set was created. |
| Filter | `ReplicationFilter.cs` | `IReplicationFilter` decides instance and member visibility per recipient. `DefaultReplicationFilter` is a denylist transcribed from the mirror's class tags (`NotReplicated`, `PlayerReplicated`). `GuardedReplicationFilter` wraps any game filter and is the floor it cannot open: `ServerStorage`, `ServerScriptService`, `Camera`, `PlayerScripts` are never visible; `Backpack` and `PlayerGui` reach their owner only; a filter that throws hides and is reported through the registry diagnostics. `AIService` is hidden by CoreAI's own decision (no yaml to cite). |
| Stream | `ReplicationStream.cs` | Per recipient: which ids it holds, its last sequence, and `Plan()` — one ordered batch of Spawn (parent-first), Patch (named members) and Remove read from the dirty set. `PlanWorld()` seeds a recipient from the live registry. `Plan()` REFUSES (throws) when the world predates the dirty set and the recipient was never seeded, rather than sending an empty world. Plans carry ids, kinds and member names — never values. |
| Applier | `ReplicationApplier.cs` | Applies a plan to a `Replica` registry inside `BeginReplicationApply()`, so revisions stay the server's. Values come from `IReplicationStateSource` — the harness implements it over captured `InstanceSnapshot`s; a wire decoder of a later phase implements it over bytes. Sequence bookkeeping: older batch = duplicate, dropped; newer = gap, `ResyncRequested`, everything dropped until `CompleteResync(next)`; a patch for an unknown id, a spawn for a held id or an unheld parent, missing or malformed state, or any exception while applying = protocol violation on the same resync path. Patches go through the ordinary setters, so `Changed`/`GetPropertyChangedSignal` fire on the replica. |
| Members | `ReplicationMembers.cs` | The member vocabulary (Roblox property names plus the `Attribute:`/`Tag:` prefixes) and `EnumerateReplicable(instance)` for whole-node expansion. |

## The flow, as tested

server registry write → `RevisionAdvanced(id, revision, member)` → `ReplicationDirtySet` →
per recipient `ReplicationStream.Plan()` → `ReplicationBatchPlan` beside captured snapshots →
`ReplicationApplier.Apply` on the client's `Replica` registry.

Tests, all EditMode under `Assets/CoreAIMods/Tests/EditMode/RbxApi/Replication/`:

- `InstanceRegistryAuthorityEditModeTests` — the member is named, the event fires outside the gate, a
  throwing subscriber breaks nothing, a replica's local write marks divergence and raises nothing, a
  replica's own `Instance.new` gets a local id the wire contract refuses.
- `ReplicationDirtySetEventsEditModeTests` — marks, removals, out-of-order revisions during a flush,
  the unobserved count, `Pending` vs `DeltasFor`.
- `ReplicationStreamPlanEditModeTests` — order, visibility transitions as remove-then-spawn, a
  whole-node mark still carrying a member removed in the same step, refusal to plan unseeded,
  per-recipient state and sequence.
- `ReplicationApplierEditModeTests` — duplicate/gap/violation handling, signals on patch, deferred
  references, a remove destroys the subtree, malformed and missing state never escape as exceptions.
- `GuardedReplicationFilterEditModeTests` — the floor, plus
  `Negative_EverythingTheBootstrapPutsInTheTree_ReplicatesExactlyWhenItsYamlLacksNotReplicated`,
  which walks what `DataModelBootstrap` creates against a transcription of the mirror's tags and
  fails the moment a new service replicates against them; the transcription itself is checked
  against the local mirror when it is on disk.
- `ReplicatedWorldConvergenceEditModeTests` over `ReplicatedWorldHarness` — two registries in one
  process joined by `NullNetworkBridge`; a mixed sequence of creates, writes, re-parents and destroys
  converges; client Lua resolves `ReplicatedStorage.RemoteX` by reference; a late joiner seeded with
  `PlanWorld()` converges; a late joiner WITHOUT a join snapshot asks for a resync rather than
  guessing; another player's `Backpack` is visible only to its owner.

## What is not here (do not overclaim)

- No production wiring. `CoreAiModsInstaller` builds no dirty set, stream or applier, and
  `IntentGateway` is likewise never constructed in production. The layer exists so MVP11/MVP12 can be
  built on it; it is not a delivered feature.
- No transport. A batch is a `ReplicationBatchPlan` beside captured snapshots, not bytes. The delta
  codec, the Mirror path and the socket-level join snapshot are the MVP11/MVP12 rows in `TODO.md`.
- Two named limits (`TODO.md`, "Two named Phase 0 limits of the replication core"):
  1. A replicated `Player` arrives as a plain instance — no `UserId`, no identity, no `Character`,
     and absent from `Players:GetPlayers()` on the replica.
  2. A deferred reference resolves only inside the batch that carried it: an `ObjectValue` pointing
     at something the recipient cannot see becomes nil and the wanted id is forgotten, so moving that
     target into view later spawns the target but never repairs the reference.
- Whole-node marks lose WHICH member changed (every part-property write through
  `LuaCsRbxInstanceBindings.RecordMutation`, every tween tick), so the plan-time expansion can only
  name what the engine-free core enumerates; a Unity-layer applier for `BasePart` geometry cannot
  hang off it until those call sites name their member (the TODO in `ReplicationStream.VisibleMembers`).

## Composition order matters

A dirty set created after the bootstrap never saw the bootstrap's instances; a stream over it must be
seeded with `PlanWorld()` before `Plan()` is legal. The natural composition order — bootstrap, then
replication — hits exactly this, which is why the refusal throws instead of reporting through the
optional diagnostics sink: a guard that is silent when nobody wired the sink is the defect it guards
against.
