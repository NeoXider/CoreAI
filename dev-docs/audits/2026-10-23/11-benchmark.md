# Benchmark execution, scoring, evidence, and resume audit

Audited SHA: `69d5bcc37502cd4bb464a3bf9ac0c53b1624b804` (`HEAD`, `main`, and `origin/main` verified locally; no fetch). Audit date: 2026-10-23. Auditor model: `gpt-6.1-sol`, reasoning effort `max`.

## Status and scope

Completed source audit within the coverage below: eight confirmed P2 findings; no confirmed P0/P1 finding. Findings are source-confirmed, not newly executed C#/Unity regressions. This report is the only owned output. Source files, published artifacts, and the unrelated preexisting ETFX `.meta` file are read-only. The explicit request to retain this report overrides the repository's general no-audit-reports convention. No delegation, git mutation, Unity/MCP operation, build, test runner, model request, or benchmark execution occurred.

The goal is to assess whether benchmark scores and saved evidence faithfully represent the attempted work, including timeout attribution, cancellation, partial resume, exclusions, replay/prefab publication, malformed reports, and configuration. Relevant invariants are isolated attempts, stable run identity, no double counting, exclusion of unmeasured work, bounded execution, and truthful evidence. Charts/tooling and exhaustive frozen-result review are excluded.

The README explicitly separates suite versions, provider throughput from decode speed, and historical runs predating the camera handoff fix. Historical performance statements are not new validation. The review traced each confirmed defect through its caller and checked the relevant existing regression guards. Most likely missed failures were combinations of otherwise-tested states: mandatory gates plus aggregation, custom prompts plus Model deadlines, Stop plus useful work, and storage failure plus suite completion.

## Confirmed actionable findings

### BENCH-01 — P2 — Scenario aggregation discards mandatory-checkpoint verdict gates (new)

Evidence: `Assets/CoreAI/Runtime/Core/Features/Benchmarking/GoalScore.cs:369` checks mandatory failures before allowing PASS; `Assets/CoreAI/Runtime/Core/Features/Benchmarking/BenchmarkReport.cs:126` replaces that verdict with `Classify(meanBase)`, whose PASS branch at line 135 checks only the numeric score. `PassCount`, `PassRate`, group pass counts, and extreme pass counts consume these reconstructed summaries (`BenchmarkReport.cs:214`, `:221`, `:378`, `:179`).

Trigger: a single repetition earns 95/100 but misses a mandatory 5-point checkpoint. Its stored result is PARTIAL, but the suite summary counts it as PASS. Repetition averaging has the same issue. Impact: pass totals and report verdicts can contradict the actual gated result, overstating completion. Confidence: high, confirmed by source call chain; not a runtime execution claim.

Existing guard/test: `Assets/CoreAiUnity/Tests/EditMode/GoalScoreEditModeTests.cs:94` explicitly covers this 95-point mandatory failure for the individual score. The reviewed tier aggregation tests use nonmandatory checkpoints (`BenchmarkExtremeTierEditModeTests.cs:153`) and do not carry this guard through aggregation. No matching TODO entry found.

Fix direction: retain a mandatory-failure gate when forming scenario summaries, with an explicit documented policy across repetitions. Regression: wrap the existing 95-point mandatory-failure result in a report and assert PARTIAL, zero passes, consistent JSON/Markdown/group/extreme pass counts; add mixed repetitions so a high mean cannot erase the gate.

### BENCH-02 — P2 — A custom unscored G6 task enters suite scoring on a model deadline (new regression of completed TODO)

Evidence: `Assets/CoreAIBenchmark/Tests/PlayMode/Benchmarks/GameCastleScenarioG6.cs:167` sets `ExcludeFromScoring` for a full custom prompt. An accepted-response deadline changes attribution to Model at `GameCreationBenchmarkHarness.cs:2633`; line 2692 applies NotGraded only when attribution is None. The resulting Model row is included by `BenchmarkReport.cs:98`. The underlying built-in geometry checkpoints still run (`GameCastleScenarioG6.cs:358`).

