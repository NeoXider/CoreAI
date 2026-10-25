# CoreAI 7.54.0 repair and release plan

Updated 2026-10-25. Root accepted the twelve source joins and available measured gates: Windows EditMode7,917/0Failed/13Skipped and hermetic Windows PlayMode386/0Failed/2Skipped, alongside portable/analyzer/Node/version receipts. Publication uses the v7.54.0 Git tag and GitHub release; platform and quality limits remain explicit.

## Goal and invariants

The real goal is a reusable CoreAI framework with reproduced defects repaired, honest regression evidence, documented generic multiplayer-session integration and a verified 7.54.0 release published by the lead. Preserve the owner's priorities and existing public APIs, optional-package combinations, serialized settings, backward compatibility and data migrations. Source-confirmed audit claims are intake evidence; every repair needs a reachable shipped contract and a failing scenario before acceptance.

Correctness, immutable admitted context, cancellation, exactly-once cleanup, ordering, replay safety, bounded work/retention, failure isolation, persistence receipts, observability and replaceable services apply where each block promises them. Performance/memory consequences require appropriate measurements; arithmetic or fixture success alone is insufficient.

The host owns accounts, authentication, identity issuance, matchmaking and session/UI policy. CoreAI must honor caller-supplied context/storage namespaces and offer transport-neutral session/context/cancellation/reset/disposal/composition seams. UH-01 remains uncounted integration guidance; `ClearChat(false,false)` resets the active role cache only. MEM-001 and MEM-005 retain their separate supplied-context obligations. Do not infer logout or implement a game's account service.

Conventions: explicit C# types; English prose; permitted XML/WHY/TODO/HACK comments; interface/installer composition; runtime-first behavior; no blocking waits/threads on WebGL; `CoreAiWebGlPersistence.Sync()` false is an observable refusal, true means automatic persistence is armed. Do not restore manual `FS.syncfs` or invent a browser transaction acknowledgement. New optional Lua/LLM tests use the matching compile guards. A new portable fixture must be linked explicitly by its serialized project-file owner.

Non-goals: standalone demos, example-game features, vendor code, unrelated assets/ETFX metadata, full game development, future ladder completion, replacing intended FullAccess policy, unsupported parity, live-model benchmarks or unsafe allocation/exhaustion experiments. Existing TODO references do not silently expand scope. Carry-ins in the matrix require an explicit lead admission decision.

## Work sequence and exit receipts

| Stage | Owner and action | Required exit evidence |
| --- | --- | --- |
| 0. Intake | Lead verifies local/remote main, branch/worktree, user identity, unrelated diffs, package versions, Editor executable/PID/project and active run/import state. Preserve ongoing user work. | Saved base SHA, repository/remote state, scope snapshot and actual Editor identity. No inferred clean/current status. |
| 1. Contracts and leases | Lead adjudicates host/game boundaries, documented limitations and hypotheses; approves exact whole-file source/test/doc owners using [FILE_LEASES.md](FILE_LEASES.md). | Named owner, files, signatures/invariants, acceptance scenario and dependencies for every dispatched task. No overlapping active writers. |
| 2. Module revalidation | Owners read current source/callers/guards and reproduce each admitted claim with small deterministic inputs. Classify confirmed framework defect, game integration, documented limitation, hypothesis or refuted/stale claim. | Per-ID evidence and reproduction result; false-positive rationale retained. Unreproduced claims are not called fixed or passed. |
| 3. Repairs and regressions | Owners implement only confirmed/lead-approved seams, tests and local module docs; coordinate foreign files through the lease holder. | Actual diff plus failing-before/passing-after contract tests, cancellation/fault/compatibility cases and each owner's task/verification report. |
| 4. Independent review | Lead obtains a second opinion from another model family for architectural changes and a final adversarial review for the large integrated wave. | Reviewed real diffs, resolved findings and explicit unresolved concerns; model and reasoning effort recorded separately. |
| 5. Integrated verification | Lead serializes shared builds/tests. SingleUnityOperator alone controls Editor/MCP, defines/imports, EditMode/PlayMode and exports. | Fresh compile, TRX/XML case accounting and positive fixture presence for each required configuration; platform gates remain distinct. |
| 6. Release metadata/docs | Serialized release owner runs `python tools/bump_version.py 7.54.0` after base/version decision; updates only the two changelogs and applicable user docs/roadmap/footer. | Seven manifests, internal pins, MCP version and release lines agree; `python tools/bump_version.py --check` passes; generic session guide references tested public seams. |
| 7. Publication | Lead reviews complete scoped diff, checks user author identity, commits with no co-author/AI trailers, tags and pushes approved release. | Commit SHA, tag, remote branch/tag SHAs and saved checks/release receipts. A local commit or attempted push is not publication. |

