# Editor and grouped Hub native completion

Worker: `/root/coreai_editor_finish`. Requested model: `gpt-6.1-sol`; requested reasoning effort: `max`.

Current state: the full EditMode gate passed the language/grouped-Hub and typing fixtures and exposed two incorrect fixed-four-space Tab expectations among the editor UI cases. Those two expectations are corrected below; source is frozen for root's focused and full reruns. This worker has not run Unity or claimed a fresh pass for the corrected assertions.

## CURRENT TAB-STOP FOLLOW-UP

- Root's full EditMode result was 8,268 total: 8,239 passed, 14 failed, 15 skipped. The language/grouped-Hub fixture passed all four cases; typing passed both cases; the editor UI fixture passed 18 of 20. The only remaining editor UI failures are recorded in `artifacts/pr-review-20261025/unity-completion-edit-final1-failures.json`.
- Both failures expected `replacement` plus four spaces but observed `replacement` plus one space. Native Tab now reaches the edit path correctly. `CodeEditorTextUtility.IndentUnit` contains four spaces, and `InsertTab` advances to the next four-column stop; it does not insert a fixed four spaces at every column. At column 11 the next stop is 12, so one space is correct. No production change is needed.

| Boundary fixture | Exact starting column | Literal expected spaces | Resulting caret/anchor |
| --- | --- | --- | --- |
| Python warning draft after language switch | 0, after its terminal newline | 4 | Source length plus 4 |
| `replacement` after source replacement | 11 | 1 | 12 |
| `local` after panel reattach | 5 | 3 | 8 |

- The test contains independent literal expectations for those three cases. It checks each fixture's initial column using only string positions, sends the unchanged native Tab event, compares the complete source, and verifies the exact collapsed caret and anchor. It does not call the implementation utility to manufacture expected text.
- `ExplicitSelection_ClosesOldCompletionBeforeNativeTab` now compares the literal `replacement ` and asserts caret/anchor 12. Its popup-close assertion and native input remain unchanged. Other assertions, input helpers, waiting thresholds, and production code are unchanged.
- Current UI fixture SHA-256: `69C6FA34C9489AAF71FC603FF2CC26B8ACF901273197DCEEB2F3F699E402BD6C`. `CodeEditorView.cs` is unchanged at `E9B7DE6FFE692F654435FA079570E35061D9360B18F929F5F6F52CD865649D01`.
- Root owns the focused and full reruns. The separate Hub service/mock and guard failures in the full result belong to other root-assigned scopes and were not edited by this worker. The earlier correction history below remains provenance, not a fresh pass claim.

## CHANGED

- `CodeEditorView.cs`: an explicit selection pauses an obsolete pending completion request when the native selection actually changes. An open popup is closed only if its source, generation, caret, or collapsed-selection snapshot no longer matches the requested selection. The internal native-cache restoration path remains unchanged and does not dismiss a matching popup.
- `CodeEditorView.cs`: completion acceptance now returns success. Rejected stale completion closes its popup without changing source/history; Tab and Enter continue into normal editor handling. Keyboard acceptance consumes a key only on success. Pointer acceptance suppresses focus processing only on success and still stops the popup click from propagating.
- `CodeEditorUiEditModeTests.cs`: both native left-arrow events now include Unity's required `FunctionKey` modifier. The exact original caret/source assertions remain. Added native regressions for explicit selection invalidating an old popup and immediate native typing followed by Tab before presentation refresh.
- `CodeEditorLanguageAndHubNativeEditModeTests.cs`: the real registered Mods Add button receives focus and an explicitly targeted native navigation-submit event. A temporary observer asserts that its existing production click action fires exactly once; the original non-null editor, grouped page, Escape, Logs, disposal, and collapse assertions remain intact.
- The promoted quote, escape-parity, tracker insertion-hint, native cache, detach, Enter/undo, Lua length-operator, Python-comment, bounds, and independent-language-history implementations were not changed in this final correction. No Core utility, Hub production composition, registry, scene, prefab, meta, Git state, Unity process, or MCP state was modified by this worker.

## VERIFY OUTPUT

Parent receipt inspected in full: `artifacts/pr-review-20261025/unity-completion-focused3.xml`, 154 cases: 145 passed, 5 failed, 4 skipped. It predates the final source changes.

