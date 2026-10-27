# Persistence notification and lifetime follow-up

Status: implementation complete; all execution is pending root. This implements accepted `DELTA-PERSIST-001/002` from frozen report 33.

## Decisions and scope

Before planning: restore notification of a completed trim before durability confirmation, preserve the primary storage failure, and retain admitted-operation ownership until callbacks and confirmation settle. Both the shared path semaphore and instance semaphore must be released before any host work. Inspect the same premature lease exit in the other repaired async APIs and keep their receipt/cancellation/cache/revision rules.

Before delegation: this is an exclusive implementation leaf. The write lease contains only `FileAgentMemoryStore.cs`, `FileAgentMemoryStoreEditModeTests.cs`, and this report. No further agent, build/test, Unity/MCP, provider/network call, Git mutation, assets/metadata, shared tracker or other runtime file is authorized. Root explicitly permitted an internal instance confirmation port if the real immediate WebGL port could not provide a deterministic Editor refusal control.

Before accepting: source/test changes were read back, all six async gate/lease exits were inspected, and static whitespace/prose checks were run. Tests are authored, not executed. Report 33 remains unchanged with SHA-256 `A39F0C0027E439400D6E929A917D549835361F792917CC9F1ABFC7EAABC68123`.

## Actual runtime changes

| Method or member | Change and invariant |
| --- | --- |
| Public five-argument constructor | Its exact parameter types, names, order and defaults remain. It forwards to one internal six-argument required-arity constructor; no public overload, optional suffix overlap or global/static hook was added. |
| Internal constructor, `_persistenceConfirmation`, `ConfirmAutomaticPersistence` | One readonly instance `Func<CancellationToken, Task<bool>>` supplies the async persistence answer. A null internal argument selects the existing real `CoreAiWebGlPersistence.SyncAsync(...).AsTask()` behavior. This is an internal deterministic regression port, not a new public persistence system. |
| `ConfirmDurableAsync` | Uses the instance port. False still throws the existing arming IOException; receipt settlement still passes `CancellationToken.None`. The default port remains immediate and does not wait for a browser transaction. |
| `TryLoadAsync` | Explicitly releases its path gate after the instance gate, then settles inside its still-live `MutationLease`. Its pure-load `wrote: false` and migration receipt/error handling remain. |
| `SaveAsync`, `MutateAsync`, `ClearAsync`, `ClearChatHistoryAsync` | Keep their existing explicit path/instance gate release, but move settlement inside the operation lease's scope. Disposal rejects new calls while retaining admitted operation/cache ownership through a pending or failed confirmation. |
| `AppendLineAsync` | Releases both storage gates, awaits the captured-context trim notification, then settles the actual write/migration before releasing the operation lease. A callback failure is captured so it cannot skip settlement. Original storage failure takes priority; otherwise a confirmation failure wins; if storage/confirmation succeed, the captured callback failure is rethrown. No trim is invented for a storage append that never returned an accepted trim outcome. |
| Save/mutate XML documentation | Removes the old browser-transaction-completion claim. WebGL's answer confirms automatic persistence is armed. |

No migration helper, shared pending registry, history revision/clear algorithm, cache admission/eviction, append deduplication policy, public API, file encoding, receipt observation rule or synchronous persistence port was changed. The existing synchronous `QueueFlush` behavior remains on its real port. No new `Task.Run` or `ConfigureAwait(false)` was added to the runtime callback/confirmation path; existing gate/I/O awaits remain as required by the file's deadlock guard.

## Authored regression controls

Five methods add six cases to `FileAgentMemoryStoreEditModeTests`:

