# Hub repair work log

Baseline: `69d5bcc37502cd4bb464a3bf9ac0c53b1624b804`. Target release: `7.54.0`.

## Scope and ownership

Repair optional Hub lifecycle, supplied-context isolation, draft preservation, and truthful operation outcomes. Authentication, account detection, matchmaking, and approved world mutation lifetime remain host policy. Preserve public APIs and optional assembly boundaries. No builds, tests, Unity operations, shared aggregate documents, versioning, or git mutations are performed by this worker.

The existing page registry, runtime adapter, routing controller, and per-operation world write receipts are the reuse seams. Local UI repairs do not need new dependencies. Source inspection is evidence for a proposed fix, never an executed regression result.

## Finding review and acceptance

| ID | Reproduce and existing guard review | Initial disposition | Regression acceptance | Proposed files |
| --- | --- | --- | --- | --- |
| HUB-001 | Disable/re-enable a window after resolving a singleton factory page; permanent destruction prevents reuse. Idempotent destruction alone does not allow reuse. | confirmedfix, pending source recheck | Hide suspends pages without destroying registry-owned instances; final teardown destroys exactly once; outstanding old work cannot mutate a replacement. | CoreAiHubWindow.cs, HubModsPages.cs, Hub lifecycle fixtures |
| HUB-002 | Replace registry A with B using the same page ID. Existing pruning keys only by ID. | confirmedfix, pending source recheck | A is deactivated/destroyed; B factory/content is used; a queued A notification cannot tear down B. | CoreAiHubWindow.cs, HubContractRegressionTests.cs |
| HUB-003 | Source store throws on Save/Delete/SetActive, or supplied service returns false on Delete. Existing catches hide receipts. | confirmedfix, pending source recheck | Failures never claim saved/deleted; live versus stored outcomes remain distinct; recovery can retry. | HubModServiceBase.cs, IHubModService.cs, HubModsPage.cs, Mods Hub fixtures |
| HUB-004 | Open A, type a draft, externally save B, then save A or leave editor. Refresh suppression preserves typing but not saved-source isolation. | confirmedfix, pending source recheck | External B remains until explicit overwrite; failed/conflicting drafts survive Back and window disable; replacement page work is fenced. Cross-thread compare-and-swap requires an owner contract if absent. | HubModEditorPage.cs, HubModsPage.cs, LuaSyntaxHighlighterEditModeTests.cs |
| HUB-005 | A failed/throwing confirmation is immediately followed by inventory refresh. Separate outcome label exists but is not used for failures. | confirmedfix, pending source recheck | Failure/cancellation remains readable after finally and later refresh; subsequent decision can replace it. | HubModsPages.cs, CoreAiModsHubBinderFullTierEditModeTests.cs |
| HUB-006 | Complete a failed endpoint save, or A save after switching to B. Destruction cancellation does not fence editor/controller generations. | confirmedfix, pending source recheck | Failed inputs/key survive; stale completion cannot overwrite B or change newer busy state; queued controller changes cannot reattach destroyed views. | HubSettingsPage.cs, HubSettingsRegressionEditModeTests.cs |
| HUB-007 | Repeated registration creates runtime adapters whose four subscriptions have no release path. Page unsubscribe does not release adapter. | confirmedfix, pending source recheck | Owned registration releases subscriptions on replacement/unregister/binder teardown; caller-owned services remain usable. | LuaCsModRuntimeHubService.cs, HubModsPages.cs, CoreAiModsHubBinder.cs, Mods Hub fixtures |
| HUB-008 | Arm check returns true after a failed local write or is described as browser flush completion. Existing false path does not prove per-operation success. | confirmedfix, pending incoming receipt source recheck | Consume IWorldStateWriteResults receipts; failed/unsupported writes never turn green; true means local operation plus automatic persistence armed, never browser commitment. | WorldStateHubPage.cs, Hub world-state fixtures |
| HUB-009 | Default Escape collapses while all top-level tabs/toggle are nonfocusable and no reopen key is assigned. | confirmedfix, pending source recheck | Tabs/toggle support keyboard focus and meaningful labels; collapse/reopen restores useful focus without accidental gameplay submission. | CoreAiHubWindow.cs, CoreAiHubWindowEscapeEditModeTests.cs |
| HUB-010 | Setup menu loads fixed Assets paths and writes into a possibly absent package folder. Runtime remains usable through explicit composition. | confirmedfix for optional Editor setup | Package-aware source resolution; explicit generated Assets destination; missing assets/save failure stop before instantiation. Root owns isolated UPM mutation validation. | CoreAiHubSetupMenu.cs, Hub README, HubContractRegressionTests.cs |

