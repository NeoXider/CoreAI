# Mod runtime, Lua sandbox, world packages and runtime version stores

Audited snapshot: `69d5bcc37502cd4bb464a3bf9ac0c53b1624b804` (`HEAD`, `main` and `origin/main`, verified locally without fetch). Audit date: 2026-10-23. Model: gpt-6.1-sol; reasoning effort: max.

## Audit record

Completed a targeted static audit, with substantive findings saved incrementally before the final review. Eleven new findings: two P1 and nine P2 after root adjudication of MOD-003; no P0 or P3 finding was confirmed. The explicit audit request authorizes this Markdown report despite the repository's usual prohibition on audit reports. Only this report was writable; all source, tests, configuration and other reports were read-only. No delegation, git mutation, Unity/MCP operation, build, test run, install, service operation or model benchmark was performed.

The goal is to check lifecycle isolation, sandbox and capability boundaries, budgets, package persistence and runtime version stores in built players, including WebGL. Correct cancellation, identity, ordering, bounded resources, recovery and replaceable host seams are relevant. Source inspection is not runtime verification.

Initial observations:

- `git rev-parse HEAD` returned the supplied SHA. `git status --short` showed the unrelated preexisting `Assets/Epic Toon FX/Upgrade/ETFX 2019.4.24f1 URP Upgrade.unitypackage.meta` and `dev-docs/audits/` as untracked. The ETFX file was not changed.
- Read `AGENTS.md`, `Docs/ARCHITECTURE_RULES.md` and `Assets/CoreAIMods/README.md`; inventoried the scoped runtime, editor, configuration and test files. No deeper `AGENTS.md` was returned by the scoped search.
- Read the runtime/Editor asmdefs and package manifest. The runtime references the bundled Lua VM and the separate Rbx and Luau assemblies; the Editor assembly is restricted to Editor. Vendor binary internals are excluded.
- The security guide explicitly distinguishes host-granted FullAccess from the gameplay sandbox. FullAccess's intentional reflection surface was not reported as an escape. It also documents synchronous actor-scoped execution, per-resume rather than lifetime budgets, process-wide heap measurements and unmeasured device-specific stack behavior; these were not promoted to new defects from documentation alone.
- `TODO.md` already records sandbox/library limitations, scheduler trip impersonation, reload-wrapper compatibility, synchronous Lua version-store reads and incomplete WebGL codec/JSON materialization bounds. The findings distinguish these existing items from new defects after tracing their call paths and tests.

## Coverage and exclusions

Requested scope: `Assets/CoreAIMods/Runtime/` except `RbxApi/`, `LuauDownlevel/`, `HubIntegration/`; `Assets/CoreAIMods/Editor/`; package/asmdef/plugin load configuration; integrated core sandbox/Lua execution/permissions/runtime-versioning contracts; host Lua version stores and related tests/docs. This is targeted call-path review plus broader source screening, not a claim that every line in the requested scope was audited. The inventory contained 109 runtime C# files and 15 Editor C# files after directory exclusions. The requested core `Runtime/Core/Sandbox/` and `Runtime/Core/Features/LuaExecution/` directories contain no C# files at this snapshot; their live implementations are in the mods package.

Primary source review (ranges identify the inspected call paths, not coverage percentages):

