# Unity host PR audit

Status: complete static PR review. Two source-confirmed findings and one related execution finding are recorded; root owns adjudication and executable proofs.

## Review contract

- Repository: `D:/Git/CoreAI`, branch `demo`.
- Immutable range: `69d5bcc37502cd4bb464a3bf9ac0c53b1624b804..bc3fd74f0a889bfe1b1e7babc6bc02ed48a7c05e`.
- Primary scope: all 36 paths assigned to owner `04` in `coverage.json`.
- Write scope: this report only. The execution report is frozen. No production/test/metadata edits, commits, builds, tests, Unity, MCP, providers, or further agents.

## Review questions

Before planning: the goal is an independent review of introduced Unity host regressions and incorrect repairs across all assigned diff hunks. Applicable requirements are public source compatibility, trusted per-operation actor identity through request/history/clear/stop, actor/connection/role cancellation isolation, stale-scope protection, immutable progress correlation and timeout attribution, truthful world write receipts, bounded capture and transform behavior, attachment borrowing, and WebGL-safe async. Host authentication, account switching, matchmaking and view policy are outside Core; repeated roles and the documented shared-world preset are valid. `AGENTS.md` and `Docs/ARCHITECTURE_RULES.md` were read in this audit session.

Before delegation: the root supplied an exclusive leaf scope and prohibited additional agents. No implementation is delegated. Changed-line causality, valid callers, test semantics and uncertainty must accompany each finding.

Before accepting: findings require a real changed line, concrete supported trigger, expected/actual behavior, causal path, and a minimal regression plan. Historical Done/green claims are evidence to inspect, not proof. This is static review only.

## Confirmed findings

### PR-HOST-001 [P2] Retain existing public signatures when adding actor-aware calls

**Changed target:** `Assets/CoreAiUnity/Runtime/Source/Api/CoreAi.cs:833` (also `ClearContext` at line 861, and `CoreAiChatService.cs:631,655`).

**Trigger:** an existing host binds the public API as a method group, for example `Action<string> stop = CoreAi.StopAgent`, `Action<string, bool, bool> clear = CoreAi.ClearContext`, or `Action<string> stop = chatService.StopAgent`. Another supported integration uses exact-arity reflection, such as `GetMethod("StopAgent", new[] { typeof(string) })`.

**Expected/actual:** those existing source and reflection bindings should remain valid while the actor-aware operation is added. At the candidate they fail to compile or return no matching method. Optional parameters preserve direct invocation syntax, but do not supply missing parameters during method-group conversion or create an old-arity reflected method.

**Causality:** the old public methods were replaced in place by signatures with an additional optional `ActorContext?` argument. No forwarding overload preserves the old signature. The same compatibility issue affects the replaced public `CoreAiChatService` and `CoreAiComponentCommandExecutor` constructors for exact-arity reflection/`Activator` users (`CoreAiChatService.cs:45`, `CoreAiComponentCommandExecutor.cs:15`). This does not request restoring global cancellation semantics: the preserved old entry point can delegate to the new actor-specific behavior.

**Minimal regression:** compile a consumer fixture containing the old delegate assignments; assert exact reflected method/constructor signatures exist for the previous argument types, and exercise those signatures with fake services. Existing direct-call tests cannot detect the missing signatures. Preserve the existing direct actor-aware call tests too.

**Uncertainty:** static language/API review only. No compile or runtime proof was run. Direct ordinary invocations remain source compatible; the claim is specifically delegate and exact-signature reflection compatibility.

### PR-HOST-002 [P2] Match progress using the explicit trace actually propagated to custom orchestrators

**Changed target:** `Assets/CoreAiUnity/Runtime/Source/Features/Chat/CoreAiChatService.cs:274,312`.

**Trigger:** a caller supplies an explicit trace containing surrounding whitespace, such as `" caller-trace "`. A valid injected orchestrator publishes its tool lifecycle events using the request's unchanged `TraceId` through `new LlmToolCallInfo(request.TraceId, normalizedRole, ..., request.ActorContext.Value.ActorId)`. Tool starts/completions happen within each idle window, while the total turn spans more than one window.

**Expected/actual:** that turn's own progress should rearm its idle deadline. The candidate captures `request.TraceId.Trim()` but deliberately preserves the original request value. Event matching compares `info.TraceId` exactly to the trimmed capture. `LlmToolCallInfo` only null-coalesces its trace; it does not trim it. Every event forwarded from the request is rejected, so the otherwise active turn receives a timeout.