## Task contract before delegation

Write GOAL, exact SCOPE, change nothing else, public signatures/compatibility, INVARIANTS, likely PITFALLS and VERIFY command with expected assertions. An engineer with zero conversation context must be able to execute it. Keep design decisions with the lead; identify the one most likely failure explicitly. No worker commits, Unity/MCP operations or asset/meta edits without an exact additional lease. Failed twice means escalation or a reasoned redesign, never a third blind retry.

Each owner reports CHANGED, VERIFY OUTPUT, NOT DONE, CONCERNS and QUESTIONS. Add baseline/lease identifiers, per-ID disposition, actual command/exit/case results, receipt paths and remaining platform gaps. Actual report paths and source/handoff states are indexed in [README.md](README.md); retained prompts and root handoffs define ownership. Reports do not establish executable acceptance.

## Verification layers

Only the lead runs these shared gates after freezing the relevant sources. Commands below are planned, not executed receipts:

```powershell
dotnet test tools/portable/Tests/CoreAI.Portable.Tests.csproj -c Release --results-directory artifacts/fix-wave-2026-10-24/core --logger "trx;LogFileName=portable-core.trx"
dotnet test tools/portable/LuaTests/CoreAI.Portable.LuaTests.csproj -c Release --results-directory artifacts/fix-wave-2026-10-24/lua --logger "trx;LogFileName=portable-lua.trx"
dotnet test tools/CoreAI.Tools.sln -c Release
node Assets/CoreAiUnity/Tests/Node~/persist_fs_jslib_test.js
node Assets/CoreAiUnity/Tests/Node~/fetch_sse_jslib_test.js
python tools/check_positive_module_opt_in.py
python tools/bump_version.py --check
```

Reuse the actual current CI/test documentation for Unity invocation and configuration. Unity EditMode must cover `core` (no optional symbols), `llm` (`COREAI_LLM`), `lua` (`COREAI_LUA`) and `full` (both), with positive fixture-presence checks and verified Standalone/WebGL symbol injection. Require `LlmPipelineInstallerEditModeTests` in llm/full and `LuaCsSecureSandboxEditModeTests` in lua/full. Exercise optional Mirror/MIRROR fixtures when available; absence is an open gate, not a pass. Run deterministic `CoreAI.Tests.PlayMode.FastNoLlm` and focused real Physics/material/UI/teardown tests affected by the repairs.

Count actual TRX `UnitTestResult` and NUnit XML outcomes, nonzero matching cases and every skip/refusal. Portable Lua engine unavailability and Unity-only refusals remain unexecuted. Preserve a failure before changing code and rerun the applicable tests after the final edit. Do not repeat passing broad suites without a new change/failure/concern. The original audit receipts remain historical evidence only.

Current committed Unity/URP dependency import must be verified independently of cache metadata. Consumer UPM/path fixes require a clean consuming fixture. Shader changes require actual compile/render comparisons. Relevant built-player, IL2CPP, WebGL reload/cancellation/persistence, browser/device and real-network/fault/resource gates need their own saved receipts. A mock/offline transport does not establish sockets, room capacity or 100-player behavior. The lead records blocked/unavailable gates explicitly and must not claim those layers accepted.

## Generic multiplayer-session integration deliverable

