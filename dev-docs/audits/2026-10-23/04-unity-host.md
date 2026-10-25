# CoreAiUnity host and Editor audit

Date: 2026-10-23. Source baseline: main/origin/main `69d5bcc37502cd4bb464a3bf9ac0c53b1624b804`.

This is a read-only source audit. The user explicitly requested each auditor's Markdown, overriding the repository's normal prohibition on checked-in audit reports. No implementation, asset, package, index, commit, running Unity instance or model was changed by this auditor.

## Goal, boundaries and evidence standard

The real goal is a reusable Unity host that works in built players, including WebGL. The applicable invariants are Unity main-thread affinity; actor/session isolation; cancellation and scope teardown; domain-reload stability; persistence outcomes that remain truthful on failure; validation before effects; bounded allocation and retained state; replaceable host services; and Editor conveniences that remain optional.

The review traces source and relevant tests. A test's existence is coverage evidence, not an executed result. Generated `.csproj` files, successful compilation, an armed browser persistence flag and a committed IndexedDB transaction are separate claims. No Unity, browser, device, performance or live-model result is inferred here.

Provider/HTTP/SSE protocol and LLM pipeline semantics belong to report 03. This report owns composition, scene/service lifetime, chat presentation and adapter boundaries, world commands/snapshots, Unity file-store integration, Editor setup/build guards and browser-template integration. Cross-package reads below are limited to demonstrating an actual host caller or contract.

## Confirmed findings

Counted findings: **seven new defects: one P1 and six P2 (UH-02 through UH-08)**. UH-01 is retained below as an uncounted host-integration/lifecycle note.

### UH-02 - P2: world restore changes scaled children's size on every round trip

**Status:** new; no matching parent-scale tracker item found. **Confidence:** high, direct transform trace; not executed in Unity.

**Evidence:** `Assets/CoreAiUnity/Runtime/Source/Features/World/WorldStateManager.cs:275-277` saves world position/rotation but local scale. `:747` applies the saved local scale before restoring the parent. `:528` / `:535` subsequently call `SetParent(..., true)`, preserving the temporarily unparented object's world scale.

**Trigger and impact:** a tracked parent has scale `(2,2,2)` and its tracked child has local scale `(1,1,1)`. Save and load: the child's world scale changes from `(2,2,2)` to `(1,1,1)` and local scale becomes `(0.5,0.5,0.5)`. Saving and loading again shrinks it again. Compound AI-built objects therefore change shape after restart, even though position and root-only tests pass.

**Fix direction:** restore hierarchy before applying saved local scales, while preserving saved world position/rotation; process multilevel trees deterministically. An alternative coherent transform representation needs a versioned migration.

**Regression check:** round-trip rotated/nonuniformly scaled parent-child-grandchild trees twice, with reversed snapshot row order. Compare local scale and world pose. `WorldStateManagerPlayModeTests.Save_ThenLoad_RestoresTransformsAndExplicitColors` asserts root scale; its hierarchy cases do not test scaled parents.

### UH-03 - P2: save/reset confirmation can succeed after the filesystem operation failed

**Status:** new; distinct from the tracked WebGL `File.Replace` compatibility gate. **Confidence:** high, including a real caller trace; fault injection was not executed.

**Evidence:** `WorldStateManager.cs:363-390` catches write/replace failures and returns through the `void Save()` contract. `:576-600` likewise catches delete failure, still raises `StateReset` and logs that the save was deleted. `ConfirmDurabilityAsync` at `:55-74` checks only `WebGlFlushAsync()`; it retains no outcome of either operation. `Assets/CoreAiUnity/Runtime/Source/Infrastructure/CoreAiWebGlPersistence.cs:85-108` always reports true outside WebGL. The actual Hub caller at `Assets/CoreAiHub/Runtime/WorldStateHubPage.cs:163-176` invokes `Reset`/`Save` followed by `ConfirmDurability`; `:191-199` turns true into the positive confirmation label.