**Causality and test gap:** the stricter actor/trace/role match is new. The real core orchestrator normalizes its trace when building a request, so its normal events work. Injected orchestration is a supported replaceable seam; the chat service itself preserves explicit traces, and the event contract calls its trace the correlation id propagated from the request. The changed in-repository `GatedToolCallOrchestrator` helper uses exactly this natural propagation (`CoreAiChatServiceEditModeTests.cs:1446`). The added positive test instead supplies `" caller-trace "` but manually publishes the different string `"caller-trace"`; it does not exercise natural propagation of the request's trace. This finding concerns the trace only, without weakening actor or role isolation.

**Minimal regression:** modify a controlled custom orchestrator to raise started/completed/failed events from its received `request.TraceId`, use the existing virtual idle clock, and parameterize explicit traces with/without surrounding whitespace. Assert matching events rearm and foreign actor/trace events still cannot rearm. Choose one consistent boundary normalization or exact identity contract and apply it to both captured and incoming metadata.

**Uncertainty:** no test execution. If custom publishers are intentionally required to trim an otherwise preserved opaque trace, that requirement needs an explicit public contract; neither the request nor event metadata contract currently states it.

## Related execution finding

The source-confirmed omitted-provider problem in frozen `01-execution.md` / PR-EXEC-001 also exists at the chat boundary: the new optional constructor provider falls back to a fresh empty provider (`CoreAiChatService.cs:56`), and `BindRequestActor` freezes that empty value before forwarding to a correctly configured queue/core. Existing manual chat composition that previously configured only the scoped memory store is therefore affected even when its inner core uses the real host provider. History/clear operations use the same new default. This is recorded as a cross-boundary instance of PR-EXEC-001, not a second counted isolation finding. Normal scope-based construction resolves and passes the actual provider.

## Read coverage

All 36 primary paths and all 177 zero-context diff hunks were read. Hunk counts use `git diff --unified=0` for the immutable range; these are source read-coverage counts, not semantic or runtime coverage. No primary ownership gap remains. Supporting context was read where needed; unchanged large fixture/panel bodies are not claimed exhaustive.

