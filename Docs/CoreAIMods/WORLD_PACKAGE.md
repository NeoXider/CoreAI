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

A mod manifest may carry `LoadOrder` (an integer, omitted when `0`): the order the mod was loaded in its
world (`mod-system.md` §2). Capture keeps the entries in id order, so the bytes stay deterministic;
restore starts the mods without a recorded order first (`0` or below — a negative value from an
untrusted package is read as "no order", not refused), by ordinal id, then the ordered mods ascending,
ties by id, so a world whose mods use each other's work at init reloads its own save. The field is
additive: `format_version` is unchanged and a package whose mods carry no order is byte-identical to one
written before it existed; a reader older than the field refuses a package that carries it explicitly
("Could not find member 'LoadOrder'") and never restores it in the wrong order. A recorded order is at
most 2^53 (`LuaModManifest.MaximumLoadOrder`): a package that carries a larger one is refused on read,
and capture writes such a stored value as no order. Capture refuses a world whose mod source store
cannot be listed (`ILuaModSourceStore.List` throws or answers a listing marked unreadable) instead of
saving the world without its mods. A mod manifest may also carry `SuspendedAfterBudgetTrips` (omitted
when false; `mod-system.md` §2), so a mod suspended after repeated budget trips stays suspended across a
save and load. Tests: the three `WorldPackage_*` load-order cases in
`Mvp3WorldPackageFollowUpEditModeTests`, and `Package_ModLoadOrderPastTheMaximum_IsRefusedOnRead_AndCapturedAsUnordered`,
`Package_RoundTripAndCapture_KeepSuspendedAfterBudgetTrips` and
`Capture_FromAStoreThatCannotBeListed_IsRefused_NotSavedWithoutItsMods` in `Mvp3WorldPackageEditModeTests`.

The durable v1 surface is class/name/Archivable, world-owned origin/ACL/revision metadata, attributes,
tags, BasePart properties, Model PrimaryPart/stored WorldPivot, ClickDetector distance, camera CFrame,
ValueBase `Value` payloads (Int/Number/String/Bool/Object/Vector3/CFrame/Color3; ObjectValue targets
must name a serialized instance in the same package, nil is 0), and exact Lua mod manifests/source. `MaterialVariant` instances and part `MaterialVariant` references
are durable package state. `OwnerModId` is teardown bookkeeping used to form the projection,
not durable tree state. Runtime key/value scratch data, callbacks, signal connections, in-flight
requests, scheduler state, input state, and camera-follow attachment are ephemeral.