## Verification handoff

Root runs compile and EditMode gates serially. Planned filters: HubContractRegressionTests, HubPageRegistryEditModeTests, CoreAiHubWindowEscapeEditModeTests, HubSubTabLifecycleEditModeTests, HubSettingsRegressionEditModeTests, WorldStateHubPageEditModeTests, HubWorldStatePageEditModeTests, CoreAiModsHubBinderFullTierEditModeTests, LuaSyntaxHighlighterEditModeTests. UI panel/input and UPM fixture checks require root's authorized Unity operator. No execution results yet.

## Progress

- Read AGENTS.md, architecture rules, and the assigned audit. Initial ownership/disposition and regression acceptance recorded before source edits. Historical audit remains unchanged.

## Continuation checkpoint - 2026-10-25

Before planning: finish the existing Hub repair in its assigned files and make source-save receipts honest. Required invariants are optional composition, registry/view lifetime isolation, preserved dirty buffers, observable local-write/arming failure, retry without repeated runtime mutation, C# 9 compatibility, and English prose. Authentication and world mutation lifetime remain host-owned. No test, build, Unity, Git mutation, version, metadata, or aggregate-doc work belongs to this worker.

Before accepting: prior source changes and fixtures require a fresh diff review. The approved optional `ILuaModSourceStrictWriter.SaveStrict` contract is now present in the Mods-owned interface/backend; `LuaModSourcePersistenceException.LocalCommitCompleted` distinguishes local installation from automatic-persistence refusal. Legacy `Save` returning, or a successful readback, cannot acknowledge persistence. Hub must keep failed/unconfirmed editor drafts dirty and preserve the updated live baseline for retry. This checkpoint claims no execution result; the root owns all gates and the Unity startup failure remains unverified compilation/runtime acceptance.

## Source-final review - 2026-10-25

Status: source-final for root acceptance, with execution gates still open. The initial table above records intake, not the final disposition. The inherited native-worker request is model gpt-6.1-sol and reasoning effort max; this worker did not independently inspect model-service metadata. Read-only Git inspection was explicitly authorized by root; no Git mutations occurred.

Before delegation: no additional agents were delegated. The root's file lease and acceptance invariants controlled this continuation. Root owns shared build/test output, Unity, release/versioning and publication. Before accepting: the real scoped source/test diffs were read, the public persistence seams were reconciled with Mods, and actual saved root receipts were inspected. No prose or source review is presented as an executed UI regression.

