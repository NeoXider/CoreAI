# PR repair receipt reconciliation — 2026-10-27

Status: source repairs and saved regressions reconciled; final acceptance remains root-owned.
Reviewer: native gpt-6.1-sol, reasoning effort max. Read-only source/receipt review; no tests or
builds were executed here. Exactly the original 21 finding IDs remain in `FIXES.json`.

## What the statuses mean

All 21 have implemented working-tree repairs (PR-14 is documentation). Twenty have at least one
named passing saved regression; PR-14 has corrected guidance and matching compile examples,
not a direct runtime case. `repaired_pending_root_acceptance` is deliberately separate from
root acceptance and publication. Original audit reports, inventory, initial red aliases and
36-case / 28 Failed / 8 Passed history are preserved. Initial red1/red2 did not compile.

## Finding-to-regression map

Test names below are exact methods; parameterized cases are counted from the saved XML/TRX rows.
The complete method lists, case counts, receipt SHA-256 values and current source hashes live in
[FIXES.json](FIXES.json). A saved run never silently covers a later source change.

| Finding | Saved regression methods | Receipt | Limits |
| --- | --- | --- | --- |
| PR-EXEC-001 | `EXEC001_ActorlessUnknownQueuePreservesLegacyMemoryResolution` (2)<br>`EXEC001_ChatWithoutStoreCapturesInnerBeforeDeferredStream` (2)<br>`EXEC001_ManualChat_OmittedScopeProviderRetainsStoreAndCoreScope` (2)<br>`EXEC001_ManualCore_OmittedScopeProviderRetainsScopedHistory` (2)<br>`EXEC001_OpaqueChatStoreSuppressesUnrelatedInnerProvider` (1)<br>`EXEC001_QueueDiscoversInnerScopeBeforeWaiting` (2) | focused5 | Scoped manual core/chat, queue discovery and opaque-store controls; fake provider/store boundaries. |
| PR-MEM-001 | `AsyncMigration_PrimaryStorageFailureSurvivesConfirmationFailure` (1)<br>`ClearAfterPartialLegacyMigration_DoesNotResurrectLegacyHistory` (1)<br>`SyncRead_AsyncOnlyConfirmation_RefusesMigrationBeforeAnyEffect` (1)<br>`SyncRead_LegacyMigration_ConfirmsThroughTheSyncPortBeforeReturning` (1)<br>`MEM001_CanonicalReadDoesNotInventAWriteReceipt` (1)<br>`MEM001_ReadMigrationWaitsForReceiptAndRetainsRefusedGeneration` (2) | focused5 | Read-triggered confirmation, no-op canonical read and migration failure controls. |
| PR-MEM-002 | `MEM002_DeniedTargetRemainsUnavailableForDiscoveryAndInvocation` (1)<br>`MEM002_DiscoveryAndInvocationAgreeForIndependentRequestBindings` (1) | focused5 | Independent request-bound discovery/invocation plus denied-target control. |
| PR-LLM-001 | `LLM001_NoneModeDoesNotBindUnusedSkillFactories` (8) | focused5 | Eight combinations of native/text, completion/stream and empty/Teacher role; no skill factory binding. |
| PR-HOST-001 | `HOST001_LegacyConstructorAritiesRemainAvailable` (3)<br>`HOST001_LegacyHistoryReflectionSignatureStillReturnsHistory` (1)<br>`HOST001_LegacyMethodsRemainExactAndDelegateBindable` (4) | focused5 | Exact legacy constructors, reflection methods and delegate assignments. |
| PR-HOST-002 | `HOST002_ForeignProgressCannotRearmTheTurn` (3)<br>`HOST002_ProducerForwardedTraceRearmsItsOwnTurn` (4) | focused5 | Caller/canonical trace correlation and actor/role/trace isolation. |
| PR-AUD-001 | `AUD001_ValidPostMarshalToolSnapshotDoesNotPoisonFlushOrDisposal` (2) | focused5 | Post-marshal snapshot flush/disposal in synchronous and asynchronous paths. |
| PR-AUD-002 | `AUD002_LogClippingPreservesUnicodeInMessageScriptAndToolSurface` (3) | focused5 | Split surrogate, intact pair and ASCII clipping through log/tool surfaces. |
| PR-MODS-001 | `WorldSourceFacade_HubUsesLegacyMutationsWithoutAcknowledgingPersistence` (2) | focused5 | Legacy facade mutation success remains distinct from persistence acknowledgment. |
| PR-MODS-002 | `FirstCanonicalSave_PreservesExistingCorruptIdentityEvidence` (1)<br>`FirstCanonicalSave_TransientManifestFailureDoesNotPoisonItsRetry` (1) | focused5 | Retry after first-save manifest failure plus corrupt-identity preservation. |
| PR-MODS-003 | `StoreNamespace_MalformedUtf16IsRejectedBeforeStorageChanges` (1)<br>`StoreNamespace_ValidSupplementaryIdentifierPreservesItsPublishedDigest` (1) | focused5 | Malformed identity rejection before I/O plus valid supplementary digest control. |
| PR-RBX-001 | `RBX003_TeardownDuringAcquireReleasesReturningLeaseImmediately` (1) | focused5 | Reentrant host teardown during actual binder material acquisition. |
| PR-08-001 | `InvalidTrustedResolverName_IsRefusedWithoutThrowing` (5) | core-final1 | Five invalid resolver names include _ENV. This finding is in portable Core/Luau, not the 500-case Unity selection. |
| PR-08-002 | `FloorDivision_MissingMathErrorsAtOperation` (1)<br>`FloorDivision_ObtainsFunctionBeforeOperandEffects` (1)<br>`FloorDivision_UnusedOperationDoesNotRequireMath` (1) | lua-editor-full1 | Executable Lua unused-operation/lazy-error/evaluation-order controls; portable Lua evidence. |
| PR-09-001 | `Setup_BuildHub_ComposesTheCoreRuntimeComponents` (1) | editor-source-batch1 | Latest source adds the optional WorldStateHubBinder assertion. Earlier focused5 version asserted only base components. Does not assert page registration with a fake world manager. |
| PR-09-002 | `HandleEscapeRequested_CollapseOccurs_IsConsumed` (1)<br>`HandleEscapeRequested_EscapeCollapsesDisabled_DoesNotCollapseHub` (1)<br>`HandleEscapeRequested_PageHandlerReturnsTrue_IsConsumedEvenWhenCollapseDisabled` (1)<br>`Window_EscapeOptOut_DoesNotCollapseAndKeepsFocus` (1) | focused5 | Handler return/collapse controls and attached-panel opt-out. Full ancestor bubbling acceptance still needs root readback. |
| PR-09-003 | `EndpointSave_FoldoutToggleDuringPendingSave_KeepsReceiptAndClearsAcceptedKey` (2)<br>`EndpointSave_RealDescriptorEditDuringPendingSave_StillInvalidatesReceipt` (2) | focused5 | Presentation toggles preserve pending receipt; real edits invalidate it. |
| PR-MCP-001 | `Start_AfterStop_RebindsLoopbackPrefix_AndServes` (1)<br>`Stop_ReentrantCancellationCallback_ReleasesPrefixBeforeCallback_AndReplacementServes` (1) | focused5 | Real same-prefix HTTP replacement. Direct transport fixture does not itself assert automatic component queue plus authenticated replacement tool call. |
| PR-NET-001 | `NET001_BindingRelease_CapturedCompanionSurvivesLegacyDisposalAndFailures` (1)<br>`NET001_Disconnect_IdenticalPeerReplacementKeepsItsGenerationAndRequests` (2)<br>`NET001_Disconnect_ReentrantReplacementReleaseNotifiesBothGenerations` (1)<br>`PeerDisconnect_ReusedOverrideReplacementSurvivesBothListenerOrders` (4)<br>`PeerDisconnect_SameIdReplacement_SurvivesDelayedBindingRelease` (1) | focused5 | Generation-aware offline bridge/session fixtures; real world/player/resource/chat teardown and live transport remain separate acceptance. |
| PR-BENCH-001 | `ProviderInactivityGap_FreshQuietContinuationUsesCurrentTurnStart` (1)<br>`ProviderInactivityGap_QuietContinuationBeyondThresholdIsEnvironment` (1) | play1 | Controlled continuation timing below/above threshold. Benchmark fixture green; containing PlayMode selection has an unrelated failure. |
| PR-14-001 | `MultiplayerDocumentationCompileExamples.cs` (two compile examples) | documentation readback | Legacy `IInGameLlmChatService` factory contract is distinct from actor-aware `CoreAiChatService`; no direct runtime row. |

