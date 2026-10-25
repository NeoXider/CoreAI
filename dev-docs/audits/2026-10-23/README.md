# CoreAI framework source audit - 2026-10-23

Baseline: `69d5bcc37502cd4bb464a3bf9ac0c53b1624b804`; local `HEAD`, `origin/main` and live remote `main` were verified against this baseline on 2026-10-24. All seven framework packages are `7.53.0`. The owner's explicit request authorizes retaining these reports.

Finalized: **2026-10-24**. Status: **completed, lead-accepted source-audit deliverable**. The retained set contains **15 Markdown files: 12 module reports (01-11 and 16), coverage 00, independent review 15 and this index**. All source reports are complete and accepted within their declared review limits. No findings were fixed and no new regression tests were implemented.

## Scope and coverage

Scope is the CoreAI framework's seven packages and internal modules only: `Assets/CoreAI`, `Assets/CoreAiUnity`, `Assets/CoreAIMods`, `Assets/CoreAIHub`, `Assets/CoreAIBenchmark`, `Assets/CoreAIMcp` and `Assets/CoreAIMirror`. The official CoreAIBenchmark package and internal benchmark module remain in report 11; core Crafting and mod material shaders remain in report 16. Standalone demos, the example game, general developer tooling and vendor internals are excluded. Their generated reports have been removed.

The [coverage inventory](00-coverage.md) records **1,312 tracked source files: 1,246 C# and 66 other source files** under those seven roots. This measures inventory, not reviewed lines or semantic coverage. Full, targeted and sampled review boundaries remain explicit in each frozen report and in coverage 00. Source acceptance does not establish Unity or built-player acceptance.

## Accepted reports and counts

Counts are raw numbered confirmed module findings before deduplication. P0 is zero. New/known describes tracker provenance, not whether a fix exists; there are no known-issue extensions in this scope. Independent review decisions, hypotheses and the UH-01 host-integration note add no findings.

| ID | Report | Accepted source-review receipt | Finding IDs | P1 / P2 / P3 | New / Known |
| --- | --- | --- | --- | --- | --- |
| 01 | [Core execution](01-core-execution.md) | Complete within declared paths; utility and metrics branches sampled | CE-01 through CE-08 | 0 / 7 / 1 | 8 / 0 |
| 02 | [Memory and tools](02-core-memory-tools.md) | Complete targeted audit; Crafting internals covered by 16 | MEM-001 through MEM-013 | 1 / 12 / 0 | 11 / 2 |
| 03 | [Providers and streaming](03-core-providers-streaming.md) | Complete targeted audit; large-file ranges and fixture limits explicit | LLM-001 through LLM-007 | 2 / 5 / 0 | 7 / 0 |
| 04 | [Unity host and Editor](04-unity-host.md) | Complete within sampled/traced coverage | UH-02 through UH-08; UH-01 is uncounted integration guidance | 1 / 6 / 0 | 7 / 0 |
| 05 | [Mods runtime and sandbox](05-mods-runtime-sandbox.md) | Complete targeted audit; MOD-003 revised to P2 configuration limitation | MOD-001 through MOD-011 | 2 / 9 / 0 | 11 / 0 |
| 06 | [Rbx API](06-mods-rbx-api.md) | Complete targeted audit; shader source supplemented by 16 | RBX-001 through RBX-008 | 2 / 6 / 0 | 8 / 0 |
| 07 | [Luau downlevel](07-mods-luau-downlevel.md) | Five production files and five direct fixtures read fully; callers targeted | LUAU-001 through LUAU-010 | 1 / 8 / 1 | 10 / 0 |
| 08 | [Hub UI](08-hub-ui.md) | Every C# implementation in the four named directories reviewed | HUB-001 through HUB-010 | 0 / 8 / 2 | 9 / 1 |
| 09 | [MCP](09-mcp.md) | All 27 runtime and 18 test/support C# files read | MCP-001 through MCP-004 | 0 / 3 / 1 | 4 / 0 |
| 10 | [Mirror network](10-mirror-network.md) | All six runtime C# files / 4,832 lines read fully; adjacent/test reads bounded | NET-001 | 0 / 1 / 0 | 1 / 0 |
| 11 | [Benchmark](11-benchmark.md) | Complete targeted scoring/resume/evidence audit | BENCH-01 through BENCH-08 | 0 / 8 / 0 | 8 / 0 |
| 16 | [Crafting and material shaders](16-crafting-material-shaders.md) | All nine Crafting C# and 13 shader/HLSL files read; four new P2s | CSM-001 through CSM-004 | 0 / 4 / 0 | 4 / 0 |
| **Raw findings** | **12 module reports** | Before removing aliases | **91** | **9 / 77 / 5** | **88 / 3** |
| **Canonical findings** | **Two aliases removed** | Canonical priorities retained | **89** | **9 / 75 / 5** | **86 / 3** |

