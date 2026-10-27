# Editor UI candidate implementation

Status: in progress; staged files only. Root agent owns live application, Git, Unity, builds and tests.

Model: gpt-6.1-sol. Reasoning effort: max.

Scope: ported Redo CodeEditorView, surface, Lua/Python language profiles and completion placement; Hub editor integration; dark English editor styles; editor regression tests; CODE_EDITOR.md. Runtime contracts and runtime implementation belong to finalize_editor_runtime.

Current milestone: existing actual Redo view (~100 KB), sibling source/highlight layout and partial Hub integration located. Resolving cross-file contracts and preserving CAS/base-version/unconfirmed-save semantics.

Required acceptance: raw source is isolated from rich text; source-event target filter; Lua default and Python unsupported execution with zero side effects; independent language drafts, history and caret; no execution or saving on typing; disposal unregisters callbacks; bounded source, analysis, completion and history. Root runs compile, portable and attached-panel tests after candidate review.

Verification: not run (worker is restricted to static consistency).