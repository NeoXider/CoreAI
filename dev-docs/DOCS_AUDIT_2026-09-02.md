# Documentation audit — 2026-09-02

Scope: every markdown under `Docs/`, `Assets/CoreAI/Docs/`, `Assets/CoreAiUnity/Docs/`,
`Assets/CoreAIMods/**`, `Assets/CoreAIHub/**`, `Assets/CoreAI.Demos/**`, the root `README*.md`, and the
agent skill texts in `Assets/CoreAiUnity/Resources/AgentSkills/`. Baseline: branch
`feature/mvp2-multiplayer`, HEAD `e3320bb0` plus the uncommitted working tree.

Method: read-only git; every claim below was checked against runtime C# before it was edited. No
tests, no Unity, no Editor run. `Docs/CoreAIMods/WORLD_PACKAGE.md`,
`Assets/CoreAIMods/Runtime/RbxApi/Unity/PROCEDURAL_MATERIALS.md`, and
`Assets/CoreAI.Demos/ProceduralMaterials/README.md` were read as sources of truth but never edited
(other owners); needed changes to them are listed in the last section.

## 1. Facts established from code

| Fact | Evidence |
|---|---|
| `Enum.Material` has exactly 45 items | `Assets/CoreAIMods/Runtime/RbxApi/Datatypes/RbxEnum.cs:120-134` |
| All 45 have a runtime render mapping | `RbxProceduralMaterialProvider.cs` defines 45 catalog entries |
| Six are CC0 texture-backed | `RbxTextureMaterialProvider.cs`: Wood, WoodPlanks, Brick, Cobblestone, Metal, Grass |
| Invalid/unmapped id renders magenta | `RbxProceduralMaterialProvider.FallbackMaterial` — "Opaque magenta/black diagnostic material returned for an invalid or unmapped id"; `TryGetMaterial` returns it on a name/value mismatch |
| `Part.Color` is an independent tint | `PartProperties.ColorWasExplicitlySet` + `ResolveRenderTint(bool)`; the tint rides a `MaterialPropertyBlock` |
| `BasePart.Material` is a real read/write Lua property | `LuaCsRbxInstanceBindings.cs:1485,1548` (write requires WorldEdit; only `Enum.Material` items accepted) |
| `BasePart.Orientation` (YXZ) and `Rotation` (XYZ) are read/write and position-preserving | `LuaCsRbxInstanceBindings.cs:1497,1504,1569,1581` |
| `CornerWedge` has a real mesh | `InstanceGameObjectBinder.BuildCornerWedgeVisual` / `GetCornerWedgeMesh` |
| Absent-child `WaitForChild` yields, warns at 5 s, honours a timeout | `LuaCsRbxSchedulerAdapter.cs:103` (`task._waitForChildBridge`), reached from `LuaCsRbxInstanceBindings.cs:698` |
| `Model`/`PVInstance` pivot slice landed | `PivotTo`/`GetPivot`/`PrimaryPart`/`WorldPivot` in `LuaCsRbxInstanceBindings.cs` and `RbxInstance.cs` |
| Creatable classes | `ClassCatalog.cs:341-400`: `Part`, `Folder`, `Model`, `ClickDetector`, `RemoteEvent`, `UnreliableRemoteEvent`, `RemoteFunction`. `Camera` is registered **non**-creatable (one canonical camera per world) |
| `HttpService` and `Players` are tree-backed, not stubs | `ServiceCatalog.cs:172-173` `RegisterTreeBacked`; `DataModelBootstrap.cs:29,32`; `LuaCsRbxHttpServiceAdapter.cs` |
| RunService topology queries and `Workspace:GetServerTimeNow` are still loud stubs | `ClassCatalog.cs:467-473` `PlannedMethod(..., "MVP2", ...)` |
| `RemoteFunction` invoke is bounded to 30 scheduler seconds | `LuaCsRbxApiBindings.cs:1673` — "response timed out after 30 seconds" |
| `save_world` / `load_world` exist and are Programmer-only | `RbxWorldPackageContracts.cs:361,416`; `CoreAiModsInstaller.cs:522-531` |
| Package versions | `coreai` 7.1.1, `coreaiunity` 7.1.1, `coreaimods`/`coreaihub`/`coreaibenchmark`/`coreaimcp` 7.1.0; tags `v7.1.0` and `v7.1.1` exist |

