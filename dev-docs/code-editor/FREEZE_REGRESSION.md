# Attached Hub mod-editor typing regression

Status: two attached-panel regression tests authored; the Paste interaction was corrected after root's first executed failure, and C# is returned/frozen for a targeted rerun. No test, build, Unity/MCP, profiler or input automation was executed by this worker.

## Contract and source evidence

Before planning: protect the reported edit-time freeze using the actual HubModEditorPage on an attached EditorWindow panel. Editing must keep the raw draft exact, produce the actual LuaSyntaxHighlighter output, and never implicitly run/reload/save/persist a mod. The regression itself must remain bounded if the runtime guard is removed.

Before delegation: this is an exclusive leaf task with no extra agent or runtime write authorization. Only the new `Assets/CoreAIMods/Tests/EditMode/HubModEditorTypingRegressionTests.cs` and this report were written. No metadata, settings, serialized manager, live service, VM, vendor code, other test file, Git state or Gateway staging was changed.

Before accepting: actual HubModEditorPage construction, BuildHighlightOverlay, Paste and diagnostics paths; LuaSyntaxHighlighter; IHubModService; existing fake patterns; and CoreAI.Mods.Tests assembly references/friend access were read. The page's live guard checks `ReferenceEquals(evt.target, _codeField)` before highlighting. The descendant rich-text Label is inside the TextField input tree and therefore must not be accepted as fresh Lua input. The highlighter delegates to LuaTokenizer/LuaRichTextFormatter, not a VM. Root owns the reported installed Label bubbling proof and the runtime guard; this worker supplied test coverage only.

## Authored fixture

`HubModEditorTypingRegressionTests` creates a real EditorWindow, adds the actual page Build result, waits a bounded thirty frames for a panel, and resolves the named source TextField and highlight Label from that live tree. No detached value setter stands in for the typing proof.

- `AttachedTyping_AndCaretKeepRawLuaAndNeverRunOrPersist` sends native KeyDownEvent input to the TextField's actual editable TextElement and asserts a change targeted at the attached TextField after every character of a small Lua draft. Exact field value, public DraftSource and real highlighter output are checked after each edit. Cursor selection/right-arrow input must preserve that draft and produce no source edit.
- `AttachedPaste_UnicodeMarkupAndBoundedLargeDraftStayRawWithoutExecution` invokes the page's actual focused Paste button through explicitly targeted NavigationSubmitEvent. It covers multiline comments, brackets, long-string syntax, literal rich-text-like input and supplementary Unicode, then a bounded roughly 3.5 KiB draft. The paste must generate an actual TextField change while preserving exact raw input and matching LuaSyntaxHighlighter.Highlight(raw).

The per-operation ancestor ChangeCeiling callback runs in trickle-down before the field's bubbling handler. It counts string events only in the source field subtree and stops immediate propagation at eight. If the target guard is reverted, recursive generated-label updates are cut off before another field callback; the fixture then fails its ceiling/output assertions instead of continuing the runaway. Normal operation must stay below that ceiling. Overlay length is also bounded against original source length; no tight CI timing threshold is used.

The IHubModService spy records both SaveOrReload overloads and all runtime/persistence/inventory mutation routes. Typing, cursor movement and paste assert zero mutations, unchanged stored source, no additional post-Build service queries, and zero preference writes. The page is constructed with in-memory preferences; no PlayerPrefs or real Lua runtime/store is created. These controls prove the page does not request VM/store effects through its actual service seam; they are not a live VM performance benchmark.

Teardown calls OnDestroyed, unregisters the event ceiling, closes the owned window and restores the prior clipboard. No persistent global test state is installed. No Undo command was authored because the existing page has no undo action/API contract; native editor/IME/OS undo behavior remains manual/platform QA, not an invented passing control.

## Static receipt and root checks