| Failed case | Concrete cause and evidence | Final correction |
| --- | --- | --- |
| `NativeBrackets_AndCompletionUseBoundedPopup`: expected `local`, actual `loc` | `SetText` can synchronously open a completion at the previous native caret. The later explicit selection left that popup visible, so the test could see it before the deferred Ctrl-Space refresh. `AcceptCompletion` rejected its stale caret snapshot, but its caller unconditionally swallowed Tab. | Invalidate mismatching popup on explicit selection; consume the accept key only after a successful completion. |
| `CompletionKeyPair_DoesNotSuppressTabAfterDocumentOrPanelChange`: expected `replacement` plus four spaces, actual unchanged `replacement` | On source replacement, presentation could open a completion for a shorter prefix at the old caret. Moving to the new source end left that popup visible. Generation invalidation already cleared split-key state; the rejected stale popup was the remaining key consumer. | Same popup ownership/fallback correction, with a direct selection-to-Tab native regression. |
| `ProgrammaticSelectionAfterLayout_DoesNotOverrideFollowingNativeNavigation`: expected caret 18, actual 19 | Installed Unity 6000.6 `TextEditingUtilities` maps `MoveLeft` to `LeftArrow` with `EventModifiers.FunctionKey`. The fixture sent an unmodified synthetic arrow, which is not that native editing event. | Correct the two arrow stimuli; retain caret 18 and caret 4 expectations and following native character assertions. |
| Both grouped Hub Escape cases: Add setup left `_editor` null | Actual `HubModsPages.RegisterService` builds `HubSubTabPage` with a real `HubModsPage` child and its existing Add callback. Installed `NavigationEventBase.Dispatch` routes an event with no explicit target to the currently focused leaf or panel root, irrespective of the element whose `SendEvent` is called. The fixture neither focused Add nor supplied `submit.target`. | Focus the real Add button, settle focus for one frame, target it explicitly, and verify one production click before reading the real editor. |

Read-only installed-source evidence:

- `UnityEngine.TextSelectingUtilities` and `UnityEngine.TextEditingUtilities` key maps require `FunctionKey` for native arrow movement.
- `UnityEngine.UIElements.NavigationEventBase<T>.Dispatch` calls `DispatchToFocusedElementOrPanelRoot`; that method uses the explicit target first, otherwise the focused leaf/panel root.
- `Button.OnNavigationSubmit` calls `Clickable.SimulateSingleClick`, which invokes its click callback immediately. `HubModsPage.OpenNewEditor` and `OpenEditor` build the actual editor synchronously. The fixture correction is input targeting and focus settlement, not a speculative action delay or a fake handler.

Reviewed the complete task-local before/after diff: view +16/-7 lines; UI fixture +45/-2; grouped Hub fixture +19/-1. All existing assertions remain. The only removed native stimuli are replaced by the same arrows with their actual native modifier, and the untargeted Add submit is replaced by focused/targeted submit plus stronger click evidence.

`git diff --no-index --check -- NUL <file>` emitted no whitespace diagnostics for all three C# files. Each command returned 1 because these files differ from NUL; no compiler or test pass is inferred from that result.

Frozen SHA-256 values:

- `Assets/CoreAIMods/Runtime/HubIntegration/CodeEditing/CodeEditorView.cs`: `E9B7DE6FFE692F654435FA079570E35061D9360B18F929F5F6F52CD865649D01`
- `Assets/CoreAIMods/Tests/EditMode/CodeEditorUiEditModeTests.cs`: `69C6FA34C9489AAF71FC603FF2CC26B8ACF901273197DCEEB2F3F699E402BD6C`
- `Assets/CoreAIMods/Tests/EditMode/CodeEditorLanguageAndHubNativeEditModeTests.cs`: `822FB14B923857393C3EFB196EFC14F54FD6E35520E2BF4AB7DB6AE7E232F9DC`

## NOT DONE

- Root owns the focused rerun for the two corrected Tab expectations in `CoreAI.Tests.EditMode.CodeEditorUiEditModeTests`, followed by the complete graphics-capable fixture selection and full suites.
- Root must record fresh counts and receipt paths against the frozen source hashes before accepting the editor/Hub native gate. Earlier passed quote/closer/detach cases remain evidence for the previous source only until this rerun.

## CONCERNS

No unresolved source finding remains from this worker's review. Fresh compile/native execution remains a required gate. There is no new timer, refocus retry, private Unity runtime reflection, source assignment substituted for native input, or weakened threshold/assertion in this correction.

## QUESTIONS

None. Root accepted the concrete semantic causes and owns the final execution gates.
