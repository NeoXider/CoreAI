# Group 09: Hub and UI independent PR review

Status: complete static review. All 25 assigned paths and all 183 diff hunks (Git unified context 3) were read: 3,382 additions and 403 deletions. Three actionable introduced defects are submitted for root adjudication: two P2 and one P3. No executable proof or acceptance is claimed.

Baseline: `69d5bcc37502cd4bb464a3bf9ac0c53b1624b804`. Candidate: `bc3fd74f0a889bfe1b1e7babc6bc02ed48a7c05e` (pinned source and line numbers).

Review goal: actionable defects introduced by candidate changes, with particular attention to page/adapter ownership, stale asynchronous completions, persistence truthfulness, retry baselines, supported public APIs, input accessibility and runtime package paths. Host account transitions and documented local/shared-world defaults are not defects by themselves.

Rules read: `AGENTS.md` and `Docs/ARCHITECTURE_RULES.md`. Only this report may be written. No builds, tests, Unity/MCP, network/model calls, additional agents or git mutations. Existing working-tree changes and extra review reports are outside candidate source scope.

An initial scope/header was written before deep reading. The intermediate checkpoint recorded source-hunk completion and pending test/document/contract reads. Those reads are now complete. Historical repair claims and green aggregates were not treated as proof of candidate correctness.

## Findings

### PR-09-001 [P2] Preserve the World-page binder when generating the setup prefab

- **Pinned changed location:** `Assets/CoreAIHub/Editor/CoreAiHubSetupMenu.cs:20` (new prefab destination); generation at lines 29-34 and component construction at lines 85-112 establish the causal path.
- **Valid trigger:** use Add Hub in the supported source-layout project with CoreAI Mods and a configured `IWorldStateManager`, before `Assets/CoreAIGenerated/Hub/CoreAiHub.prefab` exists. The baseline loaded the shipped `Assets/CoreAIHub/Runtime/CoreAiHub.prefab`. That unchanged prefab contains `WorldStateHubBinder`; its script GUID matches the binder metadata. The candidate instead constructs a new prefab containing only UIDocument, window and demo bootstrap, then adds only `CoreAiModsHubBinder` to the instance.
- **Expected / actual:** the setup-created Hub should retain its existing World tab and Save Now / Reset World controls when the world manager is available. The generated instance has no `WorldStateHubBinder`, so nothing in this setup composition registers `WorldStateHubPage`. `CoreAiHubDemo`/`HubBuiltInPages` register Chat, Settings and Statistics; the Mods binder registers Mods and World Loads, which is a separate confirmation page. The World tab disappears on the new setup path. This is a regression in an existing preset, not a demand that every framework host install world persistence.
- **Root cause:** moving the default load path away from the shipped prefab makes the previously fallback builder the normal path without preserving the shipped prefab's optional component wiring. The README still describes the shipped World-state integration. Do not infer loss of periodic autosaving: the adjacent auto-save hook explicitly says the manager already owns its default loop.
- **Minimal regression proof plan (not run):** in an isolated Editor fixture with Mods installed and no generated prefab, invoke Add Hub, inspect the created instance for `WorldStateHubBinder`, and then assert its registry contains `WorldStateHubPage.DefaultPageId` against a fake/resolved world manager. Exercise the same setup with the optional module absent to preserve optional composition. The existing path-string test and shipped-prefab test do not exercise the newly generated instance.
- **Uncertainty:** high confidence from the component/registration chain; no menu, asset import, scene lifecycle or UPM installation was executed.

### PR-09-002 [P2] Consume Escape only when the Hub actually handles it

- **Pinned changed location:** `Assets/CoreAIHub/Runtime/CoreAiHubWindow.cs:358` through line 360, especially the newly unconditional `PreventDefault` / `StopPropagation`.
- **Valid trigger:** set the supported `escapeCollapses` option to false, expand the Hub with cursor gating satisfied, and focus a control in a custom `IHubPage` that does not implement `IHubEscapeHandler` (or returns false). Give that control, or an enclosing host UI, its own Escape callback.
- **Expected / actual:** the documented opt-out disables Hub Escape handling, leaving Escape available to the focused UI/host. `HandleEscapeRequested` performs no page action and does not collapse, but `OnRootKeyDown` still consumes the event. Because the root registered this handler in trickle-down, the focused control's Escape callback and default Escape action are suppressed. Baseline handling did not stop propagation or prevent the default.
- **Root cause:** the event handler treats an eligible Escape event as consumed regardless of the outcome of the void `HandleEscapeRequested` call. The cursor/expanded guards do not establish that any action occurred.
- **Minimal regression proof plan (not run):** attach a custom page and focusable child to a real test panel; disable `escapeCollapses`, register an Escape callback on the child and a bubbling parent, and send Escape. Assert both still receive it and the Hub remains expanded. Retain positive cases where actual collapse or a true `IHubEscapeHandler` result consumes the event. Current new keyboard coverage tests only the collapse-enabled case.
- **Uncertainty:** high confidence in the explicit propagation controls and documented opt-out; physical keyboard/controller behavior was not executed.

