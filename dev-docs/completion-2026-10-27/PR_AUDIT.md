# Current CoreAI working-tree PR audit — 2026-10-27

Status: final source/correction review complete; full2 has zero failures; current live/hermetic/root acceptance remains
incomplete. Candidate version is **7.55.0 on demo**. Published 7.54.0 at `bc3fd74f` remains the review
baseline; a version bump is not a publication, tag or main promotion.

## Real goal and invariants

Ship configurable runtime blocks: portable Gateway Client with thin Unity composition, the actual
runtime editor port (Lua default/Python execution Unsupported), bounded initial Lua invocations,
eight opt-in samples, host gameplay bindings and compatible dependency updates. Preserve legacy
signatures, actor/request/partition identity, capability policy, ownership, cancellation/failure
isolation, ordering/backpressure and truthful persistence receipts. Game topology, shared RoleId
and multiplayer/session policy remain host choices. The NPC/navigation proposal was cancelled;
ignored staging is excluded from the intended feature surface.

Root owns integration, version/changelog/PLAN, all Unity/provider execution and Git/publication.
This reviewer changed only assigned documentation/ledger files and parsed existing evidence.
The owner requested retained module Markdown, overriding the default report-deletion convention.
Original audits/inventory and all rejected/negative evidence remain preserved.

## Coverage and review ownership

| Surface | Authoritative review | Source result |
| --- | --- | --- |
| Portable Core/orchestration/memory/gateway/contracts | [CORE.md](CORE.md) | No additional runtime finding; final utility delta also reviewed in candidate report. |
| Unity host/chat/endpoint lifecycle | [UNITY.md](UNITY.md) | One baseline defect in touched registry cleanup, corrected in promoted snapshot. |
| Mods/runtime/stores/examples/bindings | [MODS.md](MODS.md) | No additional runtime finding; editor defects counted separately. |
| Hub setup/lifecycle/save/routing | [HUB.md](HUB.md) | One integration finding, corrected at source; actual grouped-Hub verification still fails setup. |
| MCP | [MCP.md](MCP.md) | No additional confirmed source defect; targeted saved HTTP evidence has stated composition limits. |
| Mirror | [MIRROR.md](MIRROR.md) | No additional confirmed source defect; in-process network evidence has stated limits. |
| Benchmark | [BENCHMARK.md](BENCHMARK.md) | No additional behavior finding; both inactivity regressions pass in a historically failed containing run. |
| Editor view/language composition | [EDITOR_REVIEW.md](EDITOR_REVIEW.md) | Two original findings, source-fixed with the later quote finding below. |
| Merged editor/Hub/registry/utility candidates | [CANDIDATE_REVIEW.md](CANDIDATE_REVIEW.md) | Final twelve-file manifest approved after rejected intermediate candidates. |
| Documentation/indexes/21-finding ledger | [DOCUMENTATION.md](DOCUMENTATION.md) | Usage and tracking corrections complete for this snapshot; final receipt reconciliation pending. |

Native reviews use gpt-6.1-sol with reasoning effort max. Failed/stalled free OpenCode attempts
are not a completed review from another model family. The candidate review is independent of
implementation; an authored regression or approved hash does not itself establish execution.

## Seven additional source findings and their corrections

These IDs are separate from the original 21. The first five have source fixes promoted by root from
manifest SHA-256 `0562cb77c0e29c90f097785cdf841c29d129af134750e8dd3bb6c4894e1f12a1`.
All twelve production/test hashes were independently matched to
`artifacts/completion-20261027/editor-promotion.json`. Rejected candidate iterations and their
concrete failure triggers remain in CANDIDATE_REVIEW.md; they were not silently accepted.