**Trigger and impact:** fail the world `.tmp` write, atomic replacement or deletion with an access/share fault, or use an invalid/nonwritable save destination. On desktop the confirmation still returns true; on an armed browser the arming flag also returns true independently of the failed operation. The UI can confirm storage while an old snapshot remains or no new snapshot exists. Reset's success log is also false after a delete error.

**Fix direction:** return a typed per-operation save/reset receipt that includes the filesystem result and the platform arming check. Tie confirmation to that receipt rather than a mutable global last-result flag, which would race overlapping operations. Surface failure without claiming deletion or durability.

**Regression check:** inject a write/replace failure and a delete failure, then drive the real manager-to-Hub callback sequence and assert a negative receipt/label, preserved previous data and no success log. Existing `WorldStateManagerResetPersistenceEditModeTests` checks happy-path delete/flush invocation but not failed I/O followed by confirmation.

### UH-04 - P2: a missing requested parent still produces success and world mutations

**Status:** new; no matching tracker item found. **Confidence:** high from executor and tool result paths; Unity execution not run.

**Evidence:** `Assets/CoreAiUnity/Runtime/Source/Features/World/Infrastructure/CoreAiWorldCommandExecutor.cs:263-272` ignores the result of `TryParentSpawned` after instantiation and returns the object. The helper explicitly returns false for an unresolved parent at `:738-743`. For `change`, `:627` turns failed parent resolution into `useLocalSpace=false`, proceeds to change world-space position, and returns true; the world-preserving branch also ignores failure at `:719`. `WorldLlmTool.cs:362-432` uses the executor's bool to report success.

**Trigger and impact:** request `spawn` with `stringValue=missing_root`, or `change` with that parent, `worldPositionStays=false` and a local x coordinate. Spawn leaves a new root object and reports success. Change interprets the intended local coordinate as world space and reports success. A model cannot correct its missing/typoed parent from the structured tool result, and a failed prerequisite causes effects before validation completes.

**Fix direction:** resolve/validate parent and cycle constraints before instantiation or transform changes; return a structured missing-parent failure with no effects. Preserve explicit `none` detachment and partial-success batch semantics.

**Regression check:** missing-parent spawn must return false and create no object; missing-parent change must leave parent and all transform fields unchanged for both parenting policies. Include a mixed batch and assert exact successes/failures. Existing world tool/PlayMode tests exercise valid parents.

### UH-05 - P1: a permitted component call can request an unbounded native allocation

**Status:** new; no matching tracker item found. **Confidence:** high that the bound is absent and reaches Unity; actual device allocation/failure size is unverified.

**Evidence:** `Assets/CoreAiUnity/Runtime/Source/Features/World/Infrastructure/CoreAiComponentCatalog.cs:181-182` assigns `(int)env.floatValue` directly to `LineRenderer.positionCount`. `ComponentLlmTool.cs:52-63` exposes an unrestricted numeric field; `CoreAiComponentCommandExecutor.cs:144-151` auto-adds the missing component and invokes the setter with no finite/integral/count/budget validation.

**Trigger and impact:** expose the curated `component_command` and call `set` on an existing object with `componentType=linerenderer`, `propertyName=positioncount`, `floatValue=1000000000`. This valid finite numeric input reaches Unity's position-buffer allocation on the main thread in one call. It can exhaust native/WASM memory or stall the player before cooperative cancellation or a tool timeout can help. The dangerous input was not executed during this audit.

**Fix direction:** define a host-configurable maximum and enforce a finite, integral, nonnegative count before component creation or setter invocation. Apply the same validation contract to native-tool and direct-envelope callers. Returning an explicit error is preferable to silently applying a different count.

**Regression check:** below-limit/at-limit inputs succeed; above-limit, negative, fractional and nonfinite inputs fail before adding or mutating a component. Tests must use a fake/bounded adapter or safe sentinel counts; never test the billion-position allocation. `ComponentCatalogEditModeTests` currently checks successful Rigidbody/Collider writes and an unsupported property, not allocation limits.

Related lower-severity validation gap: light `type` / `color` setters at `CoreAiComponentCatalog.cs:135-151` silently do nothing when parsing fails, but the executor still returns true and may auto-add the Light. Validation needs an outcome-bearing setter contract.