## 2. Stale claims found and fixed

| File:line (pre-edit) | Old text | New text |
|---|---|---|
| `Assets/CoreAiUnity/Resources/AgentSkills/RbxApi.txt:123` (+ mirror `BuiltInRbxApiSkillText.cs:147`) | "`Material`, `Orientation`, `Rotation` are NOT implemented yet and raise NOT_IMPLEMENTED loudly — use CFrame for rotation until then." | Full read/write documentation of `Material`, `Orientation` (YXZ) and `Rotation` (XYZ), plus the 45-item catalog, the magenta fallback, and the independent `Part.Color` tint. |
| `RbxApi.txt:122` (+ mirror) | "`.CornerWedge` is accepted but currently draws as a Block until its mesh lands" | "`.Ball`, `.Block`, `.Cylinder`, `.Wedge`, `.CornerWedge` — every one of them materializes its real mesh." |
| `RbxApi.txt:193-194` (+ mirror) | Not-implemented list contained "`WaitForChild(name)` when the child is absent" and "Part `Material`, `Orientation`, `Rotation` properties" | Both removed; replaced with a positive statement that `Shape`/`Material`/`Orientation`/`Rotation` and absent-child `WaitForChild` all work. |
| `RbxApi.txt:72-73` (+ mirror) | "creatable classes are \"Part\", \"Folder\", \"Model\", \"ClickDetector\"" | Adds `RemoteEvent`, `UnreliableRemoteEvent`, `RemoteFunction`; states `Camera` is not creatable; adds the 30-second `RemoteFunction` bound. |
| `RbxApi.txt:79-88` (+ mirror) | Tree-backed list omitted `Players`/`HttpService`; catalog list called them "`HttpService` (MVP2), `Players` (MVP8)" | Both listed as tree-backed live services; catalog list reordered accordingly. |
| `Assets/CoreAI/Docs/RBX_API.md:40` | "`Instance.new` accepts: `Part`, `Folder`, `Model`, `Camera`, `ClickDetector`." | Correct list with the three remote classes; explicit note that `Camera` is not creatable. |
| `Assets/CoreAI/Docs/RBX_API.md:49-53` | Part-property list with no `Material`/`Orientation`/`Rotation`; "materializes … with the URP Lit material" | Full property list plus a new "`BasePart.Material` and `Part.Color`" section (45 items, six textured, magenta fallback, tint semantics, links to both catalog docs). |
| `Assets/CoreAI/Docs/RBX_API.md` / `RbxApi.txt` (absent) | Neither said that `Neon` behaves differently from every other material under `Part.Color` | Both now state that `Neon` has no palette of its own — its emission *is* `Part.Color` (`RbxProceduralMaterialProvider.cs:33`: white `_MaterialColor`, `_PartColorInfluence` 1). |
| `Assets/CoreAI/Docs/RBX_API.md` (absent) | No world save/load documentation at all | New "Saving and loading a world" section: `.world` container, create-once `save_world`, confirmation-only `load_world`, Hub World Loads page, `ConfirmedWorldMutationGate` autosave triggers, session replacement, WebGL `FS.syncfs` durability and browser budgets, pointer to `WORLD_PACKAGE.md`. |
| `Docs/ROADMAP.md:8-9` | "Last updated: 2026-08-30. Latest RELEASED lockstep version: **7.0.7**; **7.1.0 is prepared in the working tree and not yet committed or released**" | 2026-09-02; tags `v7.1.0`/`v7.1.1` released; per-package manifest versions stated; work since v7.1.1 named as unreleased. |
| `Docs/ROADMAP.md:55` | "Last released together at 7.0.7; the working tree carries the prepared, uncommitted 7.1.0" | "Six UPM packages, released in lockstep (see the version note above)". |
| `Docs/ROADMAP.md:97-98` | "Continue MVP2 (clocks, **materials catalog**, shared JSON contract, loopback remotes), then the ladder through MVP17 (**world files**, RBXL, …)" | Remaining MVP2 items only (clocks, shared JSON contract, Tier-A corpus gate); world files removed from "next"; MVP3 pointed at Track C. |
| `Docs/ROADMAP.md:95` | Track A current state stopped at the deferred-signal path | Adds the live MVP2 scheduler (R4.2 pipeline, nine R5.5 drains), loopback remotes, the complete 45-item material catalog with fallback, `Orientation`/`Rotation`, and the real `CornerWedge` mesh. |
| `Docs/ROADMAP.md:138-139` | "The unified place package, backup tiers, and RBXL are **not yet built**." | "MVP3 has landed" with the shipped component list; acceptance runs named as owned outside the document; only RBXL called unbuilt. |
| `Docs/ROADMAP.md:141` | "Next milestones. **MVP3** (place package + two-tier backups), MVP4 …" | MVP3 removed; MVP4/MVP9 plus the world-selection/autoload tail. |
| `Docs/ROADMAP.md:246` (Track G) | Track G said only "WebGL persistence syncs through `CoreAiWebGlPersistence`" | Adds the `FS.syncfs`-callback-only completion contract with its cancellation/30 s timeout, and the documented browser local-model limitation. |
| `Docs/ROADMAP.md:246` (release plan) | "**7.0.7 (latest released, 2026-08-27)**" | 7.1.1 and 7.1.0 entries added above it; 7.0.7 demoted to a dated historical row. |
| `Docs/CoreAIMods/ROBLOX_API_ROADMAP.md:491-492` | "Clocks, JSON/`HttpService`, loopback remotes, absent-child `WaitForChild` yield, the Tier-A corpus, and the **materials catalog** remain." | Those five listed as landed; only clocks, RunService topology queries, `GetServerTimeNow`, and the Tier-A corpus gate remain. |
| `ROBLOX_API_ROADMAP.md:404` | "\| MVP3 \| World file (place package) + two-tier backups \| MVP1, MVP2 \| M \|" | Row annotated "*(implemented; acceptance runs owned outside this doc)*". |
| `ROBLOX_API_ROADMAP.md:504` | MVP3 section had no status line | Section retitled "*(implemented)*" and given a Current-state paragraph listing the shipped components and the open autoload tail. |
| `ROBLOX_API_ROADMAP.md:1097` | "absent-child `WaitForChild` → MVP2" in the planned column | Moved to shipped, with the 5 s warning and timeout overload named. |
| `ROBLOX_API_ROADMAP.md:1099-1100,1102` | `PivotTo`/`GetPivot`/`PrimaryPart`/`WorldPivot` → "**MVP2 (Model pivot)**" | Moved to the shipped column on `PVInstance`, `Model` and `Workspace`. |
| `ROBLOX_API_ROADMAP.md:1101` | "`Material` → **MVP2 (materials catalog)**" | `Material` moved into the shipped member list with the 45-item and fallback facts. |
| `ROBLOX_API_ROADMAP.md:1226,1229,1234` | Loud-stub inventory rows marked `planned` for pivot, `BasePart.Material`, absent-child `WaitForChild` | All three changed to `shipped` with what landed. |
| `ROBLOX_API_ROADMAP.md:1297-1298` | Task 12 framed as future; task 13 said "(today neither wired nor stubbed)" | Both marked **landed** with the real provider/type names; a note above the table records which numbered tasks have landed and that the file paths are the original plan, not the shipped layout. |
| `ROBLOX_API_ROADMAP.md:1661` | MVP3 tool row described `save_world`/`load_world` as future work | Marked **shipped**, with the create-once and confirmation-request semantics. |
| `ROBLOX_API_ROADMAP.md:8-14` | Intro status stopped at MVP0/MVP1 | Adds "MVP2 has largely landed" and "MVP3 is implemented", pointing at `WORLD_PACKAGE.md`. |
| `Docs/CoreAIMods/RBX_API_SKILL.md:35-37` | "bindings … live in `…/LuaCs/LuaCsRoblox*`"; "enabled through `LuaCsModStackOptions.RobloxApi`" | `LuaCsRbx*` and `LuaCsModStackOptions.RbxApi` (verified in `LuaCsModRuntimeFactory.cs:26`). |
| `Docs/CoreAIMods/mod-authoring.md:85-87,97-98` | Tree-backed list omitted `Players`/`HttpService`; placeholder table listed `HttpService` MVP2 and `Players` MVP8 | Both moved into the tree-backed list and removed from the placeholder table. |
| `Docs/CoreAI/AGENT_ROLES_AND_TOOLS.md:28-30,56,70-71` | Installer description and tool table had no `get_mod_logs`, `save_world`, `load_world`; no mention of the pre-mutation autosave gate | Rows added for both world tools; the `execute_lua` and `manage_mods` rows now state the `ConfirmedWorldMutationGate` behaviour and which `manage_mods` actions bypass it. |
| `Assets/CoreAIHub/README.md:84,89` | Page table and prose listed only Mods + World state | New `World Loads` page row (id, order 250, confirm/reject contract, never receives package bytes) and prose updated. |
| `Assets/CoreAiUnity/Docs/WORLD_COMMANDS.md:202` | §7 described `WorldStateManager` persistence with no distinction from the Rbx `.world` package | Callout added distinguishing the two formats and linking `WORLD_PACKAGE.md` + `RBX_API.md`. |
| `Assets/CoreAI.Demos/DEMO_INVENTORY.md:48` | "Runtime Roblox-style material catalog, including opaque, neon, transparent, and textured shader paths" | States all 45 items, the six texture-backed ones, and the explicit invalid-id magenta fallback. |
| `README.md:62` | "Where this is going: a Roblox-like Lua API …, shareable world packages" (framed as future) | Marks the Rbx API and `.world` packages as already shipping, keeping multiplayer co-creation and host embedding as future. |