## Receipt accounting

Counts use actual `test-case` or `UnitTestResult` rows, not CLI aggregate skip messages.

| Receipt path | Rows | Outcomes |
| --- | ---: | --- |
| `artifacts/pr-review-20261025/unity-repair-focused5.xml` | 500 | 500 Passed |
| `artifacts/pr-review-20261025/unity-editor-source-batch1.xml` | 57 | 56 Passed, 1 Failed |
| `artifacts/pr-review-20261025/unity-repair-play1.xml` | 393 | 390 Passed, 2 Skipped, 1 Failed |
| `artifacts/pr-review-20261025/unity-repair-console-play2.xml` | 2 | 2 Passed |
| `artifacts/completion-20261027/core-final/core-final1.trx` | 2957 | 2954 Passed, 3 NotExecuted |
| `artifacts/editor-integration-20261026/lua-full-results1/lua-editor-full1.trx` | 2003 | 1962 Passed, 41 NotExecuted |
| `artifacts/completion-20261027/core-perf/core-perf-green-debug1.trx` | 111 | 111 Passed |
| `artifacts/completion-20261027/core-perf/core-perf-green-release1.trx` | 111 | 111 Passed |
| `artifacts/completion-20261027/core-results/core-completion1.trx` | 2955 | 2951 Passed, 1 Failed, 3 NotExecuted |
| `artifacts/pr-review-20261025/initial-regression-red3.xml` | 36 | 28 Failed, 8 Passed |