The host-owned [multiplayer-session guide](../../Docs/CoreAI/MULTIPLAYER_SESSIONS.md) is present. Its final Unity acceptance remains open. Document host-supplied admission/identity, immutable per-turn context, storage namespace selection, session connect/disconnect/supersession, cancellation versus side-effect commitment, per-view reset/disposal and replacement ownership. Cover solo, embedded, player-hosted and dedicated compositions through existing replaceable contracts. Mirror is one optional adapter. State ordering/idempotency and resource limits actually implemented and tested, including failure during callbacks and disposal.

Provide small host-facing composition examples with no account database, login UI, matchmaking service or implicit logout detection. Explain persisted-history preservation separately from transient view state, including every cached role. Tests should replace identity, transport and storage with fakes and exercise concurrent supplied sessions; unsupported replication/world handover and product policy remain explicit limits.

## Acceptance and milestones

Before accepting: read the real diff and actual receipts, verify failure/cancellation/late-completion paths and optional combinations, reject out-of-scope edits, check docs and migrations, then ask what was missed and inspect that path. A task is done only at its declared evidence layer. Leave unexecuted gates open; release notes must match the applicable evidence rather than the planned command list.

- Historical intake milestone: all 89 canonical IDs, two aliases and UH-01 were represented; the first five leases were dispatched. The current register now records all twelve module assignments, a separate audit-writer sublease and release documentation. A proposal alone never activates a lease.
- Approved cross-contract: LUAU-007 uses a trusted resolver returning iterator/state/control, never an actual raw metatable. The downlevel option is `TrustedIteratorResolverName`; the framework VM reserves `__coreai_luau_iter`, while standalone transform limits remain explicit. Mods 05 has authored the paired `LuauSourceGate`, factory and `LuaCsSecureEnvironment` integration; the source-final Lua aggregate now passes; final Unity/configuration acceptance remains separate. Only the lead executes the isolated actual-VM probe.
- Module milestone: append accepted dispositions, source/test hashes and receipt references after each reviewed module batch.
- Integration milestone: freeze sources, record exact test configuration/case totals and unresolved gates.
- Release milestone: append final version/commit/tag/remote receipts only after verified publication.


## Historical coordination checkpoints - 2026-10-25

Before planning: reconcile source-final handoffs with actual execution, preserve public/context/persistence contracts and human priorities, and never turn intake findings or skipped tests into runtime acceptance. Before delegation: all twelve original source owners and audit-writer/Mirror/benchmark handoffs are complete; all C# leases are released. Root reopened only the exact post-final4 Hub follow-up; its C# writes are frozen during root PlayMode. The documentation lease remains active. Before accepting: root reviews final diffs, raw outcomes, named exclusions and platform gaps before publication.

The intake remains 91 raw / 89 canonical, two aliases, uncounted UH-01 and MOD-003 as a finite-default configuration limit. Root admitted EXEC-A3, host A1/A4/A13 and optional audit retention/same-path coordination. WORLD-SCOPE is a default shared-world/storage preset boundary; A7 is not silently claimed fixed without its own admission receipt. All source-owner assignments used `gpt-6.1-sol` and reasoning effort `max` separately. Local MiniCPM setup is a separate task and no CoreAI feature or live-provider acceptance.

### Current root receipts

The documentation worker read actual TRX result nodes; console skip counters are not used.