### UH-06 - P2: desktop audit failures are buffered indefinitely without a production log pump

**Status:** new; separate from tracked audit file retention and multiple-writer hash-chain debt. **Confidence:** high, all drain call sites searched.

**Evidence:** `Assets/CoreAiUnity/Runtime/Source/Features/Audit/AuditLogWriter.cs:381-400` runs only the file-flush worker on desktop. Failed appends/rotation enqueue warning strings at `:474-476` / `:530-532` into the unbounded `_pendingLogs` queue (`:19`, `:406-408`). Its private drain at `:415` is called only from `Dispose` (`:135`), `FlushForTesting` (`:144`) and the WebGL-only loop (`:365`). `AuditLogInstaller.cs:10-18` registers no main-thread tick/pump.

**Trigger and impact:** an audit append repeatedly fails after startup. The worker retries every 500 ms; the game keeps running without a visible warning, and it retains another diagnostic on every retry. The bounded audit-entry queue does not bound this second queue. An always-on dedicated host accumulates diagnostics until scope disposal, when they arrive as a burst.

**Fix direction:** install an owned main-thread diagnostic pump and bound/coalesce repeated failures while retaining counters and first/recovery messages. Ensure teardown can finish without an unbounded log burst.

**Regression check:** set the existing write-failure seam, record an entry, permit ordinary worker ticks without calling `FlushForTesting`, and assert a warning reaches the production sink promptly and retained diagnostics remain bounded across retries. Existing failure tests explicitly flush/drain and do not prove this production path.

### UH-07 - P2: file-memory bookkeeping retains every actor/session path for process lifetime

**Status:** new; no matching retention tracker item found. **Confidence:** high for monotonic retention; a long-session memory benchmark was not run.

**Evidence:** `Assets/CoreAiUnity/Runtime/Source/Features/AgentMemory/Infrastructure/FileAgentMemoryStore.cs:127-134` contains four static path-keyed dictionaries: mutation semaphores, history revisions, clear revisions and order counters. `:1622-1629` creates entries by persisted path; history reads/appends populate the others at `:986`, `:1308` and `:1332`. No removal/reset exists. `Dispose` (`:1657-1660`) disposes only the instance gate. Instance `_histories` (`:235`) also keeps each loaded key until explicit history clearing. The comment's assumption of a small fixed role set (`:119-125`) does not hold for the scoped actor/session/topic keys this package now supports (`AgentMemoryActorScopeKeyEditModeTests.cs:93-115`).

**Trigger and impact:** a persistent host serves new sessions/topics, each with a unique opaque backing key, while reusing the store or rebuilding scopes in the same process. Every touched key permanently retains dictionaries and a semaphore; repeated scope disposal does not release the static entries. Per-key history caps do not bound total key count. This matters to long-running or multiplayer hosts and to domain-reload-disabled Editor sessions.

**Fix direction:** use a lifetime-safe lease/refcount or bounded lock striping for path synchronization, and an explicit bounded/evictable history cache. Do not simply remove a semaphore on each release: a concurrent waiter can still hold it and create two gates for the same path. Retain revision correctness across peer stores.

**Regression check:** churn many short-lived scopes/session keys, release all leases/stores, and verify the retained bookkeeping plateaus; interleave same-key writers during eviction and assert no lost updates. Existing concurrency tests prove same-path writes, not bounded lifetime across unique scoped keys.

### UH-08 - P2: a later WebGL build can delete the only parked StreamingAssets backup

**Status:** new; no matching backup-collision tracker item found. **Confidence:** high from preprocess/restore paths; no real build or filesystem fault was induced.

**Evidence:** `Assets/CoreAiUnity/Editor/CoreAIWebGlStreamingAssetsGuard.cs:79-84` carries entries from interrupted builds. Restore deliberately preserves the backup if its original destination already exists (`:683-687`). But the next preprocess unconditionally recursively deletes that same `backupAbs` (`:111-115`) before moving the current source folder there (`:118`). It then removes/replaces the old manifest entry.