| Exact repository-relative file | Reviewed scope |
|---|---|
| `Assets/CoreAIMods/Runtime/LuaExecution/LuaCsModRuntime.cs` | Constants/state/constructor; public actor gates; ownership lookup; first-load admission/build/rollback; unload/reload/swap; startup-object cleanup call sites; revision/revert; tick/timer/event dispatch; quarantine; shutdown; cross-mod exports; subscriptions/queues; import/rehydrate/persist; error/report buffering. Principal ranges: 171-298,477-589,786-1034,1230-1297,1384-1414,1761-2100,2295-2530,3052-3161,3199-3678,4008-4206,4408-4711,4715-4943,5014-5084,5208-5371,5496-5527,5623-5705. |
| `Assets/CoreAIMods/Runtime/LuaExecution/ILuaModRuntime.cs` | Full interface, including the default reload-mode forwarding member. |
| `Assets/CoreAIMods/Runtime/Infrastructure/LuaCsModRuntimeFactory.cs` | Options, capability intersection, engine/runtime wiring, one-shot composition and default teardown; 75-120,175-245,330-543. |
| `Assets/CoreAIMods/Runtime/Infrastructure/LuaCsGameplayBindings.cs` | Registration/owner-state/capability/transaction paths; 1-281,305-330. |
| `Assets/CoreAIMods/Runtime/Infrastructure/LuaCsVersioningRuntimeBindings.cs` | Full file. |
| `Assets/CoreAIMods/Runtime/Infrastructure/IDataOverlayPayloadValidator.cs` | Full file. |
| `Assets/CoreAIMods/Runtime/LuaExecution/LuaCapabilities.cs` | Full file. |
| `Assets/CoreAIMods/Runtime/LuaExecution/LuaModsLlmTool.cs` | Constructor/description, dispatch/authorization/cancellation, load/reload/import/export; 94-115,120-171,190-321,375-465. |
| `Assets/CoreAIMods/Runtime/LuaExecution/LuaTool.cs` | Full file, including actor routing, rate checks and cancellation conversion. |
| `Assets/CoreAIMods/Runtime/LuaExecution/LuauSourceGate.cs` | Full file; only the gate into report 07's rewriter. |
| `Assets/CoreAIMods/Runtime/LuaExecution/LuaGenerationRateLimiter.cs` | Full file. |
| `Assets/CoreAIMods/Runtime/LuaExecution/LuaCsLogicSlots.cs` | Method map plus invocation/reset/failure scope at 647-698; lifecycle integration traced in runtime. |
| `Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsScriptEngine.cs` | State/guard/coroutine creation and chunk execution, 18-201. |
| `Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsScriptState.cs` | Full file; disposal is deliberately GC-managed. |
| `Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsSecureEnvironment.cs` | State hardening; raw resume budgets/tokens; one-shot entry; stripped globals/library caps; counted callback/fence path; 205-226,400-546,725-825,1315-1435, plus method/callback screening. Matcher/formatter internals were not exhaustively re-audited. |
| `Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsExecutionGuard.cs` | Nested-run registration/allowances/ancestor charging/trips; guard execute/install/restore; 157-480,637-722,827-905, plus method map. No vendor internals were opened. |
| `Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsAllocationBudget.cs` | Per-execution/nested ceilings and confirmation sampling, 58-103,134-199. |
| `Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsCoroutineHandle.cs` | Resume, nested limits and kill path, 291-365,440-464, plus cancellation/method screening. |
| `Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsApiRegistry.cs` | Callback overloads, varargs conversion, typed reflection invocation and error wrapping; 78-139,230-257, plus registration map. |
| `Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsValueMarshaller.cs` | Host conversion/fallback, portable conversion entry and numeric parser portions; 135-203,490-585, method-use search. Not a full datatype conformance audit. |
| `Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsRbxSchedulerAdapter.cs` | Owner/state capture, runner ownership, resume/kill and coroutine creation; 292-335,425-541,666-760,836-940. |
| `Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsRbxApiBindings.cs` | Integration-only excerpts: disposal 1646-1707, main chunks/mutation identity 1929-1971, owner-state helper 2026-2040, task registration 3954-3984, callable capture 4980-5003. Other Rbx API semantics remain report 06's boundary. |
| `Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsRbxHttpServiceAdapter.cs` | All request lifecycle, decorator and completion paths, 14-55,68-265,337-498; request argument helper method map. |
| `Assets/CoreAIMods/Runtime/Scripting/LuaCs/RbxHttpPolicy.cs` | Request/response and host seams, URL/header/IP rules, allowlist and limiter; 1-96,121-344,383-450,478-574. |
| `Assets/CoreAIMods/Runtime/Infrastructure/FileLuaModSourceStore.cs` | Normal CRUD/admission, exact staging/verification/activation/rollback, path mapping and root gate; 1-253,255-449,452-675,677-868. |
| `Assets/CoreAIMods/Runtime/Infrastructure/FileLuaModStore.cs` | Full file: scoped KV caps, migration, sync/refusal handling, atomics and disposal. |
| `Assets/CoreAIMods/Runtime/Infrastructure/LuaModStoreId.cs` | Full file. |
| `Assets/CoreAIMods/Runtime/Infrastructure/FileRbxWorldPackageStore.cs` | Chunk I/O, constructor/locking, manual/autosave/startup writes, quota admission, crash-temp cleanup, two-phase write recovery, reader/encoder and rotation; 209-290,398-545,711-789,829-861,903-939,1067-1273,1407-1468. Remaining startup metadata/name helpers were screened by method map, not fully audited. |
| `Assets/CoreAIMods/Runtime/Infrastructure/RbxWorldPackageSerializer.cs` | Format limits, capture source/index handling, ZIP/JSON encoder/decoder and index validation; 20-66,678-777,845-921,1376-1400,1669-1910. World-property projection/semantic validation was screened, not exhaustively checked against report 06's domain. |
| `Assets/CoreAIMods/Runtime/Infrastructure/RbxWorldPackageContracts.cs` | Controller load gate/staging/publish/recovery/disposal, frame pump and outgoing cleanup; 1997-2104,2156-2450,3441-3505, source-admission/kept-source call-site search. Other stable facade/buffering and startup-refresh internals were not fully audited. |
| `Assets/CoreAIMods/Runtime/Infrastructure/LuaCsGameToolExecutor.cs` | Shared mutation gate, one-shot actor/envelope/plain execution, cancellation and result handling; 80-233,526-654,681-836, with broader method/catch screening. |
| `Assets/CoreAIMods/Runtime/Logging/LuaLogService.cs` | Full file. |
| `Assets/CoreAIMods/Runtime/Logging/LuaLogEntry.cs` | Full file. |
| `Assets/CoreAIMods/Runtime/Logging/LuaLogFileSink.cs` | Full file; optional sink only, never attached/executed. |
| `Assets/CoreAIMods/Runtime/Composition/CoreAiModsInstaller.cs` | File-store/gate/grant registrations, frame ticker disposal, startup restore and tool registration excerpts; 94-169,268-293,398-438,440-466,504-546,638-695, plus factory/actor wrapper method searches. |
| `Assets/CoreAIMods/Runtime/Composition/CoreAiModsLifetimeScope.cs` | Serialized grants/providers/budget and installer call, 1-152. |
| `Assets/CoreAIMods/Runtime/Infrastructure/LuaModRuntimeTickDriver.cs` | Full file. |
| `Assets/CoreAIMods/Runtime/Infrastructure/WorldRestoreGate.cs` | Full file. |
| `Assets/CoreAIMods/Runtime/Infrastructure/ResourcesBundledModSource.cs` | Full file. |
| `Assets/CoreAIMods/Runtime/Infrastructure/BundledModSeeder.cs` | Seeding/version update/save policy, especially 135-203; constructor/API/version/hash portions also read. |
| `Assets/CoreAIMods/Runtime/LuaExecution/LuaModAutoRepairPolicy.cs` | State/admission/reload policy through 135 and method screening. |
| `Assets/CoreAIMods/Runtime/Presentation/CoreAiLuaModAutoRepair.cs` | Lifetime/subscription/scheduling/catches, 35-84,130-215. |
| `Assets/CoreAIMods/Runtime/Infrastructure/IFullLuaAccessBlacklistPolicy.cs` | Full file; type/member guard integration in `LuaCsFullUnityRuntimeBindings.cs:911-959` plus reflection call-site screening. |
| `Assets/CoreAIMods/Runtime/LuaAssets/LuaSourceCap.cs` | Full file; display cap, not an execution-source limit. |
| `Assets/CoreAI/Runtime/Core/Features/RuntimeVersioning/MemoryDataOverlayVersionStore.cs` | Apply/seed/reset/history/import/export paths, 1-400. |
| `Assets/CoreAI/Runtime/Core/Features/RuntimeVersioning/MemoryLuaScriptVersionStore.cs` | Snapshot/record/seed/reset/import/export paths; 30-198,201-284,315-399. |
| `Assets/CoreAI/Runtime/Core/Features/RuntimeVersioning/VersionRetentionPolicy.cs` | Full file; deliberate original/current retention even over the byte budget. |
| `Assets/CoreAI/Runtime/Core/Features/RuntimeVersioning/ILuaScriptVersionStore.cs` | Sync/async interface and inline adapter signatures. |
| `Assets/CoreAI/Runtime/Core/Features/RuntimeVersioning/IDataOverlayVersionStore.cs` | Full file. |
| `Assets/CoreAI/Runtime/Core/Features/RuntimeVersioning/LuaScriptVersionRecord.cs` and `DataOverlayVersionRecord.cs` in the same directory | Record field/constructor contracts screened. |
| `Assets/CoreAI/Runtime/Core/Features/Permissions/AiPermissionsOptions.cs` | Full file; role permissions are separate from Lua capability tiers. |
| `Assets/CoreAiUnity/Runtime/Source/Features/Lua/Infrastructure/FileLuaScriptVersionStore.cs` | Full file: shared canonical-path state, lazy reads, staged writes, pending confirmation, WebGL/desktop work split, JSON bounds and atomics. |
| `Assets/CoreAiUnity/Runtime/Source/Features/Lua/Infrastructure/FileDataOverlayVersionStore.cs` | Full file. |
| `Assets/CoreAiUnity/Runtime/Source/Composition/CoreAILifetimeScope.cs` | Relevant host store registrations, 375-400. |

Existing tests reviewed by source (none executed):