| Receipt below `artifacts/fix-wave-20261025/` | Actual outcomes | Boundary |
| --- | --- | --- |
| `core-final5-results/core-final5.trx` | 2,853 = 2,850 Passed / 0 Failed / 3 NotExecuted | Latest portable Core rerun; same count as final2 because two new provider controls are Unity-only. |
| `lua-source-final-results/lua-source-final.trx` | 1,973 = 1,932 Passed / 0 Failed / 41 NotExecuted | Source-final portable Lua; all skips remain unexecuted. |
| `analyzer-results/analyzer.trx` | 8 Passed | Analyzer only. |
| `persistence-node.log` / `sse-node.log` | 9 PASS checks / `ALL PASS` | Mock interop/fault/backlog; no browser completion, native ABI or throughput proof. |
| `version-bump-recheck.log` | Lockstep 7.54.0 PASS | Seven packages, twelve internal pins, MCP constant and root-managed version lines. |
| `positive-opt-in-fixed.log` | PASS using index ProjectSettings | Root's tracked-text snapshot; temporary working-file vendor symbols are excluded from the release. |
| `unity-editmode-final3.xml` | 7,911 = 7,873 Passed / 28 Failed / 10 Skipped | Previous raw integrated Unity result; later source/fixture fixes require final4. |
| final3 GPU projection / `NET001_*` | 10/10 / 17/17 Passed | Included subsets, not additional totals; actual rendering and offline network fault tests. |
| `unity-editmode-final4.xml` | 7,911 = 7,896 Passed / 2 Failed / 13 Skipped | Two exact external cases excluded; remaining facade/Hub failures require later correction/rerun. |
| final4 Mirror / NET001 / GPU subsets | 229/229 / 17/17 / 10/10 Passed | Included subsets, no real socket/device/load claim. |
| `unity-playmode-final.xml` | 146 = 143 Passed / 2 Failed / 1 Skipped; exit 2 | Two exact FullAccess vendor/demo failures; Benchmark 4/4 is only WebGL-compiled coverage. |
| `unity-adversarial-red-final.xml` | 49 = 30 Passed / 19 Failed | Exactly two pre-ADV runtime snapshots; current progress 9/9 and registry 13/13 names pass in final4, runtime restored in finally. |
| Next integrated Unity / Standalone fake-only PlayMode | Pending root receipts | Compile guarded benchmark harness regressions; exclude live benchmarks/recapture and exact vendor/demo cases. |

Root visually inspected the curved rotated nonuniform direct scaled/baked render pair and reported matching brick patterns. Historical failing-before execution38 (26 Failed/12 Passed), memory17 (16 Failed/1 Passed), selective passes and negative/invalid diagnostics remain in the matrix ledger. Source-final passes supersede current diagnostic status without changing raw saved outcomes.

### Exact exclusions

The release ledger retains the raw failures and authorizes only:

- `CoreAI.Tests.EditMode.BenchmarkLegacyReportEvidenceEditModeTests.RealReports_TemporaryFakeRepairPreservesEveryKeptMarkdownSectionAndCounter`: missing six `TestResults/CoreAI/Benchmarks/BENCHMARK_20261015_224850_*.json` historical inputs; no evidence fabrication.
- `CoreAI.Tests.EditMode.CoreAiLuaWorldModuleEditModeTests.MigratedDemo_OwnsLuaConfigurationInChildModule("Assets/CoreAI.Demos/FullAccess/FullAccessDemo.unity",True)`: third-party TMP reflection constructor at `Assets/Plugins/IngameDebugConsole/Scripts/CommandInputField.cs:60`; vendor/demo repair is outside scope, with no suppression.

The raw PlayMode vendor failures are exactly `CoreAI.Tests.PlayMode.CoreAiDemoScenesSmokePlayModeTests.AllPublishedDemoScenes_LoadWithScopeCameraAndSupportedShaders` and `CoreAI.Tests.PlayMode.FullAccessDemoScenePlayModeTests.FullAccessDemo_LoadsWithFullLuaAndAnEmptyWorld`. Both are additional case exclusions for the same FullAccess constructor root cause. Existing `COREAI_LLM && !UNITY_WEBGL` guards omit benchmark harness/parallel fixtures in the raw run, so Benchmark 4/4 is not full acceptance; root will switch to StandaloneWindows64.

The root source-confirmed Unity filter rules combine negated exclusions with AND. The deterministic PlayMode selection will exclude live benchmarks and recapture. Excluded or skipped cases do not count as passes.

### Remaining release gates

Final4 XML/log were read directly. Root corrected the facade test by explicitly stopping its actor session before enqueueing a distinct logical scope; original cancellation/success/partition assertions remain. The strict Hub count failed with three destroyed chat-panel targets and one live page. [MD17](17-hub-final-integration.md) now records the source-final fixture-only explicit panel lifecycle cleanup, retaining exact unsubscribe and Hub 1-to-0 assertions. All C# is frozen and final5 Windows EditMode passes with zero failures. Windows PlayMode subsequently exposed five benchmark failures; two exact follow-up owners are active while other C# remains frozen. Read the passing final5 Windows EditMode receipt; retain raw Windows mixed runs, retain the earlier cancellation-budget timeout as unreproduced after a later scoped1.06-second pass, without claiming a product fix or weakening its watchdog, and await fresh applicable checks and final acceptance. Consumer UPM and separate optional configurations require concrete receipts. No built-player, IL2CPP, real browser durability/reload, device, real-socket, room/load, performance or live-provider acceptance is established. Automatic persistence arming is not a browser transaction acknowledgement.

