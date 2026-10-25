# CoreAI framework audit coverage - 2026-10-23

Baseline: `69d5bcc37502cd4bb464a3bf9ac0c53b1624b804`; all seven framework packages are `7.53.0`. Local `HEAD`, `origin/main` and live remote `main` were verified against this baseline on 2026-10-24. Finalized **2026-10-24**: the source-audit deliverable is complete and lead-accepted within the review limits below. The owner's explicit request authorizes retaining the audit reports.

## Scope and inventory method

The scope is the seven CoreAI framework packages and their internal modules. Official CoreAIBenchmark and internal benchmarking, core Crafting, and mod material shaders remain included. Standalone demos, the example game, general developer tooling and vendor internals are excluded. No excluded findings or source counts contribute to the current inventory or totals.

The closure inventory uses only tracked paths returned by:

```powershell
git -c core.quotepath=false ls-files -- Assets/CoreAI Assets/CoreAiUnity Assets/CoreAIMods Assets/CoreAIHub Assets/CoreAIBenchmark Assets/CoreAIMcp Assets/CoreAIMirror
```

Counted source suffixes are `.cs`, `.lua`, `.shader`, `.hlsl`, `.js`, `.jslib`, `.uxml`, `.uss`, `.html` and `.css`. Tests and package-owned browser/template source are included. Assembly definitions, manifests and other configuration are outside this source count. No filesystem traversal of caches, Library or private configuration is involved.

| Framework package root | Source files | C# | Other source | Module reports |
| --- | ---: | ---: | ---: | --- |
| `Assets/CoreAI/` | 294 | 294 | 0 | 01, 02, 03, 08, 11, 16 |
| `Assets/CoreAiUnity/` | 464 | 452 | 12 | 04; provider seams in 03 |
| `Assets/CoreAIMods/` | 448 | 396 | 52 | 05, 06, 07, 08, 11, 16 |
| `Assets/CoreAIHub/` | 15 | 13 | 2 | 08 |
| `Assets/CoreAIBenchmark/` | 26 | 26 | 0 | 11 |
| `Assets/CoreAIMcp/` | 45 | 45 | 0 | 09 |
| `Assets/CoreAIMirror/` | 20 | 20 | 0 | 10 |
| **Total** | **1,312** | **1,246** | **66** | **12 module reports** |

The 66 non-C# files are 39 Lua, 13 shader/HLSL, eight UXML/USS, two JavaScript, two JSLib, one HTML and one CSS file. Counts describe inventory, not reviewed lines, semantic coverage, active preprocessor configurations or test coverage. They supersede the earlier broader-scope inventory; historical PLAN checkpoints remain unchanged.

## Accepted source-review boundaries

The existing report bodies are frozen except for the owner's 2026-10-24 boundary adjudication of UH-01 into uncounted host-integration guidance. This closure reconciles headings and scope metadata; it does not repeat the source audit. Each report retains its own full/targeted/sampled limits and attribution for source traces, arithmetic probes or isolated mocks. Acceptance does not promote a sampled branch to a full read.

