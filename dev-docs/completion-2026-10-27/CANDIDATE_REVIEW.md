# Independent merged candidate review — 2026-10-27

Status: independent source review complete; exact final twelve-file candidate approved for root-controlled promotion. No executable/native acceptance is claimed. Reviewer: native GPT-6.1 Sol, max reasoning. The final quote/tracker candidate addresses the additional P2 below; superseded candidate failures remain preserved as review history.

Scope: twelve mirrored candidate files under `C:/AI/work/coreai-completion-20261027/combined-editor-candidates/`, including the merged editor view, corrected Lua language policy, actual grouped Hub Escape route, registry cleanup and four test files. Read-only review; no source/test/Git mutations, compilation, Unity, model or network calls.

Acceptance: verify candidate and production bytes independently; read full merged view/regressions and every delta; preserve the original helper overload and Python behavior; preserve inline-comment Lua block indentation; forward Escape through the actual active grouped child; isolate callback failures while settling all owned cleanup. An authored regression is not a passed gate.

## Findings

### CANDIDATE-REVIEW-001 [P2] Quote auto-pairs cannot type over their own tracked closer

Provenance: confirmed remaining defect in the frozen editor and the merged candidate; not introduced by the mechanical merge. Candidate view SHA-256: `f8b1f2248bd696cf18bb78567e4fc1c52b731d942496d4416a38b965eb2cc703`.

- Site: `Assets/CoreAIMods/Runtime/HubIntegration/CodeEditing/CodeEditorView.cs:1535` checks `IsCodeAtCaret()` before calling `TryAutoClose`. The helper obtains tracked type-over permission only inside `TryAutoClose` at lines 1630-1637. `LuaCodeLanguageProfile.CodeMask` excludes every character in a String token; `IsCodeAt` asks for the character immediately before the caret.
- Deterministic source-backed trigger: in an empty Lua draft, type a double quote. The editor inserts two quotes and places the caret at index 1 with the second quote tracked. Type a double quote again to close the string. The lexical check at index 0 is false because that character belongs to the string token, so `TryAutoClose` is not reached. Native text input inserts a third quote instead of moving over the tracked quote. Single quotes have the same path. Typing text between the pair before closing also reaches this failure.
- Impact: ordinary entry of a quoted Lua value leaves an extra quote and invalid source; the advertised quote pairing requires manual deletion or caret navigation. This is separate from the repaired Lua length-operator insertion case.
- Narrow direction: allow legitimate tracked-closer type-over to be considered separately from the lexical gate for inserting a new pair. Preserve the non-code insertion refusal; do not broadly permit new pairs in strings/comments. Quote escaping needs a negative control so an escaped quote is inserted as literal content rather than incorrectly skipped.
- Meaningful regression: actual attached key input for both single and double quotes, with and without intervening literal text, must retain exactly one opening/closing pair and advance the caret. Include odd/even backslash escaping, Python/comment policy controls and a following native character to verify the final caret/selection. Existing candidate native tests cover a parenthesis closer and Lua `#f` insertion but do not cover quote type-over.
- Sent promptly to root and the candidate author. No source, candidate or test was edited or executed by this reviewer.

The previously rejected first Enter candidate is recorded separately in `PR_FINDINGS_CORE_MODS.md` and is not treated as a production regression. Its reported inline-comment defect is corrected in the current full-token candidate; review of final results remains separate from execution.

Root accepted CANDIDATE-REVIEW-001 and assigned the original native editor author a narrow correction in the combined view and UI regression fixture, preserving all previous merged hunks. Its final current-token and verified-insertion implementation is independently source-approved below. New-pair insertion remains refused in comments/literals; native regression execution is pending root.

Intermediate quote candidate review: view `aeb467cf1eed1214f0a5cbcb6be1549c7a3c4386beef13fa6e1cac49c5af1439`, UI tests `d7ebbc5f9982b6b2af3f8948dc3da8f507b7de83eb00ca49dc3fb8bca6f5639e`. Both hashes and the other ten unchanged candidates were independently checked; the existing merged hunks were preserved. This candidate was not approved. During final audit the author identified, and this reviewer independently confirmed from `LuaTokenizer.ScanShortString`, a new candidate-only case: create a tracked quote pair, insert an unescaped newline between it, then type `-- ` before the retained closer. The current quote is now in a comment, but the backward search still finds the original code opener at index zero and incorrectly skips typed comment text. The author is replacing the backward-only test with classification of the actual current quote token and adding the real-tracker newline/comment regression. This superseded candidate issue did not reach production and is not counted as another production finding.

