# Queue writer allocation completion, 2026-10-27

## Task and ownership

- GOAL: Explain the fresh portable Core allocation failure using current receipts and actual allocation sources, then fix the product path without changing its allocation ceiling.
- SCOPE: `Assets/CoreAI/Runtime/Core/Features/Orchestration/QueuedAiOrchestrator.cs`, `Assets/CoreAI/Tests/EditMode/CoreHotPathAuditEditModeTests.cs`, and this report. Previous invocation/runtime files stayed source-final and untouched.
- Existing authorized memory-scope/admission/ownership repairs in the queue were preserved. No Git mutation, Unity/MCP, shared build/test invocation, asset/meta write, or nested worker was performed by this worker. Root owns executable gates.

## Decisions before implementation

- Real goal: A producer writing ordinary chunks into available capacity must create no per-chunk objects. The complete parked-reader path must remain within the existing additional 192 B/chunk ceiling.
- Implied requirements: Both cancellable and non-cancellable tokens; synchronous completion when capacity exists; exact chunk delivery; bounded chunk/byte accounting; unchanged cancellation checks, completion refusal, oversized handoff, backpressure, consumer abandonment, terminal delivery, and reader signaling.
- Reuse: Existing `AsyncChunkQueue`, `ValueTask`, reusable reader signal, backpressure loop, and complete production queue allocation harness. No queue replacement, new synchronization primitive, or task-per-chunk wrapper was introduced.
- Most likely mistake pre-empted: Removing cancellation or raising the threshold to compensate for a Debug-only failure. A direct zero-byte writer regression isolates the actual producer allocations in both build configurations.
- Out of scope: Reworking admission/fairness, changing memory/actor context ownership, changing any allocation threshold, optimizing blocked writes, changing provider behavior, and rewriting the test pump.

## Investigation

The original `QueuedOrchestrator_ParkedReader_AddsNoHeapTrafficPerChunk` fixture is a valid per-thread measurement: it installs a single-thread synchronization-context pump, drains all posted continuations on that same thread, performs two complete warm-up passes, and measures the third pass with `GC.GetAllocatedBytesForCurrentThread`. The baseline and queued stream both deliver the same 4,000 text chunks plus terminal completion. The inner provider yields before every text chunk, so the reader repeatedly parks.

The failing receipt's `TestMethod.codeBase` identifies `tools/portable/Tests/bin/Debug/net8.0/CoreAI.Mods.Tests.dll`. The failure reproduces on a focused rerun with the same exact values. No unsupported environment/noise diagnosis was made.

Source inspection identified cancellation lambdas in `AsyncChunkQueue.WriteAsync` capturing the token and per-iteration `handoff` / `wait` locals. Their display classes are allocated even when the write enqueues synchronously and never reaches a cancellation registration. Read-only PE/metadata inspection of the existing compiled assemblies confirmed:

- `<>c__DisplayClass20_0`: `cancellationToken`.
- `<>c__DisplayClass20_1`: `handoff`, `wait`, and the reference to the outer display class.
- Debug `<WriteAsync>d__20` derives from `Object`, and `WriteAsync` starts with a `newobj` instruction.
- Release `<WriteAsync>d__20` derives from `ValueType`. Both configurations still contain the two display classes.

The added direct writer test reuses a queue, one chunk, and one token, drains the queued chunk after every write, and warms the exact path twice. It checks synchronous completion and reference-identical delivery on every iteration. Assertions and logging occur outside the measured loop. The only measured operation is 4,000 ordinary capacity-available writes and takes; the requirement is exactly zero allocated bytes.

## Root-run baseline evidence

| Receipt | Configuration | Inner / complete queue | Additional complete-path allocation | Direct free writes, both token cases |
| --- | --- | --- | --- | --- |
| `artifacts/completion-20261027/core-results/core-completion1.trx` | Debug | 200 / 505 B per chunk | 305 B per chunk, failed 192 B ceiling | Not present yet |
| `artifacts/completion-20261027/core-perf/core-perf-focused1.trx` | Debug | 200 / 505 B per chunk | 305 B per chunk, failed | Not present yet |
| `artifacts/completion-20261027/core-perf/core-perf-red2.trx` | Debug | 200 / 505 B per chunk | 305 B per chunk, failed | 832,000 B / 4,000 = 208 B per write, both failed |
| `artifacts/completion-20261027/core-perf/core-perf-release-red1.trx` | Release | 200 / 360 B per chunk | 160 B per chunk, passed | 256,000 B / 4,000 = 64 B per write, both failed |