Trigger: set `COREAI_BENCHMARK_FREEBUILD_PROMPT` to an arbitrary custom task and reach the scenario or typed total deadline after an accepted, nonstalled response. Impact: arbitrary work is scored against generic build checkpoints and affects suite/dimension/role results despite the documented exclusion. Confidence: high for this branch, source-confirmed; no live request made.

Existing guard/test: clean custom runs become NotGraded and existing tier tests exclude that attribution; neither covers the combination of a custom prompt and Model attribution. `TODO.md:3364` marks full-prompt exclusion complete, so this is a residual defect rather than an unimplemented request. Transport/framework diagnostics should continue to be preserved.

Fix direction: represent scoring eligibility independently from failure attribution, or explicitly exclude custom scenarios for Model outcomes too while retaining their failure evidence. Regression: use a fake accepted stream that reaches a local/typed total deadline under a custom prompt; assert the row remains visible but contributes to no suite, tier, dimension, or role score.

### BENCH-03 — P2 — Stop after executed work finalizes an incomplete parallel attempt as a measurement (new)

Evidence: `Assets/CoreAIBenchmark/Tests/PlayMode/Benchmarks/GameCreationBenchmarkParallelSuite.cs:436` feeds the operator Stop flag into `ShouldCancel`. `GameCreationBenchmarkHarness.cs:2498` cancels the attempt on that flag; `:2544` and `:2587` accept cancellation after any recorded work as a clean stop without requiring the scenario deadline. The deadline-specific correction at `:2604` is false for an early operator stop. `GameCreationBenchmarkParallelSuite.cs:231` drops Stop-aborted attempts only inside the Environment retry branch; a clean result is merged at `:282`. `BenchmarkRunStore.cs:221` treats its None attribution as finalized.

Trigger: stop a parallel target after a tool call but before its goal is complete and before its own timeout. Impact: the truncated world/logic receives a normal score, potentially FAIL, and resume skips that scenario/repetition permanently. A stop without any work follows the tested Environment path and is dropped; that guard does not cover this case. Confidence: high, source-confirmed.

Existing tests: `ParallelBenchmarkPlayModeTests.cs:658` stops an unproductive hanging provider; `:754` stops between completed scenarios. Neither stops an active productive scenario. No matching TODO found; the separate zero-results-green TODO (`TODO.md:3286`) is not this defect.

Fix direction: carry an explicit cancellation reason (operator Stop, suite budget, scenario deadline); keep stop-interrupted attempts as evidence without finalizing or scoring them. Regression: a fake stream executes a real mutation, requests Stop, then awaits cancellation. Assert no graded/finalized result for that repetition, preserved partial evidence, cancellation and cleanup, and exactly one rerun on resume.

### BENCH-04 — P2 — One syntactically invalid failed Lua call invalidates the entire replay (new)

Evidence: `Assets/CoreAIBenchmark/Tests/PlayMode/Benchmarks/GameCreationBenchmarkHarness.cs:111` records both completed and failed calls; `:162` appends every parseable `execute_lua.code`, including failed Lua source. `:194` protects calls with `pcall(body)`, but `:200` inserts their source directly into function literals in one script. `Assets/CoreAIBenchmark/Tests/PlayMode/Benchmarks/GameCreationBenchmarkPlayModeTests.cs:928` evaluates that whole replay as one chunk.

Trigger: an `execute_lua` call contains invalid Lua such as `local =`, followed by a corrected call that builds a valid scene. The original tool compiles calls separately; the replay must parse the invalid function body before any `pcall` can run. Impact: the published replay cannot reproduce even the later successful construction, contrary to its failure-isolation claim. Confidence: high from Lua parse semantics and source; no Lua execution performed.

Existing tests: `Assets/CoreAIBenchmark/Tests/PlayMode/Benchmarks/GameCreationBenchmarkPlayModeTests.cs:695` retains a failed `bad()` chunk and checks replay text/order, but does not execute the replay or include a syntax failure. Line 754 covers hero selection with a placeholder replay. No matching TODO found.

Fix direction: retain original chunks as independently compiled inputs and replay them through the same host executor, or generate a host-supported loader with correctly escaped source and isolated compilation. Do not discard failed chunks that may have partially mutated the world. Regression: syntax-failing chunk, runtime-failing chunk after a mutation, then successful chunk; replay should preserve the same final state and failure ordering as separate original executions.