**Trigger and impact:** interrupt a WebGL build after moving a `LlamaLib*`/`LLMUnity*` folder into `Library/CoreAI/WebGlBuildBackup`; a package/setup process or user recreates a different source folder before restoration. Restoration refuses the conflict, correctly retaining the original backup. Start another WebGL build: preprocessing deletes that original backup, retaining only the new source. Unique original files are lost.

**Fix direction:** never overwrite an unresolved backup. Fail with an actionable conflict or allocate a distinct generation/backup path with a durable journal and explicit merge/recovery policy. A second ordering hole also exists at `:118-132`: the source folder is moved before the entry is added/persisted, and a `.meta` move failure at `:119` can leave that move absent from the recovery list. Journal intent before effects, then mark completion atomically.

**Regression check:** in an isolated temporary project-root seam, stage a pending backup and a conflicting source containing different sentinel files. A second preprocess must preserve both byte sets and a recoverable manifest. Inject failure immediately after folder move and before `.meta` move; recovery must still locate the moved folder. Existing `CoreAIWebGlStreamingAssetsGuardEditModeTests.cs:46-96` tests manifest serialization, not this actual move transaction.

## Host-integration lifecycle note

### UH-01 - reused chat panels need a host-owned account/session boundary

**Disposition (owner clarification, 2026-10-24):** uncounted integration note, removed from confirmed framework defects. The original classification assumed that one panel would survive an account/session change without a host reset or recreation. The game owns authentication, account switching, identity issuance and session/UI lifetimes. CoreAI must honor caller-supplied context/storage namespaces and expose generic cancellation, reset, disposal and composition seams; it must not implement a game's login or infer logout.

**Retained evidence:** `Assets/CoreAiUnity/Runtime/Source/Features/Chat/CoreAiChatPanel.cs:891` keys the instance `_roleTranscriptCache` by role only. `:4002-4015` and `:4083-4096` record live user/assistant text under that role. `:1925-1950` first tries the scoped backing store, then falls back to the instance cache when that store returns no rows; `:4104-4122` replays the cached role. `SetActorIdentityProvider` at `:317-321` does not retire this presentation state. `OnDisable` at `:711-758` applies the configured cancellation policy and preserves role caches for reuse within a session; disable/enable is not an account-transition signal. Requests obtain current actor context at `:3180-3193`, independently of the retained view state.

**Integration condition:** if actor A chats as `SmartChat` and the game switches providers to actor B while keeping that panel and its caches, an empty B backing store can fall back to A's cached transcript. This evidence requires the host to define its account/session transition policy; it does not establish a framework defect under a contract that requires host-owned reset or recreation.

**Host options and limits:** stop old work through the public `StopAgent()` at `:5594-5597` and destroy/recreate the panel at the transition, giving the next session a fresh instance with no retained role caches. A game that retains the panel must coordinate cancellation and reset its visible state and every cached role before admitting the new user's work. Public `ClearChat(false, false)` at `:5494-5557` stops generation, clears the feed and removes only the `ActiveRoleId` cache entry. The false flags preserve persisted chat history and long-term memory (`Assets/CoreAiUnity/Runtime/Source/Api/CoreAi.cs:848-895`). It is not an all-role reset; other cached roles still need host handling or panel recreation.

**Framework distinction and advisory:** MEM-001 and MEM-005 remain confirmed supplied-context/storage-contract defects in [report 02](02-core-memory-tools.md); this owner-boundary clarification does not reclassify them. A generic all-view-state reset seam could simplify retained-panel integrations, but it is advisory rather than a confirmed security defect.

**Host verification direction (not executed):** exercise the game's transition policy with two identities, every cached role and an old response completing after cancellation. `CoreAiChatPanelEditModeTests.cs:605` covers role switching within one identity; `CoreAiChatPanelRenderPathsEditModeTests.cs:41-67` covers text size. Neither establishes a game's account-switch policy. No live Unity reproduction was run.

## Existing tracked defects verified against current source

These are current open debt, not newly discovered findings. Tracker line references use this audited baseline.

