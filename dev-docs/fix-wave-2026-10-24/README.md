# CoreAI 7.54.0 acceptance evidence

Root accepted all twelve module source joins and the available measured gates on **2026-10-25**. Publication uses the Git tag and [GitHub release](https://github.com/NeoXider/CoreAI/releases/tag/v7.54.0). Final source and receipts bind to that commit/tag; these notes do not claim unrun platforms or model quality.

Intake remains **91 raw / 89 canonical** (9 P1 / 75 P2 / 5 P3), aliases CE-01 -> LLM-002 and UH-07 -> MEM-011, uncounted host note UH-01, and MOD-003 as a finite-default configuration limitation. These are intake findings, not 89 runtime-confirmed fixes.

## Accepted measured gates

| Gate | Actual outcome | Limit |
| --- | --- | --- |
| Portable Core final5 | 2,850 Passed / 0 Failed / 3 NotExecuted; 2,853 rows | Actual TRX result nodes; skips unexecuted. |
| Portable Lua source-final | 1,932 Passed / 0 Failed / 41 NotExecuted; 1,973 rows | Actual TRX result nodes; skips unexecuted. |
| Analyzer / Node / metadata | Analyzer8 Passed; persistence9 PASS checks; SSE ALL PASS; version/positive opt-in PASS | Mock/metadata gates, not browser or live-quality proof. |
| Windows EditMode final5 | 7,917 Passed / 0 Failed / 13 Skipped; 7,930 cases; exit0 | Exact two EditMode exceptions below. |
| Final hermetic Windows PlayMode | 386 Passed / 0 Failed / 2 Skipped; 388 cases; exit0 | Benchmark247 Passed/1 batch EOF skip; MCP12 Passed; Mods24 Passed; FastNoLlm103 Passed/1 batch EOF skip. |
| Included subsets | GPU10/10; Mirror229/229 with NET00117/17; G634/34 | Included totals, not additive. GPU pair visually inspected by root; Mirror transport is offline. |

Raw artifact paths and exact phase/class accounting are in [MD12](12-release-docs.md), [PLAN](PLAN.md) and the [matrix](module-map.json). All C# is frozen. [File leases](FILE_LEASES.md) record released scopes and root-owned follow-ups. Original source owners used `gpt-6.1-sol` with reasoning effort `max`, recorded separately.

## Reports and follow-ups

| Module | Source report |
| --- | --- |
| 01 execution | [01](01-execution.md) |
| 02 memory/tools/skills | [02](02-memory-skills.md) |
| 03 providers | [03](03-providers.md) |
| 04 host / audit writer | [host](workdoc04-unity-host.md), [audit](workdoc04-audit-log.md) |
| 05 Mods / 06 Rbx | [05](workdoc05-mods.md), [06](workdoc06-rbx.md) |
| 07 Luau / 08 Hub | [07](07-luau.md), [08](08-hub.md) |
| 09 MCP / 10 Mirror / 11 benchmark | [09](09-mcp.md), [10](10-mirror.md), [11](11-benchmark.md) |
| 16 crafting/materials | [16](16-crafting-materials.md) |

Integration receipts: [adversarial13](13-adversarial-review.md), [Mods14](14-unity-mods-integration.md), [host/providers/memory15](15-unity-host-integration.md), [Mirror16](16-unity-mirror-integration.md), [Hub17](17-hub-final-integration.md), [benchmark18](18-benchmark-final-integration.md), [G6 control19](19-g6-final-integration.md). The [provider intake](03-provider-intake.md) is historical/superseded and was moved byte-identically out of the audit folder; the frozen audit has15 Markdown files and12 unchanged module hashes.

## Exact environmental exceptions

EditMode:

1. `CoreAI.Tests.EditMode.BenchmarkLegacyReportEvidenceEditModeTests.RealReports_TemporaryFakeRepairPreservesEveryKeptMarkdownSectionAndCounter`: six missing `TestResults/CoreAI/Benchmarks/BENCHMARK_20261015_224850_*.json` historical inputs; no fabricated dataset.
2. `CoreAI.Tests.EditMode.CoreAiLuaWorldModuleEditModeTests.MigratedDemo_OwnsLuaConfigurationInChildModule("Assets/CoreAI.Demos/FullAccess/FullAccessDemo.unity",True)`: vendor TMP reflection constructor at `Assets/Plugins/IngameDebugConsole/Scripts/CommandInputField.cs:60`, outside framework scope.

PlayMode, same vendor cause:

1. `CoreAI.Tests.PlayMode.CoreAiDemoScenesSmokePlayModeTests.AllPublishedDemoScenes_LoadWithScopeCameraAndSupportedShaders` (the aggregate demo smoke case).
2. `CoreAI.Tests.PlayMode.FullAccessDemoScenePlayModeTests.FullAccessDemo_LoadsWithFullLuaAndAnEmptyWorld`.

Their raw failing receipts remain; no exception suppression or framework assertion removal was used. Intentionally live benchmark/recapture cases are outside the hermetic selection. The formerly unmarked SkillSet live test is now Category Benchmark and Explicit/manual-only.

## Historical evidence and limits

Raw Unity final3 is7,873 Passed/28 Failed/10 Skipped; final4 is7,896 Passed/2 Failed/13 Skipped. Controlled pre-ADV runtime is30 Passed/19 Failed (5 progress failures,13 registry failures and1 Hub diagnostic), with exact runtime-byte restoration; matching current-source progress9/9 and registry13/13 passed. Earlier compile-only red attempts remain invalid runtime evidence.

Raw Windows PlayMode final1 is382 Passed/5 Failed/2 Skipped; final2 is386 Passed/1 Failed/2 Skipped. Both were **mixed**, because an unmarked local MiniCPM skill/direct cost test actually contacted port1234. Observed last-call timings2,115/7,002 ms are structural single-run measurements, not model-quality ranking or broad live-provider acceptance. Final hermetic XML contains no such live case.

The earlier16.295306-second cancellation watchdog timeout was not reproduced. A focused1.065373-second pass and final full-suite0.214230-second pass do not establish a root cause or product fix; phase diagnostics remain and the watchdog/assertions/budget were not weakened. The five earlier benchmark failures passed after two harness defects and invalid replay/G6 test setups were corrected, with thresholds, chunk evidence and negative controls preserved.

No built-player, IL2CPP, browser durability/reload, device, real-socket/room/load, performance or live-model-quality acceptance is established. Optional configurations and clean consumer UPM require separate receipts. Automatic persistence arming is not a browser transaction acknowledgement. Hosts own authentication/admission/view/connection lifetimes and independent-world storage; use [supplied multiplayer sessions](../../Docs/CoreAI/MULTIPLAYER_SESSIONS.md).
