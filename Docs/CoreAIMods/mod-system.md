# CoreAI Mod System & UI

How mods are identified, stored, shipped with a game, managed on the Hub, and kept alive when they
fail. Everything on this page is built; §7 lists what is not built yet.

See [runtime mod contracts](../../Assets/CoreAIMods/Runtime/LuaExecution/README.md) for version-write
grants, persistence failures/retries, dispatch fairness, HTTP lifetime/backpressure, protected Luau
iteration and identity-verified legacy storage migration.

## 0. What the mod system does

- Mods are authored by the **LLM** (through `manage_mods`) or by the **player** (paste, edit and create
  on the Hub). Both go to the same store and have the same lifecycle.
- The **game ships with mods** (bundled), some enabled by default, and a newer bundled version
  updates the installed copy at the next start (§3).
- The world changes **at runtime**: the AI and the player write and edit mods without a rebuild.
- **Categories** group mods on the Hub's Mods tab.
- One **CoreAI Hub** window (UI Toolkit, event-driven) hosts the Mods tab.

## 1. Mod identity & in-source manifest (the "passport")

A mod declares its metadata in a header at the very top of its `.lua` source. Lua ignores it (it is a
comment); C# parses it with `LuaModHeader.Parse(source, fallbackId)`
(`Assets/CoreAIMods/Runtime/LuaExecution/LuaModHeader.cs`). It is the single source of metadata for
bundled mods; no separate `manifest.json` is needed at authoring time.

```lua
--[[@coreai
id: first_person
name: First-Person Controller
version: 1.0.0
active: false
capabilities: All, Full
category: player/controllers
author: CoreAI
description: WASD to move, mouse to look.
tags: movement, camera
]]
```

- `id` — stable key; never change it, it drives update matching.
- `version` — semantic version; a higher bundled version triggers an update (§3.2).
- `active` — the enabled state at the **first** seed only.
- `capabilities` — the requested tiers, intersected with the host grant on load.
- `category` — a `/`-separated path; the Hub groups mods by it.
- `tags` — optional, comma-separated.

Parsing rules:
- The block form must be the first thing in the file: the source starts with `--[[@coreai` and the
  block ends at the first `]]`. A run of `-- @coreai key: value` line comments at the top is accepted
  instead.
- Each entry is `key: value`; keys are case-insensitive; unknown keys are ignored. The value runs to
  the end of the line, so do not put a comment after it.
- Defaults when absent: `id` = the fallback id (e.g. the file name), `name` = id, `version` = `0.0.0`,
  `active` = true (any value other than `false` counts as true), `capabilities` = `All`,
  `category` = "" (root), others empty.
- `capabilities` is a comma- or space-separated list of `LuaCapabilities` names; unknown names are
  skipped.
- The parser is a pure function with no Unity dependency.

## 2. Persisted manifest

`LuaModManifest` (`Assets/CoreAIMods/Runtime/LuaExecution/LuaModManifest.cs`) is a plain JSON DTO
stored next to each mod's source; fields missing from older JSON read as their defaults. Besides id,
name, description, version, author, owner, capabilities, `Active` and `Entry`, it carries:

- `string Category = "";`     — "/"-separated category path.
- `string Tags = "";`         — comma-separated tags.
- `string Origin = "";`       — "" = user-authored; otherwise the bundled source that seeded it (its
  `IBundledModSource.Origin`: `resources` for the shipped Resources source, `bundled` when a source
  leaves it blank).
- `string SeededVersion = "";`— last bundled version seeded into this store entry.
- `string SeededHash = "";`   — FNV-1a hash of the source at seed time.
- `bool UpdateAvailable = false;` — when true, the Hub row shows **Update available** with an
  **Apply update** button (`IHubModService.ApplyBundledUpdate`, which re-seeds the mod from its bundled
  source and clears the flag). `BundledModSeeder` writes it as false, because it applies a newer
  bundled version itself (§3.2).