| ID | Origin and corrected behavior | Current execution evidence |
| --- | --- | --- |
| `UNITY-REVIEW-001` P2 | Pre-existing defect in touched Dispose: throwing activation cancellation skipped other owned host releases. Cleanup is isolated outside the mutation lock. | Named throwing-activation registry case Passed in focused3; Gateway composition 28/28 Passed. |
| `HUB-REVIEW-001` P2 | Hub consumed Escape before the active Mods editor. Grouped page/Mods/editor now forward through the existing escape-handler seam. | Both actual grouped-Hub Escape cases now Passed in full2; the earlier setup failures remain in the historical focused3 snapshot. |
| `EDITOR-REVIEW-001` P2 | Enter lost full Lua multiline literal/comment context and changed literal indentation. Full-source token classification now retains context and real inline-comment block openers. | Language policy 15/15 and actual long-string Enter/undo case Passed in focused3. |
| `EDITOR-REVIEW-002` P3 | A second Python-only scan suppressed Lua pairing after `#`. The additive helper accepts the language's already established insertion context. | Native Lua length-operator/Python-comment control Passed; original overload retained. |
| `CANDIDATE-REVIEW-001` P2 | Tracked quote type-over was blocked by insertion context. Current-token checks and a verified insertion-range tracker now distinguish closers/escapes/literals. This existed in frozen source; it was not merge-introduced. | Quote/parenthesis/literal controls and the insertion-hint tracker Passed; current UI fixture 20/20 Passed in full2. |

The auto-pair P3 discovered by the Core reviewer is counted only in the editor report. Hub Escape
is counted only under its Hub ID. No finding is duplicated merely because multiple reports refer
to it. Root final acceptance is separate from source promotion and targeted positive evidence.

## Original 21 reconciliation

[FIXES.json](../pr-review-2026-10-25/FIXES.json) preserves all 21 original IDs, source anchors,
initial red aliases and 36-case history (28 Failed/8 Passed; earlier red attempts did not compile).
Twenty repaired source findings map to 48 exact passing saved regression methods. PR-14-001 is
docs-only with two matching compile examples, not a direct runtime case. All final root statuses
remain pending. [39-receipt-reconciliation.md](../pr-review-2026-10-25/39-receipt-reconciliation.md)
records exact scope limits: direct MCP transport versus automatic component, offline Mirror
callbacks versus live full-world teardown, optional World binder versus page registration, and
Escape-handler return versus actual ancestor/descendant routing.

## Current source-delta follow-up

[FINAL_SOURCE_REVIEW.md](FINAL_SOURCE_REVIEW.md) independently approves two additional confirmed
runtime findings and the later guard/fixture correction wave. There are now seven distinct source
findings; compatibility/guard corrections are not counted as additional current runtime bugs.

| ID | Confirmed source defect | Corrected source / current acceptance |
| --- | --- | --- |
| `EDITOR-REVIEW-003` P2 | A rejected stale completion still swallowed ordinary Tab/Enter. | Success-return/fallback and stale popup invalidation source-approved; current UI 20/20 Passed after correcting two invalid fixed-four-space fixture expectations. |
| `DEMO-REVIEW-001` P2 | Real manager source/cache events did not repaint rows after status updates. | Event-driven repaint ordering source-approved; actual OpenRouter Demo05 rerun pending. |

Last-entry hashes for all twelve distinct files in the final review/correction tables independently
match current source. The earlier promotion manifest is historical for superseded paths.

## Earlier executed evidence (superseded snapshots)