| Priority | Existing item | Current evidence and regression direction |
|---|---|---|
| P1 | TODO A1, Git-URL install loses Editor UI (`TODO.md:1208-1212`) | `CoreAIChatDemoSceneCreator.cs:28-29,176-190` reads UXML/USS only from monorepo `Assets/CoreAiUnity`; an installed UPM package lives under `Packages/`. `CoreAISettingsAssetEditor.cs:29,90-99` similarly falls back to the default inspector. Use GUID/package-root resolution; verify a clean consuming project, not just this checkout. |
| P1 | TODO A4, stale facade chat after scene change (`TODO.md:1243-1248`) | `Api/CoreAi.cs:937-953` immediately returns cached `_chatService`; `:1027` may replace `_scope`, but `:1060-1063` recreates the chat only if it was already null. `CoreAIGameEntryPoint` handoff initializes the separate core facade, not these cached references. Regress using two actual lifetime scopes/queues and unload the first before another `CoreAi.AskAsync`. |
| P2 | TODO A13, domain-reload-disabled event/history retention (`TODO.md:1266-1269`) | `Api/CoreAi.cs:1142-1158` resets services and `CoreAiEvents`, but leaves the tool event fields/history at `:50-52,707-716`; `Api/CoreAiBackend.cs:127` has no subsystem reset for `OnBackendChanged`. Test two play generations with a retained subscriber/history receipt, not only manual `Invalidate()`. |
| P2 | Existing world multi-manager ownership and save scope (`TODO.md:3259-3264`) | `WorldStateManager.cs:260-261,605-606` enumerates every tracked object across loaded scenes; the pending-parent owner at `:153,515` is global. Additive-scene managers share one path. Fix requires explicit scene/store ownership; this is not solved by a local singleton-null check. |
| P2 | Existing audit rotation retention and multiple writer chain (`TODO.md:3228-3230,3302-3305`) | Each scope receives its own `AuditLogWriter` at the same default path, while sequence/hash state are instance-local; rotated files have no retention deletion. Source remains unchanged. Test concurrent scopes, restart verification and bounded disk quota. |

Runtime backend mutation and string-only chat error contracts are also already tracked (A5/A6/A8). They intersect provider/client ownership, so report 03 and the root reconciliation own their final disposition. This report does not reclassify their current end-to-end behavior from a partial read.

## Suspected issues and verification gaps

- **Actor changes in the legacy TMP chat:** `Features/PlayerChat/Presentation/InGameChatPanel.cs:84-90` binds actor-owned chat once in `Start`; `:159` sends through that retained service. Disable/enable does not re-resolve the current actor, and the rebind helper is internal. A host that changes users without destroying this panel may keep the old chat session. Apply the host-owned lifecycle boundary described in UH-01; no independent security finding is counted here.
- **Cancellation classification in direct tool adapters:** `WorldLlmTool.cs:435-444` and `ComponentLlmTool.cs:211-220` catch `OperationCanceledException` through `catch (Exception)` and return failure text; vision capture similarly catches an awaited cancellation inside its generic failure block. This contradicts the normative cancellation rule. A surrounding tool policy may restore cancellation from the live token; a full tool-policy/provider trace is required before claiming an end-user replay or wrong terminal status. Add direct pre-cancel and cancel-during-main-thread-switch tests.
- **Snapshot validation before destructive restore:** `WorldStateManager.cs:429-447` maps a missing `objects` field to an empty snapshot and destroys existing tracked objects before checking row identities/version/finite transforms. A `{}` or partially valid snapshot can therefore behave as an empty world, and null rows are dereferenced outside the spawn catch. Determine the supported corruption/import boundary, then validate the complete snapshot before effects. No malformed-file Unity experiment was run.
- **Optional-package assembly graph:** `CoreAI.Source.asmdef` and `Editor/CoreAI.Editor.asmdef` unconditionally name `undream.llmunity.Runtime`, although README/module menus describe its removal as safe. C# `#if COREAI_HAS_LLMUNITY` guards cannot remove an asmdef name. Clean-install/package-graph verification belongs to the packaging/tooling owner; no package was removed here.
- **WebGL build guard is a text heuristic:** `CoreAIWebGlPersistentDataSyncBuildGuard.TemplateEnablesAutoSync` checks assignment-looking lines, not JavaScript execution/order. It does not prove the selected flag survives a later overwrite or is executed before `createUnityInstance`. Runtime bridge refusal still protects against a false durability claim. No deliberate custom-template bypass was built.
- **Platform gates remain open:** actual WebGL `File.Replace`/rename behavior; tab reload persistence; browser quota/private mode; IL2CPP stripping/AOT; UI/rendering/device responsiveness; and player teardown remain unexecuted. Existing source guards and Node mocks do not close these gates.

