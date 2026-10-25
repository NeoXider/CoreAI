# Benchmark final integration follow-up

## Final root execution acceptance - 2026-10-25

`artifacts/fix-wave-20261025/unity-playmode-fake-final.xml` records 388 actual cases: 386 Passed, zero Failed and two batch-mode end-of-frame skips; process exit is zero. The benchmark assembly contributes 247 Passed and one of those skips. All four repaired cases, all 34 G6 cases and the previously timed-out soft-budget case passed. The live SkillSet benchmark is absent from this selection. Its earlier two local-model calls remain separate mixed-run evidence, not a model-quality result. The prior timeout remains unreproduced; the retained phase diagnostics do not claim a production fix. Root reviewed and accepts the frozen changes at these measured boundaries.

## Goal and lease

Repair the four newly executable Windows Standalone failures while preserving truthful provider evidence, independent replay compilation and optional-artifact publication. The assigned worker is `gpt-6.1-sol`, reasoning effort `max`, as specified by root; service metadata was not independently queried.

Exact source lease: `Assets/CoreAIBenchmark/Tests/PlayMode/Benchmarks/{GameCreationBenchmarkPlayModeTests.cs,GameCreationBenchmarkHarness.cs,GameCreationBenchmarkParallelSuite.cs,RbxBenchmarkWorld.cs}` and this report. Change nothing else. Root owns all Git operations, builds, tests, Unity/MCP and source-freeze acceptance. Another worker owns G6 geometry. No assets, metadata, historical model reports or live-provider runs belong to this repair.

Before planning: correctness, per-turn reset, capture isolation, immutable sealed evidence, replay ordering/local isolation, original-source fidelity and retryable required publication apply. Provider acceptance must remain distinct from visible output and from empty/synthetic chunks. Optional image failure must never remove available required artifacts or link a nonexistent PNG. Existing benchmark seams and MD11 contracts are reused; no public API change or global print capture is authorized.

Before delegation: no additional agents are authorized. Before accepting: inspect the actual source differences and preserve every existing assertion. The root alone executes the four failing filters, then the non-live benchmark suite and fake gate.

## Receipt and diagnosis

Read `dev-docs/fix-wave-2026-10-24/11-benchmark.md`, `windows-playmode-failures.json` and the four leased source files. Root's receipt is `artifacts/fix-wave-20261025/unity-playmode-windows-final.xml`: 389 cases, 382 Passed, 5 Failed, 2 Skipped. Four failures belong here; the G6 scene-depth failure is owned separately.

- Provider response failures at fixture lines 602/619: `ResponseEvidenceLlm` emits no body when `OpenResponse` is false. `LlmStreamChunk.ExecutedToolCalls` nevertheless defaults to an empty array, and the harness incorrectly treats any non-null collection as evidence. This is a real harness predicate defect, not a fabricated response from the fake. Per-turn activity also needs reset alongside the request's fresh provider state; sealing must freeze both.
- Optional artifact failure at line 876: the required model-card SVG is written successfully, but the optional PNG catch does not embed that available fallback into the Markdown.
- Replay failure at line 924: all world effects, ordering and isolated compilation pass. The executor's established `ReportSuccess` contract returns the first Lua return-value summary, or `nil`; a `print` is not that return value. Root approved keeping the print and every assertion, explicitly returning the same delimiter/quote message, adding exact decoded chunk round-trip assertions, and correcting the inaccurate XML description. No replay/product print-capture behavior is added.

## Progress and validation state

Implementation is complete and C# is frozen for root execution. Source snapshots were captured in tool-session memory for a non-Git diff review, because this lease prohibits Git operations. No test, build, Unity, provider request or Git command has run in this follow-up.

## Final changes

