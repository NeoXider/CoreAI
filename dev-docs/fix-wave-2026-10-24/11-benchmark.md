# Benchmark repair work log

Baseline: `69d5bcc37502cd4bb464a3bf9ac0c53b1624b804`. Target: `7.54.0`.
Scope: the assigned benchmark package, engine-free benchmark contracts, benchmark EditMode tests, and this file. Root owns Git, versioning, aggregate documents, builds and test execution. No provider requests or benchmark model runs.

## Initial finding review

All initial dispositions are `hypothesis` pending source revalidation; the audit alone is not execution evidence. These are framework measurement/storage contracts, not host authentication or game-session policy.

| ID | Reproduce and existing guard review | Regression acceptance | Proposed files |
| --- | --- | --- | --- |
| BENCH01 | Aggregate a 95-point result with a failed mandatory checkpoint; individual GoalScore guard exists, aggregate gate guard missing. | Mean stays numeric; every scored repetition must satisfy mandatory gates for PASS; group/extreme/JSON/Markdown totals agree. | BenchmarkReport.cs; GoalScoreEditModeTests.cs |
| BENCH02 | Custom G6 prompt plus accepted-response deadline becomes Model-attributed; existing NotGraded guard handles only clean completion. | Preserve attribution/evidence while excluding custom work from all score aggregates, including deadline outcomes. | ScenarioResult.cs; BenchmarkReport.cs; BenchmarkReportFormatter.cs; ModelComparison.cs; GameCreationBenchmarkHarness.cs; benchmark tests |
| BENCH03 | Operator Stop after useful tool work follows clean completion; existing tests cover idle Stop and between-scenario Stop. | Preserve partial evidence without grading/finalizing; cancellation cleans up and resume reruns exactly once. | ScenarioResult.cs; BenchmarkRunStore.cs; GameCreationBenchmarkHarness.cs; GameCreationBenchmarkParallelSuite.cs; ParallelBenchmarkPlayModeTests.cs |
| BENCH04 | Syntax-invalid Lua chunk followed by valid construction poisons concatenated replay; existing guard checks text/runtime failure only. | Compile original chunks separately; syntax failure isolates later chunks; preserve runtime-failure partial effects and order. | GameCreationBenchmarkHarness.cs; GameCreationBenchmarkPlayModeTests.cs |
| BENCH05 | Part center inside volume but extents/rotation outside, or negative height; geometry guards omit actual containment. | Real Rbx readback and grader reject outside extents and accept exact boundaries with documented tolerance. | RbxBenchmarkWorld.cs; G6BuildAnalysisPlayModeTests.cs; README.md |
| BENCH06 | Resume after changed/cleared stall ceiling changes scoring eligibility; option compatibility tests omit stall threshold. | Persist normalized threshold; restore or reject before request; retry policy remains adjustable. | GameCreationBenchmarkResume.cs; GameCreationBenchmarkPlayModeTests.cs; README.md |
| BENCH07 | Required report commit throws after checkpoint; writer catches it and suite succeeds; storage primitive already tests durable previous data. | Suite fails, checkpoint stays incomplete; resume republishes without model rerun; optional image failures stay optional. | GameCreationBenchmarkPlayModeTests.cs; GameCreationBenchmarkParallelSuite.cs; ParallelBenchmarkPlayModeTests.cs |
| BENCH08 | Outer client-turn timer includes adapter-internal tools; tests exercise only turn timing. | Use truthful client-turn labels/aliases unless provider interval contract exists; deterministic delayed-tool test proves actual metric semantics. | BenchmarkReport.cs; BenchmarkReportFormatter.cs; ScenarioResult.cs; RoleFitness.cs; GameCreationBenchmarkHarness.cs; GameCreationBenchmarkPlayModeTests.cs; README.md |

## Decisions and invariants

- Keep public APIs and serialized legacy fields readable; add explicit metadata only where needed.
- No new suite version or historical evidence rewrites without root decision.
- Reuse existing benchmark seams; these are corrections to existing systems, not new gameplay machinery.
- Most likely omissions: aggregation after import, Stop races after useful work, publication failure after a fully checkpointed run, and syntax errors before any Lua pcall.
- Verification status: source review in progress; builds, tests, Unity import, Play Mode and device checks NOT RUN by this worker. Root runs commands serially.