- `Assets/CoreAIMods/Tests/EditMode/LuaCsModRuntimeEditModeTests.cs`: budget/factory helper; capability tests 1970-2035; ordering/global budget 1640-1705; nested exports 1849-1905; handler budget/quarantine 2110-2145,2593-2642; reload teardown 4406-4459; trip tests 4474-4516; related test-name searches for identity, failed loads and callbacks.
- `Assets/CoreAIMods/Tests/EditMode/LuaCsModRuntimePersistenceEditModeTests.cs`: import/grant/persistence test inventory, especially tests around 551-681; tests do not validate the ordinary source filename collision.
- `Assets/CoreAIMods/Tests/EditMode/ScriptEngineSeamEditModeTests.cs`: guard/coroutine behavior 200-345 and seam test map.
- `Assets/CoreAIMods/Tests/EditMode/FileLuaModSourceStoreEditModeTests.cs`: CRUD/namespace cases 1-180 and exact replacement collision case 301-343; admission/false-sync test-name screening.
- `Assets/CoreAIMods/Tests/EditMode/FileLuaModStoreEditModeTests.cs`: CRUD/disposal/isolation/case-migration test inventory and assertions.
- `Assets/CoreAIMods/Tests/EditMode/LuaModsLlmToolEditModeTests.cs`: construction/dispatch helper 1-104 and cancellation/ownership/test inventory.
- `Assets/CoreAIMods/Tests/EditMode/LuaToolEditModeTests.cs`: cancellation 103-136 plus rate-limit/argument test inventory.
- `Assets/CoreAIMods/Tests/EditMode/LuaLogServiceEditModeTests.cs`: ring counts 86-121, clear 223-248 and concurrency 264-304; test-name screening for the remainder.
- `Assets/CoreAIMods/Tests/EditMode/DemoModProductionSurfaceEditModeTests.cs`: HTTP fakes 178-251 and outbound safety/rate cases 475-657, with related test names. No demo scene/model was run.
- `Assets/CoreAIMods/Tests/EditMode/RbxApi/Acceptance/HttpServiceOneOffRefusalEditModeTests.cs`: full file, integration tests only despite its Rbx test directory.
- `Assets/CoreAIMods/Tests/EditMode/RbxApi/LuaBindings/RbxJsonContractEditModeTests.cs`: contract description/setup 1-80 and JSON contract test inventory; no claim of a full codec audit.
- `Assets/CoreAIMods/Tests/EditMode/RbxApi/Acceptance/Mvp3WorldPackageFollowUpEditModeTests.cs`: manual quota/cancellation tests 1820-1860, startup-write assertions 2800-2848 and world persistence/recovery test inventory. `Mvp3WorldPackageEditModeTests.cs` and `Mvp3WorldPackageQaEditModeTests.cs` in the same directory were searched for ZIP/format/recovery checks, not read in full.
- `Assets/CoreAiUnity/Tests/EditMode/DataOverlayVersionStoreEditModeTests.cs`: full file. `Assets/CoreAiUnity/Tests/EditMode/LuaScriptVersionStoreEditModeTests.cs`: test inventory, retention/cancellation/atomic/confirmation coverage and synchronous durability-refusal test around 573-609.

Additional source screening only: API/limit/loop/exception/I/O sites in `Assets/CoreAIMods/Runtime/WorldBindings/LuaCsWorldRuntimeBindings.cs` (also read 289-340,440-500), `LuaCsComponentRuntimeBindings.cs` and `LuaCsWorldQueryBindings.cs`; `Assets/CoreAIMods/Runtime/LuaExecution/LuaModHeader.cs` (builder 211-285); `LuaCsAiEnvelopeProcessor.cs` (envelope/run/repair/transaction paths); `Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsCoroutineRunner.cs` (cancellation/disposal sites). The Editor screen covered `LuaTextAssetEditor.cs`, `LuaSyntaxPalette.cs`, `LuaSyntaxHighlightView.cs`, `LuaScriptViewerWindow.cs`, `LuauScriptedImporter.cs`, `AssemblyInfo.cs`, and the nine files in `Editor/RbxMaterials/`: `RbxCc0TextureSets.cs`, `RbxCavityMapBake.cs`, `RbxAmbientCgCatalogDownloader.cs`, `RbxLocalTextureCatalogImport.cs`, `RbxMaterialCatalogEditorUtility.cs`, `RbxMaterialSurfaceProfileRetuner.cs`, `RbxPackagedTextureCatalogBuild.cs`, `RbxMegascansCatalogImporter.cs`, `RbxMaterialSurfaceProfiles.cs`. The downloader's ZIP extraction at 295-322 was inspected directly. These are secondary Editor conveniences, not the runtime package import path. G10 files (`G10MeasurementRunner.cs`, `G10MeasurementModels.cs`, `G10MeasurementComposition.cs`) received an entry/method inventory only; no performance gate was evaluated.

Documentation/configuration read or consulted in relevant sections: `AGENTS.md`; `Docs/ARCHITECTURE_RULES.md`; `Assets/CoreAIMods/README.md`; `Assets/CoreAI/README.md:1-75`; `Assets/CoreAiUnity/README.md:1-110`; `Assets/CoreAI/Docs/LUA_SANDBOX_SECURITY.md`; `LUA_ACCESS_MODES.md` and `LUA_GAME_API.md` in the same directory; `Docs/CoreAIMods/mod-system.md`, `mod-authoring.md`, `MOD_SHARING.md`, `WORLD_PACKAGE.md`, `SCRIPT_ENGINE_SEAM.md`; `Assets/CoreAIMods/Runtime/Logging/README.md`; relevant `TODO.md` items cited below. Configuration: `Assets/CoreAIMods/package.json`, `Runtime/CoreAI.Mods.asmdef`, `Editor/CoreAI.Mods.Editor.asmdef`, `Tests/CoreAI.Mods.Tests.asmdef`, `Plugins/Lua.dll.meta`, `Plugins/Lua.Annotations.dll.meta` (paths relative to `Assets/CoreAIMods/`); `Assets/CoreAiUnity/Runtime/Source/CoreAI.Source.asmdef`; `Assets/CoreAiUnity/link.xml:1-55`; `Packages/manifest.json` Lua/module references; `tools/portable/LuaTests/README.md` and `CoreAI.Portable.LuaTests.csproj`. No dependency installation/resolution was attempted.

Excluded: report 06's Rbx domain/Unity implementation, report 07's Luau implementation, report 08's Hub implementation, vendor binary internals, runtime/model benchmarks and all source changes. Cross-module call sites may be inspected only to establish ownership and call chains.

## Confirmed actionable findings

### MOD-001 — P1 — Read-only scripts can mutate every version store and publish data overlays

**Status:** New; no matching open item found in `TODO.md`. **Confidence:** High (complete registration-to-store/sink path inspected; not executed).

**Evidence:** `Assets/CoreAIMods/Runtime/LuaExecution/LuaCapabilities.cs:15` defines Read as having no game side effects. `Assets/CoreAIMods/Runtime/Infrastructure/LuaCsGameplayBindings.cs:208-213` registers the entire versioning surface whenever Read is effective. `Assets/CoreAIMods/Runtime/Infrastructure/LuaCsVersioningRuntimeBindings.cs:53-73` registers global Lua-history resets; `:92-108` records and publishes an overlay; `:120-160` exposes overlay seeding/overwrite and global resets. No actor, owning mod or separate write grant reaches this binding class. `:176-183` labels the command as Programmer output. `Assets/CoreAI/Docs/LUA_ACCESS_MODES.md:17` explicitly promises no side effects for Read.

