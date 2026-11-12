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
decimal strings so WebGL does not lose 64-bit precision. Only server-authority IDs are accepted. Version 2 has the
same entries and gives every script instance a `script` block; this reader reads both, and the writer still writes
version 1 (see "Script instances" below).

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

**Lights and the day cycle.** A `PointLight` node carries an optional `light` block (`brightness`, `color`
[r, g, b], `enabled`, `shadows`, `range` in studs within [0, 60]); a PointLight without one restores with the
class defaults. `world.json` carries an optional top-level `lighting` block (`clock_time` in [0, 24),
`geographic_latitude`, `brightness`, `ambient` and `outdoor_ambient` [r, g, b]) only when the world drives its
lighting (`RbxLighting.IsDriven`: a script changed a day-cycle property, or a saved block was restored);
`TimeOfDay` is derived from `clock_time` and not stored, and a missing block means the class defaults and
the scene's own lighting. Restore puts both back through the classes' setters after the tree, and a
restored block makes the world drive its lighting. Both keys are omitted when absent, so a package without
them is byte-identical to one written before they existed; no `format_version` bump. Readers up to 7.56.0
refuse a package that carries either key by name ("Could not find member 'lighting'"), as every reader
refuses an unknown key in `manifest.json`, `world.json` or an instance (`MissingMemberHandling.Error`), so no
release opens such a world without them. The reader refuses a `light` block on a node that is not a
PointLight, a range outside [0, 60], a `clock_time` outside [0, 24), a non-finite value, and a `lighting`
block in a tree without a `Lighting` service. The block also carries `environment_diffuse_scale` and
`environment_specular_scale` (`Lighting.EnvironmentDiffuseScale`, `EnvironmentSpecularScale`), each only when it
differs from its default of 1, so a block whose scales were never changed is written exactly as before they existed
and a block without them reads 1; the reader refuses a scale outside [0, 1], the range the setters clamp to, and
the undo mark folds both in. Likewise `fog_start` and `fog_end` (studs) and `fog_color` [r, g, b]
(`Lighting.FogStart`, `FogEnd`, `FogColor`) are written only when they differ from the class defaults (0, 100000,
[0.75, 0.75, 0.75]); a block without them reads those defaults, the reader refuses a non-finite value or a
`fog_color` without three components, and the undo mark folds all three in. No range is enforced (the mirror
states none) and `fog_start` may exceed `fog_end`. A reader older than these keys refuses a block that carries
them, as it refuses every unknown key. Tests: `WorldPackageLightingEditModeTests`,
`WorldPackageEnvironmentScalesEditModeTests`, `WorldPackageFogEditModeTests`. Known limit:
`RbxLighting.RestoreDurableState` advances the Lighting service's revision, so a world with a `lighting`
block re-saves with a higher revision on that one node.

**StarterPlayer settings.** `world.json` carries an optional top-level `starter_player` block
(`dev_computer_movement_mode` and `camera_mode` by `Enum` item name, `camera_max_zoom_distance` and
`camera_min_zoom_distance` in studs) when any of them differs from the class defaults (`UserChoice`, `Classic`,
400, 0.5); a missing block means the defaults. Restore sets them through `RbxStarterPlayer`'s setters in an
order its zoom clamps cannot alter, and each `Player` copies them when it joins, as before; a `Player` itself
never enters a package. The reader refuses an unknown or numeric item name, a non-finite distance, a minimum
below 0 or above the maximum, and the block in a tree without a `StarterPlayer` service. No `format_version`
bump; readers up to 7.56.0 refuse the key by name. Tests: `WorldPackageStarterPlayerEditModeTests` (including
a literal 7.56.0 package that loads with the defaults and re-encodes to the same text).

**Roblox GUI.** Every world-owned GUI node the tree retains — a `ScreenGui`, a `GuiObject` (`Frame`, `TextLabel`,
`TextButton`, `TextBox`, `ImageLabel`, `ImageButton`, `ScrollingFrame`) or a UI layout object (`UICorner`, `UIPadding`,
`UIStroke`, `UIScale`, `UIListLayout`, `UIGridLayout`, `UIAspectRatioConstraint`, `UISizeConstraint`) — carries an
optional `gui` block with every property of its class a script can set, under snake_case Roblox names (`size`,
`background_color3`, `text_x_alignment`, …), in a fixed order per class (`RbxWorldPackageGuiSchema`, one table for
capture, restore, validation and both writers). Enums are their Roblox numbers; a `Color3` is `[r, g, b]`, a
`Vector2` `[x, y]`, a `UDim` `[scale, offset]` and a `UDim2` `[x scale, x offset, y scale, y offset]`, offsets integers.
A `UISizeConstraint` keeps its unbounded default as `"Infinity"`; every other number is finite. Layout results
(`Absolute*`), focus, the TextBox caret (`CursorPosition`) and the presenter's state are never written. The block is on
the nodes wherever they sit (StarterGui, ReplicatedStorage); a mod's own GUI is mod-ephemeral and every `PlayerGui`
copy sits under a `Player`, so neither is in the tree. A bindable (`BindableEvent`, `BindableFunction`) saves its name
only; callbacks are mod code. `world.json` carries an optional top-level `starter_gui` block
(`show_development_gui`, `screen_orientation` as a number, `core_gui_disabled` as the ascending `Enum.CoreGuiType`
numbers switched off, `All` expanded) when any switch differs from the defaults (true, 4, none). Restore puts both back
through the classes' setters after the tree; a node without a block restores with its class defaults. The reader refuses
a block on a class without one, a key the class does not have (by name), a missing or repeated key, a value of the wrong
JSON type, an offset that is not an integer, NaN, an infinity elsewhere, an enum number outside its items, a
non-canonical `core_gui_disabled` and a `starter_gui` block in a tree without a `StarterGui` service. A lone surrogate in
GUI text is replaced in the snapshot with an `ill-formed-text` diagnostic, and the WebGL work budget counts GUI text.
Both keys are omitted when absent, so a world without GUI writes the same bytes as before; no `format_version` or
reader-version change: readers up to 7.56.0 refuse either key by name. Tests: `WorldPackageGuiEditModeTests` (a
literal golden, a HUD reaching every class that round-trips byte-identically through the setters, an old package
without blocks, and drift guards that fail when a GUI class or a public GUI setter is added without persistence).