## Progress

- Initial audit and architecture rules read; preexisting root/ETFX changes recorded and left untouched.

## Native recovery checkpoint (2026-10-24)

The two prior CLI attempts settled with transport errors. This worker owns the existing partial changes exclusively and will not restart them. Model: `gpt-6.1-sol`; reasoning effort: `max`. Repository verified at `D:/Git/CoreAI`. Root's release/version changes remain outside this lease.

Real goal: preserve truthful score eligibility, attempt completion, replay state, bounds, resume policy and publication outcomes. Correctness, ordering, cancellation, failure isolation, idempotency and evidence retention apply; live performance/model quality and host-game policy are outside this repair. Existing benchmark contracts, fake clients/worlds and temporary checkpoint stores are reused. Unity Game Agent guidance is applied as an existing-system correction; no new subsystem or dependency is planned.

| ID | Revalidation and current source state | Disposition and remaining acceptance |
| --- | --- | --- |
| BENCH01 | Confirmed framework defect. Partial source retains mandatory gates in report and raw-row comparison aggregation; GoalScore tests cover means/import, but are unexecuted. | `confirmedfix`; inspect import paths and finish any missed fallback gates. |
| BENCH02 | Confirmed framework defect. `ExcludeFromScoring` now survives independently of attribution and filters report/role scoring. Current new tests construct rows directly. | `confirmedfix`; add fake accepted-provider custom-deadline coverage through the harness. |
| BENCH03 | Confirmed framework defect. `Interrupted` rows are excluded and not finalized; partial harness samples Stop once before cancellation. | `confirmedfix`; audit completion/Stop races and add productive Stop, cleanup and resume coverage. |
| BENCH04 | Confirmed framework defect. Captured chunks are serialized independently and replayed through the original executor; a syntax/partial-mutation regression was added but unexecuted. | `confirmedfix`; inspect malformed input/failure evidence and preserve old replay compatibility. |
| BENCH05 | Confirmed framework defect. Partial measurement computes CFrame-projected extents with finite bounds and a small tolerance. | `confirmedfix`; add real Rbx measurement plus scenario-grader tests for rotation, extents, ground and boundaries. |
| BENCH06 | Confirmed framework defect. Normalized stall option is persisted in manifest and public metadata; only helper-level new tests exist. | `confirmedfix`; add save/restore/mismatch regression before provider execution and check legacy import behavior. |
| BENCH07 | Confirmed framework defect. Required writer errors now propagate; parallel fault path collects publication problems. | `confirmedfix`; add temporary-store publication faults, incomplete-state preservation and republish without provider calls; retain optional image handling. |
| BENCH08 | Confirmed metric-label defect. Existing provider seam cannot isolate provider intervals. Partial source exposes client-turn aliases and corrects some labels; injected clock exists. | `confirmedfix`; add deterministic provider/tool-span coverage and complete exposed documentation/labels without changing historical values. |

Exact additional write plan: `Assets/CoreAIBenchmark/Tests/PlayMode/Benchmarks/GameCreationBenchmarkHarness.cs`, `GameCreationBenchmarkParallelSuite.cs`, `GameCreationBenchmarkPlayModeTests.cs`, `GameCreationBenchmarkResume.cs`, `RbxBenchmarkWorld.cs`, `ParallelBenchmarkPlayModeTests.cs`, `G6BuildAnalysisPlayModeTests.cs`, and new `BenchmarkFailureContractPlayModeTests.cs` if needed to keep the regressions focused; `Assets/CoreAIBenchmark/README.md`; `Assets/CoreAI/Runtime/Core/Features/Benchmarking/{BenchmarkReport.cs,BenchmarkReportFormatter.cs,BenchmarkRunStore.cs,GoalScore.cs,ModelComparison.cs,RoleFitness.cs,ScenarioResult.cs}` only for identified remaining gaps; benchmark EditMode fixtures only for portable contract coverage; this work log. Change nothing else, including asmdefs, metadata, project links, archived artifacts and aggregate documents.

Before accepting: read each actual diff, check assertion/API compatibility and error/cancellation branches, request root's portable link additions, and report exact commands. Worker builds/tests/Unity/provider requests remain NOT RUN. Root owns execution and independent acceptance.

## Source handoff closure (2026-10-25)

