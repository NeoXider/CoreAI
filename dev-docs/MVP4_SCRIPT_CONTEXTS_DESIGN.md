# MVP4 — Script contexts and the client runtime: design note

Status: **design, nothing built**. Written 2026-11-11 for the rung "MVP4 — Script contexts & client
runtime" (`Docs/CoreAIMods/ROBLOX_API_ROADMAP.md:929-977`) and the open item "Design note for plan
decision D1 (c) before any code" (`TODO.md:557-561`). Plan decision D1 (c) — "instances are views over
the mod source store" — is at `ROBLOX_API_ROADMAP.md:4634`; the owner may still override it.

How to read the citations:

- Every line number is at commit `654bb318` (HEAD of `demo`), because several files named here have
  uncommitted edits by other agents while this was written: `Scripting/LuaCs/LuaCsRbxApiBindings.cs`,
  `Scripting/LuaCs/LuaCsRbxInstanceBindings.cs`, `RbxApi/Instances/ClassCatalog.cs`,
  `RbxApi/Datatypes/RbxEnum.cs`, `Composition/CoreAiModsInstaller.cs`,
  `Infrastructure/RbxWorldPackageSerializer.cs`, `RbxApi/Instances/Networking/RbxPlayers.cs`,
  `LuaExecution/LuaModsLlmTool.cs` and
  `Assets/CoreAI/Runtime/Core/Features/AgentPrompts/BuiltInRbxApiSkillText.cs`. Their line numbers will
  drift; the members named stay.
