# Independent Core and Mods PR findings — 2026-10-27

Status: frozen-production Core/Mods review and root-authorized final merged candidate review complete. The first Enter rejection and superseded quote candidates are preserved. Final twelve-file source manifest `0562cb77c0e29c90f097785cdf841c29d129af134750e8dd3bb6c4894e1f12a1` is independently approved for root-controlled promotion. The additional quote type-over P2 is addressed in those exact candidate bytes. See [CANDIDATE_REVIEW.md](CANDIDATE_REVIEW.md) for hashes and reasoning. Compilation/native/full acceptance remains root-owned and is not claimed here.

Reviewer: native GPT-6.1 Sol; reasoning effort: max. Actual checkout: `D:/Git/CoreAI`, branch `demo`, HEAD `bc3fd74f0a889bfe1b1e7babc6bc02ed48a7c05e`. Existing work remains dirty/untracked by authorization. No source, asset, metadata, scene, prefab, project, Git state or shared tracker was changed by this reviewer.

## Review decisions

- Real goal: provide actionable, independently checked release findings against the entire current Core/Mods change, including untracked new files.
- Implied requirements: stable public contracts, per-request identity, explicit ownership, cancellation/failure isolation, bounded work, truthful persistence receipts, runtime portability, replaceable framework policy, and meaningful regressions.
- Out of scope: cancelled NPC navigation, ignored staging artifacts, UnityHost/Hub/editor presentation findings assigned elsewhere, and claims of native acceptance inferred from portable results.
- Acceptance: every confirmed finding needs changed source and exact line evidence, a realistic trigger, user-visible impact, a narrow correction, and a regression proposal; unconfirmed possibilities remain explicitly separate.

## Findings and ownership

| Report | This review's outcome | Owning action |
| --- | --- | --- |
| [CORE.md](CORE.md) | No additional confirmed Core runtime finding | Root verifies final source and retained execution/platform gates |
| [MODS.md](MODS.md) | No additional confirmed Mods VM/store/composition finding | Root verifies final native Lua/store/facade gates |
| [EDITOR_REVIEW.md](EDITOR_REVIEW.md), `EDITOR-REVIEW-002` | P3 Lua `#f` auto-pairing mismatch discovered by this reviewer and independently accepted by the editor reviewer | Count once in editor review; language-aware helper regression required |

The independent editor/host reviewer additionally owns `EDITOR-REVIEW-001` (P2 Enter inside a Lua long literal/comment), `HUB-REVIEW-001` (P2 ancestor Escape interception), and `UNITY-REVIEW-001` (P2 registry cancellation callback skipping host cleanup). Root has confirmed all four findings and authorized isolated candidate preparation. The registry issue is explicitly a pre-existing defect in the touched lifecycle path, not a newly introduced regression. These three are not re-counted as findings authored by the Core/Mods reviewer.

Absence of another VM/store finding does not clear those editor/host findings or the native gates. Candidate implementation and source promotion are separate from their source-backed reproduction; a staged file is not a production fix.

## Milestones and review decisions

1. Located and verified the real D: checkout and current `demo` HEAD. Read applicable AGENTS.md, relevant historical audit/repair records and the current tracked/untracked inventory. The direct owner request retains reports, overriding the repository's historical deletion convention.
2. Created these three report outlines before deep review. Examined full new portable editor/Gateway files, all changed Core/Mods runtime hunks and their caller/engine/store joins, active-world budget dispatch and all eight new bundled Lua resources. Read independent editor/host reports for cross-boundary ownership.
3. Checked likely failure cases before acceptance: unknown-vs-bound Empty scope, shared role across actors, raw auth retry after visible output, uncooperative telemetry, migration followed by failure/cancellation, old vs new confirmation generations, unsupported strict facade backend, failed first installation, world replacement after opening an invocation scope, first-vs-later Lua resumes and material acquisition reentrancy. Distinguish host obligations and intentional existing semantics from confirmed regressions.
4. Confirmed one language-composition mismatch and handed it to the editor reviewer for a single authoritative finding. Rejected unproven security/rollback/global-policy claims without an actual production call path. Persisted full Core/Mods source results and hashes before any candidate promotion.
5. Root requested independent review of isolated candidate diffs before promotion. The first language/helper subset was read against byte-exact frozen originals, and a candidate-only Enter regression was rejected before promotion. The remainder and revised candidate are pending; production Asset source remains unchanged during root's current Unity run.

## Candidate review, before production promotion

Staging root: `C:/AI/work/coreai-completion-20261027/editor-host-candidates/`, mirrored paths; `originals/` contains frozen originals. Independent hash comparison confirmed those originals still match production for all three handed-off files. These candidates are not production changes.