## Coverage receipt

Coverage is explicit so sampled inspection is not mistaken for a line-by-line audit of every file in this large package.

**Composition and API, traced:** `Composition/CoreAILifetimeScope.cs`, `CoreServicesInstaller.cs`, `CorePortableInstaller.cs`, `WorldCommandsInstaller.cs`, `WorldStateEntryPoint.cs`, `CoreAIGameEntryPoint.cs`, `AgentPromptsInstaller.cs`; facade cache/reset/admission/tool-event paths in `Api/CoreAi.cs`; backend mutation/notification boundary in `Api/CoreAiBackend.cs`; attachment encode/readback/cache cleanup in `Api/AiAttachmentUnityExtensions.cs`. `LlmUnityAutostartEntryPoint.cs`, `LlmUnityModelBootstrap.cs` and `LlmUnityAutoDisableIfNoModel.cs` were sampled for supported-platform, cancellation and startup lifetime. Provider construction/routing/HTTP/stream-loop implementation is excluded to report 03.

**Runtime features, traced or sampled by relevant entry points:** world snapshot save/load/reset/restore and interface; world/component envelopes, executors, catalog, primitive/prefab registry and tools; scene/camera inspection and capture tools; `Vision/AgentCameraService.cs`, `CoreAiAgentCamera.cs`; chat panel lifecycle, typed admission, cache/hydration, request ownership, streamed completion and clear paths; chat service construction, actor lookup and persisted-history lookup; legacy TMP chat; scheduler trigger; dashboard event subscription; command router generation/disposal; WebGL local-model scene containment; Unity config store; prompt resource providers. Diagnostics/UI layout cosmetics, example prompt catalog content and every branch of the 5,000-plus-line chat panel were not exhaustively reviewed.

**Persistence/audit:** `Infrastructure/CoreAiWebGlPersistence.cs`, `CoreAiPersistentPaths.cs`; file memory load/write/locks/revisions/history caching/disposal; skill store validation, read/mutate/publish/gates/durability/cleanup; Lua/data-overlay version store constructors and persistence boundaries; audit writer worker/queue/failure/rotation/disposal; audit installers/interceptors and the audited world executor. Full engine-free memory/summary/skills/authority internals belong to other auditors.

**Reviewed Editor files:**

- Full or substantially complete relevant-path reads: `AssemblyInfo.cs`, `CoreAI.Editor.asmdef`, `CoreAIEditorLog.cs`, `CoreAILifetimeScopeEditor.cs`, `LuaScriptedImporter.cs`, `CoreAIResourcesApiKeyBuildGuard.cs`, `CoreAIProductionSettingsValidator.cs`, `CoreAIWebGlPersistentDataSyncBuildGuard.cs`, `CoreAIWebGlTemplateInstaller.cs`, `CoreAIChatDemoSceneCreator.cs`, `CoreAIWebGlStreamingAssetsGuard.cs`.
- Sampled setup/entry/lifecycle/error paths: `CoreAIBuildMenu.cs`, `CoreAIDependencyInstaller.cs`, `CoreAINuGetBootstrapper.cs`, `CoreAIModuleManager.cs`, `CoreAISettingsAssetEditor.cs`, `CoreAIWelcomeWindow.cs`, `Diagnostics/AgentSessionInspectorWindow.cs`. These samples do not certify every menu, connection-test branch, reflection API or inspector layout.
- Editor UXML/USS path ownership, package metadata and runtime asmdef were checked. Serialized project Resources settings were inventoried by nonsecret fields with key values redacted; their runtime values were not modified. The authored `.unity`, `.prefab` and `.meta` content was not changed or treated as player evidence.