The root reports an index-only Standalone opt-in correction reconstructed from the immutable baseline: remove `MIRROR`, all `MIRROR_*` and `EDGEGAP_PLUGIN_SERVERS`, retain `DOTWEEN` and intended `COREAI_HAS_HUB/LLM/LUA`. The temporary Editor working-file vendor symbols and generated migration diffs will not ship. Documentation workers do not edit settings/index/source.

Version metadata is checked at 7.54.0; changelogs remain unpublished until root supplies final acceptance and publication evidence. Commit identity, no-coauthor policy, branch/tag/remote SHAs and any GitHub release are root-owned. Historical root PLAN bytes, TODO priorities, frozen audits and old changelogs remain preserved.

## Final root acceptance - 2026-10-25

Root accepts the twelve module source joins and the available measured gates. Portable Core final5 is **2,850 Passed / 0 Failed / 3 NotExecuted** (2,853 rows); source-final Lua is **1,932 Passed / 0 Failed / 41 NotExecuted** (1,973 rows); analyzer **8 Passed**. Persistence Node has nine PASS checks, SSE ends `ALL PASS`, and lockstep 7.54.0 plus the tracked-text positive opt-in check passed.

Windows EditMode final5 is **7,917 Passed / 0 Failed / 13 Skipped** (7,930 cases), exit0. Final hermetic Windows PlayMode is **386 Passed / 0 Failed / 2 Skipped** (388 cases), exit0: Benchmark247 Passed/1 Skipped, MCP12 Passed, Mods24 Passed, FastNoLlm103 Passed/1 Skipped. G6 is34/34; the cancellation-budget case Passed in0.214230 s. The live SkillSet case is absent from the final XML. Both final PlayMode skips are existing batch-mode WaitForEndOfFrame limitations. GPU10/10 and offline Mirror229/229 including NET00117/17 are included subsets, not extra totals.

The exact two EditMode and two PlayMode environmental case exclusions remain documented below. The intentionally live skill-cost/benchmark/recapture selection is separate from those environmental exceptions. Both earlier Windows389-case runs were mixed: the unmarked local SkillSet test contacted MiniCPM at port1234 for structural skill/direct runs (observed last-call timings2,115/7,002 ms). They establish no model-quality ranking or broad live-provider acceptance. Root made that test Category Benchmark and Explicit/manual-only before the final hermetic run.

The prior16.295306-second cancellation watchdog timeout remains unexplained. The later focused1.065373-second pass and final0.214230-second full-suite pass are observations, not a claimed root-cause or product fix. Case-scoped phase observability remains; no timeout/assertion/budget/exclusion was weakened. All five earlier benchmark failures also Passed, after two harness fixes and the replay/G6 fixture-contract corrections in MD18/19.

No built-player, IL2CPP, browser durability/reload, device, real-socket/room/load, performance or live-model-quality acceptance is established. Optional configurations and clean consumer UPM need their own receipts. Acceptance is bounded to the measured layers and exact selection above; skipped/excluded cases are not passes, and 91 raw/89 canonical intake findings are not89 runtime-confirmed fixes.

Publication uses the Git tag and [GitHub release](https://github.com/NeoXider/CoreAI/releases/tag/v7.54.0). Root binds the final source/receipts to the release commit/tag; no self-referential SHA is required inside the commit. Historical negative/invalid receipts and dated pending checkpoints remain below/in PLAN, superseded only as current status. The provider-intake relocation, frozen audit15-file layout/12 hashes, exact exclusions, MD14-19 handoffs and unrun limits remain part of this acceptance evidence.
