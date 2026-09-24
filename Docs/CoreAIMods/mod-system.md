# CoreAI Mod System & UI — Design Spec

Status: design spec. Sections marked **implemented** describe shipped behaviour; the rest is
planned. The historical performance analysis and the phase plan live in
`dev-docs/MOD_SYSTEM_DESIGN_NOTES.md`.

## 0. Goals

A powerful, optimized, convenient, future-proof mod core:

- Mods are authored by the **LLM** (already via `manage_mods`) or by the **player** (paste text,
  edit, create) — same store, same lifecycle.
- The **game ships with mods** (bundled), some **enabled by default**, and mods **update** when the
  game updates or new content is downloaded at runtime.
- The world changes **dynamically at runtime** — AI writes/edits mods, player writes mods — without
  a rebuild.
- **Categories** give a folder-like tree in the UI.
- A single **CoreAI Hub** (UI Toolkit, event-driven) replaces the scattered IMGUI debug panels and
  fixes the per-frame cost.

## 1. Mod identity & in-source manifest (the "passport")

Every mod carries a frontmatter block at the very top of its `.lua` source. Lua ignores it
(comment); C# parses it. This is the single source of metadata for bundled mods (no separate
`manifest.json` needed at authoring time).

```lua
--[[@coreai
id: first_person            # stable key; NEVER changes; drives update matching
name: First-Person Controller
version: 1.0.0              # semver; update trigger
active: false              # default enabled-state at FIRST seed only
capabilities: All, Full    # requested tiers (intersected with host grant on load)
category: player/controllers   # "/"-separated path -> UI tree
author: CoreAI
description: WASD to move, mouse to look.
tags: movement, camera     # optional, comma-separated
]]
```

Parsing rules:
- Recognize a leading block comment whose first token is `@coreai` (`--[[@coreai ... ]]`). Also accept
  a run of `-- @coreai key: value` line comments as a fallback.
- Each entry is `key: value`; keys are case-insensitive; unknown keys ignored.
- Defaults when absent: `id` = provided fallback (e.g. file name), `name` = id, `version` = "0.0.0",
  `active` = true, `capabilities` = "All", `category` = "" (root), others empty.
- `capabilities` parses to `LuaCapabilities` via the existing string round-trip (comma/space list).
- Pure function, no Unity dependency, no allocation in the hot path beyond the parse itself.

## 2. Manifest extension (persisted metadata)

Extend `LuaModManifest` (`Assets/CoreAIMods/Runtime/LuaExecution/LuaModManifest.cs`) with new
fields. It is a plain JSON DTO; new fields are backward-compatible (missing => default):

- `string Category = "";`     — "/"-separated category path for the tree.
- `string Tags = "";`         — comma-separated tags.
- `string Origin = "";`       — "" = user-authored; otherwise bundled source id ("resources",
  "streamingassets", "addressables:<label>", "remote:<url>").
- `string SeededVersion = "";`— last bundled version seeded into this store entry.
- `string SeededHash = "";`   — hash of the source at seed time (detects user edits).
- `bool UpdateAvailable = false;` — set by the seeder when a newer bundled version exists but the
  local copy was user-edited (manual update offered in UI).
- `long LoadOrder = 0;` — where the mod sits in its world's load order (implemented). A first load, and
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

> **Status: implemented (PR1).** `IBundledModSource`, `BundledMod`, `ResourcesBundledModSource`, and
> `BundledModSeeder` live in `Assets/CoreAIMods/Runtime/Infrastructure/`. `CoreAiModsInstaller` registers
> `ResourcesBundledModSource` and runs the seeder in the play-mode build callback **before**
> `RehydrateFromStore`. Five sample mods ship in `Assets/CoreAIMods/Runtime/Resources/CoreAIMods/`
> (`sample_welcome.lua` active; `sample_lane_racer.lua`, `sample_tetris3d.lua`, `sample_clicker.lua`
> and `sample_castle3d.lua` opt-in). Unit-tested in `BundledModSeederEditModeTests`.

