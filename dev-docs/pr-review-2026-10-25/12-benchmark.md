# Independent PR review: benchmark scoring and execution

Status: complete native takeover after the CLI review ended before producing a report. All 24 assigned changed paths and 183 hunks were reviewed. One source-confirmed P2 finding; execution gates remain unrun. Reviewer: native `gpt-6.1-sol`, reasoning effort `max`. Base: `69d5bcc37502cd4bb464a3bf9ac0c53b1624b804`. Review head: `bc3fd74f0a889bfe1b1e7babc6bc02ed48a7c05e`. Checkout: `D:\Git\CoreAI`, branch `demo`.

## Intent and decisions

- Real goal: identify introduced benchmark scoring, attribution, resume/watchdog, publication and fixture defects from the actual changed source and callers.
- Implied requirements: mandatory/aggregate/custom/interrupted score exclusions; attributable environment/model/stall outcomes; sealed per-turn provider/usage/activity evidence; cooperative and noncooperative stop/deadline handling; publication failures and publication-only retry; isolated replay/compile/effect/order fidelity; truthful throughput labels; positive volume/geometry evidence; fair coroutine/UI lifetime behavior and explicit plain-versus-live tests.
- Scope: all 24 group 12 changed paths/hunks and relevant adjacent contracts. English prose and C# 9 explicit types apply. This assigned report is an explicit exception to the default repository audit-document rule.
- Exclusions: other reports/designs, production/test edits, Git mutation, builds/tests, Unity/MCP/network calls, assets, historical model-report rewrites and additional agents. Host account/view policy and documented unsupported batch EOF behavior are not automatically bugs.
- Acceptance: causal changed-line findings only, with a valid trigger, expected/actual behavior, meaningful regression plan and uncertainty. Existing green results and historical findings are not semantic proof. A previously flaky soft-budget case is not assigned a product root cause without evidence.

## Findings

### PR-BENCH-001 [P2] Resetting turn activity makes a fresh continuation inherit the scenario's inactivity age

- Changed anchor: `Assets/CoreAIBenchmark/Tests/PlayMode/Benchmarks/GameCreationBenchmarkHarness.cs:501`, clearing `_lastProviderActivityUtc` when a new client turn begins without storing a current-turn inactivity baseline.
- Valid trigger: a scored G6 run uses its actual 570-second model-turn deadline (600 seconds less the capture reserve) and the default 120-second provider-stall threshold. Turn one executes a tool near the start, then emits provider text/progress until it completes around second 520. Its tool count advances, so `CanContinueFreeBuild` permits a continuation with 50 seconds remaining (greater than its 45-second floor). The new response is accepted, but emits no text/reasoning/tool delta before the model deadline at second 570. Its actual inactivity age is about 50 seconds, below the configured stall threshold.
- Expected: the final deadline is attributed to the model under the published accepted-response deadline policy. Resetting response/activity evidence must not make the new turn inherit elapsed time from earlier active work. A genuinely stalled continuation exceeding 120 seconds should still be Environment.
- Actual and causal path: the changed reset discards turn one's recent provider-activity timestamp. At scenario end, `ProviderActivityGapSeconds` at lines 2212-2228 uses the older tool timestamp or whole-scenario latency because current provider activity is null; it reports approximately 570 seconds rather than the new turn's 50 (plus any bounded cancellation grace). With the current response observed and no transport failure, lines 2627-2635 classify this as an Environment provider stall. That result is excluded from model scoring and is eligible for infrastructure retry in `RunTargetSuite`, instead of retaining the measured model deadline.
- Introduced behavior: before the added reset, provider activity near second 520 yielded an approximately 50-second gap in this same sequence. The reset correctly prevents old activity from being reported as new generation evidence, but the inactivity calculation needs a separate current-turn start/admission baseline rather than falling back to scenario start or an earlier tool call.
- Regression plan: use a controlled clock and two accepted turns, with early tool work and late text/progress in the first turn, then a quiet continuation that reaches the scenario deadline below the stall threshold. Assert Model attribution, retained grading eligibility and no environment retry; separately advance the continuation beyond the threshold and assert Environment. Exercise the sealed/cancelled result so a late callback cannot change the evidence. Do not run a real ten-minute provider test merely to check this timeline.
- Uncertainty: source-confirmed timestamp/control-flow mismatch; no runtime reproduction or injected-clock test was executed here. This is separate from the previously flaky soft-budget fixture, whose timing history supplies no root cause for this finding.