| Primary path | Hunks read | Actual read depth |
| --- | ---: | --- |
| `Assets/CoreAiUnity/Docs/DEVELOPER_GUIDE.md` | 1/1 | Complete changed version/footer hunk. |
| `Assets/CoreAiUnity/Editor/CoreAIChatDemoSceneCreator.cs` | 3/3 | All changed GUID/path lookup hunks with scene-creation context; GUIDs resolve to their source metadata in the checkout. |
| `Assets/CoreAiUnity/Runtime/Source/Api/CoreAi.cs` | 25/25 | All diff hunks; complete changed cache/resolver, settings, actor stop/clear/injection and subsystem-reset paths; related streaming/admission callers and clear helper. |
| `Assets/CoreAiUnity/Runtime/Source/Api/CoreAiBackend.cs` | 1/1 | Entire new locked static-event reset and its subsystem caller. |
| `Assets/CoreAiUnity/Runtime/Source/Composition/CorePortableInstaller.cs` | 2/2 | Complete changed factory composition, provider injection and host factory-preservation guard with adjacent registration context. |
| `Assets/CoreAiUnity/Runtime/Source/Composition/WorldCommandsInstaller.cs` | 2/2 | Complete changed component-options flow and world receipt alias registration with installer regression. |
| `Assets/CoreAiUnity/Runtime/Source/Features/Chat/CoreAiChatPanel.cs` | 16/16 | Every changed hunk; actor retention, hydration, dropdown/abandon/stop/clear calls and turn-generation/CTS ownership context. Unchanged UI rendering bodies not claimed exhaustive. |
| `Assets/CoreAiUnity/Runtime/Source/Features/Chat/CoreAiChatService.cs` | 25/25 | Every changed hunk; complete construction, scope resolution, nonstream deadline path, eager streaming admission, private stream, history/clear/stop/binding paths; token and borrowed-attachment contracts. Unchanged one-shot vision internals were contextual only. |
| `Assets/CoreAiUnity/Runtime/Source/Features/Chat/README_CHAT.md` | 1/1 | Complete changed actor/logical cancellation and host view-lifetime documentation. |
| `Assets/CoreAiUnity/Runtime/Source/Features/World/ComponentLlmTool.cs` | 1/1 | Complete changed cancellation catch with request dispatch/main-thread/result/error context. |
| `Assets/CoreAiUnity/Runtime/Source/Features/World/IWorldStateManager.cs` | 1/1 | Complete changed arming-versus-operation contract. |
| `Assets/CoreAiUnity/Runtime/Source/Features/World/Infrastructure/CoreAiComponentCatalog.cs` | 3/3 | All added validators and entries; registration alias sharing and relevant Light/LineRenderer setters. |
| `Assets/CoreAiUnity/Runtime/Source/Features/World/Infrastructure/CoreAiComponentCommandExecutor.cs` | 4/4 | All changed constructor/validation hunks; complete command parse/add/remove/set and mutation-order paths. |
| `Assets/CoreAiUnity/Runtime/Source/Features/World/Infrastructure/CoreAiComponentCommandOptions.cs` | 1/1 | Entire added options type. |
| `Assets/CoreAiUnity/Runtime/Source/Features/World/Infrastructure/CoreAiComponentCommandOptions.cs.meta` | 1/1 | Entire two-line metadata; only one matching GUID found. |
| `Assets/CoreAiUnity/Runtime/Source/Features/World/Infrastructure/CoreAiWorldCommandExecutor.cs` | 14/14 | Every changed hunk; complete parent preflight/change/resolve paths, spawn path and diagnostic reset; relevant batch/error callers. |
| `Assets/CoreAiUnity/Runtime/Source/Features/World/WorldLlmTool.cs` | 3/3 | Complete changed error/cancellation hunks with main-thread dispatch, batch parsing/limits and result construction context. |
| `Assets/CoreAiUnity/Runtime/Source/Features/World/WorldStateManager.cs` | 29/29 | Entire candidate file, lines 1-851: save/load/reset/arming/results, transform restore order, capture, unresolved parents, observer isolation, disposal and snapshot spawn helpers. |
| `Assets/CoreAiUnity/Runtime/Source/Features/World/WorldStateWriteResult.cs` | 1/1 | Entire added enum, immutable result and optional interface. |
| `Assets/CoreAiUnity/Runtime/Source/Features/World/WorldStateWriteResult.cs.meta` | 1/1 | Entire two-line metadata; only one matching GUID found. |
| `Assets/CoreAiUnity/Tests/EditMode/ComponentCatalogEditModeTests.cs` | 1/1 | All added finite/integral/bounded count and invalid Light regressions; bounded values and auto-add/mutation assertions checked. |
| `Assets/CoreAiUnity/Tests/EditMode/CoreAiChatServiceEditModeTests.cs` | 6/6 | All changed hunks, virtual progress tests and changed raw-identity publisher; relevant request probe, gates and token fake context. |
| `Assets/CoreAiUnity/Tests/EditMode/CoreAiFacadeEditModeTests.cs` | 19/19 | Every changed hunk; actor composition/stop assertions, native setup/teardown ownership and explicit resolver expectations checked. |
| `Assets/CoreAiUnity/Tests/EditMode/CoreServicesInstallerEditModeTests.cs` | 4/4 | Every changed audit disposal assertion and cleanup block; captured token/source/pump expectations checked. |
| `Assets/CoreAiUnity/Tests/EditMode/MultiplayerSessionIntegrationTests.cs` | 1/1 | Entire added 319-line fixture and fakes; eager actor/trace capture, history/clear/stop, reconnect and factory replacement cases. |
| `Assets/CoreAiUnity/Tests/EditMode/MultiplayerSessionIntegrationTests.cs.meta` | 1/1 | Entire two-line metadata; only one matching GUID found. |
| `Assets/CoreAiUnity/Tests/EditMode/UnityHostRegressionTests.cs` | 1/1 | Entire added 505-line fixture and helpers: parent no-effect/cycle/batch cases, scene scope retirement, resolver policy, admitted panel actor, generation reset, GUIDs and cancellation. |
| `Assets/CoreAiUnity/Tests/EditMode/UnityHostRegressionTests.cs.meta` | 1/1 | Entire two-line metadata; only one matching GUID found. |
| `Assets/CoreAiUnity/Tests/EditMode/WorldCommandsInstallerEditModeTests.cs` | 1/1 | Complete added composition test including host count limit and receipt registration identity. |
| `Assets/CoreAiUnity/Tests/EditMode/WorldStateWriteResultTests.cs` | 1/1 | Entire added 175-line fixture: write/replace/delete faults, prior snapshot preservation, independent arming, observer failure and disposal. |
| `Assets/CoreAiUnity/Tests/EditMode/WorldStateWriteResultTests.cs.meta` | 1/1 | Entire two-line metadata; only one matching GUID found. |
| `Assets/CoreAiUnity/Tests/PlayMode/FastNoLlm/WorldStateManagerPlayModeTests.cs` | 1/1 | Entire added 65-line reversed-row hierarchy round-trip test with existing backup/cleanup/tag helpers. |
| `Docs/CoreAI/MULTIPLAYER_SESSIONS.md` | 1/1 | Entire added 127-line guide; identity, host ownership, scope, cancellation, trace, borrowed attachments, endpoint and world limits checked against source boundaries. |
| `dev-docs/audits/2026-10-23/04-unity-host.md` | 1/1 | Entire added 187-line historical audit, including withdrawn host-lifecycle note and old evidence/gates; no old finding automatically counted. |
| `dev-docs/fix-wave-2026-10-24/15-unity-host-integration.md` | 1/1 | Entire added 133-line integration receipt; policy changes, fixture ownership and historical validation claims read without treating them as fresh proof. |
| `dev-docs/fix-wave-2026-10-24/workdoc04-unity-host.md` | 1/1 | Entire added 134-line repair contract/receipt; invariants and claimed implementation checked against candidate source. |

