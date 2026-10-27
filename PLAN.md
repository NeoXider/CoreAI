# Current status and next steps

## Active repair checkpoint - 2026-10-26

Current branch: `demo`, based on released `bc3fd74f` / `v7.54.0`; the older ladder checkpoints below are historical.
Goal: close all 21 accepted PR findings in `TODO.md`, review every repair diff, and verify the repaired framework and demos.
Acceptance preserves actor/session scope, public API compatibility, durability, Unicode, cancellation, ordering and replaceable host policies.
Before delegation: exact disjoint file leases; workers do not commit or operate Unity. Root owns builds, tests, Editor and Git.
Native GPT workers exhausted their reported quota. Named free OpenCode workers are continuing partial changes through `neoxider-agents`.
The release-runtime regression baseline is 36 cases: 28 failed and 8 passed; this is failing-baseline evidence, not repaired acceptance.
All 17 published demo scenes require runtime checks; manual Play Mode must include agent-driven mod creation and editing.
Publication order: verified repairs to `demo`, then promotion to `main` after demo acceptance. The published tag remains immutable.
Open work and receipts: `dev-docs/pr-review-2026-10-25/PLAN.md`, `FIXES.json`, and `artifacts/pr-review-20261025/`.

Checkpoint 2026-10-26: Core and Source compile; Mods.Hub and its dependencies compile with zero errors.
Portable Core receipt `core-repair-final4.trx` contains 2,853 Passed / 3 NotExecuted / 0 Failed (2,856 result rows).
The user explicitly waived the 15-line implementation limit when external workers were unavailable; root may finish remaining work.
Fresh native GPT-6.1 Sol/max workers are now verified live, independently reviewing repairs and adding regression coverage.
Their actor audit found a history default-literal overload ambiguity; the follow-up removes it and adds compile/runtime controls.
Added scope: migrate RedoSchool CoreAiPro capabilities as a portable Gateway Client feature, with thin Unity integration.
The user's standalone requirement rules out a Unity-only implementation. Existing server transport and build guards must be reused.
Gateway design/provenance: `dev-docs/server-bridge/ASSESSMENT.md`. Feature work is staged while root verifies the existing repair wave.
No new repair commit, demo push or main promotion has been accepted yet; all required Unity/demo gates remain open.

Verification checkpoint: focused portable Lua 130 Passed / 0 Failed; live Unity focused2 ran 432 cases, 429 Passed / 3 Failed.
Focused3 cleared the cancellation-subtype fixture and real-descriptor UI control: 27 Passed / 1 remaining Advanced-presentation case.
Reports 33/35 found notification/lifetime and Mirror-generation follow-ups; fixes are owned separately and require new execution.
Gateway candidates are frozen in `artifacts/server-gateway-20261026/staging/` with 11 baseline hashes and 29 candidate files.
Isolated C# 9/netstandard2.1 Gateway Core + net8 fake-wire fixtures passed 54 cases; standalone sample compiled and offline help passed.
Staged Source also compiled against real installed Unity references with zero errors; this is compile-only, not Unity/runtime acceptance.
Independent Gateway review is in progress; correlation precedence, leaf disposal, transport cancellation cleanup and policy-model consistency need adjudication.
No Gateway live import, repair publication, release-tag change or main promotion has been accepted.

Checkpoint: 2026-10-15, branch `main`. Current release: **7.50.2** (2026-10-15), all seven packages in lockstep.
The one MVP ladder of record is `Docs/CoreAIMods/ROBLOX_API_ROADMAP.md` §4 (renumbered on 2026-09-24:
§4.1 maps the old numbers; `Docs/ROADMAP.md` §4 is the one-screen table). Open work is in `TODO.md`,
filed under the same rung names.

## Where we are

- **MVP3 (world/place package) is closed (2026-09-25) and released in 7.47.0** on the Unity gate
  (Unity 6000.3.14f1): EditMode full 6550 total / 6539 passed / 0 failed / 11 skipped, `core`
  5266/5254/0/12, `llm` 6234/6221/0/13, `lua` 5582/5572/0/10, `MIRROR` 6550/6539/0/11; PlayMode
  `FastNoLlm` 95/94/0/1; portable engine-free 2172 passed / 0 failed, Lua tier 1848 total / 1846 passed /
  0 failed / 2 not run. Decision (tech lead, 2026-09-25): MVP3 is closed on the Unity EditMode/PlayMode
  `FastNoLlm` gate; spikes S1/S2 and the IL2CPP/WebGL player checks are open follow-ups that gate the next
  networked rung, not MVP3's product scope.
- **Releases after MVP3 sit outside the ladder** (detail in both changelogs): 7.47.1–7.47.4 (benchmark G6
  budget and grading fixes, live-test isolation, the Roblox Studio/CoreAI parity fixtures), 7.48.0
  (attachments and camera images end to end, visible Rbx material relief, a WebGL player check in real
  Chrome), 7.49.0 (safe generation progress; an SSE error or a stream without a terminal frame fails
  explicitly), 7.50.0 (messenger-style chat feed with a jump-to-latest button and unread badge), 7.50.1
  (tool-contract and memory prompts name only real behaviour), 7.50.2 (`AgentSessionInspector` and the
  structured retry carry the whole request). Latest full EditMode run: 6753 total / 6736 passed / 0 failed /
  17 skipped (7.50.2, recorded in its release commit).
- **The live-model PlayMode suite has not had one clean full run.** The MVP3 branch run had 4 of 165
  failing (live timeouts on an unresponsive LM Studio server). On 7.47.1 it ran assembly by assembly in the
  open Editor (LlmVerification against Space Bunny), and every failure passed a targeted rerun; a later
  monolithic run (7.47.3) was stopped at 233 of 271 by cascading bridge timeouts. The live fixtures changed
  in 7.48.0 (`LiveTestRequestScope`) compile but have not run (`TODO.md`, "7.47.x audit wave").
- **Audit rounds 1–3 are complete, and every finding is fixed or filed in `TODO.md`.** Round 3's
  findings are fixed by C1F (`7cf2891e`), C2F (`d4d7f95b`) and C3F (`055aed29`: nested-run steps reach
  every ancestor, the enclosing run is the innermost executing run on the OS thread, `mods_call`
  continues its caller's count and allowance, yields refused through every counted call, `sandbox: `
  trip lines). Round 2: A1-02 load order (`6248fec2`), B1-01/04/06 (`9dc55a68`), B1-07/08/10 Mirror
  (`e0737b7b`), B2-03/05/08/12 (`f817225b`), B1-02/03/05 (`3f6c18e5`), B2-01…B2-14 (`3d5b62d0`,
  `23f63eaa`), B3-01/02/08 (`02f26388`), B3-03…B3-07 (`43605d2f`). Round 3: C1-01…C1-11 (`7cf2891e`:
  two remote budget pools per sender, deferred listeners), C2-01…C2-09 (`d4d7f95b`). Also landed: one
  Roblox/Luau coercion rule on both script surfaces (`c0f6fdbc`, round-trip gap RT4 closed), the two
  causes of the Hub crash the owner hit while editing a mod (`c7397c77` Hub tabs rebuilt per
  notification off the main thread, `c0dd9fa4` unbounded Luau downleveler recursion), and reload modes
  with crash-loop protection (`3a46a24c`: a reload cleans the previous run's startup objects by default;
  two budget trips in a row suspend a mod).