**Browser bridge/template additions:** read `Runtime/Plugins/WebGL/CoreAiPersistFs.jslib` fully, the SSE bridge lifecycle/cleanup and both `Tests/Node~` harnesses. Persistence reports arming only and has no manual sync callback. SSE transport semantics remain with report 03. Compared SHA-256 for all 14 packaged `WebGLTemplates~/CoreAI` content files against installed `Assets/WebGLTemplates/CoreAI`: every file matched. The source template sets `config.autoSyncPersistentDataPath = true` before instance creation. This proves copy consistency, not successful browser storage.

**Tests read for relevant coverage:** `WorldStateManagerPlayModeTests`, `CoreAiWorldCommandExecutorPlayModeTests`, `WorldToolEditModeTests`, `ComponentCatalogEditModeTests`, `WorldStateManagerResetPersistenceEditModeTests`, `WorldStateManagerAutoSaveDisposeEditModeTests`, `CoreAiWebGlPersistenceEditModeTests`, WebGL template/StreamingAssets guard fixtures, file-memory fixtures, lifetime-scope conversation-store fixtures, actor-scoped memory key tests, chat panel cache/render/lifecycle fixtures, entry-point ownership handoff and audit writer fixtures. Several large fixtures were sampled around named behaviors; no Unity fixture was executed by this auditor.

**Explicitly excluded external/module code:** third-party Unity/LLMUnity/VContainer/MessagePipe/UniTask implementation; engine-free CoreAI implementations beyond minimal cross-boundary contracts/tests; CoreAiHub except the save-confirmation caller; CoreAIMods and its scripting VM; network/Mirror; demos/example game content; MCP; benchmarks; portable/tooling implementation. No downloaded or generated source substitutes were used.

## Actual checks and recommended verification

Performed by this auditor: read-only `rg` inventory/search and source/test tracing; exact template-content hash comparison (14/14 equal); report file creation/reread; report-only whitespace check. No compilation/test pass is claimed by these source checks.

Read-only Node checks recommended to the root, from PowerShell in the repository root:

```powershell
node 'Assets/CoreAiUnity/Tests/Node~/persist_fs_jslib_test.js'
node 'Assets/CoreAiUnity/Tests/Node~/fetch_sse_jslib_test.js'
```

Expected: exit code 0, every harness assertion passes. These execute the actual bridge source against mocked Emscripten/browser primitives; they cannot prove Unity export/linking, browser memory behavior, CORS or persistence after reload. Root-run results can be appended separately with their actual receipt.

Concrete Unity CLI filter arguments for the root's next allowed run (not executed here):

```text
-testPlatform EditMode -testFilter CoreAI.Tests.EditMode.ComponentCatalogEditModeTests
-testPlatform PlayMode -testFilter CoreAI.Tests.PlayMode.WorldStateManagerPlayModeTests
```
For the next permitted Unity run, target the fixtures named beside each finding, plus `CoreAILifetimeScopeConversationStoreEditModeTests`, `AgentMemoryActorScopeKeyEditModeTests`, `WorldCommandsInstallerEditModeTests`, `CoreServicesInstallerEditModeTests`, `CoreAiChatPanelLifecyclePlayModeTests`, `CoreAIGameEntryPointOwnershipHandoffPlayModeTests`, `AiGameCommandRouterMainThreadPlayModeTests` and `AgentCameraCapturePlayModeTests`. New regressions described above do not exist yet and must be added with their fixes. Run a fresh consuming-project/UPM smoke and built-player/WebGL reload/teardown checks after fixes; do not equate this monorepo's generated C# compile gate with those checks.

Not run here: `dotnet build/test`, Unity EditMode/PlayMode, Unity/MCP state changes, player/WebGL builds, browser/device/visual tests, IL2CPP/AOT/stripping, fault-injected real native allocations, performance/load benchmarks, native model startup, server or real LLM calls. Root owns shared build and test outputs.