| ID | Final framework disposition and implementation | Regression source and remaining acceptance |
| --- | --- | --- |
| HUB-001 | confirmedfix: window disable deactivates and preserves cached pages/content; final destruction is separate. The World Loads registration creates a new page after final destruction rather than reviving old asynchronous work. | HubContractRegressionTests.Window_DisableReenable_PreservesPageAndDraftContentUntilFinalDestruction and CoreAiModsHubBinderFullTierEditModeTests.WorldLoadRegistration_RevivesWithANewPageAndFencesAnOutstandingOldDecision. Actual panel/window lifecycle gate remains open. |
| HUB-002 | confirmedfix: assigning a new registry destroys old page/content caches even for matching IDs; cached factory identity and view generation fence queued old notifications. | HubContractRegressionTests.Window_RegistrySwapWithSameId_FencesAlreadyQueuedOldNotification. Execution remains open. |
| HUB-003 | confirmedfix: source saves require optional strict acknowledgment; legacy storage is Unconfirmed. Local installation versus arming refusal is explicit, exact persistence retry avoids repeated live execution, and active-state saves preserve the exact manifest. Strict deletion acknowledges local absence plus arming, including an already-absent retry; legacy deletion remains false/unconfirmed. | HubService_SaveRefusalReportsLiveAndStoredStateSeparatelyAndCanRetry, HubService_ArmingRefusalReportsLocalCommitAndIdenticalRetryDoesNotReload, HubService_LegacyStoreReadbackNeverAcknowledgesPersistence, strict cancellation/active-state/deletion cases, and real FileLuaModSourceStore strict mutation cases. These latest additions require root execution. |
| HUB-004 | confirmedfix: expected-source comparison blocks interleaved external edits until explicit overwrite of the latest reviewed source. Dirty buffers survive Back and window disable; destroyed editors cannot save. Failed/unconfirmed unchanged buffers remain dirty, and a failed/cancelled operation that already changed live source can retry without a false conflict. | HubModPagesRefreshEditModeTests.ModEditor_ExternalEditsRequireAnExplicitOverwriteOfTheLatestReviewedSource, ModEditor_TwoEditorsCannotSilentlyReplaceEachOthersSavedDraft, failure/unconfirmed/cancel/retry and destruction cases. Cross-thread storage compare-and-swap is not supplied or claimed. Draft survival across deliberate registry/window destruction remains documented host policy. |
| HUB-005 | confirmedfix: decision/autosave failure and cancellation use the persistent outcome element; pending inventory refresh cannot replace it. | WorldLoadPage_FailedDecisionOutcomeSurvivesFinallyAndLaterInventoryRefresh covers failed result, exception, cancellation and expired token; WorldLoadPage_AutosaveRequestFailureRemainsVisibleAfterRefresh covers autosave errors. Execution remains open. |
| HUB-006 | confirmedfix: endpoint/controller/editor/operation generations reject stale completions; NotSaved retains all entered fields/key. An old operation cannot release a newer busy state, and queued controller changes cannot reattach destroyed pages. | HubSettingsRegressionEditModeTests.EndpointSave_NotSavedPreservesTheEditedDescriptorAndRetryKey, EndpointSave_OldCompletionCannotOverwriteAnotherDraftOrReleaseItsBusyState, controller replacement, destroyed-view and queued-listener tests. Execution remains open. |
| HUB-007 | confirmedfix: runtime adapters implement idempotent disposal for all four subscriptions. Owned registration lifetimes release adapters on replacement/removal/binder teardown; factory-token removal cannot unregister a successor. Caller-supplied services remain caller-owned. Retained retry sources are invalidated by runtime mutation and cleared on adapter disposal. | HubPageRegistryEditModeTests owned-lifetime cases, HubRuntimeRegistration_RepeatedReplacementAndRemovalReturnAllListenerCountsToBaseline, and HubCallerOwnedService_SurvivesEitherSharedPagesDestructionAndUnregistration. Only the portable registry fixture has a saved execution receipt. |
| HUB-008 | confirmedfix: WorldStateHubPage consumes the specific IWorldStateWriteResults receipt; failed/unsupported operations cannot be upgraded through global ConfirmDurability. Success says local completion and automatic persistence armed, explicitly withholding browser completion. | HubContractRegressionTests world receipt/status/exception/cancellation cases and reconciled CoreAiWebGlQaEditModeTests world cases, including pending synchronous operation/reentrant writes. Real browser reload remains a separate gate. |
| HUB-009 | confirmedfix: tabs and collapse/expand support focus, directional navigation and labels; Escape puts focus on Expand and submit restores the active tab. Pointer selection releases navigation-button focus to avoid later gameplay submission. | HubContractRegressionTests.Window_KeyboardMovesTabs_AndEscapeThenSubmitReopensWithRestoredFocus uses an attached panel. Real keyboard/controller/touch/accessibility acceptance remains open. |
| HUB-010 | confirmedfix for optional setup: required resources resolve through installed package/source paths; generated assets use Assets/CoreAIGenerated/Hub; resource/save failure stops before null prefab instantiation. Qualified PackageManager.PackageInfo removes the actual Unity 6000.6 ambiguity. | HubContractRegressionTests.Setup_ResourceResolutionSupportsSourceAndInstalledUpmPaths is a pure path test. Actual UPM setup mutation/missing-resource/refused-save execution remains open; existing known debt is not duplicated. |