The retained odd-backslash continuation control exposed another concrete prerequisite before a stable final handoff: `AutoCloseTracker.OnTextChanged` cannot infer the insertion position when the inserted quote equals the immediately following tracked quote. For old characters `[quote, backslash, quote]` and new `[quote, backslash, quote, quote]`, its longest common prefix consumes the entire old text, so the stored closer remains at the newly escaped literal rather than shifting to the final quote. A later closing key then loses its tracker. Author and reviewer independently identified this exact comparison path; root was informed that a verified edit-position/range seam is justified within the optional Core utility scope. The continuation assertion must remain. No source, staged code or tests were changed by this reviewer; no intermediate quote candidate has promotion approval.

## Completed source review of the merged snapshot

- Independently hashed all twelve candidate files and their actual production counterparts. All twelve candidate hashes matched `manifest.json`; all ten existing production files matched the frozen original hashes, and both new test paths remained absent from production. Mechanical merge success was not used as a correctness result.
- Read the complete 2,530-line merged `CodeEditorView` at `f8b1f224...`, every other production delta and affected caller/lifecycle joins, and all four test files in full. The view read includes source ownership, history, input, caret/focus retention, deferred work, completion acceptance, decoration limits, transient dismissal and disposal rather than only the newly merged hunks.
- The corrected Lua `PlanEnter` tokenizes the full bounded source, filters actual tokens intersecting the current physical-line prefix and does not reinterpret long strings/comments/interpolation bodies as keywords. It retains real `then`, `do` and function openers followed by an inline comment. Negative long-delimiter cases and positive inline-comment controls are present in the policy fixture. The rejected simple caret-context early return is absent.
- `AutoClosePair` retains its exact original eight-parameter signature and behavior through the nullable classification fallback. Its additive nine-parameter overload accepts the host language's insertion classification; no overlapping optional overload was added. Lua length-operator insertion can pass through the already accepted Lua context, while legacy Python comment behavior remains guarded. The separate tracked-quote defect above still requires correction.
- The focus/selection candidate replaces repeated blur/refocus and scheduled selection restoration with an exact pending source/generation/selection consumed at input boundaries. Pointer input cancels the pending request; key, navigation and execute-command input restore then clear it. Source/document replacement and detach/disposal invalidate stale requests. The Unity 6.6 text-cache synchronization API is behind `UNITY_6000_6_OR_NEWER`; its runtime behavior and older Unity versions still require actual native checks.
- Trace the actual Escape route: `CoreAiHubWindow` asks its active `HubSubTabPage`; that wrapper forwards only to its selected child; `HubModsPage` forwards only to its live open editor; editor/surface/view dismiss their own completion or goto prompt. No static global popup flag controls Hub policy. A destroyed wrapper returns false. The normal subsequent unhandled Escape still belongs to the configured Hub collapse policy.
- Registry Dispose captures cancellation sources and publishes each owned release under `_gate`, detaches stored cancellation references and clears pending endpoints, then runs every cancellation and release outside the mutation lock. Each cancellation failure is isolated and emits a bounded generic warning. Release work is published before callback reentry and retains the original activation generation, so a late activation's owned host is still settled once. Existing explicit owned-vs-borrowed client/transport behavior is preserved.
- No additional source-confirmed regression was found in the corrected Enter, grouped Escape or registry cleanup deltas. This source result does not clear the quote finding or substitute for compilation/native execution.

## Regression quality and exact remaining checks