- `GameCreationBenchmarkHarness.cs`: provider evidence requires real generation progress, nonempty text/reasoning, nonempty tool receipts or explicit provider usage. An empty/default receipt collection or synthetic terminal chunk cannot establish acceptance. Usage alone can establish a response but does not manufacture text/tool activity or usable visible output. Fresh requests reset the activity timestamp along with their owned response state; existing seal snapshots remain immutable.
- `GameCreationBenchmarkPlayModeTests.cs`: both provider regressions retain their original assertions and now exercise actual prior-turn activity, timestamp reset, timeout/response freeze, distinct per-capture state and an eight-case evidence matrix. The matrix covers empty, terminal, null receipts, visible text, hidden reasoning, provider progress, real tool receipt and provider usage. These are loops inside existing methods: no test cases or exclusions were added.
- The replay fixture retains its print, syntax failure, partial world mutation, fresh local environment, error order, geometry/name checks and legacy raw-script assertion. It now explicitly returns the printed message under the executor's existing result contract. Exact decoded chunks and model/scenario metadata are compared with their originals, including the newline, quote and Lua long-comment fence text.
- Optional PNG write failure now embeds the already-published SVG model card. Assertions verify required Markdown/JSON/index/results SVG/model-card SVG exist, the failed PNG is not a file, and its name is absent from Markdown.
- `RbxBenchmarkWorld.cs`: corrected only the XML statement that incorrectly described returned values as printed output. Replay/runtime behavior is unchanged.
- `GameCreationBenchmarkParallelSuite.cs`: reviewed the required publication call chain; it already uses the corrected shared writer and remains byte-identical to intake. No other leased or unleased C# file changed.

## Static verification and remaining acceptance

The full before/after line comparison was reviewed and checked by reconstructing both source snapshots exactly. Added lines contain zero implicit `var` declarations, zero Cyrillic lines, zero unapproved comment prefixes and zero trailing-whitespace lines. This is source evidence, not a compiler or executed-test receipt.

The saved Windows XML was independently parsed: 389 total, 382 Passed, 5 Failed, 2 Skipped, with exactly the four named failures in this fixture and the separately owned G6 failure. That receipt predates this patch and is not acceptance of it.

Root read the actual complete source diff and requested freeze. Freeze was confirmed before its new runs; this worker will make no C# changes while root verification is active. No source-level concern remains open in this lease. Execution acceptance remains pending root receipts.

Root verification order:

1. Windows PlayMode benchmark assembly filtered to the four original methods: `LuaReplay_CompilesEachCallAndRetainsRuntimeFailureEffectsInOrder`, `OptionalModelCardWriteFailure_StillPublishesRequiredReportsWithVectorFallback`, `ProviderResponseEvidence_ParallelCapturesRemainIndependent`, and `ProviderResponseEvidence_ResetsBetweenTurnsAndSealFreezesIt`.
2. All 249 authored non-live benchmark cases, retaining existing live-benchmark/recapture exclusions and no new exclusions. Root must derive actual execution counts from its new XML.
3. The full Windows fake PlayMode gate, including the separately owned G6 correction. Root owns final pass/skip/failure accounting and any re-opened source lease.

## Frozen SHA-256 inventory

Paths are relative to `Assets/CoreAIBenchmark/Tests/PlayMode/Benchmarks/`; hashes are over the frozen working-tree bytes. The report is excluded from its own hash inventory.

| File | SHA-256 | Status |
|---|---|---|
| `GameCreationBenchmarkPlayModeTests.cs` | `0b65cbd00db5910a9944a0e844c311607804c267134b6db40b566d25a8683568` | Changed |
| `GameCreationBenchmarkHarness.cs` | `a642200fc6ca3dc4460c4f81cbf03d2b948949b43c42e00d4068f8f013bb5702` | Changed |
| `RbxBenchmarkWorld.cs` | `2e3ab1c985c2d5738c005e657499c67c414dddc55c752620e1b05d4a60f361f4` | XML only |
| `GameCreationBenchmarkParallelSuite.cs` | `b4dc94a7c903e403b74fb5e82752545bbd60d71028f97524266c4c0b22a113e6` | Unchanged from intake |

## Final2 readback and bounded timeout diagnosis

Root's next receipt, `unity-playmode-windows-final2.xml`, reports 389 total, 386 Passed, 1 Failed, 2 Skipped. All four failures owned by the first part of this report now pass, as does the separately owned G6 correction. This is root execution evidence for those changes; the complete gate is not yet green.

The remaining failure is `ParallelBenchmarkPlayModeTests.ProviderIgnoringCancellation_ElapsedSoftBudgetCancelsActiveAttempt`, which passed in the prior Windows receipt. Root extended this worker's lease only to `ParallelBenchmarkPlayModeTests.cs` and this report. Other C# remains frozen. No timeout increase, suppression, exclusion or production budget/watchdog change is authorized.