## Approved cross-module persistence addendum

Root explicitly extended this worker's lease after module 05 settled to ILuaModSourceStore.cs, FileLuaModSourceStore.cs, FileLuaModSourceStoreEditModeTests.cs and the source-store guide section. The earlier module-05 source hash snapshot is superseded for those four files by the manifest below. No other Mods runtime files were edited.

The existing ILuaModSourceStrictWriter.SaveStrict contract is consumed without changing any legacy method signature. The additive ILuaModSourceStrictRemover.DeleteStrict contract separates acknowledged removal from best-effort Delete. FileLuaModSourceStore implements both; its typed IOException-derived LuaModSourcePersistenceException exposes LocalCommitCompleted for write/removal completion before arming refusal. Strict saves/removals propagate cancellation separately. A locally absent removal retry still requests arming, and unreadable legacy identity refuses strict deletion rather than treating the package as absent.

HubModPersistenceStatus preserves NotConfigured=0, Saved=1 and Failed=2 and appends Unconfirmed=3. Existing HubModSaveResult constructors remain source/binary compatible; the four-argument constructor retains its explicit legacy success claim. A new overload carries LocalCommitCompleted. Built-in services never infer Saved from best-effort Save or readback. A pending exact-source save retries persistence without reloading while that live source remains current; later runtime mutation invalidates that receipt. Enable/Disable use strict source+manifest writes when possible; legacy configured writes report unconfirmed. All browser wording distinguishes arming from IndexedDB completion.

Real file-source regressions now include refused save arming/incomplete local writes from module 05, plus strict removal arming and already-absent retry, unreadable identity preservation, and save/removal cancellation retry. Hub fake/service/editor cases verify failure receipts, no duplicate load/reload/unload, dirty-buffer preservation, legacy unconfirmed outcomes and recovery.

## Actual checks and receipts

- This worker ran read-only source/diff/status inspections and static checks only. Scoped git diff --check exited 0. Added source has zero explicit-type violations from C# var declarations, zero Cyrillic lines and zero unapproved line-comment prefixes. The untracked HubContractRegressionTests.cs has zero trailing-whitespace lines and a final newline. Git emitted a CRLF-to-LF normalization warning for the already-modified file-store source; that is not a compiler/test receipt.
- The saved root core receipt artifacts/fix-wave-20261025/core-results/core.trx was inspected at its 2026-10-25T09:01:24.8610999Z write timestamp: 2,839 UnitTestResult entries, 2,833 Passed / 3 Failed / 3 NotExecuted. Mapping testId to TestDefinitions identifies nine HubPageRegistryEditModeTests results, all Passed. The three failures are two benchmark assertions and an old SSE skip assertion; root reported corrections, with a new result still pending at this checkpoint. These counts are the saved receipt, not a new run or acceptance of later Hub/Mods changes.
- Root's actual recovered Unity log artifacts/fix-wave-20261025/unity-editmode-recovered.log reached source compilation and reported the Hub setup PackageInfo ambiguity. It was corrected by fully qualifying UnityEditor.PackageManager.PackageInfo. Other compile errors in that receipt belonged to other owners. This supersedes the initial UDS-only observation, but still establishes no successful compile or executed Unity tests.
- Root supplied an actual Lua receipt artifacts/fix-wave-20261025/lua-full-results/lua-full.trx with 1,925 Passed / 3 Failed / 41 NotExecuted. This worker inspected the failing admission result: the test expected best-effort Delete to erase unreadable legacy identity. The source/test correction preserves corrupt bytes and occupied capacity, then explicitly repairs only the fixture's temporary corrupt directory before the original admission/save assertions. The other two backup-cancellation expectation failures belong to another owner. A corrected rerun is required.