The old failing editor case is `CodeEditorUiEditModeTests.DetachReattachAndDispose_InvalidatePendingDocumentWork`.
Its source has since been repaired; only a fresh receipt can establish the new native outcome.
The old failing PlayMode case is `FullAccessDemoScenePlayModeTests.CommandInputField_ConstructsAndInitializesWithInstalledTmp`.
Two later isolated console passes do not establish full PlayMode green. The strengthened World binder
assertion is evidenced by the 57-case source batch; the older 500-case version asserted only base components.
It still does not assert a registered World page against a fake world manager.

## Latest native focused receipt

`artifacts/pr-review-20261025/unity-completion-focused2.xml` now records **99 rows: 92 Passed,
3 Failed, 4 Skipped**, process exit 2. Native compilation reached tests. Failed exact methods:

- `CodeEditorUiEditModeTests.CompletionKeyPair_DoesNotSuppressTabAfterDocumentOrPanelChange`
  expected the replacement text plus four spaces; the Tab edit did not occur.
- `CodeEditorUiEditModeTests.DetachReattachAndDispose_InvalidatePendingDocumentWork`
  expected the complete Python stub plus `p`; actual source became `# Python execup`.
- `CodeEditorUiEditModeTests.NativeBrackets_AndCompletionUseBoundedPopup`
  expected `()` after typing its closer; actual source became `))`.

Passing subfixtures are Core contracts 11, active-world invocation budget 9, interpreter adapter 18,
Hub typing 2, ModdableUnits bindings 13, Qwen readiness 10, hot-path controls 22 and editor UI 7.
The 27 native active-world/adapter cases therefore have their own passing receipt. All four skips
explicitly defer allocation accounting to the portable leg. The complete focused selection is
failed; neither those subfixture passes nor source review closes editor input/focus/native gates.

## Dependency state

Current `Packages/manifest.json`, `Packages/packages-lock.json`, resolved PackageCache metadata and
`Assets/packages.config` are the applied-source readback. `artifacts/dependency-update-20261026/managed-assemblies.json`
records actual restored TimeProvider 8.0.1, Bcl.Numerics 9.0.22 and Tensors 9.0.22 DLL identities/hashes.
The two `*-integration-receipt.json` files were written earlier and still say resolve/restore pending;
they are not success receipts. Core final and scoped Unity runs are later compilation/execution evidence,
with their own scope limits. MCP client/server 10.3.0 and NeoTools 10.19.0 are applied; MEAI 9.10.2 and
System.Text.Json 8.0.6 remain deliberate ABI ceilings. DOTween changes are importer metadata only.