| Receipt | Actual rows/outcomes | Scope |
| --- | --- | --- |
| `core-final2.trx` |2,954 Passed/3 NotExecuted/0 Failed | Portable Core after promoted utility change; new Mods native fixtures are not implicitly in its compile list. |
| `lua-final2.trx` |1,962 Passed/41 NotExecuted/0 Failed | Portable Lua; native/player/rendering limits remain. |
| `analyzers-final1.trx` |8 Passed | Analyzer regression selection. |
| `core-perf-green-debug1.trx` and `core-perf-green-release1.trx` |111 Passed each | Earlier focused portable allocation/backpressure selections, not device/room capacity. |
| `unity-completion-focused3.xml` |154=145 Passed/5 Failed/4 Skipped | Selected UI/typing 20=17P3F; separate grouped-Hub/language 4=2P2F. |
| `unity-openrouter-direct1.xml` |1 Passed | Actual Meai client HTTP completion through OpenRouter inside Unity. |
| `unity-live-storage1.xml` |1 Passed | Genuine file-backed registration/singleton isolation. |
| `unity-openrouter-hub2.xml` |1 Passed | Actual Hub scene's live Chat/Statistics/Mods flow. |
| `unity-openrouter-demos1.xml` |18=11 Passed/5 Failed/2 Skipped | Initial real demo/model-authoring matrix; QwenGenie/Spellcraft/Skills actual tool calls pass within it. |
| `unity-openrouter-repair1.xml` |5=3 Passed/2 Failed | FullAccess/GameplayServices/LuaMods now pass; model create/edit and ModsChat manager row remain failed. |
| `benchmark-chart-final1.log` |6 tests, OK | Python benchmark-chart regression log independently read. |

Full paths, counts and receipt hashes are in the ledger and [FINAL_VERIFICATION.md](FINAL_VERIFICATION.md).
The earlier focused2=92P3F4Skipped, source batch=56P1F, PlayMode=390P1F2Skipped and Core negative=
2,951P1F3NotExecuted remain negative history. A scoped rerun never rewrites a containing run.

Current focused3 failures: completion-key Tab after document/panel change, completion acceptance
(`loc` versus `local`), post-layout navigation (18 expected/19 actual), and both grouped-Hub cases'
missing editor element during setup. The old detach case now passes. Four native allocation
cases explicitly defer to portable accounting; they are Skipped, not Passed.

The QA isolation is now public VContainer builder-slot replacement before Build/resolve with
genuine production file stores under an owned GUID root and unchanged persistence modes. The
rejected PostProcessScene/pre-Awake plan and redundant QA storeId that exceeded the Windows path
budget are history. Production path guards were not weakened. [LIVE_QA.md](LIVE_QA.md) is owned by
the live worker and records the actual helper/scene contract.

Actual PackageCache/DLL readback confirms MCP 10.3.0, NeoTools 10.19.0, TimeProvider 8.0.1 and
Numerics/Tensors 9.0.22. MEAI 9.10.2/STJ 8.0.6 remain ceilings; portable Newtonsoft is 13.0.4.
The historical pending integration JSON does not prove restore. DOTween delta is importer metadata.

## Historical first full EditMode / tooling snapshot

`unity-completion-edit-final1.xml`: **8,268 rows = 8,239 Passed / 14 Failed / 15 Skipped**.
UI fixture 20=18P2F, typing 2/2P, actual grouped-Hub/language 4/4P. All mapped methods for 19 original
EditMode findings are present and Passed; Benchmark's two regressions are PlayMode-only and PR-14
is docs-only. The containing run remains failed. The fourteen are two invalid Tab expectations,
ten positive legacy save fakes missing the required budget capability, and two source-guard
violations. [WEBGL_GUARDS.md](WEBGL_GUARDS.md), [HUB_SAVE_GUARDS.md](HUB_SAVE_GUARDS.md) and the final
source-review addendum approve corrections without weakened guards/assertions or extra runtime IDs.
Full2 was pending at that checkpoint; its current zero-failure result is recorded below. Core-final3 has 2,954 Passed/3 NotExecuted/0 Failed after corrections.

Curated export1 positive-opt-in and version checks pass at index 833eafd..., before the latest
corrections; raw non-UTF8 ignored-scratch failure remains negative history. The staged secret-pattern
scan has 411 paths/0 hits. Console normal restore/build succeeds with 0 errors/warnings after a stale
no-restore failure; no product change. Node fetch-SSE/persistence source logs are positive, not browser
proof. [FINAL_VERIFICATION.md](FINAL_VERIFICATION.md) is the authoritative current receipt/checklist.

## Current full2 and open gates