**Trigger and impact:** Load an untrusted mod with only `LuaCapabilities.Read` in a normal stack backed by shared version stores. `coreai_data_apply('shared', '{}')` updates the shared overlay store and publishes `DataOverlayApplied`; `coreai_lua_reset_all()` or `coreai_data_reset_all()` rewinds other scripts' histories, and `coreai_data_seed_overwrite` replaces a baseline. These calls succeed without WorldEdit/LogicOverride/Full and do not pass through actor ownership checks. Hosts cannot use Read to isolate observers from persistent shared-state changes. The normal mod-local `store_set` surface is deliberately available on every tier and is not this finding.

**Observed guards/tests:** Host/script capability intersection is present (`LuaCsGameplayBindings.cs:158-160,204`), but it only determines entry into the Read block. The default overlay validator checks outer JSON delimiters (`Infrastructure/IDataOverlayPayloadValidator.cs:14-29`); it has no authorization context. `Assets/CoreAIMods/Tests/EditMode/LuaCsModRuntimeEditModeTests.cs:1992-2010` checks only absent `coreai_world_destroy/spawn` and never attempts the versioning mutators.

**Fix direction:** Split read-only version inspection from mutation; gate mutation behind an explicit configurable write grant and bind it to an authorized actor/key scope. Keep global reset/overwrite operations host-only. Update the access-mode table with the actual grants.

**Regression recommendation:** Through `LuaCsModRuntimeFactory.Create`, inject two populated memory version stores and a recording command sink. Run a Read-only mod that tries apply, seed-overwrite, per-key and global resets against another mod's keys. Assert a stable refusal, unchanged payloads and histories, and zero commands. A separately authorized writer should still work; a scoped writer must not reset another actor's state.

### MOD-002 — P1 — Overlay persistence failure is reported as a successful apply and poisons retry deduplication

**Status:** New failure-reporting/durability issue. The separate cross-instance lost-update problem is already tracked at `TODO.md:3231-3234` and is not duplicated here. **Confidence:** High (complete store/caller/test path inspected; not executed).

**Evidence:** `Assets/CoreAiUnity/Runtime/Source/Features/Lua/Infrastructure/FileDataOverlayVersionStore.cs:64-69` changes `_memory` before saving. `:214-221` ignores the boolean from `CoreAiWebGlPersistence.Sync()` and catches/logs every persistence exception without returning failure. The binding then unconditionally publishes the apply command (`Assets/CoreAIMods/Runtime/Infrastructure/LuaCsVersioningRuntimeBindings.cs:106-107`). The default host registers this file store at `Assets/CoreAiUnity/Runtime/Source/Composition/CoreAILifetimeScope.cs:390-391`. The memory store returns no-change for the same payload (`Assets/CoreAI/Runtime/Core/Features/RuntimeVersioning/MemoryDataOverlayVersionStore.cs:97-100`).

**Trigger and impact:** A write fails before the atomic replacement (disk full, permission error or the existing failure-injection seam), or the WebGL page has not armed automatic persistence (`Sync() == false`). The Lua apply still completes and the sink receives success-path output. After an I/O failure, the live store exposes the new payload but reopening returns the old one. Retrying the identical payload skips saving because memory already contains it, so recovery of the filesystem does not repair the lost update. A later restart silently loses the accepted edit. WebGL refusal is never surfaced, contrary to the explicit persistence contract.

**Observed guards/tests:** Temporary-file plus replace protects the previous file against a failed replacement; it does not make the staged memory transaction successful. `Assets/CoreAiUnity/Tests/EditMode/DataOverlayVersionStoreEditModeTests.cs:186-209` already injects this failure and asserts the reopened old payload, but neither asserts a failure to the caller nor checks live memory, command publication or an identical retry. Lua's file version store has an explicit visible-refusal test (`LuaScriptVersionStoreEditModeTests.cs:592`); the overlay store lacks the equivalent behavior.

**Fix direction:** Persist a staged snapshot before publishing it, propagate a typed persistence failure, and require a true WebGL persistence-arm result. If local commit has already happened, expose an explicit unconfirmed state rather than silently treating it as durable. Do not publish `DataOverlayApplied` on a failed store operation; identical retries must complete pending persistence rather than be discarded as no-ops.

**Regression recommendation:** Extend the existing atomic-write test through `coreai_data_apply` with a recording sink: inject failure, assert failure and no publish, then remove injection and retry the identical payload, reopen, and verify it is present. Add a persistence-refusal seam for `false`, with the same assertions and no browser waits.

### MOD-003 — P2 — No-Rbx startup lacks a configurable main-chunk budget

**Status:** New; not the existing runtime-ceiling plumbing item (`TODO.md:904-906`). **Confidence:** High (complete factory-to-VM path inspected; not executed).

**Evidence:** `Assets/CoreAIMods/Runtime/Infrastructure/LuaCsModRuntimeFactory.cs:390-406` passes configured handler limits to the runtime; its public field documentation explicitly applies those limits to main-chunk resumes only with `RbxApi` set (`:175-187`). `Assets/CoreAIMods/Runtime/LuaExecution/LuaCsModRuntime.cs:869-871,2021-2028` leaves the no-Rbx main chunk on the engine's default guard. `LuaCsScriptEngine.cs:147-150,176-187` and `LuaCsSecureEnvironment.cs:739-741` select fixed one-shot defaults (10 s, 50,000,000 steps, 256 MiB). No factory option independently configures that startup guard.

**Trigger and impact:** An embedded host omits `RbxApi` and needs a startup budget below the fixed one-shot defaults. Changing `HandlerMaxSteps` does not configure this path, consistently with its documentation; obtaining an independently configurable main-chunk budget requires another engine seam or package changes. This is a configuration/composability limitation, not a violation of a promised no-Rbx handler limit or an unlimited-execution finding. Root reduced the original P1 claim after independent review; see report 15.

**Observed guards/tests:** A default guard exists and matches the documented no-Rbx behavior. `Assets/CoreAIMods/Tests/EditMode/LuaCsModRuntimeEditModeTests.cs:1854-1870,2110-2143` tightens handler budgets; those tests do not establish a separate configurable startup-budget seam.

**Fix direction:** Add an explicit configurable no-Rbx startup-budget/guard seam, document its defaults independently of handler and Rbx resume budgets, and pass the selected guard to the engine. Preserve existing documented behavior for hosts that do not opt in.

**Regression recommendation:** For the proposed startup option, use deterministic finite-loop first-load/reload cases with no Rbx API and a 1,000-step cap. Assert a typed trip, no installation after failed first load, and retention of the previous live mod after failed reload. A recording engine should observe the selected explicit guard; unchanged defaults and handler-only options must retain their documented semantics. Use small bounded allocations.

### MOD-004 — P2 — A replaced mod can continue dispatching stale timers and queued handlers in the same tick

**Status:** New residual lifecycle defect; the fixed stale-snapshot quarantine check is distinct (`TODO.md:2765`, existing test below). **Confidence:** High (single-thread reentrant call chain inspected; not executed).

