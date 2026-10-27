# Group 14: Documentation, packages and test wiring

Status: complete within the source-review limits below. One actionable documentation finding; no builds, tests, runtime, Unity, MCP, model, network calls, or delegation performed.

- Baseline: `69d5bcc37502cd4bb464a3bf9ac0c53b1624b804`.
- Candidate: `bc3fd74f0a889bfe1b1e7babc6bc02ed48a7c05e` (branch `demo`).
- Primary scope: exactly the 29 group-14 paths supplied by the requester and recorded in `coverage.json`.
- Changes are restricted to this report. Existing working-tree changes and other review reports are excluded from candidate source.
- Instructions read: root `AGENTS.md` and `Docs/ARCHITECTURE_RULES.md`.

Review questions: Are release versions, internal dependencies and release references coherent? Are optional modules independently opt-in? Do portable project links preserve meaningful tests? Do documentation claims accurately distinguish repairs, retained audit evidence and unrun verification gates?

Acceptance requires reading every assigned diff hunk, checking concrete contract context where necessary, and recording introduced defects only with a valid trigger and candidate line. Host-owned account, view and matchmaking policy is not presumed to be a framework defect. Hypotheses and coverage gaps remain uncounted.

## Findings

### PR-14-001 [P2] Distinguish the factory's legacy chat interface from the actor-aware chat service

- Pinned location: `bc3fd74f0a889bfe1b1e7babc6bc02ed48a7c05e:Assets/CoreAiUnity/README.md:14` (new paragraph).
- Trigger: a host follows the recommended supplied-player integration, resolves `IInGameLlmChatService service = factory.Resolve(admittedActor)`, and follows the paragraph's instruction to supply `ActorContext` to subsequent requests/history/clear/stop operations, or configure the service's identity-provider overloads.
- Expected: the package entry point names operations/configuration that the factory's public return type supports, and distinguishes the two chat APIs.
- Actual: `IInGameLlmChatService` exposes `SendPlayerMessageAsync(string, CancellationToken)`, `ClearHistory()`, history count and rate metrics. It has no request/role/actor overload, persisted-history restore, stop operation, or identity-provider configuration. `ActorKeyedInGameLlmChatServiceFactory.Resolve` returns that interface. Its stock `InGameLlmChatService` implementation calls `ILlmClient` directly and accepts no identity provider. The described actor-aware operations/provider belong to the separate `CoreAI.Chat.CoreAiChatService`, which does not implement that interface. Consequently the first integration route described here cannot be expressed through the resolved service; a replacement factory alone does not change its public return contract.
- Root cause: the new paragraph combines the actor-keyed legacy factory's lifetime contract with the orchestration chat service's per-operation identity contract. The linked session guide repeats the combination in its factory section.
- Source evidence: `Assets/CoreAI/Runtime/Core/Features/Orchestration/IInGameLlmChatService.cs:9`, `InGameLlmChatServiceFactory.cs:49`, `InGameLlmChatService.cs:68` and `:171`; `Assets/CoreAiUnity/Runtime/Source/Composition/CorePortableInstaller.cs:143`; `Assets/CoreAiUnity/Runtime/Source/Features/Chat/CoreAiChatService.cs:22`, `:36`, `:619`, `:631`, `:655`; contextual `Docs/CoreAI/MULTIPLAYER_SESSIONS.md:91-98`.
- Minimal regression proof plan: prepare two compilable host examples against the existing public contracts: factory-resolved legacy chat using its supported methods/caller token, and orchestration chat using `CoreAiChatService` with explicit admitted context for request/history/clear/stop. Check the entry-point paragraph against those examples and explicitly document any host-written adapter required between them. No public API expansion is necessary to repair the guidance. Root owns compilation/execution; none was performed here.
- Uncertainty: high confidence in the source/API mismatch; the finding concerns integration guidance. No runtime isolation failure is inferred. A host can implement a custom adapter, but the paragraph currently omits that distinct contract and presents the operations as belonging to the factory-selected service.

## Coverage ledger

The supplied list exactly matches coverage group `14`: **29 paths, 38 zero-context diff hunks, 9,933 added lines, 22 deleted lines**. All assigned changed content was reviewed. Ranges below are candidate lines, inclusive; multiple ranges identify distinct hunks.

