# Final verification record — CoreAI candidate 7.55.0

**Data-safety incident:** The earlier live-scene helper failed to isolate the default
`CoreAI/WorldState/world_state.json`. The 2026-10-27 OpenRouter run auto-saved an empty
QwenSpellcraft state over that pre-existing file. Its previous bytes have not been recovered;
the observed post-write file is preserved in task-local scratch. Do not rerun the live fixtures
until world-state isolation is verified. Earlier claims that every file store was isolated apply
only to the explicitly replaced registrations and did not cover this world-state file.

The latest full OpenRouter selection has 13 Passed, 3 Failed and 2 Skipped out of 18; it is not
an acceptance pass. Failures are being investigated separately from the 8,258 passing EditMode cases.

The new world-state manager isolation fixture passed 7/7 after correcting a path-separator-only
assertion. Live-scene storage composition, the three corrected OpenRouter cases, hermetic PlayMode,
manual input and visual acceptance have not yet been rerun. Portable Core and Lua passed after the
reviewed formatting changes: 2,954 Passed / 3 NotExecuted and 1,962 Passed / 41 NotExecuted.

**Status: INCOMPLETE. Full EditMode2 has zero failures with 15 explicit skips. Final API/hermetic
results and explicit root acceptance remain pending. This is not a final PASS or published release.**

Updated 2026-10-27 by the documentation reviewer (native gpt-6.1-sol, reasoning effort max).
Root owns tests/builds, Unity/providers, settings/version/changelog/PLAN, staging and Git/publication.
This reviewer parses saved evidence and changes only assigned docs/ledger files. No runtime or Git
operation is performed here. The earlier draft freeze was reopened for this bounded reconciliation.

## Candidate and source identity

- Workspace `D:/Git/CoreAI`, branch reported by root `demo`; published review baseline 7.54.0 at
  `bc3fd74f0a889bfe1b1e7babc6bc02ed48a7c05e` remains immutable.
- Root's required bump script set seven package manifests/internal pins, MCP server info and guide/
  roadmap version references to **7.55.0 candidate**. No commit/push, main promotion, new tag or
  release is established by the version bump or staging.
- Intended scope is portable Gateway Client/thin Unity composition, actual Lua editor port with
  Python execution Unsupported, initial invocation limits, eight opt-in samples, optional gameplay
  providers and compatible dependencies. NPC/navigation and ignored staging are excluded.
- Initial peer-approved twelve-file promotion manifest was
  `0562cb77c0e29c90f097785cdf841c29d129af134750e8dd3bb6c4894e1f12a1`.
  Later reviewed corrections supersede some of those bytes. [FINAL_SOURCE_REVIEW.md](FINAL_SOURCE_REVIEW.md)
  approves the final six-file source delta plus the seven-file guard/fixture correction wave;
  last-entry hashes for its twelve distinct paths independently match current files. This is
  source approval, not executable acceptance. [CANDIDATE_REVIEW.md](CANDIDATE_REVIEW.md) retains all
  rejected/intermediate candidate history.

## Findings: original 21 and seven additional source fixes

[FIXES.json](../pr-review-2026-10-25/FIXES.json) preserves all 21 original IDs, source anchors,
initial red aliases and 36-case/28 Failed/8 Passed history. Twenty source findings map to 48 exact
saved passing methods; PR-14-001 has corrected docs and two matching compile examples, no direct
runtime row. In full1, every mapped method for all 19 EditMode findings is present and Passed.
PR-BENCH-001's two methods are PlayMode-only and therefore outside full1; their earlier passing
receipt remains scoped evidence. Final root acceptance fields remain pending.

Seven additional confirmed source findings have independently reviewed fixes:

| ID | Corrected behavior | Executable scope at this snapshot |
| --- | --- | --- |
| `UNITY-REVIEW-001` P2 | Isolate throwing activation cancellation and settle unrelated owned releases outside lock. | Named registry regression Passed; broader final acceptance pending. |
| `HUB-REVIEW-001` P2 | Delegate Escape through the active grouped Mods/editor route before collapse. | Actual grouped-Hub/language fixture 4/4 Passed in full1. |
| `EDITOR-REVIEW-001` P2 | Retain full Lua lexical context for Enter inside multiline literals/comments. | Pure policy and attached native Enter controls Passed in full1. |
| `EDITOR-REVIEW-002` P3 | Preserve Lua insertion context after `#` without changing Python comments. | Native Lua/Python control Passed in full1. |
| `CANDIDATE-REVIEW-001` P2 | Distinguish tracked quote closers/escapes/literals and verified insertion ranges. Present in frozen source, not merge-introduced. | Quote/tracker/native controls Passed in full1. |
| `EDITOR-REVIEW-003` P2 | Reject stale completion without swallowing ordinary Tab/Enter. | UI 20/20 Passed in full2 after independent literal Tab expectations were corrected; final root acceptance separate. |
| `DEMO-REVIEW-001` P2 | Repaint real manager rows after status/cache/source-event changes. | Source approved; current actual Demo05/OpenRouter rerun pending. |

These seven are counted once. Guard compatibility and invalid fixture expectations below are not
invented additional current runtime findings. [PR_AUDIT.md](PR_AUDIT.md) joins the module reports;
[39-receipt-reconciliation.md](../pr-review-2026-10-25/39-receipt-reconciliation.md) retains evidence limits.

## Current saved execution snapshots

Actual XML/TRX case rows are used; skips/not-executed rows remain exclusions. No sum of scoped
reruns is presented as a new all-green containing suite.

| Receipt | Actual rows/outcomes | Scope |
| --- | --- | --- |
| `artifacts/completion-20261027/core-final/core-final3.trx` | 2,954 Passed / 3 NotExecuted / 0 Failed | Latest Core run after the correction wave; not Unity/player proof. |
| `artifacts/completion-20261027/lua-final/lua-final2.trx` | 1,962 Passed / 41 NotExecuted / 0 Failed | Saved portable Lua snapshot; native Hub/guard corrections have their own gate. |
| `artifacts/completion-20261027/analyzers-final/analyzers-final1.trx` | 8 Passed | Analyzer regression selection. |
| `artifacts/pr-review-20261025/unity-completion-edit-final1.xml` | 8,268 = 8,239 Passed / 14 Failed / 15 Skipped | Historical failed first full gate; never relabeled. |
| `artifacts/pr-review-20261025/unity-completion-edit-final2.xml` | 8,273 = 8,258 Passed / 15 Skipped / 0 Failed; exit 0 | Current native EditMode selection; exact exclusions below. |
| `artifacts/pr-review-20261025/unity-openrouter-direct1.xml` | 1 Passed | Actual Meai HTTP call in Unity through OpenRouter. |
| `artifacts/pr-review-20261025/unity-live-storage1.xml` | 1 Passed | Genuine file-store/singleton registration and isolation. |
| `artifacts/pr-review-20261025/unity-openrouter-hub2.xml` | 1 Passed | Actual Hub scene live Chat/Statistics/Mods flow. |
| `artifacts/pr-review-20261025/unity-openrouter-demos1.xml` | 18 = 11 Passed / 5 Failed / 2 Skipped | Initial actual demo/model-authoring matrix. |
| `artifacts/pr-review-20261025/unity-openrouter-repair1.xml` | 5 = 3 Passed / 2 Failed | Scoped repair; model create/edit and actual manager-row lookup still failed in this saved run. |

Core focused allocation/backpressure receipts remain 111 Passed each in Debug/Release. They are
scoped portable measurements, not current device/room/throughput acceptance. Earlier focused3
145P/5F/4Skipped, focused2 92P/3F/4Skipped, source57 56P/1F, PlayMode393 390P/1F/2Skipped and Core
2,951P/1F/3NotExecuted remain negative historical receipts. Full1 never becomes green retroactively.

## Full1 failure adjudication and verified full2 correction gate

The actual editor UI fixture has 20 rows: 18 Passed / 2 Failed; typing has 2 Passed; grouped-Hub/
language has 4 Passed. The 14 full-suite failures are fully explained in the saved failures JSON:

- Two UI tests expected a fixed four spaces. Unchanged native Tab advances to the next four-column
  stop: independently specified expectations are four spaces at column 0, one at 11 and three at 5,
  with exact source/caret/anchor assertions. Production Tab was not changed to fit the tests.
- Ten positive Hub Save cases used a legacy `IHubModService` fake without the deliberately required
  budget capability. Positive fixtures now explicitly implement the budgeted port with limits,
  cancellation, scope/disposal counts and preserved assertions; a real unsupported legacy-service
  negative remains. This is fixture compatibility, not ten current runtime defects.
- Two guards identified WebGL-reachable HttpClient primitives and a synchronous test unwrap without
  a local context detach. The entire .NET HttpClient service transport is now excluded from a
  WebGL player. Concrete Lua `Run` provides the direct caller-thread operation; Hub calls it rather
  than unwrapping `RunAsync`. The completed-task compatibility signature, budgets, cancellation,
  source/receipt fences and ownership remain. Core test context is detached/restored locally.

[WEBGL_GUARDS.md](WEBGL_GUARDS.md), [HUB_SAVE_GUARDS.md](HUB_SAVE_GUARDS.md) and the independent
correction addendum in FINAL_SOURCE_REVIEW.md pin the exact hashes. No allowlist, threshold or
assertion was weakened. Full2 now verifies the correction fixtures with zero failures; real API,
hermetic, manual and final root acceptance remain separate.

## Current full2 fixtures and exclusions

Actual full2 rows: UI 20/20 Passed; typing 2/2; grouped-Hub/language 4/4; Hub Save 22/22;
production-VM adapter 22/22; local-context guard 3/3; WebGL primitive guard 10/10; Gateway composition 28/28.
All mapped methods for 19 original EditMode finding sets are present and Passed. PR-BENCH-001 is
PlayMode-only and PR-14-001 docs-only; neither receives an invented EditMode runtime pass.

The 15 explicit skips consist of 10 allocation-accounting cases (native runtime cannot measure
per-thread allocations or delegates accounting to portable tests), 3 legacy local 4B explicit checks
excluded under the current OpenRouter-only policy, and 2 optional Roblox-docs-mirror transcription
checks without that local mirror. None is converted to Passed or used to request local inference.
NUnit root status is `Skipped:Ignored`; the saved Unity process exit is 0. Root must still review
logs and final acceptance; zero failing test rows do not prove a warning-free Editor or built player.

## Actual API/demo scope

Every actual API PlayMode call uses **OpenRouter**. Credentials remain private process state;
no key is copied into this report/assets. No local inference/fallback is acceptance evidence.

FullAccess, GameplayServices and LuaMods pass their later repair1 rows after redundant QA namespace
removal; production Windows path guards were preserved. Hub, LiveMechanics, WaveAutoBattler,
ModdableUnits, MultiplayerFoundation, OnlineAuthority, QwenGenie, QwenSpellcraft, Skills,
WorldCommands and StandaloneChat have scoped actual scene/action/provider passes. Qwen and Skills
include real model tool effects. Multiplayer/authority proof is single-process, not live network/load.

Remaining saved negatives are model-authored create/edit discovery and ModsChat owned manager-row
lookup. Both now have source-approved runtime/fixture corrections and await root's new actual API
receipt. The fixture still requires two real Programmer turns, successful own-id load/get_source/
reload, changed saved source, real VM/world effects and retained versions; no canned implementation
is passed as model output. The real manager row/buttons remain the acceptance path.

Isolation uses public VContainer exact registration-slot factories before Build/resolve, genuine
production files in an owned GUID root and unchanged persistence modes. The rejected pre-Awake
plan and path-too-long QA namespace are historical. [LIVE_QA.md](LIVE_QA.md) owns the detailed
scene/helper contract and per-scene latest receipts.