## 3. Discoverability changes (no stale claim, missing links)

| File | Change |
|---|---|
| `Docs/README.md` | New "Roblox-style mod API (Rbx)" section linking `RBX_API.md`, `WORLD_PACKAGE.md`, `PROCEDURAL_MATERIALS.md`, `TEXTURE_MATERIALS.md`, `RBX_API_SKILL.md`, `ROBLOX_API_ROADMAP.md`. |
| `Assets/CoreAI/Docs/README.md` | RBX_API row description updated; new "Related documents outside this package" table for the world package and both material catalogs. |
| `Assets/CoreAiUnity/Docs/DOCS_INDEX.md` | RBX_API row description updated; `WORLD_PACKAGE` and the two material catalogs added to the Lua/portable table. |
| `Assets/CoreAI/Docs/RBX_API.md` | "Related" list extended with `WORLD_PACKAGE.md` and both material catalogs. |
| `Docs/CoreAIMods/RBX_API_SKILL.md` | New "What the skill text currently claims" section so the skill and the runtime stay checkable side by side. |
| `README.md` | Documentation map gained `RBX_API.md` and `WORLD_PACKAGE.md` rows. |

All new relative links were resolved on disk before being written into the text.

## 4. Skill-text mirror

`Assets/CoreAiUnity/Resources/AgentSkills/RbxApi.txt` and the `Instructions` literal in
`Assets/CoreAI/Runtime/Core/Features/AgentPrompts/BuiltInRbxApiSkillText.cs` were verified
byte-identical before editing, edited once in the `.txt`, and the C# literal was regenerated from the
`.txt` (escaping `"` as `""`). Identity was re-verified by round-tripping the literal back out and
diffing — clean. The pinning test
`LuaModdingSkillEditModeTests.RbxApiInstructions_CoverTheApiFamiliesThePromptSummarizes` requires the
substrings `NOT_IMPLEMENTED`, `1 stud = 0.28 m`, `LookVector is -Z` and the datatype/property names;
all of them survive the edit. `BuiltInRbxApiSkillText.cs` is the only `.cs` file touched by this audit.