| Path | Changed lines / review coverage | Assessment |
| --- | --- | --- |
| `Assets/CoreAI/CHANGELOG.md` | 9-30; complete new release block and surrounding headings | Release claims/limits checked; version and sampled bounds agree with source. |
| `Assets/CoreAI/README.md` | 10-13; both added paragraphs | Actor/scope and release guidance read; linked guide sampled as context. |
| `Assets/CoreAI/package.json` | 3; full manifest | Version 7.54.0; no added dependency. |
| `Assets/CoreAIBenchmark/package.json` | 3, 14-16; full manifest | Version and all three internal pins agree. |
| `Assets/CoreAIHub/package.json` | 3, 14-15; full manifest | Version and both internal pins agree. |
| `Assets/CoreAIMcp/package.json` | 3, 14-15; full manifest | Version/pins agree with advertised server constant. |
| `Assets/CoreAIMirror/package.json` | 3, 14-15; full manifest | Version/pins agree; contextual runtime/test asmdefs retain MIRROR constraints. |
| `Assets/CoreAIMods/package.json` | 3, 14-15; full manifest | Version/pins agree; no newly introduced vendor dependency. |
| `Assets/CoreAiUnity/CHANGELOG.md` | 16-39; complete new release block | Counts, exclusions, mixed-run classification and unrun limits read. |
| `Assets/CoreAiUnity/README.md` | 14-17; both added paragraphs | PR-14-001 on the new API guidance; release paragraph read. |
| `Assets/CoreAiUnity/package.json` | 3, 14; full manifest | Version/internal pin agree. |
| `Docs/README.md` | 13-14, 19 | Acceptance link and session-guide index row read. |
| `Docs/ROADMAP.md` | 80 | Seven-package current release agrees with manifests. |
| `PLAN.md` | 90-228; entire appended history | Intake, audit closure, boundary adjudication, repair, negative runs and final acceptance kept as dated phases. |
| `ProjectSettings/ProjectSettings.asset` | 851; full changed define value and neighboring targets | Removes Mirror/version/Edgegap symbols while retaining DOTWEEN, COREAI_HAS_HUB, COREAI_LLM and COREAI_LUA. |
| `README.md` | 477, 501-502 | Session index and release acceptance paragraph read. |
| `TODO.md` | 3428-3442; entire new wave section | Intake counts and gate boundaries reconciled; previous prefix preserved. |
| `dev-docs/README.md` | 3, 58-61 | Scoped retention exception and separate audit/repair entry points read. |
| `dev-docs/audits/2026-10-23/00-coverage.md` | 1-66; whole new file | Inventory, scope, sampled limits, counts and audit-era unrun gates checked. |
| `dev-docs/audits/2026-10-23/15-independent-security-review.md` | 1-100; whole new file | All selected adjudications, MOD-003 qualification, ownership and evidence limits read as historical audit evidence. |
| `dev-docs/audits/2026-10-23/README.md` | 1-85; whole new file | All module counts, aliases, provenance, P1 follow-ups and historical receipts read. |
| `dev-docs/fix-wave-2026-10-24/12-release-docs.md` | 1-147; whole new file | Final acceptance, superseded checkpoints, exact exclusions, fixture correction claims and preservation statements read. |
| `dev-docs/fix-wave-2026-10-24/13-adversarial-review.md` | 1-147; whole new file | Compatibility changes, three additional findings, implementation ownership, hash checkpoint and later root acceptance read. |
| `dev-docs/fix-wave-2026-10-24/FILE_LEASES.md` | 1-69; whole new file | Historical scopes, exclusions, cross-owner seams, benchmark extensions and final frozen state read. |
| `dev-docs/fix-wave-2026-10-24/PLAN.md` | 1-132; whole new file | Planned commands distinguished from receipts; host policy, source gates and final accepted limits read. |
| `dev-docs/fix-wave-2026-10-24/README.md` | 1-57; whole new file | All gates/subsets, reports, exact exceptions, negative/mixed history and platform limits read. |
| `dev-docs/fix-wave-2026-10-24/module-map.json` | 1-8884; whole new JSON object | All 31 top-level fields and all records read in structured views; repeated paths/boilerplate factored rather than repeatedly dumped. Covers 12 modules, 89 claims, two aliases, one host note, seven carry-ins, 56 unscored candidates, 53 overlaps, 14 historical leases and every verification/checkpoint/follow-up field. |
| `tools/portable/LuaTests/CoreAI.Portable.LuaTests.csproj` | 64; complete added link plus project/property/import context | Resolves once to Mods fixture; matching Lua/LLM symbols and C#9; no added test slicing. |
| `tools/portable/Tests/CoreAI.Portable.Tests.csproj` | 34-38; all five added links plus project/property/reference context | Each path resolves once; default compile discovery remains disabled; Unity-only provider cases retain their guard. |