### PR-09-003 [P3] Do not invalidate a pending save when the Advanced foldout opens

- **Pinned changed location:** `Assets/CoreAIHub/Runtime/HubSettingsPage.cs:580`, the group-wide `ChangeEvent<bool>` version increment. The completion fence at lines 951-955 and 1003-1007 exposes the effect.
- **Valid trigger:** start an endpoint save whose controller is waiting for readiness, then open or close the enabled Advanced foldout without editing any endpoint value. Complete that same save successfully through the controller.
- **Expected / actual:** changing presentation alone should allow the matching save receipt to update the status and clear an accepted session-key input. The foldout is inside `endpointGroup` and emits a boolean change, so the new blanket handler increments `_endpointEditorVersion`. `CanApplyEndpointResult` then rejects the successful completion. `finally` re-enables Save, while the operation label remains "Saving and waiting for readiness…" and the accepted key remains in the input. There is no replacement operation or changed descriptor to justify discarding this receipt.
- **Root cause:** the draft-generation fence counts a Foldout's presentation state as an endpoint edit. Restrict version changes to endpoint data inputs or otherwise exclude the foldout's own change event.
- **Minimal regression proof plan (not run):** use an attached panel and a controllable `TaskCompletionSource<CoreAiRoutingUiResult>` controller; enter a key, start Save, toggle the actual Advanced foldout, and complete with `SavedAndReady`. Assert the success message, cleared key and enabled Save button. Pair this with an actual descriptor edit during the wait and assert that edit remains protected. Existing asynchronous fixtures switch endpoint/controller or destroy the page; they do not cover foldout-only interaction.
- **Uncertainty:** static event-routing conclusion; the specific attached-panel Foldout event sequence remains for root to execute. No runtime proof is claimed.

## Uncounted hypotheses and limits

- Exact-source retry handling was traced through the pending receipt, live-source comparison, mutation invalidation and strict/legacy result branches. Direct Lua runtime source persistence is best-effort (`LuaCsModRuntime.PersistMod`, lines 5386-5420), so a store failure there alone does not establish the hypothesized missing-receipt/reexecution defect. No separate finding is counted.
- Two-window sharing of the World Loads singleton page is already present in the baseline. The new registration wrapper still merits composition coverage, but this review does not relabel the existing sharing issue as an introduced defect.
- Thread affinity of external events, simultaneous background writers, custom cancellation callbacks that throw, and real browser reload durability were not proved. The editor explicitly documents interleaved source comparison rather than storage compare-and-swap. These gaps are not scored bugs.
- Host authentication/account transitions, matchmaking, a host retaining views across accounts, repeated RoleIds and documented local/shared-world presets were not used as defect triggers.
- The new receipt wording correctly separates local installation and automatic persistence arming from browser completion. Legacy configured stores/managers remaining unconfirmed is documented behavior, not a finding by itself.
- The three historical reports were read in full as candidate documentation. Their test counts/hashes are historical claims; this review did not inspect or adopt their executable acceptance receipts. No actionable introduced documentation-only defect was counted.

## Path and hunk coverage ledger

Every row is complete. Numbers are candidate-side hunk starts from `git diff --no-ext-diff --unified=3 <baseline> <candidate> -- <path>`; `all` denotes the complete newly added file. The ledger accounts for every changed hunk, including removed code. Surrounding unchanged source was sampled where needed, rather than claiming an exhaustive review of every unchanged line in large existing files.

