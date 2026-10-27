# PR-MEM-001 persistence fix design handoff

Status: design-only and frozen for root adjudication. Runtime remains pinned to `bc3fd74f0a889bfe1b1e7babc6bc02ed48a7c05e`. Reports 02/05, production code, tests and Git were not changed.

The concrete plan is [persistence-fix-plan.md](../../artifacts/pr-review-20261025/persistence-fix-plan.md).

## Goal and decisions

Notify the actual mutations introduced by read-triggered canonical migration and settle them through the store's existing persistence ports. Preserve identity validation, original bytes/backups, ambiguous-data refusal, public APIs, CAS/cache semantics and admitted-operation lifetimes.

Use a call-owned internal mutation receipt that survives exceptions. Latch after observed successful copy or retirement, including copy-success/retire-failure. Inspect an observable newly created destination after a failed copy under the existing in-process gates. Do not replace `File.Copy`, notify before every read, infer unobservable OS effects, or add a new copy engine/format/global arming flag. An already thrown I/O error remains an observable failure.

Pure helper bookkeeping occurs under the existing gates. Host confirmation happens only after the outer API releases the file gates. The canonical-only/no-effect path creates no new pending generation and calls no persistence hook.

## Exact future file lease

- Runtime: `Assets/CoreAI/Runtime/Core/Features/AgentMemory/AgentMemoryFileKey.cs`; `FileConversationSummaryStore.cs` in the same folder; `Assets/CoreAiUnity/Runtime/Source/Features/AgentMemory/Infrastructure/FileAgentMemoryStore.cs`.
- Tests: `Assets/CoreAiUnity/Tests/EditMode/MemoryContractRegressionTests.cs`, `FileConversationSummaryStoreEditModeTests.cs`, `MemoryFileStoreRegressionTests.cs`, `FileAgentMemoryStoreEditModeTests.cs`.
- User documentation: `Assets/CoreAI/Docs/AGENT_BUILDER.md`.
- Change nothing else. Scoped decorators, orchestration/chat/facade files, public store interfaces, registry algorithms and metadata are outside this lease. Root assigns implementation after the original audits finish.

## Store behavior

The summary store already has a shared pending-generation protocol. Enroll one outer migration operation using `PathState.RecordMutation`, including an exceptional exit after an observed write. Reuse/coalesce a normal save/clear confirmation covering the same effects. Retain the path/operation lease until confirmation settles, keep failure pending for a peer, and never replay committed copy/retirement bytes because the hook refused arming.

Carry value, original failure, mutation receipt and pending request out of the gated core. This prevents `LoadSummaryCore`'s best-effort catch or a partial exception from losing the notification. Sync load retains log/empty-result storage-failure behavior; strict async load and transaction opening surface their existing errors. When only the async hook is configured, run the existing synchronous-confirmation preflight immediately before migration effects: no async wait in a sync method, and no refusal for an unchanged canonical read.

Async confirmation of this operation's observed effect uses `CancellationToken.None`, outside the file gate and on the entry context. A successful old request acknowledges only its own generation. Transaction opening captures text and baseline together after migration enrollment; preserve its pending-confirm/reread loop and cleanup on disposal/cancellation.

`FileAgentMemoryStore` has no injected custom confirmation hook. Use its actual best-effort synchronous `QueueFlush`/`Sync` and strict async `ConfirmDurableAsync` ports after locks for observed migration work. Include read-only and `persistToDisk: false` APIs that nevertheless migrated files, and avoid a duplicate check when a normal persistent write already confirms that outer operation. A stronger cross-instance deferred-confirmation system for that store requires a separate root decision.

## Controls and regression acceptance

- Preserve no-effect reads; true/false/pending/throwing callbacks; copy-success/retire-failure; observed partial-copy effects versus refusal before effects; sync async-only-hook refusal before writes; fresh-peer confirmation without byte replay.
- Cover races with new generations, transaction save/clear/commit, alias paths, CWD changes, cancellation and disposal. Freeze absolute I/O identity before awaits. Do not change history clear revisions or evict transient cache rows for a filename-only migration.
- Keep WebGL inline/no-thread and no-blocking-wait behavior. Arming is not an IndexedDB completion receipt; no manual `FS.syncfs`.
- One copy-plus-retirement is one logical receipt. If the regression requires one hook invocation across concurrent readers, single-flight the existing private `DurabilityRequest` attempt outside locks; otherwise do not claim that stronger guarantee. Failure may permit a later retry attempt.

## Open decisions and accounting

Root must choose concurrent confirmation single-flight policy and approve the bounded observed-side-effect interpretation of exceptional `File.Copy`. No new public interface or global flag is needed. Stronger Source-store pending semantics are deliberately not smuggled into the repair.

- CHANGED: this handoff and the artifact plan only.
- VERIFY OUTPUT: inspected the actual helper, summary pending/hooks/read boundaries and file-memory arming/lifetime paths; no implementation result is claimed.
- NOT DONE: production implementation, regression writing/execution, builds/tests, Unity/player/WebGL and Git/publication.
- CONCERNS: exception paths can erase a returned-only mutation signal; callbacks under locks can deadlock/reenter; async-only hooks cannot be awaited synchronously; callback multiplicity must not be overstated.
- QUESTIONS: the bounded design decisions above remain with root and do not block the original source audits.