### BENCH-05 — P2 — G6's mandatory build-volume check ignores part extents and allows negative height (new regression of completed TODO)

Evidence: `Assets/CoreAIBenchmark/Tests/PlayMode/Benchmarks/RbxBenchmarkWorld.cs:46` checks only `inst.Position`; line 47 accepts y down to -8. `Assets/CoreAIBenchmark/Tests/PlayMode/Benchmarks/GameCastleScenarioG6.cs:382` passes mandatory `within_build_volume` when this count is zero. Geometry checks at `Assets/CoreAIBenchmark/Tests/PlayMode/Benchmarks/G6BuildAnalysis.cs:429` assess composition/construction, not containment. The documented volume is x/z -64..64 and y 0..96 (`Assets/CoreAIBenchmark/README.md:46`).

Trigger: a named Castle part centered at x=63 with Size.X=20 extends to x=73 but records no violation; a pivot at y=-1 is also accepted. Rotated large parts have the same gap. Impact: invalid scene geometry earns the mandatory containment checkpoint and its adherence credit. Confidence: high, source-confirmed, with an independent arithmetic boundary probe recorded below.

Existing guard/test: `TODO.md:3356` marks full-extent G6 bounds checking complete for the earlier primitive path. The current Roblox measurement path has no corresponding extent check; the reviewed G6 geometry tests cover parsing/composition but not this mandatory read-back boundary.

Fix direction: compute the oriented part bounds from Size and CFrame, and enforce the published volume (document any intentional ground-slab tolerance). Regression: crossing x/z edges by size or rotation, crossing the y ceiling, negative-height parts, and an exactly-contained part through the real Rbx measurement and scenario grader.

### BENCH-06 — P2 — Resume does not freeze the option that changes deadline scoring eligibility (new)

Evidence: `Assets/CoreAIBenchmark/Tests/PlayMode/Benchmarks/GameCreationBenchmarkHarness.cs:2194` reads `COREAI_BENCHMARK_PROVIDER_STALL_SECONDS` (default 120); line 2622 uses it to turn an accepted-response deadline into Environment, which is excluded and rerun instead of scored as Model. `Assets/CoreAIBenchmark/Tests/PlayMode/Benchmarks/GameCreationBenchmarkResume.cs:17` omits this setting from `DurableOptions`; line 150 saves only that list, and lines 95 and 128 restore/validate only saved options.

Trigger: start with the stall ceiling disabled (`0`), interrupt the run, and resume in a fresh process without that variable. The same accepted-response attempt with a 130-second activity gap changes from a scored Model deadline to an excluded Environment failure. Impact: one run id can mix different scoring/finalization policies without a mismatch error or recorded configuration explaining the difference. Confidence: high from the complete option save/restore and attribution call chains.

Existing tests: `Assets/CoreAiUnity/Tests/EditMode/BenchmarkExtremeTierEditModeTests.cs:63` explicitly demonstrates that the threshold changes attribution; `BenchmarkRunStoreEditModeTests.cs:475` and `:492` cover other compatibility changes. No matching TODO found.

Fix direction: persist and restore/validate the normalized stall threshold alongside the other grading options, and include it in public reproducibility metadata. Regression: save a disabled/nondefault threshold, clear/change the environment, resume, and assert restoration or refusal before any request; retain retry-count flexibility.

### BENCH-07 — P2 — Required report-write failure can still produce a successful benchmark/CLI exit (new)

Evidence: `Assets/CoreAIBenchmark/Tests/PlayMode/Benchmarks/GameCreationBenchmarkPlayModeTests.cs:1424` catches any artifact-write exception, logs a warning, and returns `"(not written)"`. The single-target path only conditionally marks completion (`:1234`) and then asserts result count/framework failures (`:1251`), not publication success. The parallel path has the same conditional at `Assets/CoreAIBenchmark/Tests/PlayMode/Benchmarks/GameCreationBenchmarkParallelSuite.cs:411`; its problem collection at line 470 checks rows/framework failures, not failed report writes. `Assets/CoreAiUnity/Tests/EditMode/GameCreationBenchmarkLauncher.cs:1245` maps only NUnit failure to a nonzero CLI exit.