**Chat commands and outlines.** A world-owned `TextChatCommand` carries an optional `text_chat_command` block
(`primary_alias`, `secondary_alias`, `enabled`, `autocomplete_visible`) and a world-owned `SelectionBox` an optional
`selection_box` block (`adornee_id` as a decimal string, `"0"` for nil; `color3` and `surface_color3` [r, g, b];
`transparency`, `visible`, `line_thickness`, `surface_transparency`). Restore puts them back without advancing a
revision (`RestoreDurableState` on both classes), so a load followed by a save is byte-identical; their setters do
advance revisions, so an AI edit of only them is an undoable AI change. An adornee the package does not keep (a mod's
own part, a character, a destroyed instance) is captured as nil with a `mod-ephemeral` or `missing` diagnostic (member
`Adornee`), and an `adornee_id` that names no instance of the package is read as nil with a `missing` diagnostic
rather than refused: the outline loses its target, not the world its save. A non-finite colour is captured as the
class default with a `non-finite-value` diagnostic; the reader refuses non-finite values, a block on another class,
a missing field and an unknown key by name. A world without either class writes neither key; readers up to 7.56.0
refuse either key by name. `BasePart.LocalTransparencyModifier` is local to each machine and never saved. Tests:
`WorldPackageAdornmentsEditModeTests`.

**Highlights.** A world-owned `Highlight` carries an optional `highlight` block (`adornee_id` as a decimal string,
`"0"` for nil, which highlights the parent; `depth_mode` `"AlwaysOnTop"` or `"Occluded"`; `enabled`; `fill_color` and
`outline_color` [r, g, b]; `fill_transparency`, `outline_transparency`), with the same rules as `selection_box`:
restored without a revision advance, an adornee the package does not keep captured as nil with a diagnostic and read
as nil when it names no instance, non-finite values and unknown keys refused by name. The key is omitted when absent,
so a world without a Highlight writes the same bytes as before, and a package written before the block existed loads
its Highlights with the class defaults; no `format_version` change. Tests: `WorldPackageAdornmentsEditModeTests`
(`Highlight_*`).

**Terrain cells.** The Workspace `Terrain` node is runtime infrastructure (every data model bootstrap
creates it, so it is not in `world.json`), but its cells are durable: a world with at least one cell writes
them in a last entry `Terrain/voxels.bin` (`TerrainVoxelCodec`, CAVX v1, canonical run-length chunks; save,
load, save is byte-identical). Capture takes a copy-on-write snapshot of the cells (one reference per chunk),
so the pre-mutation snapshot an undo restores holds the cells of that moment. A package with the entry
declares `minimum_reader_version: 2` (`format_version` stays 1): readers up to 7.56.0 accept version 1 only
and refuse it ("World package requires reader version 2; this reader is version 1.") instead of opening the
world without its cells, and this reader refuses an entry under version 1. A world without cells writes no
entry and version 1, so its package is byte-identical to one written before the entry existed. The reader
decodes the entry into a scratch store held to the platform's Terrain bounds and budgets before anything is
restored, and refuses a non-canonical entry, an entry without a cell, and cells outside the bounds; restore
puts the cells into the restored world's Terrain after the tree (a composition whose class catalog has no
`Terrain` refuses such a package before any side effect). The entry is held to the entry limit and, on
WebGL, the 4 MiB package budget like every other; the write-behind proof bounds it at 12,299 bytes per chunk.
A cell write counts as a change of the world for the undo mark and for the startup record. Tests:
`WorldPackageTerrainEditModeTests`.

**Mod folders and script contexts in the manifests.** A mod's folder (P20) is its manifest's `Category`, a
`/`-separated path copied raw from the `@coreai` header (`Games/Arcade`; a one-level category is a one-folder
path), so it travels with the mod and needs no new key. Folders that hold no mod travel in an optional
`manifest.json` key `mod_folders`: canonical paths (`ModFolderPath.Parse`), case-insensitive duplicates once,
sorted by `ModFolderTree.ComparePaths`, at most 1,024 (`MaximumModFolders`). Capture reads them from the
session's shared `IModFolderListStore` (`RbxWorldRuntimeSessionController.ModFolderListStore`; a file store
keeps them in `<mod store>/.mod-folders`, `FileModFolderListStore`), and a confirmed load or restore writes the
package's list into it before the staged start and puts the previous list back when the load does not publish.
The key is omitted when there is no such folder; the reader refuses an empty list, more than 1,024 entries, and
an entry that is not canonical, is the top level, repeats another or is out of order, naming the entry and the
reason. A mod manifest may carry `Context` (`server`, `client` or `shared`; MVP4 SC-5), derived from the header's
`context:` key and omitted when empty, so every manifest of a mod without one is byte-identical to before. Both
keys are additive without a `format_version` bump: readers up to 7.56.0 refuse a package that carries either by
name, as they refuse every unknown key (`WorldPackageLightingEditModeTests.UnknownKeys_*`). Tests:
`WorldPackageModFoldersEditModeTests`, `Mvp4ScriptManifestEditModeTests`.

