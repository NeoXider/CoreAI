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
- **Folders** organise mods on the Hub's Mods tab: a mod's `category` is a folder path, folders hold mods
  and other folders.
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
- `category` — the mod's folder, a `/`-separated path (`Games/Arcade`); empty is the top level. A
  one-level category such as `Samples` is a one-folder path, so older mods load unchanged. Rules for a
  folder that is written (by a player, the AI or a move): letters, digits and combining marks of any
  script, spaces and `_ - . ( ) & + ! ' , #`; at most 64 characters per folder name, 8 levels and 255
  characters in all; names compare case-insensitively. A broken rule is refused with a named code
  (`INVALID_CHARACTER`, `EMPTY_SEGMENT`, `SEGMENT_TOO_LONG`, `TOO_DEEP`, `PATH_TOO_LONG`, `RESERVED_NAME`).
  Moving a mod rewrites only this line (`Assets/CoreAIMods/Runtime/ModFolders/README.md`).
- `tags` — optional, comma-separated.
- `context` — optional, the script context the mod declares: `server`, `client` or `shared` (any case).
  A mod that declares one gets its own script instance in the world (below). Any other value fails the
  load with `BAD_ARGUMENT: @coreai context 'clinet' is not server, client or shared`. `shared` makes a
  ModuleScript: it loads and is listed, but its code runs only when a script requires it (see "Modules and
  `require`" below). Without the key the mod is a legacy script and
  runs exactly as before. The context decides where the script starts (below) and which side it runs as
  (`dev-docs/MVP4_SCRIPT_CONTEXTS_DESIGN.md`, SC-5, SC-9, SC-13, SC-14):
  - a `server` script is the server whoever wrote it: `RunService:IsServer()` is true,
    `Players.LocalPlayer` is nil, and it may use the server members (`FireClient`, `FireAllClients`,
    `OnServerEvent`, `OnServerInvoke`, `ServerStorage`, `ServerScriptService`, `store_set`, `store_get`);
  - a `client` script is the client: `RunService:IsClient()` is true, `Players.LocalPlayer` is the player at
    this screen (for a script the host wrote; another author's script acts as its author), its remotes are
    sent as that player, and it may use the client members (`FireServer`, `OnClientEvent`, `InvokeServer`,
    `OnClientInvoke`, `UserInputService`, `RunService.RenderStepped`/`PreRender`, camera writes,
    `camera_follow`, `camera_set_cframe`). It starts only once that player exists (`run_state`
    `waiting-for-local-player` until then);
  - using a member of the other side raises `CONTEXT_VIOLATION`, naming the rule and the fix, for example
    `CONTEXT_VIOLATION: UserInputService.IsKeyDown is client-only (§3.2; UserInputService.yaml) and this
    script runs as server | fix: move this code into a client script (context: client) and send the result
    to the server with a RemoteEvent`; so does `mods_call`/`mods_get` into a mod of the other side (a mod
    that declares nothing counts as the server side) — scripts of the two sides talk through remotes only;
  - a legacy script keeps every pre-MVP4 rule: its side comes from the machine and the actor's grant, and a
    wrong-side remote member raises `NOT_AUTHORITY` as before.
  TODO: `logic_define`, `logic_reset`, `events_emit` across sides and `FindFirstChild` on `game` are not
  checked yet (design note, C3 entry).
- `parent` — optional, the `/`-separated instance path from `game` where the mod's script lives, such
  as `Workspace/Door` (a leading `@game/` is accepted). The path must exist when the mod loads and must not
  lie inside an instance a mod made; otherwise the load fails with `BAD_ARGUMENT` naming the missing
  segment or the parent it refuses. It applies at every load and reload.

Where `script` lives:
- **A mod that declares neither key** keeps the legacy placement: `script` is
  `ServerScriptService/<id> (Folder)/<id> (Script)`, which the runtime builds for every run, never saves,
  and removes when the mod unloads.
- **A mod that declares `context:` or `parent:`** gets one script instance of its own, created before its
  main chunk runs: a `Script` with `RunContext` Server (`context: server`) or Client (`context: client`),
  or a `ModuleScript` (`context: shared`), named after the mod, under the `parent:` path or else under
  `ServerScriptService` (server) or `ReplicatedStorage` (client and shared), inside folders named after the
  mod's `category` path. The instance belongs
  to the world, not to the mod: a reload keeps it (a clean reload removes only the previous run's startup
  objects), an unload keeps it, and it costs no instance quota. A load that fails the first time removes
  it again, with the folders it created; a reload that fails keeps it where it was. Forgetting the mod
  removes the instance when it still sits at its default placement with nothing inside it, and otherwise
  leaves it in the world unbound. When its instance is destroyed, the next load creates a new one.
- A reload whose header drops `context:` keeps the context of the existing instance. A reload to another
  context moves an instance that still sits at its default placement and changes its `RunContext`; one a
  player or script moved stays where it is. A reload that changes the class (a script to `shared`, a
  ModuleScript to a script, a LocalScript to a server script) replaces the instance: a new one of the new
  class takes its name, its parent (or the new context's default placement, when the old one sat at its
  own), its children and its `Enabled`, with a new id; the old one goes once the reload succeeded, and a
  failed reload puts it back. Moving the mod to another folder moves a default-placed instance with it.
- A legacy mod reloaded with a header that declares a context hands what its legacy folder held to the new
  instance (a clean reload removes what the mod itself owned there, as its startup objects) and the legacy
  folder goes.
- A world with a declared mod is saved as package format version 2, which keeps the instance and its binding;
  a world whose mods declare nothing is saved exactly as before (`Docs/CoreAIMods/WORLD_PACKAGE.md`, "Script
  instances"). When such a world loads, a declared mod whose instance the world no longer holds stays detached
  and does not start until it is loaded again. A world saved as version 1 keeps the instance as a plain
  unbound script, and the next load of the mod binds it again when it still sits at its placement under the
  mod's name, instead of making a second one.
- Whether a script with its own instance starts is decided when it loads (MVP4 SC-9): a ModuleScript never
  starts on its own; a script with `Enabled` false does not start; a script must sit where its class runs (a
  server script in `Workspace`, `ServerScriptService` or `ReplicatedStorage`, a client script in `Workspace`,
  `ReplicatedStorage` or `ReplicatedFirst`, a script that declares nothing only in `Workspace` or
  `ServerScriptService`; nothing runs in `ServerStorage`); and a declared script starts only on a side this
  machine has: a `context: client` script never on a dedicated server, a `context: server` script never on
  a pure client. In single-player and on a host (one machine that is both server and client) both start,
  each once. A mod that declares nothing keeps the pre-MVP4 rule and starts on every machine it started on
  before. A script that does not start stays loaded: it is listed with the reason as its `run_state`, keeps
  its instance, source and versions, and starts when a reload or a change of its instance lets it.
- The same rules follow the instance while the world runs (MVP4 SC-3, SC-9), checked at the start of the next
  frame: setting `script.Enabled = false` (or the deprecated `Disabled = true`), moving the script into a
  container where it does not run, or destroying it (or a parent) stops it — its threads, connections, timers
  and tweens end, what it made stays, and it stays loaded, listed with the reason (`disabled`,
  `container-does-not-run`, `detached`). Setting `Enabled = true` again, or moving it back, starts its main
  chunk afresh and keeps what the earlier run made. A script parented to nil while running keeps running. None
  of this writes the mod store or a revision. A quarantined script is never restarted by `Enabled`; reload it.
  A script that disables itself stops at the next frame, not at its next line.
- Script members in Lua: `Enabled`, `Disabled` and `RunContext` (an `Enum.RunContext` item) are readable by
  every script. `Source` (the mod's current text; `""` for a script no mod is bound to) is readable only with
  the host's unrestricted authority. Writing `Source` or `RunContext` is for the host's console
  (`execute_lua`) only; a mod gets `NOT_AUTHORITY`. A `Source` write reloads the mod with the new text (a new
  revision, like `manage_mods reload`); a `RunContext` write restarts the script under the new context and
  records it in the stored manifest, and is refused for a mod whose header declares `context:` (edit the
  header) or declares nothing (its script is rebuilt at every start). TODO: writing `Source` on a script no
  mod is bound to fails with `NOT_IMPLEMENTED` until a later MVP4 step; load the code as a mod with a
  `parent:` header instead. A copy made by `Clone()` never runs (it is bound to no mod); the cloning mod is
  told once.

Modules and `require` (MVP4 step C4; `dev-docs/MVP4_SCRIPT_CONTEXTS_DESIGN.md`, SC-10 to SC-12, SC-18, SC-19):
- `require(module)` takes the ModuleScript of a `context: shared` mod, or a Roblox path string relative to the
  calling script: `./X` starts at `script.Parent`, `../X` at `script.Parent.Parent`, `@self/X` at `script`,
  `@game/ReplicatedStorage/X` at `game`, and a `..` component goes up one more. A path never waits: a missing
  module fails at once (use `WaitForChild` and require the instance). A number (an asset id) is refused with
  `BAD_ARGUMENT` (CoreAI has no published models, DEV-127); an instance that is not a ModuleScript, a ModuleScript
  no mod is bound to (made with `Instance.new` or `Clone`) and an unloaded module are refused naming it.
- The module runs the first time a script requires it, on the requiring thread, with the requiring script's side
  and authority, and it must return exactly one value that is not nil. Every later `require` of it in that script
  returns the same value, so a returned table is shared by all threads of the script. Another script runs its own
  copy (DEV-126: Roblox shares it between scripts of one side; CoreAI keeps every script in its own environment
  and notes it once per module). Inside the module, `script` is the ModuleScript and its globals stay in the
  module; it still reads the requiring script's globals.
- A module that yields (for example `WaitForChild` at its top level) makes the requiring thread wait until it
  returns; a second thread that requires it meanwhile waits for the first one. A cycle (A requires B, which
  requires A) raises `CYCLIC_REQUIRE: Requested module was required recursively: ReplicatedStorage.A ->
  ReplicatedStorage.B -> ReplicatedStorage.A` (DEV-1).
- A module that raises fails at the `require` line with its own error (its chunk is `mod:<module id>`, so the
  line is the module's); the module's log records the failure too, and the error is charged to the requiring
  script. Later requires in that script raise `Requested module experienced an error while loading: ...` until
  the module's source changes or the script restarts.
- Who may require what: a module written by another actor than the script's author is refused with
  `NOT_AUTHORITY` (the host's modules may be required by everyone), and a `context: client` script may not require
  a module under `ServerStorage` or `ServerScriptService` (`CONTEXT_VIOLATION`).
- Reloading or reverting a module restarts every running script that required it, directly or through another
  module, in the reload's mode, server-side scripts first; the reload reports them, and one whose restart fails
  keeps its old run. Unloading a module restarts nobody. The console (`execute_lua`) may require modules too; a
  module that yields there raises `CONTEXT_VIOLATION`, as every wait in the console does.
- An error raised in a mod's code names the chunk it came from, `[mod:<id> script:main.lua line:<n>]` for a host
  error and `[string "mod:<id>"]:<n>:` for a Lua error. A tail call (`return f()`) replaces its caller's frame
  (DEV-111), so an error raised by the called host function is attributed to the next mod frame out.

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
- `string Context = "";` — the context the header declares, in its canonical spelling (`server`,
  `client`, `shared`) when it is valid, otherwise the value the manifest already held, otherwise empty
  (a legacy script). Every manifest writer derives it the same way (`LuaModManifest.ResolveContext`), and
  the world package's mod entries carry it. Omitted from JSON when empty, so the manifest of a mod that
  declares nothing is byte-identical to one written before the field existed; a 7.56.0 reader refuses a
  package whose mod manifest carries it. A restore of a stored mod whose header declares no context and
  that has no script instance yet starts it with this context (§1, "Where `script` lives").
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
list/get_source/edit/load/reload/unload/forget/export/import/versions/revert/diagnostics and the folder actions
below. `reload` cleans the
previous run's startup objects (§5c) unless the optional `keep_objects: true` is passed (the MCP wrapper
`ManageModsMcpTool` forwards it); its result says which mode ran (`clean_startup_objects` or
`keep_objects`) and what it cleaned. When a mutating action changed the live startup world but the change
will not reopen after a restart, the result carries a `startup_warning`
([`WORLD_PACKAGE.md`](WORLD_PACKAGE.md), "Startup selection"). The `category` from the `@coreai` header
is persisted into the manifest.

Reading and changing an existing mod (the way the model is told to change a mod; `reload` with the whole
source stays for replacing a mod outright):

| Action | Arguments | Does |
|---|---|---|
| `get_source` | `mod_id`, optional `from_line` (1-based, default 1), optional `line_count` (default: as many as fit) | returns one page of whole lines: `first_line`, `last_line`, `total_lines`, `total_chars`, `next_from_line` (only while more lines remain; the message names it too) and `data`, the text exactly as stored. A page is sized so its serialized result stays within `ICoreAISettings.MaxToolResultChars` (8,000 by default, the cut `ToolExecutionPolicy` applies to every tool result; 0 = unlimited) and within `LuaModsLlmTool.MaxSourceLengthReturned` (16,000 source characters), so the pages of a mod concatenate back to its exact source (a cap smaller than a page's own message and numbers, a few hundred characters, cannot be met and is cut as usual). A single line too long for any page is shown cut, with a `--[[ …[+N chars] ]]` marker and `cut_line`; the next page starts at the following line. A `from_line` past the end is refused with the line count |
| `edit` | `mod_id`, `old_text`, `new_text` (empty deletes), optional `replace_all` (default false), optional `keep_objects` | replaces an exact occurrence of `old_text` in the mod's current source, then reloads the result through the same call as `reload`: the same authorization, rate limit, backup trigger (`manage_mods-reload`, because the undo service and the self-repair authorship tracker key on it), revision, persistence and `keep_objects` behaviour, and a failed load changes nothing. The result names the replacements, the lines the new text covers and the new revision |

`edit` refusals change nothing and say what to send next: `old_text` empty; `new_text` missing; `old_text`
equal to `new_text`; `old_text` not found (with the lines where its first line appears, so the model can read
them with `from_line`); `old_text` found N times without `replace_all` (with the lines, asking for more
surrounding context or `replace_all=true`). Line endings: when `old_text` does not occur exactly, it is
looked up once more with CRLF read as LF on both sides (a source saved on Windows can hold CRLF while a model
writes LF), and `new_text` is always written in the line endings the source mostly uses. No other whitespace
is ignored. `export` of a mod whose bundle is longer than `MaxToolResultChars` says in its message that the
bundle arrives cut and cannot be imported, and points at `get_source` and `edit`.

Folders through `manage_mods` (parameters `folder` and `target`):

| Action | Arguments | Does |
|---|---|---|
| `list` | optional `folder` | each mod carries its `folder`, its `context` (`legacy`, `server`, `client`), its `script_path` (where `script` lives, as `GetFullName` spells it) and its `run_state` (`running`, `quarantined`, `suspended`; for a mod loaded without a run `module`, `detached`, `disabled`, `not-on-this-side` or `container-does-not-run`); a `folders` array lists every folder (`path`, `mods`); with `folder` only that branch, an unknown one is `FOLDER_NOT_FOUND` |
| `load` | `mod_id`, `code`, optional `folder` | writes `category: <folder>` into the header before loading (the existing casing of a folder is reused) |
| `move` | `mod_id`, `folder` (empty = top level) | moves a loaded mod without running it again: only its header line and stored manifest change |
| `create_folder` | `folder` | creates an empty folder; `FOLDER_ALREADY_EXISTS` when it exists in any casing |
| `rename_folder` | `folder`, `target` = new name | renames in place; a case-only rename is allowed |
| `move_folder` | `folder`, `target` = new parent (empty = top level) | moves the folder with everything in it; `MOVE_INTO_ITSELF` into itself or a subfolder |
| `delete_folder` | `folder` | deletes an empty folder with its empty subfolders; `FOLDER_NOT_EMPTY` while a mod is inside |

A refused call's message starts with the code. Every folder action that changes something spends the
same rate limit as `execute_lua` and the other changing `manage_mods` actions, and a read-only agent gets
none of them. A folder that a move empties is kept until it is deleted. Without `folder`, `load` keeps the
source exactly as sent unless the host names a default folder for AI mods
(`ICoreAISettings.AiModsDefaultFolder`, empty by default): adding the `category:` line would otherwise shift
the line numbers of the mod's errors.

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
- Toolbar: a search box (name / folder / tags), **Refresh**, **Add** (new mod in the editor),
  **Paste** (opens the Lua in the system clipboard, `GUIUtility.systemCopyBuffer`, in the editor),
  **Import** (installs an export bundle from the clipboard) and **New folder**.
- Mods are shown as a folder tree: a folder holds mods and other folders, folders first, each with a fold
  arrow and the count of its mods that are on (`3/12`; `empty` for a folder without mods). A mod without a
  folder sits at the top level. The list is virtualized, so a thousand mods scroll like ten, and the folds
  you choose are remembered (PlayerPrefs key `CoreAI.Hub.Mods.FoldState`).
- Search filters by name, id, tags and folder path and opens every folder that holds a match; clearing it
  brings your folds back.
- A folder's toggle switches its whole branch on or off (mixed when only some are on); a folder next to it
  is never touched, even one whose name starts the same.
- **Move** on any row starts "Move to": the folders that can take the item show **Here**, the banner offers
  **Top level** and **Cancel**. Moving a running mod does not restart it. Folder rows also have **New
  folder**, **Rename** and **Delete** (only an empty folder; a folder with mods says `FOLDER_NOT_EMPTY`).
  A folder a move empties is kept; created and kept folders are stored in PlayerPrefs
  (`CoreAI.Hub.Mods.Folders`) unless the host passes its own folder list to `HubModsPage`.
- Each mod row: an **On/Off** toggle (load / unload), name and status line, **Edit**, **Export** (bundle to
  the clipboard) and **Delete**; **Apply update** appears when the manifest's `UpdateAvailable` is set, and
  **About** when the mod has a `description:`. About opens a panel under the tree with the description as
  Markdown (`CoreAiMarkdownView`: rendered when the optional renderer package is installed, plain text
  otherwise); About on the same mod or **Close** hides it, and it follows a refresh.
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

Each `HubModRecord` also carries the mod's `Context`, `RunState` and, while loaded, `ScriptPath` (§1, "Where
`script` lives"). They come from the runtime for a loaded mod and from the manifest the listing already holds
otherwise (`unloaded`, or `suspended` after budget trips), never from a store read of their own. Deleting a mod
in the Hub ends the binding of its script instance the way a forget does (`ILuaModScriptInstanceReleaser`).

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
- **Cleared by reload:** a successful `manage_mods reload` or `edit` (or `ReloadMod`) swaps in a fresh
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
- Empty folders in a world package, and a folder list shared by the Hub and `manage_mods` from the
  composition root (today the tool keeps its own in-memory list unless the host passes one).
- An in-memory cache for `FileLuaScriptVersionStore`.
- Standard host world events for mods (§5).
- Routing the §5b write-policy core over a network transport.

## 8. Platform / compatibility notes

- WebGL: the `Resources` source works. File stores such as `FileLuaModSourceStore` confirm durability
  through `CoreAiWebGlPersistence` (the engine's automatic `persistentDataPath` persistence) instead of
  driving IDBFS by hand. The seeder is synchronous.
- Windows path length: the file stores write paths of at most 259 characters on every runtime (whether Unity's
  Mono on Windows would accept longer ones when the OS allows them is not verified, and nothing relies on it). The
  folders they name after an id use `CoreAiStorageNames`: a new store namespace is `Stores\s-<26>` and a new mod
  folder `m-<26>` (the first 128 bits of the id's SHA-256 in lowercase base32: case-safe on Windows, a collision
  about 2^-64 likely at a billion ids); a folder an earlier build wrote under its long name (`id-<64 hex>`, or
  `id-<52 base32>` in a version-1 session) keeps loading, updating in place and deleting under that name, and a
  confirmed world load stages its sources in a version-2 session (`.source-layout` = `sha256-base32-128-v2`) with
  short folders only. With a store id the longest paths below the storage root are: a confirmed world load's staged
  sources 138 characters (`\CoreAI\Mods\Stores\s-<26>\.world-sessions\<26>\m-<26>\manifest.json.tmp`), a mod's
  first save 108 (it stages in `.staging-<32 hex>`), the startup selection 111, mod data and a 64-character manual
  slot 128; so every store works from a storage root of up to 121 characters (157 without a store id), where the
  long names allowed 55. Past that a save, the best-effort `Save` included, throws
  `LuaModSourceStorageRootTooLongException`, which names the folder and says to point `CoreAiStorageRoot.Override`
  at a shorter one or shorten the company or product name `persistentDataPath` is built from; the session's
  admission check refuses the mod before it runs, the Hub shows the message, and the seeder counts such a mod in
  `SeedResult.Failed` (with `FirstFailure`) and logs an error instead of counting it as installed. The seeder also
  reads every save back, so a host store that drops a save silently is reported the same way. A build older than the
  short names sees an empty store under a store id and cannot load (and so refuses to capture) a mod saved here in
  the default store; it never reads a wrong mod. Tests: `FileStoreLongRootEditModeTests`,
  `StoragePathBudgetEditModeTests`.
- The `COREAI_LUA` define gates the Lua demos, benchmark scenarios and Lua-dependent tests; the
  Lua-CSharp runtime ships bundled as `Lua.dll`/`Lua.Annotations.dll` in this package (see
  [the package README](../../Assets/CoreAIMods/README.md)).
- All seven `com.neoxider.coreai*` packages bump in lockstep at release time.