The direct measurements separate 64 B of cancellation display classes in both configurations from 144 B of additional Debug async-frame allocation. The complete-path difference between configurations is consistent with those actual producer allocations. Passing the old complete-path gate in Release did not make its 64 B/write traffic acceptable; the new direct regression catches it.

## CHANGED

`AsyncChunkQueue.WriteAsync` now has a non-async available-capacity path. It estimates the same payload size, observes cancellation, checks cancellation/completion/chunk capacity/byte capacity under the existing signal lock, enqueues and accounts bytes, fires the existing reader signal, and returns a completed default `ValueTask`.

If the token is cancelled, the buffer is complete, capacity is exhausted, or a chunk needs an oversized handoff, execution delegates to `WriteWhenBlockedAsync`. That method contains the original async loop, cancellation registrations, capacity waits, handoff behavior, cleanup, and signal calls. It rechecks all predicates under the lock, so a reader freeing capacity between the first check and fallback remains safe. The slow loop is retained intact rather than replaced with recursive retries.

`QueueWriter_AvailableCapacity_AllocatesNothingPerChunk(false/true)` adds two portable allocation regressions. The original 192 B/chunk complete-pipeline assertion, warmup, pump, and measurement remain unchanged.

## VERIFY OUTPUT

- Root-run red evidence and compiled metadata were read directly as documented above.
- Reviewed the complete owned source diff, including the pre-existing memory-scope/provider changes. The performance patch does not alter those changes.
- Compared fast-path predicates and enqueue/accounting/signal order with the original async loop. Cancellation, closed-buffer, capacity, and oversize paths still enter the original guarded loop. Existing backpressure and terminal-delivery tests remain required root gates.
- `git diff --check -- Assets/CoreAI/Runtime/Core/Features/Orchestration/QueuedAiOrchestrator.cs Assets/CoreAI/Tests/EditMode/CoreHotPathAuditEditModeTests.cs`: exit 0, no output.
- Source-final handoff was sent after the product fix; only this report remained editable afterward.
- Post-fix compile/test results are pending the root's gates at the time of this handoff. No post-fix pass is inferred from source inspection.

## Exact root repro filters

Run the same filter in Debug and Release after a real rebuild; the baseline diagnostics above require the rebuilt test containing the new writer cases:

```powershell
dotnet test tools/portable/Tests/CoreAI.Portable.Tests.csproj --no-restore -c Debug --filter "FullyQualifiedName~QueueWriter_AvailableCapacity_AllocatesNothingPerChunk|FullyQualifiedName~QueuedOrchestrator_ParkedReader_AddsNoHeapTrafficPerChunk"
dotnet test tools/portable/Tests/CoreAI.Portable.Tests.csproj --no-restore -c Release --filter "FullyQualifiedName~QueueWriter_AvailableCapacity_AllocatesNothingPerChunk|FullyQualifiedName~QueuedOrchestrator_ParkedReader_AddsNoHeapTrafficPerChunk"
```

Then retain actual TRX evidence for affected behavior using:

```powershell
dotnet test tools/portable/Tests/CoreAI.Portable.Tests.csproj --no-restore -c Debug --filter "FullyQualifiedName~CoreHotPathAuditEditModeTests|FullyQualifiedName~QueuedAiOrchestratorEditModeTests|FullyQualifiedName~ExecutionAdmissionRegressionTests"
```

Root should rerun the full Core leg after the affected checks because the fresh full leg exposed the defect.

## NOT DONE

- This worker did not run shared builds/tests or Unity. Root owns Debug/Release positive evidence, affected backpressure/cancellation/admission coverage, full Core accounting, native compile, and independent acceptance.
- No runtime/player/WebGL/performance claim beyond the named managed allocation contract is made.

## CONCERNS

- Blocked/oversized writes still allocate their existing waiter and cancellation objects. The change removes allocations from ordinary capacity-available writes; it does not claim that backpressure is allocation-free.
- The queue remains single-producer/single-reader. Predicate checks, enqueue, and byte accounting stay under the existing lock; no broader concurrent-writer guarantee was introduced.

## QUESTIONS

None. Current receipts, compiled metadata, and the red direct regressions established the product allocation source without changing the contract.