**Script instances: format version 2 (MVP4 SC-7, rules R3 and R4).** `Script`, `LocalScript` and `ModuleScript` are
ordinary instances whose `SourceId` names the mod they are bound to (MVP4 design note SC-1). A format version 2
package gives every such node a `script` block: `source_id` (the mod id, `""` when unbound) and, on a Script or
LocalScript only, `enabled` and `run_context` (the `Enum.RunContext` number); a ModuleScript's block holds
`source_id` alone. The meaning of `Mods/` changes with it: in version 1 every active mod starts at the legacy
placement (`ServerScriptService/<id>/<id>`), in version 2 a source no instance is bound to is detached and does not
start, which is why the format version is raised and a version 2 package declares `minimum_reader_version: 2`. The
reader accepts `format_version` 1 and 2 (`RbxWorldPackageSerializer.SupportedFormatVersions`); a payload carries
its `FormatVersion`, and `RbxWorldPackageRestoreResult.LegacyScriptPlacement` is true for version 1. It refuses, by
name: a version 2 package that declares a lower `minimum_reader_version`; a `script` block in a version 1 package;
in version 2, a script node without a block, a binding to a mod the package does not hold (dangling), two nodes
bound to one mod (duplicate), a bound node whose class and `run_context` disagree with its mod's `Context` by the
SC-4 table, and a bound node whose mod declares a context that is not server, client or shared; in any version, a
block on a class that is no script, `run_context` 3 (`Plugin`) or an undefined number, a `run_context` other than 0
on a LocalScript, and `enabled` or `run_context` on a ModuleScript.

- **The writer picks the lowest version that can express the world (rule R4,
  `RbxWorldPackageSerializer.FormatVersionFor`).** A world whose saved tree holds no `Script`, `LocalScript` or
  `ModuleScript` and none of whose mods declares a context is written as version 1, byte for byte as before script
  instances existed: a mod that declares nothing runs from its legacy script, mod-owned runtime infrastructure that
  is never saved. A world with a saved script instance, or a mod whose header declares `context:` (its manifest's
  `Context`), is written as version 2.
- **What a version 2 capture repairs**, in the snapshot only, is what its reader would refuse: a binding to a source
  the package does not hold is saved unbound (`missing`, member `SourceId`), and a world-owned bound script inside a
  mod-ephemeral subtree is left out (`mod-ephemeral`, member `Script`), so its source loads detached. Two instances
  bound to one source, or a binding that disagrees with its source's context, can only come from a runtime defect, so
  a version 2 capture fails on it instead of saving another script than the one running.
- **How a loaded world starts its mods (rule R3).** In version 1 every active mod is loaded: a mod that declares nothing
  at the legacy placement, a declared mod at its placement, where it binds the unbound node the version 1 save kept
  for it. In version 2 a declared source (a header `context:` or `parent:`, or a stored `Context`) whose instance the
  world does not hold is detached: it is loaded and listed (`run_state` `detached`) but does not start, and a reload
  places a new instance and starts it; a mod that declares nothing still starts at the legacy placement. Whether a
  loaded declared source starts follows the run predicate (`Docs/CoreAIMods/mod-system.md`, "Where `script` lives"): a
  `context: shared` source (a ModuleScript) never starts as a script, nor does a saved script with `enabled` false, one
  in a container that does not run it, or one whose side the machine lacks (a client script on a dedicated server).
  Client scripts start after every server-side one. The load reports every active source it loaded without starting
  in `RbxWorldLoadResult.NotStartedMods` with its run state (`door (detached)`, `tools (module)`,
  `hud (not-on-this-side)`, `gate (disabled)`) and in one session diagnostic. The default world's store holds no tree,
  so its boot start is the version 1 way.
- Readers up to 7.56.0, and every reader before this one, accept `format_version` 1 only and refuse a version 2
  package by its version ("Unsupported world package format version 2").

Tests: `WorldPackageScriptsEditModeTests`, `ScriptSnapshotEditModeTests`, `ScriptWorldPackageEditModeTests`,
`ScriptSessionEditModeTests`.

An in-memory instance snapshot also keeps the runtime-infrastructure flag (`InstanceSnapshot.IsRuntimeInfrastructure`,
`InstanceRegistry.RestoreInstance(..., isRuntimeInfrastructure)`), so a world restored straight through
`InstanceTreeSerializer` keeps its Terrain as infrastructure: it is no authored content and costs no instance quota
after the round trip. `world.json` has no key for the flag; its projection leaves such nodes out. Tests:
`RuntimeInfrastructureSnapshotEditModeTests`.

**Client staging.** `RbxWorldPackageRestoreOptions.RegistryAuthority = Replica` restores into a replica registry
inside one replication-apply scope, so a client's copy keeps the server's ids and every captured revision and
nothing is marked diverged; a later local write is. `ReplicaRbxWorldSessionHost` stages and commits such
worlds like the headless host (MVP4 SC-16), and refuses, before anything is restored, a payload that a client must
never hold (`RbxClientJoinPayload.RefuseServerContent`, step C5): a `Mods/` entry that declares no context (legacy)
or `server`, a `Script` bound to a source whose `RunContext` is Legacy or Server, or any instance under
`ServerScriptService` or `ServerStorage`. Tests: `Mvp4ReplicaStagingEditModeTests`, `ClientCompositionEditModeTests`.

