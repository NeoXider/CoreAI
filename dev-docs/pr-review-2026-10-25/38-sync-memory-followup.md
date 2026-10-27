# Synchronous memory notification lifetime follow-up

Status: implementation complete, C# lease returned and frozen for root checks. No build/test or runtime execution was performed by this worker.

## Scope and decisions

Before planning: fix the concrete synchronous equivalent of the admitted-operation notification lifetime regression. The ordinary chat/transcript trim handler may call Dispose while its append is still on the stack; resident ownership must remain until that handler and admitted call finish. Both the shared path gate and instance gate must already be free during notification. Preserve arguments, migration receipts, best-effort writes, primary failure handling and queued confirmation coalescing.

Before delegation: this is an exclusive leaf implementation; only `FileAgentMemoryStore.cs`, `FileAgentMemoryStoreEditModeTests.cs` and this report are writable. No additional agent, source outside that lease, build/test, Unity/MCP, network/provider, metadata, Git mutation or shared tracker operation is authorized. Gateway review and historical persistence reports remain frozen.

Before accepting: the regression was authored first, then HEAD and current source were compared read-only before applying the fix. Static source read-back and whitespace/prose checks were performed. The new tests are not described as executed red or green. Root had reported 500/500 Unity focused passes before this follow-up; those do not cover its two new cases.

## Confirmed static trigger and minimal repair

HEAD has a function-level `using MutationLease` in both `AppendChatMessage` and `AppendTranscriptEntry`, with an explicit path release before `RaiseTrimmed`. The current pre-follow-up repair had moved that using declaration into the inner try, ending operation ownership before settlement and notification. `MutationLease.Dispose` calls `ExitOperation`; a handler that then calls Dispose sees `_activeOperations == 0` and clears the resident cache before the original admitted call returns. The new two-case fixture captures that count outside any handler assertion.

Only the two synchronous append methods were changed. Their using declaration is function-level again. Each explicitly calls `mutationGate.Release()` in its outer finally after the existing instance-gate release, before `SettleSyncMigration`, original-failure propagation and `RaiseTrimmed`. The lease's final Dispose retains admitted-operation ownership through notification while its idempotent Release does not double-release the semaphore.

No async path, public constructor/API, confirmation port, receipt, cache/revision algorithm, row persistence/deduplication behavior or queued/covered boolean logic was changed. Invalid/no-op arguments keep their original early return paths. Callback exceptions still unwind through the function-level lease; recorded storage failure still prevents notification through the existing failure path.

## Authored regression

`SyncHistoryTrim_DisposeRetainsAdmittedOperationUntilCallbackReturns(false/true)` covers chat and transcript append entry points with one-row caps:

- Seed a row before subscribing, then append another row to cause exactly one real trim.
- Capture that notification occurs before the append returns.
- Start a reentrant synchronous history/transcript read on a worker and wait at most five seconds, proving both storage gates are available while the callback runs. Capture any failure and assert only after the callback returns.
- Call Dispose twice inside the handler, capture resident count, and capture rejection of a new call. No assertion executes inside the handler.
- Outside the callback, assert the actual newest row, one notification, resident count one during callback disposal, new-call rejection and count zero after the append settles. The bounded read is observed/cleaned up even if the gate control fails.

The test uses existing `NewStore`, actual append/read methods and the public resident-count/disposal contract; no source-index assertion, new persistence port or runtime test hook was added.

## Adjacent sync inspection and limit

`TryLoadDetailed`, `GetChatHistory`, `GetTranscriptEntries`, `Save`, `Clear` and `ClearChatHistory` still end their current lease before post-gate `SettleSyncMigration`. That path calls the immediate real `CoreAiWebGlPersistence.Sync`, with no async callback. Its false WebGL answer can subsequently call the optional logger's Warn method. This follow-up does not claim that conditional host-logger lifetime variant is covered or repaired. Root was informed before handoff; no broader sync ownership rewrite or new sync confirmation seam was introduced. The two ordinary, deterministic trim notification violations requested here are addressed.

## Static verification and root gates

- Scoped `git diff --check` on the two C# files exited 0.
- Read-back confirms one function-level operation lease and explicit path release before settlement/notification in each modified method; existing instance-gate finally blocks remain.
- Exact new EditMode filter: `CoreAI.Tests.EditMode.FileAgentMemoryStoreEditModeTests.SyncHistoryTrim_DisposeRetainsAdmittedOperationUntilCallbackReturns`; expected discovery is two boolean cases. Run the full `CoreAI.Tests.EditMode.FileAgentMemoryStoreEditModeTests` fixture and the existing memory/persistence controls before acceptance.
- C# is frozen after handoff so root can run PlayMode/full gates while this report is finalized. No worker test/compile output or runtime acceptance is claimed.

CHANGED: the two assigned C# files and this report only.
VERIFY OUTPUT: static HEAD/current source comparison, read-back and whitespace checks; new regression authored before runtime fix.
NOT DONE: execution of the new cases, root compilation, broader Unity/PlayMode/player/WebGL gates.
CONCERNS: retain the documented conditional post-gate logger boundary as unverified rather than counting this as every synchronous lifetime path fixed.
QUESTIONS: none. Lease returned to root.
