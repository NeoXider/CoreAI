# Runtime code editor assessment

Status: source assessment/design ready for root approval and implementation leases. Runtime freeze reproduction, performance and acceptance are PENDING. Root is the sole Unity operator, currently using FullAccess PlayMode.

Requested native model: `gpt-6.1-sol`.
Requested reasoning effort: `max`.

User goal: make the runtime editor usable, investigate game stalls caused by small edits, reuse suitable RedoSchool editor/API code, support configurable language switching and complete real Lua integration. Typing must update a draft without compiling/running a VM, saving persistence or rebuilding the whole inventory tree per keypress.

Read scope: current CoreAI Mods Hub editor/shared UI and relevant tests/demo flows; targeted RedoSchool Neoxider and `_source` editor/API code, tests, styles and provenance. Write scope: this assessment only. No source/assets/settings/scenes/prefabs/secrets are copied or modified; no builds/tests, Unity/MCP, providers/network, Git mutation or additional agents are run by this worker.

## Confirmed scope after user clarification

Transfer the ready editor and its interpreter API shape; make language selection configurable. **Lua is the real, working default. Python is an explicit nonexecuting stub.** Python may have editing/highlighting/completion behavior, but Run/Save-to-runtime must return a typed unsupported result with zero VM, backend, persistence or network effects. No Python VM, Pyodide worker, backend provisioning, server project API, secrets, settings, scenes or prefabs are transferred.

## Source inventory and reuse decision