- Paths without a prefix are under `Assets/CoreAIMods/Runtime/`. Files that exist only in the working
  tree (P1's bundled control script, the P18 host) are cited from the working tree and say so.
- "Mirror" means the offline Roblox reference `D:\Git\RobloxDocs\creator-docs\content\en-us\`.
- "Verified" means I read the code or ran a check; "not verified" means I could not, and the step that
  depends on it starts with a test that settles it.

One check was run rather than read: a throw-away .NET 8 reflection probe over the bundled
`Assets/CoreAIMods/Plugins/Lua.dll` (in the session scratchpad, not in the repository). It showed
`LuaState.Load(ReadOnlySpan<char> chunk, string chunkName, LuaTable environment)`,
`Traceback.StackFrames` with `CallStackFrame.Function`, and a public field `Prototype.ChunkName`. These
three facts are what make per-module environments and per-chunk error attribution possible without
rewriting source (§3, SC-13 and SC-19).

---

## 0. The decisions in one line each

| # | Decision |
|---|---|
| SC-1 | One store entry is one source and is bound to at most one script instance; the instance is an ordinary durable world instance that names its source by `SourceId` (= the mod id). |
| SC-2 | `Source` is a view: reading it reads the runtime's current revision of that entry; writing it is a reload through the existing store, versions and gate, from the elevated console only. |
| SC-3 | `Enabled` is per-instance state, saved in `world.json`, never a store write; false stops the run (instances stay), true starts a fresh run, and it can never clear quarantine or a budget suspension. |
| SC-4 | `RunContext` is a real member of `Script` (Legacy, Server, Client; Plugin refused); written only by the elevated console, and a change restarts the script (R1.9). |
| SC-5 | The declared context is written in the `@coreai` header (`context: server|client|shared`), copied into `LuaModManifest.Context`, and held for a world by the bound instance's class and `RunContext` in `world.json`. |
| SC-6 | Placement: a header `parent:` path, else a default container per context (`ServerScriptService` for server, `ReplicatedStorage` for client and shared) under the mod's folder path. |
| SC-7 | The world package bumps `format_version` 1 → 2; a v1 package still reads, through one migration path; `api_version` stays `MVP2`. |
| SC-8 | A package or store entry without a context loads as **legacy** — today's behaviour exactly — not as `shared` and not refused. |
| SC-9 | A script runs when one engine-free predicate says so: loaded, enabled, not quarantined or suspended, in a container its class may run in (R1.3, R2.1), and on a side the topology has (§3.2). |
| SC-10 | `require` takes a `ModuleScript` or a Roblox string path, never blocks on a missing module, refuses asset ids, and checks author trust and location before running anything. |
| SC-11 | A module runs once per requiring script's environment (the script's own Lua state) and returns the same value to every `require` there; two scripts get two copies (a recorded deviation). |
| SC-12 | A cycle raises `CYCLIC_REQUIRE` naming the path; a second thread waits instead; a module that errored stays failed for that environment until its source changes. |
| SC-13 | `CONTEXT_VIOLATION` is raised from one data table of side-only members, checked at the binding sites for declared contexts only. |
| SC-14 | `IsNetworkServer` = server context → true, client → false, legacy → today's `Topology != Client && IsHost`; `RunService:IsServer/IsClient`, `LocalPlayer` and the remote sender follow the same context. |
| SC-15 | A replica never mints a `Player`: `EnsureNetworkActor` refuses on `RegistryAuthority.Replica`, and a client script starts only after the replica holds the local player. |
| SC-16 | The client composition stages a filtered join payload into a replica registry through a new `IRbxWorldSessionHost`, then starts only `client` scripts; server and legacy sources never reach it. |
| SC-17 | `Instance.new("Script"/"LocalScript"/"ModuleScript")` works; a clone of a script instance is unbound and does not run until MVP13 (loud note). |
| SC-18 | Reloading or reverting a module restarts every running script that required it, inside the same gated operation, with the same reload mode. |
| SC-19 | Every chunk is named `mod:<id>`, modules are downleveled once at load with their own id, and errors are attributed to the chunk that raised them while the error streak is charged to the running script. |

---

## 1. What the code does today

The facts the decisions rest on, all verified by reading:

- **One source per mod, keyed by id.** `ILuaModSourceStore.Save(id, source, manifest)`/`TryLoad`
  (`LuaExecution/ILuaModSourceStore.cs:77-109`); `LuaModManifest.Entry = "main.lua"`
  (`LuaExecution/LuaModManifest.cs:79-80`); the package writes `Mods/NNNN/manifest.json` +
  `Mods/NNNN/main.lua` (`Infrastructure/RbxWorldPackageSerializer.cs:935-944`).
- **One Lua state per mod.** `BuildMod` creates `mod.State = _engine.CreateState()` for every load
  (`LuaExecution/LuaCsModRuntime.cs:2085`); the `Mod` record carries state, caps, handlers, quarantine
  and startup objects (`LuaCsModRuntime.cs:495-560`).
- **Mod isolation is deliberate.** `mods_get`/`mods_call` copy plain data by value "so no mod can
  mutate another's state behind its back" (`LuaCsModRuntime.cs:4673-4677`, `:4699-4745`), and the
  Roblox `shared` global is a loud *unsupported* stub because "a table every mod of the world can
  write is the shared mutable state mod isolation exists to prevent"
  (`Scripting/LuaCs/LuaCsRbxApiBindings.cs:3763-3767`).
- **The synthetic `script`.** Registered per mod in `RegisterCore` (`LuaCsRbxApiBindings.cs:3453-3457`)
  and built by `GetOrCreateExecutingScript` as `ServerScriptService/<modId> (Folder)/<modId> (Script)`,
  both mod-owned runtime infrastructure (`LuaCsRbxApiBindings.cs:3829-3865`), so capture excludes it
  (`Infrastructure/RbxWorldPackageSerializer.cs:161-199`). Pinned by
  `Lua_R1_1_ProductionPath_ScriptGlobalIsItsExecutingRegistryInstance`
  (`Tests/EditMode/RbxApi/LuaBindings/RbxApiLuaBindingsEditModeTests.cs:994-1029`).
- **Script classes exist but are inert.** `LuaSourceContainer` and `BaseScript` are abstract, `Script`
  and `LocalScript` are concrete but not creatable, and there is no `ModuleScript`
  (`RbxApi/Instances/ClassCatalog.cs:570-577`); no `Enum.RunContext` in `RbxApi/Datatypes/RbxEnum.cs`.
- **`require` is removed** by the sandbox (`Scripting/LuaCs/LuaCsSecureEnvironment.cs:819`).
- **The side is derived from the grant.** `IsNetworkServer => Topology != Client && IsHost`
  (`Scripting/LuaCs/LuaCsRbxInstanceBindings.cs:214-215`); `RequireNetworkSide` raises
  `NOT_AUTHORITY` (`:217-234`) at the remote members (`:1087-1124`, `:1263-1270`, `:1903-1918`;
  `LuaCsRbxApiBindings.cs:4471`). `RunService:IsServer/IsClient` answer per process from
  `IRbxRuntimeTopology` (`LuaCsRbxInstanceBindings.cs:1894-1899`), whose bridge implementation says a
  host cannot answer `IsClient` "because CoreAI does not yet let a mod declare which context it runs
  in" (`RbxApi/Instances/RbxRuntimeTopology.cs:82-88`).
- **Players are minted on demand.** Any non-server context calls `EnsureNetworkActor`
  (`LuaCsRbxApiBindings.cs:3428-3431`), which creates a Player through `RbxPlayers.EnsureActor`
  without looking at `Registry.Authority` (`LuaCsRbxApiBindings.cs:2335-2357`;
  `RbxApi/Instances/Networking/RbxPlayers.cs:386-410`); so does `GetLocalPlayer` for a non-server
  context (`LuaCsRbxApiBindings.cs:2197-2205`). A lookup-only `RbxPlayers.GetLocalPlayer(actorId)`
  exists (`RbxPlayers.cs:600-604`). The remote sender is the script owner's actor
  (`LuaCsRbxApiBindings.cs:2207-2213`). `LocalPlayerActorId` already names "this process's local
  player" for the click pump (`LuaCsRbxApiBindings.cs:3140-3149`, resolver `:3243-3262`).
  **Not verified:** I found no production call that connects the solo player at the screen;
  `ConnectActor` (`LuaCsRbxApiBindings.cs:1544-1555`) is called only from tests and the Mirror
  provider.
- **Replica registries exist.** `RegistryAuthority { Authoritative, Replica }` and the constructor
  parameter (`RbxApi/Instances/InstanceRegistry.cs:45-49`, `:177-205`); a replica forces local ids on
  `Create` (`:1101-1107`) and marks a write outside `BeginReplicationApply` as diverged (`:784-795`,
  `:878-889`). `RestoreFresh` always builds an authoritative registry
  (`RbxWorldPackageSerializer.cs:806-810`).
- **Session hosts.** `IRbxWorldSessionHost.Stage(payload)`
  (`Infrastructure/RbxWorldPackageContracts.cs:306-330`); `HeadlessRbxWorldSessionHost` stages through
  `RestoreFresh` and swaps on `Commit` (`RbxWorldPackageContracts.cs:1256-1410`).
- **Versions are source-only.** `RecordRevision` keys `mod:<id>` and stores source text
  (`LuaCsModRuntime.cs:3133-3145`); `TryRevertMod` reloads from a revision when loaded
  (`LuaCsModRuntime.cs:3180-3240`). The header travels with every revision because it is part of the
  source.
- **Manifests are rebuilt from the header at every load** (`LuaCsModRuntime.BuildManifest`,
  `LuaCsModRuntime.cs:5515-5537`), and four writers build them independently: that one,
  `HubModServiceBase` (`HubIntegration/HubModServiceBase.cs:496-507`), `BundledModSeeder.BuildManifest`
  (`Infrastructure/BundledModSeeder.cs:180-200`) and `RbxWorldPackageSerializer.CloneManifest`, which
  copies a fixed field list (`RbxWorldPackageSerializer.cs:2038-2060`).
- **The package reader is strict and versioned.** `CurrentFormatVersion = 1`,
  `CurrentApiVersion = "MVP2"` (`RbxWorldPackageSerializer.cs:22-24`); exact version and API checks
  (`:1719-1737`); unknown JSON members are refused (`MissingMemberHandling.Error`, `:1904`). The store
  is lenient (`Infrastructure/FileLuaModSourceStore.cs:59-62`).
- **Restart order.** `CompareRestoreOrder` (`LuaCsModRuntime.cs:5081-5111`), used by
  `RehydrateFromStore` (`:5122-5184`) and the staged world start (`:5186` onward).
- **Chunk names.** Every chunk is loaded as `"sandbox_chunk"`
  (`Scripting/LuaCs/LuaCsRbxSchedulerAdapter.cs:508`; `LuaCsSecureEnvironment.cs:782`, `:808`), and an
  error is attributed to the state's owner mod with the virtual path `main.lua`
  (`Scripting/LuaCs/LuaCsRbxValues.cs:144-175`).
- **Yielding host calls.** Waits are served by a Lua bridge that captures host primitives into
  upvalues and erases them from `task` (`LuaCsRbxSchedulerAdapter.cs:94-119`, run per state at
  `:520-523`); `RunModChunk` spawns the main chunk as a scheduler thread
  (`LuaCsRbxApiBindings.cs:1998-2045`).
- **Error codes already exist.** `RbxErrorCode.CyclicRequire` and `ContextViolation`
  (`RbxApi/Instances/RbxError.cs:19`, `:23`, `:85`, `:89`); `CONTEXT_VIOLATION` is in use today for
  waits and connections without an owning mod (`LuaCsRbxApiBindings.cs:4191-4207`).
- **Replication hides server containers.** `ServerStorage`, `ServerScriptService`, `PlayerScripts`
  and more are never visible to a client (`RbxApi/Instances/Replication/ReplicationFilter.cs:49-66`).
- **Clone hook.** `RbxInstance.CopyCustomStateTo` is the per-class copy hook
  (`RbxApi/Instances/RbxInstance.cs:681`, `:1189`).
- **The game tree** has `ReplicatedStorage`, `ServerStorage`, `ServerScriptService` and
  `StarterPlayer` but no `ReplicatedFirst`, `StarterPlayerScripts` or `StarterCharacterScripts`
  (`RbxApi/Instances/DataModelBootstrap.cs:19-38`).
- **The default world has no tree on disk.** Boot seeds bundled mods, restores the startup selection,
  and rehydrates the default world's mods only; the default world is never recorded
  (`Docs/CoreAIMods/WORLD_PACKAGE.md:313-314`, `:370-380`).

What Roblox specifies (normative `Docs/CoreAIMods/RobloxReference/01_SCRIPTS_AND_SCHEDULER.md` and the
mirror):

- R1.1–R1.13 (classes, `RunContext`, `Enabled`, termination; `01_SCRIPTS_AND_SCHEDULER.md:21-74`),
  R2.1 (container matrix, `:98-110`), R3.1–R3.8 (`require`, `:167-192`).
- `BaseScript.Enabled` is `NotReplicated` and not saved (`classes/BaseScript.yaml:55-91`); the
  deprecated inverse `Disabled` is saved (`:21-52`). `BaseScript.RunContext` is written only at
  `PluginSecurity` (`:132-153`). `Script.Source`/`ModuleScript.Source` need the `PluginOrOpenCloud`
  capability (`classes/Script.yaml:37-67`, `classes/ModuleScript.yaml:86-108`).
- `require` by string: `./`, `../`, `@self/`, `@game/`, non-blocking; the cache is per side of the
  boundary; asset ids only on the server (`reference/engine/globals/LuaGlobals.yaml:511-590`).
- A module may return any value except nil (`scripting/module.md:22`).
- **The mirror disagrees with itself on cycles**: the class page says a cyclic `require` hangs and
  "do[es] not generate errors" (`classes/ModuleScript.yaml:41-45`); the guide says it "results in a
  `Requested module was required recursively` error" (`scripting/module.md:25`). DEV-1
  (`ROBLOX_API_ROADMAP.md:337`) was written against the first.
- `RunService:IsClient`/`IsServer` are per execution context, and a `ModuleScript` takes its
  requirer's (`classes/RunService.yaml:374-387`, `:465-478`).
- Side-only members named in the mirror: `UserInputService` (client,
  `classes/UserInputService.yaml:17-20`), `PreRender`/`BindToRenderStep` (client,
  `classes/RunService.yaml:195`, `:880`), HTTP from servers (`classes/HttpService.yaml:12`),
  `TextChannel:SendAsync`, `DisplaySystemMessage`, `MessageReceived` (client,
  `classes/TextChannel.yaml:93-100`, `:126-132`, `:210-219`) and `SetDirectChatRequester` (server,
  `:164-172`).
- Roblox recommends client scripts as `Script` with `RunContext = Client` in `ReplicatedStorage` and
  server scripts in `ServerScriptService` (`scripting/locations.md:24-46`).

---

## 2. Hazards checked before deciding

| Area | What could go wrong | What the code says | Answer in this design |
|---|---|---|---|
| Versioned stores | A context kept only in the manifest is not versioned, so a revert silently keeps the new context | Revisions are source text only (`LuaCsModRuntime.cs:3133-3145`) | The header is the authoring truth (SC-5); a revert re-applies the old revision's `context:` |
| Revert | A reverted module leaves its dependents on the new copy | `TryRevertMod` reloads only the mod itself (`:3221-3224`) | SC-18 restarts dependents |
| Quarantine | `script.Enabled = true` from another script restarts a quarantined or budget-suspended mod | Quarantine is cleared only by a reload (`mod-system.md:241-243`); suspension is persisted (`LuaCsModRuntime.cs:3568`) | SC-3: `Enabled` never clears either |
| Hot reload | A clean reload destroys the script instance with the startup objects | Startup objects are what the main chunk registered and the mod still owns (`mod-system.md:328-337`) | The bound instance is world-owned and created before the capture window, so it is never a startup object |
| World package | A new manifest field vanishes in capture | `CloneManifest` copies a fixed list (`RbxWorldPackageSerializer.cs:2038-2060`) | Step A2 adds `Context` there and to the three other writers, with one test per writer |
| World package | A source with no instance means "default placement" in old packages but "detached" in new ones | — | A semantic change, so `format_version` 2 (SC-7) |
| World package | A durable script under a non-durable folder breaks capture | The projection throws on a missing retained parent (`:190-195`) | Default-placement folders are world-owned too (SC-6) |
| Autosave gate | A gameplay toggle writes the store outside the gate and makes the startup selection write whole packages | Store writes are recorded at once (`WORLD_PACKAGE.md:285-288`) | No gameplay path writes the store: `Enabled`, reparenting and `Destroy` are tree state only; `Source` and `RunContext` writes are console-only, inside `execute_lua`'s gate |
| Luau downleveler | Module errors point at the wrong line | Lines are preserved per chunk (`LuaExecution/LuauSourceGate.cs:18-23`) | Modules are downleveled once, with their own id (SC-19); no source prelude, because `Load` takes an environment (verified) |
| Error contract | A module error names the requirer and `main.lua` | Attribution by the state's owner (`LuaCsRbxValues.cs:159-171`) | Attribute by `Prototype.ChunkName` of the innermost Lua frame (SC-19) |
| Mod ownership | Instances made by module code have no teardown owner | Ownership comes from the context that registers the binding (`LuaCsRbxInstanceBindings.cs:36-99`) | Module code runs in the requirer's context, so the requirer owns what it makes |
| Capability tiers | A module raises its own tier, or a `Full` module blocks world restore | Bindings are registered per state by tier (`LuaExecution/LuaCapabilities.cs:6-9`); an active `Full` mod refuses restore (`WORLD_PACKAGE.md:353-355`) | Modules run with the requirer's tier and persist `Capabilities = None` |
| Isolation | Per-side module sharing would hand one actor's closures to another | `shared` is unsupported for exactly this reason (`LuaCsRbxApiBindings.cs:3763-3767`) | SC-11 keeps one environment per script; SC-10 adds an author-trust rule |
| Yields | A module that calls `WaitForChild` at top level cannot yield through a host call | Yields through counted calls are refused (MVP3 round 3, `ROBLOX_API_ROADMAP.md:853-856`) | The module body is a scheduler thread; the requirer waits through the wait bridge |
| Replica | Restoring into a replica marks every record diverged, or minting a Player there is a protocol violation | `InstanceRegistry.cs:784-795`; `TODO.md:2145-2153` | Stage inside `BeginReplicationApply` (SC-16); `EnsureNetworkActor` refuses on a replica (SC-15) |
| Join snapshot | Server sources reach clients through a script placed in `Workspace` | The filter hides server containers only (`ReplicationFilter.cs:49-66`) | Server and legacy `SourceId`s are stripped and their sources never enter a client payload (SC-16) |
| Corpus | A strict default context breaks Tier-A fixtures that use input | `TAC-009-userinput-began.lua` runs as a mod today | Legacy keeps today's rules (SC-8) |
| Hub performance | The Mods page reads disk-backed stores per row | Forbidden (`mod-system.md:206-212`) | Run state and detachment come from the runtime, never the store |
| Wrappers | A new runtime member is lost by a facade | Every `ILuaModRuntime` wrapper must forward (`mod-system.md:341-345`) | No new required interface member; new data rides on `LuaModInfo` |
| Source limit | Modules fill the 256-source ceiling | `RbxWorldPackageSerializer.MaximumMods = 256` (`:32`) | Modules count as sources; P20 measures and raises the ceiling |

---

## 3. Decisions

### SC-1 — Script model: a durable instance bound to one store entry

A `Script`, `LocalScript` or `ModuleScript` is an ordinary registry instance (`RbxLuaSourceContainer`,
§5 step A1) with a hidden `SourceId`. A store entry (mod id) is bound to **at most one** instance, its
*bound instance*. The bound instance is **world-owned** (`OwnerModId = null`), carries the ledger
origin `mod:<id>` and the mod's owner actor, and is saved in `world.json` like any other instance, so
its `Name`, `Parent`, `Enabled`, `RunContext` and children survive save and load. The unit of execution
stays the mod: one store entry, one run, one `Mod` record, one Lua state, one error streak.

- `script` in a main chunk is the bound instance. `script.Parent` is whatever contains it, so a
  `Script` inside a `Part` sees that part (DoD row 3).
- A source whose bound instance was destroyed is **detached**: it stays in the store, keeps its
  versions and is listed with `run_state: detached` until a load re-creates the instance.
- `SourceId` is not visible to Lua; the AI sees the instance path in `manage_mods list`.
- The run key is the mod id in MVP4. MVP13 may introduce `<modId>@<instanceId>` for cloned runs
  (SC-17); nothing in MVP4 assumes the key and the store key are the same string beyond today's code.

Alternatives rejected:

- *(P-B) Placement in the manifest, instance materialized at start and excluded from capture.* No
  package change, but the instance's children (a `ModuleScript` under a `Script`, value objects) cannot
  be saved — their parent is not in `world.json`, and the projection refuses such a tree
  (`RbxWorldPackageSerializer.cs:190-195`) — and a parent named by path breaks on rename.
- *(P-C) Both: durable instance plus a placement copy in the manifest.* Two truths to reconcile on
  every load.
- *Several instances per source in MVP4* (clones that run). Every runtime map is keyed by mod id
  (`_mods`, owner attribution, connection generations, quotas, logs); re-keying them is MVP13-sized
  work and no MVP4 DoD row needs it.

### SC-2 — `Source` is a view over the store

- **Read** returns the runtime's current source of `SourceId` (the last loaded revision; for a dormant
  entry, the store's). **Write** of a bound instance is `ReloadMod(id, text)` in the default mode;
  write of an unbound instance creates a store entry: id = the instance `Name` normalized by the rule
  `manage_mods` uses and made unique (`Name`, `Name_2`, ...), owner = the console's actor, context from
  the instance's class and `RunContext`, placement = the instance, admitted by
  `ILuaModSourceAdmission.CanAdmit` like any first load.
- **Who:** both read and write require the elevated tier (`ActorContext.Grants.IsUnrestricted`), and
  write additionally requires a one-shot console context (no owning mod), which is always inside
  `execute_lua`'s gate (`WORLD_PACKAGE.md:214-221`). A mod gets `NOT_AUTHORITY`. This is the DEV-14
  reading (`ROBLOX_API_ROADMAP.md:351`) applied to the `PluginOrOpenCloud` capability of `Source`
  (mirror `classes/Script.yaml:67`): DEV candidate **SC-d**.
- **Versions:** unchanged key `mod:<id>`; every write is a revision. **Revert:** unchanged; a reverted
  revision re-applies its own `context:`/`parent:` directives (SC-5). **Quarantine:** per run,
  unchanged. **Package:** `Mods/NNNN/main.lua` stays the only copy of the text; the instance never holds
  source.

Rejected: *`Source` readable by every script* (Roblox refuses it to game scripts, and it would leak a
restricted actor's code to another actor); *`Source` writable by mods* (a mod writing mods at runtime,
outside the gate and the version-write grants).

### SC-3 — `Enabled`

- A property of `Script` and `LocalScript`, default true, saved in `world.json` (Roblox saves the same
  state through `Disabled`). `Disabled` is provided as an alias with a once-per-mod deprecation note,
  as for `wait`/`spawn` (`ROBLOX_API_ROADMAP.md:108-113`).
- `false` stops the run: threads, connections, timers and tweens end (R1.10); instances the run made
  **stay** (Roblox keeps them), and the store is not touched. A script that disables itself stops before
  its next line and cannot re-enable itself (R1.10).
- `true` starts a fresh run in `ModReloadMode.KeepObjects` when the run predicate (SC-9) holds.
- It **never** clears quarantine or `SuspendedAfterBudgetTrips`; on such a script it records the flag
  and logs once that a tool reload is needed. Otherwise a second script could restart a mod that trips
  its budget every run.
- `ModuleScript` has no `Enabled` (it has no `BaseScript` ancestor, R1.1).
- The manifest's `Active` keeps its meaning — "loaded" (the Hub toggle, `manage_mods load/unload`).
  A script runs only when both are true.

Rejected: *`Enabled` as a view over `Active`* — every toggle would write the store, and through the
startup selection a whole package (`WORLD_PACKAGE.md:285-299`); toggling to restart is a common Roblox
idiom.

### SC-4 — `RunContext` and the effective context

`Enum.RunContext` is added with the mirror's values (`Legacy 0, Server 1, Client 2, Plugin 3`,
`reference/engine/enums/RunContext.yaml`). The effective context of a script:

| Class | `RunContext` | Manifest `Context` | Effective context |
|---|---|---|---|
| `ModuleScript` | — | `shared` or empty | `shared` |
| `LocalScript` | (must be Legacy, R1.8) | `client` or empty | `client` |
| `Script` | `Server` | `server` | `server` |
| `Script` | `Client` | `client` | `client` |
| `Script` | `Legacy` | `server` (an imported Legacy `Script`, MVP14) | `server` |
| `Script` | `Legacy` | empty | `legacy` (SC-8) |
| `Script` | `Plugin` | — | refused: CoreAI has no plugins |

- Read by anyone. Written only by the elevated console (mirror write security `PluginSecurity`,
  `classes/BaseScript.yaml:153`; DEV-14): the write updates `Context`, and a running script restarts
  under the new context (R1.9). A write on a script whose header declares `context:` is refused with
  "edit the header" — the header would undo it at the next reload.
- A declared-context change that changes the class (`server` → `shared`) replaces the instance: same
  `Name`, `Parent` and children, new class, new id.

### SC-5 — Where the declared context lives

- **Header** (authoring, versioned with the source): `context: server|client|shared`, case-insensitive.
  Absent means legacy. Any other value fails the load with
  `BAD_ARGUMENT: @coreai context 'clinet' is not server, client or shared`. `LuaModHeader.Parse` stays a
  pure, non-throwing function (`LuaExecution/LuaModHeader.cs:39-48`); the refusal is raised by the load.
- **`LuaModManifest.Context`** (`"" | "server" | "client" | "shared"`), omitted from JSON when empty, so
  every existing manifest is byte-identical (the `LoadOrder` precedent, `LuaModManifest.cs:93-99`). It is
  the derived copy every writer sets through one helper; the store-only default world and
  `manage_mods list` read it.
- **World package:** `Mods/NNNN/manifest.json` carries the same field; `world.json` carries the bound
  instance's class and `RunContext`. The reader refuses a package where they disagree
  (`invalid_package`), because the reader never repairs untrusted input (`WORLD_PACKAGE.md:87-90`).
- **Precedence on a load or reload:** the header when it declares a context, else the existing bound
  instance, else the manifest (store-only rehydrate, an import bundle without a header), else legacy.
  A restore does not re-apply header directives: it keeps the saved tree.

Rejected: *manifest only* (not versioned, and invisible to the AI reading `get_source`); *a separate
per-mod file* (a second entry per mod, a layout change of `Mods/`, no gain).

### SC-6 — Placement

- **Header `parent:`**: a `/`-separated path from `game` with the component rules of `@game/` require
  paths (mirror `LuaGlobals.yaml:525-537`); a leading `@game/` is accepted. It applies at every load and
  reload. Unresolved, an interactive load fails with `BAD_ARGUMENT` naming the missing segment; a
  rehydrate or restore leaves the source detached with that reason, so boot never stops on it.
  The parent must be world-owned; a mod-owned parent is refused (it would not save).
- **Default placement** for a new instance with no `parent:`:
  - `server` → `ServerScriptService/<folder path>/<id>` as `Script` with `RunContext = Server`;
  - `client` → `ReplicatedStorage/<folder path>/<id>` as `Script` with `RunContext = Client`
    (Roblox's recommendation, `scripting/locations.md:33`; it runs once per client and is never copied);
  - `shared` → `ReplicatedStorage/<folder path>/<id>` as `ModuleScript`; a server-only module is
    placed with `parent: ServerStorage/...` and is then never shipped (R2.1);
  - `legacy` → exactly today's `ServerScriptService/<id> (Folder)/<id> (Script)` with
    `RunContext = Legacy`, so `script.Parent` is still the mod's own folder (`TAC-015` keeps passing).
- `<folder path>` is the manifest `Category` split on `/` (it is already a path,
  `Docs/CoreAIMods/mod-system.md:45`); the segments are world-owned `Folder`s found or created by name.
- Gameplay reparenting is live tree state like any other and saved with the world; a later reload
  re-applies a header `parent:`.

Rejected: *`LocalScript` in `StarterPlayer/StarterPlayerScripts` as the client default* — Roblox copies
it into every `Player.PlayerScripts` (R2.3), the per-copy run is MVP13 work, and the container does not
exist yet (`DataModelBootstrap.cs:28`); *server scripts in `ReplicatedStorage`* (R1.6 calls it "not
recommended").

### SC-7 — Package format: `format_version` 2

- `world.json` instances gain an optional `script` block: `source_id` (string, empty = unbound),
  `enabled` (bool), `run_context` (int). `Mods/NNNN/manifest.json` gains `Context`.
- **Why a bump:** the meaning of a `Mods/` entry changes. In v1 every active entry starts at the
  synthetic location; in v2 an entry without a bound instance is detached and does not start. The
  compatibility policy asks for a bump on a semantic change (`WORLD_PACKAGE.md:476-478`). The additive
  precedents (`LoadOrder`, `diagnostics.member`, `RbxWorldPackageSerializer.cs:2102-2108`) did not
  change any meaning, so they did not need one.
- The writer emits `format_version: 2` and `minimum_reader_version: 2`. The reader accepts 1 and 2; a v1
  payload is marked `LegacyScriptPlacement = true`, and the session gives every active v1 mod a bound
  instance at the legacy placement before it starts. A reader older than this refuses a v2 package
  explicitly (`RbxWorldPackageSerializer.cs:1719-1724`).
- New reader checks, each a refusal with a twin: a `source_id` naming no `Mods/` entry (dangling
  reference); two instances with the same `source_id`; a class/`RunContext`/`Context` disagreement;
  `LocalScript` with a non-Legacy `RunContext`; `RunContext = Plugin`.
- New capture diagnostics (capture repairs the live world, `WORLD_PACKAGE.md:60-80`): a bound instance
  inside a mod-owned subtree is omitted with reason `mod-ephemeral` and member `Script`, so the source
  loads detached; an instance whose source is gone is saved unbound with reason `missing`, member
  `SourceId`.
- `api_version` stays `"MVP2"`: the reader compares it exactly (`:1733-1737`), so changing it would
  refuse every saved world; versioning the mod API is MVP18's (`ROBLOX_API_ROADMAP.md:254-255`).

Rejected: *no bump* (a v2 package with zero script instances would be indistinguishable from a v1
package and either start every mod at the wrong place or start none).

### SC-8 — Packages and mods without a context load as legacy

`legacy` is today's behaviour exactly: the side is `Topology != Client && IsHost`, the side refusals stay
`NOT_AUTHORITY`, `UserInputService`, the camera and the rest of the client surface keep working in Solo
and Host, the placement is the synthetic one, and none of the new `CONTEXT_VIOLATION` rows apply. A
legacy script never runs on a pure client and its source never enters a join snapshot. Each legacy mod
logs once: `note: mod '<id>' declares no context and runs as a legacy server-side script; add
"context: server", "client" or "shared" to its @coreai header.` The MVP13 export lint flags legacy
scripts that use client-only members.

Pinned by `Mvp4_PackageV1WithoutContexts_LoadsAsLegacyScripts_GoldenTree` and its twin
`Mvp4_PackageV2_SourceWithoutBoundInstance_IsDetached` (DoD row 4).

Rejected:

- *Load as `shared`*: `shared` is a `ModuleScript` (§3.2, `ROBLOX_API_ROADMAP.md:408`), which runs only
  when required, so every saved world, every store and every bundled sample would stop running.
  Reading "shared" as "run on both sides" instead would run each old mod twice in host mode and build
  every castle twice.
- *Refuse*: every saved world, autosave and startup selection becomes unloadable, and every AI-written
  mod without the new key fails.
- *Load as strict `server`*: `sample_tetris3d`, `sample_lane_racer`, `sample_clicker` and P1's control
  script read input or the camera; so does corpus fixture `TAC-009`. They would fail with
  `CONTEXT_VIOLATION`, the store copies the player edited would break on upgrade, and the Tier-A rate
  would drop, which the DoD forbids (`ROBLOX_API_ROADMAP.md:975`).

### SC-9 — When a script runs

One engine-free predicate, `ScriptRunRules.Evaluate` (§5 step A1), is re-evaluated whenever one of its
inputs changes (load, unload, `Enabled`, `RunContext`, ancestry, destruction, quarantine, topology, the
local player appearing):

1. The entry is loaded (`Active`), not quarantined, not suspended.
2. The class runs on its own: `ModuleScript` never does (R2.1).
3. `Enabled` (R1.3).
4. The nearest container permits the class (R1.3, R1.5–R1.8, R2.1; built in
   `ScriptRunRules.Place`):

   | Container | Legacy `Script` | `Server` | `Client` | `LocalScript` |
   |---|---|---|---|---|
   | `Workspace` | runs | runs | runs (every client, even inside another player's character) | only inside the local player's character |
   | `ServerScriptService` | runs | runs | — | — |
   | `ReplicatedStorage` | — | runs | runs | — |
   | `ReplicatedFirst` | — | — | runs | runs |
   | `StarterPlayerScripts`, `StarterCharacterScripts` | — | — | runs in place | — (template) |
   | a player's `Backpack`, `PlayerGui`, `PlayerScripts` | — | — | local player only | local player only |
   | `ServerStorage`, `StarterGui`, `StarterPack`, anything else | — | — | — | — |

   A `Client` script in a `StarterPlayer` folder runs in place because Roblox runs "the original
   script **and** the copy" (`scripting/locations.md:25`); the per-player copies, and so the
   `LocalScript` templates, are DEV candidate SC-e and MVP14's. A client script in another player's
   `PlayerGui` (or that player's character, for a `LocalScript`) is `not-on-this-side`. The binding
   layer supplies "inside whose character or player folder" (`ScriptPlayerScope`), because only it
   knows which model is a character. Detached to nil while running keeps running (R1.12); destroyed
   stops (R1.11).
5. The topology has the side (§3.2, `ROBLOX_API_ROADMAP.md:404-408`):

| Effective context | Solo | Host | DedicatedServer | Client |
|---|---|---|---|---|
| `server` | runs | runs | runs | never |
| `client` | runs once (the local client) | runs once (the host's client) | never | runs |
| `shared` | on require | on require | on require | on require |
| `legacy` | runs | runs | runs | never |

6. A `client` script also waits until the process's local player exists (R2.2): Solo and Host resolve
   it with the existing rule (`LocalPlayerActorId`, else the followed character's player, else the only
   player, `LuaCsRbxApiBindings.cs:3243-3262`); a replica requires `RbxPlayers.GetLocalPlayer` to answer.

Start order on every restart: the server side (`server` and `legacy`) first, then `client`, each in
`CompareRestoreOrder` order (R2.2: client scripts start after the client received its world). An
all-legacy world starts exactly as today. A run state is reported for every entry: `running`,
`disabled`, `quarantined`, `suspended`, `detached`, `module`, `not-on-this-side`,
`container-does-not-run`, `waiting-for-local-player`.

Moving a running script into a container that does not run it (for example `ServerStorage`) stops it.
The rulebook's text is ambiguous here (R1.12 says eligibility stops; it does not say the thread dies);
this design follows its implementation note "re-evaluate the predicate on any of the four inputs
changing" (`01_SCRIPTS_AND_SCHEDULER.md:78-79`) and records the stance as an UNCERTAIN item to verify.

### SC-10 — `require`: arguments and checks

`require` is installed per chunk environment through the scheduler's Lua bridge, never through Lua's
own module loader (which stays removed, `LuaCsSecureEnvironment.cs:819`). In order:

1. **Argument** (R3.5): a `ModuleScript` instance; or a string path relative to the *calling chunk's*
   `script` — `./`, `../`, `@self/`, `@game/`, `..` components — resolved by `FindFirstChild` without
   waiting (R3.6, mirror `LuaGlobals.yaml:525-537`); a number raises
   `BAD_ARGUMENT: require(assetId) loads a published model, which CoreAI does not have | fix: put the
   module in the world and require the instance`; anything else, `BAD_ARGUMENT`.
2. **Bound:** an unbound `ModuleScript` (made with `Instance.new` or cloned) raises
   `BAD_ARGUMENT: ModuleScript '<path>' has no source`; an unloaded entry raises `... is unloaded`.
3. **Trust:** a module runs with its requirer's authority, so its author must be trusted at least as
   much: allowed when the module's owner actor is the requirer's or the host (empty
   `OwnerActorId`, `LuaModManifest.cs:58-59`); otherwise `NOT_AUTHORITY: module '<id>' belongs to actor
   '<a>'; a script of actor '<b>' cannot run its code`. Without this, a restricted actor could plant
   code in a module a host script requires.
4. **Location:** a `client` requirer may not require a module under `ServerStorage` or
   `ServerScriptService` (`CONTEXT_VIOLATION`: a client never receives it; in Solo it would only work
   because the registry is shared).
5. **Cache and cycles:** SC-11 and SC-12.

The console (`execute_lua`) may require modules too: its environment is the one-shot state, and a module
that yields there raises `CONTEXT_VIOLATION` like every other wait without an owning mod
(`BuiltInRbxApiSkillText.cs:54-68`).

### SC-11 — `require`: one environment per requiring script

The module's compiled chunk (downleveled once, at module load) is loaded into the **requiring script's
own Lua state** with `LuaState.Load(source, "mod:<moduleId>", environment)` (verified to exist), where
`environment` is a fresh table holding `script` = the `ModuleScript`, `require` bound to that
`ModuleScript`, and a metatable whose `__index` is the state's globals. Module globals therefore stay in
the module, `script` inside a module is the module (R1.1), and relative `require` paths inside a module
resolve from the module. The cache key is `(requiring state, module instance id)`.

What holds: R3.1, R3.2 and R3.3 within one script and everything it requires; R3.4 trivially (each
script is one side). What does not: a module required by two top-level scripts on the same side runs
twice and the two do not share its table. Roblox documents patterns that rely on sharing
("Data sharing", "Custom events", `scripting/module.md`), so this is DEV candidate **SC-a**, made loud:
the first time a module loads into a second environment on the same side the runtime logs once per
module `note: module '<path>' now runs separately in scripts '<a>' and '<b>'; its table is not shared
between them (CoreAI keeps every script in its own environment) — share state through attributes or
value objects, or move both scripts' logic into one script that requires the module`.

Rejected:

- *One environment per side* (the Roblox VM model): the module table would be shared between authors
  (the confused-deputy hole `shared` was closed for), and instance proxies are per mod context
  (`LuaCsRbxInstanceBindings.cs:30`, `:282-289`), so the same part reached through the module and
  through the script would not compare equal.
- *One environment per (author, side), each module in its own state, values handed across states*:
  the same proxy-identity break, plus a module's closures running on another state's thread with its
  own bindings — an unverified engine path.
- Widening the environment to a *mod package* (MVP18 folder mods: all scripts of one mod in one state
  with per-script `_ENV`) is the right later answer for imported places; it is open question Q2.

### SC-12 — `require`: cycles, concurrency, failures

- The module body runs as a scheduler thread spawned in the requirer's state, under the requirer's
  context, as a child of the requiring script (R1.13). If it finishes without yielding, `require`
  returns at once and does not yield (`classes/ModuleScript.yaml:37-41`); if it yields (for example
  `WaitForChild` at top level, the common client-module pattern of `scripting/module.md`), the requirer
  waits through the wait bridge until it completes. Where a wait is refused today (a
  `coroutine.create` coroutine, the console; `BuiltInRbxApiSkillText.cs:54-68`), the module body runs
  as a plain call and a yield in it raises the same `CONTEXT_VIOLATION`.
- The result must be exactly one non-nil value (R3.1, `scripting/module.md:22`), else
  `BAD_ARGUMENT: module '<path>' returned <n> values` or `... returned nil`.
- **Cycle** (DEV-1): the engine-free `ModuleRequireGraph` records, per environment, which thread is
  loading which module and which module each waiting thread waits for. A request whose wait-for chain
  leads back to a module the requesting thread is itself loading — directly, or through another
  thread that waits — raises `CYCLIC_REQUIRE: Requested module was required recursively:
  ReplicatedStorage.A -> ReplicatedStorage.B -> ReplicatedStorage.A | fix: move what both need into a
  third module that neither requires, or require one of them inside a function` (Roblox's guide wording,
  `scripting/module.md:25`, plus the path). A request for a module another thread is loading, without
  such a chain, waits.
- **Failure** (R3.8): an error in the body is raised at the requiring call site, logged once in both the
  requiring mod's and the module's log buffers, and charged once to the requiring script's streak. The
  entry stays failed for that environment until the module's source changes or the script restarts;
  later requests raise `BAD_ARGUMENT: Requested module experienced an error while loading: <first
  error>` (my wording; the mirror does not give Roblox's text).

### SC-13 — The `CONTEXT_VIOLATION` matrix

The rules are data (`ScriptContextRuleTable`, §5 step A1): member, side, rule reference, fix. Binding
sites call `context.RequireContext(member)`; packages add rows at composition before the bindings are
built. The check applies to `server` and `client` scripts (and to modules through their requirer);
legacy scripts and the console keep today's rules. Message shape:

`[mod:<id> script:main.lua line:<n>] CONTEXT_VIOLATION: UserInputService.InputBegan is client-only
(§3.2; UserInputService.yaml) and this script runs as server | fix: move this code into a client script
(context: client) and send the result to the server with a RemoteEvent`

| Member (binding site at HEAD) | `server` | `client` | Rule |
|---|---|---|---|
| `RemoteEvent:FireServer`, `.OnClientEvent`, `RemoteFunction:InvokeServer`, `.OnClientInvoke` (`LuaCsRbxInstanceBindings.cs:1093-1098`, `:1112-1122`, `:1270`, `:1903`) | violation | ok | existing side rule, now coded `CONTEXT_VIOLATION` |
| `.OnServerEvent`, `:FireClient`, `:FireAllClients`, `.OnServerInvoke`, `:InvokeClient` (`:1087-1092`, `:1106-1111`, `:1123-1127`, `:1263`, `:1910-1918`; `LuaCsRbxApiBindings.cs:4471`) | ok | violation | same |
| `UserInputService` (every member and the global) | violation | ok | mirror `UserInputService.yaml:17-20`; DEV candidate **SC-b** (Roblox silently never fires) |
| `RunService.PreRender`, `RenderStepped`, `BindToRenderStep` (`LuaCsRbxInstanceBindings.cs:3426-3438`) | violation | ok | `RunService.yaml:195`, `:880` |
| `workspace.CurrentCamera` writes, `camera_*` globals, P1's camera queries | violation | ok | the camera exists only where frames are drawn; SC-b |
| `ServerStorage`, `ServerScriptService` reached through `GetService`, `game.X` or `FindFirstChild*` | ok | violation | `RunService.yaml:476-478`; SC-b (Roblox shows them empty) |
| `HttpService` requests | ok | violation | `HttpService.yaml:12` |
| `store_set`/`store_get`, `logic_define`/`logic_reset`, `coreai_world_*` | ok | violation | authoritative state lives on the server |
| `mods_call`/`mods_get` into a run of the other side | violation | violation | no direct call crosses the boundary (DoD row 2) |
| `events_emit` | same-side runs only | same-side runs only | host `EmitEvent` reaches server-side and legacy runs |
| `require` of a module under a server-only container | ok | violation | SC-10 |
| `UxmlService` (P18) | violation | ok | screens are per client |
| `TextChannel:SendAsync`, `:DisplaySystemMessage`, `.MessageReceived`, `TextChatService.SendingMessage` (P2) | violation | ok | `TextChannel.yaml:93-100`, `:126-132`, `:210-219` |
| `TextChannel:SetDirectChatRequester`, `.ShouldDeliverCallback` (P2) | ok | violation | `TextChannel.yaml:164-172`; `ShouldDeliverCallback` side not verified |
| `Script.Source` read/write, `RunContext` write | elevated console only, `NOT_AUTHORITY` | same | SC-2, SC-4 |
| `Players.LocalPlayer` | nil (Roblox parity) | the local player | not a violation |

The server-only container check fires where the service is reached; an instance reached by reference
(an `ObjectValue`) is not checked. The replication filter (MVP6) is the real boundary; this row exists
so Solo does not teach solo-only coupling.

### SC-14 — `IsNetworkServer`, `RunService`, `LocalPlayer`, the remote sender

`LuaCsRbxModContext` gains `ScriptContext Context` and `string NetworkActorId`:

| Context | `IsNetworkServer` | `RunService:IsServer()` / `IsClient()` | `Players.LocalPlayer` | Remote sender (`NetworkActorId`) |
|---|---|---|---|---|
| `server` | true | true / false | nil | — (server side) |
| `client`, host-authored | false | false / true | the process's local player | the local player's actor |
| `client`, other author | false | false / true | the author's player | the author's actor (as today) |
| `legacy` | `Topology != Client && IsHost` (unchanged) | the process topology (unchanged) | unchanged | the script owner's actor (unchanged) |
| module | the requirer's | the requirer's (`RunService.yaml:378-382`) | the requirer's | the requirer's |

A `server` script written by a restricted actor now runs as the server on Solo, Host and
DedicatedServer — the bug the rung's goal names (`ROBLOX_API_ROADMAP.md:933-937`) — while its world
writes stay under its actor's ACL. The ownership ledger and quotas still key on the author.

The "other author" row keeps today's security model: no client script on the host's process ever sends
a remote as an actor that did not write it. Without it, a restricted author's client script would fire
remotes as the host's player, and a server handler that trusts `player` would treat it as the host.
Whether other authors' client scripts should run on other players' clients at all is MVP11's question.

### SC-15 — No `Player` minting on a replica

- `EnsureNetworkActor` (`LuaCsRbxApiBindings.cs:2335-2357`) returns the existing Player and **throws**
  `InvalidOperationException("a replica admits players only through replication")` when
  `Registry.Authority == Replica` and none exists. `ConnectActor` (`:1544-1555`) refuses on a replica.
- `RegisterCore` (`:3428-3431`) no longer ensures a Player for a `client` context; `GetLocalPlayer`
  (`:2197-2205`) is a lookup for `client` contexts (`RbxPlayers.GetLocalPlayer`, `RbxPlayers.cs:600-604`).
- On a replica, a `client` script starts only when the replicated local Player is present (SC-9 item 6),
  so a script never asks before the seed arrives — the protocol violation `TODO.md:2147-2153` describes.

### SC-16 — The client composition

Engine-free, in two new files under `Infrastructure/` (not under the binding lease):

- `ReplicaRbxWorldSessionHost : IRbxWorldSessionHost`, modelled on `HeadlessRbxWorldSessionHost`
  (`RbxWorldPackageContracts.cs:1256-1410`). `Stage(payload)` refuses a payload that holds a
  `server`/legacy source or any descendant of `ServerScriptService`/`ServerStorage`; restores through
  `RestoreFresh` with the new `RbxWorldPackageRestoreOptions.RegistryAuthority = Replica`, inside one
  `BeginReplicationApply` scope so the captured revisions are the server's and nothing is marked
  diverged; `Commit` swaps like the headless candidate. **Not verified:** that
  `InstanceTreeSerializer.Restore` (`RbxApi/Instances/InstanceTreeSerializer.cs:608`, `:707`) runs
  unchanged on a replica; step A3 pins it first.
- `RbxClientWorldComposition`: builds the Rbx bindings over the staged registry with a `Client`
  topology bridge and `LocalPlayerActorId` = the client's actor, a `LuaCsModRuntime` with an in-memory
  source store and `autoPersistMods: false`, no `ConfirmedWorldMutationGate`, no `manage_mods` mutations,
  no startup selection; then starts every bound `client` script in order once the local Player exists.
- Server side: `RbxWorldPackageSerializer.CaptureForRecipient(context, recipientActorId,
  IReplicationFilter filter)` — the tree filtered by `IReplicationFilter.IsVisibleTo`
  (`ReplicationFilter.cs:15-18`, `DefaultReplicationFilter` at `:43`, `:78`);
  `server`/legacy instances that stay visible (a `Script` in a `Workspace` part) saved with
  `source_id = ""`; `Mods/` holding only `client`/`shared` sources bound to a visible instance.
- MVP4 proves it in process (EditMode); MVP5 carries the payload over a socket and MVP6 the deltas
  (`ReplicationApplier`).

### SC-17 — `Instance.new`, clones

- `Script`, `LocalScript` and `ModuleScript` become creatable. `Instance.new` makes an unbound instance:
  it runs nothing and `require` of it fails (SC-10); only an elevated console `Source` write binds it.
- `Clone()` copies class, `Name`, `Enabled`, `RunContext` and children through `CopyCustomStateTo`
  (`RbxInstance.cs:1189`) but **not** `SourceId`. When the original was a runnable script the runtime
  logs once per mod `NOT_IMPLEMENTED: a cloned Script does not run yet (roadmap MVP13: script templates)
  | fix: clone the part without the script and handle all copies from one script with CollectionService
  tags`. DEV candidate **SC-c**. MVP13 gives clones runs of their own.

### SC-18 — Hot reload and module dependents

- A script reload builds a new state, so its module cache is new and every module it requires runs
  again (§6.3's clean-state rule, `ROBLOX_API_ROADMAP.md:4485-4499`).
- A module reload or revert restarts every running script whose environment loaded that module,
  directly or through another module, in `CompareRestoreOrder` order and in the same `ModReloadMode`.
  It runs inside the same gated operation, so it costs one autosave (`WORLD_PACKAGE.md:214-221`).
  `ModReloadReport` gains `RestartedDependents` and `FailedDependents`; a dependent that fails keeps its
  old run, as a failed reload always does (`mod-system.md:269-283`).
- A module unload or forget does not restart anyone (there is nothing new to run); the report counts the
  scripts that still hold the old copy.
- Survival matrix additions for §6.3: the bound instance survives reload and disable; the module cache
  does not survive a reload of its script; a dependent restart follows the module reload's mode.

### SC-19 — Chunk names, downlevel, error attribution

- Main chunks are loaded as `mod:<id>` instead of `sandbox_chunk` (`LuaCsRbxSchedulerAdapter.cs:508`) —
  the pending §5.2.7 item (`ROBLOX_API_ROADMAP.md:293-300`). Module chunks are `mod:<moduleId>`.
- A module is downleveled once, when it is loaded, with `LuauSourceGate.ToLua52(source, moduleId)`
  (`LuauSourceGate.cs:38-49`); the compile source is cached on the module entry. Line numbers are those
  of the author's text.
- `LuaCsRbxValues.WithProductionContext` (`LuaCsRbxValues.cs:144-175`) reads the innermost Lua frame's
  `Prototype.ChunkName` (verified public) and attributes the error to that mod id; the quarantine streak
  is still charged to the running script.

---

## 4. How the packages being built fit

### P1 — the default player control script

Today a bundled system mod with no context (`Resources/CoreAIMods/coreai_player_controls.lua:1-10`,
working tree) whose `getHumanoid` falls back to `Players:GetPlayers()[1]` because it may run server-side
(`:43-46`). After MVP4:

- Its header gains `context: client` and a version bump, so `BundledModSeeder` updates installed copies
  (`mod-system.md:138-147`) and keeps a player's edited copy only through the existing rules. It lands at
  `ReplicatedStorage/System/coreai_player_controls` as `Script` with `RunContext = Client`. The fallback
  line goes; `Players.LocalPlayer` is non-nil by SC-9 item 6.
- It never runs on a dedicated server; it runs once on the host's own client and on each pure client.
- The P1 replacement idiom ("replace it with its own script of the same name",
  `ROBLOX_API_ROADMAP.md:1789-1791`) is the same mod id; the seeder never touches a user mod with that
  id (`mod-system.md:141`).
- `UserInputService`, `Humanoid:Move` and the camera module are in the client column of SC-13; the
  input-focus port and cursor port are C# and are not affected.
- Dependency: Solo must connect the local player before client scripts start. P1 needs a player for its
  character anyway; the composition that does it is not in the tree yet (§1, "not verified").
- Until MVP4 lands it keeps working as a legacy script, which is the point of SC-8.

### P18 — runtime UI from Lua (`UxmlService`)

- `UxmlService` (`dev-docs/RUNTIME_UI_LUA_SURFACE.md:24-43`) registers its members as client-only rows in
  `ScriptContextRuleTable`; it is available to `client` and legacy scripts, and to server scripts not at
  all. On a dedicated server the service reports `UI_DISABLED` as its own design says.
- Screen ownership stays `Owner = modId` (`RUNTIME_UI_LUA_SURFACE.md:43`, `:86`); the run key equals the
  mod id in MVP4, so nothing changes. `Enabled = false` is treated like the first half of P18's hot
  reload (connections dropped, screens kept until the next run re-creates them or ends its first run;
  `RUNTIME_UI_LUA_SURFACE.md:93-97`); unload, forget and quarantine destroy screens as P18 specifies.
- The `Ui` capability is orthogonal to contexts.

### P2 — the in-world chat's `TextChatService` subset

- Register the subset's members in `ScriptContextRuleTable` with the sides the mirror gives
  (`TextChannel.yaml` lines in SC-13). `ShouldDeliverCallback`, `OnIncomingMessage` and
  `TextChatCommand.Triggered` sides are not verified here; P2 reads them from the mirror when it adds
  the rows.
- The chat overlay and command registry are C# views over `CoreAiChatService`; only the Lua surface is
  contextual. Lua's single `MessageReceived` per answer is a client event, so the default-world agent's
  lines arrive in client scripts.
- `TextChatService` and its `TextChannel`s must live where clients see them (Roblox replicates
  `TextChatService`); creating them under a server-only container would hide them from every client.

### P20 — mod folders

- The manifest `Category` path is the `<folder path>` of SC-6, so the tree a player sees in the Explorer
  is the Roblox-shaped one (`ROBLOX_API_ROADMAP.md:3575-3576`). One mod folder may appear under two
  containers (server scripts in `ServerScriptService/F`, client scripts and modules in
  `ReplicatedStorage/F`), as in Roblox projects.
- MVP4 provides one hook P20 calls when a folder moves or a mod changes folder:
  `LuaCsModRuntime.RePlaceDefaultScript(modId)`, which moves a default-placed instance to its new folder
  path and leaves a header-placed one alone. Folder rename, delete-empty and branch enable/disable are
  P20's; branch enable/disable is `Active`, so the run predicate follows it.
- The path travels in the manifest as before; P20 needs no format bump
  (`ROBLOX_API_ROADMAP.md:3571-3572`). Its golden "a package saved before this change loads
  identically" must be read as a v1 golden after MVP4 (SC-7).
- A server script that needs a shared module in the same folder requires it by `game.ReplicatedStorage`
  path or `@game/ReplicatedStorage/F/X`, not `script.Parent`, because the containers differ.

---

## 5. Change list

Ordered so the tree compiles and the existing suites stay green after every step; the tests a step
deliberately changes are named in that step. Sizes: **S** up to two agent-days, **M** up to a week.
Steps A1–A3 touch no file currently under another agent's edits except where marked; B and C need the
binding files back.

### A1 (S) — Engine-free script model (new files in `RbxApi/Instances/Scripts/`) — done 2026-11-11

Namespace `CoreAI.Mods.Rbx.Instances.Scripts`; nothing registers these types yet, so no behaviour
changes. Tests: `Assets/CoreAIMods/Tests/EditMode/RbxApi/Scripts/` (five fixtures, linked into
`tools/portable/Tests/CoreAI.Portable.Tests.csproj`).

- `ScriptContext.cs`: `enum ScriptContext { Legacy, Server, Client, Shared }`; `ScriptClassNames`;
  `RunContextValues` (the `Enum.RunContext` integers until B1 registers the enum);
  `ScriptContexts.TryParseDeclared`, `ToDeclaredName`, `ToDisplayName`, `TryGetEffective(className,
  runContext, declared, out effective, out error)` (the SC-4 table; an undeclared context lets the
  instance decide), `ClassNameFor`, `RunContextFor`, `RunsOnServerSide`, `IsSideAvailable(context,
  RbxNetworkTopology)`, `SameSide`.
- `ScriptRunRules.cs`: `ScriptContainer`, `ScriptPlayerScope`, `ScriptPlacement`, `ScriptRunState`;
  the `ScriptRunInputs` parameter struct; `ScriptContainers.FromAncestorClassNames` and `IsServerOnly`;
  `ScriptRunRules.Evaluate(in inputs)`, `Place` (the SC-9 item 4 table), `DefaultContainerClass`,
  `ToWireName`.
- `ScriptContextRuleTable.cs`: `ScriptSide`, `ScriptContextRule(member, side, ruleRef, fix)` (member is
  `Class`, `Class.Member` or a CoreAI global; a bare class covers its members), `ScriptContextRuleTable`
  with `CreateDefault`, `Add`, `Freeze`, `TryGet` (exact, then class), `Check(member, context)` (null or
  the CONTEXT_VIOLATION; legacy is never checked) and `Violation`. P2 and P18 rows are added by those
  packages before `Freeze`, not by `CreateDefault`; the `coreai_world_*` rows come with C3, which knows
  the exact global names.
- `ModuleRequireGraph.cs`: `Begin(environment, module, modulePath, thread)` → `Load | Cached | Wait |
  Cycle | Failed`; `HandOff(environment, module, bodyThread)` for a body running on its own thread;
  `Complete` and `Fail` return the waiters to resume; `ForgetThread`, `Invalidate`, `ForgetEnvironment`,
  `EnvironmentsUsing`, `TryGetStatus`. Cycles are found by following the wait-for chain from the
  module's loader back to the requester.
- `RequirePath.cs`: require-by-string parse (`./`, `../`, `@self/`, `@game/`, `..`) and a non-blocking
  `Resolve<TNode>` over caller-supplied parent and child lookups.
- `ModuleRequirePolicy.cs`: `CheckTrust` (SC-10 item 3), `CheckLocation` (item 4) and the stable
  errors (`CycleError`, `PreviouslyFailedError`, `ReturnCountError`, `ReturnedNilError`,
  `AssetIdError`, `ArgumentTypeError`, `NotAModuleScriptError`, `NoSourceError`, `UnloadedError`,
  `PathError`).
- Moved to B1: the instance classes (`RbxLuaSourceContainer`, `RbxBaseScript`), because they need new
  `ReplicationMembers` names under `Replication/` and the catalog factory B1 edits anyway.

### A2 (S) — Header, manifest, one writer rule

- `LuaExecution/LuaModHeader.cs`: fields `Context` and `Parent` (raw text, empty when absent); keys
  `context` and `parent` in `Builder.Apply` (`:241-279`).
- `LuaExecution/LuaModManifest.cs`: `[JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
  [DefaultValue("")] public string Context = "";` and
  `public static string ResolveContext(LuaModHeader header, LuaModManifest existing)` (header when it
  declares one, else `existing.Context`, else empty).
- Use it in all four writers: `LuaCsModRuntime.BuildManifest` (`LuaCsModRuntime.cs:5515-5537`),
  `HubModServiceBase` (`HubModServiceBase.cs:496-507`), `BundledModSeeder.BuildManifest`
  (`BundledModSeeder.cs:180-200`), and `RbxWorldPackageSerializer.CloneManifest`
  (`RbxWorldPackageSerializer.cs:2038-2060`, a file another agent is editing now — merge).
- No runtime reads `Context` yet. A package captured after this step may carry `Context` in a v1
  manifest; an older reader refuses it explicitly, which is the accepted `LoadOrder` precedent.

### A3 (S) — Replica staging

- `Infrastructure/RbxWorldPackageContracts.cs`: `RbxWorldPackageRestoreOptions.RegistryAuthority`
  (default `Authoritative`).
- `Infrastructure/RbxWorldPackageSerializer.cs` `RestoreFresh` (`:777-843`): pass the authority to the
  registry (`:806-810`); for a replica, restore inside `registry.BeginReplicationApply()`.
- New `Infrastructure/ReplicaRbxWorldSessionHost.cs` (SC-16) without the source checks, which need B2.
- First test: today's v1 package staged into a replica keeps every captured revision and marks nothing
  diverged; a local write after commit is diverged. This settles the "not verified" point of SC-16.

### B1 (S) — Catalog and enum (files under P1's edits: `ClassCatalog.cs`, `RbxEnum.cs`)

- Instance classes (from A1): `RbxLuaSourceContainer : RbxInstance` with `string SourceId { get;
  internal set; }` (never copied by `CopyCustomStateTo`) and `RbxBaseScript : RbxLuaSourceContainer`
  with `bool Enabled` and `int RunContext` (`RunContextValues`), both raising `NotifyPropertyChanged`
  and advancing the revision under new `ReplicationMembers.Enabled`/`RunContext` names.
- `ClassCatalog.CreateMvp1` (`ClassCatalog.cs:570-577`): `Script` and `LocalScript` creatable with
  factory `RbxBaseScript`; new `ModuleScript` (base `LuaSourceContainer`, creatable, factory
  `RbxLuaSourceContainer`); `LinkedSource` as a known-unimplemented deprecated member.
- `RbxEnum`: `RunContext` (`Legacy 0, Server 1, Client 2, Plugin 3`).
- The synthetic script (`LuaCsRbxApiBindings.cs:3848-3851`) now gets an `RbxBaseScript` and keeps its
  path; script members are not bound to Lua yet, so Lua still sees what it sees today.
- Run the catalog fixtures; update a creatable-class golden if one exists (none found by search).

### B2 (M) — Package v2 reader and codec (the writer stays v1 until C1)

- `RbxApi/Instances/InstanceSnapshot.cs`: `ScriptSnapshot Script` with `SourceId`, `Enabled`,
  `RunContext`.
- `RbxApi/Instances/InstanceTreeSerializer.cs`: capture and restore of script state; validation of class
  against `RunContext` (`LocalScript` Legacy only, `Plugin` refused).
- `Infrastructure/RbxWorldPackageSerializer.cs`: `WorldScriptDto` under `WorldInstanceDto`
  (`:2162-2217`); `SupportedFormatVersions = {1, 2}` in `ValidateManifest` (`:1706-1779`);
  `ValidateScriptBindings(payload)` (dangling, duplicate, disagreement); the capture diagnostics of
  SC-7; `RbxWorldPackagePayload.FormatVersion` and `RbxWorldPackageRestoreResult.LegacyScriptPlacement`.
- Tests read hand-written v2 packages and every v1 golden; the writer still emits v1, because until C1
  binds instances a v2 package would declare every mod detached.

### C1 (M) — Registration and runs, bound instances, legacy placement, v2 writer

- `LuaCsModRuntime`: a `ScriptEntry` registration record (id, source, compile source, caps, owner,
  context, bound instance id, run state) separate from the `Mod` run record (`:495-560`);
  `LoadModInternal` (`:1831`) becomes register → bind or create the instance → `TryStartRun`; unknown
  `context:` and unresolved `parent:` refusals; module entries are registered and compiled, never run;
  the start-order partition in `RehydrateFromStore` (`:5122-5184`) and the staged start (`:5186`);
  `LuaModInfo` gains `Context`, `ScriptClassName`, `ScriptPath`, `RunState`, `Dependents`.
- Seam `IScriptInstanceHost` (new, `LuaExecution/`), implemented by `LuaCsRbxApiBindings`:
  `RbxLuaSourceContainer BindScriptInstance(string modId, ScriptContext context, string parentPath,
  string folderPath, string ownerActorId)`, `void ReleaseScriptInstance(string modId, bool destroy)`,
  `event Action<string, ScriptInstanceChange> ScriptInstanceChanged`.
- `LuaCsRbxApiBindings`: the `script` value (`:3453-3457`) is the bound instance;
  `ExecutingScriptBacking` (`:94`), its load-candidate bookkeeping (`:134-157`, `:1787`, `:1856-1860`)
  and `GetOrCreateExecutingScript` (`:3815-3865`) are replaced by the legacy placement of SC-6
  (world-owned folder and script).
- `RbxWorldRuntimeSessionController`: migrate v1 payloads; skip detached sources in the staged start
  instead of failing the candidate; writer emits v2.
- `LuaModsLlmTool`/`HubModServiceBase`: list fields and run states (another agent is editing
  `LuaModsLlmTool.cs` now — merge).
- Deliberately changed tests: `Lua_R1_1_ProductionPath_ScriptGlobalIsItsExecutingRegistryInstance`
  (the script is world-owned, no longer runtime infrastructure) and any literal package golden that
  carries mods (`WritePackage_AuthoredWorld_MatchesLiteralGoldenJson`,
  `Tests/EditMode/RbxApi/Acceptance/Mvp3WorldPackageEditModeTests.cs:1997`). The v1 reader golden
  (`ReadPackage_HandWrittenLiteralPackage_RestoresLiteralIdsParentsRevisionsAndPartState`, `:2140`) must
  pass unchanged.

### C2 (M) — Script members in Lua

- `LuaCsRbxInstanceBindings`: `TryReadScript`/`TryWriteScript` for `Enabled`, `Disabled`, `RunContext`,
  `Source`; `BoundProperties` rows for `BaseScript` and `LuaSourceContainer` (`:2005-2033`).
- `LuaCsModRuntime`: `StopRun(modId, ScriptStopReason)` over `TeardownModEffects` (`:3661`) with a new
  `LuaModTeardownReason.ScriptStopped` that keeps instances and writes nothing; restarts on
  `ScriptInstanceChanged`; the quarantine and suspension guard of SC-3; console `Source` writes (SC-2).
- `RbxBaseScript.CopyCustomStateTo`: unbound clones and the once-per-mod note (SC-17).

### C3 (M) — Contexts in the bindings

- `LuaCsRbxModContext` (`LuaCsRbxInstanceBindings.cs:23-257`): `Context`, `NetworkActorId`,
  `IsNetworkServer` (`:214-215`), `RequireNetworkSide` (`:217-234`: `CONTEXT_VIOLATION` for declared
  contexts, unchanged `NOT_AUTHORITY` for legacy), `RequireContext(string member)`.
- `RegisterCore` (`LuaCsRbxApiBindings.cs:3384-3437`) takes the context; `RunService:IsServer/IsClient`
  per context (`LuaCsRbxInstanceBindings.cs:1894-1899`); `GetLocalPlayer`, `FireRemoteServer`,
  `GetOnClientEvent` keyed by `NetworkActorId` (`LuaCsRbxApiBindings.cs:2197-2213`,
  `LuaCsRbxInstanceBindings.cs:1093-1098`); the `EnsureNetworkActor`/`ConnectActor` replica refusal;
  the SC-13 rows at their binding sites; cross-side `mods_call`/`mods_get`/`events_emit` in
  `LuaCsModRuntime` (`:4639-4750`); waiting for the local player.
- `RbxRuntimeTopology.cs:82-88`: the comment becomes "per-context answers live on the script context".
- Legacy tests asserting `NOT_AUTHORITY` stay green by construction.

### C4 (M) — `require`

- `LuaCsRbxSchedulerAdapter`: the bridge (`:94-119`) captures `task._requireResolve` and
  `task._requireAwait` and defines `require` per environment; `CaptureChunk` (`:497-510`) takes a chunk
  name.
- `LuaCsRbxApiBindings`: the two primitives over `ModuleRequireGraph`, the module environment table,
  module thread spawn like `RunModChunk` (`:2029-2044`) with its fault routed to the requirers, the
  trust and location checks, string paths, the SC-11 note.
- `LuaCsModRuntime`: module compile at load (downlevel once), dependent tracking and the restart of
  SC-18 in `ReloadMod` (`:2472`) and `TryRevertMod` (`:3180`).
- `LuaCsRbxValues.WithProductionContext` (`:144-175`): attribution by chunk name; main chunks named
  `mod:<id>`.

### C5 (M) — Client composition

- `ReplicaRbxWorldSessionHost` source checks; new `Infrastructure/RbxClientWorldComposition.cs`;
  `RbxWorldPackageSerializer.CaptureForRecipient`. Cuttable to the start of MVP5 if the rung runs long;
  it is in the rung's "To do" (`ROBLOX_API_ROADMAP.md:964-965`), not in its DoD.

### C6 (S) — Fits, skill, docs, acceptance

- P1's script `context: client` and version bump; the `@coreai` keys, contexts, `require` and the
  `CONTEXT_VIOLATION` rows in `BuiltInRbxApiSkillText.cs` and
  `Assets/CoreAiUnity/Resources/AgentSkills/RbxApi.txt` (both under other agents' edits now);
  `mod-system.md` (§1 keys, §2 field, a contexts section); `WORLD_PACKAGE.md` (v2); the roadmap's DEV
  table (SC-a…SC-e, DEV-1's mirror conflict) and MVP4 "Done so far"; `TODO.md`; the changelog.
- The acceptance fixture `Mvp4ScriptContextsAcceptanceEditModeTests` (DoD rows 1–4) and the corpus pin.

Total: five S steps and six M steps — at most 40 agent-days by the size caps, about 30–35 expected.
That is more than the rung's "M" label; see Q9.

---

## 6. Tests

Tiers: **PT** — portable engine-free (`tools/portable/Tests`, explicit file list); **PL** — portable
Lua conformance (`tools/portable/LuaTests` and the same fixtures in EditMode); **EM** — Unity
EditMode; **PM** — PlayMode `FastNoLlm`. Names follow §6.6 (`ROBLOX_API_ROADMAP.md:4583-4589`).
Every positive test names its negative twin.

### PT (A1, A2, B2)

| Test | Twin |
|---|---|
| `ScriptContext_ParsesServerClientSharedCaseInsensitively` | `ScriptContext_UnknownValue_ReturnsNamedError` |
| `R1_3_ScriptRunsOnlyWhenEnabledAndInARunningContainer` | `R1_3_DisabledScriptDoesNotRun` |
| `R1_5_LegacyScriptRunsInWorkspaceAndServerScriptService` | `R1_5_LegacyScriptInReplicatedStorageDoesNotRun` |
| `R1_6_ServerRunContextAlsoRunsInReplicatedStorage` | `R2_1_NothingRunsInServerStorage` |
| `R1_7_ClientRunContextRunsOnClientSides` | `ContextMatrix_ClientScriptNeverRunsOnDedicatedServer` |
| `ContextMatrix_ServerScriptRunsOnSoloHostAndDedicated` | `ContextMatrix_ServerScriptNeverRunsOnPureClient` |
| `R1_8_RunContextOnLocalScript_IsRefused` | `R1_8_LegacyRunContextOnLocalScript_IsAccepted` |
| `R1_12_DetachToNilKeepsARunningScript` | `R1_12_MoveIntoServerStorageStopsIt` (UNCERTAIN stance) |
| `R2_1_ModuleScriptNeverRunsOnItsOwn` | `R2_1_ModuleScriptIsRequirableFromBothSides` |
| `ContextRules_EveryRowHasSideRuleAndFix` | `ContextRules_FrozenTableRefusesAdd` |
| `RequireGraph_R3_2_SecondBeginIsCached` | `RequireGraph_InvalidatedModuleLoadsAgain` |
| `RequireGraph_DEV1_TwoModuleCycleNamesPath` | `RequireGraph_ConcurrentRequestWaitsInsteadOfCycling` |
| `RequireGraph_DEV1_CrossThreadWaitCycleIsDetected` | `RequireGraph_WaitChainWithoutLoopIsNotACycle` |
| `RequireGraph_R3_8_FailedModuleStaysFailedPerEnvironment` | `RequireGraph_OtherEnvironmentLoadsTheModuleAfresh` |
| `LuaModHeader_ParsesContextAndParentKeys` | `LuaModHeader_AbsentKeysAreEmpty` |
| `LuaModManifest_EmptyContext_IsOmittedAndByteIdentical` | `LuaModManifest_DeclaredContext_RoundTrips` |

### PL (A2 writers, B2 codec, C1–C4)

| Test | Twin |
|---|---|
| `ManifestWriters_AllFourCarryContext` (one case per writer) | `ManifestWriters_HeaderWithoutContext_KeepsTheStoredOne` |
| `PackageV2_ScriptInstances_RoundTripSourceIdEnabledRunContext` (golden) | `PackageV1_Golden_StillReadsAndIsMarkedLegacyPlacement` |
| `PackageV2_DanglingSourceId_IsRefused` | `PackageV2_BoundSourceId_IsAccepted` |
| `PackageV2_TwoInstancesOneSource_IsRefused` | `PackageV2_UnboundScripts_AreAccepted` |
| `PackageV2_ContextDisagreesWithInstance_IsRefused` | `PackageV2_ContextAgrees_IsAccepted` |
| `Capture_ScriptInsideModOwnedSubtree_IsOmittedWithDiagnostic` | `Capture_ScriptInsideWorldOwnedPart_IsSaved` |
| `Load_UnknownContextValue_FailsWithBadArgument` | `Load_DeclaredClient_CreatesClientScriptInReplicatedStorage` |
| `Load_UnresolvedParent_FailsNamingTheSegment` | `Load_ParentPath_PlacesTheScriptThere` |
| `R1_1_ScriptGlobalIsTheBoundInstance` | `R1_1_ConsoleHasNoScriptGlobal` |
| `R1_10_EnabledFalseStopsThreads_TrueRestartsFresh` | `R1_10_ScriptCannotReEnableItself` |
| `R1_9_RunContextWriteRestartsUnderTheNewContext` | `RunContextWrite_FromAMod_IsNotAuthority` |
| `Enabled_DoesNotClearQuarantine` | `Reload_ClearsQuarantine` (existing behaviour) |
| `Enabled_DoesNotClearBudgetSuspension` | `HandLoad_ClearsBudgetSuspension` (existing) |
| `SourceRead_FromElevatedConsole_ReturnsCurrentRevision` | `SourceRead_FromRestrictedMod_IsNotAuthority` |
| `SourceWrite_FromConsole_OnUnboundScript_CreatesEntryAndRevision` | `SourceWrite_FromHostMod_IsNotAuthority` |
| `DEVSCc_CloneOfRunningScript_IsUnboundAndNotesOnce` | `Clone_OfModuleScript_IsUnbound_RequireFailsNamed` |
| `ContextViolation_FireClientFromClientScript_NamesTheRule` (DoD 1) | `ContextViolation_FireClientFromServerScript_Passes` (DoD 1) |
| `ContextViolation_UserInputServiceFromServerScript` | `UserInputService_FromClientScript_Works` |
| `ContextViolation_ServerStorageFromClientScript` | `ServerStorage_FromServerScript_Works` |
| `ContextViolation_ModsCallAcrossSides` | `ModsCall_SameSide_Works` |
| `Legacy_UndeclaredMod_KeepsTodaysSideAndNotAuthorityCode` | `Declared_Server_UsesContextViolationCode` |
| `IsNetworkServer_RestrictedAuthorsServerScript_IsServerOnHost` | `IsNetworkServer_RestrictedAuthorsLegacyMod_IsClientAsToday` |
| `RunService_IsClientInClientScript_IsServerInServerScript_OneSoloProcess` | `RunService_LegacyAnswersFromTopology` |
| `ClientScript_OtherAuthor_SendsRemotesAsItsAuthor` | `ClientScript_HostAuthored_SendsAsTheLocalPlayer` |
| `R3_1_ModuleMustReturnExactlyOneValue` | `R3_1_ModuleReturningNil_IsRefused` |
| `R3_2_RequireExecutesOnce` | `R3_2_AfterModuleReload_DependentRunsItAgain` |
| `R3_3_RequiredTableIsSharedWithinTheScript` | `DEVSCa_TwoScriptsGetTwoCopiesAndOneNote` |
| `R3_4_ServerAndClientRequireRunTheModuleTwice` | — (the twin is R3_2) |
| `R3_5_RequireByStringPaths` (`./`, `../`, `@self/`, `@game/`, `..`) | `R3_5_RequireByAssetId_IsRefusedLoudly` |
| `R3_6_RequireMissingModuleFailsImmediately` | `R3_6_RequireAfterWaitForChildSucceeds` |
| `DEV1_CyclicRequireRaisesError` | `DEV1_ConcurrentRequireOfALoadingModuleWaits` |
| `R3_8_ModuleErrorPropagatesWithTheModulesLine` | `R3_8_SecondRequireReRaisesTheFirstError` |
| `Require_ModuleYieldingAtTopLevel_RequirerWaits` | `Require_SynchronousModule_DoesNotYieldTheRequirer` |
| `Require_ModuleScriptGlobalIsTheModule` | `Require_ModuleGlobalsDoNotLeakIntoTheRequirer` |
| `Require_ForeignActorsModuleFromHostScript_IsNotAuthority` | `Require_HostModuleFromRestrictedScript_Works` |
| `Require_ServerOnlyModuleFromClientScript_IsContextViolation` | `Require_ReplicatedModuleFromClientScript_Works` |
| `ErrorAttribution_ModuleErrorNamesTheModuleChunk` | `ErrorAttribution_MainChunkErrorNamesTheScript` |

### EM / PM (C1–C6)

| Test | Twin |
|---|---|
| `Mvp4_ScriptInsidePart_ParentIsPart_TouchedFires` (fake-touch driver; DoD 3) | `Mvp4_ScriptInServerScriptService_ParentIsTheService` |
| PM: `Mvp4_ScriptInsidePart_RealTouch` | — |
| `Mvp4_Solo_ServerClientShared_TalkThroughLoopbackRemotesOnly` (DoD 2) | `Mvp4_Solo_DirectModsCallFromClientToServer_IsContextViolation` |
| `Mvp4_PackageV1WithoutContexts_LoadsAsLegacyScripts_GoldenTree` (DoD 4) | `Mvp4_PackageV2_SourceWithoutBoundInstance_IsDetached` |
| `Mvp4_ScriptInstances_AppearAtTheirPaths_AndSaveWithTheWorld` | `Mvp4_DefaultWorldRestart_RecreatesDefaultPlacement` |
| `Mvp4_DestroyingTheParentStopsTheScript_SourceListsDetached` | `Mvp4_LoadOfDetachedSource_RecreatesItsInstance` |
| `Mvp4_ModuleReload_RestartsDependentsInOrder_OneAutosave` | `Mvp4_ModuleUnload_RestartsNobody` |
| `Mvp4_RevertToARevisionWithAnotherContext_ReappliesIt` | `Mvp4_RevertOfAHeaderlessScript_KeepsItsContext` |
| `Mvp4_ModuleErrors_ChargeTheRequirersStreak` | `Mvp4_ModuleErrors_AreLoggedInBothBuffers` |
| `Mvp4_ClientScript_WaitsForTheLocalPlayer` | `Mvp4_ClientScript_StartsWhenThePlayerExists` |
| `Mvp4_ClientComposition_StagesReplica_RunsOnlyClientScripts` | `Mvp4_ClientComposition_RefusesAPayloadWithAServerSource` |
| `Mvp4_ClientComposition_MintsNoPlayer` | `Mvp4_ServerComposition_MintsOnAdmission` (existing behaviour) |
| `Mvp4_EnsureNetworkActor_OnReplica_Throws` | `Mvp4_EnsureNetworkActor_OnAuthoritative_Mints` |
| `Mvp4_JoinSnapshot_CarriesNoServerOrLegacySource` | `Mvp4_JoinSnapshot_CarriesVisibleClientAndSharedSources` |
| `Mvp4_ExecuteLuaRunContextWrite_WritesExactlyOneAutosave` | `Mvp4_GameplayEnabledToggle_WritesNoStoreEntry` |
| `Mvp4_PlayerControls_DeclaresClient_RunsOnTheLocalClient` | `Mvp4_PlayerControls_DoesNotRunOnDedicatedServer` |
| `TierACorpus_Mvp4Gate_AtLeastHalf_NoFixtureRegresses` (DoD 5) | the existing `CombinedCorpus_MeetsTheMvp8UnmodifiedThreshold` (`TierACorpusEditModeTests.cs:651`) |

`TAC-020-players-localplayer` is classified Failing because it needs a client context
(`Tests/EditMode/RbxApi/CompatibilityCorpus/TierACorpusCatalog.cs:279-282`). The corpus catalog may run it
as a client script without touching its source (class and `RunContext` are instance metadata, as in
Roblox); whether `PlayerGui` is materialized for it is not verified (`ClassCatalog.cs:790` says
"empty Backpack/PlayerGui/PlayerScripts" stays a stub).

### DoD → tests

| DoD row (`ROBLOX_API_ROADMAP.md:969-975`) | Tests |
|---|---|
| Server-only API from a `client` script raises `CONTEXT_VIOLATION`; the twin from a `server` script passes | `ContextViolation_FireClientFromClientScript_NamesTheRule`, `ContextViolation_FireClientFromServerScript_Passes` |
| Solo runs all three contexts talking through loopback remotes only | `Mvp4_Solo_ServerClientShared_TalkThroughLoopbackRemotesOnly`, `Mvp4_Solo_DirectModsCallFromClientToServer_IsContextViolation`, `R3_4_ServerAndClientRequireRunTheModuleTwice` |
| `script.Parent` of a `Script` in a `Part` is that part and `Touched` works | `Mvp4_ScriptInsidePart_ParentIsPart_TouchedFires`, PM `Mvp4_ScriptInsidePart_RealTouch` |
| An old package without contexts loads exactly as decided | `Mvp4_PackageV1WithoutContexts_LoadsAsLegacyScripts_GoldenTree`, `PackageV1_Golden_StillReadsAndIsMarkedLegacyPlacement` |
| Tier-A rate does not drop; gate at least 50% | `TierACorpus_Mvp4Gate_AtLeastHalf_NoFixtureRegresses` |
| The rung's "Test" line: R3_x conformance, composition, package round trip | the `R3_*` and `DEV1_*` rows, `Mvp4_ClientComposition_*`, `PackageV2_*` |

---

## 7. Risks and open questions for the owner

Each with the default this note recommends.

1. **Q1 — Legacy instead of `shared` or refusal (SC-8).** The roadmap offered two options; this note
   picks a third. *Default:* legacy, deprecated with a note now; MVP18's `api_version` gate decides when
   undeclared mods stop loading.
2. **Q2 — Modules per script, not per side (SC-11, DEV candidate SC-a).** Documented Roblox patterns
   that share a module's table between scripts break, with a loud note. *Default:* accept for MVP4; give
   one mod package one state with per-script `_ENV` in MVP18 (folder mods) before MVP14 imports whole
   places.
3. **Q3 — `BindableEvent` is a backlog stub** (`ClassCatalog.cs:1332`). The guide's "Custom events"
   module pattern has no CoreAI workaround besides attributes. *Default:* pull a minimal
   `BindableEvent`/`BindableFunction` (S) into MVP4 or MVP13 so SC-a's fix line can name it.
4. **Q4 — Format v2 cannot be read by older builds.** A downgrade cannot open new saves (refused
   explicitly). *Default:* accept; mention it in the release notes.
5. **Q5 — `api_version` stays `MVP2`.** *Default:* keep; MVP18 defines the policy and the reader's
   accepted set.
6. **Q6 — Restricted authors' client scripts on the host's process (SC-14).** *Default:* they act as
   their author; MVP11 decides whether they run on other players' clients.
7. **Q7 — Who connects the solo player at the screen.** Not found in production code. *Default:* the
   solo composition connects the local host's player at boot (P1 needs it), and client scripts wait
   for it.
8. **Q8 — Client writes to server-owned instances in Solo still reach the one registry.** Solo-only
   coupling remains until MVP6's write authority. *Default:* no MVP4 enforcement; a `Strict` world's
   resolver may refuse them early.
9. **Q9 — Size.** The steps sum to about 30–35 agent-days, more than the rung's "M". *Default:* keep
   the scope and relabel the rung L, or cut C5 (client composition) to the start of MVP5; the DoD does
   not need C5.
10. **Q10 — Side checks that are stricter than Roblox (DEV candidate SC-b).** Input, camera and render
    signals from a server script, and server containers from a client script, raise instead of being
    silent or empty. *Default:* raise — the error teaches the split before host mode exists.
11. **Q11 — DEV-1 versus the mirror.** The guide now documents an error for cyclic requires
    (`scripting/module.md:25`), the class page a hang (`classes/ModuleScript.yaml:41-45`). *Default:*
    `CYCLIC_REQUIRE` carrying Roblox's guide wording, and DEV-1 re-filed as "matches the guide, differs
    from the class page".
12. **Q12 — Moving a running script into a non-running container stops it (SC-9).** UNCERTAIN in the
    rulebook. *Default:* stop, with a test, and a recorded stance to verify in Studio.
13. **Q13 — The default world forgets custom placement on restart.** Like every world-owned instance
    there (`WORLD_PACKAGE.md:313-314`). *Default:* accept and document; a header `parent:` survives.
14. **Q14 — Dependents restart with the module's reload mode.** A clean module reload rebuilds every
    dependent's startup objects. *Default:* follow the Hub toggle / `keep_objects`; report every
    restarted dependent.
15. **Q15 — `Source`/`RunContext` writes at the elevated tier (DEV candidate SC-d).** *Default:* console
    only, inside the gate.
16. **Q16 — Client scripts are not copied into `PlayerScripts` (DEV candidate SC-e).** Only relevant
    to imported scripts in the `StarterPlayer` folders. *Default (accepted):* a `Client` script there
    runs in place once per client, which Roblox also does for the original; a `LocalScript` template
    does not run until MVP14 copies it with the importer.
17. **Q17 — The 256-source ceiling.** Modules count. *Default:* unchanged in MVP4; P20 measures.
18. **Q18 — Engine behaviour not yet proven.** `InstanceTreeSerializer.Restore` on a replica (A3 tests
    it first), and error attribution from `Prototype.ChunkName` across a module thread's frames (C4 tests
    it first). *Default:* each step starts with the pinning test; a failure reopens SC-16 or SC-19
    before more code.
19. **Q19 — Concurrent edits.** Seven of the files this rung changes have uncommitted edits by other
    agents (list at the top). *Default:* run A1–A3 now; start B and C only after those edits land, and
    re-check every line number cited here.

---

## 8. Edits the implementation owes other documents (not made by this note)

- `Docs/CoreAIMods/ROBLOX_API_ROADMAP.md`: MVP4 "Done so far"; the DEV table (SC-a…SC-e with real
  ids, DEV-1's restatement); §3.2 legacy column; §6.3 survival rows (SC-18); D1's row pointing here.
- `Docs/CoreAIMods/RobloxReference/01_SCRIPTS_AND_SCHEDULER.md`: the R1.12 stance as a U-item.
- `Docs/CoreAIMods/mod-system.md` §1 (`context:`, `parent:`), §2 (`Context`), a new section on contexts,
  placement, `require` and run states.
- `Docs/CoreAIMods/WORLD_PACKAGE.md`: version 2, the `script` block, the new refusals and diagnostics,
  the migration, the join payload.
- The Rbx API skill text (both copies) and `TODO.md` (close the design-note item; add Q3, Q7, Q9).