`unity-completion-edit-final2.xml` has 8273 actual rows: 8258 Passed, 15 Skipped, 0 Failed; process exit 0.
UI 20/20, typing 2/2, grouped-Hub/language 4/4, Hub Save 22/22, production-VM adapter 22/22, context guard 3/3,
WebGL primitive guard 10/10 and Gateway composition 28/28 Passed. Original 19 EditMode finding-method
sets are all present and Passed; Benchmark remains PlayMode-only and PR-14 docs-only. The 15 skips
are 10 native allocation-accounting exclusions, 3 legacy local 4B explicit checks and 2 optional local
Roblox-docs-mirror cases. No local inference or skipped-case pass is inferred.

- Root reviews full2 logs/exclusions and awaits current actual OpenRouter create/edit and manager
  receipts, plus source-final hermetic PlayMode and final clean-export checks.
- Root acceptance of original 21 and additional 7 remains pending an explicit decision; native counts
  do not make that decision automatically. All earlier failed containing runs remain failed history.
- Computer Use, MiniRpg movement and qualitative materials visuals stay explicit user-deferred
  limitations. Main/release remain pending required gates and root decision.
- Player/IL2CPP/WebGL/browser/device, real network/load and production capacity/performance need
  their own evidence. Source, Node and portable/native fixtures do not establish those targets.

## Documentation verification

All 21 original IDs are preserved and 48 method maps independently resolve to Passed saved rows.
Both multiplayer classes match the actual compile-example bodies. Assigned relative links and
English/trailing-whitespace checks are rerun before freeze. Earlier scoped git diff --check passed;
root owns the fresh final Git check. This reviewer runs no test/build/Unity/provider operation.


## Follow-up source and Node snapshot

The initial twelve-file promotion readback matched its approved manifest. Root's subsequent
native-repair work has advanced `CodeEditorView.cs`, `CodeEditorLanguageAndHubNativeEditModeTests.cs`
and `CodeEditorUiEditModeTests.cs`; the old promotion hash does not certify these later bytes.
Those intermediate hashes are retained in FIXES.json. FINAL_SOURCE_REVIEW.md subsequently approved
the final source/correction wave and full2 supplies the current native results. Focused3 remains
a failed historical snapshot; approval never retroactively relabels that run.

`artifacts/completion-20261027/fetch-sse-node-final1.log` ends `ALL PASS`;
`persist-node-final1.log` contains nine PASS checks. Both logs were independently read, and root
reports each command exited 0. These are Node source harnesses, not browser/player acceptance.

## Root adjudication of all 28 findings - 2026-10-28

Scope: the 21 original findings ([FIXES.json](../pr-review-2026-10-25/FIXES.json)) and the seven
additional ones above, checked against the source after release 7.55.0 (`134184ec`) plus the 2026-10-28
working tree. Each finding was re-read from its original report, the current production path was located,
and every named regression was found in source and in the new full EditMode receipt
`artifacts/storage-root-20261028/edit-full1.xml` (8,304 cases: 8,289 Passed / 0 Failed / 15 Skipped). A
read-only pass located fixes and tests; a sample of its citations was re-read by hand and matched
(`McpHttpServer.cs:160-185`, `CoreAiMirrorSessionHost.cs:250-257`, `DownlevelResult.cs:144`,
`CodeEditorView.cs:1451-1455`, `LlmClientRegistry.cs:295-342`). A regression only counts when it fails on
the old code; where a mapped test would stay green with the fix reverted, a discriminating one was added
("new regression").

**Result: all 28 are already fixed. None is still present and none was rejected as not a defect. Two more
defects found while verifying them are confirmed and fixed (ADJ-001, UNITY-REVIEW-002).**

