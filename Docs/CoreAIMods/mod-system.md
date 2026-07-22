# CoreAI Mod System & UI — Design Spec

Status: DRAFT (implementation contract for subagents). All code, comments, identifiers,
and commit messages are English. Guard every Lua/mod C# file with
`#if !COREAI_NO_LUA`. Never hand-create Unity `.meta` files.
Do not run `git commit` in subagent tasks — the orchestrator commits after review.

## 0. Goals

A powerful, optimized, convenient, future-proof mod core:

- Mods are authored by the **LLM** (already via `manage_mods`) or by the **player** (paste text,
  edit, create) — same store, same lifecycle.
- The **game ships with mods** (bundled), some **enabled by default**, and mods **update** when the
  game updates or new content is downloaded at runtime.
- The world changes **dynamically at runtime** — AI writes/edits mods, player writes mods — without
  a rebuild.
- **Categories** give a folder-like tree in the UI.
- A single **CoreAI Hub** (uGUI Canvas, event-driven) replaces the scattered IMGUI debug panels and
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

## 3. Bundled sources & the seeder

> **Status: implemented (PR1).** `IBundledModSource`, `BundledMod`, `ResourcesBundledModSource`, and
> `BundledModSeeder` live in `Assets/CoreAIMods/Runtime/Infrastructure/`. `CoreAiModsInstaller` registers
> `ResourcesBundledModSource` and runs the seeder in the play-mode build callback **before**
> `RehydrateFromStore`. Two sample mods ship in `Assets/CoreAIMods/Runtime/Resources/CoreAIMods/`
> (`sample_welcome.lua` active, `sample_camera_pulse.lua` opt-in). Unit-tested in
> `BundledModSeederEditModeTests`.

### 3.1 Sources (pluggable)

`IBundledModSource` returns discovered bundled mods as `BundledMod { string Id, string Source,
string Version }` (id/version parsed from the `@coreai` header) plus an `Origin` marker on the source.
Implementations:

- **PR1 (done):** `ResourcesBundledModSource` — `Resources.LoadAll<TextAsset>("CoreAIMods")` (the
  project's Lua scripted importer makes `.lua` a `TextAsset`). Synchronous, all platforms, built into the
  player. Base source. This is how a game ships with ready-made mods.
- **PR2:** `StreamingAssetsBundledModSource` — scan `StreamingAssets/CoreAIMods/*.lua` via
  `UnityWebRequest` (async; needed on Android/WebGL). Editable post-ship; path to Addressables.
- **PR2:** `AddressablesBundledModSource` — load `.lua` TextAssets by label `coreai-mod`. Runtime/DLC
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

`manage_mods` (`Assets/CoreAIMods/Runtime/LuaExecution/LuaModsLlmTool.cs`) already handles
load/reload/unload/forget/export/import/versions/revert/diagnostics. Add: honor `category` (from the
header or an explicit arg) and expose it in `list` output. No behavior change to existing actions.

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
  in the status line) and records a revision.

Performance rule (critical): the Mods list/tree is built **once** and rebuilt only on
`LuaModRuntime.ModSourceLoaded` / `ModSourceUnloaded` (and explicit user actions). NEVER call
disk-backed stores (`ILuaScriptVersionStore.GetKnownKeys/TryGetSnapshot`, `ILuaModSourceStore.List`)
from a per-frame path. See §6.

## 5. Dynamic world event contract (PR2)

`CoreAiWorldEvents` — string constants the host emits into the mod runtime so mods hook a live world:
`world_ready`, `scene_loaded` (payload: scene), `object_spawned`/`object_despawned` (payload: id, name),
`tick` (~20 Hz, existing), `save`, `load`. Mods subscribe via `hooks_on`. Combine with existing world
transactions (`coreai_world_begin/commit`), persistence (`store_set/get`), and capability tiers.

## 5a. Error policy: quarantine, not unload

Runtime failures never auto-unload a mod. Every hook/timer call runs under a per-call guard; a
failure increments the mod's **consecutive-error streak** (any successful call resets it to zero).
When the streak reaches the threshold — `LuaCsModRuntime.MaxErrorsBeforeQuarantine`, default 8,
configurable via `LuaCsModStackOptions.MaxErrorsBeforeQuarantine` — the mod is **quarantined**:

- **Suspended:** its `hooks_on` handlers, `hooks_every` timers, and queued events are all skipped,
  and its `logic_define` overrides are cleared so the game falls back to the vanilla C# formulas.
- **Still loaded:** the mod stays in the runtime and in `manage_mods list` (with
  `quarantined: true`); `get_source`, `diagnostics`, `versions`, `export` keep working. This is what
  keeps the async "AI repairs a broken mod live" loop honest — a repair that takes minutes still
  finds its target instead of a `not loaded` error.