| Actual source | Observed contract/behavior | Decision |
| --- | --- | --- |
| `Assets/CoreAIMods/Runtime/HubIntegration/HubModEditorPage.cs` | `Build()`, `DraftSource`, `IsDirty`; explicit `Save()`/`SaveCore(bool)` calls `IHubModService.SaveOrReload`. Existing conflict checks, retry/unconfirmed receipts, source identity, keep-objects choice and draft preservation are substantive behavior. | Retain this Lua application host and its ownership/receipt behavior; replace its minimal editing surface with the ported editor. |
| `Assets/CoreAIMods/Runtime/HubIntegration/LuaSyntaxHighlighter.cs` | `Highlight(string)` delegates to the real Lua tokenizer/formatter; no regex or VM execution. | Reuse behind the Lua language provider, with bounded analysis and neutral token/span output where needed. |
| `Assets/CoreAIMods/Runtime/LuaAssets/LuaTokenizer.cs`, `LuaRichTextFormatter.cs` | Pure C# `Tokenize(string)` returns tiled tokens; scanner has an explicit advancing-position safety net. Formatter builds escaped rich text. Unterminated strings/comments are tolerated. | Reuse; do not replace Lua support with a Python lexer or claim regex backtracking is the identified cause. |
| `Assets/CoreAIMods/Runtime/HubIntegration/HubModsPage.cs` | Cached inventory, coalesced `ModsChanged` refresh, debounced search, editor lifetime and draft-close policy. | Preserve; typing must not begin calling inventory refresh or backend mutations. |
| `Assets/CoreAIHub/Runtime/HubPageWidgets.cs`, `CoreAiHubUss.uss`; Mods `HubModWidgets.cs` | Existing UITK/dark-theme host, button/field styles; current code field wraps and auto-grows. | Keep UI in this Unity package layer; reuse theme tokens and narrow code-editor USS. No eighth package or web editor dependency. |
| `D:/Git/RedoSchool/Assets/_source/Scripts/Epics/CodePlayground/Application/ICodeEditorView.cs` | `GetText()`, `SetText(string)`, `SetCurrentLineMarker(int?)`, `SetErrorLine(int?)`, `SetHintLines(...)`, `ClearHintLines()`, `SetEditable(bool)`; optional `ICodeEditorErrorView.ShowError(CodeErrorLocation)`. | Port the general editor port/model semantics. Teacher-specific hints remain optional presentation behavior; do not import lesson policy. |
| `D:/Git/RedoSchool/Assets/_source/Scripts/Epics/CodePlayground/Presentation/Views/CodeEditorView.cs` | Ready native UITK view, constructor `(TextField, Label gutter, Label mirror = null, ScrollView = null)`, per-document `SetText(text, documentKey)`, `ReplaceTextUndoable`, `GoToLine`, error/caret/gutter presentation, named subscriptions and `ReleaseTree()`. | This is the requested ready editor to adapt, not a new editor written beside it. Extract language policy and Redo host/theme dependencies; keep its useful input/layout/history algorithms. It is a large, Python-specific view, not an already generic package. |
| Same Redo folder: `CodeEditorHistory.cs`, `CodeEditorTextUtility.cs`, `CodeCompletionEngine.cs`, `CompletionPlacement.cs` | History has `Push(text, caret, timestampMs, forceNewEntry)`, `Undo`, `Redo`, `Clear`, default depth 200 / coalesce 750 ms. Text helpers cover normalization, indentation, selection, comments, line move/duplicate/delete, pair handling and caret/line mapping. Completion is cached, max eight items, tokenization cap 50,000 chars. | Adapt the ready helpers. Add anchor/selection retention and a byte budget to history. Move Python keywords/import/turtle/project exports and colon indentation behind language providers; they are not generic Lua behavior. |
| `D:/Git/RedoSchool/Assets/_source/Scripts/Epics/CodePlayground/Presentation/UI/CodeStationEditorPane.uxml`, `CodeStationPanel.uss` | Gutter outside the scroller; highlight mirror and TextField are **siblings** inside a field stack; horizontal/vertical scrolling. View uses pooled per-line rendering (`LineRun.ApplyChangedRange`) and actual font metrics. | Adapt this editor layout into CoreAI's dark Hub; do not copy the whole Code Station/teacher/project surface. |
| `D:/Git/RedoSchool/Assets/ThirdParty/Neoxider/markdownrenderer/Runtime/MarkdownRendering/ObjectRenderers/SyntaxHighlighting/ISyntaxHighlighter.cs` | Actual neutral signature: `string[] Highlight(string[] dedentedLines)`. Python/C# implementations carry multiline lexical state and use a configurable `SyntaxPalette`. | Reuse the small provider shape/selected reviewed syntax code if appropriate; do not add MarkdownRenderer/Markdig as a mandatory code-editor dependency. CoreAI already owns a real Lua lexer. |
| Redo `.../Application/ICodePlaygroundRuntime.cs` | Interpreter-facing event API: `Status`, `StatusChanged`, `MessageReceived`, `Initialize()`, `Run(CodeRunRequest)`, `Stop(runId)`, plus Python station reset/input/module/asset/focus commands. Commands return immediately and events report progress. | Port the small run-id/status/event/cancel shape, not Python station business operations. |
| Redo `.../Domain/CodeRunRequest.cs`, `CodeRunResult.cs`, `CodeRuntimeMessage.cs`; `.../Infrastructure/PyodideWebRuntime.cs`, `EditorFakeRuntime.cs`, `CodePlaygroundWebBridge.cs` | Run request carries identity, code, runtime choice and bounds; the closest located implementation is a Python/WebGL worker bridge. This assessment reads the API/request shape, not the complete backend implementation. | Use as API provenance/reference. **Do not transfer these runtime implementations**; Lua must use CoreAI's real runtime and Python must be a typed stub. A fake interpreter is not a Lua integration. |
| Redo `Platform/Shared/CodePlayground/IStudentProjectsGateway.cs` and Code Station presenters/draft store | Student/project/module/assignment storage, Python module files and application state. | Exclude. These are Redo business/backend contracts, not the reusable language/runtime API. |

Located supporting Redo tests include `CodeEditorHistoryTests`, `CodeEditorTextUtilityTests`, `CodeCompletionEngineTests`, `CompletionPlacementTests`, `CodeLineSelectionTests`, `CodeStationLineNumberingTests`, and integration `CodeEditorPasteNormalizationTests`, `CodeEditorCompletionNavigationTests`, `CodeEditorViewTreeReleaseTests`, `CodeEditorStationLayoutTests`. They supply meaningful cases to port; no existing test result is adopted as CoreAI acceptance.