**Evidence:** `Assets/CoreAIMods/Runtime/LuaExecution/LuaCsModRuntime.cs:3219-3239` captures `Mod` objects for the tick. Its liveness checks use only `mod.Removed` (`:3245,3268,4012,4046,4071`). An unload sets this flag (`:2335`), but `SwapInReplacementLocked` replaces the dictionary/list entry without marking the outgoing object removed (`:2508-2523`). Deactivating subscriptions (`:4920-4937`) does not clear its timers or pending events. `InvokeGuarded` synchronously raises `ModHandlerErrored` (`:4151-4155`), permitting a reload before the current dispatch loop resumes.

**Trigger and impact:** Give a mod a first timer/handler that fails and a second timer/handler that writes a marker or mutates the world. Reload it synchronously from `ModHandlerErrored`, as the existing fixture already does. The replacement is installed and its predecessor's effects are torn down, then the old tick object continues into the second timer/handler because `Removed` remains false. Queued events on other replaced tick objects can run too. Old code can therefore change the newly rebuilt world or reinstall a logic override after successful reload. No parallel threads are needed. `ShutdownWithoutPersistence` likewise never marks its outgoing tick objects removed (`:3622-3640`), so a shutdown reached from a host callback has the same stale-dispatch gap.

**Observed guards/tests:** `LuaCs_Quarantine_ReloadLandsMidTick_FreshInstanceIsNotQuarantined` (`Assets/CoreAIMods/Tests/EditMode/LuaCsModRuntimeEditModeTests.cs:2604-2637`) proves this callback/reload entry is supported, but only checks the fresh instance's quarantine state and a later tick. `Factory_Reload_StopsTheReplacedRunsThreadsHandlersAndTweens_WithoutHostWiring` (`:4409-4447`) reloads between frames and covers scheduler registrations, not the current legacy tick snapshot.

**Fix direction:** Retire the outgoing generation under `_gate` when swapping/shutting down and enforce that retirement before every further dispatch. A generation/liveness check should apply to timer and already-snapshotted handler batches, while allowing the current invocation to unwind safely.

**Regression recommendation:** Extend the mid-tick reload fixture with a second due timer, a second handler for the same event, and an already queued event. Trigger reload from the first handler's error callback and assert none of the old markers appears, while the replacement runs on the next tick. Add the analogous scope-disposal callback case.

### MOD-005 — P2 — World-package mutation isolation is per object although the directories are shared

**Status:** New; distinct from the overlay-store cross-instance TODO. **Confidence:** High (static interleaving; no concurrent test run).

**Evidence:** `Assets/CoreAIMods/Runtime/Infrastructure/FileRbxWorldPackageStore.cs:411` creates an instance-local semaphore. The constructor explicitly shares manual/autosave directories across startup namespaces (`:413-416,456-466`). `CreateManualAsync` checks slot count/bytes (`:737-769`) before awaiting the temporary-file write (`:782,1102-1105`); that write yields between chunks (`:229-249`). A second store has an independent gate and can pass the same quota check. Autosave rotation/rollback and startup sequence allocation use the same per-object protection (`:909,1128-1139,1407-1450,498-505`). Constructor crash-temp cleanup (`:473,845-849`) also has no shared ownership check.

**Trigger and impact:** Construct two stores for the same root (including different `startupNamespace` values), set `maximumManualSlots: 1`, and start different-slot saves whose encoded packages require a chunk yield. Both can count zero slots before either installs its final file, then both return success with two slots. Byte quotas can be exceeded in the same way. Interleaved autosave rotation/recovery has no directory-wide transaction boundary either; its exact crash/recovery outcomes require a focused test. This is a single-player/WebGL async interleaving as well as a possible desktop concurrency problem.

**Observed guards/tests:** Create-new renames prevent overwriting the same final filename, but do not serialize two different slots or ring transactions. `SaveWorldTool_ManualSlotLimitReached_IsAnOrdinaryFailedResultAndWritesNothing` (`Assets/CoreAIMods/Tests/EditMode/RbxApi/Acceptance/Mvp3WorldPackageFollowUpEditModeTests.cs:1823-1842`) saves sequentially through one store, so it misses the shared-root case.

**Fix direction:** Share an async transaction coordinator by canonical directory/root and filesystem identity (or require one injected coordinator shared by every store for those directories). Protect quota admission, install, persistence confirmation, rollback and temp cleanup together. Startup-only namespacing must not accidentally split the manual/autosave lock.

**Regression recommendation:** Use the existing in-memory filesystem seam and a controlled first-write yield. Start two same-root stores with different startup namespaces and a one-slot cap; release the writes and assert exactly one accepted save and one final slot. Add two-store autosave tests with one failed confirmation and verify the exact durable ring after reopening; do not use timing sleeps.

### MOD-006 — P2 — Outbound mod HTTP requests outlive cancellation, reload and world disposal

**Status:** New. Owned here because the defect is in `Runtime/Scripting/LuaCs`, outside report 06's excluded `Runtime/RbxApi` implementation. **Confidence:** High (complete request and teardown paths inspected; no network request executed).

**Evidence:** `Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsRbxHttpServiceAdapter.cs:262` starts an untracked task; `:392-399,417-425` passes `CancellationToken.None` to both host resolver and transport. The adapter subscribes to scheduler phases (`:98`) but implements no disposal or per-mod teardown. Completions enter an unbounded queue (`:81,446-470`). Mod teardown only kills scheduler work/connections/instances (`Assets/CoreAIMods/Runtime/Infrastructure/LuaCsModRuntimeFactory.cs:457-487`); killing the waiting thread (`Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsRbxSchedulerAdapter.cs:836-845`) has no link to the request task. World shutdown calls runtime shutdown and Rbx binding disposal (`Assets/CoreAIMods/Runtime/Infrastructure/RbxWorldPackageContracts.cs:3441-3465`); the binding's dispose path (`Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsRbxApiBindings.cs:1646-1707`) also has no HTTP cancellation/detach. The fixed-window limiter (`Assets/CoreAIMods/Runtime/Scripting/LuaCs/RbxHttpPolicy.cs:544-570`) limits admission rate, not outstanding requests.

**Trigger and impact:** A host explicitly installs an allowlist, DNS resolver and transport. A mod starts an approved request while DNS or transport remains pending, then is unloaded/reloaded or its world is replaced. The request receives no cancellation and can still send after teardown. An outstanding request retained by the resolver/transport also retains its old adapter/world/request state; repeated requests over successive rate windows accumulate outstanding work. After the old scheduler stops being pumped, late completions can remain queued without a consumer. Generation-specific response signals correctly avoid delivering a late result to a different wait, but do not release or stop the external work.

**Observed guards/tests:** Default policy/resolver/transport refuse outbound access; this finding requires the documented host opt-in and is not a default-network escape. URL/header/IP/redirect checks are present. `Assets/CoreAIMods/Tests/EditMode/DemoModProductionSurfaceEditModeTests.cs:197-222` uses immediately completed fake resolver/transport tasks; its rate-limit case (`:599-639`) does not test cancellation or retiring a request. `HttpServiceOneOffRefusalEditModeTests.cs:29-70` covers synchronous refusal only.

**Fix direction:** Track requests by owning mod generation and world lifetime, pass linked cancellation to both seams, cancel on retirement, ignore terminal late results, and detach the scheduler subscriber on disposal. Add configurable outstanding-request/completion bounds; a rate limit alone is not backpressure. Keep cancellation asynchronous on WebGL.