- **The MVP ladder was revised** for the flagship goal (Studio+Play Roblox-like app, ~100 players per
  room, a general framework): strictly sequential, multiplayer first, plan decisions D1–D8 decided by
  the tech lead (the owner may override).
- **MVP4 (new numbering: script contexts & client runtime) is next and not started.** The MVP3
  follow-ups below gate it.

## Verification

- Unity EditMode/PlayMode run in the open Unity Editor on the owner's machine (how 7.47.1–7.50.2 were
  verified). CI runs them only when the `UNITY_LICENSE` secret is available; otherwise the Unity jobs are
  skipped with a warning, and on the merge queue a missing licence fails the gate.
- Compile gate while the editor holds the project lock: `dotnet build` on the Unity-generated csproj
  (`AGENTS.md`). The MVP3 wave, run in a cloud container without Unity, used a Roslyn build of every asmdef
  at C# 9 against Unity reference assemblies (six configurations plus Mirror) instead.
- Portable suites: `tools/portable/Tests` (engine-free, CI job `portable-core`) and `tools/portable/LuaTests`
  (Lua tier; CI job `portable-lua`, floor 1,400 passed, at most 42 not executed).

## Next three rungs

1. **MVP3 follow-ups** (the rung closed 2026-09-25 in 7.47.0; these gate the next networked rung):
   - the IL2CPP/WebGL player checks of "Check the tests" in `TODO.md` (the native-stack mod
     `local function f() pcall(f) end f()` ends in "C stack overflow", the `__concat` chain, three
     Save & run of `sample_castle3d`, the budget guard on IL2CPP);
   - the real WebGL page-reload smoke of the world package;
   - spikes **S1** (guarded VM cost on an IL2CPP Linux server build and Mono x64) and **S2** (bytes per
     CFrame patch, JSON vs binary); record the numbers in `TODO.md`;
   - one full live-model PlayMode run on a settled tree, against a backend that cancels disconnected turns.
2. **MVP4 — script contexts & client runtime** (M) — first steps:
   - design note for plan decision D1 (c): `Script`/`LocalScript`/`ModuleScript` as instances whose
     `Source` is a view over the mod source store; decide the package `format_version` question and
     whether a package without contexts loads as `shared` or is refused;
   - add the declared context to the `@coreai` header and `LuaModManifest`; rewrite `IsNetworkServer` on
     context + topology; `CONTEXT_VIOLATION` tests with negative twins;
   - `require` for `shared` modules with the R3.2/R3.4 caching tests and `CYCLIC_REQUIRE`.
3. **MVP5 — host mode over a real socket + join snapshot** (L) — first steps:
   - serve Mirror's host-mode local connection as the host's `Player` (rewrite the `KnownLimitation_HostMode_…`
     test as the positive case);
   - spike **S3** (a 5 MB snapshot over real kcp) to size the chunked join snapshot;
   - the inbound rate limit and the protocol version handshake; a first two-process harness; the
     player-host preset with its boot test.

## History of this wave

