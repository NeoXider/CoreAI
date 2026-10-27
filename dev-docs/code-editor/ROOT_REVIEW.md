# Runtime editor integration review

Status: root review and executable acceptance in progress. Workers were stopped at the owner's
request; root owns all remaining changes, Unity operations and Git. The new NPC-agent system
was cancelled and its isolated candidates are not part of this change.

## Accepted evidence

- Integration receipt: `artifacts/editor-integration-20261026/receipt.json`, 32 candidates with
  per-file source hashes and original backups. Later root corrections are described below.
- Actual additional bundled Lua resources: 13/13 tests passed in `samples-results3/additional-samples3.trx`.
- Initial-chunk budgets/cancellation: 9/9 passed in `limits-results2/invocation-limits2.trx`.
- Portable Lua full run: 1,962 Passed / zero Failed / 41 NotExecuted actual result entries in
  `lua-full-results1/lua-editor-full1.trx`.
- Portable Core full checkpoint: 2,951 Passed / zero Failed / three NotExecuted actual result
  entries in `core-full-results1/core-editor-full1.trx`. A later completion regression was added.
- Fresh native Unity selection: 56 total, 47 Passed, nine Failed; raw evidence is retained in
  `editor4-tests1-result.json`. This is a negative checkpoint, not editor acceptance.

All receipt paths above are under `artifacts/editor-integration-20261026/`. Portable tests do
not prove native rendering/input, PlayMode, built-player/WebGL behaviour or browser durability.

## Findings and corrections

| Finding | Correction | Current proof |
| --- | --- | --- |
| Copied line-selection/traceback helpers retained Redo business dependencies. | Portable status text and bounded Lua line parsing replace those references. | Core source compiles and contract tests run. |
| Public cancellation overloads made existing `Resume(default)` calls ambiguous. | Required VM resume helpers are internal and distinctly named; original public calls remain. | Compile-only legacy call coverage plus real VM tests. |
| Scheduler instruction trips lacked typed causes needed by the interpreter result classifier. | Keep exact Lua error text/author line and attach typed step, timeout or memory causes. | The original typed assertion passes with and without Rbx; it was not weakened to match text. |
| Opening/editing Lua could expand tabs and change nonbreaking spaces inside literals. | Automatic normalization changes line endings only. Explicit indentation commands remain separate. | Actual literal/caret preservation regression passed. |
| A result fence omitted mod identity. | Include mod id as well as document, language, revision and run id. | Cross-mod reused-run-id regression passed. |
| Completion offered the current prefix before a useful keyword. | Exclude the current in-progress prefix from document-word candidates. | New contract/native completion reruns pending. |
| Unity 6.6 exposes the TextElement notification setter explicitly. | Use its verified `INotifyValueChanged<string>` interface. | Native compile passed this point. |
| Selection used a SelectAll/SelectNone workaround. | Use the existing `TextField.SelectRange` API. | Native selection/undo/reattach rerun pending. |
| Detached measurement produced repeated DPI warnings. | Defer gutter and font measurements until the mirror/panel exists. | Native lifecycle/warning rerun pending. |
| Presentation fixtures counted Editor frames despite a 16 ms deferred presentation. | Wait for the exact presentation under a finite real-time deadline while repainting the window. | Native raw-source/highlight assertions retained; rerun pending. |
| The Hub menu added the World binder only to the instantiated object after saving the prefab. | Add available optional binders to the authored BuildHub object before saving. | The newly strengthened binder assertion caught the gap; rerun pending. |
| Two controller fixtures tried to drive MonoBehaviour Start through SendMessage in EditMode. | Expose idempotent runtime presentation initialization used by Start and host injection; fixtures test that public path. | Actual MonoBehaviour lifecycle remains a separate PlayMode/demo gate. |
| The scope fixture registered a duplicate concrete source store. | Use the scope's actual registration under a unique test store id. | Native provider-snapshot rerun pending. |

The existing source expectation is a synchronous pre-save comparison. This review does not claim
atomic cross-process/reentrant compare-and-swap. A host with concurrent writers must serialize
its service/store operations or supply a transactional implementation.

## Remaining gates

Finish native source/selection/completion/disposal/host-binding regressions, inspect all 17
published scenes in PlayMode and perform actual model-authored mod create/edit. Computer Use
requires `node_repl`, which is not provided in the current session; mouse/keyboard acceptance
through that tool remains unverified. No main promotion or new release is accepted by this report.