**Regression recommendation:** Use deferred fake resolver/transport tasks and record their tokens. Start a request, unload/reload/dispose before completion, and assert cancellation plus no later transport dispatch/result retention. Advance an injected clock across rate windows while the resolver remains parked and assert a fixed outstanding-work bound. No real DNS or HTTP is necessary.

### MOD-007 — P2 — Lua log retention has no bound on the number of mod ids

**Status:** New; not the unrelated file audit-log retention TODO (`TODO.md:3228`). **Confidence:** High (all storage/clear paths inspected; not executed).

**Evidence:** `Assets/CoreAIMods/Runtime/Logging/LuaLogService.cs:29,90-91,199-207` creates a new per-mod ring for every distinct id with no eviction. Each ring allocates the default 512-entry array (`:23,220-223`). `Clear()` empties rings without removing their dictionary entries (`:181-195`). `Assets/CoreAIMods/Runtime/LuaExecution/LuaCsModRuntime.cs:2092` logs failed first loads before they ever occupy a live/persisted mod slot, and unloading does not retire their log buffer. Messages themselves are accepted without a length/byte cap (`LuaLogService.cs:82-91`). The README promises bounded memory for long sessions (`Assets/CoreAIMods/Runtime/Logging/README.md`, Invariants).

**Trigger and impact:** Repeatedly attempt a failed load under a new mod id. Every attempt permanently adds a 512-reference array plus its retained log text even after the global 4,096-entry ring rolls over. Live-mod/source limits do not constrain failed-load ids. A small number of very large messages can also retain much more memory than the ring-count limits suggest; per-run allocation budgets do not impose a session log-byte budget. Even a full log clear leaves the per-id arrays allocated. Long-running creator/server sessions and memory-constrained players can exhaust memory from diagnostic history.

**Observed guards/tests:** `LuaLogServiceEditModeTests.cs:86-118` checks entry-count limits for one or two fixed ids, `:237-246` checks cleared query results only, and `:264-301` uses eight fixed producer ids. None bounds retained id count or bytes. Keeping some unloaded-mod logs is useful; permanent retention of all ids is not required for that feature.

**Fix direction:** Introduce configurable global retained-id and byte budgets with LRU/age eviction; retire empty buffers on clear and preserve a bounded global diagnostic tail. Clamp messages before buffering, while indicating truncation. Avoid deleting all useful failure history on unload as a substitute for a real budget.

**Regression recommendation:** Append or fail-load many unique ids with tiny capacities; assert the configured retained-id/byte bound and eviction order. Verify `Clear()` releases per-id buffers, retained messages stay queryable within policy, and concurrent append/query/eviction preserves sequence ordering.

### MOD-008 — P2 — Default mod-source and store-namespace filenames are not collision-safe

**Status:** New residual defect; exact world replacement already has a different, safe mapping. **Confidence:** High (call paths plus a small pure string probe; no filesystem mutation).

**Evidence:** `Assets/CoreAIMods/Runtime/Infrastructure/FileLuaModSourceStore.cs:52` selects `useCaseSafeFolderNames: false` for ordinary stores. `:559-564,574-588` uses sanitized text plus an FNV suffix only when the id contains invalid filename characters; a valid id is used unchanged. `Assets/CoreAIMods/Runtime/Infrastructure/LuaModStoreId.cs:38-59` has the same structural collision for composition namespaces. The in-memory probe produced exactly `a/b -> a_b_3a8e75c1` and `a_b_3a8e75c1 -> a_b_3a8e75c1`. Ordinary source `TryLoad` does not compare the requested id to the manifest id (`FileLuaModSourceStore.cs:175-190`).

**Trigger and impact:** Save/load distinct mods named `a/b` and `a_b_3a8e75c1` for the same authorized actor in an ordinary source store. Both runtime ids are distinct, but persistence writes the same manifest/source pair, so one replaces the other; unloading, forgetting or changing active state through one id affects the shared persisted package. A later world capture lists only the surviving manifest (`RbxWorldPackageSerializer.cs:854,874-916`) and cannot recover the overwritten mod. The same pair used as two `storeId` values aliases supposedly isolated source/KV/startup namespaces. Case-distinct simple ids also alias on case-insensitive filesystems. This is a persistence/isolation defect, not an arbitrary-path escape.

**Observed guards/tests:** The source root-prefix check (`Assets/CoreAIMods/Runtime/Infrastructure/FileLuaModSourceStore.cs:554-568`) prevents traversal but not collisions. `ExactReplacement_CaseAndLegacySanitizerCollisionsRoundTripExactly` (`Assets/CoreAIMods/Tests/EditMode/FileLuaModSourceStoreEditModeTests.cs:301-340`) already uses precisely this pair, but only on `prepared.SourceStore`, whose constructor enables the SHA-256 mapping (`Assets/CoreAIMods/Runtime/Infrastructure/FileLuaModSourceStore.cs:443-448,591-607`). The ordinary-store tests cover round trips and different simple namespaces, not colliding ids.

**Fix direction:** Use a fixed SHA-256-based mapping for all ids, including already-valid ids, with an explicit legacy migration that verifies manifest identity. Apply it to composition namespaces as well; do not merely append another suffix to invalid ids.

**Regression recommendation:** Repeat the existing collision-pair test against a directly constructed ordinary `FileLuaModSourceStore`, then reopen, deactivate and delete each mod independently. Repeat it with two `storeId` values for source and KV stores and two startup namespaces. Run the case-distinct-id variant on the supported case-insensitive filesystem too.

### MOD-009 — P2 — A cancelled manage_mods request still mutates when the optional backup gate is absent

**Status:** New. **Confidence:** High (full dispatch path inspected; not executed).

**Evidence:** `Assets/CoreAIMods/Runtime/LuaExecution/LuaModsLlmTool.cs:214-255` observes `cancellationToken` only by passing it to `_worldMutationGate.ExecuteAsync`. `TryGetConfirmedBackupTrigger` returns false when no gate is composed (`:286-288`); the fallback immediately calls `ExecuteAuthorizedAction`, which has no token (`:291-317`). `Load` calls the synchronous runtime and reports success (`:396-404`); no cancellation check occurs before it. The constructor permits a missing backup gate, and the existing test helper uses exactly that composition (`Assets/CoreAIMods/Tests/EditMode/LuaModsLlmToolEditModeTests.cs:52-65`).

**Trigger and impact:** Compose the supported standalone mod tool without world-package backups, cancel the supplied token, then call `load`, `reload`, `import`, `forget` or another mutating action. The operation still executes and may persist changes after the host cancelled the turn. The default world-session gate checks cancellation, so the defect is specific to leaving that optional block out. Separately, broad catches in this tool (`:258-261`) and the one-shot executor (`Infrastructure/LuaCsGameToolExecutor.cs:703-705,737-739`) turn cancellation into ordinary failure results; this conflicts with the normative cancellation rule, but the existing one-shot test explicitly allows that result and it is not the primary defect here.

**Observed guards/tests:** Authorization and the shared authorization lock still apply. `LuaToolEditModeTests.cs:103-132` checks token forwarding for the different one-shot tool and accepts either a cancelled task or failure JSON. The inspected `LuaModsLlmToolEditModeTests` contains no cancellation case.