Trigger: finalized results checkpoint successfully, but a required SVG/Markdown/JSON/INDEX write fails (for example, a permanently locked final report). Impact: the suite can finish green/exit zero while the report is missing, stale, or partly replaced. Retained state allows a later repair, but the caller receives no machine-readable failure indicating that repair is needed. Confidence: high, source-confirmed; no disk-failure injection run.

Existing guard/test: `Assets/CoreAiUnity/Tests/EditMode/BenchmarkRunStoreEditModeTests.cs:221` asserts that the storage primitive throws after exhausted retries and preserves the previous file; the outer writer absorbs that error. Parallel artifact tests assert happy-path files (`Assets/CoreAIBenchmark/Tests/PlayMode/Benchmarks/ParallelBenchmarkPlayModeTests.cs:403`). No matching TODO found.

Fix direction: distinguish optional image failures from required evidence publication; propagate required write failures into the suite result/problems and retain the incomplete checkpoint. Regression: inject a permanent required-artifact commit failure after successful checkpointing; assert nonzero suite/CLI outcome, no completion marker, previous durable data intact, and a later resume publishes without new model calls.

### BENCH-08 — P2 — The reported provider-call generation timer includes tool execution (new; owned here)

Evidence: `Assets/CoreAIBenchmark/Tests/PlayMode/Benchmarks/GameCreationBenchmarkHarness.cs:488` starts the clock before the outer client turn and `:511`/`:529` add its whole elapsed duration without subtracting tool time. Its streaming wrapper enumerates `_inner.CompleteStreamingAsync` at line 379. In the real adapter, `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/MeaiLlmClient.cs:500` delegates to its internal multi-roundtrip loop, which awaits tool completion at `:1327` and `:1479`. Thus tool latency lies inside the measured span. `Assets/CoreAI/Runtime/Core/Features/Benchmarking/BenchmarkReport.cs:246` uses this duration for GenerationTokensPerSecond; `Assets/CoreAI/Runtime/Core/Features/Benchmarking/RoleFitness.cs:124` also consumes it. The README's speed table and `BenchmarkReport.cs:238` claim it excludes tool execution.

Trigger: identical provider output accompanied by a slow Lua/camera/other tool. Impact: the reported provider-call speed falls with tool latency and can change speed-weighted role ratings even though provider throughput is unchanged. Base correctness scoring does not use this speed. Confidence: high from the full capture-to-adapter call chain; no performance measurement made.

Existing tests: `Assets/CoreAIBenchmark/Tests/PlayMode/Benchmarks/GameCreationBenchmarkPlayModeTests.cs:535` checks completed/cut outer-turn time using a streaming fake, without separating provider and tool intervals. No matching open TODO found. This report owns the benchmark instrumentation/label defect; the LLM adapter executing tools internally is expected behavior, not a second LLM finding.

Fix direction: instrument actual provider-request intervals, accounting explicitly for provider/tool overlap, or rename the metric to describe client-turn throughput and correct the exclusion claims. Regression: use a deterministic clock and independently controlled provider/tool spans; delaying only the tool must leave a genuinely provider-only metric unchanged while increasing effective session duration.

## Exact coverage and exclusions

Coverage is focused source inspection, not a claim to have reviewed every line of each large file.