[Independent security review 15](15-independent-security-review.md) adjudicates **14 selected claims: 13 ACCEPT, 1 REVISE, 0 REJECT**. It contributes no additional module findings. MOD-003 is the revised P2 configuration/composability limitation: the no-Rbx main chunk has a default guard, while the documented handler cap applies to that chunk only with Rbx. Reports 05 and 15 are final. Report 10's NET-001 is independently accepted; report 16's four new P2 findings are included above. The fog concern remains a hypothesis, not a confirmed finding.

## Duplicate and provenance reconciliation

| Canonical finding | Alias | Counting decision |
| --- | --- | --- |
| 03 / LLM-002 (P1) | 01 / CE-01 (P2) | Same failed-result streaming projection loses executed receipts and enables replay. Keep canonical P1; remove the P2 alias. CE-01's partial-text/usage and retry-consumer details remain in its report. |
| 02 / MEM-011 (P2) | 04 / UH-07 (P2) | Same file-memory history/path-state cache retention. Count once; MEM-011 also includes summary-store path state. |

The only three numbered known-family findings are **MEM-006, MEM-008 and HUB-010**. They remain attached to existing tracker families; no known-issue extension is counted. BENCH-02 and BENCH-05 are new residual findings against completed tracker items. Other known debt mentioned in report prose is outside numbered totals.

Related causes remain distinct when their failing implementations differ: MEM-001 / MEM-005 supplied-context isolation; CE-06 / LLM-004 buffering; LLM-003 / LLM-007 routing; UH-03 / HUB-008 persistence receipts; MOD-002 / HUB-003 failure propagation; and MEM-008 / MOD-008 collisions in different persistence namespaces.