- `long LoadOrder = 0;` — where the mod sits in its world's load order. A first load, and
  a mod created on the Hub, is stamped with one past the highest value in the store (dormant mods
  included; `LuaModManifest.NextLoadOrder(store)` is the shared rule for host tools that write
  manifests). A reload, a Hub edit, a bundled update and a seeder update keep it; an unload followed by a
  load moves the mod to the end; a rehydrate never stamps. `0` (or less) means no recorded order: a
  store or package written before the field existed, or a bundled mod seeded on a fresh install. Both
  restart paths (`RehydrateFromStore` and a world restore) start mods without an order first, by
  ordinal id, then ordered mods ascending, ties by id — so a mod may use at init what an earlier mod
  made. Two concurrent first loads may receive the same value; they then start by id. The field is
  omitted from JSON when `0`, so an unordered manifest is byte-identical to one written before it
  existed. A recorded order is 1 to 2^53 (`LuaModManifest.MaximumLoadOrder`, `IsRecordedLoadOrder`); a
  larger stored value (only a hand-edited store holds one) reads as no order and is skipped when the
  next one is stamped, and a world package that carries one is refused. A first load while the store
  cannot be listed (`ILuaModSourceStore.List` throws, or the file store answers a listing it marks
  unreadable) stores the mod without an order instead of stamping it first.
- `bool SuspendedAfterBudgetTrips = false;` — set together with `Active = false` when the runtime
  quarantined the mod after repeated budget trips (§5a), so neither a restart nor a world restore starts
  it again; a successful load or reload by hand writes a manifest without it. Omitted from JSON when
  false, and carried by the world package's mod entries.

A host that writes its own `ILuaModSourceStore` (`Assets/CoreAIMods/Runtime/LuaExecution/ILuaModSourceStore.cs`)
follows its contract: `Save` may throw to refuse keeping a new mod (the file store does past the
world-package limit of 256 mods, with `RbxWorldPackageFormatLimitException`), and `List` throws when the
store cannot be listed at all rather than answering an empty list — a load stamped meanwhile then records
no order, and a world capture fails instead of saving the world without its mods. The bare Lua-CSharp
runtime persists best-effort; a world session is stricter and undoes a load whose source the store did not
keep. A store that implements the public `ILuaModSourceAdmission.CanAdmit(modId, out refusal)`
(`Assets/CoreAIMods/Runtime/Infrastructure/RbxWorldPackageContracts.cs`) lets the session refuse such a mod
before it runs, by the same rule the store applies; without it the session admits a new mod while the
store lists fewer manifests than the limit.

## 3. Bundled sources & the seeder

`IBundledModSource`, `BundledMod`, `ResourcesBundledModSource` and `BundledModSeeder` live in
`Assets/CoreAIMods/Runtime/Infrastructure/`. `CoreAiModsInstaller` registers
`ResourcesBundledModSource`, and at startup in play mode it runs the seeder into the default mod store
**before** the startup world is restored or `LuaCsModRuntime.RehydrateFromStore` runs. Five sample mods
ship in `Assets/CoreAIMods/Runtime/Resources/CoreAIMods/` (`sample_welcome.lua` active;
`sample_lane_racer.lua`, `sample_tetris3d.lua`, `sample_clicker.lua` and `sample_castle3d.lua` opt-in).
Unit-tested in `BundledModSeederEditModeTests`.

### 3.1 Sources

`IBundledModSource` has an `Origin` marker and `Load()`, which returns the discovered mods as
`BundledMod { string Id, string Source, string Version }` (id and version parsed from the `@coreai`
header). CoreAI ships one source:

- `ResourcesBundledModSource` — `Resources.LoadAll<TextAsset>("CoreAIMods")`, origin `resources`
  (the project's Lua scripted importer makes `.lua` a `TextAsset`). Synchronous, works on every
  platform, built into the player. This is how a game ships with ready-made mods.

A host can register more `IBundledModSource` implementations in the same container; the seeder takes
all of them in registration order. When two sources ship the same `id`, the first one installs it and
a later one replaces it only with a strictly newer version.

### 3.2 Seeder rules

`BundledModSeeder.Seed()` compares each discovered bundled mod with the `ILuaModSourceStore` entry of
the same `id`:

| Store state | Action |
|---|---|
| absent | **install**: `Save` the source; `Active = header.active`; set `Origin`, `SeededVersion`, `SeededHash`; no load order |
| present, `Origin == ""` (user mod, id clash) | **skip**: user mods are never touched |
| present, bundled `version` <= `SeededVersion` | **skip** |
| present, bundled `version` > `SeededVersion` | **update**: `Save` the new source, **keep the stored `Active` and `LoadOrder`**, refresh `SeededVersion`/`SeededHash`. A copy the player edited is updated too; an edit that ran stays in the mod's revision history (`manage_mods versions` / `revert`) |

So `active:` from the header applies only on first install, and `version` drives updates without
touching the player's enabled/disabled choice. Versions compare as dotted numbers (`1.10.0` > `1.2.0`;
missing parts count as 0). The hash is FNV-1a 32-bit over the UTF-8 source. Seeding is best-effort: a
failing source or mod is logged and skipped, and never stops startup.