| Candidate | SHA-256 | Independent result |
| --- | --- | --- |
| `CodeLanguageProfiles.cs` | `4bcdb895b53261be15ddea990977723da191ba1f5cb2bf0c784e88d578fa855a` | Rejected pending correction: loses real block indentation before an inline comment |
| `CodeEditorTextUtility.cs` | `5011e39e8b781a72eae185e2eaf446e82d0abd65bb98fb8487abb2ff41a26a7c` | Source direction accepted: exact existing eight-parameter overload forwards through nullable default classification; new nine-parameter overload accepts host context without replacing Python behavior |
| `CodeEditorView.cs`, author's two isolated hunks | `8881b5f2341e776d7d6066f4d7a56588c0a8dec02d625927cf66d4ad4e5fc485` | Source direction accepted for forwarding accepted language context and dismissing this view's own transient UI; complete merged Hub wiring/native regression is still pending |

Candidate-only regression `CANDIDATE-COREMODS-001`: the first `PlanEnter` fix asks `ICodeLanguageContext.IsCodeAt(text, caret)` before reading the current line. For `if true then -- note` with the caret at the end, the full tokenizer correctly marks the final comment character as non-code, so the new early return emits only the base indent. Production currently ignores the trailing comment and returns four spaces for the real `then`. `for ... do -- note` and `function f() -- note` have the same problem. This is a deterministic source-derived candidate regression; no test was run and no production regression is counted from it.

Required correction: use the full-source lexical classification of actual current-line tokens to distinguish a genuine opener followed by a comment from an opener-looking word physically inside a long literal/comment. Add positive inline-comment opener controls alongside the negative long-literal/comment controls. Sent promptly to root and author; no production or staged code was edited by this reviewer.

The author acknowledged this regression and reported a replacement approach. At idle handoff the author supplied all eleven stable candidate files, `candidate.diff` and `candidate-manifest.json` under the staging root above; the reported revised PlanEnter SHA-256 is `3d181648aa888ef4603960ea473e5430337711f703b47bc4cd8efd055a43429d`. This replacement, complete merged view and final regressions have not been independently read or executed by this reviewer. Root requested an idle handoff to free reviewer capacity for a live-harness lifecycle failure; it will dispatch a fresh review of final merged candidates. No later candidate is implicitly approved by the partial source-direction acceptance above.

Follow-up: root dispatched that review against the mechanically merged twelve-file manifest at `C:/AI/work/coreai-completion-20261027/combined-editor-candidates/manifest.json`. Corrected PlanEnter passed independent source review, including positive inline-comment controls. The full merged view exposed `CANDIDATE-REVIEW-001` [P2], tracked quote closers blocked by the outer lexical insertion gate; this is present in frozen production too and is not a new merge regression. Root accepted it and assigned a narrow isolated correction. The final current-token/verified-insertion candidate and retained regressions passed independent source review; intermediate failures were not promoted. Hashes, exact reproduction, regression requirements and final approval are owned by `CANDIDATE_REVIEW.md`.

## Scope and source fingerprints

The reports pin 28 Core and 34 Mods changed/new `.cs`/`.lua`/`.asmdef` paths, excluding `.meta` from the code manifest. Paths come from both `git diff --name-only` and `git ls-files --others --exclude-standard`, so untracked feature source is included. The Mods view presentation/input is owned by the independent editor reviewer; this review inspects its language/interpreter integration rather than claiming a duplicate full view audit.

| Manifest | SHA-256 |
| --- | --- |
| Core, 28 paths | `6eedc79c53c6892473c5ff0a862d499d3cedcf76770961def66e17f681e2728e` |
| Mods, 34 paths | `a3bc971041c34047ab28bc5d8f7b31c295148d1939afb876320d783b2efcf798` |

Manifest construction and representative full file hashes are recorded in each report. Hashes pin the reviewed production snapshot; any promoted candidate changes require a delta review and a new fingerprint.

## Verification evidence

No builds/tests were run by this reviewer. Read-only source/diff/hash/whitespace commands were used. Saved root TRX files were independently parsed through actual `UnitTestResult` nodes:

- `artifacts/completion-20261027/core-final/core-final1.trx`: 2,954 Passed and 3 NotExecuted.
- `artifacts/completion-20261027/core-perf/core-perf-green-debug1.trx`: 111 Passed.
- `artifacts/completion-20261027/core-perf/core-perf-green-release1.trx`: 111 Passed.

These are saved portable results, not current-source native acceptance; the receipt files do not embed this review's complete source manifest. Three skipped/not-executed cases remain excluded. Test source was read to evaluate regression relevance without relabeling authored tests as executed proof.

NOT DONE: root promotion and source-final native focused/full results; real OpenRouter API PlayMode acceptance; attached editor/actual Hub input; built-player, IL2CPP, WebGL/browser, device, real network/load and performance evidence. A direct provider HTTP completion or tool-call probe does not satisfy Unity acceptance. Independent staged-candidate source review is complete.

CHANGED: the original three assigned Markdown reports plus root-authorized `CANDIDATE_REVIEW.md`. VERIFY OUTPUT: dirty/untracked source reads, input hashes, existing portable TRX accounting and independent isolated/merged candidate review. CONCERNS: the five accepted editor/host findings have source-reviewed candidate repairs; root must still verify their actual integration. QUESTIONS: none. No source lease is held.