The user expressly authorized reuse from their local RedoSchool sources. `markdownrenderer/package.json` identifies `com.neoxider.markdownrenderer` 1.1.1, author Neoxider. No standalone package license was found in the narrow renderer license-file lookup; that author label alone is not a redistribution license. Keep a file-level provenance note for extracted first-party helpers, preserve any applicable notices, and have root resolve publication provenance before shipping any uncertain third-party portion. No third-party engine/backend is needed for the recommended port. Translate Redo prose/comments into CoreAI's required English and replace Redo theme tokens rather than copying project branding/settings.

## Freeze investigation: causal candidates versus runtime proof

| Candidate | Concrete user-flow/source chain | Current conclusion / required proof |
| --- | --- | --- |
| **C1: descendant ChangeEvent reentry** | `HubModEditorPage.BuildHighlightOverlay` inserts a rich-text `Label` **inside** the TextField input. The original ancestor `RegisterValueChangedCallback` unconditionally assigned `_highlightOverlay.text = Highlight(evt.newValue)`. A string change emitted by the attached descendant can bubble back into the same callback; its generated markup is then highlighted again, growing markup and nested event work. A single character is enough to enter this cycle. | High-confidence event-boundary defect candidate, distinct from a Lua VM loop. Root has already reported applying `ReferenceEquals(evt.target, _codeField)` before highlighting; this worker must not overwrite that live fix. Runtime reproduction/fix proof still needs a bounded attached-panel test and real typing. Detached `VisualElement` tests do not establish the attached-panel event path. Prefer a sibling mirror plus target filtering and non-notifying decoration writes. |
| **C2: unbounded synchronous presentation work** | Even with C1 stopped, each accepted value change lexes/formats the entire draft and assigns one full rich-text label. The field and overlay use `WhiteSpace.Normal`, auto-grow in the outer page scroll and have no source/highlight budget. Larger paste produces full string/token allocations and layout work on the UI thread. | Source-confirmed work placement; actual stall size/timing is unmeasured. Introduce a bounded editor viewport, scheduled/coalesced enhancement, changed-line rendering and clear fallback above the analysis budget. Do not claim that debouncing alone bounds one large synchronous layout. |
| **C3: explicit Save & run can block the game** | `SaveCore` synchronously calls `IHubModService.SaveOrReload`, which invokes the real runtime load/reload and persistence. The source comment itself documents blocking until completion/yield. `LuaCsModRuntime` defaults are 10,000 ms and 50,000,000 handler steps; its Rbx path runs the main chunk through a guarded `RunModChunk`. | This is an explicit execution path, **not typing**. Existing guards are not a responsive UI guarantee: a bounded 10-second main-thread run can still appear frozen. Test controlled error/infinite code through the real Lua adapter and, if necessary, add a cooperative execution operation rather than `Task.Run` around Unity world calls. |
| **Not established: automatic VM compile/save on keypress** | Current typing callback reaches `LuaSyntaxHighlighter`; header/history/diagnostics refresh and save occur on explicit actions. `HubModsPage` already coalesces mod events and caches inventory. | No source evidence that ordinary typing invokes SaveOrReload or rebuilds the entire mod inventory. Protect the zero-side-effect invariant with integration counters; do not describe unconfirmed alternatives as bugs. |
| **Not established: regex catastrophic backtracking in CoreAI highlight** | Current Lua highlighting uses a handwritten tokenizer, bounded advancing scans and a formatter, not Regex. | Do not replace this with a speculative regex diagnosis. Malformed/long input remains a regression/performance case. |

Redo avoids the specific ancestor/descendant overlay shape by using sibling mirror/field elements, but it still synchronously calls `RefreshGutter/RefreshHighlight/RefreshGuides/RefreshCompletion` on edits and has Python-specific normalization reentry. Port its existing algorithms with explicit event ownership, budgets and lifecycle tests; do not import it unchanged and promise the freeze is solved.

## Proposed minimal modular API

Use existing assemblies/packages. Portable document/language/runtime DTO contracts belong in the engine-free Core assembly; UITK elements belong in CoreAIHub's Unity UI layer; the Lua adapter belongs in optional Mods integration. No Unity UI implementation belongs in a generic Core/MainFrame layer. Names below are proposed public API, not claims that these types already exist.