**The join payload (MVP4 step C5).** `RbxClientJoinPayload.CaptureForRecipient(context, recipientActorId, filter)` is
the package a joining client receives, in process for now (MVP5 carries it over a socket, MVP6 the deltas): the
server's capture with every instance the replication filter hides from that recipient left out (the default filter is
Roblox's: nothing under `ServerStorage`, `ServerScriptService` or another player's `PlayerGui`/`Backpack`), the
DataModel's services kept as empty shells, a visible script bound to a server or legacy source kept unbound (a
`Script` in a `Workspace` part reaches every client without its source), `Mods/` holding only the `client` and
`shared` sources bound to a visible instance, per-instance state (parts, lights, GUI, chat commands, selection boxes)
only for the instances kept, a `PrimaryPart` or `ObjectValue` reference to a hidden instance read as nil, and no
capture diagnostics. Owner and ACL metadata are not stripped yet (MVP5, MP-20). Players are never in a package, so a
client's local `Player` arrives by replication. `RbxClientWorldComposition.Join(payload, options)` stages it into a
replica registry, builds the Roblox surface over it with a client topology and the local player's actor, and starts
its sources from an in-memory store that persists nothing: modules register, client scripts wait for the local
player and start at the frame after it appears.

The durable v1 surface is class/name/Archivable, world-owned origin/ACL/revision metadata, attributes,
tags, BasePart properties, Model PrimaryPart/stored WorldPivot, ClickDetector distance, camera CFrame,
ValueBase `Value` payloads (Int/Number/String/Bool/Object/Vector3/CFrame/Color3; ObjectValue targets
must name a serialized instance in the same package, nil is 0), and exact Lua mod manifests/source. `MaterialVariant` instances and part `MaterialVariant` references
are durable package state, and so are the optional blocks above (PointLight, Lighting day cycle, StarterPlayer, GUI,
StarterGui, Terrain cells, mod folders). `OwnerModId` is teardown bookkeeping used to form the projection,
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
is lost on the next save.

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
`load_world`/`load_autosave` request is refused before the player is asked, because loading it would
switch off every cross-actor check and write the downgrade into every later save. A composition that
must open legacy worlds is composed with `worldAclVersion: null`.

Version 1 limits compressed packages to 64 MiB, each expanded entry to 64 MiB, all expanded entries to
128 MiB, ZIP entries to 2,048, mods to 256, instances to 100,000, hierarchy depth to 2,048, and
attributes/tags to 256 each per instance (`RbxWorldPackageLimits.Format`).

The entry limit was 16 MiB up to 7.56.0 and was raised so that a world at the desktop instance ceiling
(16,384 charged instances) can be backed up; nothing else in the format changed. Measured on the writer
(CoreCLR, `world.json` bytes): the reference grid of 16,384 plain Parts writes 19.6 MB (1,192 B per
instance); the heaviest realistic world at the ceiling, every charged instance a rotated, coloured Part
with a fractional size, one of twelve materials, eight attributes and four tags, built by AI console
calls, writes 47.8 MB (2,915 B per instance) and compresses to 3.1 MB, which leaves a quarter of the
64 MiB entry limit free (`WorldPackageCeilingEditModeTests` pins both the fit and the headroom). The
expanded total stays 128 MiB, so the most a reader inflates per package is what it was. A reader up to
7.56.0 reads every package whose entries are within 16 MiB exactly as before (the entries are byte for
byte what it wrote), and refuses a larger `world.json` by the size its ZIP header declares, before
inflating it ("World package entry 'world.json' is N bytes; limit is 16777216."). A reader may be held to
lower limits than the format's, never higher (`RbxWorldPackageSerializer.ReadPackage(bytes, limits)`):
`RbxWorldPackageLimits.Version1Baseline` is the 7.56.0 set, `RbxWorldPackageLimits.WebGl` the browser's.
Every inflated entry must also stay within the size its header declares, so an understated header cannot
let entries inflate past the expanded total.

The per-instance limits are also enforced at the source, so a
script cannot build a world that cannot be saved: a `SetAttribute` that would add a 257th attribute,
an `AddTag` that would add a 257th tag, and a `Parent` assignment that would put an instance deeper
than 2,048 levels (`InstanceTreeSerializer.MaximumSnapshotDepth`) raise `BAD_ARGUMENT`. The byte
limits are not enforced at the source.

The mod limit holds at the source too. A live world stores at most 256 distinct mod sources — an
unloaded mod keeps its source, `manage_mods` action `forget` removes it — so a load that would add a
257th distinct source is refused before its chunk runs, with a hint to forget a mod first, and a mod
whose source is already stored is never refused. `FileLuaModSourceStore` refuses a 257th stored id and
an exact replacement set of more than 256 sources the same way: its `Save` throws the refusal. The
session and the store count by one rule, `ILuaModSourceAdmission.CanAdmit` (public, implemented by the
file store and the session store), so an unreadable manifest cannot let the session admit a mod whose
source the store then refuses to keep. A load whose source the store did not keep anyway is undone
as a failed load — the formula it displaced is put back and no revision is written — and the tool result
says why. A world that is already past the limit cannot be captured
(`RbxWorldPackageFormatLimitException`), so `forget` — and only `forget`; an `unload` keeps the source
and gains nothing — runs without its pre-mutation backup until the world is under the limit again,
while every other gated mutation stays refused. A bare `LuaCsModRuntime` composition without the world
session gets only the store's refusal, which is logged: its 257th mod runs without a persisted source.