| Assigned path | Hunk count; candidate starts | Review focus / disposition |
| --- | --- | --- |
| `Assets/CoreAI/Runtime/Core/Features/Hub/HubPageRegistry.cs` | 3; 22, 43, 147 | Full candidate file plus diff; ownership transfer, disposal outside lock, conditional unregister. |
| `Assets/CoreAIHub/Editor/CoreAiHubSetupMenu.cs` | 4; 1, 10, 96, 111 | Full candidate file plus diff; package resolution, generated component composition, failure cleanup. PR-09-001. |
| `Assets/CoreAIHub/README.md` | 5; 27, 42, 137, 187, 198 | Every changed paragraph/API sample; lifecycle, strict receipts, keyboard/setup contracts. |
| `Assets/CoreAIHub/Runtime/CoreAiHubWindow.cs` | 19; 87, 110, 153, 212, 244, 272, 301, 333, 473, 492, 524, 561, 584, 601, 617, 642, 763, 864, 896 | Full candidate file plus diff; cache/factory identity, generations, lifecycle, focus and event propagation. PR-09-002. |
| `Assets/CoreAIHub/Runtime/HubSettingsPage.cs` | 17; 108, 158, 574, 620, 640, 655, 668, 718, 811, 862, 898, 925, 944, 1036, 1060, 1275, 1286 | All hunks; surrounding construction, controls, subscriptions, operation ownership and completion guards. PR-09-003. |
| `Assets/CoreAIHub/Runtime/WorldStateHubPage.cs` | 6; 13, 33, 141, 151, 212, 241 | Entire candidate operation/lifecycle/status code plus diff; local receipt versus global arming, exceptions/reentrancy. |
| `Assets/CoreAIMods/Runtime/HubIntegration/CoreAiModsHubBinder.cs` | 6; 1, 40, 51, 93, 112, 144 | Full candidate file plus diff; actor/store arguments, registration tokens, teardown and registered page set. |
| `Assets/CoreAIMods/Runtime/HubIntegration/HubModEditorPage.cs` | 12; 80, 137, 176, 197, 207, 345, 366, 408, 518, 555, 569, 584 | All hunks and surrounding complete construction/save/conflict/history/close paths; dirty state and retry baseline. |
| `Assets/CoreAIMods/Runtime/HubIntegration/HubModServiceBase.cs` | 10; 1, 18, 125, 230, 254, 346, 367, 485, 511, 626 | Full save/storage/lifecycle implementation and diff; strict/legacy receipts, retry invalidation, manifest fields, delete/toggle errors. |
| `Assets/CoreAIMods/Runtime/HubIntegration/HubModsPage.cs` | 15; 2, 30, 101, 179, 392, 410, 424, 453, 499, 544, 568, 584, 601, 632, 642 | All hunks; adjacent build/refresh/editor/action callbacks and subscriptions, destroyed-view fences. |
| `Assets/CoreAIMods/Runtime/HubIntegration/HubModsPages.cs` | 12; 67, 90, 104, 126, 199, 277, 325, 335, 414, 628, 732, 758 | Full candidate file plus diff; adapter ownership, page recreation, startup reads, actor forwarding and persistent outcomes. |
| `Assets/CoreAIMods/Runtime/HubIntegration/IHubModService.cs` | 3; 74, 128, 259 | All result/enum/API contract hunks and adjacent signatures; legacy overload compatibility and truthful states. |
| `Assets/CoreAIMods/Runtime/HubIntegration/LuaCsModRuntimeHubService.cs` | 3; 14, 43, 217 | Full changed adapter/lifetime code plus actor-forwarding primitive methods; listener cleanup and retry invalidation. |
| `Assets/CoreAIMods/Tests/EditMode/CoreAiModsHubBinderFullTierEditModeTests.cs` | 12; 1, 13, 34, 50, 153, 165, 769, 1010, 1019, 1121, 1134, 1230 | Every fixture/fake/assertion hunk; strict refusal/arming/cancellation, storage-only retry, world-page replacement, listener ownership. Not executed. |
| `Assets/CoreAIMods/Tests/EditMode/LuaSyntaxHighlighterEditModeTests.cs` | 8; 1, 106, 139, 160, 189, 351, 379, 398 | All added/changed fake and editor/CRUD cases, including unchanged-source failure dirty state and destroyed views. Not executed. |
| `Assets/CoreAiUnity/Tests/EditMode/CoreAiRoutingUiEditModeTests.cs` | 11; 16, 28, 69, 101, 128, 158, 203, 224, 243, 261, 362 | All ownership/teardown/helper/assertion hunks; manual EditMode lifecycle cleanup preserves checks. Not executed. |
| `Assets/CoreAiUnity/Tests/EditMode/CoreAiWebGlQaEditModeTests.cs` | 7; 1, 11, 34, 44, 75, 112, 157 | All replacement and removed callback-based test hunks; synchronous receipts, local inventory, reentrancy and honest retry. Not executed. |
| `Assets/CoreAiUnity/Tests/EditMode/HubContractRegressionTests.cs` | 1; all 1-334 | Entire new fixture; window harness, keyboard, registry swap, world receipts and source/UPM path test. Not executed. |
| `Assets/CoreAiUnity/Tests/EditMode/HubContractRegressionTests.cs.meta` | 1; all 1-2 | Complete metadata read; file format and GUID present. No Unity import validation. |
| `Assets/CoreAiUnity/Tests/EditMode/HubPageRegistryEditModeTests.cs` | 3; 81, 178, 191 | All lifetime/token/disposal-failure assertions and fake changes. Not executed. |
| `Assets/CoreAiUnity/Tests/EditMode/HubSettingsRegressionEditModeTests.cs` | 14; 16, 28, 58, 83, 139, 156, 170, 209, 235, 268, 303, 598, 664, 675 | All owned-page cleanup, async fakes, cancellation/controller/destruction and queued subscription cases. Not executed. |
| `Assets/CoreAiUnity/Tests/EditMode/HubSettingsStatisticsPageEditModeTests.cs` | 8; 3, 12, 25, 38, 62, 86, 111, 145 | All ownership helper, teardown and call-site changes. Not executed. |
| `dev-docs/audits/2026-10-23/08-hub-ui.md` | 1; all 1-176 | Entire historical audit read; original findings separated from candidate regressions. |
| `dev-docs/fix-wave-2026-10-24/08-hub.md` | 1; all 1-126 | Entire repair log, scope, receipt caveats, handoff and hash manifest read. |
| `dev-docs/fix-wave-2026-10-24/17-hub-final-integration.md` | 1; all 1-177 | Entire fixture-ownership investigation and bounded acceptance narrative read. |