| Report | Accepted review depth | Retained limits |
| --- | --- | --- |
| [01 - Core execution](01-core-execution.md) | Execution, authority, messaging, settings and queue contracts traced within declared paths. | Metric collector internals and unlisted utility branches sampled; memory/provider collaborators cross-reference 02/03. |
| [02 - Memory and tools](02-core-memory-tools.md) | Exact methods/files distinguish full, targeted and search-only review of memory, prompts, skills and tools. | Crafting checker/schema internals excluded from 02 are covered by 16; unlisted branches remain outside full review. |
| [03 - Providers and streaming](03-core-providers-streaming.md) | Provider/HTTP/SSE/routing traces include named large-file ranges and fixture samples. | Not every dialect serializer, routing sink or native dependency implementation is reviewed. |
| [04 - Unity host and Editor](04-unity-host.md) | Host lifetimes, stores, world commands, browser bridges and UI paths traced. | The large chat panel and cosmetic/UI branches are sampled. Template hash equality proves copy consistency only; no browser or Unity execution. |
| [05 - Mods runtime and sandbox](05-mods-runtime-sandbox.md) | Targeted runtime, execution, guards, version stores and host-store review. | Guard/library internals, world semantic projection and Editor conveniences partly sampled. MOD-003 is a P2 configuration limitation. |
| [06 - Rbx API](06-mods-rbx-api.md) | Datatypes, instances, bindings, material integration and caller seams reviewed within named ranges. | Scheduler, replication and large binding files sampled; Humanoid/Players state machines and much of the corpus excluded from deep review. Shader source is covered by 16. |
| [07 - Luau downlevel](07-mods-luau-downlevel.md) | All five production files and five direct fixtures read fully; callers targeted. | Upstream/VM execution and protected-metatable compatibility unverified. |
| [08 - Hub UI](08-hub-ui.md) | Every C# implementation in the four named core/package/integration directories reviewed. | Asset evidence is structural text; runtime scheduling, input and accessibility unrun. Optional Hub presence/absence remain separate configurations. |
| [09 - MCP](09-mcp.md) | All 27 runtime and 18 test/support C# files read. | Socket, graphics, client-cancel protocol and device behavior require separate execution. |
| [10 - Mirror network](10-mirror-network.md) | All six runtime C# files / 4,832 lines read fully; final receipt accepted. | Representative tests and adjacent seams remain range-bounded. One new P2, NET-001; no real network acceptance. A no-Mirror build does not exercise the adapter. |
| [11 - Benchmark](11-benchmark.md) | Official CoreAIBenchmark package and internal benchmarking scoring/resume/publication seams reviewed. | Exhaustive G3/G4/G5/G7/G8 grader/reference algorithms and frozen-result bulk excluded; no new model benchmark run. |
| [16 - Crafting and material shaders](16-crafting-material-shaders.md) | All nine Crafting C# and all 13 material shader/HLSL files read; final receipt accepted, four new P2s. | Source acceptance does not establish shader compilation, rendering or device behavior. Fog remains a hypothesis. |
| [15 - Independent security review](15-independent-security-review.md) | Final ledger: 14 selected claims, 13 ACCEPT, 1 REVISE, 0 REJECT. | No extra finding count or claim of reviewing every module branch. MOD-003 revised to P2; its default guard exists. |

Report numbers and descriptive finding IDs remain stable. The retained set is **15 Markdown files**: reports 01-11 and 16, coverage 00, independent review 15 and the [index](README.md).

## Reconciled findings

The 12 module-report confirmed-finding headings contain **91 raw findings: nine P1, 77 P2 and five P3**. Removing **CE-01 -> LLM-002 (canonical P1)** and **UH-07 -> MEM-011 (P2)** gives **89 canonical findings: nine P1, 75 P2 and five P3**. P0 is zero. Provenance is **88 new / three known** raw and **86 new / three known** canonical, with no extension. The only numbered known families are MEM-006, MEM-008 and HUB-010. Independent adjudications, hypotheses and UH-01's host-integration/lifecycle note are excluded from these totals.

Report 04 now counts seven new defects (one P1 / six P2), UH-02 through UH-08. UH-01 retains its evidence as integration guidance: the game owns authentication, account switching, identity issuance and session/UI lifetimes, while CoreAI must honor supplied context/storage namespaces and expose generic cancellation/reset/disposal/composition seams. MEM-001 and MEM-005 remain counted supplied-context defects. This clarification supersedes the previous 90-canonical/10-P1 totals.

The index contains the accepted module counts, alias decisions, nine canonical P1 follow-ups and central verification receipts. No findings were fixed and no new regression tests were implemented. The source audit's runtime-first, replaceable-block, capability-isolation, cancellation/lifecycle, ordering, replay, resource-bound and failure-observability requirements remain applicable to follow-up fixes.

## Verification boundaries

Existing checks were executed on **2026-10-23** and saved receipts reread on **2026-10-24**: portable core **2,605 Passed / 0 Failed / 3 NotExecuted**, portable Lua **1,846 Passed / 0 Failed / 38 NotExecuted**, analyzer **8 Passed / 0 Failed**, both Node bridge harnesses passed, and the seven-package version check passed. [The index](README.md) links the TRXs and states each receipt's limits. Portable core uses actual result entries because summary/console skip counters disagree. Lua skips remain unexecuted despite passing the existing CI threshold.

Positive module opt-in remains **interrupted/incomplete**, not passed. Optional-symbol, minimum-version and clean consumer imports are **NOT RUN**. Committed URP `17.6.0` versus cached URP/Core `17.3.0` has not been import-validated; portable passes do not establish shader compilation or the declared Unity dependency graph.

Unity EditMode/PlayMode, built-player/IL2CPP/WebGL, browser persistence/reload, devices, real network processes, gameplay/input/accessibility, load/fault/performance and live-model/model benchmarks are **NOT RUN** for acceptance. No tests, builds or runtime gates were run during documentation closure. Inventory checks, heading reconciliation, relative-link/whitespace validation and a scoped diff check do not close those gates. The [final validation receipt](../../../artifacts/audit-2026-10-23/final-validation.json) is regenerated by the lead after the documentation freeze; the prior receipt is superseded.