The writer never throws on a lone surrogate either: text that did not come through capture (mod
manifests, settings, a hand-built payload) has it replaced with U+FFFD before the strict UTF-8 encode;
the reader still refuses invalid UTF-8 bytes. A world that captures but still cannot be encoded — its
`world.json` past the entry limit, or past the WebGL write budget — gets a failed
`RbxWorldPackageWriteResult` with `PackageCannotBeEncoded` set, as opposed to an I/O or durability
failure. The gate treats it like a world past the mod limit: `forget` and the safety autosave of a
confirmed load run without the backup they can never get, and say so (`backup_warning` in the
`manage_mods` result, `RbxWorldLoadResult.BackupWarning` for a load, shown in the Hub); every other
gated mutation stays refused, and a durability failure still refuses everything. A world past the mod
limit can be replaced by a confirmed load the same way.

The hierarchy validator and capture traversal are iterative and linear. Capture checks depth/count
before accepting each node. The writer preflights
exact UTF-8 expanded size, so it cannot emit a package its reader rejects on aggregate expansion. The
writer emits `world.json` straight from the payload (`RbxWorldPackageWorldJson`), byte for byte what the
reader's DTOs serialized to, and refuses a null in a property the reader requires, as the serializer did;
the reader decodes each JSON entry from its bytes without materializing it as one string. JSON
DTO materialization still precedes the 100,000-instance semantic check; hostile 16 MiB `world.json`
browser peak memory remains a residual risk (a WebGL reader keeps the 16 MiB entry limit).

The world-owned projection runs after the generic instance snapshot has materialized the live tree,
so mod-ephemeral subtrees still count toward the snapshot's depth, count and allocation budgets.

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
either limit writes nothing and `save_world` returns it as an ordinary failed result. No tool or Hub
page deletes a manual slot, so a store at its cap stays full until the player removes files under
`persistentDataPath/CoreAI/Saves/Manual` by hand, which a WebGL player cannot do. When a store opens it
deletes the temporary files a crash left behind — only
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
the deterministic trigger `manage_mods-<action>`; `edit` is a reload of the edited source and uses
`manage_mods-reload`. Read-only `list`, `get_source`, `export`, `versions`,
and `diagnostics` bypass capture and autosave. A null or unsuccessful write result, store/capture
exception, or cancellation prevents the Lua/runtime mutation and becomes the tool's structured
failure. Manual slots are not read, written, or rotated by this gate. An `execute_lua` queued behind a
confirmed load keeps its own error and gets the explanation (the world it ran against was replaced)
after it, instead of in its place.