**Fix direction:** Check cancellation before authorization-side effects and immediately before standalone dispatch, independent of backup composition; preserve `OperationCanceledException` separately from ordinary failure. A future asynchronous runtime load seam should propagate cancellation into execution, without claiming the current synchronous API can preempt arbitrary host callbacks.

**Regression recommendation:** Parameterize every mutating action with an already-cancelled token, both with and without a gate. Assert no runtime/store/sink calls and a cancellation outcome. Use a controlled queued call to cancel before dispatch; do not only assert that the token was received by an unrelated fake executor.

### MOD-010 — P2 — A valid empty overlay baseline cannot be restored

**Status:** New. **Confidence:** High (store, binding validator and reset tests inspected; not executed).

**Evidence:** `Assets/CoreAIMods/Runtime/Infrastructure/IDataOverlayPayloadValidator.cs:17-20` explicitly accepts an empty payload. `Assets/CoreAI/Runtime/Core/Features/RuntimeVersioning/MemoryDataOverlayVersionStore.cs:82-94` records it as a real original revision, but `:171-173` refuses `ResetToOriginalChanged` when `OriginalPayload` is empty. `:140-146` also treats an empty original as uninitialized and lets `SeedOriginal(..., overwriteExistingOriginal: false)` replace it and clear history. The file store delegates to these methods (`Assets/CoreAiUnity/Runtime/Source/Features/Lua/Infrastructure/FileDataOverlayVersionStore.cs:72-84`), and Lua exposes them through `coreai_data_reset`/`coreai_data_seed`.

**Trigger and impact:** Apply `""`, then `"{}"`, then reset the key (or reset all). The current payload remains `"{}"` instead of returning to the recorded empty baseline. A later non-overwriting seed can destroy that baseline and history. Empty is a supported overlay value, so it must not double as the missing-baseline sentinel. The Lua memory version store has the same empty-source sentinel (`MemoryLuaScriptVersionStore.cs:156,187`), although normal mod loading rejects blank source; no broader mod-load impact is claimed.

**Observed guards/tests:** A slot/history already distinguishes a recorded baseline from a missing key. `Assets/CoreAiUnity/Tests/EditMode/DataOverlayVersionStoreEditModeTests.cs:15-37,106-150` covers only nonempty originals and therefore misses this case.

**Fix direction:** Track baseline existence independently of string contents (or use the initialized slot/history), permitting empty original values while preserving the explicit overwrite flag.

**Regression recommendation:** Test empty-first apply followed by a nonempty edit, per-key reset and reset-all; assert an empty current value and a preserved original revision. A non-overwriting seed must leave the baseline/history unchanged. Repeat after a file-store reopen.

### MOD-011 — P2 — Due timers can permanently starve event delivery and later mods

**Status:** New. **Confidence:** High (complete tick/budget path and the nearby test inspected; not executed).

**Evidence:** `Assets/CoreAIMods/Runtime/LuaExecution/LuaCsModRuntime.cs:3234-3277` always spends the global 256-invocation allowance on timers, in fixed load order, before any queued event. `:4008-4025` resets every dispatched timer to its interval; `hooks_every(0, ...)` is explicitly supported (`:4499-4506`). Sixteen mods with sixteen every-frame timers each consume all 256 slots every tick. The default 32-mod admission allows this configuration (`:173,224-225,235`). There is no rotating cursor, reserved event allowance or overload admission rule.

**Trigger and impact:** Load those sixteen inexpensive timer mods and one ordinary mod waiting for a queued event; tick normally. The queued event never runs, and due timers in subsequently loaded mods never run either. A steady producer eventually makes bounded queues discard older undelivered events (`:4851-4856`). This can stop unrelated mods indefinitely without any one script exceeding its instruction/time/thread quota. The issue also occurs with legitimate always-on per-frame hooks, not only hostile scripts.

**Observed guards/tests:** `Assets/CoreAIMods/Tests/EditMode/LuaCsModRuntimeEditModeTests.cs:1677-1705` checks exactly the saturated first tick but uses one-second timers and then `Tick(0)`, making all those timers not due on the second tick. It therefore proves eventual delivery only after the load disappears. Stable timer-before-event ordering is intentional (`:1655-1673`); permanent starvation is not required to preserve that ordering.

**Fix direction:** Define a configurable overload/fairness policy: reserve event work, rotate admission across mods/frames or refuse a timer configuration that cannot be serviced. Preserve deterministic ordering inside each admitted batch and the global work ceiling; simply raising the ceiling does not address starvation.

**Regression recommendation:** Use zero-interval timers, keep the first group continuously due for many ticks, and require a later mod's timer and queued event to run within a documented bounded number of ticks. Assert total invocations per tick never exceed the configured ceiling and repeat to verify deterministic order.

## Existing TODO evidence and deliberate limits

These were considered when triaging findings; they are not rediscovered as new bugs:

- `TODO.md:865-871`: scheduler budget-trip impersonation. The runtime still examines `RbxError` message markers (`LuaCsModRuntime.cs:3495-3535`); typed hook/handler classification is separate. Existing issue; no new exploit run.
- `TODO.md:872-880`: default-interface reload-mode forwarding and the bare runtime's source-cap refusal. The default world facade is stricter; neither is reported again as MOD-003 or MOD-008.
- `TODO.md:859-864,904-906`: startup capture ends with the initial main-chunk execution; scheduler-built startup objects and direct-Advance-only bookkeeping remain documented follow-ups. MOD-004 concerns dispatch of a retired generation after a successful swap instead.
- `TODO.md:908-928,950-970`: uncounted VM `__concat` nesting, platform-dependent native stack limits, host callback re-entry/token audits and raw-coroutine cancellation. `LuaCsSecureEnvironment.cs:472-489,516-518` confirms the raw coroutine uses its private trip source instead of linking the resumer's token. These remain existing issues; no unsafe deep-recursion probe was attempted.
- `TODO.md:1196-1197`: synchronous Lua version-store re-reads. `FileLuaScriptVersionStore.cs:123-127,243-261` still reads/parses per call. No performance measurement was run.
- `TODO.md:2199-2202`: nonincremental WebGL JSON/ZIP and pre-semantic materialization limits. The world-store code has compressed-byte checks on reads and additional semantic/text work limits on writes (`FileRbxWorldPackageStore.cs:1214-1233,1249-1263,1273-1399`); these checks are not equivalent to a bounded incremental decoder.
- `TODO.md:3231-3234`: overlay cross-instance lost updates, separate from MOD-002's single-instance failure and retry behavior.
- Some older tracker statements are stale: `TODO.md:2523-2526` describes blocking semaphore waits for stores whose current entry paths are fail-fast (`FileLuaModStore.cs:54`, `FileLuaScriptVersionStore.cs:112`, `FileLuaModSourceStore.cs:474`). No store deadlock was inferred from that TODO wording alone.
- The broad Full reflection surface, host opt-in for private members, and re-import keeping an already-loaded mod's capability tier are documented policy (`LUA_ACCESS_MODES.md:58-76`; `MOD_SHARING.md:173-175`). No intentional FullAccess capability was classified as a sandbox escape. Blacklist hooks and type/member guards were inspected; AOT runtime behavior was not validated.
- Ordinary source-store writes/deletes/active-state changes (`FileLuaModSourceStore.cs:120,283,311`) and KV writes/clear/migration (`FileLuaModStore.cs:236,252,294`) also discard `CoreAiWebGlPersistence.Sync()`'s boolean. This is the same visible-durability-failure family as MOD-002 and should be included in its remediation/false-return regression sweep. Exact world-source preparation does await and reject a false sync result (`FileLuaModSourceStore.cs:390-438`). The ordinary runtime's best-effort persistence policy is documented; a false arm result is currently not even logged there.