```csharp
public interface ICodeLanguageProvider
{
    string LanguageId { get; }
    string DisplayName { get; }
    CodeTokenBatch Tokenize(CodeDocumentSnapshot snapshot, CodeAnalysisBudget budget);
    CodeTextEdit PlanIndent(CodeDocumentSnapshot snapshot, CodeSelection selection, bool outdent);
    CodeCompletionBatch Complete(CodeDocumentSnapshot snapshot, CodeSelection selection, int maxItems);
}

public interface ICodeInterpreter
{
    string LanguageId { get; }
    CodeRuntimeStatus Status { get; }
    event Action<CodeRuntimeEvent> MessageReceived;
    void Run(CodeRunRequest request);
    void Stop(string runId);
}
```

- `CodeDocumentSnapshot`: immutable document id, language id, revision, text and selection (caret **and anchor**). A new edit increments revision. Histories are document-owned; selecting another mod must not reuse its undo stack or expected saved base.
- `CodeTokenBatch`/completion/diagnostic results carry document id + revision + language id. The view applies only current results; stale/cancelled language work cannot paint another draft or move its selection. Analysis receives character/token/time caps and returns partial/disabled enhancement explicitly; the text itself must never be silently truncated.
- Adapt Redo's text/history/selection algorithms into BCL-only helpers. Separate common operations from language policy: Lua comment `--`, keywords/block indentation/Rbx and hook completions; Python comment `#`/colon indentation and keywords in its editing provider. Do not leak Python turtle, assignment-module or backend catalogs into Lua completion.
- The runtime port follows Redo's event/run-id idea, but its CoreAI request/result carries language id, draft revision, explicit source/operation identity, cancellation and bounds. Use a typed `UnsupportedLanguage`/`NotSupported` outcome for Python; `Status` must not become Running and no callback may enter Lua as a fallback. Language dropdown options are Lua and Python-stub; Lua is selected by default. Changing language changes editing/runtime adapter selection, never source text, mod identity or grants by implication.
- `LuaCodeInterpreter` must resolve the actual scene `ILuaModRuntime` and admitted actor from the existing Mods composition. Explicit Run uses the actual Lua load/reload execution seam with declared persistence semantics; no simulated success. The existing **Save & run** route remains honestly labeled and uses the current `IHubModService` receipt/conflict behavior. Do not call SaveOrReload for typing or silently present it as compile-only validation or save-only persistence. A separate save-only command requires a real persistence capability and tests before being exposed.
- Generic editor events are `DraftChanged`/selection/language/explicit Run/Stop/Save requests. Only the application adapter owns mod loading, expected-source comparison, versions, save/reload modes and persisted status. Existing managed-mod APIs must continue consuming the same stored Lua source; editor language services must not become a parallel fake mod runtime.
- If automatic syntax checking is offered, it is optional, debounced, cancelled and revision-fenced; it must use a true parse/compile-only seam. No public compile-only operation was found in the inspected `IScriptEngine`/runtime surface. Do not execute a script merely to validate it, and do not claim `LuaCsCompileCheck` exists. Explicit Lua execution remains required even if validation is deferred.
- Keep `ReleaseTree`/Dispose ownership explicit: unregister callbacks, cancel scheduled analysis/completion/reveal work, release event handlers and preserve the draft as required by the host. Retain the current conflicting-edit and unconfirmed-save behavior; a late save result must not update a different document.

For responsive Lua execution, reuse the existing `LuaCsScriptEngine.RunChunkAsync(..., IScriptFrameYielder, CancellationToken)` capability where the actual mod build/rollback/frame ownership can be forwarded safely. Wrapping the synchronous `ILuaModRuntime.LoadMod` in an async-looking UI call or scheduling it for the next frame does **not** remove its blocking main chunk. If the current application seam cannot meet the run budget, root should authorize a separate optional async Lua load/reload capability and all world-session facade forwarding; retain sync APIs for compatibility. No background Unity access or WebGL ThreadPool workaround is acceptable.

## Exact prospective implementation leases

These are proposed leases awaiting root approval. No file below was modified by this worker.