- `CodeEditorLanguagePolicyEditModeTests`: real language profile/helper calls for long strings, equal-sign delimiters, long comments/interpolation, base indentation, actual block openers with trailing comments, the existing size bound, explicit Lua insertion classification, legacy Python comments and old type-over signature. These are not source-index assertions.
- `CodeEditorLanguageAndHubNativeEditModeTests`: attached EditorWindow key events; long-literal Enter plus undo/redo; Lua length operator and Python comment insertion; the real CoreAiHubWindow Rebuild with existing UXML, actual HubModsPages.Register service and grouped Mods/Logs wrapper. Completion/goto dismissal is asserted before collapse; Logs/destroyed wrapper remain unhandled. Reflection is limited to fixture setup/state observation rather than replacing the production Escape path.
- `CodeEditorUiEditModeTests`: preserves native Enter/undo, Tab/comment, bracket/completion and document/detach controls; strengthens native selection/focus assertions and adds a subsequent navigation/character control after programmatic layout selection. The pending quote correction must add actual quote/escaped-input controls without weakening these assertions.
- `GatewayClientCompositionEditModeTests`: the new real-registry controlled-factory case combines ready A, faulting pending B and healthy pending C. It checks every cancellation, absence of the mutation lock during token callbacks, warning observability, all host-release counts exactly once, late owned leaf retirement exactly once and borrowed wire/auth/header counts zero. Pending completions are released in finally and awaits use the existing five-second bound. No custom fake registry duplicates the implementation.
- The portable test projects use explicit compile lists; the new Mods language-policy/native fixtures are not silently claimed as part of the earlier portable 2,954/111 results. Root must run the actual Unity fixtures after promotion and verify discovery/results, followed by the source-final/full gates.

## Final quote/tracker delta and promotion decision

Approved final manifest: `C:/AI/work/coreai-completion-20261027/combined-editor-candidates/manifest.json`, SHA-256 `0562cb77c0e29c90f097785cdf841c29d129af134750e8dd3bb6c4894e1f12a1`.

Independently rehashed all twelve final candidates and actual frozen production paths immediately before approval: every candidate matched the manifest; every existing original still matched, and both new fixture paths were still absent from production. Nine candidates remain byte-identical to the initial merged review. The final three replacements are:

| Repository path | Final approved SHA-256 |
| --- | --- |
| `Assets/CoreAIMods/Runtime/HubIntegration/CodeEditing/CodeEditorView.cs` | `1c28689e0279ce3537484cc3b68c4d93238aba47a85a392dbe6de417b5120b35` |
| `Assets/CoreAI/Runtime/Core/Features/CodeEditing/CodeEditorTextUtility.cs` | `025eacbafb35674d298968ab5145f18f02e8a84c203c48245620f510e34f4c2d` |
| `Assets/CoreAIMods/Tests/EditMode/CodeEditorUiEditModeTests.cs` | `0d18e2476d434dbcd7916f082fb1ad1553b434cddb325114bb004d0b00f6dc07` |

Final source reasoning:

- The view considers a tracked closer separately from permission to insert a new pair. Non-quote closers require code at their actual current character, so a parenthesis after a quoted argument can advance correctly without permitting a parenthesis in string/comment content.
- A Lua quote must belong to an actual bounded `LuaTokenizer` String token that starts with the matching quote, has length greater than one and ends exactly after the current tracked quote. Odd preceding backslash parity refuses type-over. Comments, long literals, interpolation bodies and a one-character incomplete opener cannot qualify. Python reuses its existing post-quote lexical boundary. The superseded backward-opener-only check is gone.
- A tracked quote that must instead be inserted literally captures exact source, expected next string, insertion index and document generation. `TrackTextChange` validates all those values and clears the hint before applying it. The next input/command/navigation, pointer input, focus out, source replacement, document/language invalidation, detach and disposal clear stale hints. No ambient flag authorizes an unrelated source mutation.
- Core retains the exact original two-argument `OnTextChanged` signature and its old fallback body. The additive three-argument overload accepts an insertion index only when old/new text prove a single-character insertion at that exact position by length plus ordinal prefix/suffix checks. Invalid hints take the existing text-diff path. The original AutoClosePair arity also remains present.
- Read the final full diff against production and the complete added regressions. A direct character comparison against the previously reviewed UI-test snapshot `f5a1b65f...` found zero old characters changed and 10,511 characters added, so the prior controls/assertions were preserved rather than weakened.
- Final native cases exercise both quote kinds with empty/text content, odd/even backslashes and the subsequent real closer, opposite quotes and literal brackets, tracked quotes moved into comments both before the opener and after an unescaped newline, Lua long-literal negatives, and quote-then-parenthesis-then-following-input. Tracker state is created by real key input. The additional pure tracker case validates the ambiguous insertion, invalid-hint fallback and legacy overload behavior.