## Hypotheses and missing validation

- **Malformed ZIP declared-length accounting:** the aggregate expanded-size check uses ZIP metadata (`RbxWorldPackageSerializer.cs:1694`) while the per-entry read counts actual bytes (`:1835-1842`). A small in-memory probe on this shell's .NET 10.0.11 changed an 8,192-byte entry's central-directory length to 1; `ZipArchiveEntry.Length` and the actual stream read both returned 1 (`zip_bytes=145`). Thus the suspected bypass was not reproduced and is not a confirmed finding. Unity Mono/IL2CPP behavior was not tested.
- **Cross-mod scheduled callback state:** `LuaCsRbxScriptThreadFactory.CaptureCallable` prefers the currently executing scheduler thread's owner state (`Scripting/LuaCs/LuaCsRbxSchedulerAdapter.cs:451-454`) even when `mods_call` is executing a different mod's export. A targeted regression should have mod A call B's export which schedules a callback, then verify B's globals, owner, allocation accounting and reload cleanup. The vendor VM's closure/state behavior was not inspected, so this is not classified as a defect.

## Checks actually run

- Read-only `git status --short` and `git rev-parse HEAD`: initial output recorded above; final scope/whitespace check recorded below.
- Scoped `rg --files`, `rg -n`, `Get-Content` and Python line-numbered reads: inventory and static evidence only. Some initial searches used nonexistent paths or PowerShell-incompatible wildcard path arguments and returned file-not-found/invalid-path errors; corrected paths and `-g` filters were used for follow-ups. No result from those errors was treated as source evidence.
- Pure in-memory PowerShell/.NET ZIP probe: created an 8,192-byte repeated-character entry, changed only the central-directory uncompressed length to 1, and read it with `ZipArchive`. Exact output: `.NET 10.0.11`; `declared_length=1; actual_read=1; zip_bytes=145`. No file/network/Unity/VM operation. This did **not** reproduce the suspected expansion bypass.
- Pure in-memory Python evaluation of the inspected filename algorithm: exact output `a/b -> a_b_3a8e75c1` and `a_b_3a8e75c1 -> a_b_3a8e75c1`. This verifies the string mapping collision, not a real store execution or crash.
- Python source inventory: 109 scoped runtime C# files, 15 Editor C# files; zero C# files in each of the two requested core sandbox/execution directories. `Test-Path CoreAI.Mods.csproj`, `CoreAI.Source.csproj`, `CoreAI.Tests.csproj` each returned `True`; these projects were not built.
- Final `git rev-parse HEAD main origin/main`: all three returned `69d5bcc37502cd4bb464a3bf9ac0c53b1624b804`.
- Original auditor's pre-adjudication `git diff --check -- dev-docs/audits/2026-10-23/05-mods-runtime-sandbox.md`: exit 0, no output. The untracked report was also read directly by a Python check: no trailing whitespace or Cyrillic, contiguous `MOD-001` through `MOD-011`, original priorities P0=0/P1=3/P2=8/P3=0, balanced fences and a final newline; exit 0. After root adjudication, current headings are P0=0/P1=2/P2=9/P3=0; final root validation records the corrected document separately.
- Final scoped status: this owned report and the preserved ETFX `.meta` remain untracked. `git diff --name-only` listed `PLAN.md` and `dev-docs/README.md`, which appeared as unrelated concurrent workspace edits and were not edited by this worker. No production source change was made by this audit.

The final reread checked each finding's trigger, existing guards, test gap and ownership. In particular, revised MOD-003 retains a default startup guard but lacks a separately configurable no-Rbx startup seam; its documented handler cap is not bypassed. MOD-005's confirmed impact is quota admission (specific crash-recovery outcomes remain untested), MOD-006 requires an explicitly installed host transport, and the ZIP probe remains negative evidence rather than a claimed escape. Reports 06/08 should cross-reference the MOD ids here for overlapping callers instead of duplicating ownership of these defects.

## Focused checks for root, not executed here

Root should serialize these with all other build/test work. They validate the baseline and future regression fixes; a green existing suite alone cannot disprove the gaps above.

1. Compile gate after fixes: `dotnet build CoreAI.Mods.csproj --no-restore`, then `dotnet build CoreAI.Source.csproj --no-restore`. Expected: zero compile errors; record existing warnings separately.
2. Existing portable subset (PowerShell):

   ```powershell
   dotnet test tools/portable/LuaTests/CoreAI.Portable.LuaTests.csproj -c Release --artifacts-path artifacts/portable-lua --filter "FullyQualifiedName~LuaCsModRuntimeEditModeTests|FullyQualifiedName~LuaCsModRuntimePersistenceEditModeTests|FullyQualifiedName~FileLuaModSourceStoreEditModeTests|FullyQualifiedName~LuaLogServiceEditModeTests|FullyQualifiedName~DemoModProductionSurfaceEditModeTests" --logger "trx;LogFileName=mods-runtime-sandbox.trx"
   ```

   Expected: no failed supported cases; report TRX `NotExecuted`/shim refusals explicitly. This project does not link all relevant world-follow-up/tool/host-version-store fixtures, so it is not complete coverage. Add the recommended regressions to the appropriate authorized suite before using test success as repair evidence.
3. On the next authorized Unity EditMode run, focus `LuaModsLlmToolEditModeTests`, `DataOverlayVersionStoreEditModeTests`, `LuaScriptVersionStoreEditModeTests`, `Mvp3WorldPackageFollowUpEditModeTests`, `LuaCsSecureSandboxEditModeTests`, `LuaCsGuardFrameAndAllocationEditModeTests`, `ScriptingSeamHonestyEditModeTests`, plus the affected mod-runtime/store/log fixtures. Expected: new negative cases fail on the audited snapshot and pass only with the corresponding fixes; no weakened assertions or silent skips.
4. Subsequent built-player checks: cancellation/teardown with delayed fake HTTP; same-tick reload callbacks; small no-Rbx budget fixture; configured WebGL persistence refusal; reload after failed store writes; root-shared world-store contention; bounded logger growth. Separately measure native stack and GC behavior on supported Mono/IL2CPP/WebGL targets. No real model or public network is required for these regressions.

## Not run

All builds and automated tests (root coordinates shared build/test output), Unity EditMode/PlayMode, IL2CPP/device/WebGL/browser checks, persistence-crash integration tests, network integration tests, concurrency/performance/load tests, and real LLM/model benchmarks. The verification recommendations above remain unexecuted.
