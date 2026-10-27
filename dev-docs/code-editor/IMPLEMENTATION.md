# Runtime code editor implementation handoff

Status: STAGING IN PROGRESS. No candidate has been promoted into live Assets and no build/test/Unity/provider/network operation has been run by this worker.

Native model requested by root: `gpt-6.1-sol`.
Reasoning effort requested by root: `max`.

Staging root: `artifacts/code-editor-20261026/staging/`. Production-relative candidate paths are mirrored below it. `_baseline/` and `BASELINE.json` preserve current live editor/style/project source hashes. `PROVENANCE.json` records exact Redo source hashes. `_port/port_redo_editor.py` is the initial source-transfer helper, not a production runtime or a final rebuild script; later candidate integration changes are separate.

## Milestone 1: actual source port

- Staged the actual Redo `CodeEditorView` (about 101 KB after removing project documentation/dependencies), `CodeEditorTextUtility` (about 52 KB), `CodeEditorHistory`, `CodeLineSelection` and completion placement. This retains ready input/scroll/gutter/history/selection/error/completion algorithms; no Redo backend, lesson controller, login, settings, scene or prefab is copied.
- Staged portable BCL editor/document/language/interpreter contracts with independent Lua/Python draft identity, structured run outcomes, output/diagnostic/source limits and a typed nonexecuting Python stub.
- Staged configurable Lua/Python editing profiles. Lua uses CoreAI's actual `LuaTokenizer`; Python syntax code is a reviewed local Neoxider source adaptation without the MarkdownRenderer package dependency. Python execution is still unsupported.
- Staged a real UITK surface whose mirror is a sibling of the input. Its event callback checks the source target; the original live editor's root-applied guard is preserved in the baseline and will not be overwritten by an old copy.

## Runtime scope extension and open integration work

Root approved staged runtime changes only in `LuaExecution/ILuaModRuntime.cs`, `LuaCsModRuntime.cs` and `Infrastructure/RbxWorldPackageContracts.cs` for an optional immutable per-invocation budget capability. Existing global quotas/public interfaces must remain unchanged. Additional exact capability forwarding files require root approval.

The existing real Rbx `RunModChunk` accepts a budget but does not accept a caller cancellation token. Per-call finite instruction/time bounds can be forwarded without changing global quotas; true during-VM cancellation forwarding needs an actual supporting engine seam. Pre/post token checks or a Task wrapper will not be described as in-loop cancellation.

Root also requested that Gateway integration remain intact: do not overwrite older `tools/portable/Tests/CoreAI.Portable.Tests.csproj` links or README edits. The editor harness will be a separate additive candidate project rather than a replacement for that shared project. Any baseline drift will be recorded before handoff.

Remaining work: finish adapter integration and bounded execution forwarding, repair/normalize the staged ready-view dependencies, add portable and attached-UI regressions, a separate explicit harness candidate and the feature guide; complete source manifest and static review. Runtime, performance, IME, player/WebGL and final integration outcomes remain PENDING.