## Read-only verification output

- Pinned candidate equals checkout HEAD at intake. Existing QualitySettings/ETFX metadata and other review reports were excluded from source review and left untouched.
- Parsed all seven candidate manifests: one `7.54.0` version, twelve matching internal pins. Read `McpServerInfo.Version` in `Assets/CoreAIMcp/Runtime/Protocol/McpMethods.cs:35` and Developer Guide footer at line 897: both `7.54.0`. No release utility executed.
- Recounted baseline Git-tree suffix inventory: Core 294, Unity 464, Mods 448, Hub 15, Benchmark 26, MCP 45, Mirror 20 = 1,312 files; 1,246 C# and 66 other source files. This checks inventory, not semantic coverage.
- Matrix has 89 unique canonical IDs, exact module membership, 9 P1 / 75 P2 / 5 P3, 86 new / three known, two aliases and one host note. Every actual regression-fixture path exists at the candidate.
- Candidate audit folder contains 15 Markdown files; all twelve module SHA-256 values match the candidate LF blobs.
- Root PLAN and TODO retain their complete baseline byte prefixes. Removing only each new 7.54.0 changelog block reproduces its baseline content.
- Six added project links resolve once each. Source inspection confirms C#9, appropriate project references/defines, and the provider fixture's Unity-only exclusion; no result count is inferred from those links.
- Scoped `git diff --check <baseline> <candidate> -- <29 paths>` exited 0 without diagnostics. Of 124 added relative Markdown link pairs, 119 resolve in the candidate tree; five citations point to four audit artifacts outside that tree. External URLs were not fetched.

## Uncounted observations and coverage gaps

- The matrix retains stale per-claim pending diagnostics/render requirements beneath final acceptance and contains inaccurate bookkeeping such as `release_opt_in_correction.standalone_retain` naming `COREAI_HAS_LLM`/`COREAI_HAS_LUA` instead of the actual `COREAI_LLM`/`COREAI_LUA`. Some overlap-owner labels also disagree with the historical lease prose. Its explicit frozen/exact-prompt authority and final acceptance blocks bound those records; no consuming automation or concrete introduced failure was established. These are uncounted reconciliation observations.
- The documented Windows run used temporary optional vendor symbols; the shipped Standalone symbols differ intentionally. Separate optional configurations and clean consumer import remain explicitly unrun. The retained Windows selection is not proof of a clean candidate checkout/import.
- Receipt totals were cross-compared as documented metadata, not independently rerun or certified against execution. No saved artifact execution, publication URL/tag resolution, performance, live-provider, browser or player proof was performed here.
- Four historical artifact targets are outside the pinned tree (`final-validation.json` and the Core/Lua/analyzer TRXs under `artifacts/audit-2026-10-23/`). All four exist locally. Their relative citations are local evidence references, not independently available pinned source evidence; this review does not establish their availability in a fresh clone.

## Context and verification limits

All primary changed hunks were read. Untouched bulk changelog/TODO/roadmap history was not semantically reaudited; its preservation was checked against the baseline. The large matrix was read via complete field/record projections with shared values factored, not by interpreting its formatting as evidence.

Adjacent contracts were read only to validate assigned claims: release utility/version seams; Mirror runtime/test asmdefs; queue/component defaults; the session guide; the full actor-keyed factory/interface and selected default-chat/installer/orchestration-chat methods. Linked fixture inspection was targeted to imports/guards and selected assertion bodies (including provider metadata/replay and engine-free Rbx cases); it was not a second full review of those fixtures. The derived-source import was sampled for its transformation contract. Benchmark classification diff and coherent-castle fixture diff were inspected to check the corresponding documentation claims; no grading or runtime result was inferred.

No builds/tests, Unity/MCP, model/network calls, additional agents, production/test fixes, Git index/ref mutation, commits or publication were performed. Proposed regression work remains root-owned.

## Handoff

- CHANGED: this report only.
- VERIFY OUTPUT: all 29 paths / 38 hunks covered; static inventory/version/path/hash/history comparisons above; one PR finding, no executed runtime proof.
- NOT DONE: root adjudication and regression proof; independent execution/consumer/platform/publication verification.
- CONCERNS: PR-14-001; documented selection/configuration limits and uncounted matrix bookkeeping ambiguities.
- QUESTIONS: none.