The MVP3 closure items, the audit fix waves W1–W6, audit rounds 1–3 and their fixes, the docs passes
DOCS-1/2/3/4 and the portable Lua-tier runner are recorded in `TODO.md` ("MVP3 closure and the
MVP1/MVP2/MVP8/multiplayer audit fix waves", "Closed") and in both changelogs. The 7.47.x audit wave
(released in 7.48.0) is in `TODO.md`, "7.47.x audit wave".

## Audit checkpoint - 2026-10-23

Baseline: `69d5bcc37502cd4bb464a3bf9ac0c53b1624b804` on `main`; all seven manifests declare `7.53.0`. This append preserves the earlier checkpoint and its historical verification statements. The owner explicitly requested one Markdown report per auditor, authorizing retained reports for this audit despite the default repository rule.

Before planning:

- Real goal: audit all project-owned CoreAI modules at the current baseline, with evidence for confirmed defects and a separate list of incomplete verification; no implementation fixes in this wave.
- Applicable implied invariants: built-player viability, configurable/replaceable blocks, WebGL persistence and nonblocking execution, lifecycle/cancellation stability, concurrent access and ordering, bounded resources, security/capability isolation, observable errors and production-path testability.
- Conventions: English developer prose in `dev-docs`, explicit source/configuration evidence, stable report-prefixed finding IDs with P0-P3 severity, preservation of unrelated files, and Git ownership by the lead without co-authorship.
- Exclusions: vendor internals, binaries, generated artifacts, scene/prefab edits and visual/player acceptance. Their integration or verification gaps remain explicit. The existing untracked Epic Toon FX upgrade metadata remains untouched.

Milestone: tracked inventory prepared in [00-coverage.md](dev-docs/audits/2026-10-23/00-coverage.md); [audit index](dev-docs/audits/2026-10-23/README.md) created with pending receipts. The inventory accounts for 1,485 primary source files (1,395 C# and 90 other source files), 33 project-owned assembly definitions, three vendor assembly definitions and explicitly excluded source. Fourteen semantic module reports are planned.

Coverage seams sent to the lead: nested core benchmarking assembly; optional mod Hub assembly; runtime browser bridges, Node fixtures and source/installed WebGL templates; standalone .NET sample; authored parity visual Lua; operational benchmark sweep; material shaders; supporting-plugin integration metadata; and eleven example-game C# files without an assembly-definition ancestor. The core Sandbox directory contains tracked folder metadata only; actual scripting sandbox source is assigned to report 05. These seams have proposed report ownership and need final reconciliation.

Before accepting:

- Real diff reviewed: inventory auditor read every file written and inspected the scoped append-only documentation diff; lead acceptance remains pending.
- Check results: baseline and inventory commands completed; scoped `git diff --check` exited 0, explicit new-report whitespace/Cyrillic checks passed, and five relative Markdown links resolved. No test or runtime pass follows from these static checks.
- Failure modes: each module report must cover the applicable error/cancellation/security/resource and concurrency paths and separate confirmed behavior from unexecuted verification.
- Missed scope: reconcile all report receipts against the assembly/source inventory; semantic coverage, optional configurations, Unity/player/WebGL, networking, vendor integration and final adversarial review are still open.

Next action: the lead reviews each real report and actual verification receipt, rejects unsupported completion claims, and updates the pending index from that evidence. No commits, staging or production fixes were performed by the inventory auditor.

## Audit reconciliation checkpoint - 2026-10-23

The inventory auditor read all completed module reports 01-09 and 11-14 in full; partial Mirror report 10 and the initial independent-review draft 15 were also inspected. The audit index is being reconciled from actual source-report metadata and central execution receipts, with final acceptance reserved to the lead. No other auditor report or production file was edited.

Coverage correction: the initial 1,485 suffix-filtered source files omit three executable demo `.lua.txt` TextAssets that report 12 did review. They are now explicitly listed as a supplement: 1,488 authored source units including those files. These are inventory counts, not semantic or line coverage. Sampled/excluded module branches remain visible in report 00. The lead assigned supplementary report 16 to the nine Crafting C# files and 13 material shader/HLSL files; report 15 is independent adjudication and is not included in module-finding totals.

Initial deduplication: CE-01 and LLM-002 share the failed-result streaming projection defect; LLM-002 is canonical, with its reported P1 versus CE-01's P2 retained for independent severity assessment. UH-07 aliases MEM-011's file-memory cache/path-state retention defect. Distinct isolation, persistence, wrapper and UI failures remain separate when their causal boundaries differ. Thirteen completed module reports contain 114 numbered source findings before these two aliases (13 P1, 93 P2, eight P3), or 112 candidate roots after alias removal. These are source-report metadata, not independently accepted runtime defects; Mirror and supplementary report 16 are excluded until complete.

Central receipts supplied by the lead: version check and both Node bridge harnesses exited 0; chart Python suite passed 6/6. Portable-core process exited 0, and its authoritative TRX contains 2,605 Passed, zero Failed and three NotExecuted result entries (2,608 total); console/summary skip counters disagree and are not used as the authoritative count. Positive module opt-in was interrupted after an overbroad local/cache scan, recorded as -1/incomplete, with no pass/failure claim or working-tree rerun. Lua/analyzer results and final independent/Mirror/supplementary reports are still pending at this checkpoint.

Acceptance remains open: the lead must review final source judgments and aliases, inspect actual test receipts, reconcile sampled coverage and optional/player gates, and verify that documentation-only changes preserve the existing plan/history and unrelated ETFX metadata. No audit-only source finding is presented as a fixed or regression-tested behavior.

## Audit finalization checkpoint - 2026-10-24

The owner resumed the 2026-10-23 audit. The immutable audited baseline remains `69d5bcc37502cd4bb464a3bf9ac0c53b1624b804`, seven packages at `7.53.0`; the lead verified local `HEAD`, `origin/main` and live remote `main` still match it on 2026-10-24. Historical checkpoints above are preserved verbatim. The retained per-auditor Markdown reports remain expressly authorized.

Before planning: the goal is to complete missing source-report receipts and reconcile the existing audit, without production fixes. Runtime-first composition, permission isolation, replay safety, bounded work, cancellation/lifecycle stability and honest test provenance remain the acceptance invariants. Unrelated source/assets and the existing ETFX metadata stay outside this documentation task.

Before delegation: the lead assigned disjoint ownership for final Mirror report 10, supplementary Crafting/material report 16, and this index/coverage/append-only PLAN pass. Each task names exact output files, the fixed baseline, prohibited source/Git/Unity mutations and its required readback receipt; no further agents are delegated by this finalization pass. Auditors use model `gpt-6.1-sol` with reasoning effort `max` as separate settings.

Milestone: final independent review 15 contains 14 adjudications, 13 ACCEPT, one REVISE and zero REJECT. MOD-003 is now P2: no-Rbx startup lacks a configurable main-chunk seam, while a default guard exists and the documented handler cap applies to the main chunk only with Rbx. Report 05 and the index reflect that narrower claim. Recounting the 13 complete main-report headings gives 114 raw findings: 12 P1, 94 P2 and eight P3. Removing CE-01 as the alias of canonical P1 LLM-002 and UH-07 as the alias of P2 MEM-011 leaves 112 unique roots: 12 P1, 92 P2 and eight P3. Mirror and supplementary findings remain outside these checkpoint totals until their final receipts are accepted. Provenance is 108 new, five known families and one known-API extension before aliases; both removed aliases are new.

Verification provenance: the saved central suites were run on 2026-10-23, not rerun today. Reread TRX result entries are portable core 2,605 Passed / zero Failed / three NotExecuted (2,608 entries), portable Lua 1,846 Passed / zero Failed / 38 NotExecuted (1,884 entries), analyzer eight Passed / zero Failed / zero NotExecuted. Lua's skips split into 32 unavailable-engine, three live-LM, two unavailable-demo-assembly and one unavailable-composition-root cases; its existing 1,400-pass floor and 42-skip ceiling pass. Existing Python chart tests passed 6/6; both Node bridge harnesses and the version check exited zero. The positive module opt-in scan remains interrupted/incomplete, not a pass or demonstrated product failure.

Before accepting: source-confirmed findings, independent source adjudication, portable fixture passes and runtime acceptance remain separate evidence. The lead source-checked the other five P1s outside the independent ledger; TOOL-004 was reproduced through the pure production `process_line` seam without running its rewriting entry point. A preliminary new-Markdown scan resolved 522 full-path citations across 297 source/doc files with no out-of-range anchors, trailing whitespace or missing final newline; this does not establish citation semantics. Final report 10/16 receipts, count reconciliation and the final documentation/hash receipt remain pending. Unity EditMode/PlayMode, built-player/IL2CPP/WebGL, devices, real network/load/fault/performance and live-model gates remain unrun. No findings are fixed and no new regressions are implemented by this audit.

Milestone - Mirror receipt accepted on 2026-10-24: report 10 is final and the lead read it fully. All six runtime C# files / 4,832 lines were read by its auditor; test and adjacent-source coverage stays explicitly representative/range-bounded. NET-001 is its one new P2 and was independently accepted by review 15. Fourteen main-module reports now total 115 raw findings (12 P1, 95 P2, eight P3), or 113 unique roots after the two P2 aliases (12 P1, 93 P2, eight P3). Raw provenance is 109 new / five known / one known-API extension; supplementary report 16 and final documentation acceptance remain pending.

## Framework-only audit correction and closure - 2026-10-24

The completed, lead-accepted source-audit deliverable is limited to the seven CoreAI framework packages and internal modules at `69d5bcc37502cd4bb464a3bf9ac0c53b1624b804`, version `7.53.0`. The retained audit folder remains `dev-docs/audits/2026-10-23`: 15 Markdown files, comprising 12 module reports (01-11 and 16), coverage 00, independent review 15 and README. Standalone demos, the example game, general developer tooling and vendor internals are excluded; the three generated out-of-scope reports were deleted. The seven-root inventory is 1,312 tracked source files (1,246 C# / 66 other), not semantic or line coverage. This correction supersedes earlier scope/count/pending statements while preserving every historical byte above.

Final headings reconcile to 92 raw findings (10 P1 / 77 P2 / 5 P3; 89 new / 3 known) and 90 canonical findings (10 P1 / 75 P2 / 5 P3; 87 new / 3 known). CE-01 aliases canonical P1 LLM-002; UH-07 aliases P2 MEM-011. Known families are MEM-006, MEM-008 and HUB-010, with no extension. MOD-003 is a P2 configuration limitation. Independent review: 14 claims, 13 ACCEPT / 1 REVISE / 0 REJECT. Report 10 is final with one new P2; report 16 is final with four new P2s after all nine Crafting C# and 13 shader/HLSL files were read. Full/targeted/sampled limits remain explicit.

Saved 2026-10-23 checks reread on 2026-10-24 remain core 2,605 Passed / 0 Failed / 3 NotExecuted, Lua 1,846 Passed / 0 Failed / 38 NotExecuted, analyzer 8 Passed, both Node bridge harnesses passed and seven-package version check passed. Positive module opt-in remains interrupted/incomplete. URP 17.6.0 versus cached 17.3.0 is not import-validated; fog is a hypothesis. No findings were fixed or new regressions implemented, and no tests/builds/Unity/player/WebGL/network/device/performance/model benchmarks ran during closure. Runtime and optional-consumer gates remain NOT RUN. Closure checks cover the 15-file set, heading counts, links/whitespace, preserved frozen reports, append-only PLAN and scoped diff; the lead regenerates `artifacts/audit-2026-10-23/final-validation.json` after this freeze. No Git index/commit mutation; unrelated ETFX metadata and the developer index remain untouched.

## Host account/session boundary adjudication - 2026-10-24

Before planning: apply the owner's framework/host boundary without another audit or production changes. The game owns authentication, account switching, identity issuance and session/UI lifetimes; CoreAI must honor supplied context/storage namespaces and expose generic cancellation/reset/disposal/composition seams. UH-01 assumed retaining one panel across users without host reset/recreation, so its evidence is retained as an uncounted integration note. ClearChat(false, false) stops generation and clears the feed plus only the active role cache while preserving persisted stores; retained panels need every role handled or recreation. MEM-001 and MEM-005 remain confirmed supplied-context defects. An all-view-state reset helper is advisory.

Milestone: report 04 now has seven new counted defects (one P1 / six P2), UH-02 through UH-08. Final totals are 91 raw (9 P1 / 77 P2 / 5 P3; 88 new / 3 known) and 89 canonical (9 P1 / 75 P2 / 5 P3; 86 new / 3 known). The prior 90-canonical/10-P1 totals are superseded. CE-01 -> LLM-002 and UH-07 -> MEM-011 remain the two aliases. The 12 module reports / 15 Markdown files, seven packages and 1,312 tracked source files are unchanged; earlier PLAN bytes are preserved.

Before accepting: verify confirmed headings, local links, English/whitespace, scoped diff and preserved frozen files. No tests, builds, Unity/MCP, model calls, source/assets or Git index/commit changes belong to this documentation correction. Existing execution receipts and unrun gates remain unchanged; the lead must regenerate the stale final-validation.json after the four-document freeze.

## Framework repair and release wave intake - 2026-10-24

Before planning: the owner authorized module-wide revalidation/fixes, real regression tests, generic multiplayer-session integration guidance, integrated checks and a 7.54.0 release/docs/push. The starting intake is 89 canonical findings, two aliases and UH-01 as uncounted host guidance. Revalidate contracts and failure cases before changing code; no automatic implementation of every claim. Preserve public APIs, optional combinations, saved data and human TODO priorities. Game account/login/matchmaking and product lifetime policy stay host-owned; CoreAI supplies configurable context/session/cancellation/reset/disposal/composition seams.

Milestone: durable plan, claim-to-file/regression/dependency matrix and whole-file register are in dev-docs/fix-wave-2026-10-24/{PLAN.md,module-map.json,FILE_LEASES.md,README.md}. The matrix records seven explicit carry-in proposals requiring lead admission rather than expanding all legacy TODOs. The lead approved/dispatched Luau 07, Crafting/shaders 16, MCP 09, Mirror 10 and Benchmark 11; shared execution/memory/provider/host/Mods/Rbx/Hub files still require exact owners. LUAU-007 uses a trusted iterator/state/control resolver, never raw metatable access; VM/gate/factory integration belongs to the upcoming Mods holder and actual VM-probe execution to the lead.

Before delegation/acceptance: each task needs exact file/signature/invariant/acceptance-test scope, likely pitfalls and no unrelated edits; every owner writes its own task/verification report. Lead reads real diffs and actual failing-before/passing-after/failure-path receipts, arbitrates overlaps and obtains independent architectural/final review. Only SingleUnityOperator touches Editor/MCP; only lead runs shared integration gates and owns Git. The lead reports fresh origin still 69d5bcc37502cd4bb464a3bf9ac0c53b1624b804, matching Unity 6000.6 installed and no CoreAI Editor at intake; no Unity pass is claimed. Version/changelogs/aggregate docs and portable project links remain serialized. Implementation, portable tests, Unity matrix/FastNoLlm, consumer/player/WebGL/device/network/performance and commit/tag/push receipts stay distinct; unavailable gates remain open. Publication uses Neoxider <xotrios@gmail.com> with no co-author/AI trailers and verified remote SHAs.

## Repair integration milestone - 2026-10-24

Goal and scope: finish confirmed framework repairs, reusable multiplayer guidance, regressions and release 7.54.0. Game authentication and lifetime policy remain host-owned. All workers use gpt-6.1-sol with reasoning effort max.
Delegation: all twelve module workstreams were dispatched; audit logging is a separate whole-file host sublease. Memory owns AgentBuilder/AgentMemoryPolicy; providers own ILlmClient and Meai adapters; execution owns the remaining orchestration files. Exact exclusions are in each retained task prompt. Settled CLI transport/capacity failures require reviewed handoff; native finish_providers now exclusively owns 03.
Acceptance evidence: immutable HEAD plus execution regressions produced 26 failed/12 passed (38 total); derived existing-API memory regressions produced 16 failed/1 passed (17 total). Current memory checkpoint produced 236 passed/0 failed/0 skipped, before a further summary-resource cleanup found during root review. Receipts are under artifacts/fix-wave-20261024. Production Luau assembly builds with zero warnings/errors; its full semantic tests remain pending.
Root review decisions: cancellation targets ActorId plus connection SessionId and optional RoleId; global scope/role cancellation stays administrative. Summary snapshots need deterministic discard after failed/cancelled preparation, and in-memory CAS keys must be independent of process working directory. Preserve optional interfaces and storage-corruption evidence.
Open gates: final source convergence, independent review, full portable suites, actual Unity tests and optional Mirror configuration. CoreAI Editor PID 52592 was opened with Unity 6000.6.0f1 for import; transient integration compile errors are not a Unity pass. Version bump, commit, tag, push and release have not occurred.


## Repair documentation and diagnostic checkpoint - 2026-10-25

Before planning: finish confirmed framework repairs, user integration guidance and an evidence-backed 7.54.0 release; retain public compatibility, supplied-context isolation, failure/cancellation/cleanup contracts and human priorities. Game authentication, matchmaking, transport ordering and connection/view policy remain host-owned. Before delegation: all twelve module reports now exist, with separate audit-writer and documentation leases, exact disjoint prompts and model `gpt-6.1-sol` / reasoning effort `max`. Settled CLI holders will not resume. Before accepting: read actual diffs and receipt outcomes, preserve failed evidence and every unrun gate; do not turn 89 intake claims into 89 verified fixes.

Current source checkpoints: execution01, memory02 including lifetime/CWD cleanup, providers03, host04 and multiplayer guide, Mods05, Rbx06 lifetime join, Luau07, MCP09 and crafting16. Hub08 and audit04 are active; Mirror10 and benchmark11 final report handoffs remain pending. Memory owns AgentBuilder/Policy, Providers ILlmClient/MEAI, Execution other orchestration, Hub HubIntegration and Rbx its six named LuaCs files. The [current wave index](dev-docs/fix-wave-2026-10-24/README.md), [leases](dev-docs/fix-wave-2026-10-24/FILE_LEASES.md) and [matrix](dev-docs/fix-wave-2026-10-24/module-map.json) supersede intake-only state. Audit totals remain 91 raw / 89 canonical, two aliases and uncounted UH-01; MOD-003 is a finite-default configuration limitation.

Directly reread immutable red receipts: execution38 = 26 Failed / 12 Passed, memory17 = 16 Failed / 1 Passed. Accepted October24 selective receipts: Core801 Passed, providers208 Passed, Luau VM69 Passed, Node SSE ALL PASS. Core801 includes the memory lifetime/CWD follow-up, not 801 memory-only cases. New SSE date-token red3 = 2 Failed / 1 Passed prompted root DateParseHandling.None correction; rerun pending. October25 full Core diagnostic has 2,839 actual rows = 2,833 Passed / 3 Failed / 3 NotExecuted; root corrected benchmark normalization/Markdown and the old malformed-SSE fixture expectations, with final rerun pending. Focused Lua diagnostic has 256 = 244 Passed / 4 Failed / 8 NotExecuted; MOD001/LUAU007 and two RBX006 failures need root triage/rerun. The earlier full-Lua result is empty after native OutOfMemory and is not accepted.

Actual-project Unity startup crashed in UDS before compilation; the empty same-Editor6000.6 smoke ran zero tests. Root preserved Library/DataStore and is recovering actual-project import. No Unity test pass, render/consumer/player/WebGL/device/network/performance acceptance or publication is claimed. Root ran the standard 7.54.0 version bump; both changelog drafts are dated October25 and remain provisional until final gates. No release commit/tag/push/GitHub release is recorded. Factory Resolve tracks lifetime without binding later requests; supplied ActorContext remains required, and roles may be shared by players. Default shared world capture/storage remains an explicit host-integration preset limit.

Documentation milestone: fourteen scoped files are DraftComplete; scoped diff plus direct JSON/link/English/whitespace checks passed, with sixty relative links resolved. The preexisting PLAN prefix and pre-wave TODO priorities remain byte-identical, and only the new provisional blocks change the two changelogs. This closes documentation drafting, not implementation/runtime/release acceptance; root will attach final receipts.

## Source handoff and receipt reconciliation - 2026-10-25

Before planning: reconcile the frozen 7.54.0 source with actual result nodes while preserving human priorities and all historical PLAN bytes. Before delegation: all twelve source-owner handoffs and audit-writer/Mirror/benchmark handoffs are complete; all C# leases are released and only the exact documentation lease remains active. Before accepting: root reviews raw failures, exact environmental exclusions, final Unity/PlayMode results and platform limits; source completion is not 89 runtime-confirmed fixes.

Current actual receipts under `artifacts/fix-wave-20261025`: Core final5 2,853 rows (2,850 Passed / 0 Failed / 3 NotExecuted); Lua source-final 1,973 rows (1,932 Passed / 0 Failed / 41 NotExecuted); analyzer8 Passed; persistence Node nine PASS checks; SSE `ALL PASS`; 7.54.0 lockstep and index-based positive opt-in checks passed. Raw Unity final3 is 7,911 (7,873 Passed / 28 Failed / 10 Skipped), including GPU10/10 and NET001 offline17/17. Root inspected the scaled/baked nonuniform curved render pair. Later fixes require final4; deterministic PlayMode and final acceptance are pending.

Retain exactly two environmental exclusions: the explicit historical benchmark case missing six `BENCHMARK_20261015_224850_*.json` reports, and the FullAccess demo case throwing in the vendor TMP reflection initializer at `CommandInputField.cs:60`. Their raw failures remain visible; no data fabrication or exception suppression. Root reports an index-only Standalone opt-in correction reconstructed from the immutable baseline; temporary Editor vendor symbols and generated settings migrations will not ship. Local MiniCPM setup is separate from CoreAI feature/semantic acceptance.

Intake remains 91 raw / 89 canonical, two aliases, uncounted UH-01 and documented MOD-003. Current ownership, exact receipts/exclusions and remaining gates are in `dev-docs/fix-wave-2026-10-24/{README.md,PLAN.md,module-map.json,12-release-docs.md}`. No built-player/IL2CPP/browser durability/device/real-socket/load/performance/live-provider or publication claim is made. Root owns final source/docs acceptance and commit/tag/push/release evidence.

## Raw final4 and narrow follow-up - 2026-10-25

Direct final4 XML accounting is 7,911 = 7,896 Passed / 2 Failed / 13 Skipped, with the two exact external cases excluded; final4 remains a negative checkpoint. Mirror229/229 includes NET00117/17, and GPU projection10/10 passed. Root preserved independent logical cancellation scopes by adding an explicit actor/session stop in the facade fixture before its second enqueue; original first cancellation, second success and durable actor partition checks remain. The strict Hub initial-one subscriber assertion still fails with four listeners. Root reopened only the exact four-fixture/conditional HubSettingsPage lease recorded in `dev-docs/fix-wave-2026-10-24/17-hub-final-integration.md`, with C# writes frozen during root PlayMode. Listener identities remain under investigation; no baseline masking. Next integrated Unity/PlayMode and publication receipts remain pending.

## Raw PlayMode and configuration boundary - 2026-10-25

The saved fake-only PlayMode result is 146 = 143 Passed / 2 Failed / 1 Skipped, exit2: MCP12/12, Mods24/24, FastNoLlm103 Passed/2 Failed/1 Skipped, Benchmark4/4. Both failures are the named aggregate-demo and FullAccess-demo cases reaching the same third-party TMP constructor; root will exclude exactly those two cases and retain raw failures. Active WebGL target guards omitted benchmark harness/parallel fixtures, so four passed cases are not full benchmark acceptance. Root will switch to StandaloneWindows64 for the actual fake harness regression selection, excluding live benchmark/recapture. During a bounded pre-ADV negative/Hub diagnostic, root alone temporarily swaps two runtime files and restores exact bytes in finally; no worker derives final runtime hashes in that window. Next integrated Unity, Standalone PlayMode and publication receipts remain pending.

## Controlled ADV reproduction and Hub identities - 2026-10-25

The actual controlled pre-ADV run is 49 cases = 30 Passed / 19 Failed: ADV001 progress5 failed (four controls passed), ADV002 registry13 failed, and Hub diagnostic1 failed. The corresponding current-runtime final4 selected progress9/9 and registry13/13 pass by exact case-name matching. Root restored the only two temporarily swapped runtime files in finally, with red/green hashes and Restored=true recorded at 2026-10-25T17:04:00.2536955+05:00. Earlier compile-only red attempts remain invalid runtime evidence. The Hub diagnostic proves three destroyed CoreAiChatPanel.RefreshApiProfileControls targets plus the live page; the narrow owner is fixing manual test-panel lifecycle only. Next integrated Standalone Unity and fake PlayMode receipts remain pending.

## Windows EditMode selection passed - 2026-10-25

Direct `unity-editmode-final5.xml` accounting is 7,930 = 7,917 Passed / 0 Failed / 13 Skipped, exit 0 at 17:17:05+05:00. Assembly split is Core208 Passed/2 Skipped, MCP192 Passed, Mods3,264 Passed/5 Skipped, Mirror229 Passed and Source4,024 Passed/6 Skipped. The selected Windows run verifies the explicit actor-stop fixture, manual chat-panel OnDestroy cleanup, ADV001/002, GPU10 and NET00117; the two exact EditMode environmental exclusions and all earlier raw negatives remain. All C# is frozen. Windows fake-only PlayMode with guarded benchmark fixtures is running; live benchmark/recapture and two exact FullAccess demo cases are excluded. Publication remains unperformed. Root also moved the old provider repair-intake draft byte-identically into the repair folder; actual frozen audit layout is fifteen Markdown files with twelve matching module hashes and unchanged91/89 intake counts. Regenerated audit metadata does not revalidate repaired runtime source.

## Compiled Windows PlayMode exposed benchmark failures - 2026-10-25

Direct `unity-playmode-windows-final.xml` accounting is 389 = 382 Passed / 5 Failed / 2 Skipped, exit2: Benchmark249 =243 Passed/5 Failed/1 Skipped, MCP12 Passed, Mods24 Passed, FastNoLlm103 Passed/1 Skipped. All five benchmark regression cases remain in scope, with no new exclusion. Root assigned disjoint follow-ups for response-evidence isolation, replay/optional-artifact fallback and the G6 scene-depth positive control; other C# remains frozen. The earlier Windows EditMode pass remains a saved checkpoint. Affected new source needs fresh checks, and release/publication remain provisional/pending.

## Windows rerun and live-test classification correction - 2026-10-25

Raw Windows final2 is389 =386 Passed /1 Failed /2 Skipped; all five prior benchmark failures Passed. A different ParallelBenchmark provider-ignores-cancellation case reached its fifteen-second watchdog and remains in scope without timeout increase/suppression. Root extended that exact fixture lease for diagnosis. Both raw Windows selections were mixed, not hermetic: an unmarked SkillSetBenchmark live structural-cost test contacted local MiniCPM at port1234, with inspected last-call observations2,115/7,002 ms. These observations are not model-quality or live-provider acceptance. Root added Category Benchmark and Explicit/manual-only to that test; existing !Benchmark selection will exclude it. No new environmental exclusion was added and the remaining framework failure is retained. Final hermetic selection is expected388 cases, with actual passing counts pending.

## Final 7.54.0 available-gate acceptance - 2026-10-25

Root accepts all twelve source joins and available measured gates. Final hermetic Windows PlayMode is388 =386 Passed/0 Failed/2 batch EndOfFrame skips, exit0 at18:04:14+05:00: Benchmark247 Passed/1Skipped, MCP12 Passed, Mods24 Passed, FastNoLlm103 Passed/1Skipped. No live SkillSet case is present. Windows EditMode is7,930 =7,917 Passed/0 Failed/13Skipped; portable Core2,850 Passed/3NotExecuted, Lua1,932 Passed/41NotExecuted, analyzer8 Passed, Node/version/positive-opt-in passed. GPU10, Mirror229/NET00117 and G634 are included subsets.

The earlier five benchmark failures passed after two harness defects and replay/G6 fixture contracts were corrected without weakening grading, chunk evidence or negative controls. The earlier16.295306-second cancellation watchdog timeout remains unreproduced, not product-fixed: focused1.065373s and final full0.214230s passes are retained with phase observability. Earlier Windows389 selections were mixed because the unmarked local SkillSet test contacted MiniCPM; its structural last-call observations are not model-quality or broad live-provider acceptance. Root marked it Benchmark/Explicit before final hermetic selection.

Exact two EditMode and two PlayMode environmental exceptions, all raw negatives/invalid attempts, frozen audit15-file/12-hash layout and91/89 intake boundaries remain in the acceptance ledger. Built-player/IL2CPP/browser durability/device/real-socket/load/performance/live-quality gates remain unrun. This checkpoint supersedes earlier dated pending statements only as current status. Publication uses the Git tag and GitHub release https://github.com/NeoXider/CoreAI/releases/tag/v7.54.0; source/receipts bind to that commit/tag, with no self-referential SHA required inside the commit. All C# and the finalized documentation are frozen for root staging.

## Demo repair and feature integration checkpoint - 2026-10-26

Before planning: complete the owner-authorized repairs, reusable Gateway client, runtime code editor/interpreter port, compatible dependency/MCP updates and all-demo QA on demo. Preserve supplied actor/session isolation, ABI compatibility, bounded Lua execution, nonexecuting Python, owned cleanup and WebGL's single-threaded path. RedoSchool business/auth/lesson implementations, global tool upgrades and unrelated user assets are outside this change.

Before delegating: native gpt-6.1-sol workers use reasoning effort max, disjoint staging/file leases and their own Markdown reports. Root alone owns Unity, integration and Git. The owner explicitly authorized root to finish large changes when CLI/free-agent limits were exhausted. No commit may carry coauthor or AI attribution.

Milestone: all 29 Gateway candidates were integrated after live baseline/hash validation. The focused live portable selection passed 94 tests. The complete portable Core result in artifacts/server-gateway-20261026/core-gateway-full1-results/core-gateway-full1.trx contains 2,941 Passed, zero Failed and three NotExecuted entries (2,944 actual result rows). Source/Unity and final dependency gates remain separate.

Acceptance correction: the two recent attached-editor test jobs executed the older loaded test assembly. Root verified its MVID and IL string constants against the changed source and found the DLL timestamp predates the helper/diagnostic changes. Native typing passed in that loaded assembly; the saved paste failures do not validate the newest helper. Explicit AssetDatabase refresh and compilation are now requested before the next applicable test run.

The verified compatible UPM candidate was applied after SHA256 baseline checks, with original manifest and lockfile backups retained in artifacts/dependency-update-20261026/pre-integration. Unity package resolution/import, MCP 10.3.0 reconnect and fresh compiled-source test evidence are still pending. Managed patch candidates remain staged/unverified. The ready Redo editor source is being adapted in isolated staging; no candidate editor is claimed integrated or runtime-tested yet.

Before accepting: root will review actual feature diffs and independent audits, run fresh tests against the final imported tree and exercise all 17 published demos plus real agent-created/edited mods. The existing v7.54.0 tag is immutable. Demo push, main promotion and a new release remain unperformed and gated by the applicable QA.
## Editor integration and Lua examples checkpoint - 2026-10-26

The owner's latest correction cancels the new NPC-agent system and further subagent work. Root interrupted all three active workers and is finishing directly under the earlier explicit authorization. NPC candidates remain isolated in ignored artifacts and are not promoted into Assets or the published demo inventory. The existing 17 demos remain the manual acceptance scope.

Milestone: root promoted 32 reviewed editor/host-binding candidate files with per-file baseline/candidate hashes and backups in artifacts/editor-integration-20261026/receipt.json. Follow-up root fixes remove leftover Redo business dependencies and keep cancellation-aware coroutine helpers internal under distinct names, preserving existing Resume(default) source compatibility. The actual portable editor contract selection passed8/8 (editor-core3.trx); UI/real-VM budget gates remain pending.

Root added eight opt-in bundled Lua resources: orbit gallery, waypoint platform, tween gate, respawning coins, saved score, round clock, recycling conveyor and a frame-budgeted Lua maze solver. Actual-source real Lua/scheduler regressions passed13/13 in artifacts/editor-integration-20261026/samples-results3/additional-samples3.trx, including bounded/restarted maze search, coin deduplication/respawn, score reload, pause and round progression, no per-frame object growth and owned unload. The examples guide is Assets/CoreAIMods/Docs/LUA_MOD_EXAMPLES.md. Source tests do not claim rendering/input-device/performance or browser durability acceptance.

Dependency checkpoints after the actual managed restore are portable Core2941Passed/3NotExecuted and Lua1940Passed/41NotExecuted (actual TRX rows). Unity6000.6 imported MCP10.3.0 and NeoTools10.19.0; project HTTP MCP is reconnected to the uniquely identified CoreAI instance. Current GUI process17884 was launched through the desktop shell at2026-10-26T22:44:46+05:00; unrelated editor/build processes remain untouched.

Remaining acceptance: finish runtime budget and editor UI compile/regressions, reconcile original21 finding evidence and documentation, exercise every demo and real model mod create/edit, update release/version/changelogs and complete publication gates. Computer Use currently has no node_repl tool in this session; its required mouse/keyboard QA remains explicitly unverified. Demo push, main promotion and a new release remain unperformed.
## Fresh editor gate and added mod examples - 2026-10-27

Root's current CoreAI GUI process17884 completed a fresh source compilation; the native loaded CoreAI.Core assembly now contains CodeEditorDocument, unlike the stale earlier UI test jobs. Root fixed the copied TextElement call through its actual INotifyValueChanged<string> interface and restricted portable-only NUnit scheduling attributes from Unity's runner. New runtime source, not only generated project files, is imported.

Latest available focused checks: Core editor contracts10/10 Passed (editor-core4.trx), including whitespace preservation inside Lua literals and cross-mod result fencing; invocation guard/cancellation9/9 Passed (invocation-limits2.trx); actual bundled sample13/13 Passed (additional-samples3.trx). Full portable Lua after the interpreter changes passed1,962 with41NotExecuted actual result entries (lua-editor-full1.trx); CLI skip summary is not used for accounting. Its source precedes only later editor-core whitespace/result-fence and Unity NUnit-attribute compatibility changes.

The first invocation-budget probe found an actual typed-error metadata gap in scheduler coroutine trips. Root fixed CreateOwnTrip to keep the exact Lua error text/author line while attaching a typed step, timeout or memory cause. The original narrow assertion then passed with and without Rbx; it was not weakened to accept arbitrary text. Source compilation of Resume(default) remains unambiguous through internal, distinctly named resume helpers.

The new runtime editor/interpreter guide is Docs/CoreAI/CODE_EDITOR.md; the eight example guide is Assets/CoreAIMods/Docs/LUA_MOD_EXAMPLES.md. Real attached-panel/host-composition Unity tests are starting now; their outcomes, demo actions, Computer Use input, release/version and publication remain pending.
## Root completion status verified - 2026-10-27

CoreAI is not complete. The working tree is still on local demo at bc3fd74f with uncommitted changes. Remote main remains bc3fd74f; no remote demo head was returned. Package version remains7.54.0. No new release or promotion of this repair wave is accepted.

The latest focused Unity editor batch contains57 cases:56 Passed and1 Failed. The remaining failure is CodeEditorUiEditModeTests.DetachReattachAndDispose_InvalidatePendingDocumentWork (expected the Python draft plus p; actual #p). Receipt: artifacts/pr-review-20261025/unity-editor-source-batch1.xml.

Portable checkpoints are Core2951 Passed/3NotExecuted and Lua1962 Passed/41NotExecuted. Earlier Unity PlayMode has390 Passed/1Failed/2Skipped in unity-repair-play1.xml; the isolated console follow-up passed2/2. A complete green PlayMode rerun after the editor integration is not present.

The17-scene manual acceptance and actual model-authored Lua creation/editing remain unverified. A complete PlayMode pass against real models on the current final source is not recorded. Source/portable results and scene-load smoke tests do not establish live-model interaction acceptance.

## Resumed completion wave - 2026-10-27

Goal: finish the current CoreAI repair/features on demo, verify all published demos and actual model-authored Lua create/edit, close confirmed review findings with regressions, update user/developer docs and versions, push demo and promote main only after the applicable gates pass. Reddit work is outside this wave. No new NPC-agent system is added.

Requirements: engine-free portable core, replaceable host seams, scoped identity and world ownership, source preservation, bounded Lua and UI work, exception/cancellation stability, exact persistence receipts and truthful test evidence. No skipped/offline test counts as a real-model or manual-input pass. Preserve unrelated files and user settings/data.

Delegation: coreai_editor_finish owns CodeEditorView and two native typing fixtures; coreai_runtime_finish owns the active-world budget facade plus real editor/runtime tests; coreai_live_qa_finish owns new live-demo acceptance fixtures only. All are native gpt-6.1-sol with max reasoning. Root owns settings, scene wiring, Unity/batch execution, final review, Git and publication. Independent CLI audit route is being checked through the explicit free OpenCode preference.

Before acceptance: read each actual diff, freeze source before running Unity, require exact XML/TRX/provider receipts and close failures without weakening assertions. Original audit reports remain frozen; the21-finding ledger and TODO must be reconciled against actual evidence. No new release or main promotion is claimed while any required gate is unverified.

Provider steering: the owner now requires OpenRouter for all real API PlayMode tests and forbids local-model testing. No local inference request was sent. Root found an existing OpenRouter credential in the local credential store without logging/copying its value. All new live fixtures require explicit OpenRouter test environment and reject local/native/offline fallbacks. Earlier MiniCPM load was setup only and is not test evidence.

Native runtime worker completed the active-world invocation-policy fix and27 regression cases (18 adapter,9 replacement-world). Native editor worker completed the native selection and stale completion-pair fixes; root is reviewing source before graphics-capable batch. Core portable rerun exposed an existing parked-reader allocation guard failure (305B/chunk vs192); a focused repro and separate Core performance investigation are active, with no threshold relaxation authorized.

Source-final and positive checkpoints: the full Core portable Release run passed2954 with3NotExecuted (actual TRX rows). Queue/hot-path regression selection passed111/111 in Debug and111/111 in Release after a negative baseline that measured208B/write Debug and64B/write Release. The old192B end-to-end bound was not relaxed; new direct-write0B assertions remain.

Root wired the existing ModdableUnits scene by verified serialized fileIDs: scope.gameplayBindingsProviders includes its existing controller, and controller.modsScope references the existing Lua scope. No new NPC system or generated scene builder was added. Qwen readiness is provider-aware and the pre-container world-store seam is in place. First Unity focused batch aborted compilation on3 new test namespace references; the author corrected them and the second batch is now running. Root owns every Editor process and no local inference is permitted.

Free OpenCode DeepSeek route passed a chat probe and read/wrote an initial audit outline, but the real worker hit a TLS certificate failure and stalled; the exact task was stopped before transferring documentation ownership to native reviewers. Its generic artifact delta included other workers' files and is not evidence of authorship. Native independent audits now cover all7 changed modules; confirmed additional editor/host findings are being staged while Unity source stays frozen.

Further native and API checkpoints: unity-completion-focused2.xml contains99 cases92Passed/3Failed/4Skipped. All27 new VM/adapter cases,13 bindings and10 Qwen readiness cases pass; failures remain in native editor selection/closer/Tab behavior. Native authors prepared separate patches; root merged12 candidate files into C:/AI/work/coreai-completion-20261027/combined-editor-candidates with original/candidate SHA validation and no merge conflict. No candidate is promoted before independent final review.

Original-scene OpenRouter hub1 aborted before a model request: the supposed pre-Awake PostProcessScene hook actually found built containers, threw in SetAgentMemoryPersistenceMode, and left optional Mirror sceneId compatibility unprocessed. This is a negative isolation/setup receipt, not API acceptance. The author is correcting isolation through public VContainer builder-slot replacement before service resolution; root approved the existing optional endpointRegistryStore parameter's DI fallback. No user store reset or duplicate backing-store registration is accepted. A separate existing direct Meai HTTP PlayMode case is now running against OpenRouter while isolation fixes remain staged.

Actual API PlayMode positive: unity-openrouter-direct1.xml passed the real MeaiLlmClient_CreateHttp_ShouldCreateAndConnect case, exit0; the Unity log records POST https://openrouter.ai/api/v1/chat/completions under the process-only OpenRouter/deepseek environment. This is one real HTTP client path, not seventeen-scene, tool-authoring, manual input, player or performance acceptance. No local inference was used. The scene isolation author has staged the safe public VContainer backing-factory replacement and optional endpoint-store DI expression for independent/root review before the next scene run.

The corrected storage-registration regression passed in actual Unity PlayMode: unity-live-storage1.xml1/1, exit0. It verifies exact public VContainer slot replacement preserves interfaces/lifetime/singleton and never invokes the default storage factory. Original-scene Hub OpenRouter acceptance is retried next with the promoted safe isolation helper and generic optional endpoint-registry store seam. Independent native editor review additionally confirmed tracked quote type-over inside strings is broken in the frozen baseline/candidate; a narrow quote/escape regression correction is being prepared in the merged candidate directory before promotion.

Computer Use availability was rechecked: the installed skill requires node_repl for all Windows actions; that tool is absent from the current catalog. Plugin discovery returned no relevant Computer Use integration. No custom terminal/UI-Automation replacement was attempted. This remains the previously deferred manual-input gate while all available checks continue.

Full original-scene OpenRouter first matrix: unity-openrouter-demos1.xml18=11Passed/5Failed/2Skipped, exit2. Positive cases include live Hub, battle override, wave observer, Forge combat, actor-isolated chat, authority grant/revoke, HTTP-backed QwenGenie/Spellcraft realtools, Skills crafting/attack, WorldCommands and standalonechat. The five failures include initial source/state paths expanded to278 Windows characters by redundant GUID store namespaces, so the real path guard correctly rejects them; the author is fixing the test storage layout without weakening production path safety. FullAccess self-test failed store_roundtrip and GameplayServices had no tour world after the same failed source load. Model-authored code did reach manage_mods but failed admission, so create/edit is NOT accepted yet.

Root found the separately installed Codex CLI advertises computer_use=true. A strictly read-only capability worker coreai-computer-capability-20261027 (gpt-6.1-sol/max through wrapper, session53628) is checking only whether its required official node_repl/@oai/sky tool can import/list apps; no UI control is authorized until root grants an exclusive target lease. Feature advertisement is not proof of actual capability.

Live failure diagnosis was verified: the custom QA root plus redundant storeId GUID hashing expanded a Windows source path to278 characters, above the existing259 guard. Only test replacement constructors were corrected to use the already GUID-owned root directly; production path guards and normal missing-root semantics are unchanged. The helper now performs real source admission and owned-id key/value roundtrip before paid requests. A focused OpenRouter rerun of the5 affected cases is running (unity-openrouter-repair1).

Codex CLI's advertised Computer Use feature was not actual tool availability: coreai-computer-capability-20261027 completed with callable-tool matches[] and no official node_repl. No UI fallback or custom protocol was attempted. C:/AI/work/coreai-completion-20261027/COMPUTER_CAPABILITY.md contains the exact read-only result. The requested Windows Computer Use gate remains deferred/unverified.

## Final demo publication checkpoint - 2026-10-27

Data-safety incident: the live test helper omitted WorldStateManager isolation. Its 60-second
autosave overwrote the pre-existing default `CoreAI/WorldState/world_state.json` at 10:42 with
an empty QwenSpellcraft snapshot. Previous bytes have not been recovered; the observed current
file was copied to task scratch. No more live Unity runs are authorized until the actual manager
is isolated before Initialize and other active file-backed stores are checked.

Root bumped all seven package manifests, internal pins, McpServerInfo and versioned guide lines to7.55.0 through tools/bump_version.py; lockstep check passed. This is a demo candidate, not a published Git tag or main promotion. Final scope remains editor/Hub native input, actual model create/edit and the Demo05 mod-manager repaint, followed by complete native suites and demo push. Core-final2 has2954Passed/3NotExecuted/0Failed; focused3 has145Passed/5Failed/4Skipped. The source authors are correcting the five native failures and two remaining actual OpenRouter scenarios without weakening assertions. Computer Use, MiniRpg native movement and qualitative material visuals remain separately deferred gates.

The first current full five-assembly EditMode run completed 8,268 cases: 8,239 Passed, 14 Failed and 15 Skipped. All four grouped Hub tests and both typing tests passed. Two UI expectations incorrectly required four spaces at every column; independent literal expectations now pin the four-column policy. Ten older Hub save cases require an explicitly budgeted service fake. The remaining guards require excluding the .NET HttpClient transport from WebGL, using the adapter's synchronous Lua entry point in the Hub, and detaching/restoring the test synchronization context. Authors preserve all previous behavioral assertions; fresh full execution remains required.

The repeated EditMode gate completed 8,273 cases: 8,258 Passed, zero Failed and 15 Skipped. The
latest real OpenRouter matrix has 13 Passed, three Failed and two Skipped; its three failures and
the subsequent test-only corrections are recorded in `OPENROUTER_TRIAGE.md` and are not yet retested.
Independent review approved 48 formatting-only candidates; after promotion the portable Core and Lua
suites passed 2,954/3 NotExecuted and 1,962/41 NotExecuted respectively. The earlier native run
precedes those formatting bytes. The newly added explicit world-state path and test isolation have
not yet passed their focused Unity gate. Main promotion, a tag and a public release remain pending.

World-state manager path isolation subsequently passed seven of seven focused Unity cases after a
path-separator assertion correction. The test fixture now requires the world-state and audit slots
to be redirected before construction; its live-scene composition and corrected OpenRouter cases
remain unverified. The previous global snapshot was not recovered. These remain demo-branch gates.

Root exported the staged candidate to a clean tree. The unchanged positive-module and seven-package version checks both passed; the raw dirty-checkout scan failure remains separate. The staged credential-pattern scan found zero matching paths across 411 added/modified files. An empty orphaned Git index lock from 2026-10-26 was verified against the absence of Git processes and moved to task scratch without changing the index. No commit, push, main promotion or release is established by these checks.