Current owner: native delegated closure worker. The earlier benchmark writers have settled; this is an incremental review of their actual changes. Root owns the final portable and Unity gates, Git, release metadata and aggregate documents.

Real goal: close BENCH01-BENCH08 with truthful score/pass gates, retryable interruption and publication state, faithful replay, measured containment, stable resume identity and honest timing labels. The invariants are correctness, cancellation and ordering, failure isolation, idempotent resume/publication, evidence retention and backward compatibility. Provider quality/performance claims and historical artifact rewrites are outside scope.

Current status: source/diff review and exact fixture inventory in progress. The runtime and deterministic regression changes already exist. The package README and final inventory were not completed by the previous owner. No build, test, Unity, live-provider or Git mutation is authorized for this worker.

Root reported an actual full Core diagnostic of `2833 Passed / 3 Failed / 3 NotExecuted`. Two benchmark fixture corrections are present but still unverified: the normalized stall mismatch now chooses a distinct normalized value (`0` or `1`), and the extreme group Markdown golden now includes `70 (0/1 pass)` plus an explicit G1/extreme PARTIAL-row assertion. These are root-owned fixture corrections to preserve during review; this worker does not claim their tests passed.

Before acceptance: read all scoped diffs, map each finding to its behavior and exact fixture signature, finish only confirmed gaps, record SHA-256 inventory and exact root test filters, then release this write lease. No new portable project links are expected because the new regressions are in existing fixtures.

### Final source review and disposition

All 16 preexisting C# diffs were read, followed by the closure edits below. Each BENCH finding remains a confirmed framework fix, with source acceptance complete and execution acceptance pending root gates. Host-game policy was not added. Existing scoring, fake-client, world-query and checkpoint seams were reused.

| ID | Reviewed behavior and final source disposition | Exact regression methods |
| --- | --- | --- |
| BENCH01 | `confirmedfix`: numeric means are retained; PASS also requires mandatory gates in every scored repetition. Group/extreme counts and raw-row comparison fallback retain the gates. | `GoalScoreEditModeTests.BenchmarkMandatoryGates_SurviveMeanAggregationAndReportImport(bool, int)`; `BenchmarkRepetitionMean_DoesNotRequireEveryIndividualNumericScoreToPass()`; existing `BenchmarkExtremeTierEditModeTests.Markdown_ShowsTierScores_GroupTable_AndExtremeSection()` golden updated by root. |
| BENCH02 | `confirmedfix`: custom eligibility is a separate field from Model/Environment/Framework attribution. Aggregates and role speed omit those rows; diagnostic/tool/token evidence remains. | `GoalScoreEditModeTests.BenchmarkExcludedDeadline_PreservesAttributionWithoutAffectingScoresOrRoleSpeed(FailureAttribution)`; `ParallelBenchmarkPlayModeTests.CustomPrompt_DeadlineKeepsAttributionAndEvidenceWithoutAnyScore()`. |
| BENCH03 | `confirmedfix`: Stop is sampled even when the provider completes on that tick; the grader is skipped, evidence is checkpointed, cancellation/lane cleanup runs, and resume replaces the unfinished identity once. | `GoalScoreEditModeTests.BenchmarkInterruptedAttempt_IsEvidenceWithoutFinalizationAndIsReplacedOnce()`; `ParallelBenchmarkPlayModeTests.ProductiveStop_CancelsAndRetainsUnscoredAttemptThenResumeRetriesOnce()`; `ProductiveStop_WhenProviderCompletesOnTheSameTickStillSkipsGrading()`; existing idle-provider Stop regression updated to retain an unscored row. |
| BENCH04 | `confirmedfix`: independent original chunks are stored in a JSON/Lua-comment envelope and replayed through the original executor. Syntax failures do not poison later compilation; runtime failures retain their mutations/order. Legacy executable replay remains supported. | `GameCreationBenchmarkPlayModeTests.LuaReplay_CompilesEachCallAndRetainsRuntimeFailureEffectsInOrder()`; observer text/order regression retained; recapture uses `TryReplayLua`. |
| BENCH05 | `confirmedfix`: real Rbx read-back uses rotated `Size`/`CFrame` extents and 0.001-stud tolerance across x/z -64..64 and y 0..96. The positive castle fixture no longer includes an intentionally below-ground slab. | `G6BuildAnalysisPlayModeTests.LiveWorld_BuildVolumeUsesRotatedFullExtentsAndPublishedGround(string, string, bool)` (13 cases covering x/z edges, height, ground, rotation and tolerance); existing `LiveWorld_GradePassesTheCoherentCastleAndFailsTheHeap()`. |
| BENCH06 | `confirmedfix`: one finite normalization policy feeds execution, manifest options, resume matching and public metadata. Current overrides must normalize to the saved value; retry count remains adjustable. Legacy metadata is restored when present and cannot invent an absent historical setting. | `GoalScoreEditModeTests.BenchmarkStallThreshold_UsesFiniteNormalizedResumeIdentity(string, double)`; `GameCreationBenchmarkPlayModeTests.ResumeManifest_RestoresNormalizedStallPolicyAndRejectsChangedOverride(string, double)`. |
| BENCH07 | `confirmedfix`: required publication errors propagate in the single suite and become parallel suite problems. Completion is not marked; finalized checkpoints can republish with zero provider calls. Optional model-card PNG failure uses a vector fallback. | `ParallelBenchmarkPlayModeTests.RequiredPublicationFailure_FailsSuiteAndRepublishesCheckpointWithoutProviderRerun()`; `GameCreationBenchmarkPlayModeTests.OptionalModelCardWriteFailure_StillPublishesRequiredReportsWithVectorFallback()`. |
| BENCH08 | `confirmedfix`: the existing client seam cannot isolate provider spans. Exposed Markdown, card, hero and UITK labels now describe client-turn time including internal tools; old metric fields remain equal-value aliases. | `GameCreationBenchmarkPlayModeTests.ClientTurnTiming_IncludesInternalToolsAndKeepsLegacyAliases(double)` (0/2500-ms tool intervals; checks timer, aliases/import, Markdown and both SVGs). |