## Coverage and verification

- Group 12 inventory: 24 of 24 changed paths, all 183 hunks, 1551 added and 181 deleted lines. Complete changed-hunk review includes deleted code, every added regression body/helper and all added historical developer documents. No assigned changed path/hunk remains unread. Existing large sources received complete changed-hunk review plus relevant surrounding call paths, not an exhaustive audit of every untouched line or frozen geometry array.
- No builds, tests, Unity, provider, MCP, network or runtime probes executed by this reviewer.

### Exact changed-hunk ledger

Ranges are the complete `git diff --unified=0` old-to-new headers. Missing count means one line; count zero denotes an insertion/deletion boundary. Every listed hunk was read.

| Repository-relative path | Reviewed old-to-new ranges |
| --- | --- |
| `Assets/CoreAI/Runtime/Core/Features/Benchmarking/BenchmarkReport.cs` | `-32,0 -> +33,2; -99 -> +101,2; -126 -> +129,2; -133 -> +137; -135 -> +139; -234 -> +238; -236,0 -> +241,3; -238,7 -> +245,2; -249,0 -> +252,14; -252 -> +268` |
| `Assets/CoreAI/Runtime/Core/Features/Benchmarking/BenchmarkReportFormatter.cs` | `-27,0 -> +28,2; -102,2 -> +104,2; -105 -> +107; -812 -> +814; -956,0 -> +959; -976,0 -> +980,2; -1092,0 -> +1098,2; -1102,0 -> +1110; -1138,0 -> +1147,2; -1243,2 -> +1253; -1279,12 -> +1287,0` |
| `Assets/CoreAI/Runtime/Core/Features/Benchmarking/BenchmarkRunStore.cs` | `-156,0 -> +157,3; -222 -> +225,2` |
| `Assets/CoreAI/Runtime/Core/Features/Benchmarking/GoalScore.cs` | `-47,0 -> +48,9` |
| `Assets/CoreAI/Runtime/Core/Features/Benchmarking/ModelComparison.cs` | `-61 -> +61; -787 -> +787; -857 -> +857; -878 -> +878; -890 -> +890,3; -897 -> +899; -899 -> +901; -903 -> +905,5; -906 -> +912; -908 -> +914; -917,2 -> +923,2` |
| `Assets/CoreAI/Runtime/Core/Features/Benchmarking/RoleFitness.cs` | `-124 -> +124` |
| `Assets/CoreAI/Runtime/Core/Features/Benchmarking/ScenarioResult.cs` | `-33,0 -> +34,6; -82 -> +88; -84,0 -> +91,3` |
| `Assets/CoreAIBenchmark/README.md` | `-46 -> +46; -113 -> +113; -116 -> +116,3; -120 -> +122; -139 -> +141; -142 -> +144,3; -170 -> +174; -211,0 -> +216; -220 -> +225; -482 -> +487; -516 -> +521; -527 -> +532; -530 -> +535` |
| `Assets/CoreAIBenchmark/Tests/PlayMode/Benchmarks/BENCHMARK_DESIGN.md` | `-24 -> +24; -30 -> +30,3; -37 -> +39,3; -57 -> +61; -81,3 -> +85,12; -349 -> +362,2; -360,2 -> +374,5; -366 -> +383,5` |
| `Assets/CoreAIBenchmark/Tests/PlayMode/Benchmarks/G6BuildAnalysisPlayModeTests.cs` | `-377 -> +377,11; -510,0 -> +521,41` |
| `Assets/CoreAIBenchmark/Tests/PlayMode/Benchmarks/GameCreationBenchmarkHarness.cs` | `-40,0 -> +41; -190,16 -> +191,7; -240,0 -> +233; -277 -> +270; -289 -> +282; -298 -> +291; -302,0 -> +296,5; -304,0 -> +303; -315,4 -> +314,2; -321,0 -> +319,3; -400 -> +400; -402 -> +402,4; -409 -> +412,4; -489 -> +495; -494,0 -> +501; -511 -> +518; -529 -> +536; -546 -> +553; -548 -> +555; -1842 -> +1849; -2201,8 -> +2208,2; -2238 -> +2239; -2264,0 -> +2266; -2286 -> +2288; -2300 -> +2302; -2305 -> +2307; -2334,0 -> +2337,3; -2497,0 -> +2503,2; -2509,0 -> +2517; -2644 -> +2652; -2653 -> +2661; -2684 -> +2692; -2689,3 -> +2697; -2704,0 -> +2711,2; -2721 -> +2729; -2723 -> +2731,2; -2745 -> +2754,2` |
| `Assets/CoreAIBenchmark/Tests/PlayMode/Benchmarks/GameCreationBenchmarkParallelSuite.cs` | `-45,0 -> +46,3; -123 -> +126,2; -148,0 -> +153,8; -230,0 -> +243,6; -288,2 -> +306,4; -410,3 -> +430,3; -435,4 -> +454,0; -449,2 -> +465,9` |
| `Assets/CoreAIBenchmark/Tests/PlayMode/Benchmarks/GameCreationBenchmarkPlayModeTests.cs` | `-594 -> +594; -597,0 -> +598; -598,0 -> +600; -602,0 -> +605; -604,0 -> +608; -605,0 -> +610,2; -612 -> +618; -619,0 -> +626,3; -620,0 -> +630; -622,0 -> +633,31; -656,0 -> +698; -668 -> +710; -673 -> +715,8; -714 -> +763; -752,0 -> +802,192; -928 -> +1169; -1234 -> +1475; -1289,0 -> +1531; -1295 -> +1537; -1331 -> +1573; -1333,2 -> +1575; -1345 -> +1586; -1356 -> +1597; -1362 -> +1603; -1379,0 -> +1621; -1389 -> +1631; -1408,2 -> +1650,2; -1415 -> +1657; -1420 -> +1662; -1423,6 -> +1664,0` |
| `Assets/CoreAIBenchmark/Tests/PlayMode/Benchmarks/GameCreationBenchmarkResume.cs` | `-22 -> +22,2; -78,0 -> +80; -135,0 -> +138,11; -150 -> +163,4; -179 -> +195` |
| `Assets/CoreAIBenchmark/Tests/PlayMode/Benchmarks/ParallelBenchmarkPlayModeTests.cs` | `-37 -> +37,3; -44,0 -> +47; -48,0 -> +52,4; -55,0 -> +63; -61 -> +69; -63,2 -> +71,5; -66,3 -> +77,3; -70 -> +81,18; -72,0 -> +101,6; -74,3 -> +108,4; -78 -> +113,2; -560,0 -> +597,134; -674 -> +844,3; -687,0 -> +860,2; -691,0 -> +866; -692,0 -> +868; -694 -> +870,12; -697,0 -> +885; -698,0 -> +887; -705,0 -> +895; -708,0 -> +899; -709,0 -> +901; -1320,0 -> +1513,53; -1561,0 -> +1807,2; -1574,0 -> +1822; -1587,0 -> +1836` |
| `Assets/CoreAIBenchmark/Tests/PlayMode/Benchmarks/RbxBenchmarkWorld.cs` | `-4,0 -> +5; -46,2 -> +47,7; -181 -> +187; -196,0 -> +203,29` |
| `Assets/CoreAIBenchmark/Tests/PlayMode/SkillSetBenchmarkPlayModeTests.cs` | `-232,0 -> +233,2` |
| `Assets/CoreAiUnity/Tests/EditMode/BenchmarkExtremeTierEditModeTests.cs` | `-402 -> +402,2` |
| `Assets/CoreAiUnity/Tests/EditMode/GameCreationBenchmarkWindowUitk.cs` | `-1414 -> +1414` |
| `Assets/CoreAiUnity/Tests/EditMode/GoalScoreEditModeTests.cs` | `-0,0 -> +1; -1,0 -> +3; -2,0 -> +5; -108,0 -> +112,150` |
| `dev-docs/audits/2026-10-23/11-benchmark.md` | `-0,0 -> +1,188` |
| `dev-docs/fix-wave-2026-10-24/11-benchmark.md` | `-0,0 -> +1,143` |
| `dev-docs/fix-wave-2026-10-24/18-benchmark-final-integration.md` | `-0,0 -> +1,96` |
| `dev-docs/fix-wave-2026-10-24/19-g6-final-integration.md` | `-0,0 -> +1,100` |