## Open acceptance gates

- Correct the three native editor failures in focused2 and rerun current-source integrated Unity
  selections after any corrections. Facade/adapter/readiness subfixtures passed within that failed run. Third-party immutable-folder missing-meta and Burst startup messages
  remain observed issues; a zero test failure count alone does not erase them.
- Real API PlayMode uses OpenRouter exclusively. A direct forced-tool HTTP probe is provider evidence,
  not Unity scene evidence. No local-model inference may substitute for the authorized gate.
- All 17 demos need actual scene-specific behavior checks; MiniRpg movement and ProceduralMaterials
  visuals have explicit partial/manual rows. The code editor needs native input, focus, selection,
  completion, save/reload and visible behavior review. Computer Use is deferred while unavailable.
- Built players, IL2CPP, WebGL/browser, device, live network, capacity and performance remain separate
  gates unless their own receipts are supplied. Queue microbenchmarks are not room/device capacity.
- Root reviews all module findings, owns final acceptance/version/changelog/Git and follows demo-first
  publication. This reconciliation does not authorize promoting a partial gate to passed.


## Promoted fixes and later receipts

Root promoted twelve exact peer-reviewed source/test files from candidate manifest SHA-256
`0562cb77c0e29c90f097785cdf841c29d129af134750e8dd3bb6c4894e1f12a1`.
`artifacts/completion-20261027/editor-promotion.json` records the paths/hashes; independent readback
matched all twelve production/test files. [CANDIDATE_REVIEW.md](../completion-2026-10-27/CANDIDATE_REVIEW.md)
retains rejected/intermediate candidate history. The fifth finding, `CANDIDATE-REVIEW-001` (P2 quote
closer type-over), was present in frozen source too; it is not a merge-introduced defect and is
counted once alongside the prior four. All five source fixes are promoted; final native/runtime
acceptance is not implied.

The new `unity-completion-focused3.xml` has **154 rows: 145 Passed, 5 Failed, 4 Skipped**. The selected
UI/typing leg is 20 rows (17 Passed / 3 Failed); the separate actual grouped-Hub/language leg is
4 rows (2 Passed / 2 Failed). Current failing UI methods are completion-key Tab, completion
acceptance (`loc` instead of `local`) and post-layout selection/navigation (18 expected,19 actual).
Both grouped-Hub cases fail to find the expected editor field during setup; they do not yet verify
Escape dismissal. The previous detach/reattach failure now passes. All quote/tracker controls,
15 language-policy cases, the actual long-string Enter case, Lua/Python insertion control and
28 Gateway-composition cases pass; the registry throwing-callback cleanup case is among them.
The four allocation skips remain explicit portable-accounting exclusions. The full selection is failed.

Later actual TRX rows: `core-final2.trx` 2,954 Passed /3 NotExecuted, `lua-final2.trx`1,962 Passed /
41 NotExecuted, `analyzers-final1.trx`8 Passed; all zero Failed. The Core rerun includes the promoted
utility change, but the explicit portable compile list does not silently include the new Mods
native/policy fixtures. Earlier Core/Lua counts remain historical snapshots, not source-final proof.

Actual Unity/OpenRouter evidence is now available: `unity-openrouter-direct1.xml`1/1 Passed through
the real Meai HTTP client; `unity-live-storage1.xml`1/1 Passed for genuine file-backed singleton
registration; `unity-openrouter-hub2.xml`1/1 Passed for the actual Hub scene's Chat/Statistics/Mods.
The initial demo/model-authoring matrix remains **18 rows:11 Passed,5 Failed,2 Skipped**. Its scoped
repair selection remains **5 rows:3 Passed,2 Failed**. FullAccess, GameplayServices and LuaMods now
pass after removal of a redundant QA namespace that exceeded the Windows path budget; production
path guards were unchanged. Model-authored create/edit still fails tool discovery, and ModsChat
still times out finding the owned row through the actual manager path. QwenGenie, QwenSpellcraft
and Skills have actual model/tool passes within the initial failed matrix. Individual positives
do not relabel that matrix as green.