No remaining confirmed source defect was found in the final pinned candidate. CANDIDATE-REVIEW-001 is addressed by this candidate at source-review level. Root may promote these exact bytes for compilation and execution; a different hash requires a delta review. This approval does not count authored tests, a clean mechanical merge or source inspection as a native pass.

## Initial merged source fingerprints

These are the independently checked twelve-file hashes before the accepted quote correction. The final changed hashes will be recorded separately; unchanged candidates must continue to match these values.

| Repository path | SHA-256 |
| --- | --- |
| `Assets/CoreAIMods/Runtime/HubIntegration/CodeEditing/CodeLanguageProfiles.cs` | `3d181648aa888ef4603960ea473e5430337711f703b47bc4cd8efd055a43429d` |
| `Assets/CoreAIMods/Runtime/HubIntegration/CodeEditing/CodeEditorView.cs` | `f8b1f2248bd696cf18bb78567e4fc1c52b731d942496d4416a38b965eb2cc703` |
| `Assets/CoreAIMods/Runtime/HubIntegration/CodeEditing/CodeEditorSurface.cs` | `06f9e1955d062555492c58bc9fd7e36173e1dfe930fc21a63599778793754491` |
| `Assets/CoreAIMods/Runtime/HubIntegration/HubModEditorPage.cs` | `103c8f46e4148a4452d8241c82ad55a63abf0aaaff0c95718401cea1c6af9381` |
| `Assets/CoreAIMods/Runtime/HubIntegration/HubModsPage.cs` | `5afb8c21524900b834fae658f539b884e4ec94c5b6a7bbbbda5c1bd8106dfb18` |
| `Assets/CoreAI/Runtime/Core/Features/CodeEditing/CodeEditorTextUtility.cs` | `5011e39e8b781a72eae185e2eaf446e82d0abd65bb98fb8487abb2ff41a26a7c` |
| `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/LlmClientRegistry.cs` | `dac4f19dcd6acf9d06348494f926c2bff632cf15d96e052cb87f810231aa45ca` |
| `Assets/CoreAiUnity/Tests/EditMode/GatewayClientCompositionEditModeTests.cs` | `8cb010e05634c92d5277f1bb4b12c61d41fcfd6e7ae86d5bdfab08d720aad434` |
| `Assets/CoreAIHub/Runtime/HubSubTabPage.cs` | `2b3323eaf81006eed018cef3ab56fa7bcef15b9a33ee31e2e6ffeb876875585b` |
| `Assets/CoreAIMods/Tests/EditMode/CodeEditorLanguagePolicyEditModeTests.cs` | `dee8c6a2ab32e8f4d7bf84d32ab00907df205b7984afe0b8b72f3a6e3591c6e8` |
| `Assets/CoreAIMods/Tests/EditMode/CodeEditorLanguageAndHubNativeEditModeTests.cs` | `b8924168128607c2b0a23958cc46cd4e3880a287fce0080ecf22020864a660bc` |
| `Assets/CoreAIMods/Tests/EditMode/CodeEditorUiEditModeTests.cs` | `f5a1b65f211fce10b30ce78d52a809b7c9ed4243510e0ba4c1e776f8d4f049af` |

## Verification

No tests or builds run by this reviewer. Root's reported first native batch (99 cases: 92 passed, 3 failed, 4 skipped) predates these candidates and is not evidence they work. Root must compile and run the language policy, full native editor/Hub and gateway-composition fixtures, then required source-final/full checks. Actual OpenRouter API PlayMode, older Unity versions, built-player/IL2CPP/WebGL, device/visual/physics, real-network/load and profiling gates remain separate from source approval.

CHANGED: `CANDIDATE_REVIEW.md` and the resumed status/link in `PR_FINDINGS_CORE_MODS.md` only during this follow-up. VERIFY OUTPUT: full merged view, all four regression files, every candidate delta and affected joins; twelve candidate/original hash comparisons; final manifest hash; old UI regression bytes preserved with additions only. NOT DONE: all compilation/tests/native/model/platform gates and root promotion. CONCERNS: no remaining confirmed source finding in the pinned final candidate; actual native behavior still requires root evidence. QUESTIONS: none. No source, test, candidate, asset, metadata or Git state was mutated by this reviewer.