## 4. Authoring surfaces

### 4.1 LLM

`manage_mods` (`Assets/CoreAIMods/Runtime/LuaExecution/LuaModsLlmTool.cs`) handles
list/get_source/load/reload/unload/forget/export/import/versions/revert/diagnostics. `reload` cleans the
previous run's startup objects (§5c) unless the optional `keep_objects: true` is passed (the MCP wrapper
`ManageModsMcpTool` forwards it); its result says which mode ran (`clean_startup_objects` or
`keep_objects`) and what it cleaned. When a mutating action changed the live startup world but the change
will not reopen after a restart, the result carries a `startup_warning`
([`WORLD_PACKAGE.md`](WORLD_PACKAGE.md), "Startup selection"). The `category` from the `@coreai` header
is persisted into the manifest.

### 4.2 Player — CoreAI Hub (UI Toolkit, event-driven)

The Hub is a **UI Toolkit** window (`CoreAiHubWindow` + `CoreAiHub.uxml` / `CoreAiHubUss.uss`,
`UIDocument`). Pages register into `HubPageRegistry`; the module's `CoreAiModsHubBinder` adds the Mods
tab and upgrades Settings/Statistics with live DI sources. Menu `CoreAI → Setup → Add Hub` drops in the
ready prefab (install/usage: `INSTALL.md` §4). The window collapses to a compact toggle. Tabs:

- **Chat** — the agent chat (embedded `CoreAiChatPanel`).
- **Settings** — live backend config.
- **Statistics** — live orchestration metrics (`InMemoryAiOrchestrationMetrics`).
- **Mods** — sub-tabs **Mods** and **Logs** (`HubModsPages`, `HubModsPage`, `HubModLogsPage` in
  `Assets/CoreAIMods/Runtime/HubIntegration/`).
- **About**, and more pages from C# modules and Lua mods.

The Mods sub-tab:
- Toolbar: a search box (name / category / tags), **Refresh**, **Add** (new mod in the editor),
  **Paste** (opens the Lua in the system clipboard, `GUIUtility.systemCopyBuffer`, in the editor) and
  **Import** (installs an export bundle from the clipboard).
- Mods are grouped into one collapsible group per `Category` (blank → `Uncategorized`).
- Each row: an **On/Off** toggle (load / unload), name and status line, **Edit**, **Export** (bundle to
  the clipboard) and **Delete**; **Update available** with **Apply update** when the manifest's
  `UpdateAvailable` is set.
- Editor: a top **← Back** to the list plus a **Save & run** / **Copy** / **Paste** /
  **Refresh diagnostics** / **History** action bar above the code area (**History** lists revisions,
  each with **Revert**). Save runs the mod (errors shown in the status line) and records a revision.
  **Save & run** of a loaded mod is a clean reload (§5c): the status line says what it did —
  `Saved & ran 'castle'; cleaned 126 objects of the previous run.` (`HubModSaveResult.Describe`). A
  **Keep objects on Save & run** toggle switches it to the hot reload: off by default, one Hub-wide
  setting kept under the PlayerPrefs key `CoreAI.Hub.Mods.KeepObjectsOnSave` by
  `PlayerPrefsHubModEditorPreferences`; a host with its own settings store supplies an
  `IHubModEditorPreferences` (`Assets/CoreAIMods/Runtime/HubIntegration/HubModEditorPage.cs`).
- **Add** opens a template matched to the composition (`HubModTemplates.For`,
  `IHubModService.NewModTemplate`): with the Rbx API it runs per-frame work on `RunService.Heartbeat` and
  periodic work in a `task.spawn` loop that yields with `task.wait` (each resume under the short
  per-resume budget); without it, the legacy `hooks_every` timer.