| Lease | Exact destination paths | Acceptance boundary |
| --- | --- | --- |
| A — portable ready-editor helpers/contracts | New `Assets/CoreAI/Runtime/Core/Features/CodeEditing/CodeEditorContracts.cs`, `CodeDocument.cs`, `CodeEditorHistory.cs`, `CodeEditorTextUtility.cs`; new `Assets/CoreAI/Tests/EditMode/CodeEditorDocumentEditModeTests.cs`, `CodeEditorHistoryEditModeTests.cs`, `CodeEditorTextUtilityEditModeTests.cs` | Adapt reviewed Redo helper/API source, BCL only; full selection/revision ownership; bounded history; language behavior injected. No new package/asmdef. |
| B — actual UITK editor port | New `Assets/CoreAIHub/Runtime/CodeEditing/CodeEditorView.cs`, `CodeEditorLineRenderer.cs`; narrow existing `Assets/CoreAIHub/Runtime/CoreAiHubUss.uss`; new `Assets/CoreAiUnity/Tests/EditMode/CodeEditorViewEditModeTests.cs`, `Assets/CoreAiUnity/Tests/PlayMode/FastNoLlm/CodeEditorViewPlayModeTests.cs` | Transfer Redo's sibling mirror/gutter/scroll/input/history/error/completion UX; adapt CoreAI dark theme and injected provider. No Redo presenters/backend/teacher state. All Unity-specific code stays here. |
| C — Lua default + explicit Python stub integration | New `Assets/CoreAIMods/Runtime/HubIntegration/LuaCodeLanguageProvider.cs`, `LuaCodeInterpreter.cs`, `PythonCodeInterpreterStub.cs`; modify existing `HubModEditorPage.cs`, `LuaSyntaxHighlighter.cs`, `HubModWidgets.cs`; new `Assets/CoreAIMods/Tests/EditMode/CodeEditorLanguageIntegrationEditModeTests.cs`; extend existing `LuaSyntaxHighlighterEditModeTests.cs` only by its assigned owner | Real Lua source/tokenizer/runtime; Python typed unsupported with zero side effects; language selection actually selects its adapter; existing draft/save/owner/conflict/receipt semantics retained. Preserve root's live target guard. |
| D — execution responsiveness only if required by real gate | Separate root-approved lease on `ILuaModRuntime.cs`, `LuaCsModRuntime.cs`, relevant world-session facade forwarding in `Infrastructure/RbxWorldPackageContracts.cs`, optional Hub async capability and actual Lua guard/runtime tests | Root must decide this lease after measuring controlled explicit Run. It cannot be hidden inside a thin editor port or advertised as solved by UI scheduling alone. |

Root owns metadata/imports, final assembly decisions, builds, Unity operation and Git. Any new `.meta`/asset work needs its own explicit lease; the plan does not authorize copying Redo scenes/prefabs/settings or adding a MarkdownRenderer/web-editor hard dependency.

## Required regressions and proposed budgets

All execution/performance outcomes are PENDING; these budgets are proposed acceptance targets, not measurements.

1. **Attached-panel typing isolation:** build the real Mods editor under a live UIDocument/panel with counting language/interpreter/persistence/inventory seams. Type `local x = 1`, insert/delete one character, and change the decoration label through a controlled callback. The document changes once; decoration does not reenter as a draft edit; **0 VM compile/run, 0 save, 0 inventory rebuild** per keypress. Include the installed Unity event target path and root's immediate guard in this test.
2. **Paste/format cases:** 1 KiB, 50 KiB and above-limit large paste; rapid repeated paste; CRLF/CR/tabs/nonbreaking spaces, unclosed quotes/backticks/long comments, literal rich-text tags, Unicode and malformed partial Lua. No hang, hidden truncation or markup execution. Above the analysis budget, retain editable raw text with a visible enhancement fallback. Above the configurable document limit, reject atomically while preserving the old draft and clipboard.
3. **Selection/history/language:** Tab/Shift-Tab, Enter indentation, comment toggle, undo/redo, multiline selection, caret/anchor, scroll and error navigation survive formatting. Switching Lua/Python retains text, mod id and draft ownership; completion/indent policy changes to the selected provider. Python Run/Save-to-runtime returns typed unsupported and all Lua/backend/save counters remain zero.
4. **Explicit Lua integration:** load/run a uniquely owned harmless Lua mod, edit/reload it through the ported surface and inspect actual world/mod changes and persisted receipt separately. A failed Lua compile/load preserves the old live mod and draft. Existing conflict, keep-objects, arming-refusal and cancellation/retry tests must continue passing.
5. **Lifecycle/fencing:** switch documents while analysis or a save is pending; close/dispose; reopen; cancel. An old document/revision/language result cannot update the new view or resurrect a disposed callback. Repeated bind/release must leave one active input owner and no orphan scheduler.
6. **Controlled real Lua error/infinite run:** a unique QA mod with a syntax error, a main chunk `while true do end`, and an erroring timer/hook must be bounded by the actual Lua runtime, preserve draft/conflict state and leave game heartbeat/Stop/UI responsive. Test via explicit Run/Save & run, **never by typing**. Record configured timeout/steps and actual main-thread/frame delay; a 10-second guarded stall is not a passing UX result. Root must inspect synchronous and cooperative paths separately.

