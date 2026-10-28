# Live scene storage isolation

The first focused Unity run produced six Passed and one Failed because the assertion compared
Windows slash styles. After canonicalizing both paths, the rerun passed all seven cases. This
proves the manager's selected-path behavior; the live-scene composition still needs its own run.

The live published-demo fixture previously built the real `WorldStateManager` with its default
snapshot path. `WorldStateEntryPoint` calls `Initialize`, which restores that file and starts a
60-second auto-save loop. The final live log contains repeated default-file restoration and an
empty-world save. This is a confirmed isolation failure; the earlier live run is not a safe
storage-isolation receipt.

## Repair

- `WorldStateManager` retains its public three-argument constructor, including optional defaults.
  A public four-argument overload selects an absolute snapshot file before initialization. All
  restore, save, auto-save and reset paths use that selected file. `SaveFilePath` exposes the
  selected path for hosts and verification. Empty, relative, traversal and directory paths fail
  during construction, before any file is opened.
- The live-fixture owner integrates an exact-slot replacement using
  `Path.Combine(_worldStoreRoot, "world_state.json")`. The existing registration wrapper preserves
  implementation type, interfaces, lifetime and key. It must replace only an existing manager,
  retain `WorldStateEntryPoint`, and assert the resolved `SaveFilePath` equals the owned path.
- `WorldStateManagerIsolationEditModeTests` covers the original default location without reading
  it, explicit-path initialization and unresolved-object restoration, saving, reset and invalid
  paths. It creates and removes only its GUID-owned temporary directory. Run this exact class;
  do not use a broad `WorldStateManager` test-name filter yet.

## Store audit and remaining gates

| Store | Source observation | Required isolation |
| --- | --- | --- |
| World state | Active through `WorldCommandsInstaller` and `WorldStateEntryPoint` | New explicit-path overload; verify fixture integration and execute focused regression |
| Audit | `CoreServicesInstaller.RegisterCore` always calls `RegisterAuditLog`; audit interceptors resolve the writer in a build callback | Replace exact `CoreAI.Features.Audit.AuditLogWriter` slot with `new AuditLogWriter(Path.Combine(_worldStoreRoot, "Audit"), new AuditLogWriterOptions())` before build; the public seam already exists |
| Skills | `FileSkillStore` defaults to `CoreAI/Skills`, but no construction or registration was found in production `Assets` during this narrow audit | Optional exact-slot replacement with `new FileSkillStore(Path.Combine(_worldStoreRoot, "Skills"), resolver.Resolve<ILog>())`; do not claim the store is active without registration evidence |
| Agent memory and summaries | Existing fixture replaces backing slots when persistence mode is persistent | Retain existing GUID-owned paths and exact-slot checks |
| Lua versions, data overlays, token calibration, endpoint registry | Existing fixture supplies owned paths | Retain existing isolation and build checks |
| Mod state, mod sources and world packages | Existing fixture replaces real backing stores, including namespaced `FileRbxWorldPackageStore` | Retain existing paths and teardown after scene unload |
| Game configuration | `UnityGameConfigStore` stores references to real `ScriptableObject` assets; `TrySave` mutates them and marks them dirty in Editor | No file write in this store, but asset mutation remains an independent gate; reject unreviewed mutation or snapshot/clone registered assets before exposing mutating tools |

The earlier `WorldStateManagerResetPersistenceEditModeTests` backed up and deleted the global
snapshot. It now creates a GUID-owned temporary directory and uses the explicit path constructor;
the migrated reset fixture has not yet been rerun.

## Recovery inventory and verification status

Read-only inventory of
`C:/Users/User/AppData/LocalLow/DefaultCompany/CoreAI/CoreAI/WorldState` found only
`world_state.json` (127 bytes, creation date 2026-07-09). No sibling backup or temporary file was
listed. File contents were not printed or modified. No broader historical recovery source was
searched; recovery provenance and any restore remain the root owner's responsibility.

No Unity process, test run, application configuration, scene or user-state write was performed by
this worker. Source and focused regression are prepared; runtime isolation remains unverified
until the root owner reviews the integrated helper and runs the exact regression and live gate.

## Verification on 2026-10-28

- Focused EditMode selection (world-state, audit-writer, orchestrator file-history, installer and
  English-prose guard classes): 86/86 Passed, including the migrated
  `WorldStateManagerResetPersistenceEditModeTests` (2/2) and `WorldStateManagerIsolationEditModeTests` (7/7).
- Non-live PlayMode: `ChatHistoryPlayModeTests` and `PublishedDemoStorageRegistrationPlayModeTests`, 2/2 Passed.
- A SHA-256 listing of `persistentDataPath/CoreAI` taken before and after both runs was identical.
- The hermetic PlayMode gate (391 cases, 389 Passed / 2 Skipped) is not isolated: scene and scope fixtures
  wrote the default world snapshot, audit log, a Lua mod state file and eight seeded sample mods. Those
  files were restored from a copy taken before the run; the open design item is in `TODO.md`.
- The live-scene composition itself still runs only inside the explicit OpenRouter fixture.

## Single storage root - 2026-10-28