| ID | Classification | Fix (current source) | Regression evidence |
| --- | --- | --- | --- |
| PR-EXEC-001 P1 | already fixed | `AiOrchestrator.cs` legacy constructor resolves the provider from the store; `AgentMemoryScope.cs`; `CoreAiChatService.cs` | `PrReviewCompositionEditModeTests` EXEC001, 11 Passed |
| PR-MEM-001 P2 | already fixed | `AgentMemoryFileKey.cs` migration receipt; `FileConversationSummaryStore.cs` confirms read migrations; `FileAgentMemoryStore.SettleAsync` | 7 Passed; **new regression** `FileAgentMemoryStoreEditModeTests.AsyncLoad_LegacyMigrationRunsTheConfiguredConfirmation` for the agent-memory read path |
| PR-MEM-002 P2 | already fixed | `CallSkillToolLlmTool.cs` and `ReadSkillLlmTool.cs` bind the same admitted request | MEM002, 2 Passed |
| PR-LLM-001 P2 | already fixed | `MeaiLlmClient.cs` applies disabled-tool mode before skill binding, completion and streaming | LLM001, 8 Passed |
| PR-HOST-001 P2 | already fixed | legacy arities on `CoreAi.cs`, `CoreAiChatService.cs`, `CoreAiComponentCommandExecutor.cs` | HOST001, 8 Passed |
| PR-HOST-002 P2 | already fixed | `CoreAiChatService.cs` matches the propagated raw and trimmed trace | HOST002, 7 Passed |
| PR-AUD-001 P2 | already fixed | `AuditLogWriter.cs` normalises before hashing; only lone surrogates become U+FFFD | AUD001, 2 Passed |
| PR-AUD-002 P3 | already fixed | `LuaLogService.cs` clips on a pair boundary | AUD002, 3 Passed |
| PR-MODS-001 P2 | already fixed | strict capability in `ILuaModSourceStore.cs`; facades in `RbxWorldPackageContracts.cs`, `HubModServiceBase.cs` | `WorldSourceFacade_HubUsesLegacyMutationsWithoutAcknowledgingPersistence`, 2 Passed |
| PR-MODS-002 P2 | already fixed | `FileLuaModSourceStore.cs` stages a first install and moves it into place | 2 Passed; staging follow-up is ADJ-001 |
| PR-MODS-003 P2 | already fixed | `LuaModStoreId.cs` strict UTF-8 before any I/O | 2 Passed |
| PR-RBX-001 P2 | already fixed | `InstanceGameObjectBinder.cs` rechecks teardown after acquire | RBX003, 1 Passed |
| PR-08-001 P3 | already fixed | `DownlevelResult.cs` refuses `_ENV` as a resolver name | 5 Passed |
| PR-08-002 P2 | already fixed | `LuauRewriteParser.cs` resolves the floor helper lazily | 3 Passed (`UnusedOperationDoesNotRequireMath` discriminates) |
| PR-09-001 P2 | already fixed | `CoreAiHubSetupMenu.cs` adds world and mod binders before prefab persistence | 1 Passed |
| PR-09-002 P2 | already fixed | `CoreAiHubWindow.OnRootKeyDown` consumes Escape only when handled | 4 Passed; **new regression** `HubContractRegressionTests.Window_RootKeyDown_ConsumesEscapeOnlyWhenTheHubHandlesIt` (2 cases) observes bubbling through the real root handler |
| PR-09-003 P3 | already fixed | `HubSettingsPage.cs` invalidates a pending save only for draft-field edits | 4 Passed |
| PR-MCP-001 P2 | already fixed | `McpHttpServer.Stop` releases the listener before cancellation callbacks | 2 Passed, real loopback transport |
| PR-NET-001 P2 | already fixed | generation-checked release in `MirrorNetworkBridge.cs`, `CoreAiMirrorSessionHost.cs` | 9 Passed |
| PR-BENCH-001 P2 | already fixed | `GameCreationBenchmarkHarness.cs` measures a continuation from its own turn start | 2 PlayMode cases, hermetic receipt in [FINAL_VERIFICATION.md](FINAL_VERIFICATION.md) |
| PR-14-001 P2 | already fixed (docs) | `Docs/CoreAI/MULTIPLAYER_SESSIONS.md`, `Assets/CoreAiUnity/README.md` | `MultiplayerDocumentationCompileExamples.cs` compiles in the full run |
| UNITY-REVIEW-001 P2 | already fixed | `LlmClientRegistry.Dispose` settles cancellations outside the lock, isolated | 1 Passed |
| HUB-REVIEW-001 P2 | already fixed | grouped Mods/editor Escape forwarding (`HubSubTabPage`, `HubModsPage`, `CodeEditorView`) | 2 native cases Passed |
| EDITOR-REVIEW-001 P2 | already fixed | `CodeLanguageProfiles.cs` classifies with the full source | 13 Passed |
| EDITOR-REVIEW-002 P3 | already fixed | `CodeEditorTextUtility.cs` accepts the language's insertion context | 2 Passed |
| CANDIDATE-REVIEW-001 P2 | already fixed | `CodeEditorView.cs` current-token quote checks, verified insertion tracker | 7 Passed |
| EDITOR-REVIEW-003 P2 | already fixed | `CodeEditorView.cs` swallows Tab/Enter only after a successful acceptance | 2 Passed |
| DEMO-REVIEW-001 P2 | already fixed | `LiveMechanicsModsChatPersistenceController.RecomputeModLists` repaints after source events | only the explicit live Demo05 case existed; **new hermetic regression** `LiveMechanicsModsChatManagerPlayModeTests.ManagerRows_RepaintAfterRuntimeLoadAndUnloadEvents` drives the real scene without a model |

