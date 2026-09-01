# CoreAI Rbx world package

## Format and runtime boundary

A world is one `.world` ZIP container. The engine-free codec reads and writes bytes; it does not
reference Unity, `RbxSpace`, or a filesystem. Capture receives explicit world settings, including
`meters_per_stud`. A host that restores a payload applies that scale through the transaction port in
`RbxWorldPackageRestoreOptions`.

Version 1 contains deterministic `manifest.json`, `world.json`, and indexed
`Mods/NNNN/manifest.json` plus `Mods/NNNN/main.lua` entries. The manifest declares the format/API/
minimum-reader versions, UTC capture time, world entry, and sorted mod index. The world entry contains
settings, DataModel tree, external BasePart state, and optional camera state. IDs and revisions are
decimal strings so WebGL does not lose 64-bit precision. Only server-authority IDs are accepted.

The durable v1 surface is class/name/Archivable, world-owned origin/ACL/revision metadata, attributes,
tags, BasePart properties, Model PrimaryPart/stored WorldPivot, ClickDetector distance, camera CFrame,
and exact Lua mod manifests/source. `OwnerModId` is teardown bookkeeping used to form the projection,
not durable tree state. Runtime key/value scratch data, callbacks, signal connections, in-flight
requests, scheduler state, input state, and camera-follow attachment are ephemeral.

Capture and `ExportSnapshot` project the live DataModel to world-owned state before collecting Part
properties. Any node with non-null `OwnerModId` starts a mod-ephemeral subtree; that node and every
descendant are omitted even when a descendant's own `OwnerModId` is null. Package validation rejects
an injected mod-owned node. Retained Parent references are therefore closed over the retained tree.
A retained world-owned Model that names an excluded PrimaryPart fails capture: version 1 never
silently clears a durable `PrimaryPart` reference. The caller must clear the reference or promote the
complete referenced subtree to world ownership.

`Player` nodes, BaseParts without readable property state, mod-owned nodes injected into a package,
mods without source, non-finite values, invalid origin tags, dangling durable references, and
unsupported class/state combinations are rejected instead of being silently discarded.
`UDim.Offset` is parsed as an exact invariant Int32, not through float.

## Validation and limits

Read and restore validate semantic state before calling the scale transaction, binder, Part sink, or
camera adapter. Restore requires one DataModel root with exactly one direct Workspace. Camera state
requires a camera rig before any scale mutation. A later restore failure invokes the scale rollback.

Version 1 limits compressed packages to 64 MiB, each expanded entry to 16 MiB, all expanded entries to
128 MiB, ZIP entries to 2,048, mods to 256, instances to 100,000, hierarchy depth to 2,048, and
attributes/tags to 256 each per instance.

The hierarchy validator and capture traversal are iterative and linear. Capture checks depth/count
before accepting each node. The writer preflights
exact UTF-8 expanded size, so it cannot emit a package its reader rejects on aggregate expansion. JSON
DTO materialization still precedes the 100,000-instance semantic check; hostile 16 MiB `world.json`
browser peak memory remains a residual risk.

The current world-owned projection runs after the generic instance snapshot has materialized the live
tree. A future ownership-aware capture traversal must skip mod-ephemeral roots before they consume
snapshot depth, count, and allocation budgets.

Codec validation prevents corrupt input from reaching adapters, but arbitrary external binders and
sinks are not transactional. Production restore must construct a disposable fresh world or provide
transactional adapters before exposing it; the codec does not claim general rollback of their effects.

## Persistence status

`FileRbxWorldPackageStore` is a storage primitive, not production save/load orchestration. Manual slots
are create-once. Autosaves use timestamp/sequence/trigger names and rotate only after the new file's
persistence callback reports success. Store mutations are serialized so two saves cannot interleave
their durability phases. Manual bytes are never rotated.

Autosave rotation is a two-phase durability protocol. The first browser callback confirms the new
file. Old files are then journalled before deletion, and a second callback confirms the frozen ring.
If either phase fails, the new file is removed, the exact prior ring is restored, and a separate
uncancelled recovery sync is requested. A failed recovery callback is reported as durability
unconfirmed; it is never promoted to success. Deterministic volatile/durable filesystem tests reload
after failed first sync, failed second sync, successful second sync, and a mid-rotation I/O exception.

The store's default WebGL path uses `CoreAiWebGlPersistence.SyncAsync()`. Its task completes only from
the matching `FS.syncfs` success/error callback. A caller cancellation or 30-second realtime PlayerLoop
timeout removes the pending call; a late callback is ignored. Desktop completes true because native
file writes are already durable at this boundary. The legacy Boolean `Sync()` remains for existing
callers but is not used as a durability result by the world-package store.

`ConfirmedWorldMutationGate` is the reusable pre-mutation orchestration boundary. A runtime host
constructs one shared instance from its current-world capture delegate and `IRbxWorldPackageStore`,
then injects that same instance into every covered mutator. Its async single-flight spans capture,
confirmed `CreateAutoAsync`, and the complete mutation callback, so a concurrent tool cannot capture
or mutate in the middle of another protected operation. It uses asynchronous waiting only; there is
no thread, blocking wait, or sync-over-async path.

The implemented adapters conservatively protect every `execute_lua` call with trigger `execute_lua`.
Every mutating `manage_mods` action (`load`, `reload`, `unload`, `import`, `forget`, and `revert`) uses
the deterministic trigger `manage_mods-<action>`. Read-only `list`, `get_source`, `export`, `versions`,
and `diagnostics` bypass capture and autosave. A null or unsuccessful write result, store/capture
exception, or cancellation prevents the Lua/runtime mutation and becomes the tool's structured
failure. Manual slots are not read, written, or rotated by this gate.