- **Cleared by reload:** a successful `manage_mods reload` (or `ReloadMod`) swaps in a fresh
  instance with a zero streak and no quarantine, and dispatch resumes. `unload`/`forget` remain the
  explicit removal paths.

Observability: quarantine entry raises `LuaCsModRuntime.ModQuarantined(modId, errorCount)`, and every
teardown of a mod instance's side effects (unload, reload pre-swap, quarantine entry) raises
`ModTearingDown(modId, reason)` — future subsystems (instance registries, signals) hook the same
point. Logic-slot override failures are fail-open (reset to vanilla) but attributed: they are
recorded into the mod's handler-error diagnostics with the owning mod id and slot name.

## 5b. Multiplayer write policy (planned — lands with the Roblox API track, MVP12)

CoreAI is a framework, not one game: what clients may change in a shared world is **per-world
configuration**, not a hardcoded rule. The world setting `ClientWritePolicy` (part of the host
integration profile) selects one of three modes:

- **`RobloxParity` (default):** a client write to a server-owned replicated instance applies
  locally, never replicates, and the server state overwrites it on the next sync. This mirrors
  documented Roblox behavior (local VFX/hides are a feature) — the Roblox tutorial corpus and the
  LLM's priors assume it, so it is the default.
- **`Strict`:** such writes are rejected with a `NOT_AUTHORITY` error (plus a "use a RemoteEvent"
  hint) — competitive games, anti-cheat.
- **`Open`:** client writes are forwarded to the server and replicate to everyone — creative /
  co-build worlds (the "friend's AI edits the host's world" scenario).

Implementation seam (reserved now, implemented at MVP12): every mutation is routed through a single
authority resolver `(instance, property/action) → ApplyLocalOnly | Replicate | Reject`. The MVP
implementation behind that seam is just the world-default policy above; **partial authority** —
per-instance / per-property / per-player rules ("clients may move furniture but not delete walls")
— is planned future functionality that becomes a new resolver implementation, not a replication
rewrite. `NOT_AUTHORITY` always fires on explicit replication attempts regardless of policy.

The AI-facing Lua skill deliberately does NOT document this yet: the skill only describes the
implemented surface (a documented-but-missing feature is a bug magnet for LLM authors). The skill
section for write policies ships together with MVP12. Details: `ROBLOX_API_ROADMAP.md` §MVP12.

## 6. Performance / optimization

Root cause of the observed 6 FPS with the mod panel open: `LiveMechanicsModsChatPersistenceController`
calls disk-backed `ILuaScriptVersionStore` (`GetKnownKeys`/`TryGetSnapshot`) from `OnGUI`.
`FileLuaScriptVersionStore.ReadFromDisk` re-reads and re-parses the whole JSON file on EVERY call
(`LoadFromDisk` does `File.ReadAllText` + full deserialize). `GetInactiveSavedMods()` does ~2 reads per
saved mod, is called twice per `OnGUI`, and `OnGUI` fires 2+ times per frame => dozens of full-file
reads+parses per frame.

Fixes:
1. **Quick fix (PR-perf, independent):** in the F9 controller, cache the active/inactive lists and the
   inactive-count; recompute only on `ModSourceLoaded`/`ModSourceUnloaded` and after user actions, not in
   `OnGUI`. Remove all store enumeration and `ReadMetadata`/`Split` from the draw path.
2. **Store-level:** `FileLuaScriptVersionStore` should keep an in-memory cache and reload from disk only
   when the file changed (mtime/dirty flag), instead of `ClearAll` + full read on every `ReadFromDisk`.
3. **Structural:** the CoreAI Hub (uGUI, event-driven) removes per-frame IMGUI layout entirely.

## 7. Phasing / task graph

- **Phase 1 — Mod Core:** §1 header parser, §2 manifest, §3 Resources source + seeder + wiring, §4.1
  LLM category, tests. Plus perf quick-fix (§6.1) and store cache (§6.2) in parallel.
- **Phase 2 — CoreAI Hub:** §4.2 uGUI shell + tabs; Mods tab with Add/Paste/Copy/Update/tree; migrate
  Backend/Tokens/Chat.
- **Phase 3 (PR2):** §3.1 StreamingAssets + Addressables sources; §5 world event contract; store cache
  finalization; retire IMGUI panels.

## 8. Platform / compatibility notes

- WebGL: `Resources` works; `StreamingAssets` needs `UnityWebRequest`; `FileLuaModSourceStore` already
  flushes IDBFS. Keep the seeder synchronous only for the Resources source; async for the rest.
- `COREAI_NO_LUA` builds: everything guarded; the core compiles with Lua fully stripped out
  (the Lua-CSharp runtime ships bundled as `Lua.dll`/`Lua.Annotations.dll`, so there is no
  external package to omit).
- All five `com.neoxider.coreai*` packages bump in lockstep at release time.