| New ID | Classification | Trigger and fix | Regression |
| --- | --- | --- | --- |
| ADJ-001 P3 | confirmed, fixed | A crash or failed cleanup between writing a first-save staging folder and moving it into place left `.staging-<id>` with a complete manifest; `List()` showed the mod twice and the stored-mod quota counted it. `FileLuaModSourceStore` now skips staging folders when listing and counting. | `FileLuaModSourceStoreEditModeTests.List_IgnoresAStagingFolderLeftByAnInterruptedFirstSave` |
| UNITY-REVIEW-002 P2 | confirmed, fixed | `LlmClientRegistry.AddOrUpdateEndpointAsync` and `RemoveEndpointAsync` cancelled a superseded activation under the mutation lock; a throwing host callback aborted the operation, so a removed endpoint stayed registered and its owned host was never released. Both now capture the source under the lock and cancel it afterwards, isolated and logged, like `Dispose` and `SetEndpointActiveAsync`. | `GatewayClientCompositionEditModeTests.RemoveEndpoint_ThrowingActivationCallbackCannotAbortRemovalOrSkipOwnedHostRelease`, `UpdateEndpoint_ThrowingSupersededActivationCallbackCannotAbortTheUpdate` |

Red check for the regressions added here: with the four fixes temporarily reverted to their old behaviour
(registry add/remove from `134184ec`, staging folders listed, Escape always consumed, read migration not
confirmed), `artifacts/storage-root-20261028/edit-red2.xml` ran the new cases: 6 = 1 Passed / 5 Failed, every
failure on the asserted symptom (two copies listed; no confirmation exception; `AggregateException` from the
host callback for remove and update; Escape not reaching the host). The one pass is the collapse control,
which is consumed on both versions. The green code was restored and `edit-focused2.xml` passed 175/175; the full EditMode suite on the final tree
(`edit-full2.xml`) is 8,307 = 8,292 Passed / 0 Failed / 15 Skipped. The
DEMO-REVIEW-001 hermetic case is judged red by reasoning only: without the repaint, the runtime load has no row
for the test to find; it passes on the current scene in the hermetic gate.

Residual test-strength notes (no defect; kept open in `TODO.md`): PR-BENCH-001 does not check that
`RunScenario` passes the turn baseline; PR-MCP-001's automatic restart is covered only over the in-memory
transport and the real-listener test becomes `Assert.Ignore` when the URL ACL bind is refused; PR-08-002
lacks the two-chunk `LuaCsScriptEngine` reuse case; PR-AUD-001 does not assert the lone-surrogate
precondition; PR-09-001's `AddHub` path is untested. Open question for the owner, not a finding: in Auto
tool mode with an empty `AgentRoleId`, a memory store and a skill proxy, `RoleBinding.RequireRole` throws.