- Scoped diff check exited 0; final direct file scan: 255 lines, zero `var` declarations, zero Cyrillic lines and zero trailing whitespace. Direct checks cover the untracked fixture even though an ordinary Git diff does not.
- CoreAI.Mods.Tests already references CoreAI.Mods.Hub, Hub UI and Unity Editor test assemblies; the highlighter/page internals already grant the Mods test assembly access. No assembly or metadata was written by this worker. A later read-only check found the companion metadata present while the Editor was running; it was left untouched for root's import review.
- Exact root fixture filter: `CoreAI.Tests.EditMode.HubModEditorTypingRegressionTests` (two authored UnityTest cases). Root must compile/import and verify that native keyboard and NavigationSubmit paste dispatch actually execute on the installed UI Toolkit version; missing changes or an unattached panel are failing assertions, not a skipped success.
- Root may run the requested temporary guard-off control only in its isolated/ignored probe: the event ceiling must be reached and the test fail promptly without hanging. This worker did not revert runtime code or execute either control.
- Follow the automated cases with manual live editor typing/paste/caret checks and a profiler capture. No game-frame, allocator, full-size source, IME, undo, player or runtime VM performance result is inferred from this authored fixture.

CHANGED: the new typing fixture and this report only; C# is frozen for root checks.
VERIFY OUTPUT: static source/interface/API-pattern read-back and whitespace/type/prose checks; no execution.
NOT DONE: compilation, attached-panel test execution, root guard-off control, manual input, game/player behavior and profiler evidence.
CONCERNS: actual UI Toolkit key/paste dispatch and root's guard-off containment proof remain unrun. No current evidence shows the target guard is insufficient; report a concrete input failure if execution reveals one.
QUESTIONS: none block handoff. No runtime fix was attempted outside the lease.

## Paste interaction correction after the first root execution

Root reported an actual live Editor selection of 69 cases: 68 Passed and one Failed. The native attached typing/caret case passed. The Paste case expected the exact 82-character Unicode draft but observed a single Cyrillic character. This is not relabeled as a production freeze failure or ignored; the original fixture did not establish focus or prove its NavigationSubmit actually invoked the Paste button.

Read-only follow-up found that `HubModWidgets.MakeButton` constructs a normal `Button(onClick)` and does not override its focusability. The project's existing attached `HubContractRegressionTests` explicitly focuses its button, sends NavigationSubmit and yields a panel turn. Installed Unity 6000.6.0f1 documentation was inspected at `D:/unity/Editor/6000.6.0f1/Editor/Data/Managed/UnityEngine/UnityEngine.UIElementsModule.xml`: focusable/Focus, NavigationSubmitEvent, EventBase.target, Button.clicked and Button(Action) document the chosen native interaction. No Editor API was invoked by the worker and no private runtime method was reflected/invoked.

The corrected Paste helper is an IEnumerator. It focuses the owning window and actual focusable Paste button, yields one panel update and asserts the button owns focus. It then sets and immediately verifies the exact clipboard, resets the event ceiling, sends a submit event whose target is explicitly that button, and observes the real public Button.clicked event exactly once. A five-frame maximum handles deferred panel dispatch without a CI wall-clock threshold. It verifies clipboard contents again and unregisters the temporary click observer in finally. Both Paste operations yield this helper before checking unchanged exact draft/highlight/no-service assertions. Clipboard restoration remains in teardown.

This is a correction to fixture dispatch proof, not a bypass: it neither assigns the source field directly nor invokes private Paste, relaxes expected output, suppresses logs or ignores failures. Exact actual button activation/readback failures now produce direct diagnostics before any draft comparison. The native typing loop, target-specific change requirement, recursion ceiling, Unicode/large draft and zero service/persistence/runtime mutation controls remain unchanged.

Final fixture SHA-256: `9376482AF6FDFD39F421EF7463C33D07368D063E70F40C8B42E99B206674A20E`. Root must rerun `CoreAI.Tests.EditMode.HubModEditorTypingRegressionTests` before claiming the corrected Paste proof. The first typing pass is root-provided evidence for the earlier revision; it does not make the corrected two-case revision green. Actual OS keyboard/IME/clipboard interference, guard-off control and manual profiler behavior remain separate verification limits. Runtime source is untouched; the worker's C# lease is frozen for root's Play Mode/manual QA.