## 5. Claims that could NOT be verified — left unchanged, flagged

| File | Claim | Why it was not verified |
|---|---|---|
| `Assets/CoreAiUnity/Docs/DGF_SPEC.md:209-232,429` | "the **reference implementation** in the repository is **NGO**"; "**Networking:** NGO (§5.1)" | Directly contradicts `Docs/ROADMAP.md` Track B and `ROBLOX_API_ROADMAP.md` §2, which lock Mirror via NeoxiderTools `Neo.Network`. Neither is checkable against code: CoreAI still has no multiplayer implementation, only `INetworkBridge`/`NullNetworkBridge`. **Needs an owner decision**, not a doc edit. |
| `Assets/CoreAiUnity/Resources/AgentSkills/RbxApi.txt:64-65` | "Registered enum types: Material, PartType, CameraType, NormalId, Axis, RotationOrder, KeyCode, UserInputType, UserInputState, MouseBehavior" | `SignalBehavior` is constructed inline in `LuaCsRbxInstanceBindings.cs:1216` for the `Workspace.SignalBehavior` read, not via `RbxEnumRegistry.CreateWithBuiltins`. Whether `Enum.SignalBehavior` resolves from Lua could not be settled by reading alone. Left as-is. |
| `Docs/BENCHMARK_LEADERBOARD.md:101-118` | Model scores and pass rates | Benchmark result data; nothing in the repository can confirm or refute it without running the suite. |
| `Assets/CoreAI.Demos/DEMO_INVENTORY.md:79-82` | "IMGUI is the dominant overlay tech across demos"; per-demo UI classification | Depends on the `ImguiBanRatchetEditModeTests` whitelist and on scene contents; not enumerated. The `ModdableUnits` "authored but not yet wired" claim WAS verified — `TODO(moddableunits-binding-seam)` is still live in `ModdableUnitsDemoController.cs:99` — so it is correct, not stale. |
| `Assets/CoreAiUnity/Docs/DEVELOPER_GUIDE.md:817` | "**Version of this guide:** 7.0.3 (2026-08-12)" | A guide-revision stamp rather than a package-version claim. Its technical content still matches the code; only the owner can say whether the stamp should track releases. |
| `Assets/CoreAiUnity/Docs/DGF_SPEC.md:50,429` | "Current architecture note (7.0.1)" / "CoreAI 7.0.1:" | Same class of stamp. The six-package statement it introduces is still true, so the body was left alone. |
| `Docs/CoreAIMods/ROBLOX_API_ROADMAP.md:1308-1310` (file paths) | `RobloxApi/Marshalling/RobloxJson.cs`, `RobloxApi/Services/HttpServiceImpl.cs`, `RobloxApi/Networking/…` | These planned paths do not exist; the shipped code lives under `RbxApi/`. Rather than rewrite every planned path in a plan-of-record table, a note was added above the table saying the paths are the original plan and the shipped layout uses `RbxApi`. |
| `Docs/CoreAIMods/mod-system.md`, `Docs/ARCHITECTURE_RULES.md` | Loud-stub policy statements, Hub/IMGUI migration notes | Policy and architecture statements, not factual status claims; nothing contradicted the code. |
| All docs in scope | EditMode suite totals (3188 / 3179 / 0 / 9) | No document in scope asserts a test count, so nothing needed changing; the totals themselves were not re-measured (no Unity runs in this pass). |