Closure edits were restricted to the package README/design, client-turn card/window labels, the existing timing fixture assertions and the PlayMode stall mismatch fixture. The PlayMode fixture also used `expected + 1` at the 3600 clamp; its mismatch now chooses a distinct normalized value (`0` or `1`), matching the root's portable fixture correction. No new fixture, assembly/project link, dependency or suite version was added.

### API and evidence contract changes

- Additive result fields: `ScenarioResult.ExcludeFromScoring`, `Interrupted`, and `ClientTurnMs`. `GenerationMs` remains a read/write alias with the same backing value.
- Additive report members: `TotalClientTurnMs`, `ClientTurnTokensPerSecond`; `TotalGenerationMs` and `GenerationTokensPerSecond` retain their values and signatures. Role fitness uses only eligible rows for its speed input.
- Additive reproducibility metadata: nullable `BenchmarkRunMetadata.ProviderStallSeconds`, exposed as `metadata.providerStallSeconds`; null means older unrecorded evidence. `BenchmarkProviderFailurePolicy.NormalizeStallSeconds(string)` centralizes the normalized resume identity.
- JSON retains legacy generation fields and adds explicit client-turn aliases, `excludeFromScoring` and `interrupted`. Published historical artifacts were not edited.
- `RbxBenchmarkWorld.TryReplayLua(string, out string)` accepts the new versioned independent-chunk envelope and older executable Lua. Direct execution of the new comment envelope is insufficient; the package docs name the loader/recapture path.
- Internal deterministic seams are an injected millisecond clock, an optional required-artifact writer and a separate operator Stop predicate. No other owner's public interface was edited.

### Static verification performed by closure worker

- Scoped `git diff --check`: exit `0`, no output, after all closure source/docs edits.
- Added C# line scan for implicit `var` declarations and Cyrillic: `0` matches. This is a scoped text check, not the English-only test or a compiler.
- Existing `tools/portable/Tests/CoreAI.Portable.Tests.csproj` links `GoalScoreEditModeTests`, `BenchmarkExtremeTierEditModeTests`, `BenchmarkRunStoreEditModeTests` and `BenchmarkLegacyReportEvidenceEditModeTests`; no link changes needed. `CoreAI.Benchmark.Tests.asmdef` already includes all changed PlayMode fixture files by folder ownership.
- Worker build/test/Unity/provider executions: **NOT RUN**. No source-level assertion is reported as an executed regression. Root's prior `2833 Passed / 3 Failed / 3 NotExecuted` diagnostic predates the final fixture/label changes and is not a green receipt for this inventory.