File reads/writes are chunked with PlayerLoop yields. The JSON/ZIP codec itself is not incremental, so
the actual WebGL player refuses packages above 4 MiB, more than 4,096 instances, more than 32,768
collection items, or more than 2 MiB text characters before entering unbounded work. These are WebGL
execution limits, not format limits. Browser timing for packages within that budget still needs the
real build interaction gate.

W3.5 callback and rollback mechanics are implemented and covered by deterministic reload-model and
JavaScript bridge tests. Full W3.5 acceptance remains open until a real WebGL build completes
save -> callback -> page reload and proves the bytes survive. Production composition injects one
shared W3.4 gate into both the initial/replacement `execute_lua` stacks and the production
`manage_mods` tool. The declared mutating actions of both tool contracts are covered; Unity acceptance
still requires the owner-run focused/full EditMode gate.

## Production session replacement

`RbxWorldRuntimeSessionController` owns the live registry, Rbx bindings, Lua stack, source store,
and frame pump. `ILuaModRuntime`, `LuaTool.ILuaExecutor`, `LuaCsModStack`, `LuaCsLogicSlots`, and
`ILuaModSourceStore` are stable facades: consumers may retain them while every call resolves the
published session. The scene adapter stages a fresh inactive hierarchy; headless players use the
same controller with an engine-free host adapter.

Confirmed loading first writes the package's exact source set into an isolated version directory
and awaits a successful `SyncAsync` completion. No world scale or camera state changes before that
durability result. `Stage -> Rbx/Lua construction -> active source start -> publication` has no
await. Dormant sources are installed but do not execute. A failed stage shuts down its VM,
connections, scheduler work, registry, binder, and source version while the outgoing facades remain
usable. Top-level `store_set`/`store_clear` operations use a session overlay and reach the durable
mod-data store only after publication. Script revision writes use the same deferred-publication
rule. Network subscription is established before publication and queued outbound work is released
afterward. Replay failures after the non-throwing publication point cannot roll the world back; they
are reported as degraded-activation diagnostics with the failed operation instead of being silently
discarded.

The stable logic-slot facade copies only the host's declared slot names into the staged session before
active source startup. Outgoing overrides, handlers, and failure listeners are not copied into the
candidate; retained facade listeners are retargeted only after publication. Camera restore and mod
startup use an isolated staged rig. A successful publication applies the final staged pose/follow
state once, including startup-mod camera changes; rejection leaves the live pose, follower target,
offset, enabled state, and `RbxSpace` scale unchanged.

An active mod requesting `Full` capability is rejected before staging because its arbitrary Unity
surface cannot be transactionally isolated. A dormant `Full` mod remains exact package data and does
not execute. Hosts that provide a custom session source backend pass an
`IRbxWorldModSourceStore` to production composition; the public `ILuaModSourceStore` remains the
stable session facade used by runtime, capture, Hub, and management tools.

The outgoing Lua runtime is made inert only after the replacement is published. Its hooks,
connections, scheduler threads, registry, and Rbx bindings are then detached and disposed. Runtime
mod-owned subtrees were excluded during capture, so active source execution recreates them once
after the durable tree is restored rather than duplicating snapshot state.

Exact source preparation is fail-fast while another store instance mutates the same root; it never
blocks a WebGL thread. A failed persistence callback removes the version and confirms cleanup before
the live session can change. Session-source directories retain at most three versions, never deleting
the current runtime target or the default startup store. Prepared/unselected versions are not a
durable startup pointer. Until W3.5 persists world selection/autoload, a process restart intentionally
returns to the previously selected world plus the default source set rather than guessing that an
in-process loaded session should become the startup pair.

Isolated version entries encode the complete UTF-8 mod id as a case-safe SHA-256 name and verify the
post-write manifest, source bytes, count, ids, and active flags before durability. This prevents both
legacy sanitizer collisions and case-only collisions on Windows. Synchronous mod-data access uses a
non-blocking gate: contention fails with an actionable error rather than parking the WebGL main loop.
Persistent per-mod key/value files also use the complete UTF-8 id's case-safe SHA-256 stem, so ordinal
ids such as `Case` and `case` cannot share data, deletion, or restart state on Windows. On first access,
a legacy sanitized file is atomically moved to the requesting exact id's hash; an old case-aliased file
cannot be split retrospectively and is therefore claimed by only that first exact id.

`save_world` writes a create-once manual package. `load_world` only returns
`player_confirmation_required` plus a one-use request id; it cannot apply a package. Host/UI code
subscribes to `ManualLoadConfirmationRequested` or reads `GetPendingManualLoads`, then calls
`ConfirmManualLoadAsync`, which consumes that request and uses the same confirmed staged-swap path as
direct trusted host loading. Requests expire after two minutes by default, a new request replaces the
older request for the same slot, and the bounded eight-entry pool evicts its oldest request. Expired,
unknown, rejected, and reused ids are fail-closed and never mutate the live session.

The built-player Hub registers a **World Loads** page when `IRbxWorldRuntimeService` is available.
It renders the immutable pending metadata returned by `GetPendingManualLoads`, subscribes before its
visual tree is opened so late navigation cannot miss a request, and removes/disables a row before
calling `ConfirmManualLoadAsync(requestId, true|false)`. It never receives package bytes and exposes
no direct-load action. The FullAccess WebGL harness provides `CreateWorldMarker`, `SaveWorld`,
`RequestWorldLoad`, and `DumpWorldMarker` `SendMessage` entry points for deterministic browser
acceptance. Its load entry point only creates the expiring request; the player must make the decision
through the Hub page.

## Compatibility policy

Unsupported format, schema, API, or minimum-reader versions fail explicitly. A semantic or entry-layout
change increments `format_version` and adds an explicit decoder/migration path. Additive metadata still
requires a compatibility decision; decoder removal is an explicit breaking change.