### Adjacent read-only source and caller coverage

- Scoring: checkpoint/mandatory classification and failure policy in `GoalScore.cs`; result data in `ScenarioResult.cs`; report eligibility/scenario/tier/pass/speed consumers; `RoleFitness.Evaluate`; comparison raw-row reconstruction; import, identity merge/finalization and saved option matching in `BenchmarkRunStore.cs`. Checked that exclusions affect role speed/dimensions while diagnostic totals remain visible, and that a failed mandatory gate cannot become aggregate PASS.
- Execution: `SessionCapturingLlmClient` turn start, stream evidence/usage collection, sealed state and cut/completed timing; `RunScenario` setup, orchestrator/client wiring, continuation/cancellation/grading/results; `ProviderActivityGapSeconds`; actual G6 timeout resolver/capture reserve and continuation floor; target retry/merge/publication/error collection/watchdog; resume checkpoint attachment/save and metadata restoration.
- Publication/replay: complete changed `WriteArtifacts` body and its single/parallel callers; required-writer fault and republish fixture; optional PNG/vector fallback; observer original-chunk JSON envelope; `RbxBenchmarkWorld.TryEvaluateLua`/`TryReplayLua`; actual replay regression and recapture caller. Historical report files were not modified or regraded.
- Geometry: full changed CFrame/Size projected-extents measurement, all 13 new boundary cases and positive 60-part castle translation. Source changes retain original part identities/counts, sizes/rotations/relative positions and grading assertions. The full unmodified analyzer and bulk geometry corpus were not independently audited.
- Scheduling/UI: full adjacent `ParallelBenchmarkScheduler.cs` coroutine stepper, scheduler and visual-lane implementation; target lane/cancellation/publication callers; the changed UITK speed label. Reviewed new fake-clock/plain NUnit timing test, fake-client Unity coroutine cases, scoped soft-budget phase diagnostics, and live SkillSet test's explicit/category classification. No interactive UI or live provider proof is claimed.
- Documentation: complete changed README/design hunks and all four added historical audit/repair documents. Their prior green counts and source-final statements were read after the independent source pass and were not adopted as current semantic evidence. Their referenced XML/process artifacts were not independently revalidated in this takeover.