### Root execution handoff

PowerShell portable contract gate from `D:/Git/CoreAI`:

```powershell
dotnet test tools/portable/Tests/CoreAI.Portable.Tests.csproj -c Release --filter "FullyQualifiedName~CoreAI.Tests.EditMode.GoalScoreEditModeTests|FullyQualifiedName~CoreAI.Tests.EditMode.BenchmarkExtremeTierEditModeTests|FullyQualifiedName~CoreAI.Tests.EditMode.BenchmarkRunStoreEditModeTests|FullyQualifiedName~CoreAI.Tests.EditMode.BenchmarkLegacyReportEvidenceEditModeTests" --results-directory artifacts/fix-wave-20261025/benchmark-core --logger "trx;LogFileName=benchmark-core.trx"
```

Expected: zero failed `UnitTestResult` outcomes in the TRX; retain and explain any `NotExecuted` outcome. This gate does not execute the real Rbx/Unity fixtures or UITK source.

Root's existing Unity 6000.6.0f1 operator should run actual PlayMode assembly `CoreAI.Benchmark.Tests`, preserving the default exclusion of Explicit live benchmarks. For a focused regression run, the exact CLI selection arguments are:

```text
-testPlatform PlayMode -assemblyNames CoreAI.Benchmark.Tests -testFilter "CoreAI.Tests.PlayMode.Benchmarks.GameCreationBenchmarkPlayModeTests.ClientTurnTiming_IncludesInternalToolsAndKeepsLegacyAliases;CoreAI.Tests.PlayMode.Benchmarks.GameCreationBenchmarkPlayModeTests.ResumeManifest_RestoresNormalizedStallPolicyAndRejectsChangedOverride;CoreAI.Tests.PlayMode.Benchmarks.GameCreationBenchmarkPlayModeTests.OptionalModelCardWriteFailure_StillPublishesRequiredReportsWithVectorFallback;CoreAI.Tests.PlayMode.Benchmarks.GameCreationBenchmarkPlayModeTests.LuaReplay_CompilesEachCallAndRetainsRuntimeFailureEffectsInOrder;CoreAI.Tests.PlayMode.Benchmarks.ParallelBenchmarkPlayModeTests.CustomPrompt_DeadlineKeepsAttributionAndEvidenceWithoutAnyScore;CoreAI.Tests.PlayMode.Benchmarks.ParallelBenchmarkPlayModeTests.ProductiveStop_CancelsAndRetainsUnscoredAttemptThenResumeRetriesOnce;CoreAI.Tests.PlayMode.Benchmarks.ParallelBenchmarkPlayModeTests.ProductiveStop_WhenProviderCompletesOnTheSameTickStillSkipsGrading;CoreAI.Tests.PlayMode.Benchmarks.ParallelBenchmarkPlayModeTests.RequiredPublicationFailure_FailsSuiteAndRepublishesCheckpointWithoutProviderRerun;CoreAI.Tests.PlayMode.Benchmarks.G6BuildAnalysisPlayModeTests.LiveWorld_BuildVolumeUsesRotatedFullExtentsAndPublishedGround"
```

Expected: every selected parameterized regression passes using fake clients and temporary stores; no live provider requests. The complete non-Explicit assembly also protects earlier cancellation, replay, G6 geometry and parallel scheduling cases. The UITK one-line label additionally requires the normal Unity compilation gate. Root must record the actual command/operator and receipt; this handoff is not execution proof.

### Exact source/test/document inventory at handoff

SHA-256 is over the current working-tree bytes after closure. All listed diffs were read. The work log is excluded from its own hash table to avoid a self-reference; package versions, metadata, project files and historical artifacts are outside this inventory and were not changed by this worker.

