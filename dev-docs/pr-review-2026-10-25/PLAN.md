# CoreAI PR-style review and repair - 2026-10-25

Goal: independently review every change in 69d5bcc37502cd4bb464a3bf9ac0c53b1624b804..bc3fd74f0a889bfe1b1e7babc6bc02ed48a7c05e, fix confirmed regressions, update documentation and push only the demo branch.
Branch: demo was absent locally/remotely and was created at current origin/main bc3fd74f; published main and v7.54.0 remain the immutable review head.
Invariants: supplied actor/session isolation, explicit host-owned authentication/lifetimes, finality, bounded resources, cancellation/reentrancy/order, corruption preservation, truthful persistence/evidence and replaceable optional modules.
Scope: all 309 changed files, including seven packages, tests, manifests, documentation and the narrow ProjectSettings opt-in correction. New game features, vendor/demo repair and local LM Studio configuration are excluded.
Before delegation: partition exact changed paths without gaps/overlaps; each independent reviewer writes its own Markdown with actionable PR findings, causal changed lines, a concrete trigger and a regression plan. Reviewers do not edit production, build/test, use Unity/MCP or mutate Git.
Before accepting: root revalidates each finding against source/contracts, rejects host-game policy assumptions, reads every diff, reproduces accepted failures and runs meaningful regressions and integrated checks. Prior green receipts do not replace code review.
Publication: Neoxider <xotrios@gmail.com>, no co-author or AI trailers; push demo after verified fixes/docs. Do not move or replace the published v7.54.0 tag.
Status: refreshed remotes; starting coverage inventory and independent audits. Preserve unrelated ETFX metadata and identical-content Unity settings stat-cache noise.

Milestone 1 - immutable changed-file inventory frozen: all 309 unique paths in the pinned range have one primary owner across 14 disjoint groups. coverage.json records exact path arrays, kinds, statuses, churn, report destinations, reviewer, model and effort; source tests, domain records and companion metadata follow their domain owners. All 42 changed dev-docs records are included.
Before accepting the inventory: compare the saved JSON against independently re-read name/status and numstat diffs, assert unique ownership and matching metadata companions, and check planning links and whitespace. File ownership does not establish source-review completeness or executable acceptance.
Dispatch milestone: root confirmed native reviewers 01 and 02 on gpt-6.1-sol with max effort. Groups 03-14 use the root-selected Codex CLI settings and remain planned until process launch is confirmed. Their exact arrays and report names are ready for dispatch.
Next milestone: each independent module reviewer produces its own report; the root revalidates actionable claims against the immutable changed lines and governing contracts before implementing, testing, documenting and publishing confirmed fixes on demo.
Milestone 1 validation complete: independently re-read name/status and numstat diffs equal both JSON path sets exactly (309/309, 14 groups, zero gaps or duplicate primary owners); all 24 metadata companions, per-file/group churn and statuses match. Five existing planning links resolve; English/newline/whitespace checks pass. No builds, tests, provider calls, Unity/MCP or Git mutations were performed by the coverage auditor.

## Dispatch milestone

All fourteen review groups are dispatched with disjoint report ownership: native GPT-6.1 Sol/max for 01-05, and nine neoxider Codex CLI GPT-6.1 Sol/max tasks for 06-14. The free DeepSeek/OpenCode tool probe timed out at 180 seconds without a file; this is route failure evidence, not quota evidence. All 309 original changed paths remain assigned exactly once. Production and tests remain unchanged while the independent audits run.


---

## 2026-10-26 continuation (demo acceptance gate)

Supersedes: prior PLAN scope exclusion of demo/vendor repairs is superseded by the 2026-10-26 user instruction (fix all demo scenes; manual PlayMode and agent-driven mod create/edit must be exercised; push demo; only then promote to main).

### Before planning
- Goal: close all 21 accepted findings and the expanded demo acceptance gate on `demo` only; preserve `v7.54.0` and main.
- Invariants: framework replaceability; host-owned authentication/policy (no invented game security assumptions); actor/session isolation; known Empty vs unknown provider; durability receipts; cancellation/reentrancy/order.
- Out of scope: tag/release mutation, main promotion before demo push, fabrication of tests/verification.

### Before delegating
- Exact lease: each worker owns only its named report/files; no production edits by reviewers.
- Acceptance: real diff + real output required; no green receipt substitutes for code review.
- Pitfalls: Unity batchmode lock; WebGL `FS.syncfs` deprecated; synthetic OS clicks do not reach uGUI; Input System vs legacy; Firebase credentials.

### Before accepting
- Real diff reviewed for every change; real output captured; unrun gates listed, not claimed.
- New remaining work gate: manual PlayMode all demo scenes; agent-driven mod create/edit; integrated regression receipt green; demo push; main promotion only after demo push.

### New remaining work gate (unchecked)
- [ ] Manual PlayMode all demo scenes
- [ ] Agent-driven mod create/edit exercised
- [ ] Integrated regression receipt green
- [ ] Demo branch pushed
- [ ] Main promoted only after demo push


## 2026-10-26 publication gate correction

Current gate (supersedes older demo-only wording; old wording retained as history): demo-first, then main after demo QA. Push demo only after verified fixes and documentation; promote to main only after all demo scenes are checked and fixed and demo QA passes. Preserve v7.54.0; no tag or release mutation. Use ASCII hyphens in tracking text.