**Write-behind autosaves.** With `ConfirmedWorldMutationGate.WriteBehindBackups` on and a store that
implements `IRbxWorldAutoSaveWriteBehindStore` (the file store does), the durable write of the autosave
runs behind the mutation; the safety autosave of a confirmed load stays durable-first. What stays in front
of the mutation is the capture and the store's `PrepareAutoAsync`, which decides every refusal the encode
could raise. The file store makes that decision without encoding when it can: it validates the payload as
the writer does, then bounds every entry from above without formatting a number or deflating a byte
(structure and text counted exactly, each float at 16 characters, each double at 25, each line break at two,
zlib's worst case for every entry plus its ZIP headers) and compares the bounds with its platform's limits.
When every bound fits, the encode itself runs in `CommitAutoAsync`, after the pending marker and one
player-loop frame later than the mutation (`UniTask.NextFrame`; no wait in Edit Mode or without a player
loop), so the frame that shows the change is drawn first, and then a frame budget at a time
([Encoding a frame at a time](#encoding-a-frame-at-a-time)), without the payload validation the proof already
ran; when a bound does not fit, the encode runs in front of the mutation as before and decides exactly. When
the next gated mutation has to wait for this backup (it settles the pending write before it runs), the gate
asks the encode to finish at once (`RbxPreparedAutoSave.RequestEncodeAtOnce`): the wait for its next frame
ends and the rest runs in that one step, since the caller waits anyway. A failure of the deferred encode,
which the proof rules out unless the payload changed after it, is a lost backup like a failed write: the next
gated mutation backs itself up durably first. The pre-mutation snapshot the undo service keeps holds the
payload until the encode finishes and then only the compact package.
`FileRbxWorldPackageStore(defersProvenEncodes: false)` encodes every autosave in front;
`waitBeforeDeferredEncodeAsync` replaces the frame wait (before the deferred encode and between the frames of
every encode). Measured at 2,048 grid Parts
(CoreCLR, contended machine, see `dev-docs/PERF_BASELINE_2026-11.md`): the proof costs a few milliseconds
where the encode cost over a hundred. Tests: `WorldPackageEncodeBehindEditModeTests`.

A world session needs more from the shared gate than serialization: to keep the startup selection
(below) in step it must see each mutation start and end, and it runs the boot-time startup restore
under the gate. That second face is the public `IStartupAwareWorldMutationGate` (`IsHeld`,
`MutationStarting`, `AfterMutationAsync`, `ExecuteWithoutBackupAsync`), which `ConfirmedWorldMutationGate`
implements and a host gate that wraps one forwards. A host gate without it still serializes and backs
up every mutation, but the controller says once, through its diagnostics, that it falls back: the
startup world then follows mod source changes only (recorded from the frame pump), gated changes to the
world tree are not recorded for the next start, the boot restore runs without the gate, and a source
change may be recorded in the middle of a gated mutation (the next change records the finished state).

### Encoding a frame at a time

Every package the file store writes — the autosave behind a mutation, a durable-first autosave, a manual save,
a startup selection and the record of a kept world — is encoded in units of bounded work
(`RbxWorldPackageSerializer.IncrementalEncoder`, `Infrastructure/RbxWorldPackageIncrementalEncoder.cs`) and
spread over frames (`RbxWorldPackageEncodeFrames`). A unit is the payload validation, one manifest, the head,
the tail or 16 instances of `world.json`, one mod's two entries, one Terrain chunk, 64 KiB hashed for the
startup digest, 4 KiB handed to deflate, or the close of the ZIP. `WritePackage` and `ComputeEntryDigest` are
the same encoder run in one go, so a package written over many frames is byte for byte the one written at
once. Each entry reaches deflate in 4 KiB slices at fixed offsets whatever the frames did, so the bytes never
depend on the timing; on Mono (the editor and Mono players) and .NET Framework deflate gives the same stream
however its input is split, so the bytes are also those of the writer before this change (pinned in the
editor by `Deflate_OnMono_GivesTheSameStreamHoweverItsInputIsSplit` and the byte comparison in
`WorldPackageChunkedEncodeEditModeTests`); CoreCLR's deflate does not, so in the engine-free suite only the
entries equal the old writer's. The format, the limits and every refusal (in the writer's order) are unchanged.

The work of one frame (a slice) stops at the first unit that ends past its budget, measured with the host
clock (`Stopwatch.GetTimestamp`, which a WebGL player has too), and the budget follows the frames that
actually come:

- the gap is the time from the end of the encode's previous slice to the start of this one (the rest of the
  frame; the encode's own work is not in it, so a long unit never inflates the next budget);
- the budget is the store's (`FileRbxWorldPackageStore(encodeFrameBudget:)`, default
  `DefaultEncodeFrameBudget` = 4 ms) or a third of the gap, whichever is more: at most about a quarter of the
  frame, 4 ms at 60 fps;
- a gap of 250 ms or more — a hidden or throttled browser tab, below 4 fps, where nobody sees a hitch —
  runs all the remaining work in that slice, and every later slice of the same encode does too. A deferred
  autosave measures the gap across its first wait, so in a tab at 1 fps it settles in its first slice, one
  frame after the mutation, as the one-block encode did; any other encode settles in its second slice;
- a request for the encode at once (the gate settling the backup for its next mutation) does the same, and
  ends the wait for the next frame immediately;
- a budget of zero runs exactly one unit per slice (tests).

The record of a kept world (and the confirmed load's selection) computes its startup digest with the same
encoder and then goes on to the package from the same entries, so the world is encoded once per record
instead of twice; the package starts at the next frame, since the digest's last slice may have spent this
one. The store encodes before it takes its transaction, so a long encode never holds a startup read or a
commit behind a mutation. What stays in one frame: the capture (the tree, Terrain and mod sources read into
a payload), the payload validation unit on the paths that validate (the deferred backup skips it, its proof
ran it), and, when two encodes run at the same time (a backup and a record), each slices to its own budget.

Measured engine-free on a contended machine (`tools/perfbench --only edit-gate`, frames played as 12.7 ms of
game per frame or a 1 s hidden-tab gap; Unity players and WebGL not measured): the seed-1337 Block World at
view radius 8 (379 Terrain chunks, nine mods) recorded in 75-91 ms in one frame before, and now takes 53-66 ms
of work over 13-16 frames, the longest 4.8 ms (CoreCLR); the perfbench Block World-sized payload records in
11 frames, the longest 4.7 ms (CoreCLR), or 16 frames, the longest 5.1 ms (the Mono CLI with the Boehm
collector), and in two frames at one frame a second. On the Mono CLI a frame of a 2,048- or 16,384-Part
encode can still reach about 40 ms, most likely a stop-the-world collection of that collector (it
grows with the heap, and on CoreCLR the same units measured alone stay under 0.8 ms but for collections);
whether Unity's incremental collector removes it is not measured. Tests:
`WorldPackageChunkedEncodeEditModeTests`.

**When the play world stops.** An encode spread over frames waits for frames that a closed scene, a quitting player
or an unloaded page may never run, so the session controller's `Dispose` finishes every record of the startup world
and the autosave behind the last gated mutation in one step, inside the call
(`RbxWorldRuntimeSessionController.FinishWritesInFlightAtOnce`, which a host may also call itself;
`LuaModRuntimeTickDriver` calls it on an application pause and on quit). "Finished" means encoded: the file write
that follows still yields a frame per 64 KiB written, as every package write always did, so a player that stops
running frames at once can still lose a write of more than 64 KiB, exactly as before the incremental encode. A
record that starts after the stop is not spread over frames either. A browser gives no callback on a page unload
(Unity's documentation: the Web platform does not support `OnApplicationQuit`): there, a record or a backup still encoding when the page closes is lost (before the incremental
encode only its write was in flight, a frame or two; now the encode is too, about 13-16 frames for a Block World's
record at 60 fps), and the autosave behind a mutation is reported at the next start as a missing backup. A hidden tab
whose player runs on `requestAnimationFrame` (Unity's default, no `Application.targetFrameRate`) runs no frames at
all, so a record waits there until the tab is shown again. A change made while a record is encoding gets its own
record afterwards (the record took its pending state when it captured the world). Tests:
`KeptWorldRecordAndBackupBehind_MidSlicesWhenThePlayWorldStops_AreFinishedAtOnce`,
`KeptWorldRecord_FinishWritesInFlightAtOnce_EndsTheWaitForFrames`,
`KeptWorld_AChangeWhileARecordIsMidSlices_GetsARecordOfItsOwn`.

**When a browser stores a write.** Read from the loader Unity 6000.6 ships (`BuildTools/prejs/IdbFs.js` and
Emscripten's `libidbfs.js`), with `config.autoSyncPersistentDataPath = true`: a file write lands in the page's
in-memory file system at once; closing a written file, a rename, a delete or a new directory queues a persist that
starts after the current frame (`setTimeout(0)`); the persist lists every file of `persistentDataPath`, reads every
key of the IndexedDB store, then writes every changed file whole in one IndexedDB transaction. A write made while a
persist runs queues one more. A page closed or reloaded before that transaction commits loses that persist (the
previous one stays, a transaction is all or nothing), and the loader drops a persist's error (a full quota) without
a word. Every page of an origin shares one IndexedDB copy and each persist writes its own page's view of the whole
folder, removing files that page does not have, so two open pages of a game overwrite each other's saves. None of
this was timed in a player.

### The change journal of a kept world

A world kept between starts writes a player's Terrain changes to a small journal next to its newest startup entry
(`Startup/<entry>.journal`, `RbxKeptWorldJournal`) in the frame they were made, and the full record of the world
(after the quiet time, at most every 15 s while it keeps changing) takes the journal's place. A start opens the entry,
replays its journal over the entry's Terrain and then starts the world, so a block placed a moment before a reload is
there after it without waiting for a full record.

- **What is journaled.** A Terrain write made outside the generator's mark (`CoreAITerrainGenerating`), of at most
  4,096 cells, in chunks the entry holds. A record holds the cells of the changed box as they are after the write
  (values, not the operation), so a write replayed twice in order gives the same cells. One placed or broken block is
  29 bytes (an 8-byte length and CRC-32, the box, one material and one occupancy byte per cell); the file starts with
  8 header bytes. Measured engine-free on a contended Windows machine: 0.36 to 1.6 ms a block for the file's open,
  append and close (once a frame, whatever the number of writes in it), a journal of 5,000 blocks is 145,008 bytes, and
  replaying it at a start took 6 to 14 ms.
- **What waits for the full record.** A write in land the entry does not hold (a generator that fills only empty
  cells would fill a block the player broke there again at the next start), a write over 4,096 cells, a reset of the
  Terrain, every change that is not a Terrain write (AI changes, instances, mod sources, Workspace attributes such as
  the Block World's torch list), and any write once the journal holds 256 KiB (a browser writes the whole file again
  after each frame that changed it).
- **Order.** A record takes the journal's position when it captures the world; once its entry is written, the writes
  journaled after that capture become the new entry's journal and the older journals are deleted, in the same step, so
  one browser persist carries both. A write made while a record encodes is in that record or in the journal after it.
- **When the journal stops.** A confirmed load, an undo or redo (the world is replaced in place), a cleared startup
  selection and a start whose restore fell back stop the journal until the next record of the world starts it again;
  meanwhile writes wait for that record. A cleared selection deletes the journals.
- **A journal that cannot be read.** A record whose length or checksum does not match (a write cut short by a crash or
  a reload) ends the replay there: the records before it are applied, the rest is reported (a diagnostic and the
  save state's note), and the file is cut back to its readable part so later appends stay readable. The entry itself
  is never touched.
- **A store of its own.** The journal writes the file store's startup directory directly, so a store built over its
  own file system (a test double) keeps no journal, and every change waits for the full record.

Tests: `KeptWorldJournalEditModeTests` (engine-free), `KeptWorldJournalRestoreEditModeTests` (in Unity: the restore reads
packages with player-loop frames).

File reads/writes are chunked with PlayerLoop yields. Decoding is not incremental, and the WebGL write budget
below predates the incremental encode and is kept as it was, so
the actual WebGL player writes with the 7.56.0 entry limit (16 MiB, `RbxWorldPackageLimits.Version1Baseline`)
and reads with `RbxWorldPackageLimits.WebGl` (4 MiB compressed, 16 MiB per entry), and it refuses to write
a package above 4 MiB, with more than 4,096 instances, more
than 32,768 collection items, or more than 2 MiB of text characters before entering unbounded work.
The text count covers every string the encoding writes: names, ledger metadata, value payloads
(`StringValue` text included), Humanoid state, attributes, tags, mod sources and manifests, and
material names. A WebGL read checks only the 4 MiB byte size: the instance, item and text budget holds
for writes, so a package another build wrote within the format limits can still cost a WebGL reader
decode work beyond that budget. These are WebGL execution limits, not format limits. So that a script cannot
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
default to `CoreAiWebGlPersistence.SyncAsync`. No real-browser test yet saves, reloads the page and
checks that the bytes and the startup selection survive. Production composition injects one shared
gate into both the initial/replacement `execute_lua` stacks and the production `manage_mods` tool,
and the declared mutating actions of both tool contracts are covered.

### Startup selection (the world that opens on the next start)

A player-confirmed load survives a process restart. `FileRbxWorldPackageStore` keeps a startup area
`Saves/Startup/` (namespaced as `Startup/Stores/s-<26>/` when the composition has a mods store id, or under
the `id-<64 hex>` folder an earlier build created, see `CoreAiStorageNames` in `mod-system.md` §8,
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
made after the confirmation reopen too. What triggers a record (`RbxWorldRuntimeSessionController`,
`Infrastructure/RbxWorldPackageContracts.cs`):

- **A change to the mod sources is recorded at once**, whoever made it. A gated call (`execute_lua`, a
  mutating `manage_mods` action) records it while the gate is still held; a write to the session's
  source store from anywhere else — the Hub **Mods** page, host code — is recorded by the frame pump at
  the next frame (so it needs the pump), never under the shared gate, and at most once per frame.
- **A change to the world tree alone** — a world-owned instance created, removed, re-parented or
  written, seen through the registry's events while a gated call runs, or a change across that call of the
  state whose setters raise no event (the `lighting`, `starter_player` and `starter_gui` blocks and the
  PointLight and GUI properties, compared by `RbxWorldStateMark.Values` at its start and end; a day-cycle
  script writing ClockTime between calls is not seen) — is recorded at most once per
  `StartupRefreshInterval` (5 s by default; `TimeSpan.Zero` records every change at once). The first
  change after a quiet interval is recorded at once; a change inside the interval is recorded by the
  frame pump once it has passed, together with every change made meanwhile. A change made within the
  interval before the process ends is not recorded. Each record is a whole package written, synced and
  pruned while the gate is held — on WebGL up to 4 MB handed to the browser — which is why an AI
  building in many small calls does not write one per call. Its digest and its encode run a frame budget
  at a time ([Encoding a frame at a time](#encoding-a-frame-at-a-time)); only the capture is one frame.
- **Nothing else is.** A call that changed neither — a read-only call, one that only moved parts through
  physics or moved the camera, or one that changed only the mods' own instances, which no entry holds —
  writes nothing, and an entry whose content equals the newest one is not written again.
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
usable. Mod-data writes made while the staged session starts (`store_set`, and clearing a mod's
data) go to a session overlay and reach the durable mod-data store only after publication. Script revision writes use the same deferred-publication
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

**Live network sessions.** A world load is refused with status
`network_sessions_active`, and the live world is left unchanged, while the network bridge lists
registered actors on a non-`Solo` topology — at request time, at confirmation and on a raw host load.
The rule and the ACL floor are checked once more under the session lock right before publication, so a
client that joins while the safety autosave is written makes the load fail and roll the staged world
back instead of having its world replaced; `RbxWorldLoadResult.Status` then names the reason
(`network_sessions_active`, `invalid_package`).
Live Mirror sessions cannot be handed to a new world: there is no session handoff yet (roadmap MVP5,
host mode). The check is conservative (any registered actor counts), and loopback actors on the solo
bridge never block a load.

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
session's refusal text), `read_failed` (an I/O failure), `network_sessions_active` (the live-network
guard above) and `session_unavailable` (the world session was already shut down because the game is
closing or restarting it); no request is created. `invalid_package` also covers a package the
confirmation would refuse, refused before the player is asked: an active `Full`-capability mod, or a
source store that cannot atomically replace a source set. `list_autosaves` reports a store failure as
`list_failed`. An `execute_lua` call whose world a confirmed load replaced while it waited or ran
returns an error that says so ("execute_lua ran against the previous world: …"), because none of its
changes reached the live world.

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

The world package (roadmap MVP3) closed in 7.47.0. Each item of its Definition of Done is proven by a
named EditMode test that fails on a wrong implementation (fixtures `Mvp3WorldPackageEditModeTests`,
`Mvp3WorldPackageFollowUpEditModeTests`, `Mvp3WorldPackageQaEditModeTests`,
`RbxWorldHostDiWiringEditModeTests`):

| DoD item | Proving tests |
|---|---|
| (a) save → load round-trips the world-owned tree with stable ids, golden comparison | `WritePackage_AuthoredWorld_MatchesLiteralGoldenJson` (literal `manifest.json`/`world.json`, ids above 2^53 as strings), `ReadPackage_HandWrittenLiteralPackage_RestoresLiteralIdsParentsRevisionsAndPartState`, `WorldOwnedPayload_WithPackagedLuaSources_CodecRestoreRecapture_RoundTrips` |
| (b) mods restart clean on load | `ConfirmedPackageLoad_SwapsEveryFacadeAndRestartsOnlyActiveModsOnce` (active mods start once; the outgoing registry keeps no `OldCallback` and the outgoing scheduler's `LiveThreadCount` is 0) |
| (c) a manual slot is untouchable by AI tools; restore only with player confirmation | `SaveWorldTool_SecondSaveToSameSlot_IsRefusedAsResultAndKeepsFirstBytes` (the second save differs), `WorldPersistenceSurface_ExposesNoDeleteOverwriteRemoveOrReplacePath`, `ProgrammerRole_WorldTools_AreExactlySaveLoadListAndLoadAutosave`, positive confirm on the real controller in `StartupSelection_ConfirmedManualLoad_RestartRestoresSameTreeAndExactSources` |
| (d) the autosave ring rotates and records triggers | `FileStore_DefaultAutosaveCapacity_IsTenAndRotatesOnlyTheOldest`, `ListAutoSaves_HyphenatedTriggers_RoundTripExactly`, `ListAutoSavesTool_ReturnsExactNameTriggerTimestampAndSize`, `ConfirmedBackup_GatedExecuteLua_WritesExactlyOneExecuteLuaAutosaveToFileStore` |
| (e) WebGL persistence after save | `FileStores_WithoutInjectedHook_DefaultToCoreAiWebGlPersistenceSyncAsync`; the `false`-is-failure rule by the store durability tests; the real-browser reload smoke is an open follow-up |
| (f) an invalid slot or autosave name is a JSON result | `SaveWorld_InvalidSlot_IsRefusedAsResult_WithoutCallingService`, `LoadWorld_InvalidSlot_IsRefusedAsResult_WithoutCallingService`, `LoadAutoSave_InvalidName_IsRefusedAsResult_WithoutCallingService` (including `null` and the echoed slot) |

Open follow-ups: the real-browser WebGL page-reload smoke and the IL2CPP/WebGL player checks.

## Compatibility policy

Unsupported format, schema, API, or minimum-reader versions fail explicitly. A semantic or entry-layout
change increments `format_version` and adds an explicit decoder/migration path. Additive metadata still
requires a compatibility decision; decoder removal is an explicit breaking change.