Supporting read-only seams include `ActorContext`, actor issuance/provider, `AgentMemoryScope` binding/AsyncLocal/key resolution, scoped memory decorator, queue actor cancellation, `AiTaskRequest.Clone`, `LlmToolCallInfo`, and concrete core trace normalization. Their primary ownership remains with the assigned execution/memory/tools reviewers.

## Acceptance review and uncounted boundaries

- Checked the supplied actor through chat binding, stream clone, history, clear and stop; the panel retains the admitted connection for active cancellation. Same-role players are valid. Role-only presentation caches and account/view replacement remain host responsibilities under the explicit guide.
- Changed global/admin cancellation fallback removal is documented policy with caller tokens retained; it is not counted as a bug merely because a legacy custom orchestrator lacks the optional actor capability.
- Facade resolution now uses local `out` values, detects retired Unity scope/container identities, and preserves an explicit resolver as the authoritative composition. Explicit resolver mode intentionally releases automatic chat/settings caches, as documented and asserted by the new fixture; that deliberate mode is not counted as a stale-scene defect.
- Subsystem generation reset clears tool/backend subscribers and tool history; manual `Invalidate` preserves subscriber state by design. Actual domain-reload-disabled play generations remain unexecuted.
- World save/reset receipts separate local status from persistence arming and explicitly do not acknowledge browser transactions. Missing parent/cycle validation precedes effects; hierarchy restore applies parent scales before descendant world pose. The default cross-scene capture/shared storage path is an explicit single-world preset, not an automatic multiplayer bug.
- Curated LineRenderer validation is finite, integral and bounded before auto-add/mutation. Added tests use small safe sentinel values; no large native allocation was attempted. Stable GUID source lookup is checked in the checkout; no clean consumer-UPM import was run.
- Attachment lists/bytes remain borrowed with documented completion/disposal lifetime. The streaming admission clone intentionally retains those reference fields. No new threads or blocking waits were identified in the reviewed host diff; browser scheduling/AOT evidence remains separate.
- What the added tests miss: old delegate/reflection signatures (PR-HOST-001), self-published raw explicit traces through the existing gated helper (PR-HOST-002), and omitted-provider manual composition (related PR-EXEC-001). No additional introduced defect is confirmed from this scope.

## Verification and limits

- Read-only Git/source inspection only; root owns actual proofs and fixes.
- Final `git rev-parse HEAD` matched the immutable candidate. Scoped runtime/test status had no changes; only this new report was untracked. The frozen execution report was not edited.
- Report checks found all 36 owner paths, two host findings, zero trailing-whitespace lines and zero Cyrillic prose lines. Hunk inventory independently counted 177.
- No compile, test, Unity Editor/player, WebGL/browser/device, live model or network acceptance is claimed.
