# PR-MEM-001 persistence repair implementation receipt

Status: IN PROGRESS. Root approved design `17-persistence-fix-design.md` and
`artifacts/pr-review-20261025/persistence-fix-plan.md`; source writes are authorized on `demo`.

## Exact lease

Runtime:

1. `Assets/CoreAI/Runtime/Core/Features/AgentMemory/AgentMemoryFileKey.cs`
2. `Assets/CoreAI/Runtime/Core/Features/AgentMemory/FileConversationSummaryStore.cs`
3. `Assets/CoreAiUnity/Runtime/Source/Features/AgentMemory/Infrastructure/FileAgentMemoryStore.cs`

Tests:

4. `Assets/CoreAiUnity/Tests/EditMode/MemoryContractRegressionTests.cs`
5. `Assets/CoreAiUnity/Tests/EditMode/FileConversationSummaryStoreEditModeTests.cs`
6. `Assets/CoreAiUnity/Tests/EditMode/MemoryFileStoreRegressionTests.cs`
7. `Assets/CoreAiUnity/Tests/EditMode/FileAgentMemoryStoreEditModeTests.cs`

User documentation:

8. `Assets/CoreAI/Docs/AGENT_BUILDER.md`

This record: `dev-docs/pr-review-2026-10-25/20-persistence-repair.md`.

`PrReviewPersistenceEditModeTests.cs` is a read-only shared baseline and must not be weakened.
Nothing outside this lease is changed by this worker.

## Approved behavior

Read-triggered canonical migration is an observed storage mutation and must settle through the
store's existing persistence ports. The helper keeps `File.Copy` and the legacy retirement, latches
a call-owned receipt after a successful copy or retirement (including copy-success/retirement-failure),
and after a failed copy latches only an observably created destination. Validation, backups,
ambiguity refusal, identity checks, public interfaces and byte content are unchanged.

The summary store enrolls one outer migration generation through `PathState.RecordMutation` under
the file gate and confirms it outside every file gate on the entry context. Canonical-only reads
create no pending generation and call no hook. An async-only synchronous preflight runs before real
migration effects. A refused confirmation stays pending for a fresh peer without replaying bytes.
Concurrent confirmation multiplicity remains the existing behavior; no single-flight or
exactly-once promise is added.

The file memory store aggregates document, history and embedded-conversation migration effects in
one outer receipt and uses its existing `QueueFlush`/`ConfirmDurableAsync` ports after both gates.
`persistToDisk: false` and read-only calls that migrated are covered. The existing immediate arming
receipt is retained; no shared pending protocol or global arming flag is added.

## Accounting

- CHANGED: this record initially; implementation follows in the same task.
- VERIFY OUTPUT: not run. Root owns compilation, EditMode execution, Unity/MCP and Git.
- NOT DONE: static diff review by this worker only; no build, test, Unity or Git mutation.
- CONCERNS: none beyond the approved design boundaries at this point.
- QUESTIONS: none block implementation.