- `AsyncHistoryTrim_DisposeRetainsAdmittedCallbackAndResidentLease`: uses the existing held synchronization context and a controlled initial mutator to force deferred notification. While the append remains pending, Dispose must retain one resident partition, reject new operations, and let a healthy peer read the newest committed row through the released shared gate. Releasing the callback completes exactly one notification on its captured context, settles the admitted append and releases resident resources. Controlled waits have five-second bounds; the initial mutator has a ten-second escape and releases in finally.
- `AsyncHistoryTrim_RefusedArmingNotifiesAndAllowsReentryBeforeIOException(false/true)`: the actual append uses the internal false-answer port, with both a successful and a subsequently throwing handler. Outside-callback assertions verify trim-before-confirm order, correct counts/context, successful synchronous reentry into the actual newest history, then the expected arming IOException. A later successful memory save and fresh-peer read prove the trim/row was not replayed; this does not invent idempotency for an explicitly repeated append request. Callback observations are recorded and asserted outside so primary failure precedence cannot hide a failed test assertion.
- `AsyncHistoryTrim_HandlerFailureStillSettlesTheCompletedWrite`: an actual trim handler throws a sentinel exception while confirmation succeeds. The same sentinel reaches the awaiting caller after exactly one confirmation; the live store and a peer both read the completed append. This preserves handler failure containment without losing the write's settlement.
- `AsyncMigration_PrimaryStorageFailureSurvivesConfirmationFailure`: an existing directory at the retirement destination lets canonical copy succeed and makes legacy retirement fail. The port also throws. Assertions preserve the actual original filesystem failure, confirm exactly once with an uncancellable token, retain original canonical/legacy bytes, and let a fresh peer complete migration after the obstruction is removed.
- `AsyncSave_PendingConfirmationRetainsOperationAfterReleasingBothGates`: a pending instance confirmation holds a real completed save. A peer reads the new memory before confirmation, Dispose retains the admitted resident partition while the Task stays pending, and new calls are rejected. Releasing confirmation settles the original save and drops resident ownership. This controls the same repaired lifetime outside the append path.

Existing successful trim/context/gate controls and the rest of the file remain. The existing asynchronous exception helper is reused rather than calling blocking `Assert.ThrowsAsync` on Unity's context.

## Static verification and exact root checks

- Scoped `git diff --check` on the two C# files exited 0. Both files contain zero `var` declarations and zero Cyrillic lines. Read-back confirms one public five-argument constructor and one internal required-arity six-argument constructor.
- All six `SettleAsync` sites are now inside their admitted-operation lease, after explicit gate release. Append notification precedes settlement and its callback failure is only thrown after settlement succeeds.
- The fixture's source inventory contains 65 declared NUnit cases, including the five new methods/six cases. This is not a discovery or pass count. No compiler or runner was invoked.
- Final read-only SHA-256 checks: Source `372DE5F4CADF4849E4843B10ECA657DCAA06B9C176802DFB7816BB5CB54927C6`; tests `1E031E68C69AAF9D5CFE323ACC10282FB14577300BEF53463FD66E4190D157F6`. Frozen report 33 still matches the hash above. Direct report checks found zero trailing-whitespace lines and zero Cyrillic lines.

Root's exact Unity EditMode fixture filters are `CoreAI.Tests.EditMode.FileAgentMemoryStoreEditModeTests`, `CoreAI.Tests.EditMode.MemoryFileStoreRegressionTests`, and `CoreAI.Tests.EditMode.PrReviewPersistenceEditModeTests`. For the smallest new-test selection, use the five exact method names listed above; the refused-arming method must discover both boolean cases. Expected: named cases are discovered, compile, pass, and finish without blocked storage gates, missed notifications, duplicate rows or premature resident cleanup. Run the existing async gate/context/disposal and migration controls alongside them.

## Completion accounting

- CHANGED: the assigned Source store, its existing EditMode fixture, and this report only. Historical reports and other files remain untouched by this worker.
- VERIFY OUTPUT: static source/read-back and document checks only; no build/test/Unity execution.
- NOT DONE: root compilation, Unity/portable checks, real browser/player arming behavior, device/AOT and broader fault/load gates. The instance-port cases prove production append/settlement behavior under a controlled answer when executed; they do not prove the browser enables automatic persistence or acknowledges an IndexedDB transaction.
- CONCERNS: root must verify the default-port path and constructor ABI with the real build and run the added callback/receipt/disposal cross-products. No corruption claim or new cross-instance pending-confirmation guarantee is made.
- QUESTIONS: none. The implementation lease is ready to return to root.
