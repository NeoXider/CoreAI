# CoreAI PR-style review inventory - 2026-10-25

Immutable review range: `69d5bcc37502cd4bb464a3bf9ac0c53b1624b804..bc3fd74f0a889bfe1b1e7babc6bc02ed48a7c05e`. All repair work and publication belong to `demo`.

The owner explicitly requested retained per-auditor Markdown reports for this review. These files are the scoped exception to the repository's usual rule against retained audit reports. Historical reports inside the immutable diff are input material; their claims must be revalidated against the changed code and contracts.

[coverage.json](coverage.json) is the authoritative exact-path inventory. It assigns all 309 changed files to one primary reviewer each and records kinds, added/deleted lines, reviewer, model, effort and review status. This is file ownership coverage. It does not claim semantic coverage, passing tests, runtime acceptance or completed reviews.

| Group | Primary scope | Files | Report | Dispatch |
| --- | --- | ---: | --- | --- |
| 01 | Execution, actors and orchestration | 16 | `01-execution.md` | launched |
| 02 | Memory, tools and skills | 35 | `02-memory-skills.md` | launched |
| 03 | Providers, HTTP, SSE and endpoints | 26 | `03-providers.md` | planned |
| 04 | Unity host, facade, chat, world and DI | 36 | `04-unity-host.md` | planned |
| 05 | Audit and bounded log retention | 14 | `05-audit-log.md` | planned |
| 06 | Mods, source, strict execution and stores | 33 | `06-mods.md` | planned |
| 07 | Rbx API, material lifetimes and HTTP world | 28 | `07-rbx-api.md` | planned |
| 08 | Luau downlevel and iterator seams | 12 | `08-luau.md` | planned |
| 09 | Hub and UI | 25 | `09-hub.md` | planned |
| 10 | MCP | 13 | `10-mcp.md` | planned |
| 11 | Mirror | 9 | `11-mirror.md` | planned |
| 12 | Benchmark | 24 | `12-benchmark.md` | planned |
| 13 | Crafting and procedural projection | 9 | `13-crafting-projection.md` | planned |
| 14 | Documentation, packages and test wiring | 29 | `14-docs-packages.md` | planned |

Every reviewer owns the exact `groups[].paths` array and writes only its named report during the independent review. The root owns production fixes, regression execution, final reconciliation and Git. A module report is pending until that auditor creates it; report names in the table are planned destinations, not completed-report links.

The seven package manifests, aggregate release/progress documents, ProjectSettings change and portable test wiring have primary owner 14. Domain documentation, historical module reports, source tests and new Unity metadata follow their domain owners. The 42 changed files under `dev-docs/` are explicitly included in the JSON inventory.

Read [PLAN.md](PLAN.md) for milestones, [FILE_LEASES.md](FILE_LEASES.md) for ownership rules, and [00-coverage.md](00-coverage.md) for the coverage auditor's verification and limits. Cross-domain contextual reads are listed in `crossDomainReadSeams`; they do not create second primary owners.


---

## 2026-10-26 continuation

21 accepted findings remain open (`PR-EXEC-001`, `PR-MEM-001/002`, `PR-LLM-001`, `PR-HOST-001/002`, `PR-AUD-001/002`, `PR-MODS-001/002/003`, `PR-RBX-001`, `PR-08-001/002`, `PR-09-001/002/003`, `PR-MCP-001`, `PR-NET-001`, `PR-BENCH-001`, `PR-14-001`). Code repairs started but none accepted green. Expanded demo acceptance gate supersedes prior demo/vendor exclusion: manual PlayMode all demo scenes, agent-driven mod create/edit, integrated regression receipt green, then demo push, then main promotion. Published `v7.54.0` remains immutable. Tracker model `opencode/beam-free`, effort `max` (effective model-side effort unknown).


## 2026-10-26 publication gate correction

Current gate (supersedes older demo-only wording; old wording retained as history): demo-first, then main after demo QA. Push demo only after verified fixes and documentation; promote to main only after all demo scenes are checked and fixed and demo QA passes. Preserve v7.54.0; no tag or release mutation. Use ASCII hyphens in tracking text.


## 2026-10-27 repair reconciliation

The 21 original finding IDs remain intact. Source repairs are implemented; 20 findings have named
passing saved regressions and PR-14-001 has corrected documentation with two compile examples.
No direct runtime row is invented for the docs-only finding. Final root acceptance remains pending.
The older continuation's "not started / none green" wording is historical, not current repair state.

[FIXES.json](FIXES.json) now records exact test methods, parameterized case counts, receipt hashes,
source hashes and explicit scope limits. [39-receipt-reconciliation.md](39-receipt-reconciliation.md)
explains the map, negative receipts, dependency restore readback and open gates. Source review and
saved focused passes do not establish current integrated Unity green, manual demo/input/visual QA,
live provider behavior or publication. All newly authorized real API PlayMode checks use OpenRouter.

The [current PR audit](../completion-2026-10-27/PR_AUDIT.md) covers the working-tree changes after
7.54.0, including Gateway Client, the actual editor port, invocation budgets, bundled examples,
host bindings and compatible dependency updates. It is separate from the frozen original reports.


Later current-source status is in [FINAL_VERIFICATION.md](../completion-2026-10-27/FINAL_VERIFICATION.md):
7.55.0 is a demo candidate only; five additional source fixes are peer-reviewed/promoted, while
focused3 remains145P5F4Skipped and real demo/model-authoring selections retain failures/partial rows.
Original21 IDs, initial red history and the published 7.54.0 baseline remain separate.


FullEditMode1 is 8268 rows/8239P14F15Skipped; all 19 original EditMode finding-method sets pass within
it, while Benchmark is PlayMode-only and PR-14 docs-only. Seven additional source findings now have
reviewed fixes; source guard/fixture compatibility corrections are tracked separately. Current
full2/API/hermetic results and root acceptance remain pending in FINAL_VERIFICATION.md.


Current full2 is 8273 rows/8258 Passed/15 Skipped/0 Failed, exit 0. All original 19 EditMode mapped
finding sets and current editor/Hub/save/runtime/guard fixtures pass; Benchmark is PlayMode-only
and PR-14 docs-only. Current actual API/hermetic/export and explicit root acceptance remain pending.