The per-store isolation above could not keep tests out of the developer's saves: the 2026-10-28 full
EditMode run deleted five world autosave packages (the default `FileRbxWorldPackageStore` pruned past its
backup capacity), and the hermetic PlayMode gate overwrote `world_state.json`, appended to the audit log and
seeded sample mods, because sixteen runtime files computed their default from `Application.persistentDataPath`
directly. The design decision is a configurable root honoured by every default store.

- **Provider.** `CoreAI.Infrastructure.CoreAiStorageRoot` (`Assets/CoreAiUnity/Runtime/Source/Infrastructure/`)
  is the only code that reads the engine data path. `PersistentDataRoot` is the innermost active
  `Override(absoluteDirectory)` scope, otherwise `Application.persistentDataPath`; `Combine(...)` builds
  `CoreAI/<segments>` under it and rejects rooted and traversal segments. Scopes nest and can be disposed in any
  order. Stores resolve the root once, in their constructor.
- **Stores routed through it.** `WorldStateManager`, `AuditLogWriter`, `FileAgentMemoryStore`,
  `FileSkillStore`, `FileTokenCalibrationStore`, `FileLlmEndpointRegistryStore`, `FileLuaScriptVersionStore`,
  `FileDataOverlayVersionStore`, the `CoreAILifetimeScope` token-calibration and conversation-summary
  registrations, `FileLuaModStore`, `FileLuaModSourceStore`, `FileRbxWorldPackageStore`, `LuaLogFileSink`, and
  the editor's Delete All Persistent Saves menu and Agent Session Inspector. Explicit constructor paths still
  win.
- **Test redirect.** `CoreAiTestStorageRootRunCallback` (`CoreAI.Tests.PlayMode.Shared`, a Unity Test
  Framework `TestRunCallback`) creates `%TEMP%/cai-ts-<10 hex>` at the start of every EditMode and PlayMode run,
  overrides the root, re-applies it after a mid-run domain reload from the `COREAI_STORAGE_TEST_RUN_ROOT`
  process variable, and deletes the directory when the run finishes; stale run directories older than twelve
  hours are removed at the next run. The root is never longer than the engine default, so Windows path
  budgets behave as in production. Scene tests and the hermetic gate scenes are covered because the override is
  in place before the first test loads anything.
- **Guards.** `CoreAiStorageRootEditModeTests.Sources_ReadTheEngineDataPathOnlyThroughTheStorageRoot` scans all
  CoreAI packages, demos and the example game, code only (comments and string literals stripped,
  interpolation holes kept), and allows exactly two files: the provider and the run callback.
  `DefaultStores_BuiltByATest_LandUnderTheRedirectedRoot`, `ModsStorageRootEditModeTests` and
  `CoreAiStorageRootPlayModeTests` build each default store and assert its path under the redirected root.
- **Red check.** With one store temporarily reverted to the engine path, the focused run
  (`artifacts/storage-root-20261028/edit-red1.xml`) failed exactly the store regression and the guard
  (13 = 11 Passed / 2 Failed), naming the reverted line; the revert was then restored.

| Run | Receipt | Result | Owner folder before/after |
| --- | --- | --- | --- |
| Focused EditMode | `edit-focused1.xml` | 80 = 80 Passed | identical (678 files) except Unity's own `TestResults.xml` |
| Red check | `edit-red1.xml` | 13 = 11 Passed / 2 Failed (expected) | identical except `TestResults.xml` |
| Full EditMode | `edit-full1.xml`, same assemblies and filter as `unity-completion-edit-final2` | 8,304 = 8,289 Passed / 0 Failed / 15 Skipped, exit 0 | identical except `TestResults.xml` |
| Full EditMode, final tree | `edit-full2.xml`, same arguments | 8,307 = 8,292 Passed / 0 Failed / 15 Skipped, exit 0 | identical except `TestResults.xml` |
| Hermetic PlayMode | `play-hermetic3.xml`, same assemblies, filter and categories as the 7.54.0/7.55.0 gate | 395 = 391 Passed / 0 Failed / 4 Skipped, exit 0 | identical except `TestResults.xml` |
| Earlier PlayMode attempts | `play-hermetic1` (compile error in a new test), `play-hermetic2` (Mirror stopped Play Mode on the MiniRpg scene before the test armed the identity patch) | no results file | identical |

The four PlayMode skips are the two batch-mode `WaitForEndOfFrame` cases (as before) and the two new explicit
tests. During the hermetic gate the scene fixtures wrote `world_state.json`, the audit log, Lua script
versions and seeded mods; all of it landed in the per-run temporary root. One run directory survived with only a
`world_state.json`: a quit-time save written after the run finished, which is the same path that overwrote the
developer's snapshot on 2026-10-28 and now lands in temp. The aborted `play-hermetic2` never reached
`RunFinished`, so its directory also remains; both are removed by the twelve-hour stale sweep at a later run.

Receipts are under `artifacts/storage-root-20261028/` with each run's process arguments and owner-folder
diff. The owner folder is `C:/Users/User/AppData/LocalLow/DefaultCompany/CoreAI`; listings are SHA-256 per
file plus the directory set, compared by a read-only script.