MiniRpg native movement and ProceduralMaterials qualitative visuals remain user-deferred partial/
NUnit Ignore rows. Computer Use is unavailable in both required hosts (capability report
`C:/AI/work/coreai-completion-20261027/COMPUTER_CAPABILITY.md`); no UI-helper workaround was used.
Editor Computer Use is unperformed. Automated callbacks are not native mouse or manual QA proof.

## Tooling, export, dependency and documentation checks

- `benchmark-chart-final1.log`: six Python tests, OK; root reports exit 0.
- `fetch-sse-node-final1.log`: ALL PASS; `persist-node-final1.log`: all nine checks PASS; root reports
  both Node exits 0. Source harnesses, not browser/player acceptance.
- `positive-module-working1.log` retains the raw failure on non-UTF8 ignored Mirror scratch.
  `positive-module-clean1.log` is PASS on root's curated staged export1, index 833eafd..., before the
  latest correction wave. `version-clean1.log` confirms seven packages/MCP const 7.55.0 there.
  A final export after corrections still needs its own root check; tool/exclusions were unchanged.
- `staged-secrets-final1.json`: 411 paths scanned, zero credential-pattern paths for that staged
  snapshot. It does not certify later additions automatically.
- `console-example-final1.log` retains a failed `--no-restore` attempt using stale MEAI 10.9 assets.
  Normal restore/build in `console-example-final2.log` succeeds with zero warnings/errors; root
  reports exit 0. No product source was changed to accommodate that environment failure.
- Root excludes five generated ProjectSettings files and unrelated ETFX metadata from staging;
  restoration/backups/intended-file review remain root-owned. No Git commit/push is claimed.
- Current package/managed readback: MCP 10.3.0, NeoTools 10.19.0, TimeProvider 8.0.1, Numerics/Tensors 9.0.22;
  retained MEAI 9.10.2/STJ 8.0.6 ceilings; portable Newtonsoft 13.0.4. Restored DLL hashes match receipts.
  DOTween changes are importer metadata. Historical version references are labeled historical.
- Assigned usage/indexes now document the concrete synchronous Lua Run/completed-task facade and
  actual WebGL transport availability, with no internal repair receipts in user flows. Original
  IDs/method maps, hashes/counts, both compile examples, links and English/whitespace are checked.

Full artifact paths and hashes are in FIXES.json under `artifacts/completion-20261027/` unless shown
otherwise. Saved compilation, Node, scan and source results are separate from actual runtime gates.

## Remaining root acceptance checklist

1. FullEditMode2 counts, all named correction fixtures and original EditMode mappings are now
   reconciled. Root still reviews logs, 15 exclusions and frozen source identity before its explicit
   final acceptance. No skipped row or authored test is relabeled as a pass.
2. Obtain current actual OpenRouter results for the two repaired live methods, then reconcile the
   complete intended demo/authoring selection while preserving the two deferred manual rows.
3. Obtain the source-final hermetic PlayMode gate and any required affected regressions. Core3 is
   already positive; rerun portable legs only if later relevant source/dependency changes justify it.
4. Re-export intended staged files/settings after all corrections/docs and rerun unchanged positive
   opt-in/version/secret/package checks. Preserve raw dirty and older clean-export snapshots.
5. Root explicitly adjudicates original 21 and additional 7, final diffs/docs, guards/settings/secret
   checks and publication readiness. Until that decision, all root-acceptance fields remain pending.
6. Root owns fresh final Git/commit/push, demo-first publication and any later main/release action.
   Required native/UI/visual gates and documented manual deferrals remain explicit; no full product
   completion, player/browser/device/real-network/capacity/performance or new release is claimed.


## Current draft freeze awaiting live receipts

Assigned relative links and whitespace validated after the full2 reconciliation. Full EditMode2
remains 8,273 rows/8,258 Passed/0 Failed/15 Skipped, exit 0. The complete OpenRouter demo/authoring
session is root-reported as running; no result or final root acceptance is inferred before its
actual XML is supplied. Source formatting candidates are outside the repository and are not
claimed installed. Source, native, real API, manual, network/player and publication gates remain
separate. This documentation draft is frozen pending root's bounded receipt handoff.