| Reviewed file | Coverage |
| --- | --- |
| `AGENTS.md`; `Docs/ARCHITECTURE_RULES.md` | Repository/runtime/composition/verification rules. |
| `Assets/CoreAIBenchmark/README.md` | Scenario/scoring/metrics/configuration/artifact sections and resume/publication contracts, especially lines 60-217 and 478-551. |
| `Assets/CoreAIBenchmark/Tests/PlayMode/Benchmarks/BENCHMARK_DESIGN.md` | Scoring/report design, lines 1-95. |
| `Assets/CoreAiUnity/Tests/README.md`; `CONTRIBUTING.md` | Test-honesty rules A1-A8; documented portable verification commands. |
| `Assets/CoreAI/Runtime/Core/Features/Benchmarking/GoalScore.cs` | Attribution policy, checkpoint contracts, compute/mandatory verdict path, restore and nonfinite handling. |
| `Assets/CoreAI/Runtime/Core/Features/Benchmarking/ScenarioResult.cs` | Complete result/evidence data contract. |
| `Assets/CoreAI/Runtime/Core/Features/Benchmarking/BenchmarkReport.cs` | Import-summary preservation, exclusions, scenario/tier/pass aggregates, timings, dimensions, groups (lines 1-412). |
| `Assets/CoreAI/Runtime/Core/Features/Benchmarking/BenchmarkRunStore.cs` | Secret-source persistence, manifest compatibility, result merge/identity/finalization, atomic checkpoint writes, completion discovery, backup and import. |
| `Assets/CoreAI/Runtime/Core/Features/Benchmarking/BenchmarkJson.cs`; `Assets/CoreAI/Runtime/Core/Features/Benchmarking/BenchmarkArtifactRecovery.cs` | Complete raw-row/date-preserving JSON reader and hero/artifact recovery. |
| `Assets/CoreAI/Runtime/Core/Features/Benchmarking/BenchmarkLegacyReportEvidence.cs` | Trace ownership, recovered counters/bonuses, required evidence, repetition/section matching. |
| `Assets/CoreAI/Runtime/Core/Features/Benchmarking/BenchmarkReportFormatter.cs` | Pass/speed consumers, imported-row preservation, JSON summary/result fields, numeric/string serialization; display geometry not exhaustively reviewed. |
| `Assets/CoreAI/Runtime/Core/Features/Benchmarking/ModelComparison.cs` | ParseSummaryJson, raw-result reconstruction, finite-number helpers, JSON end-of-input/depth guards. Charts excluded. |
| `Assets/CoreAI/Runtime/Core/Features/Benchmarking/RoleFitness.cs` | Dimension/speed consumption, missing-dimension and role gates, overall blend. |
| `Assets/CoreAI/Runtime/Core/Features/Benchmarking/BenchmarkTargets.cs` | Target identity/key/temperature contract and parser/normalization/serialization inventory; not a full parser audit. |
| `Assets/CoreAI/Runtime/Core/Features/Benchmarking/CoreAI.Benchmarking.asmdef`; `Assets/CoreAIBenchmark/Tests/PlayMode/CoreAI.Benchmark.Tests.asmdef` | Engine-free scoring assembly and test/package/platform boundaries. |
| `Assets/CoreAIBenchmark/Tests/PlayMode/Benchmarks/GameCreationBenchmarkHarness.cs` | Observer/replay; session capture/sealing; Lua and recording executors; complete RunScenario cancellation/attribution/grading/result/cleanup path; prefab save; timeout/error/token helpers. Screenshot/model-card rendering internals excluded. |
| `Assets/CoreAIBenchmark/Tests/PlayMode/Benchmarks/GameCreationBenchmarkResume.cs` | Complete resume resolution, frozen inputs, checkpoint attachment/recovery/save path. |
| `Assets/CoreAIBenchmark/Tests/PlayMode/Benchmarks/GameCreationBenchmarkParallelSuite.cs` | Settings/metadata; complete target iteration/retry/merge/finalization; cooperative cancellation; multi-target setup/teardown and resume application. |
| `Assets/CoreAIBenchmark/Tests/PlayMode/Benchmarks/ParallelBenchmarkScheduler.cs` | CoroutineStepper wait/disposal/fault isolation (lines 1-160); scheduler/visual-lane API inventory, not a complete fairness proof. |
| `Assets/CoreAIBenchmark/Tests/PlayMode/Benchmarks/GameCreationBenchmarkPlayModeTests.cs` | Timeout/budget resolver and tests, token/capture tests, observer/replay tests, hero selection, explicit recapture, Stop tests, single-suite execution and complete WriteArtifacts/WriteSceneScreenshots path. |
| `Assets/CoreAIBenchmark/Tests/PlayMode/Benchmarks/GameCastleScenarioG6.cs` | Build contract, configurable prompts, exclusion, complete Grade/ReadGeometry path. |
| `Assets/CoreAIBenchmark/Tests/PlayMode/Benchmarks/RbxBenchmarkWorld.cs` | Measurement/query/synchronous-completion behavior, real runtime stack setup, disposal, timed tool wrapper. |
| `Assets/CoreAIBenchmark/Tests/PlayMode/Benchmarks/G6BuildAnalysis.cs` | Read-back format/parser/pagination/cap; complete checkpoint list; sampled enclosure/tower/keep analysis, not a complete geometry-algorithm proof. |
| `Assets/CoreAIBenchmark/Tests/PlayMode/Benchmarks/BenchmarkCastleMaterialAssets.cs` | Bake/persist-texture/override/dedup/artifact-scope code, lines 1-234; embedded tests sampled. |
| `Assets/CoreAIBenchmark/Tests/PlayMode/Benchmarks/GameBuildScenariosG1.cs` | Scenario inventory; RecordedScene final-state reconstruction (lines 978-1148). |
| `Assets/CoreAIBenchmark/Tests/PlayMode/Benchmarks/GameMechanicScenariosG2.cs` | Baseline prompt/hygiene/hidden-sample grading, lines 30-218. |
| `Assets/CoreAIBenchmark/Tests/PlayMode/Benchmarks/BenchmarkSuiteCatalog.cs` | Complete registry/order. |
| `Assets/CoreAIBenchmark/Tests/PlayMode/SkillSetBenchmarkPlayModeTests.cs` | Dummy-tool/skill setup, captured metric semantics, both execution/cancellation/result paths and structural comparison. |
| `Assets/CoreAiUnity/Tests/EditMode/GameCreationBenchmarkPrefabPublisher.cs` | Publication planning/journal/move recovery, GUID preservation, JSON path-span patching and dependency discovery; folder/log helpers not exhaustively reviewed. |
| `Assets/CoreAiUnity/Tests/EditMode/GameCreationBenchmarkLauncher.cs` | Resume/secret reconstruction and option setup; summary parser/late report selection; final NUnit-to-exit-code callback. |
| `Assets/CoreAiUnity/Tests/EditMode/GameCreationBenchmarkWindowUitk.cs` | Entry/field inventory only; UI implementation excluded. |
| `Assets/CoreAiUnity/Tests/PlayMode/LlmInfra/PlayModeProductionLikeLlmTestSupport.cs` | Handle ownership/temperature, memory-store rebuild contract, owned HTTP/behavior settings and 180-second response-wait defaults. |
| `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/MeaiLlmClient.cs` | Only boundary confirmation for BENCH-08: streaming wrapper, provider enumerator, awaited internal native/text tool batches. |
| `Assets/CoreAiUnity/Runtime/Source/Features/World/Infrastructure/CoreAiWorldCommandExecutor.cs` | Only production transform-parity comparison, lines 605-680. No separate world-executor finding. |