Owner-boundary adjudication on 2026-10-24 removes UH-01 from confirmed defects and retains its source evidence as an uncounted [host-integration/lifecycle note](04-unity-host.md#host-integration-lifecycle-note). The game owns authentication, account switching, identity issuance and session/UI lifetimes, including cancelling old work and resetting every retained role cache or recreating the panel. CoreAI must honor supplied context/storage namespaces and provide generic cancellation/reset/disposal/composition seams. MEM-001 and MEM-005 still violate those supplied-context contracts and remain counted; a generic all-view-state reset helper is advisory.

## Priority fixes for follow-up

These are the **nine canonical P1 findings**. Fixes and production regressions remain follow-up work. Independent review and lead source checks establish their stated source paths, not a live runtime pass.

| Finding | Priority fix and affected composition | Evidence limit |
| --- | --- | --- |
| MOD-001 | Enforce read-only capabilities at shared version-store and overlay mutation boundaries. | Binding-to-store trace and independent review; no VM reproduction. |
| MEM-001 | Capture an immutable actor partition before enqueueing, including an empty scope resolved through a mutable host provider. | Lead source check; production-composition regression missing. |
| LLM-001 | Prevent auth-refresh replay after a non-streaming turn has executed a tool effect. | Server-managed composition traced and independently reviewed; no live backend reproduction. |
| LLM-002 / CE-01 | Preserve executed receipts when converting failed completions to streams so retry/fallback retains replay protection. | Canonical P1 independently accepted; no live replay regression. |
| LUAU-001 | Bound repeat/continue preprocessing expansion before execution budgets apply. | Parser/caller trace and safe arithmetic; no large expansion run. |
| UH-05 | Validate and cap LineRenderer counts before native allocation. | Lead source check; unsafe allocation was not executed. |
| RBX-008 | Bound graph traversal and serialization before the outbound byte cap is reached. | Codec/caller trace and bounded arithmetic; no large-payload/OOM run. |
| MOD-002 | Propagate overlay persistence failure and publish success/deduplication state only after persistence succeeds. | Fault path traced; no production fault-injection regression. |
| RBX-001 | Pass the requested raycast range through the Unity conversion without squaring it. | Conversion trace and arithmetic probe; no real Physics execution. |

## Verification receipts and limits

The following checks were executed on **2026-10-23**; saved receipts were reread on **2026-10-24**. They were not rerun during closure. Existing fixture passes do not implement or prove the new regression scenarios.

| Check | Saved result | What it establishes |
| --- | --- | --- |
| Seven-package version check | `python tools/bump_version.py --check`: **exit 0**, packages and MCP advertise `7.53.0` | Version metadata consistency. |
| Persistence bridge harness | `node Assets/CoreAiUnity/Tests/Node~/persist_fs_jslib_test.js`: **exit 0**, all assertions PASS | Actual bridge source against mocked browser/Emscripten primitives. |
| SSE bridge harness | `node Assets/CoreAiUnity/Tests/Node~/fetch_sse_jslib_test.js`: **exit 0**, ALL PASS | Existing mock fetch/callback harness; no browser heap/backpressure measurement. |
| Portable core suite | [TRX](../../../artifacts/audit-2026-10-23/core/portable-core.trx): **2,605 Passed / 0 Failed / 3 NotExecuted** | 2,608 actual result entries; linked engine-free fixtures only. |
| Portable Lua suite | [TRX](../../../artifacts/audit-2026-10-23/lua/portable-lua.trx): **1,846 Passed / 0 Failed / 38 NotExecuted** | 1,884 result entries; existing CI thresholds pass, portable coverage only. |
| Analyzer source suite | [TRX](../../../artifacts/audit-2026-10-23/analyzer/analyzer.trx): **8 Passed / 0 Failed / 0 NotExecuted** | Existing analyzer source fixtures; shipped-DLL behavior is a separate gate. |
| Positive module opt-in | **Interrupted / incomplete**, process result `-1` | No pass or demonstrated product-check failure. |
| Packaging, optional-symbol and minimum-version consumer imports | **NOT RUN** | Package source checks do not establish consuming-project compatibility. |
| Current Unity dependency import and shader configuration | **NOT RUN** | Committed URP `17.6.0` versus cached URP/Core `17.3.0` has not been import-validated. Shader compilation and fog behavior remain unverified. |
| Unity EditMode/PlayMode, built-player, IL2CPP, WebGL and device checks | **NOT RUN** | No Unity, graphics, browser reload, device or platform acceptance. |
| Real sockets/network processes, input/accessibility, load/fault/performance and live-model/model benchmarks | **NOT RUN** | No networking, interaction, scale, memory, throughput or model-quality acceptance. |

Core totals use actual `UnitTestResult` outcomes, not the inconsistent console/summary skip counters. Lua's 38 NotExecuted entries are 32 unavailable-engine, three live-model, two missing-demo-assembly and one missing-composition-root cases; none is counted as passed.

No tests, builds, Unity/player/WebGL/network/device/performance checks or model benchmarks ran during this documentation closure. Direct closure checks cover the 15-file set, current finding headings, relative links, whitespace, preserved report bytes and the append-only plan. The lead's [final validation receipt](../../../artifacts/audit-2026-10-23/final-validation.json) is regenerated immediately after this documentation freeze; the prior receipt is superseded. The completed deliverable is a source audit with explicit verification limits, and all implementation and runtime gates above remain open.