- A mod the runtime suspended after repeated budget trips (§5a) shows "suspended after repeated budget
  trips — start it manually" on its row (`HubModRecord.SuspendedAfterBudgetTrips`); turning it on, a
  load or a Save & run clears it.

`IHubModService.SaveOrReload(id, code, ModReloadMode)` (returning `HubModSaveResult`) and
`IHubModService.NewModTemplate` have no default bodies: a host that implements the interface itself
must implement them.

Performance rule (critical): the Mods list is built **once** and rebuilt only on the runtime's
source-loaded / source-unloaded notifications (`AddModSourceLoadedListener` /
`AddModSourceUnloadedListener`) and explicit user actions. NEVER call disk-backed stores
(`ILuaScriptVersionStore.GetKnownKeys/TryGetSnapshot`, `ILuaModSourceStore.List`) from a per-frame
path. The Mods and Logs tabs coalesce their notifications: however many `ModsChanged` or `LogsChanged`
events arrive, from whatever thread, each tab rebuilds at most once per panel update and always on the
main thread, through the panel's own scheduler (`HubModsPage.cs` and `HubModLogsPage.cs`). See §6.

## 5. Events mods can hook

A mod subscribes with `hooks_on(name, fn)`. The host raises an event with
`ILuaModRuntime.EmitEvent(caller, name, payload)`, and mods raise events for each other with
`events_emit(name, payload)`. `hooks_on('tick' | 'update' | 'frame', fn)` maps to a 0.05 s (~20 Hz)
timer. CoreAI defines no standard world event names; a host that wants mods to react to its world
(world ready, scene loaded, object spawned) emits its own events. Combine events with world
transactions (`coreai_world_begin/commit`), persistence (`store_set/get`) and capability tiers.

## 5a. Error policy: quarantine, not unload

Runtime failures never auto-unload a mod. Every hook/timer call runs under a per-call guard; a
failure increments the mod's **consecutive-error streak** (a successful call resets it to zero, except
in a frame in which one of the mod's scheduler threads has already faulted: that frame's fault stays
counted).
When the streak reaches the threshold — `LuaCsModRuntime.MaxErrorsBeforeQuarantine`, default 8,
configurable via `LuaCsModStackOptions.MaxErrorsBeforeQuarantine` — the mod is **quarantined**:

- **Suspended:** its `hooks_on` handlers, `hooks_every` timers, and queued events are all skipped,
  and its `logic_define` overrides are cleared so the game falls back to the vanilla C# formulas.
  Its cross-mod exports are suspended too: another mod's `mods_call`/`mods_get` into a quarantined
  mod fails with a quarantine error instead of invoking the export, so a broken mod cannot charge the
  caller's error streak.
- **Still loaded:** the mod stays in the runtime and in `manage_mods list` (with
  `quarantined: true`); `get_source`, `diagnostics`, `versions`, `export` keep working. This is what
  keeps the async "AI repairs a broken mod live" loop honest — a repair that takes minutes still
  finds its target instead of a `not loaded` error.
- **Cleared by reload:** a successful `manage_mods reload` (or `ReloadMod`) swaps in a fresh
  instance with a zero streak and no quarantine, and dispatch resumes. `unload`/`forget` remain the
  explicit removal paths.

**Budget trips quarantine sooner, and survive a restart.** A budget trip — the instruction, time or
memory budget of one call or resume — is a stall as long as the budget (10 s for a hook or timer by
default), so eight in a row would freeze the game for over a minute, and the next start would run the
mod and freeze again. Two trips in a row (`LuaCsModRuntime.MaxBudgetTripsBeforeQuarantine`, default
`DefaultMaxBudgetTripsBeforeQuarantine` = 2, set through `LuaCsModStackOptions.MaxBudgetTripsBeforeQuarantine`
or the runtime's constructor; ordinary errors in between do not
break the run, a clean call or frame does) quarantine the mod whatever its error streak, and its stored
package is marked `Active = false` with `SuspendedAfterBudgetTrips` (§2), so neither a restart nor a
world restore starts it. A load or reload by hand, or turning it on in the Hub, clears the mark. An
instance quota refusal is an ordinary error, not a budget trip; ordinary errors still quarantine at 8.
The classification reads the trip's typed cause, with a text fallback that a mod's own error message
can imitate.

