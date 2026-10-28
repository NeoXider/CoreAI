# Published demo live acceptance

**Data-safety incident:** Earlier runs did not isolate `WorldStateManager`; its timed autosave
overwrote the pre-existing default `CoreAI/WorldState/world_state.json` with an empty demo state.
The previous bytes have not been recovered. Do not rerun this fixture until its real world-state
manager is redirected to a verified owned path before initialization.

**Update 2026-10-28:** isolation is verified. The fixture's owned-path checks passed in three real scenes, and
every test run now also redirects `CoreAiStorageRoot` to a temporary directory, so default stores cannot reach
the developer's saves. The free-model rerun and its HTTP 401 outcome are in
[OPENROUTER_TRIAGE.md](OPENROUTER_TRIAGE.md#free-model-rerun---2026-10-28).

Status: real OpenRouter verification is in progress. The complete matrix is not green. The latest fixes for the two remaining automated failures await a root-owned rerun; two manual acceptance gates remain open.

The goal is the actual seventeen published scenes plus two real Programmer turns authoring and editing one harmless Lua mod. The user selected OpenRouter; the tested model was deepseek/deepseek-v4.1-flash. No local/native backend is permitted for these acceptance runs.

## Saved receipts and negative history

These NUnit receipts were inspected from disk. A direct API completion does not replace scene or tool-effect acceptance.

| Receipt | Actual result | Scope |
| --- | --- | --- |
| [Direct HTTP PlayMode](../../artifacts/pr-review-20261025/unity-openrouter-direct1.xml) | 1 passed | Production Meai HTTP client reached OpenRouter in PlayMode |
| [Storage replacement](../../artifacts/pr-review-20261025/unity-live-storage1.xml) | 1 passed | Exact factory-slot replacement preserved the genuine file-store contract/singleton; the default factory never ran |
| [Original Hub](../../artifacts/pr-review-20261025/unity-openrouter-hub2.xml) | 1 passed | Actual published Hub, typed live result and actual page transitions |
| [Complete live matrix](../../artifacts/pr-review-20261025/unity-openrouter-demos1.xml) | 18 total: 11 passed, 5 failed, 2 skipped | Seventeen scenes plus create/edit; this is a negative matrix |
| [Affected gates after path repair](../../artifacts/pr-review-20261025/unity-openrouter-repair1.xml) | 5 total: 3 passed, 2 failed | FullAccess, GameplayServices and LuaMods passed; strict model edit and ModsChat remained failed |

The earlier unity-openrouter-hub1.log ended with exit 3 and no XML before a model turn. Configuring scopes from PostProcessScene was too late: their containers had already built. That assumption was rejected. The independent Mirror metadata callback remains.

The first full matrix correctly rejected a 278-character Windows source path, above the production limit of 259. The helper redundantly added Stores/id-SHA256 under an already GUID-owned root. Removing that test-only namespace removed 75 characters, giving the corresponding 203-character footprint. The real store supports absent fresh directories; no initialization workaround or path-guard relaxation was needed. Three affected scene passes in repair1 validate the repair. Negative receipts remain intact.

In repair1 the model genuinely created the mod and Folder.Stage=created, then successfully called get_source and reload. Later discovery called unavailable coreai_world_find through call_skill_tool and requested a nonexistent Rbx API section named 12. Attributes & tags. Strict all-tool-success correctly failed before edit acceptance completed. A successful mutation does not erase earlier or later tool failures.

ModsChat exposed a real repaint defect. Setup/source events recomputed cached lists but did not repaint; existing repaint callers were actions on buttons that had not initially rendered. The latest controller change repaints after event-driven cache recomputation. Ready status and source/version/active flags are now updated first. It adds no per-frame store enumeration or injected row. Runtime verification of that change is still pending.

## Actual composition and storage isolation

The fixtures load the original scene paths and use the production CoreAI/CoreAiMods scopes, Lua-CSharp VM, active-world facades, tools, prompt policy, queue/orchestrator, HTTP client, panel admission and typed results. No model reply, generated Lua implementation, tool success, world or store is faked.

Current isolation uses the installed VContainer public IContainerBuilder indexer inside LifetimeScope.Enqueue, after Configure and before BuildRegistry/service resolution. Existing backing-store slots are replaced in place, preserving implementation type, interfaces, lifetime and key. Duplicate singleton registrations are not appended. The passing storage regression proves describing/replacing the registration does not invoke the original factory.

Actual FileAgentMemoryStore, FileConversationSummaryStore, FileLuaScriptVersionStore, FileDataOverlayVersionStore, desktop FileTokenCalibrationStore, FileLuaModStore, FileLuaModSourceStore and FileRbxWorldPackageStore receive paths within one previously absent GUID directory. Their original scoped/active-world facades resolve those backing services. Serialized scene fields and selected persistence modes stay unchanged. Source/state roots are not redundantly namespaced beneath that GUID directory.

LlmPipelineInstaller preserves an explicit endpoint-registry argument, then accepts an optionally registered ILlmEndpointRegistryStore, then uses its existing default. The fixture supplies the genuine FileLlmEndpointRegistryStore at its owned root. User endpoint/profile state is not used. SetWorldPackageStore remains a generic pre-container host seam: the fixture tests late-retarget rejection, but no longer calls it from a scene postprocessor.

Before a paid turn in a mods scene, STORAGE_PREFLIGHT checks real source admission and actual file-backed key/value Set/Get/Clear with an owned probe id. This is distinct from the model-authorship id; the model must still author, persist and execute its own source.

Settings are snapshotted in memory, temporarily overridden with the explicit HTTP provider and restored without save/reimport. Initially dirty flags are restored; clean assets stay clean. Original settings bytes are compared after teardown. Tool argument capture is temporarily enabled to prove actual load/get_source/reload receipts. Credentials are process-only and excluded from receipts.

PostProcessScene now performs only armed/path-gated optional Mirror generated-identity metadata handling, with COMPATIBILITY receipts and rejection of baked invalid identities. It configures no scope or storage and does not run during player builds.

Cleanup cancels work, drains tracked tasks, forgets only reserved ids, verifies scene-source baselines, unloads to PlayModeSceneSandbox, restores settings and removes only exact reserved GUID directories after checking absolute bounds. User save/source namespaces are not used. Force-killed leftovers require verification of the exact run id and bounds before cleanup.

## Case coverage

Passes below belong to the named saved runs/source revisions. They are not a new single-run pass of the current tree.

| Case | Latest saved result | Actual acceptance observable |
| --- | --- | --- |
| 01 FullAccess | Passed, repair1 | Self-test, Tetris HUD/rendered nudge/stopped runtime, live panel |
| 02 GameplayServices | Passed, repair1 | Tour world, physical dropped-block health effect, gravity restored, live scope |
| 03 Hub | Passed, demos1 and hub2 | Real typed reply and actual Statistics/Mods/Chat pages |
| 04 LiveMechanics | Passed, demos1 | Actual hit 33 under owned rule, vanilla 15 after unload, live panel |
| 05 ModsChat | Failed, repair1; latest repaint fix pending | Actual owned Logs/Deactivate/Activate row, timer reports, then live panel |
| 06 WaveAutoBattler | Passed, demos1 | Two battle_tick payloads, reports stop after owned observer unload, live panel |
| 07 LuaMods | Passed, repair1 | Actual shipped load/event/unload controls, damage 45 then vanilla slot, live scope |
| 08 MiniRpg | Partial / NUnit skipped, demos1 | Chat/manager driver passed; native walk/look input remains interactive |
| 09 ModdableUnits | Passed, demos1 | Unique real archetypes spawned and enemy died in combat, live panel |
| 10 MultiplayerFoundation | Passed, demos1 | Actor-count proof, actual actor A turn, A marker absent from B transcript; one-process scope |
| 11 OnlineAuthority | Passed, demos1 | Guest refused/allowed/refused by revision, host revision allowed, live scope; one-process scope |
| 12 ProceduralMaterials | Partial / NUnit skipped, demos1 | Material/view/sweep drivers and live scope passed; reviewed visual captures remain open |
| 13 QwenGenie | Passed, demos1 | Actual HTTP wish UI, grant_gold receipt/effect, charge decrement/reset |
| 14 QwenSpellcraft | Passed, demos1 | Actual HTTP Cast UI, cast_spell receipt, observed mana spend/refill |
| 15 Skills | Passed, demos1 | Real GameMaster consumed iron/wood, crafted an item and damaged dummy |
| 16 WorldCommands | Passed, demos1 | Actual controls spawned/destroyed cmd_enemy_1, live scope |
| 17 StandaloneChat | Passed, demos1 | Actual nonempty successful typed panel result and collapse/reopen |
| Programmer create/edit | Failed, repair1; narrowed task pending | Actual load/get_source/reload, changed saved source/Folder.Stage, one owned object, retained revisions, every tool successful |

The latest authoring task exposes manage_mods and read_skill only. It requests whole catalog skills using all=true and no section, then ends tool use after mutation. The host independently verifies effects. No Lua implementation is prefilled. Strict completion, every-tool success, saved source, runtime effect and revision assertions remain; actual successful load/get_source/reload action receipts for the owned id are now required as well.

This does not certify manual editor selection, native typing/save-button input, Windows Computer Use, player/WebGL builds, real networking/capacity or performance. Those gates remain separate.

## Run contract and bounds

Root privately supplies process variables:

    COREAI_TEST_BASE_URL=https://openrouter.ai/api/v1
    COREAI_TEST_MODEL=deepseek/deepseek-v4.1-flash
    COREAI_TEST_API_KEY=<private process environment>
    COREAI_TEST_STREAMING=true
    COREAI_TEST_NATIVE_TOOLS=true
    COREAI_TEST_EXTRA_BODY_JSON={"reasoning":{"enabled":false}}
    COREAI_PLAYMODE_LLM_BACKEND=http

Canonical explicit URL/model variables are required. There is no asset/local-config/native/Offline fallback. Temperature is zero; configured orchestration/context retries are zero. Actual role routes must be ClientOwnedApi. There is no scenario replay after effects; stream-opening retries remain production transport behavior. Provider/auth/budget failures cannot trigger another backend.

Exact root-owned test filters:

    CoreAI.Tests.PlayMode.PublishedDemoStorageRegistrationPlayModeTests
    CoreAI.Tests.PlayMode.CoreAiPublishedDemosLivePlayModeTests
    CoreAI.Tests.PlayMode.CoreAiEditorSaveLivePlayModeTests
    CoreAI.Tests.EditMode.QwenDemoReadinessBackendEditModeTests

The two live classes are Explicit and LiveLlm, with UNITY_EDITOR && COREAI_LLM && COREAI_LUA guards. Demos have a 180-second framework budget. Programmer creation/edit has a 360-second budget with at most two 120-second turns and finite output/tool budgets. LiveTestRequestScope reserves teardown time and bounds tracked cancellation drain to five seconds. UI roles are cancelled in teardown. No fixture enables LogAssert.ignoreFailingMessages.

## Current source handoff

Saved positive and negative XML and actual source/event/store contracts were inspected. Latest standalone Unity Roslyn compilation of both live fixtures and the complete CoreAI.Demos assembly returned exit 0, with existing obsolescence warnings. Logs are in C:/AI/work/coreai-completion-20261027/live-isolation-candidates/model-manager-repair2/. This is compile evidence only; root owns Unity import/reruns, final complete-matrix accounting, console/visual/native-input review and release gates.

The actual Demo05 live scene test is the repaint regression: it must locate and invoke the real row callbacks, observe deactivation/reactivation and reports, then complete the actual panel turn. Runtime source fixes are not recorded as passes until that test is rerun. Existing event guards, autoload batching, failure state and disposal remain.