## Contextual sampling and verification boundaries

Read adjacent contracts/composition only: full `IHubPage.cs`, `IHubEscapeHandler.cs`, `CoreAiHubDemo.cs`, `HubBuiltInPages.cs`, `WorldStateHubBinder.cs`, `WorldStateAutoSaveHook.cs`, `ILuaModSourceStore.cs`, `WorldStateWriteResult.cs` and Hub Editor asmdef; the shipped Hub prefab and World binder GUID; routing result/controller code through line 177; targeted runtime persistence lines 5386-5420 and declaration/call-site search results. These are contextual reads, not additional primary ownership. The shipped prefab has no baseline-to-candidate diff.

Not exhaustively audited: unrelated settings/backend/model discovery code, lower-level world/session/provider implementations, other tests, UI Toolkit internals, full UXML/USS layout/import behavior, all asset GUID references, external packages, or host account/session policy. No concurrency, performance, visual, accessibility, IL2CPP/device or browser behavior is established by source inspection.

Actually performed: read-only pinned `git show`, baseline-to-candidate `git diff` (including hunk inventory), `rg`/file discovery and inventory parsing. HEAD readback matched the candidate. One initial contextual `rg` used guessed/wildcard paths and returned path errors; corrected discovery located the runtime and storage contracts. Output truncation was resolved by bounded reads before marking coverage complete. No builds/tests, Unity/MCP/model/network calls, git mutation, source fixes or extra agents were used.

## Handoff

- **CHANGED:** only `dev-docs/pr-review-2026-10-25/09-hub.md`; incremental header and final independent review.
- **VERIFY OUTPUT:** 25 primary paths, 183 unified-context-3 hunks, 3,382 additions / 403 deletions accounted for by read-only inventory. The report check returned 25 ledger paths, 183 ledger hunks, no missing/extra paths, three finding headers, no trailing whitespace/Cyrillic and a final newline. Final HEAD still matched the candidate; status retained the preexisting QualitySettings/ETFX changes and untracked review directory. Three submitted findings; proof plans are unexecuted.
- **NOT DONE:** executable regression proofs, fixes, broader host/asset/runtime validation, or changes to aggregate review state. Root owns adjudication, proof and any follow-up changes.
- **CONCERNS:** new generated-prefab composition, Escape opt-out propagation, and presentation-only invalidation of endpoint receipts as detailed above; sampling limits remain explicit.
- **QUESTIONS:** none.