### 3.1 Sources (pluggable)

`IBundledModSource` returns discovered bundled mods as `BundledMod { string Id, string Source,
string Version }` (id/version parsed from the `@coreai` header) plus an `Origin` marker on the source.
Implementations:

- **PR1 (done):** `ResourcesBundledModSource` — `Resources.LoadAll<TextAsset>("CoreAIMods")` (the
  project's Lua scripted importer makes `.lua` a `TextAsset`). Synchronous, all platforms, built into the
  player. Base source. This is how a game ships with ready-made mods.
- **Planned:** `StreamingAssetsBundledModSource` — scan `StreamingAssets/CoreAIMods/*.lua` via
  `UnityWebRequest` (async; needed on Android/WebGL). Editable post-ship; path to Addressables.
- **Planned:** `AddressablesBundledModSource` — load `.lua` TextAssets by label `coreai-mod`. Runtime/DLC
  delivery.
- **Future:** `RemoteBundledModSource` — download from a URL/manifest.

Collision precedence when the same `id` appears in multiple sources:
`remote > addressables > streamingassets > resources` (outer layer overrides built-in).

### 3.2 Seeder state machine

`BundledModSeeder` runs at startup **before** `LuaModRuntime.RehydrateFromStore`. For each discovered
bundled mod, compared against the `ILuaModSourceStore` entry with the same `id`:

| Store state | Condition | Action |
|---|---|---|
| absent | — | **install**: `Save` source; `Active = header.active`; `Origin`, `SeededVersion=header.version`, `SeededHash=hash(source)` |
| present, `header.version > SeededVersion`, stored source hash == `SeededHash` (untouched) | — | **update**: `Save` new source (the subsequent rehydrate/`LoadMod` records a revision for rollback); **keep the user's `Active`**; bump `SeededVersion`/`SeededHash` |
| present, newer version, stored source hash != `SeededHash` (user-edited) | — | **skip + flag**: set `UpdateAvailable=true`; leave user's copy; manual update in UI |
| present, `header.version <= SeededVersion` | — | leave as-is |
| present, `Origin == ""` (user mod, id clash) | — | never touch user mods |

Result: `active:` from the header applies only on first install; `version` drives updates and never
clobbers the user's runtime toggle or edits. Hash: stable non-crypto (e.g. FNV-1a over UTF-8), same
algorithm everywhere.

## 4. Authoring surfaces

### 4.1 LLM

`manage_mods` (`Assets/CoreAIMods/Runtime/LuaExecution/LuaModsLlmTool.cs`) handles
list/get_source/load/reload/unload/forget/export/import/versions/revert/diagnostics. `reload` cleans the
previous run's startup objects (§5c) unless the optional `keep_objects: true` is passed (the MCP wrapper
`ManageModsMcpTool` forwards it); its result says which mode ran (`clean_startup_objects` or
`keep_objects`) and what it cleaned. When a mutating action changed the live startup world but the change
will not reopen after a restart, the result carries a `startup_warning`
([`WORLD_PACKAGE.md`](WORLD_PACKAGE.md), "Startup selection"). The `category` from the `@coreai` header
is persisted into the manifest; a `category` argument and a category column in `list` output are
planned.

### 4.2 Player — CoreAI Hub (UI Toolkit, event-driven)

> **Status: implemented.** The Hub is a **UI Toolkit** window (`CoreAiHubWindow` + `CoreAiHub.uxml` /
> `CoreAiHubUss.uss`, Unity 6.3 `UIDocument`), not uGUI/IMGUI. Pages register into `HubPageRegistry`;
> the module's `CoreAiModsHubBinder` lights up the Mods tab and upgrades Settings/Statistics with live
> DI sources. Menu `CoreAI → Setup → Add Hub` drops in the ready prefab (install/usage: `INSTALL.md` §4).

A single floating window (`CoreAI Hub`) built on UI Toolkit, replacing the old F7–F10 IMGUI panels.
Collapses to a compact bar via the top-right toggle. Tabs:

- **Chat** — the agent chat (embedded `CoreAiChatPanel`).
- **Settings** — live backend config (context window, timeouts, streaming, pricing, logging).
- **Statistics** — token budget + live orchestration metrics (`InMemoryAiOrchestrationMetrics`).
- **Mods** — category tree; per-mod row with actions; header buttons.
- **About**, and extensible (C# modules and Lua mods can add pages).

Mods tab requirements:
- Category tree (collapsible folders derived from `Category` paths).
- Buttons: **Add mod** (new blank mod in the editor), **Paste** (from system clipboard via
  `GUIUtility.systemCopyBuffer`), and per-mod **Copy** (source to clipboard), **Edit**, **Enable/Disable**
  toggle, **Delete**, **Import**/**Export** (bundle JSON), **Update** (visible when `UpdateAvailable` or a
  newer bundled version exists; re-seeds from the bundled source).
- Editor sub-panel: a top **← Back** to the list plus a **Save & run** / **Copy** / **Paste** /
  **Refresh diagnostics** action bar above the code area; Save validates by running the mod (errors shown
  in the status line) and records a revision. **Save & run** of a loaded mod is a clean reload (§5c): the
  status line says what it did — `Saved & ran 'castle'; cleaned 126 objects of the previous run.`
  (`HubModSaveResult.Describe`). A **Keep objects on Save & run** toggle switches it to the old hot reload:
  off by default, one Hub-wide setting kept under the PlayerPrefs key `CoreAI.Hub.Mods.KeepObjectsOnSave`
  by `PlayerPrefsHubModEditorPreferences`; a host with its own settings store supplies an
  `IHubModEditorPreferences` (`Assets/CoreAIMods/Runtime/HubIntegration/HubModEditorPage.cs`).
- **Add mod** opens a template matched to the composition (`HubModTemplates.For`,
  `IHubModService.NewModTemplate`): with the Rbx API it runs per-frame work on `RunService.Heartbeat` and
  periodic work in a `task.spawn` loop that yields with `task.wait` (each resume under the short
  per-resume budget); without it, the legacy `hooks_every` timer.
- A mod the runtime suspended after repeated budget trips (§5a) shows "suspended after repeated budget
  trips — start it manually" on its row (`HubModRecord.SuspendedAfterBudgetTrips`); **Enable**, a load or
  a Save & run clears it.

`IHubModService` gained `SaveOrReload(id, code, ModReloadMode)` (returning `HubModSaveResult`) and
`NewModTemplate`. They have no default bodies, so a host's own implementation of the interface must add
them (`Assets/CoreAI/CHANGELOG.md`, Changed).

Performance rule (critical): the Mods list/tree is built **once** and rebuilt only on the runtime's
source-loaded / source-unloaded notifications (`AddModSourceLoadedListener` /
`AddModSourceUnloadedListener`) and explicit user actions. NEVER call disk-backed stores
(`ILuaScriptVersionStore.GetKnownKeys/TryGetSnapshot`, `ILuaModSourceStore.List`) from a per-frame
path. The Mods and Logs tabs coalesce their notifications: however many `ModsChanged` or `LogsChanged`
events arrive, from whatever thread, each tab rebuilds at most once per panel update and always on the
main thread, through the panel's own scheduler (`HubModsPage.cs` and `HubModLogsPage.cs` in
`Assets/CoreAIMods/Runtime/HubIntegration/`). Before, 500 failing `Heartbeat` handlers queued 500 rebuilds
of the Logs list in one frame, and a rehydrate of 30 mods rebuilt the Mods list 30 times synchronously on
the thread that raised the event — part of the Unity crash the owner hit while editing a mod on the Hub.
See §6.

## 5. Dynamic world event contract (planned)

`CoreAiWorldEvents` (not implemented yet) — string constants the host would emit into the mod runtime
so mods hook a live world:
`world_ready`, `scene_loaded` (payload: scene), `object_spawned`/`object_despawned` (payload: id, name),
`tick` (~20 Hz, existing), `save`, `load`. Mods subscribe via `hooks_on`. Combine with existing world
transactions (`coreai_world_begin/commit`), persistence (`store_set/get`), and capability tiers.

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
default), so eight in a row froze the game for over a minute, and the next start ran the mod and froze
again. Two trips in a row (`LuaCsModRuntime.MaxBudgetTripsBeforeQuarantine`, default
`DefaultMaxBudgetTripsBeforeQuarantine` = 2, set through `LuaCsModStackOptions.MaxBudgetTripsBeforeQuarantine`
or the runtime's constructor; ordinary errors in between do not
break the run, a clean call or frame does) quarantine the mod whatever its error streak, and its stored
package is marked `Active = false` with `SuspendedAfterBudgetTrips` (§2), so neither a restart nor a
world restore starts it. A load or reload by hand, or the Hub's **Enable**, clears the mark. An instance
quota refusal is an ordinary error, not a budget trip; ordinary errors still quarantine at 8. The
classification reads the trip's typed cause, with a text fallback that a mod can imitate (`TODO.md`).

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
attribution map no longer grows with every mod id ever tried. A failed **reload** also destroys the
objects its chunk built (a tween among them) and puts the previous run's startup objects back (§5c); a
failed **first** load keeps what its chunk built, as a Roblox script that errors does. The checks that
can still refuse a built candidate (the second capacity check) run inside the build's rollback, and a
load or reload of an id whose build is still running — a chunk that loads or reloads its own id, a
`ModTearingDown` listener that reloads the same id during a reload — is refused before any of its code
runs (`Mod '<id>' was loaded concurrently.` / `reloaded concurrently.`), so a rollback can only undo its
own build. Still open (`TODO.md`): a failed reload's `RemoteFunction` waits, and the instances a failed
first load's chunk created are not swept.

Observability: quarantine entry raises `LuaCsModRuntime.ModQuarantined(modId, errorCount)`, and every
teardown of a mod instance's side effects (unload, reload pre-swap, quarantine entry) raises
`ModTearingDown(modId, reason)` — future subsystems (instance registries, signals) hook the same
point. Logic-slot override failures are fail-open (reset to vanilla) but attributed: they are
recorded into the mod's handler-error diagnostics with the owning mod id and slot name.

The streak is also what the host's auto-repair reads (`AddModHandlerErroredListener` reports it with
every failure). `LuaModAutoRepairPolicy` still attempts its first repair at a streak of
`DefaultMinConsecutiveErrors` (3; unchanged by the 2026-09-24 fix waves), but for scheduler threads the
streak now counts faulting frames (M2-08): any number of faults in one frame count once, and a frame whose
threads ran cleanly resets it, so a burst inside one frame no longer triggers a repair on its own while an
error repeated every frame does. A successful hook or timer call in a frame whose scheduler fault was
already charged no longer forgives that fault (A2-06), so a mod whose thread faults every frame while a
timer succeeds every frame is still quarantined.

## 5b. Multiplayer write policy (decision core implemented; not on the network yet)

CoreAI is a framework, not one game: what clients may change in a shared world is **per-world
configuration**, not a hardcoded rule. The engine-free decision core ships in
`CoreAI.RbxApi.Instances` (`Replication/`): `ClientWritePolicy`, per-instance host grants in
`WriteGrantLedger`, `ClientWriteAuthority.Resolve` and `IntentGateway`. The OnlineAuthority demo runs
it in one process; nothing routes client writes over a transport yet. The policy has exactly two modes;
co-building is a host grant in `WriteGrantLedger` (a granted write is forwarded as an intent the server
checks), not a third mode — there is no `Open` policy (owner decision 3,
[`MVP25_BUILD_PLAN_2026-09-04.md`](../../dev-docs/MVP25_BUILD_PLAN_2026-09-04.md) §D):

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

Implementation seam (reserved now, implemented at MVP6, the replication rung; the old MVP12): every
mutation is routed through a single authority resolver `(instance, property/action) → ApplyLocalOnly |
Replicate | Reject`. The first implementation behind that seam is just the world-default policy above;
**partial authority** — per-instance / per-property / per-player rules ("clients may move furniture but
not delete walls") — is planned future functionality that becomes a new resolver implementation, not a
replication rewrite. `NOT_AUTHORITY` always fires on explicit replication attempts regardless of policy.

The AI-facing Lua skill deliberately does NOT document this yet: the skill only describes the
implemented surface (a documented-but-missing feature is a bug magnet for LLM authors). The skill
section for write policies ships together with MVP6. Details: `ROBLOX_API_ROADMAP.md` §MVP6.

## 5c. Reload modes: what a reload does with the previous run

> **Status: implemented** (owner decision, 2026-09-24: support both modes, clean by default).

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
- **`KeepObjects`:** every object stays and the new chunk builds next to them — the hot reload of every
  earlier version. The kept startup objects stay tracked, so a later clean reload removes them too.

`ILuaModRuntime.ReloadMod(caller, id, code, ModReloadMode)` answers a `ModReloadReport` (`CleanedObjects`,
`KeptObjects`, `RescuedObjects`, `Describe()`). Its default interface body runs the mode-less reload and
answers null, for a runtime that tracks no startup objects; every wrapper of an `ILuaModRuntime` (the
world-session facade, an attribution facade) must forward it, or the mode is lost and the reload is a
clean one (`TODO.md`). Rules and limits:

- Only what the main chunk builds before its first yield is tracked. Objects built after it — in a
  `task.spawn` loop, a handler, a remote call — are never startup objects (telling a mod's threads apart
  needs thread identity; `TODO.md`), and an object re-attributed to another actor is skipped.
- Until the new chunk finishes, both runs' startup objects exist, so a mod near its instance quota
  (2,048 per actor) can fail a clean reload that would fit afterwards.
- Startup objects are tracked per process: after a restart or a world restore, the objects the
  restarted main chunk builds are the startup set.
- `LuaCsModRuntimeFactory` wires the reload and unload teardown (threads, connections, tweens and the
  mod's instances) by default; a host no longer wires it by hand.
- Tests: `Reload_*` and `Factory_*` in `Assets/CoreAIMods/Tests/EditMode/LuaCsModRuntimeEditModeTests.cs`.

## 6. Performance / optimization

Mod UI never enumerates disk-backed stores from a draw or per-frame path. Mod lists are cached and
recomputed only when the runtime reports a source load/unload or the user acts; the Hub Mods tab and
the demo mod manager both follow this rule. `FileLuaScriptVersionStore` still reads and parses its
whole file on every synchronous call, which is exactly why it must stay off per-frame paths. The
original 6 FPS investigation that produced this rule is recorded in
`dev-docs/MOD_SYSTEM_DESIGN_NOTES.md`.

## 7. Delivery status

Implemented: §1 header parser, §2 manifest fields, §3 Resources source + seeder + wiring, §4.2 the
UI Toolkit Hub with the Mods tab, §5a quarantine and the budget-trip suspension, §5c reload modes.
Planned: §3.1 StreamingAssets / Addressables /
remote sources, the `category` argument of §4.1, an in-memory cache for `FileLuaScriptVersionStore`,
§5 world event contract, and wiring the §5b write-policy core to a network transport.

## 8. Platform / compatibility notes

- WebGL: `Resources` works; `StreamingAssets` needs `UnityWebRequest`. File stores such as
  `FileLuaModSourceStore` confirm durability through `CoreAiWebGlPersistence` (the engine's automatic
  `persistentDataPath` persistence) instead of driving IDBFS by hand. Keep the seeder synchronous only
  for the Resources source; async for the rest.
- Default builds omit Lua; `COREAI_LUA` builds include the guarded Lua surfaces
  (the Lua-CSharp runtime ships bundled as `Lua.dll`/`Lua.Annotations.dll`, so there is no
  external package to omit).
- All seven `com.neoxider.coreai*` packages bump in lockstep at release time.