Before planning: distinguish a cancellation defect from setup, optional rendering/publication, abandoned-task release or teardown delay. The actual suite clock starts through `Stopwatch.StartNew()`; no zero sentinel exists. The soft cancellation predicate compares elapsed real time with the assigned budget; the scheduler watchdog adds the configured cancellation grace and one second. Both remain unchanged.

Source and receipt review:

- Prior receipt: Passed, `0.229293` seconds. Final2: Failed after `16.295306` seconds under the existing 15,000-ms limit. Both XML cases report zero asserts, so that field cannot identify whether assertions executed in this Unity iterator runner.
- Both captured outputs show the same 21-ms scenario result, the budget stop before the second scenario, and a target-complete report path. The cancellation core therefore settled; those outputs alone do not locate the extra time.
- Reviewed `RunTargets`, coroutine stepper/scheduler, `RunScenario`, `ProbeScenario`, `HangingLlmClient`, model-card capture and fixture teardown. Pending waits remain subject to the watchdog; batch end-of-frame capture uses an update yield. No benchmark source writes global Unity time settings. The default token counter uses a local fallback without a ranks provider/network download.
- Optional model-card rendering, required publication, returning to the outer iterator, inline continuation work during `HangingLlmClient.Release()`, and teardown remain hypotheses until a focused timing receipt identifies the phase. No production fix is claimed from inspection alone.

Root approved one fixture-only timing probe. Prefix `[SoftBudgetProbe]` records real Stopwatch milliseconds and managed thread id around conditional setup, MakeRun, the unchanged OnCall budget assignment, RunTargets, the identical `BenchmarkRunStore.AtomicWrite` calls, return/assertions, pending-task release, and teardown environment/native-object/progress/temp-directory cleanup. The original timeout, assertions, stepping, cancellation grace and fake behavior remain intact. The diagnostic C# was frozen before the focused root run; no repeated full-suite runs were used to guess a diagnosis.

Probe snapshot SHA-256: `ParallelBenchmarkPlayModeTests.cs` = `47c9ca81a885624ac550b254b64c6da7e433826d0a667bc619dacc7530341fdd`. Its before/after diff was reviewed and exactly reconstructed; added-line type/English/comment/whitespace scans all returned zero violations. Worker execution remains not run.

The focused root receipt is `artifacts/fix-wave-20261025/unity-soft-budget-diagnostic.xml`, with matching `.log` and `-exit.json`: **1 Passed / 0 Failed / 0 Skipped**, process exit 0, case duration `1.065373` seconds. This worker read the actual XML, process exit and every phase log. The diagnostic fixture hash remains unchanged.

| Phase | Elapsed real time |
|---|---:|
| Provider entered / budget expired | 234.466 / 235.143 ms |
| Required artifact writes | 969.102 through 1027.076 ms |
| RunTargets returned / assertions complete | 1028.688 / 1038.353 ms |
| Pending task release start / complete | 1039.206 / 1041.123 ms |
| Teardown start / complete | 1044.988 / 1056.495 ms |

Every phase executed on managed thread 1. No delay reproduced in setup, release, native destruction, progress cleanup or directory deletion; the cancellation core and all original assertions passed with the original 15-second limit. The prior 16.295-second timeout is classified as **unreproduced**. No production budget, scheduler or cancellation fix is inferred from this passing focused run.

After reviewing the focused receipt, root explicitly approved retaining these bounded, case-scoped phase diagnostics as future failure observability. The earlier temporary-removal plan is superseded by that decision. The trace is not a claimed timeout fix. C# remains frozen at the hash above for one complete hermetic gate; root alone owns its actual final counts and acceptance. No timeout was raised and no framework case was suppressed or excluded.

Root also found a preexisting live `SkillSetBenchmark_WithSkillProxy` in the earlier gates, with local HTTP model calls. Root owns the explicit/category correction in `SkillSetBenchmarkPlayModeTests.cs`; this worker did not edit it. Earlier Windows runs were therefore mixed runs, not proof of a strictly fake-only gate or model quality. The final fake gate will use root's corrected exclusions without suppressing the remaining framework test.