Existing test source reviewed (not executed):

- `Assets/CoreAiUnity/Tests/EditMode/GoalScoreEditModeTests.cs`: scoring/mandatory/finite-input cases and complete mandatory-failure regression.
- `Assets/CoreAiUnity/Tests/EditMode/BenchmarkExtremeTierEditModeTests.cs`: typed timeout/response/stall/retry policy; aggregation/exclusions; old/malformed parser cases; serialization and hostile-name SVG tests.
- `Assets/CoreAiUnity/Tests/EditMode/BenchmarkRunStoreEditModeTests.cs`: identity/merge/import/recovery; incomplete-run markers; atomic transient/permanent failure; secret redaction; frozen target inputs; interrupted/resumed result equivalence.
- `Assets/CoreAiUnity/Tests/EditMode/BenchmarkLegacyReportEvidenceEditModeTests.cs`: kept-evidence repair regression, lines 1-82; remaining test-name/contract inventory. Referenced fixture files were identified but not independently fully reviewed.
- `Assets/CoreAiUnity/Tests/EditMode/GameCreationBenchmarkPrefabPublisherEditModeTests.cs`: byte-preserving path changes, interrupted GUID/material/texture moves, repeated publication and CLI/collision guards; detailed recovery cases at lines 66-181.
- `Assets/CoreAIBenchmark/Tests/PlayMode/Benchmarks/ParallelBenchmarkPlayModeTests.cs`: scripted independent targets/artifact checks, checkpoint resume, typed deadlines after useful work, uncooperative cancellation/Stop/budget tests and fake clients; remaining fixture inventoried.
- `Assets/CoreAIBenchmark/Tests/PlayMode/Benchmarks/G6BuildAnalysisPlayModeTests.cs`: paging/error guards and recorded-world/geometry test inventory. Large frozen geometry arrays excluded.
- `Assets/CoreAIBenchmark/Tests/PlayMode/Benchmarks/GameObserveActScenariosG8PlayModeTests.cs`: seeded observation, scale pollution and per-occurrence mutation cases, lines 1-140.
- `Assets/CoreAIBenchmark/Tests/PlayMode/Benchmarks/ExtremeLogicScenariosPlayModeTests.cs`: independent golden damage solution and representative wrong-rule mutations, lines 1-190.
- `Assets/CoreAIBenchmark/Tests/PlayMode/Benchmarks/ExtremeWorldScenariosPlayModeTests.cs`: test inventory and sampled hardcoded/live-probe/restoration cases around lines 303-379.
- `Assets/CoreAIBenchmark/Tests/PlayMode/Benchmarks/ExtremeInstructionScenariosPlayModeTests.cs`: prompt-injection reachability, golden trace and near misses, lines 38-106.

