# Independent code editor review, 2026-10-27

## Scope and evidence

Independent adversarial read of the untracked/current `CodeEditorView.cs`, `CodeEditorSurface.cs`, `CodeLanguageProfiles.cs`, and `CompletionPlacement.cs`, authored by another worker. Inspected text/history/contracts utilities and the actual Hub mod editor caller to establish source ownership and event/lifetime behavior. The runtime adapter/facade previously implemented by this worker were not called an independent review.

No C# fix, build/test execution, Unity/MCP call, Git mutation, asset/meta write, or inference was performed. Attached typing, native text-cache/focus behavior, caret geometry, and visual results require root-run native evidence. The current UI regression fixture was read; its existence is not an execution receipt.

## Confirmed findings

### EDITOR-REVIEW-001 [P2] Lua Enter indentation loses the lexical state of a long string/comment

- Site: `Assets/CoreAIMods/Runtime/HubIntegration/CodeEditing/CodeLanguageProfiles.cs:127`, especially `LuaTokenizer.Tokenize(before)` at line 132 and the keyword indentation decision at lines 142-143.
- Caller: `CodeEditorView.OnKeyDown` calls the profile's `PlanEnter` before native newline insertion and schedules `CompleteNativeNewLine` to apply the returned indent. The full source is available to the profile, but it slices away all previous lines before tokenizing.
- Deterministic reproduction: Use source `local s = [[\nthen\n]]`, with the caret immediately after `then`. Call `LuaCodeLanguageProfile.PlanEnter(source, caret, caret)`, or press Enter there in the attached editor. The current line `before` is `then`, so the isolated tokenizer classifies it as a keyword and the returned indent is four spaces even though the caret is inside the long-string body. Equal-sign long delimiters and long comments have the same lexical-state loss.
- Impact: Enter adds block indentation to string data, changing the literal's value beyond the newline the author typed. A line that only contains a block-looking word inside a comment is also indented as executable syntax.
- Meaningful regression: Assert no extra code-block indent inside `[[...then`, `[=[...do`, a long comment, and a supported multiline/interpolated literal. Retain positive cases for actual Lua `if ... then`, `for ... do`, and function declarations. Add one attached native Enter test verifying the exact source and a single undo/redo operation.
- Required direction: Use the full bounded Lua lexical context at the insertion point when deciding whether a keyword opens a code block. Reuse the existing Lua tokenizer/context rather than inferring syntax from the isolated physical line.
- Status: Sent to root promptly; source-backed reproduction only, not executed by this reviewer.

### EDITOR-REVIEW-002 [P3] A Python-only second lexer disables Lua auto-pairing after the length operator

- Primary site: `Assets/CoreAIMods/Runtime/HubIntegration/CodeEditing/CodeEditorView.cs:1634`, where Lua-context-gated auto-close delegates to `CodeEditorTextUtility.AutoClosePair`.
- Call-path evidence: `IsCodeAtCaret` correctly uses `LuaCodeLanguageProfile` for Lua. `AutoClosePair` then independently calls the utility's `IsCodeAt`; its `ScanCode` treats `#` as a Python comment marker. That second decision disagrees with the accepted Lua context.
- Repro: In Lua source `local n = #f`, put the caret at the end and type `(`. Lua's tokenizer classifies the caret as code. The shared utility sees the earlier `#` as a comment, returns `AutoCloseEdit.None`, and native input inserts only `(` instead of the normal paired `()`.
- Impact: Normal Lua editing after the length operator loses the advertised auto-pair behavior. Python's comment handling must remain correct for the Python profile.
- Meaningful regression: Attached Lua key input after `#f` must produce `#f()` with the caret inside the pair; Python input after `# comment` must not auto-pair. A pure helper regression should use an explicit language-aware context seam instead of removing lexical checks globally.
- Required direction: Make the language's accepted lexical context authoritative through the shared auto-close helper, or provide a language-aware helper policy. Preserve existing Python behavior.
- Deduplication: The independent Core/Mods reviewer discovered this call-path mismatch and asked this editor review to own it. It is counted here once; the Core/Mods review cross-references this report.
- Status: Sent to root; source-backed, not executed by this reviewer.

The actual Hub ancestor intercepts editor Escape before popup/goto handlers run; that separate P2 integration finding is owned and counted in `HUB.md` as `HUB-REVIEW-001`.

## Reviewed behavior and evidence limits