Before accepting: the saved failures were treated as failures, not bypassed. Incomplete UI/player gates remain incomplete. The root must read the final real diff, reconcile the changed module-05 hashes, run the focused and integrated checks against this final source, and obtain its independent review.

## Root verification handoff

Run serially through the root's existing Unity/portable harness; this worker did not execute these commands:

```powershell
dotnet build CoreAI.Hub.UI.csproj --no-restore
dotnet build CoreAI.Hub.UI.Editor.csproj --no-restore
dotnet build CoreAI.Mods.Hub.csproj --no-restore
dotnet build CoreAI.Tests.csproj --no-restore
dotnet build CoreAI.Mods.Tests.csproj --no-restore
dotnet test tools/portable/Tests/CoreAI.Portable.Tests.csproj --filter FullyQualifiedName~HubPageRegistryEditModeTests
dotnet test tools/portable/LuaTests/CoreAI.Portable.LuaTests.csproj --filter FullyQualifiedName~FileLuaModSourceStoreEditModeTests
```

The actual portable link is HubPageRegistryEditModeTests.cs in tools/portable/Tests/CoreAI.Portable.Tests.csproj. HubContractRegressionTests uses UnityEditor/UITK and is Unity-only; it must not be claimed portable. Confirm the file-source fixture is included in the selected Lua project and inspect TRX UnitTestResult entries, not only console summaries.

Focused Unity EditMode filter: CoreAI.Tests.EditMode.HubContractRegressionTests;CoreAI.Tests.EditMode.HubPageRegistryEditModeTests;CoreAI.Tests.EditMode.CoreAiHubWindowEscapeEditModeTests;CoreAI.Tests.EditMode.HubSubTabLifecycleEditModeTests;CoreAI.Tests.EditMode.HubSettingsRegressionEditModeTests;CoreAI.Tests.EditMode.WorldStateHubPageEditModeTests;CoreAI.Tests.EditMode.HubWorldStatePageEditModeTests;CoreAI.Tests.EditMode.CoreAiWebGlQaEditModeTests;CoreAI.Tests.EditMode.CoreAiModsHubBinderFullTierEditModeTests;CoreAI.Tests.EditMode.LuaSyntaxHighlighterEditModeTests;CoreAI.Tests.EditMode.HubModPagesRefreshEditModeTests;CoreAI.Tests.EditMode.FileLuaModSourceStoreEditModeTests.

Not done: successful Unity compilation/tests after the latest corrections; actual UI input/panel lifecycle; isolated UPM menu execution and failure injection; built-player/IL2CPP/WebGL/browser reload/device/network/performance validation; release/commit/tag/push. No claim closes those gates. No new .meta, .unity, .prefab, asmdef, package/version, changelog, root PLAN/TODO or historical-audit changes were made by this worker.

## Source-final SHA-256 manifest

These hashes identify the 23 source/test/package-doc files at this checkpoint, including inherited Hub repairs and the four explicitly extended Mods files. They identify source bytes only and do not bind earlier executable receipts to these bytes. This work log cannot include a self-hash; root can record its final hash externally.