Suggested starting budgets: at most one coalesced enhancement job per editor/document, max 8 completion items, analysis fallback above 50,000 chars, a configurable initial document cap of 256 Ki UTF-16 chars, history limited by both 200 entries and 8 MiB retained text. Do not silently change Lua source-store limits to match UI defaults. For a 10 KiB draft, target input-handler p95 <= 4 ms and no editor-caused frame > 50 ms; schedule enhancement in <= 2 ms slices/frame where feasible. Measure larger paste/layout separately. These figures require real Editor/player profiling and may be tightened by root; cancellation tokens without cooperative polling do not satisfy them.

## Root-only manual PlayMode checklist

- [ ] PENDING: capture the current FullAccess scene/Console and an owned QA draft. Run a **bounded event probe** first: observe event target/origin and count without allowing recursive text rewriting, then confirm the guarded editor handles actual typing. Do not intentionally recreate an unbounded loop in the live game just to produce a crash.
- [ ] PENDING: type/delete/undo a tiny Lua edit, paste the malformed/sized cases, move selection/caret, scroll and open/close the editor repeatedly while the world heartbeat continues. Record short video plus counters/frame timings; no VM/save/tool trace should appear until explicit action.
- [ ] PENDING: select Lua by default, explicitly Run/Save & run a unique owned Lua mod, confirm real state, then select Python and request Run: show typed unsupported with no model/backend/VM/persistence activity. Switch back and verify the Lua draft/selection remain usable.
- [ ] PENDING: run controlled error/infinite Lua only through the bounded test/operation path approved by root; verify Stop/cancel and real frame responsiveness, then delete only that run's owned QA artifacts. Do not touch user mod ids, world resets or configuration.

## CHANGED

This assessment only: focused CoreAI/Redo inventory, source-level freeze candidates, current root hotfix provenance, ready-editor/interpreter API reuse recommendation, exact future leases and pending acceptance criteria. No production implementation was performed.

## VERIFY OUTPUT

Static `rg`/targeted reads located the ready Redo editor, its general view API and the Python interpreter port/request/closest bridge paths. CoreAI typing, highlight, save, inventory-refresh and guarded Lua main-chunk paths were traced. The Lua lexer has advancing malformed-input handling; no regex freeze or automatic VM/save-on-typing path was established. Root's immediate target guard is reported by root, not a runtime result verified by this worker.

## NOT DONE

No code transfer, source/assets/settings/scenes/prefabs/secrets modification, build/test, Unity/MCP, provider/network call, extra agent, Git mutation, freeze reproduction, timing measurement, player/WebGL/IL2CPP/IME or publication proof. Python runtime/backend/provisioning is explicitly excluded. Implementation starts only after root assigns exact leases.

## CONCERNS

The probable event feedback loop, whole-document UI cost and explicit synchronous Lua execution are different mechanisms and need separate proof. Porting Redo unchanged would import Python/project coupling and synchronous enhancement cost. A language dropdown without a consumed Lua runtime adapter, or a Python option that secretly executes Lua, would not satisfy the user request.

## QUESTIONS

Ready for root's implementation lease on A/B/C with Lua as default and Python as a typed nonexecuting stub. Root should separately authorize D only if the real explicit-run responsiveness gate requires runtime changes; no new runtime execution guarantee is claimed from this assessment.