Explicit exclusions: charts and `tools/benchmark_chart.py` (auditor 14); exhaustive G3/G4/G5/G7/G8 scenario/reference algorithms and adversarial input-space proof; camera/rasterization internals; complete target/UI parser audits; all other frozen-result bulk, actual PNG/prefab fidelity, and provider/model implementation audits. G5/G7/G8 scenario files were searched for grader/fixture call sites only; that inventory is not substantive algorithm coverage. No inference that excluded code is correct.

## Observed guards, historical evidence, and remaining uncertainty

These are source observations, not pass claims:

- Typed total/scenario deadlines, response-wait/transport failures, provider activity and retry eligibility have distinct policy paths and focused fake-client tests. First-provider-response evidence is reset per turn, and capture sealing freezes late-session statistics. The combination gaps are BENCH-02/03/06.
- Checkpoints use per-target files with flush-before-replace and bounded retry; result identity and duplicate/group/tier validation happen before resume. No simultaneous cross-process writers are supported by the README. BENCH-07 concerns the outer evidence writer's treatment of failure.
- Publication journals moves before execution, checks asset GUIDs, repairs JSON/Markdown/state links, and has recovery/idempotency test source. This audit did not move an asset or validate an actual published prefab in Unity.
- The G2 baseline intentionally gives exact Lua solutions (`GameMechanicScenariosG2.cs:102`, `:141`, `:184`); those cases measure tool execution/transcription. Their inspected checkpoints do not assign Reasoning credit. Golden/reference grader fixtures are separate from the live scenario goal. No confirmed answer leak into hidden tests was established; the exhaustive leakage review remains outside sampled coverage.
- Existing TODO items relevant to trust remain separate: partial/comparable-ranking policy (`TODO.md:66`, `:71`, chart ownership auditor 14), tasks outliving cancellation grace (`TODO.md:3243`), zero-result Stop appearing green (`TODO.md:3286`), and remaining live-scope migration (`TODO.md:183`). BENCH-01 through BENCH-08 are owned here; BENCH-02/05 identify residual defects against completed TODO entries rather than duplicate open work.
- The real harness is guarded by `COREAI_LUA && COREAI_LLM && !UNITY_WEBGL`. The portable scoring assembly has no engine references; prefab export is optional Editor code. No built-player/WebGL support is inferred from that assembly layout. Browser durability would require an integration calling `CoreAiWebGlPersistence.Sync()` and surfacing false; the standalone engine-free store does not itself establish that contract. WebGL browser persistence is missing validation here, not a proven live benchmark failure on a path compiled out.
- No stress/fault proof was executed for long unbounded histories, many targets, delayed orphan tool work, power loss, native VM disposal, screenshot readback, or the first-2000-part geometry sampling limit. These remain validation gaps, not additional confirmed defects.