A non-archivable instance is kept, with its `Archivable = false` flag, unlike a Roblox place save,
which leaves it out: a package is the exact snapshot behind every safety autosave and confirmed load,
so dropping an instance a script marked non-archivable (a Model's `PrimaryPart`, say) would make
restoring a backup lose live content. The non-archivable instances the runtime creates itself, a
`Player` and its character, never enter a package.

Capture and `ExportSnapshot` project the live DataModel to world-owned state before collecting Part
properties. Any node with non-null `OwnerModId` starts a mod-ephemeral subtree; that node and every
descendant are omitted even when a descendant's own `OwnerModId` is null. Package validation rejects
an injected mod-owned node. Retained Parent references are therefore closed over the retained tree.
A retained world-owned Model that names an excluded (mod-ephemeral or otherwise missing) PrimaryPart
does not fail capture: the snapshot alone clears that `PrimaryPart` reference to null and records a
versioned additive `diagnostics` entry (`model_id`, `dropped_primary_part_id`, `reason` where reason
is `mod-ephemeral` or `missing`) in the package manifest. Old packages without `diagnostics` remain
readable because the field is optional. The live instance tree is untouched; only the captured payload
is adjusted, so the next gated `execute_lua` is not blocked by a dangling reference. An injected
mod-owned node in a package is still rejected.

The same rule covers every other state one line of Lua can leave in the live world that the reader
would refuse. Capture (and `ExportSnapshot`, which is the same projection) replaces the value in the
snapshot only and records one `diagnostics` entry per replaced member; the live world keeps what the
script wrote:

| Live state | Captured as | `reason` | `member` |
|---|---|---|---|
| `Model.PrimaryPart` outside its own Model | cleared | `not-descendant` | — (the original PrimaryPart entry shape) |
| `ObjectValue.Value` pointing at a destroyed, unparented or otherwise unsaved instance | nil | `missing` | `Value` |
| `ObjectValue.Value` pointing at a mod-owned instance | nil | `mod-ephemeral` | `Value` |
| a part's `MaterialVariant` naming no retained variant (renamed, destroyed, or mod-owned) | `""` / none | `missing` / `mod-ephemeral` | `MaterialVariant` |
| NaN or ±infinity in `NumberValue` / `Vector3Value` / `CFrameValue` / `Color3Value` | `0` / `(0,0,0)` / identity / black | `non-finite-value` | `Value` |
| non-finite `Model.WorldPivot` | no stored pivot | `non-finite-value` | `WorldPivot` |
| non-finite `ClickDetector.MaxActivationDistance` / `MaterialVariant.StudsPerTile` | `32` / `1` | `non-finite-value` | the property |
| non-finite Part `CFrame`, `Size`, `Color` or `Transparency` | the Roblox default Part bundle value (a replaced `Color` also clears the explicit-colour flag) | `non-finite-value` | the property |
| non-finite camera `CFrame` | identity, reported on the Workspace camera | `non-finite-value` | `CFrame` |
| an attribute with a non-finite component | omitted | `non-finite-value` | `Attributes.<name>` |
| `ClickDetector.MaxActivationDistance < 0`, `MaterialVariant.StudsPerTile <= 0` | `32` / `1` | `out-of-range` | the property |
| a lone UTF-16 surrogate (half an emoji cut by `string.sub`) in a name, origin/owner field, value payload, string attribute, tag, MaterialVariant map, Humanoid `DisplayName` or Part `MaterialVariant` | U+FFFD in its place (a tag that becomes a duplicate is dropped) | `ill-formed-text` | the member (`Name`, `Value`, `Tags`, `Attributes.<name>`, …) |
| a lone UTF-16 surrogate in a mod source | U+FFFD in its place; `model_id` is `0` | `ill-formed-text` | `Mods/<id>/source` |

`member` is an optional manifest key; entries that carry one name the instance in `model_id` and
write `dropped_primary_part_id` as `"0"`. No `format_version` bump was needed: such entries only
appear in worlds the old writer could not save at all. `Workspace.Gravity` assigned by a script is
not captured: the package's gravity comes from the host's `RbxWorldSettings`, so a script's change
is lost on the next save (tracked in `TODO.md`).

`Player` nodes, BaseParts without readable property state, mod-owned nodes injected into a package,
mods without source, non-finite or out-of-range values in a package being read, invalid origin tags,
dangling durable references, and unsupported class/state combinations are rejected instead of being
silently discarded — capture repairs a live world, the reader never repairs untrusted input.
`Instance.new` seeds every scripted BasePart with the Roblox default Part bundle the moment it is
created, so a Part that never had a property written is still readable state for capture; only a
missing sink or a host-created BasePart whose state was never pushed is rejected.
`UDim.Offset` is parsed as an exact invariant Int32, not through float.

## Validation and limits

Read and restore validate semantic state before calling the scale transaction, binder, Part sink, or
camera adapter. Restore requires one DataModel root with exactly one direct Workspace. Camera state
requires a camera rig before any scale mutation. A later restore failure invokes the scale rollback.
Malformed numbers inside specialised state (a `ClickDetector` distance, a `StudsPerTile`, a datatype
component) are a `BAD_ARGUMENT` validation failure, never an escaped `FormatException`.

Restore writes the tree as **one server-generated host operation**:
`InstanceTreeSerializer.Restore(snapshot, registry, hostActorId)` validates first, registers every
node (the operation's anchor), then applies every write inside a single
`ApplyServerGeneratedMutation`, and stamps the captured revisions last, so the restored world keeps
the revisions it was saved with. The host actor is the composition's local host (`"local"`) unless
`RbxWorldPackageRestoreOptions.HostActorId` names another one (blank falls back to `"local"`;
`InstanceTreeSerializer.Restore` itself refuses a blank host before any registration). The restore
writes do not pass through the world ACL — a `HostProtected` service could not otherwise be linked to
its DataModel — and no host scope outlives the restore.

**ACL floor.** A session composed with a world ACL version refuses a package that carries no
`world_acl_version` (a legacy package) before any side effect: no safety autosave, no staging, and a
`load_world`/`load_autosave` request is refused before the player is asked. Loading such a package
used to switch off every cross-actor check and write the downgrade into every later save. A
composition that must open legacy worlds is composed with `worldAclVersion: null`.

Version 1 limits compressed packages to 64 MiB, each expanded entry to 16 MiB, all expanded entries to
128 MiB, ZIP entries to 2,048, mods to 256, instances to 100,000, hierarchy depth to 2,048, and
attributes/tags to 256 each per instance. The per-instance limits are also enforced at the source, so a
script cannot build a world that cannot be saved: a `SetAttribute` that would add a 257th attribute,
an `AddTag` that would add a 257th tag, and a `Parent` assignment that would put an instance deeper
than 2,048 levels (`InstanceTreeSerializer.MaximumSnapshotDepth`) raise `BAD_ARGUMENT`. The byte
limits are not enforced at the source (see `TODO.md`).

The mod limit holds at the source too. A live world stores at most 256 distinct mod sources — an
unloaded mod keeps its source, `manage_mods` action `forget` removes it — so a load that would add a
257th distinct source is refused before its chunk runs, with a hint to forget a mod first, and a mod
whose source is already stored is never refused. `FileLuaModSourceStore` refuses a 257th stored id and
an exact replacement set of more than 256 sources the same way: its `Save` throws the refusal. The
session and the store count by one rule, `ILuaModSourceAdmission.CanAdmit` (public, implemented by the
file store and the session store): the session used to count the manifests the store could list while the
store counted folders holding a manifest file, so one unreadable manifest let the session admit a mod
whose source the store then refused to keep. A load whose source the store did not keep anyway is undone
as a failed load — the formula it displaced is put back and no revision is written — and the tool result
says why. A world that is already past the limit cannot be captured
(`RbxWorldPackageFormatLimitException`), so `forget` — and only `forget`; an `unload` keeps the source
and gains nothing — runs without its pre-mutation backup until the world is under the limit again,
while every other gated mutation stays refused. A bare `LuaCsModRuntime` composition without the world
session gets only the store's refusal, which is logged: its 257th mod runs without a persisted source
(see `TODO.md`).

The writer never throws on a lone surrogate either: text that did not come through capture (mod
manifests, settings, a hand-built payload) has it replaced with U+FFFD before the strict UTF-8 encode;
the reader still refuses invalid UTF-8 bytes. A world that captures but still cannot be encoded — its
`world.json` past the 16 MiB entry limit, or past the WebGL write budget — gets a failed
`RbxWorldPackageWriteResult` with `PackageCannotBeEncoded` set, as opposed to an I/O or durability
failure. The gate treats it like a world past the mod limit: `forget` and the safety autosave of a
confirmed load run without the backup they can never get, and say so (`backup_warning` in the
`manage_mods` result, `RbxWorldLoadResult.BackupWarning` for a load, shown in the Hub); every other
gated mutation stays refused, and a durability failure still refuses everything. A world past the mod
limit can be replaced by a confirmed load the same way.

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
are create-once. A manual slot name is 1-64 letters, digits, `-` or `_` after trimming surrounding
whitespace, and not a reserved Windows device name (`CON`, `PRN`, `AUX`, `NUL`, `COM1`-`COM9`,
`LPT1`-`LPT9`, case-insensitive); an autosave name is exactly one `.world` file name with no
directory part and no character a file name cannot hold on a supported player (`"`, `<`, `>`, `|`,
a control character, `\0`) and not a reserved Windows device name either (`CON.world`, `nul.WORLD`,
`COM1.x.world`, `LPT9.world` are refused with "Auto package name '…' is a reserved device name.";
`CONSOLE.world` is fine), because such a name opens the device instead of a file. The autosave name is
checked character by character rather than through `Path.GetFileName`, which throws on Mono for
exactly those characters, and `load_autosave` answers `read_failed` for any failure to open the file. The store throws
`ArgumentException` for a name that breaks these rules. A store keeps at most 64 manual slots and
256 MiB of manual-slot bytes (`DefaultMaximumManualSlots`, `DefaultMaximumManualSlotBytes`; the
constructor parameters `maximumManualSlots` and `maximumManualSlotBytes` change them); a save past
either limit writes nothing and `save_world` returns it as an ordinary failed result. No tool deletes
a manual slot, so a store at its cap stays full until the player removes files under
`persistentDataPath/CoreAI/Saves/Manual` by hand, which a WebGL player cannot do (a Hub delete surface
is tracked in `TODO.md`). When a store opens it deletes the temporary files a crash left behind — only
names of exactly the shape `<entry>.<32 lowercase hex>.tmp` in the manual, autosave and startup
directories. Autosaves
use timestamp/sequence/trigger names and rotate only after the new file's persistence callback
reports success. The just-confirmed autosave is never a rotation candidate, so a
host clock that moved backwards cannot make a successful backup delete itself. Store mutations are
serialized so two saves cannot interleave their durability phases. Manual bytes are never rotated.

Autosave rotation is a two-phase durability protocol. The first durability check confirms the new
file. Old files are then journalled before deletion, and a second check confirms the frozen ring.
If either phase fails, the new file is removed, the exact prior ring is restored, and a separate
uncancelled recovery check is requested. A failed recovery check is reported as durability
unconfirmed; it is never promoted to success. Deterministic volatile/durable filesystem tests reload
after failed first sync, failed second sync, successful second sync, and a mid-rotation I/O exception.

The store's default WebGL path uses `CoreAiWebGlPersistence.SyncAsync()`, which returns immediately.
It reports whether the engine's automatic `persistentDataPath` persistence is armed for this page —
that is the only durability signal Unity exposes since 6.3 deprecated the manual `FS.syncfs` path,
whose completion callback never fired. `true` means the completed write has been handed to that
persistence; it does not claim the IndexedDB transaction has committed. Desktop returns true because
native file writes are already durable at this boundary.

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
failure. Manual slots are not read, written, or rotated by this gate. An `execute_lua` queued behind a
confirmed load keeps its own error and gets the explanation (the world it ran against was replaced)
after it, instead of in its place.

A world session needs more from the shared gate than serialization: to keep the startup selection
(below) in step it must see each mutation start and end, and it runs the boot-time startup restore
under the gate. That second face is the public `IStartupAwareWorldMutationGate` (`IsHeld`,
`MutationStarting`, `AfterMutationAsync`, `ExecuteWithoutBackupAsync`), which `ConfirmedWorldMutationGate`
implements and a host gate that wraps one forwards. A host gate without it still serializes and backs
up every mutation, but the controller says once, through its diagnostics, that it falls back: the
startup world then follows mod source changes only (recorded from the frame pump), gated changes to the
world tree are not recorded for the next start, the boot restore runs without the gate, and a source
change may be recorded in the middle of a gated mutation (the next change records the finished state).

File reads/writes are chunked with PlayerLoop yields. The JSON/ZIP codec itself is not incremental, so
the actual WebGL player refuses to write a package above 4 MiB, with more than 4,096 instances, more
than 32,768 collection items, or more than 2 MiB of text characters before entering unbounded work.
The text count covers every string the encoding writes: names, ledger metadata, value payloads
(`StringValue` text included), Humanoid state, attributes, tags, mod sources and manifests, and
material names. A WebGL read checks only the 4 MiB byte size: the instance, item and text budget holds
for writes, so a package another build wrote within the format limits can still cost a WebGL reader
decode work beyond that budget. These are WebGL execution limits, not format limits. Browser timing
for packages within that budget still needs the real build interaction gate. So that a script cannot
grow a world the pre-mutation autosave can no longer write, a WebGL player's Lua runtime refuses
instance registrations past **4,032** charged instances
(`LuaCsModRuntime.WebGlEmergencyMaxRegisteredInstances` = the 4,096 save budget minus a 64-instance
allowance for the uncharged world skeleton). The refusal is a coded line that names the creation, for
example `BUDGET_EXCEEDED: Instance.new("Part"): actor '<id>' cannot register instance '<id>':
emergency registered instances ceiling reached (4032) | fix: destroy instances you no longer need
with Instance:Destroy(); this ceiling is shared by every actor in the world`. A host may lower that
ceiling (`emergencyMaxRegisteredInstances`, `EmergencyRegisteredInstanceCeiling`) but never raise it.
The byte and text budgets are not bounded at the source.

The durability mechanics (the `false`-is-failure rule, rollback, and the startup selection below) are
covered by deterministic reload-model tests, and
`FileStores_WithoutInjectedHook_DefaultToCoreAiWebGlPersistenceSyncAsync` pins that both file stores
default to `CoreAiWebGlPersistence.SyncAsync`. The real-browser smoke — a WebGL build that saves,
reloads the page and proves the bytes and the startup selection survive — is still open (an MVP3
follow-up in `TODO.md`). Production
composition injects one shared W3.4 gate into both the initial/replacement `execute_lua` stacks and
the production `manage_mods` tool. The declared mutating actions of both tool contracts are covered;
the full Unity EditMode gate ran green on 2026-09-25 (see "Acceptance status (MVP3)").

### Startup selection (the world that opens on the next start)

A player-confirmed load survives a process restart. `FileRbxWorldPackageStore` keeps a startup area
`Saves/Startup/` (namespaced as `Startup/Stores/<storeId>/` when the composition has a mods store id,
so a world selected in one composition never opens in another with a different Lua tier; manual slots
and autosaves are not namespaced). It holds create-once entries: `<N>.world`, an exact copy of the
confirmed package; `<N>.default`, a marker meaning "start with the default world"; and an
informational `<N>.json` that is never needed to boot. The highest `N` is the selection (a tie goes to
the marker). The entry goes through the same durability check as every other write — a `false`
answer is a failure, the new entry is removed and the previous selection stays — and the store
serializes it with the autosave ring. Older entries are pruned only after the new one is durable; a
failed prune is harmless because the highest `N` still wins. Neither manual slots nor autosaves are
ever touched.

Only `ConfirmManualLoadAsync(requestId, true)` selects a world, after the load has been published
— for a manual slot and for an autosave alike (the copy outlives the autosave rotating away). `save_world`
and a raw host `LoadConfirmedAsync` never change it; rejected, expired and still-pending requests die
with the process. A failed selection write never rolls the live world back: `RbxWorldLoadResult`
reports `StartupSelectionPersisted` / `StartupSelectionError`, and the next start opens the previous
selection.

While the selected world stays live, the selection follows it: the session records the world as it is
now as a new create-once startup entry — tree and exact mod sources from one capture — so the changes
made after the confirmation reopen too; before, the entry stayed the package confirmed at load time, and
mods whose sources lived only in the session's source version were gone after a restart. What triggers
a record (`RbxWorldRuntimeSessionController`, `Infrastructure/RbxWorldPackageContracts.cs`):

- **A change to the mod sources is recorded at once**, whoever made it. A gated call (`execute_lua`, a
  mutating `manage_mods` action) records it while the gate is still held; a write to the session's
  source store from anywhere else — the Hub **Mods** page, host code — is recorded by the frame pump at
  the next frame (so it needs the pump), never under the shared gate, and at most once per frame.
- **A change to the world tree alone** — a world-owned instance created, removed, re-parented or
  written, seen through the registry's events while a gated call runs — is recorded at most once per
  `StartupRefreshInterval` (5 s by default; `TimeSpan.Zero` records every change at once). The first
  change after a quiet interval is recorded at once; a change inside the interval is recorded by the
  frame pump once it has passed, together with every change made meanwhile. A change made within the
  interval before the process ends is not recorded (`TODO.md`). Each record is a whole package written,
  synced and pruned while the gate is held — on WebGL up to 4 MB handed to the browser — which is why
  an AI building in many small calls no longer writes one per call.
- **Nothing else is.** A call that changed neither — a read-only call, one that only moved parts through
  physics or moved the camera, or one that changed only the mods' own instances, which no entry holds —
  writes nothing, and an entry whose content equals the newest one is not written again. (Before, the
  refresh compared a digest of a whole capture, camera and part poses included, so in a live world every
  gated call wrote an entry.)
- **A world the startup restore would refuse is not recorded.** The refresh runs the restore's own checks
  first (an active `Full`-capability mod, an ACL downgrade): such a world keeps the previous entry, and
  the caller is told — a `manage_mods` result carries a `startup_warning` field, `execute_lua` appends
  the note to its output, the log says it once per refused state — instead of every later start falling
  back to the default world without a word. The isolation rule for `Full` mods is not weakened: in a
  `Full`-tier composition a world with an active `Full` mod does not carry over.
- **A record that fails** (capture, write or durability) keeps the previous entry and logs `The live
  world changed ('<trigger>'), but the change was not recorded for the next start: <reason> The next
  start opens the world as it was before it.` The caller's tool result carries the note too, and
  `RbxWorldRuntimeSessionController.StartupSelectionNote` keeps it for a change made outside the gate
  (a Hub edit) until a later record succeeds.

A Hub edit made while a confirmed load is recording its selection is not lost: it is recorded after it.
The default world (nothing confirmed, or the Hub reset) and a world loaded through a raw host load are
never recorded. `IRbxWorldStartupSelection` (implemented by `RbxWorldRuntimeSessionController`, and
deliberately not by `IRbxWorldRuntimeService`, so no AI tool reaches it) offers
`RestoreStartupSelectionAsync`, `ClearStartupSelectionAsync` (writes the default marker; the live world
is unchanged) and `ReadStartupSelectionAsync` (metadata only, no package decode).

## Production session replacement

`RbxWorldRuntimeSessionController` owns the live registry, Rbx bindings, Lua stack, source store,
and frame pump. `ILuaModRuntime`, `LuaTool.ILuaExecutor`, `LuaCsModStack`, `LuaCsLogicSlots`, and
`ILuaModSourceStore` are stable facades: consumers may retain them while every call resolves the
published session. The scene adapter stages a fresh inactive hierarchy; headless players use the
same controller with an engine-free host adapter. Capture always carries a camera pose because the
Lua surface falls back to an in-memory rig, so both adapters stage a rig for every package: the
engine-free adapter restores Part state into an in-memory sink and camera state into an in-memory
rig that the replacement session's bindings read, and a scene host without a camera stages the same
in-memory rig instead of refusing the package it captured itself. Publication applies the staged
pose to the live camera only when the scene has one.

A confirmed load takes the shared pre-mutation gate, so its `load_world-pre` safety autosave is written
inside it and includes an `execute_lua` that was in flight. Confirmed loading first writes the
package's exact source set into an isolated version directory and awaits a successful `SyncAsync`
completion. No world scale or camera state changes before that
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
durable startup pointer; the startup selection above is the only one.

At boot the production composition seeds the bundled mods, then calls
`RestoreStartupSelectionAsync`, and rehydrates the default world's persisted mods only when nothing was
restored (`RbxWorldStartupSequence`, which runs without play mode in tests). The restore goes through
the same staged swap as a confirmed load but writes no `load_world-pre` safety autosave, and it holds the
shared gate (`ExecuteWithoutBackupAsync`), so a mod change or an `execute_lua` that arrives during boot
waits and then lands on the restored world — such an `execute_lua` reports that its world was replaced,
by design. It never
throws: a missing, corrupt, oversized or vanished entry, an ACL downgrade, a source-durability failure,
an active `Full` mod or a staging failure keeps the default world live and is reported as
`RbxWorldStartupRestoreOutcome.FellBack` with a diagnostic. It never clears the selection on its own and
never falls back to an older entry. The player resets it from the Hub (below).

**Live network sessions (the MVP5 entry guard; the old MVP11).** A world load is refused with status
`network_sessions_active`, and the live world is left unchanged, while the network bridge lists
registered actors on a non-`Solo` topology — at request time, at confirmation and on a raw host load.
The rule and the ACL floor are checked once more under the session lock right before publication, so a
client that joins while the safety autosave is written makes the load fail and roll the staged world
back instead of having its world replaced; `RbxWorldLoadResult.Status` then names the reason
(`network_sessions_active`, `invalid_package`).
Live Mirror sessions cannot be handed to a new world until session handoff exists (MVP5, host mode). The
check is conservative (any registered actor counts), and loopback actors on the solo bridge never block a
load.

`PumpFrame` contains `Advance` and `Tick` separately: a throwing tick still lets the scheduler advance,
a throwing advance still delivers the queued mod events, and each fault is reported once.

Isolated version entries encode the complete UTF-8 mod id as a case-safe SHA-256 name and verify the
post-write manifest, source bytes, count, ids, and active flags before durability. This prevents both
legacy sanitizer collisions and case-only collisions on Windows. Synchronous mod-data access uses a
non-blocking gate: contention fails with an actionable error rather than parking the WebGL main loop.
Persistent per-mod key/value files also use the complete UTF-8 id's case-safe SHA-256 stem, so ordinal
ids such as `Case` and `case` cannot share data, deletion, or restart state on Windows. On first access,
a legacy sanitized file is atomically moved to the requesting exact id's hash; an old case-aliased file
cannot be split retrospectively and is therefore claimed by only that first exact id.

The Programmer role gets four AI tools on this service: `save_world` writes a create-once manual
package; `list_autosaves` returns the autosave ring (`name`, `trigger`, UTC `timestamp`, `size`);
`load_world` (manual slot) and `load_autosave` (autosave file name) only return
`player_confirmation_required` plus a one-use request id; they cannot apply a package, and no tool
reaches the startup selection.

None of the four lets a failure cross the tool boundary as an exception; each returns a JSON result
the model can act on, and only cancellation is still propagated. `save_world` reports a capture
failure (including a disposed session) as `capture_failed` and writes nothing, and a second save to
an existing slot is refused with the first bytes kept. `load_world` and `load_autosave` report
`not_found` (a missing slot, or an autosave that rotated away), `invalid_package` (corrupt, truncated,
over the read limit, or a legacy package refused by an ACL-composed session — the error carries the
session's refusal text), `read_failed` (an I/O failure), `network_sessions_active` (the MVP5 entry guard
above) and `session_unavailable` (the world session was already shut down because the game is closing
or restarting it; it used to escape as an `ObjectDisposedException`); no request is created.
`invalid_package` also covers a package the confirmation would refuse, now refused before the player
is asked: an active `Full`-capability mod, or a source store that cannot atomically replace a source
set. `list_autosaves` reports a store failure as `list_failed`. An `execute_lua` call whose world a
confirmed load replaced while it waited or ran returns an error that says so ("execute_lua ran against
the previous world: …"), because none of its changes reached the live world; it used to report
`WORLD_DETACHED`/`INSTANCE_DESTROYED` and tell the model to reload mods that were already running.

Every one of these tools that takes a name validates it first, with the store's rules above. An invalid
name — blank or whitespace-only, too long, a character outside the allowed set, a reserved device name,
or an autosave name that is not a single `.world` file name — is refused as an ordinary JSON tool
result, never as an exception: `success: false` and an `error` that names the parameter (`slot` or
`name`), states the rule, says the tool was not executed and asks for a retry with a valid value. The
load tools also return `status: "invalid_argument"`, `player_confirmation_required: false` and an empty
`request_id`. The world service is not called, so nothing is captured, written or queued.

For a valid name, host/UI code subscribes to `ManualLoadConfirmationRequested` or reads
`GetPendingManualLoads`, then calls `ConfirmManualLoadAsync`, which consumes that request and uses the same confirmed staged-swap path as
direct trusted host loading. Requests expire after two minutes by default, a new request replaces the
older request for the same slot, and the bounded eight-entry pool evicts its oldest request. Expired,
unknown, rejected, and reused ids are fail-closed and never mutate the live session.

The built-player Hub registers a **World Loads** page when `IRbxWorldRuntimeService` is available.
It renders the immutable pending metadata returned by `GetPendingManualLoads`, subscribes before its
visual tree is opened so late navigation cannot miss a request, and removes/disables a row before
calling `ConfirmManualLoadAsync(requestId, true|false)`. It never receives package bytes and exposes
no direct-load action. When the service also implements `IRbxWorldStartupSelection` (the production
controller does) the page opens with a **Next start** section: `Opens on start: <world>` (or `default
world`) and a **Start with the default world next time** button, which calls
`ClearStartupSelectionAsync` and leaves the live world and the saves alone. The Confirm tooltip and
the outcome line say whether the confirmed world will reopen on the next start (the tooltip: "together
with the changes the AI makes to it afterwards"); a service without a
startup selection shows no section, and after a load its outcome line says the world will not reopen
after a restart. The FullAccess WebGL harness provides `CreateWorldMarker`, `SaveWorld`,
`RequestWorldLoad`, and `DumpWorldMarker` `SendMessage` entry points for deterministic browser
acceptance. Its load entry point only creates the expiring request; the player must make the decision
through the Hub page.

## Acceptance status (MVP3)

**Closed (2026-09-25), released in 7.47.0; the Unity verification gate is green.** Unity 6000.3.14f1,
on the released tree: EditMode full 6550 total / 6539 passed / 0 failed / 11 skipped; the `core` leg
5266 / 5254 passed / 0 failed / 12 skipped, `llm` 6234 / 6221 / 0 / 13, `lua` 5582 / 5572 / 0 / 10, the
`MIRROR` leg 6550 / 6539 / 0 / 11; PlayMode `FastNoLlm` 95 total / 94 passed / 0 failed / 1 skipped (the
batchmode `WaitForEndOfFrame` skip). The portable `dotnet test` suites report 2172 passed / 0 failed for
the engine-free tests and 1848 total / 1846 passed / 0 failed / 2 not run for the Lua tier
(`tools/portable/LuaTests`, which runs `Mvp3WorldPackageEditModeTests` and
`Mvp3WorldPackageQaEditModeTests` against a UnityEngine shim; a case that reaches the engine, a
file-store load included, is Inconclusive by design and counts as not executed). The live-model
PlayMode suite is not part of this gate and is not green: its last run on this branch had 4 of 165
failing, all live-model timeouts while the LM Studio server was unresponsive; it is to be re-run. The
real-browser WebGL page-reload smoke and the IL2CPP/WebGL player checks are open follow-ups that gate
the next networked rung, not MVP3's product scope (`TODO.md`).

Each item of the roadmap's MVP3 Definition of Done is proven by a named test that fails on a wrong
implementation (EditMode fixtures: `Mvp3WorldPackageEditModeTests`, `Mvp3WorldPackageFollowUpEditModeTests`,
`Mvp3WorldPackageQaEditModeTests`, `RbxWorldHostDiWiringEditModeTests`):

| DoD item | Proving tests |
|---|---|
| (a) save → load round-trips the world-owned tree with stable ids, golden comparison | `WritePackage_AuthoredWorld_MatchesLiteralGoldenJson` (literal `manifest.json`/`world.json`, ids above 2^53 as strings), `ReadPackage_HandWrittenLiteralPackage_RestoresLiteralIdsParentsRevisionsAndPartState`, `WorldOwnedPayload_WithPackagedLuaSources_CodecRestoreRecapture_RoundTrips` |
| (b) mods restart clean on load | `ConfirmedPackageLoad_SwapsEveryFacadeAndRestartsOnlyActiveModsOnce` (active mods start once; the outgoing registry keeps no `OldCallback` and the outgoing scheduler's `LiveThreadCount` is 0) |
| (c) a manual slot is untouchable by AI tools; restore only with player confirmation | `SaveWorldTool_SecondSaveToSameSlot_IsRefusedAsResultAndKeepsFirstBytes` (the second save differs), `WorldPersistenceSurface_ExposesNoDeleteOverwriteRemoveOrReplacePath`, `ProgrammerRole_WorldTools_AreExactlySaveLoadListAndLoadAutosave`, positive confirm on the real controller in `StartupSelection_ConfirmedManualLoad_RestartRestoresSameTreeAndExactSources` |
| (d) the autosave ring rotates and records triggers | `FileStore_DefaultAutosaveCapacity_IsTenAndRotatesOnlyTheOldest`, `ListAutoSaves_HyphenatedTriggers_RoundTripExactly`, `ListAutoSavesTool_ReturnsExactNameTriggerTimestampAndSize`, `ConfirmedBackup_GatedExecuteLua_WritesExactlyOneExecuteLuaAutosaveToFileStore` |
| (e) WebGL persistence after save | `FileStores_WithoutInjectedHook_DefaultToCoreAiWebGlPersistenceSyncAsync`; the `false`-is-failure rule by the store durability tests; the real-browser reload smoke is an open follow-up |
| (f) an invalid slot or autosave name is a JSON result (7.45.0) | `SaveWorld_InvalidSlot_IsRefusedAsResult_WithoutCallingService`, `LoadWorld_InvalidSlot_IsRefusedAsResult_WithoutCallingService`, `LoadAutoSave_InvalidName_IsRefusedAsResult_WithoutCallingService` (now including `null` and the echoed slot) |

The residue closed alongside the DoD:

- **Rung-zero restore envelope** — `RungZeroHostRestore_AclPackageLoad_RestoresTreeAsOneHostEnvelopedOperation`
  and its headless twin (retained operation count 0 → 1 through production composition),
  `RungZeroHostRestore_AclPackageLoad_LeaksNoHostScopeAndKeepsOwnership`, and the engine-free
  `HostRestore_AclSnapshot_RetainsExactlyOneOperationForTheHostActor` / `HostRestore_KeepsEveryCapturedRevision`
  (`RungZeroAclEngineFreeTests`).
- **ACL floor** — `AclComposedSession_LegacyPackage_IsRefusedBeforeAnySideEffect`,
  `HeadlessSessionController_AclComposed_RefusesLegacyPackageBeforeAnySideEffect`,
  `WorldLoadRequest_AclComposedSession_RefusesLegacyPackageBeforeAskingThePlayer`, and the negative twin
  `LegacyComposedSession_AcceptsLegacyPackageAndKeepsAPackagedAclVersion`.
- **Restored trees charge the instance quota** —
  `LuaCs_RegisteredInstanceQuota_ChargesInstancesRestoredBeforeTheRuntimeExisted` (`LuaCsModRuntimeEditModeTests`); a restored `Humanoid` in a headless world gets the scheduler —
  `RestoredHumanoid_InAHeadlessWorld_IsDrivenByTheSchedulerFromTheStart` (`Mvp8HumanoidEditModeTests`).
- **Startup selection (the W3.5 tail)** — store: `StartupStore_Select_NewStoreInstanceReadsExactConfirmedBytes`,
  `StartupStore_SyncFalse_IsFailure_AndReloadKeepsPreviousSelection`, `StartupStore_StoreIdNamespaces_Isolate`;
  controller (a restart is a second controller over the same directories):
  `StartupSelection_ConfirmedManualLoad_RestartRestoresSameTreeAndExactSources`,
  `StartupSelection_ConfirmedAutosaveLoad_SurvivesTheAutosaveRotatingAway`,
  `StartupSelection_SaveWorldAndRawHostLoad_DoNotChangeStartup`,
  `StartupSelection_DurabilityFalse_LoadStaysPublished_FlagFalse_RestartBootsPrevious`,
  `StartupRestore_CorruptOversizedOrMissingEntry_KeepsDefaultWorld_NeverThrows`; composition:
  `StartupSequence_RestoresFirst_AndRehydratesTheDefaultWorldUnlessRestored`,
  `Composition_RegistersControllerAsStartupSelection_AndNamespacesTheDefaultStartupArea`,
  `ComposedConfirmedLoad_ReopensInAFreshCompositionOverTheSameStore`; Hub:
  `WorldLoadPage_StartupSection_ShowsSelectedWorldAndUtcTime_AndResetChoosesDefault`.
- **Tool failures as JSON** — `SaveWorldTool_CaptureFailure_IsReturnedAsCaptureFailedResult`,
  `LoadWorldTool_ReadPhaseFailuresAndRefusals_AreReturnedAsJsonResults`,
  `LoadAutoSaveTool_RotatedAwayName_IsRefusedAsNotFoundResult`, `ListAutoSavesTool_StoreFailure_IsReturnedAsJsonFailure`;
  the MVP5 entry guard: `WorldLoad_LiveNetworkSessions_AreRefusedAtRequestConfirmAndRawLoad` and its twin
  `WorldLoad_LoopbackActors_DoNotBlockALoad`.
- **Audit round 1 of the world package** — the mod-source limit:
  `ModSourceLimit_DistinctModBeyondTheFormatLimit_IsRefusedWithTheWayOut_AndTheWorldStaysCapturable`,
  `ConfirmedBackup_WorldPastTheModLimit_RunsOnlyForgetWithoutBackup_AndRefusesOtherMutations`,
  `Save_DistinctIdBeyondTheWorldPackageModLimit_IsRefused_ExistingIdsStillUpdate`
  (`FileLuaModSourceStoreEditModeTests`); the startup selection following the live world (`[UnityTest]`s,
  Unity only): `StartupSelection_GatedAiChangesAfterAConfirmedLoad_ReopenAfterRestart`,
  `StartupSelection_UnconfirmedRefresh_KeepsThePreviousEntryAndReportsIt`,
  `StartupSelection_ChangesToANonStartupWorld_NeverSelectIt`; loads under the gate:
  `WorldLoad_ClientJoiningDuringTheSafetyAutosave_IsRefusedBeforePublish_AndRollsBack`,
  `WorldLoad_SafetyAutosaveWaitsForTheSharedGate_SoAnExecuteLuaInFlightIsInTheBackup`,
  `WorldLoadRequest_PackageTheConfirmationWouldRefuse_IsRefusedBeforeThePlayerIsAsked`,
  `ExecuteLua_WhoseWorldAConfirmedLoadReplacedWhileItWaited_ReportsTheLoad`,
  `WorldLoadTools_SessionAlreadyShutDown_ReturnSessionUnavailable`; the store:
  `PackageNames_AutoFileNameWithACharacterNoPlayerAccepts_IsRefusedWithoutThrowing`,
  `WebGlWorkBudget_CountsValueStringsAndHumanoidState_ExactlyAtTheBoundary`,
  `FileStore_ManualSlotCountAndByteCaps_RefuseAsResultsAndWriteNothing`,
  `SaveWorldTool_ManualSlotLimitReached_IsAnOrdinaryFailedResultAndWritesNothing`,
  `FileStore_Open_SweepsOnlyCrashLeftTemporaryFiles`.
- **Load order (A1-02)** — `WorldPackage_ModsThatNeedEachOtherAtInit_ReloadTheirOwnSave_InLoadOrder`,
  `WorldPackage_WrittenWithoutLoadOrder_StillRestores_ModsStartByOrdinalId`,
  `WorldPackage_NegativeLoadOrder_IsNotRejected_AndCountsAsNoRecordedOrder`
  (`Mvp3WorldPackageFollowUpEditModeTests`); the runtime restart paths:
  `LuaCs_RehydrateFromStore_AfterRestart_StartsModsInTheirLoadOrder_NotInIdOrder`,
  `LuaCs_RehydrateExactOrThrow_AfterRestart_StartsModsInTheirLoadOrder_NotInIdOrder`
  (`LuaCsModRuntimePersistenceEditModeTests`).
- **Audit rounds 2 and 3 of the world package (B2-01…B2-14, `3d5b62d0`, `23f63eaa`; C2-01…C2-09,
  `d4d7f95b`)** — the startup selection (`[UnityTest]`s in `Mvp3WorldPackageFollowUpEditModeTests`, Unity
  only): `StartupSelection_ChangeWithAnActiveFullCapabilityMod_KeepsThePreviousEntryAndSaysWhy`,
  `StartupSelection_FullCapabilityModForgotten_ChangesAreRecordedAgain`,
  `StartupSelection_GatedModLoad_IsRecordedOnce_NotAgainOnTheNextFrame`,
  `StartupSelection_HubAndHostModChangesOnTheStartupWorld_ReopenAfterRestart`,
  `StartupSelection_UnchangedWorldWritesNoEntry_AChangeWritesExactlyOne`,
  `StartupSelection_ReadOnlyCallsWhilePartsAndTheCameraMove_WriteNoEntry`,
  `StartupSelection_WorldChangesInsideTheInterval_AreRecordedOnceItHasPassed`,
  `StartupSelection_ZeroInterval_RecordsEveryWorldChangeAtOnce`,
  `StartupSelection_HubChangeTheRestoreWouldRefuse_LeavesANote`,
  `StartupSelection_HubChangeWhileTheConfirmedLoadRecordsIt_ReopensAfterRestart`; the boot restore and
  host gates (`Mvp3WorldPackageEditModeTests`, like the rest of this item unless named otherwise):
  `StartupRestore_HoldsTheSharedGate_SoAModChangeArrivingDuringBootLandsOnTheRestoredWorld`,
  `StartupRestore_ExecuteLuaArrivingDuringBoot_WaitsAndReportsTheReplacedWorld`,
  `HostGateForwardingTheStartupFace_RecordsGatedChanges_AndSerializesTheBootRestore`,
  `HostGateWithoutTheStartupFace_IsReportedOnce_AndSourceChangesAreStillRecordedFromThePump`,
  `GatedMutationThatThrows_TheWorldChangeItMadeIsRecordedByTheNextFrame`; the mod limit and admission:
  `ModSourceLimit_UnreadableManifest_TheSessionRefusesWithTheStoresOwnRefusal`,
  `ModSourceLoad_StoreThatDoesNotKeepTheSource_UndoesTheLoadAndTheToolSaysWhy`,
  `ModSourceLoad_StoreThatDoesNotKeepTheSource_PutsBackTheFormulaItDisplaced_AndRecordsNoRevision`,
  `HostSourceStoreWithAdmission_RefusesANewModBeforeItRuns`,
  `Admission_CountsAFolderWhoseManifestCannotBeRead_AndAgreesWithSave` (`FileLuaModSourceStoreEditModeTests`);
  `ExecuteLua_QueuedBehindAConfirmedLoad_KeepsItsOwnErrorAndExplainsTheReplacedWorld`;
  `PackageNames_AutoFileNamedAfterADevice_IsRefusedWithoutThrowing`; the same-id build refusal and the
  load-order bounds in `LuaCsModRuntimeEditModeTests` and `LuaCsModRuntimePersistenceEditModeTests`
  (`LuaCs_FirstLoadOfAnIdAnotherLoadIsBuilding_IsRefusedBeforeItsChunkRuns_TheFirstKeepsRunning`,
  `LuaCs_ReloadOfAnIdAnotherReloadIsBuilding_IsRefusedBeforeAnythingRuns_TheFirstCompletes`,
  `LuaCs_AStoredLoadOrderPastTheMaximum_ReadsAsUnordered_AndLaterFirstLoadsKeepTheirOrder`,
  `LuaCs_FirstLoadWhileTheStoreCannotBeListed_StampsNoLoadOrder_AndLogsIt`).

## Compatibility policy

Unsupported format, schema, API, or minimum-reader versions fail explicitly. A semantic or entry-layout
change increments `format_version` and adds an explicit decoder/migration path. Additive metadata still
requires a compatibility decision; decoder removal is an explicit breaking change.