| File | SHA-256 |
| --- | --- |
| `Assets/CoreAI/Runtime/Core/Features/Hub/HubPageRegistry.cs` | `5e95ed24018f08b8de33759ba814f997749cc79f60f7ed1c14e6388545bdf3cc` |
| `Assets/CoreAIHub/Editor/CoreAiHubSetupMenu.cs` | `7f6b724380126a035954d82259dd1ffd5997f9d149da007e042141546f6c2266` |
| `Assets/CoreAIHub/README.md` | `269271a5d23fe3b847403b6cfb8c52e5c272f7a063507dc482bb3931ec0904fb` |
| `Assets/CoreAIHub/Runtime/CoreAiHubWindow.cs` | `9cddab51c656c714d70759ea3162efd06302fa9c4e35b995e539f21b8bfc4261` |
| `Assets/CoreAIHub/Runtime/HubSettingsPage.cs` | `4d9ee39d2aac11a89da46157c5ad67e80418f6d4d51f7933d5f4e61cb5e8b135` |
| `Assets/CoreAIHub/Runtime/WorldStateHubPage.cs` | `63cbf60db203ee373661113d1f5662dd7e088e1a238e9eb91a6791beed293ae6` |
| `Assets/CoreAIMods/Runtime/HubIntegration/CoreAiModsHubBinder.cs` | `082ce057e48e8043f8cf0eaccfe7f95eeacb13aa5424aba3bf233521712adbb4` |
| `Assets/CoreAIMods/Runtime/HubIntegration/HubModEditorPage.cs` | `9e4399b70f888c33bf429aa4047a809b6de13956513a990e07fafe0a8786905a` |
| `Assets/CoreAIMods/Runtime/HubIntegration/HubModServiceBase.cs` | `8deb427be78b612cb0687053f23d201013eed54a59e5f4a1f10718fa378583e2` |
| `Assets/CoreAIMods/Runtime/HubIntegration/HubModsPage.cs` | `1be8370a6ce010c7bb3364d5c85290d04690b0595a66737d7982193f3f1eb23c` |
| `Assets/CoreAIMods/Runtime/HubIntegration/HubModsPages.cs` | `d6db9bd596f19ecdef7f549efd870a97e049fa43cc5324ecfcb7b39a53d199d3` |
| `Assets/CoreAIMods/Runtime/HubIntegration/IHubModService.cs` | `a1c27bb5a1f0b4011353f1b2f2d890376335c0da20ad3ffe654914cf87399425` |
| `Assets/CoreAIMods/Runtime/HubIntegration/LuaCsModRuntimeHubService.cs` | `cc1910a4ba74fcb4263a563b9c9be99cc7b88e2dd60b2445be56f1f73165c2eb` |
| `Assets/CoreAIMods/Runtime/LuaExecution/ILuaModSourceStore.cs` | `18f400a2e96f9779b4316bcbceb92934c78b82abd9c644e166985b21e5c2782e` |
| `Assets/CoreAIMods/Runtime/Infrastructure/FileLuaModSourceStore.cs` | `f7041ddf10d06f08653c52e3ff7043ef7e31ffd6dc00575b12348831565a8ded` |
| `Assets/CoreAIMods/Runtime/LuaExecution/README.md` | `b7b8853cd210a3dfda72eb34b16076df29ba4197a1d5f93bf9939d49ebcc1b40` |
| `Assets/CoreAIMods/Tests/EditMode/CoreAiModsHubBinderFullTierEditModeTests.cs` | `11ffe915681b5807e408652913fed15c76a7b3604c239f59ecddaa3968b22d8d` |
| `Assets/CoreAIMods/Tests/EditMode/LuaSyntaxHighlighterEditModeTests.cs` | `7ffd1287eac0d9f0b5d1adb63a0c999cd1025a3037ddab2670242ea99a0388f4` |
| `Assets/CoreAIMods/Tests/EditMode/FileLuaModSourceStoreEditModeTests.cs` | `6b1828a6f75d469087942724d6e5cc6a64b034a124f7b83acdd8670f751d53cb` |
| `Assets/CoreAiUnity/Tests/EditMode/HubContractRegressionTests.cs` | `b6925b1a8e156e941b397ce5991500e3070c10f47e715868839d96ba9f4391ec` |
| `Assets/CoreAiUnity/Tests/EditMode/HubPageRegistryEditModeTests.cs` | `cfac20877a4cdd63c371792ecb38ea3b9b38aa76c603edd8bb5f185e896d010f` |
| `Assets/CoreAiUnity/Tests/EditMode/HubSettingsRegressionEditModeTests.cs` | `d16e6e49437c9675648541eb87b118175d18332158aac4dd84ae5a0bba1dc8e0` |
| `Assets/CoreAiUnity/Tests/EditMode/CoreAiWebGlQaEditModeTests.cs` | `441d15e049c3b7f4679d6fb7823a2ab2e97c3832ed23af557ac25ca7097b4d8d` |