**Disconnect is the one runtime path that does unload.** When an actor disconnects,
`LuaCsRbxApiBindings.ActorModsDisconnected` names the mods loaded for that actor and the runtime unloads
them — host-authority mods excepted — while the stored package keeps its active flag, so the next world
load or `RehydrateFromStore` starts them again (a rejoin alone does not). A quarantined mod keeps its actor
record and is unloaded by its actor's disconnect like any other, instead of holding its instance quota for
ever; a failed first load leaves no actor record behind. The unload never runs in the middle of mod code:
a disconnect raised while a hook, a timer, a logic-slot formula or a scheduler thread runs — a script that
kicked its own player — is released once that code has returned, or at the next `Tick`. A load or reload
whose main chunk disconnects its own actor fails with `InvalidOperationException` ("did not load: its actor …
disconnected while its main chunk ran") and is rolled back.

**A failed load or reload leaves nothing behind.** Its handlers are never added; the `logic_define`
and `logic_reset` changes its chunk made are put back as they were — including another live mod's
formula the chunk reset, and a formula defined inside a `coroutine.create` body, which belongs to the
mod like any other (a successful reload still replaces them, and a reset by the formula's own mod
stays); a failed first load also drops its instance-quota attribution and removes the `OnServerInvoke`
callbacks, tweens and pending waits its chunk created, and an unload drops the attribution too, so the
attribution map does not grow with every mod id ever tried. A failed **reload** also destroys the
objects its chunk built (a tween among them) and puts the previous run's startup objects back (§5c); a
failed **first** load keeps what its chunk built, as a Roblox script that errors does. The checks that
can still refuse a built candidate (the second capacity check) run inside the build's rollback, and a
load or reload of an id whose build is still running — a chunk that loads or reloads its own id, a
`ModTearingDown` listener that reloads the same id during a reload — is refused before any of its code
runs (`Mod '<id>' was loaded concurrently.` / `reloaded concurrently.`), so a rollback can only undo its
own build. Not cleaned up: a failed reload's `RemoteFunction` waits, and the instances a failed first
load's chunk created.

Observability: quarantine entry raises the internal event `LuaCsModRuntime.ModQuarantined(modId,
errorCount)`, and every teardown of a mod instance's side effects (unload, reload pre-swap, quarantine
entry) raises the internal `ModTearingDown(modId, reason)`. Logic-slot override failures are fail-open
(reset to vanilla) but attributed: they are recorded into the mod's handler-error diagnostics with the
owning mod id and slot name.

The streak is also what the host's auto-repair reads (`AddModHandlerErroredListener` reports it with
every failure). `LuaModAutoRepairPolicy` attempts its first repair at a streak of
`DefaultMinConsecutiveErrors` (3). For scheduler threads the streak counts faulting frames: any number
of faults in one frame count once, and a frame whose threads ran cleanly resets it, so a burst inside
one frame does not trigger a repair on its own while an error repeated every frame does. A successful
hook or timer call in a frame whose scheduler fault was already charged does not forgive that fault,
so a mod whose thread faults every frame while a timer succeeds every frame is still quarantined.

## 5b. Multiplayer write policy (decision core built; not on the network yet)

CoreAI is a framework, not one game: what clients may change in a shared world is **per-world
configuration**, not a hardcoded rule. The engine-free decision core ships in
`CoreAI.RbxApi.Instances` (`Replication/`): `ClientWritePolicy`, per-instance host grants in
`WriteGrantLedger`, `ClientWriteAuthority.Resolve` and `IntentGateway`. The OnlineAuthority demo runs
it in one process; nothing routes client writes over a transport yet. The policy has exactly two modes;
co-building is a host grant in `WriteGrantLedger` (a granted write is forwarded as an intent the server
checks), not a third mode — there is no `Open` policy:

- **`RobloxParity` (default):** a client write to a server-owned replicated instance applies
  locally, never replicates, and the server state overwrites it on the next sync. This mirrors
  documented Roblox behavior (local VFX/hides are a feature) — the Roblox tutorial corpus and the
  LLM's priors assume it, so it is the default.
- **`Strict`:** such writes are rejected with a `NOT_AUTHORITY` error (plus a "use a RemoteEvent"
  hint) — competitive games, anti-cheat.