## 6. Changes needed in files this audit must not edit

| File | Needed change | Severity |
|---|---|---|
| `Docs/CoreAIMods/WORLD_PACKAGE.md` | None found. Every statement checked (format, limits, two-phase autosave durability, `ConfirmedWorldMutationGate` trigger names and the read-only `manage_mods` bypass list, `save_world` create-once, `load_world` confirmation-only, Hub World Loads page, WebGL `FS.syncfs` completion and browser budgets) matches the code. It is being edited concurrently by its owner; the user-facing summaries added to `RBX_API.md` were derived from it and should be re-checked if its contract changes. | none |
| `Assets/CoreAIMods/Runtime/RbxApi/Unity/PROCEDURAL_MATERIALS.md` | None. Its owner landed an edit during this pass that now states "The catalog maps all 45 public `Enum.Material` items, grouped by shader family" and documents the Neon exception (`_MaterialColor` white, `_PartColorInfluence` 1, so Neon emission *is* `Part.Color`). Both were verified against `RbxProceduralMaterialProvider.cs:33` and adopted into `RBX_API.md` and the Rbx API skill text. | none |
| `Assets/CoreAI.Demos/ProceduralMaterials/README.md` | None. "The public Lua enum contains 45 items and every item has a runtime render mapping", the six-textured split, and the `CoreAiRbxMaterial_FALLBACK_UNMAPPED` reservation all match the code exactly. | none |

## 7. Process notes

- One violation of the read-only-git rule occurred: `git checkout -- Docs/CoreAIMods/mod-authoring.md`
  was run to undo a botched in-place `sed` splice. The resulting diff was inspected afterwards and
  contains only this audit's intended edit, so no concurrent work was lost — but the correct recovery
  is a file backup, which is what the remainder of the pass used.
- The worktree is shared with other agents. Material counts, provider contents, tool names, and the
  `RemoteFunction` timeout string were re-verified against the working tree after those agents' edits
  landed; all facts in section 1 still held.