### Acceptance self-audit

- The mandatory-gate policy preserves numeric means and requires mandatory gates for aggregate PASS. Custom and interrupted rows are filtered independently of attribution, while diagnostic/token/tool evidence remains visible. Stop bypasses the grader and remains unfinished for resume, including same-tick provider completion.
- Required artifact exceptions now propagate to single-suite failure or parallel problems. Checkpoint state is saved before export and successful publication is marked afterward. Finalized checkpoint identities permit publication-only recovery without provider replay. Optional image failure still embeds the vector fallback. No additional introduced publication defect was established.
- Independent replay chunks retain original text and execution order, including compile failures and runtime-failure mutations. Client-turn aliases/labels describe internal tool time instead of provider-only/decode-only performance. No historical measurement was retimed.
- The provider-evidence fix rejects empty synthetic receipts, separates usage from generation activity and freezes evidence on seal. Its newly cleared activity timestamp nevertheless lacks a turn-local inactivity baseline, producing PR-BENCH-001. The new reset tests check null evidence but do not exercise attribution after a long earlier turn.
- The flaky soft-budget case remains an unreproduced historical timing failure followed by passing diagnostic execution. Its retained logs do not constitute a production scheduler/cancellation fix and provide no root cause for PR-BENCH-001. Documented batch end-of-frame and host/account policy limits are not counted as bugs.

## Unconfirmed hypotheses

- No additional introduced finding is counted. Null/malformed legacy records, mixed legacy timing availability, noncooperative late-provider resource behavior and replay-envelope corruption were not assigned new guarantees beyond their existing contracts. Their unexecuted edge cases remain verification limits, not padded findings.

## Completion accounting

- CHANGED: this report only.
- VERIFY OUTPUT: 24 assigned paths and all 183 hunks reviewed; one P2, zero counted P1/P3 findings. Direct final checks found 24 ledger paths, zero missing/duplicate paths, one finding heading, zero trailing-whitespace lines, zero Cyrillic characters and a final newline. `git diff --check -- <report>` exited 0; direct checks also cover this untracked report. HEAD remains the pinned review head and all 24 primary paths have no worktree difference from it.
- NOT DONE: implementation or regression writing, injected-clock reproduction, builds/tests, Unity EditMode/PlayMode, UI, actual artifact fault/replay/geometry runs, player/IL2CPP/WebGL/browser/device, real-provider quality/latency, load/performance, Git/publication. Root owns all executable acceptance and corrections.
- CONCERNS: per-turn activity reset can misclassify a fresh continuation's model deadline as a long provider stall, excluding/retrying a model measurement.
- QUESTIONS: none currently block read-only progress.