- Raw source remains in a plain `TextField` with rich text disabled on the editable element. Highlight/gutter decoration does not own document changes; exact target checks reject bubbled decoration events.
- Normalization currently preserves literal tabs, NBSP, and non-ASCII characters, and canonicalizes line endings. Display markup stays on the sibling presentation layer.
- Source length, decorated line count, highlight output, guide count, history character retention, and completion result count are bounded. Large documents switch to plain text synchronously before deferred presentation.
- Document generation, exact source/caret snapshots, paused scheduled items, and history identity fence deferred Enter/completion/focus callbacks. Language switching preserves separate drafts, selections, histories, and scroll offsets; disposal invalidates work and removes event subscriptions.
- Runtime save caller retains an exact external-source expectation separately from the normalized editor draft, disables editing/language selection during a bounded command, and fences result identity/revision before applying UI state. Source expectation remains a host-serialized precondition rather than a cross-process compare-and-swap.
- No unverified claim about native undo fallback, focus geometry, platform input dispatch, or player performance is made. Those require the actual native driver and should not be inferred from source-only inspection.

## Source snapshot (SHA-256)

```text
2617703FECE6FDA32E6F715DAF12005FB8DCF57F1F95B769970BE2C58A7EE5A6  Assets/CoreAIMods/Runtime/HubIntegration/CodeEditing/CodeEditorView.cs
9D2BCA5D9E5BBFEF4D60CCCDDC6108DFBAF4FB7639E7D4295BE870244F271679  Assets/CoreAIMods/Runtime/HubIntegration/CodeEditing/CodeEditorSurface.cs
EEFE12840C0EA3B99A1B5174F76A7C32FBBD75D617E1E858CE679DD8A608E235  Assets/CoreAIMods/Runtime/HubIntegration/CodeEditing/CodeLanguageProfiles.cs
8C7EDC61BECBE68187A506D6A32114FA69CF05B10A6AE377A844230B7DE1EE48  Assets/CoreAIMods/Runtime/HubIntegration/CodeEditing/CompletionPlacement.cs
```

## Handoff

### Accepted candidate follow-up (staged only)

Root authorized mirrored fixes/regressions, with no production Assets changes during the active Unity batch. The candidate set is at `C:/AI/work/coreai-completion-20261027/editor-host-candidates/`, containing `candidate-manifest.json`, `candidate.diff`, byte-exact `originals/`, and a separate `CodeEditorView-owned-hunks.diff`.

- Lua Enter now selects actual current-line code tokens from the full bounded source. The first proposed caret-mask guard was rejected during independent review because it suppressed indentation for `if ... then -- note`; it was replaced before handoff. Tests now include real openers with trailing comments as well as long/equal-delimited/comment/interpolated literal bodies and the existing analysis bound.
- The legacy eight-argument `AutoClosePair` signature and its Python behavior remain. An additive overload accepts a host language's preclassified insertion context. The Lua view uses that seam after its language-specific gate. The private shared implementation preserves legacy branch order and selection/type-over behavior. Original utility newline bytes were preserved.
- Added 15 pure language/helper cases and four attached native language/actual-Hub cases in new staged test files. The existing Gateway composition fixture has one staged host-disposal regression. These 20 new cases are candidates, not pass receipts. Root owns portable links for the pure profile fixture/source when needed.
- `CodeEditorView` overlaps the original UI author's separate `editor-native-candidates` work. This worker owns only the additive lexical-context call and new transient Escape method. Its two hunks must be composed with the author's native focus/key/cache fixes against the captured original SHA; a stale full-file copy must never overwrite that work.
- Root reported native focused2 as 92 passed / 3 failed / 4 skipped; those three native editor failures are still an acceptance issue handled by the original UI author. This report does not mark UI accepted or conflate those failures with the staged lexical/Hub fixes.

Candidate SHA-256 for the corrected language profile: `3D181648AA888EF4603960EA473E5430337711F703B47BC4CD8EFD055A43429D`. The manifest pins every other candidate and original. Added-line whitespace checks passed, and all ten existing production inputs were verified unchanged. No candidate build/test was run here. The final merged candidate set still requires independent review and root-run gates.

- CHANGED: This review document and subsequently authorized mirrored lexical/Hub candidates and regressions; production Assets remain unchanged.
- VERIFY OUTPUT: Full four-file editor source, production save/Hub callers, text/history contracts, scheduled callback guards, and current UI regression sources read; primary input hashes recorded.
- NOT DONE: Candidate promotion, pure/native regression execution, attached Hub integration checks, player/device/visual/performance evidence, and root acceptance.
- CONCERNS: Two Lua lexical-context defects plus the separately counted Hub Escape integration defect require adjudication. Source inspection does not validate native caret/undo/geometry.
- QUESTIONS: None.