The actual QA isolation uses the public VContainer builder registration slots before Build/resolve
and genuine production file stores under one GUID-owned root. Persistence modes are unchanged;
this supersedes the rejected PostProcessScene/pre-Awake plan. The live worker owns [LIVE_QA.md](../completion-2026-10-27/LIVE_QA.md)
and the final helper contract; this report does not rewrite that worker's source/report.

Root set **7.55.0 as the demo candidate version** with the required bump script; manifest/internal-pin,
MCP server info and guide/footer readback agree. No main promotion, release/tag or Git publication
is claimed. Both required Computer Use hosts lack the Node REPL/Computer Use APIs. User-deferred
MiniRpg native movement and ProceduralMaterials visual cases remain explicit partial/Ignore rows;
code-editor Computer Use remains unperformed. No UI-helper workaround was used.

The raw positive-module scan failed on a non-UTF8 file under ignored Mirror scratch. Root will rerun
the unchanged tool on a curated clean final-tree export. Neither success nor a product defect is
inferred from that raw failure. Root reports Python chart 6/6 Passed; its saved receipt path is not
yet supplied. [FINAL_VERIFICATION.md](../completion-2026-10-27/FINAL_VERIFICATION.md) is the current
root checklist and will be reconciled to fresh receipts before documentation freeze.


## First full EditMode gate and correction wave

`unity-completion-edit-final1.xml` contains 8268 actual rows: 8239 Passed, 14 Failed, 15 Skipped.
All mapped methods for 19 original EditMode findings are present and Passed. The two Benchmark
methods are PlayMode-only; PR-14 remains docs-only. No root acceptance is inferred from named
positives inside this failed containing run.

UI fixture 20=18P2F, typing 2P, grouped-Hub/language 4P. All 14 failures are explained and corrected
at source: two invalid fixed-four-space Tab expectations, ten positive Hub Save fixtures using an
unbudgeted legacy fake, and two guards. Corrected literal next-stop expectations 4/1/3, explicit
budget-capable positive service fixtures with preserved unsupported negative, full WebGL exclusion
of the .NET transport, concrete synchronous Lua Run/compatibility RunAsync and fixture-local context
detach retain original guards and assertions. [WEBGL_GUARDS.md](../completion-2026-10-27/WEBGL_GUARDS.md)
and [HUB_SAVE_GUARDS.md](../completion-2026-10-27/HUB_SAVE_GUARDS.md) describe this compatibility wave;
it is not 14 additional runtime bugs.

[FINAL_SOURCE_REVIEW.md](../completion-2026-10-27/FINAL_SOURCE_REVIEW.md) source-approves two more
confirmed runtime findings: EDITOR-REVIEW-003 (stale completion key swallowed) and DEMO-REVIEW-001
(manager event/cache repaint). Total additional source findings is seven; all have reviewed fixes,
with full2/API/hermetic results and explicit root acceptance pending. Current last-entry hashes
for all twelve distinct paths in the independent final review tables match actual files.

Core-final3 actual TRX is 2954 Passed/3 NotExecuted/0 Failed. Curated staged export1 positive-opt-in
and version logs pass at index 833eafd... before latest corrections; staged secrets scan 411 paths
reports 0 pattern hits. Console final1 is a stale `--no-restore` assets failure; final2 normal restore/
build has 0 warnings/errors. All raw negatives and older snapshots are retained. Root owns the
final export/settings/Git and publishes only after its required acceptance decisions.


## FullEditMode2 readback

`unity-completion-edit-final2.xml`: 8273 actual rows=8258 Passed/15 Skipped/0 Failed, process exit 0.
Every mapped method for 19 original EditMode finding sets is present and Passed. Benchmark remains
PlayMode-only and PR-14 docs-only. UI 20/20, typing 2/2, grouped-Hub/language 4/4, Hub Save 22/22, real-VM
adapter 22/22 and both guard fixtures pass. The 15 exclusions are 10 native allocation-accounting,
3 legacy local 4B explicit and 2 optional local Roblox-docs-mirror cases. They remain skipped;
OpenRouter-only policy is preserved. Root acceptance, API/hermetic and final export gates remain
pending explicit root decision and current receipts; no earlier negative run is relabeled.