- **Host grants (not a policy mode):** a client whose actor holds a grant covering `(instance, action)`
  does not apply the write locally; it sends a `MutationIntent`, the server checks the grant and applies
  it, and the authoritative change replicates to everyone — creative / co-build worlds (the "friend's AI
  edits the host's world" scenario).

The AI-facing Lua skills do not describe write policies, because the policy is not wired to a
transport. The replication plan is in [`ROBLOX_API_ROADMAP.md`](ROBLOX_API_ROADMAP.md) (MVP6).

## 5c. Reload modes: what a reload does with the previous run

A reload runs the new main chunk against a world the previous run already built into. Two modes
(`ModReloadMode`, `Assets/CoreAIMods/Runtime/LuaExecution/LuaCsModRuntime.cs`):

- **`CleanStartupObjects` (default everywhere):** Hub **Save & run**, `manage_mods reload`,
  `ILuaModRuntime.ReloadMod(caller, id, code)` and the runtime's own `ReloadMod`. The previous run's
  *startup objects* — the instances its main chunk registered up to its first yield (or its end) that the
  mod still owns — are taken out of the world (`Parent = nil`, remembering parent and sibling position)
  before the new chunk runs, so the new chunk builds into a clean world and does not find them. When it
  succeeds they are destroyed, together with every object inside them that the same mod still owns as
  the same actor (coins a `Heartbeat` handler dropped into the mod's folder); an object anyone else owns
  — a player's build, another mod's object — is moved out to the startup object's parent first, with
  everything inside it. When the new chunk fails, the objects it built are destroyed and the startup
  objects go back exactly where they were. A castle-building mod saved five times leaves one castle.
- **`KeepObjects`:** every object stays and the new chunk builds next to them (a hot reload). The kept
  startup objects stay tracked, so a later clean reload removes them too.

`ILuaModRuntime.ReloadMod(caller, id, code, ModReloadMode)` answers a `ModReloadReport` (`CleanedObjects`,
`KeptObjects`, `RescuedObjects`, `Describe()`). Its default interface body runs the mode-less reload and
answers null, for a runtime that tracks no startup objects; every wrapper of an `ILuaModRuntime` (the
world-session facade, an attribution facade) must forward it, or the mode is lost and the reload is a
clean one. Rules and limits:

- Only what the main chunk builds before its first yield is tracked. Objects built after it — in a
  `task.spawn` loop, a handler, a remote call — are never startup objects, and an object re-attributed
  to another actor is skipped.
- Until the new chunk finishes, both runs' startup objects exist, so a mod near its instance quota
  (2,048 per actor) can fail a clean reload that would fit afterwards.
- Startup objects are tracked per process: after a restart or a world restore, the objects the
  restarted main chunk builds are the startup set.
- `LuaCsModRuntimeFactory` wires the reload and unload teardown (threads, connections, tweens and the
  mod's instances) by default; a host does not wire it by hand.
- Tests: `Reload_*` and `Factory_*` in `Assets/CoreAIMods/Tests/EditMode/LuaCsModRuntimeEditModeTests.cs`.

## 6. Performance / optimization

Mod UI never enumerates disk-backed stores from a draw or per-frame path. Mod lists are cached and
recomputed only when the runtime reports a source load/unload or the user acts; the Hub Mods tab
follows this rule. `FileLuaScriptVersionStore` reads and parses its whole file on every synchronous
call, which is exactly why it must stay off per-frame paths.

## 7. Not built yet

- Bundled sources other than `Resources` (StreamingAssets, Addressables, a remote download).
- A `category` argument and a category column in `manage_mods`.
- An in-memory cache for `FileLuaScriptVersionStore`.
- Standard host world events for mods (§5).
- Routing the §5b write-policy core over a network transport.

## 8. Platform / compatibility notes

- WebGL: the `Resources` source works. File stores such as `FileLuaModSourceStore` confirm durability
  through `CoreAiWebGlPersistence` (the engine's automatic `persistentDataPath` persistence) instead of
  driving IDBFS by hand. The seeder is synchronous.
- The `COREAI_LUA` define gates the Lua demos, benchmark scenarios and Lua-dependent tests; the
  Lua-CSharp runtime ships bundled as `Lua.dll`/`Lua.Annotations.dll` in this package (see
  [the package README](../../Assets/CoreAIMods/README.md)).
- All seven `com.neoxider.coreai*` packages bump in lockstep at release time.