| File | SHA-256 |
| --- | --- |
| `Assets/CoreAI/Runtime/Core/Features/Benchmarking/BenchmarkReport.cs` | `b6717afff3d0915c94575213694926bec6876396b6b9c4713089af6bdb55c26c` |
| `Assets/CoreAI/Runtime/Core/Features/Benchmarking/BenchmarkReportFormatter.cs` | `d30e3c5c66ba6c54b628057687e969cae0fbad6d40d693cd9ab55ef57721988b` |
| `Assets/CoreAI/Runtime/Core/Features/Benchmarking/BenchmarkRunStore.cs` | `1d319fc2da056cb71885bc248767f9be39184e5e997124a1382cf34e4e7f7c7d` |
| `Assets/CoreAI/Runtime/Core/Features/Benchmarking/GoalScore.cs` | `76039f2b90ab391147c0112b7abbca7bf033925d5fb98b853f6e92b6e6250a02` |
| `Assets/CoreAI/Runtime/Core/Features/Benchmarking/ModelComparison.cs` | `b2e8b985536fead9038cb2e87c3cb184db9be3ba59ffaac26ff7afeeb7ab4596` |
| `Assets/CoreAI/Runtime/Core/Features/Benchmarking/RoleFitness.cs` | `8f18600664ed8a8cb6959c25ccfa8eb980a76411014d31f5efbbe1aee4b32756` |
| `Assets/CoreAI/Runtime/Core/Features/Benchmarking/ScenarioResult.cs` | `839d1599994872c7d6cb448efb8cea4ac3f09e5f9d8c0787c7fddd109b662ceb` |
| `Assets/CoreAIBenchmark/README.md` | `58fbaca78e3e567abecff42763eacdde387146d79a522ec973592640fa5f0828` |
| `Assets/CoreAIBenchmark/Tests/PlayMode/Benchmarks/BENCHMARK_DESIGN.md` | `7f1c12b1d84e3a27d0c9d4d4a99dd4db8b701c95e6f3a166785b1c2072b0f11a` |
| `Assets/CoreAIBenchmark/Tests/PlayMode/Benchmarks/G6BuildAnalysisPlayModeTests.cs` | `6c449afbd82271a59c9eacda2e71f9fbd47c5afec105551498b4d6a4462b5dc7` |
| `Assets/CoreAIBenchmark/Tests/PlayMode/Benchmarks/GameCreationBenchmarkHarness.cs` | `8369125ade759ff925497c73737ee82f471139bc370a3e4f094390bc33ec1110` |
| `Assets/CoreAIBenchmark/Tests/PlayMode/Benchmarks/GameCreationBenchmarkParallelSuite.cs` | `b4dc94a7c903e403b74fb5e82752545bbd60d71028f97524266c4c0b22a113e6` |
| `Assets/CoreAIBenchmark/Tests/PlayMode/Benchmarks/GameCreationBenchmarkPlayModeTests.cs` | `c25f8945fedaa5700d3b459dce7a917200783863eca61fcdb37e0b294be28616` |
| `Assets/CoreAIBenchmark/Tests/PlayMode/Benchmarks/GameCreationBenchmarkResume.cs` | `11f7822f1cdbf8b88c5b6f096dc7aa814c83014b52fa5ef91cf8b7326ff21d6a` |
| `Assets/CoreAIBenchmark/Tests/PlayMode/Benchmarks/ParallelBenchmarkPlayModeTests.cs` | `d8cda9a345c501c98ff427b0de067c333ac80961475269bd50854023cd5baba9` |
| `Assets/CoreAIBenchmark/Tests/PlayMode/Benchmarks/RbxBenchmarkWorld.cs` | `23483150285b05b571e34cbd6d83366d1ba40cb64af033908871752ece838e73` |
| `Assets/CoreAiUnity/Tests/EditMode/BenchmarkExtremeTierEditModeTests.cs` | `ffd07a24613dd955b7cbfd1bf70296bc80a9c6b406ef2aa8cca9e4de8d0e4f1a` |
| `Assets/CoreAiUnity/Tests/EditMode/GameCreationBenchmarkWindowUitk.cs` | `55e19da175231bcce25a1eaaf9d736378c8a615f59b39ffded355f1713e27665` |
| `Assets/CoreAiUnity/Tests/EditMode/GoalScoreEditModeTests.cs` | `9bdd21ed9158fd829cd5defb55caebb399ddfbd6107df7d516d0fefdaf61bd1c` |

Source closure is complete and the write lease is released for root verification. Remaining acceptance belongs to root's actual portable/Unity tests, compilation and independent acceptance; no live-model quality, visual rendering, device, performance, build or publication claim is made here.