Representative historical sample: `Docs/BenchmarkRuns/2026-10-17/BENCHMARK_20261017_122332_opencode-beam-free__Beam-Free.json` and its `.md` companion (summary/selected metadata/rows; Markdown header and tables through line 92). The artifact records suite 2.0, a resumed run with two segments, and 35 scenario rows. A read-only Python calculation found 35 unique graded scenarios, no duplicate scenario/repetition, finite base values in 0..100, and row verdicts 21 Pass / 0 Partial / 14 Fail. The independently recomputed mean was `62.179637457`, matching the saved `62.179637` within 1e-6; Markdown reports the same counts. No high-base mandatory-failure row occurred in this sample. This verifies selected archived-file consistency only; it does not validate model quality, elapsed time, API execution, trace completeness, or reproducibility on the audited SHA.

## Checks actually run

- `git status --short`: unrelated untracked `Assets/Epic Toon FX/Upgrade/ETFX 2019.4.24f1 URP Upgrade.unitypackage.meta` and the preexisting `dev-docs/audits/` directory observed. No source/git mutation performed.
- `git rev-parse HEAD`: `69d5bcc37502cd4bb464a3bf9ac0c53b1624b804`.
- `git rev-parse main origin/main`: both refs returned that same SHA. Final read-only `git diff --name-only` showed shared changes to `PLAN.md` and `dev-docs/README.md`; neither was edited by this auditor. Scoped status still shows the preexisting untracked ETFX `.meta` file and this untracked report.
- Read-only `rg --files`, targeted `rg -n`/`rg -l`, and numbered Python/PowerShell source reads identified the paths above. An initial Windows `rg` call with literal wildcard path arguments returned OS error 123; subsequent searches used `-g`/resolved paths. A guessed factory path returned OS error 2 and was resolved to `LlmInfra/PlayModeProductionLikeLlmTestSupport.cs`. Neither was a test or validation failure.
- PowerShell here-string piped to `python -`, using only `json`, `math`, `pathlib`, and `collections`: the archived JSON check above exited 0 and printed `rows=35 graded_scenarios=35 recomputed_suite=62.179637457 published_suite=62.179637 within_rounding=True`; `duplicate_id_rep=False finite_base_0_100=True row_verdict_counts={'Pass': 21, 'Partial': 0, 'Fail': 14}`.
- The same in-memory/source-inventory probe printed `pivot_x=63 size_x=20 extent_max_x=73 pivot_test_accepts=True extent_test_accepts=False` and `RESUME OPTION INVENTORY: stall_option_saved=False`. These are arithmetic/source probes, not execution of C# or Lua.
- `git diff --check -- dev-docs/audits/2026-10-23/11-benchmark.md`: exit 0, no output. The report is untracked, so an additional direct `python -` full-text check was used: no trailing-whitespace lines, no Cyrillic characters, final newline present, eight unique BENCH finding headings. The report was reread, including failure-path and scope caveats; this is a document check, not a source test.

## Validation NOT RUN; focused checks for root

No builds or automated suites run. Root owns shared build/test output and should execute requested checks serially. Unity EditMode/PlayMode, built player, WebGL/browser durability, device, actual prefab/replay integration, live providers, model quality and performance checks are NOT RUN. No fixes or regression tests were authored under the read-only source constraint.

Suggested existing checks (not executed; expected result is successful compilation and all selected tests passing, with a nonzero discovered-test count):

```powershell
dotnet build CoreAI.Benchmarking.csproj --no-restore
dotnet build CoreAI.Benchmark.Tests.csproj --no-restore
dotnet test tools/portable/Tests/CoreAI.Portable.Tests.csproj --no-restore --filter "FullyQualifiedName~GoalScoreEditModeTests|FullyQualifiedName~BenchmarkExtremeTierEditModeTests|FullyQualifiedName~BenchmarkRunStoreEditModeTests|FullyQualifiedName~BenchmarkLegacyReportEvidenceEditModeTests|FullyQualifiedName~GameCreationBenchmarkPrefabPublisherEditModeTests"
```

For root's later Editor verification, the fake-client `CoreAI.Tests.PlayMode.Benchmarks.ParallelBenchmarkPlayModeTests` fixture and the geometry/recording fixtures can exercise cancellation/resume/grading without a real model. The explicit `GameCreationBenchmark_Suite`, SkillSet benchmark, and recapture/publication operations are outside these proposed checks. Each finding names the additional meaningful test it needs.
