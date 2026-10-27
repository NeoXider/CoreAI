# Group 08: Luau downlevel and iterator seams

Status: complete independent static review, 2026-10-25; two actionable findings (one P2, one P3), no executed proofs.
Baseline: `69d5bcc37502cd4bb464a3bf9ac0c53b1624b804`.
Candidate: `bc3fd74f0a889bfe1b1e7babc6bc02ed48a7c05e` (all finding locations use this head).

Goal: identify actionable introduced defects or incorrect repairs in all 12 assigned paths and every changed hunk.
Scope: group 08 of `coverage.json`, including both historical documents; adjacent contracts/callers are context only.
Invariants: lexical scope, evaluation order/errors, trusted iterator triples and protected metatables, sandbox integrity, compiler failure propagation, compatible core/Mods integration, runtime-first replaceable seams.
Constraints: only this report may change; no builds, tests, runtime/model/network/Unity calls, git mutations, fixes, or delegation.
Coverage: all 12 primary candidate files and all 76 unified-context diff hunks read. Historical documents were inspected as claims, not verification evidence.
Acceptance: full path/hunk ledger; concrete changed-line findings with trigger, expected/actual, causal analysis and proposed regression proof; hypotheses separate.

## PR-08-001 [P3] Accepted resolver name `_ENV` captures the environment instead of the callable

Pinned location: `Assets/CoreAIMods/Runtime/LuauDownlevel/DownlevelResult.cs:142` (resolver-name allowlist); the generated bare-name capture is `Assets/CoreAIMods/Runtime/LuauDownlevel/LuauRewriteParser.cs:702`.

Concrete valid host configuration: use `new LuauDownlevelOptions(trustedIteratorResolverName: "_ENV")`, install the named global callable before execution, then run a direct-table loop. The existing execution helper can install that global without replacing its lexical environment:

```lua
-- Host setup, run before the transformed chunk:
rawset(_ENV, "_ENV", function(value) return next, value, nil end)

-- Source to transform with the configured name:
local sum = 0
for _, value in {4, 5} do sum = sum + value end
return sum
```

Expected: the configured installed resolver is called and the chunk returns 9. `_ENV` satisfies the published simple ASCII identifier restrictions, is not in the reserved `__luau_t` prefix, and is accepted by the new validation.

Actual, inferred from the candidate and Lua 5.2 binding semantics: `Process` succeeds and emits `local __luau_t0 = _ENV;`. That reference binds the chunk's implicit environment upvalue, not the callable stored under its `_ENV` key. The normalized loop consequently tries to call the environment table. The new configuration seam therefore accepts a name it cannot resolve according to its own global-callable contract.

Root cause: validating keyword/prefix exclusions is insufficient when generating a bare Lua identifier; `_ENV` has special lexical binding in the target language. Reject that name explicitly, or capture the actual global entry through a binding mechanism that honors it.

Minimal regression proof plan: use `RunLuau` with the setup/source above and the configured name, requiring 9 and one resolver invocation. If root chooses to reject this special name instead, add `_ENV` to `InvalidTrustedResolverName_IsRefusedWithoutThrowing`, require original source plus Error before execution, and document the additional exclusion. Retain the ordinary replaceable-resolver and protected-metatable integration cases.

Uncertainty: high confidence for the advertised Lua 5.2 target; no upstream or bundled-VM execution was performed. The default backend's `__coreai_luau_iter` name is unaffected; this finding applies to the new configurable host seam.

## PR-08-002 [P2] Eager floor capture fails even when the division is never evaluated

Pinned location: `Assets/CoreAIMods/Runtime/LuauDownlevel/LuauRewriteParser.cs:818`.

Concrete supported-state trigger: create a normal engine state, execute `math = nil` on it, then downlevel and run:

```lua
local function unused()
    return 7 // 2
end
return 42
```

Expected: return 42 without evaluating the unused function or looking up its arithmetic helper. This worked at the baseline: its generated `math.floor` access remains inside `unused`. State reuse is supported by `IScriptEngine.RunChunk`; the sampled secure-environment entry restores only the reserved iterator resolver and does not restore `math`.

Actual, inferred from the candidate: `EnsureFloor` inserts `local __luau_t0 = math.floor;` at chunk entry. Execution indexes nil before reaching the first author statement, although the division is unreachable. A host environment that omits the optional math library has the same failure. A metatable-backed math table can likewise acquire an earlier observable lookup.

Root cause: fixing lexical shadowing by hoisting the fallible `math.floor` lookup changes error timing and makes unused nested code impose a new environment prerequisite. A trusted primitive source or equivalent safe capture must preserve both hygiene and lazy error behavior.

Minimal regression proof plan: use the execution fixture's existing `hostSetup` parameter with `math = nil`, run the trigger, and require exactly 42; add the same two-chunk sequence through `LuaCsScriptEngine` to cover state reuse. Also retain the existing ordinary/compound/nested-shadowing tests, so postponing the lookup cannot reintroduce local shadowing.

Uncertainty: high confidence in the emitted prelude and execution order; actual VM exception text and host-specific diagnostics were not executed. The finding concerns an unused operation, not a demand that the old transform successfully execute division with a missing library.

## Coverage ledger

Each `+start,count` pair identifies one candidate-side hunk from `git diff --no-renames --unified=3 <baseline> <candidate>`. All pairs below were reviewed, including removals. Full candidate contents were read for every row; no assigned path was skipped. There are no assigned metadata paths.

| Primary path | Candidate lines read | Reviewed hunks | Assessment |
|---|---:|---|---|
| `Assets/CoreAIMods/Runtime/LuaExecution/LuauSourceGate.cs` | 1-77 | `+35,11` | Resolver option forwarded; Error still throws before VM entry. |
| `Assets/CoreAIMods/Runtime/LuauDownlevel/DownlevelResult.cs` | 1-235 | `+2,162; +211,7` | PR-08-001; also positive limits, work/output/edit accounting and rollback inspected. |
| `Assets/CoreAIMods/Runtime/LuauDownlevel/LuauDownleveler.cs` | 1-348 | `+10,15; +41,66; +134,12; +193,7; +217,12; +292,9; +307,16; +324,17` | Overloads, catches, trigger expansion, conflict handling and output sizing inspected. |
| `Assets/CoreAIMods/Runtime/LuauDownlevel/LuauLexer.cs` | 1-750 | `+31,16; +74,7; +88,7; +99,9; +111,7; +135,13; +256,14; +277,51; +405,7; +472,13; +598,12; +616,11; +642,66` | All lexical/budget/Unicode/string changes read; newline hypothesis below remains uncounted. |
| `Assets/CoreAIMods/Runtime/LuauDownlevel/LuauRewriteParser.cs` | 1-1805 | `+18,14; +38,8; +69,8; +89,7; +99,10; +160,7; +254,14; +274,16; +297,7; +322,10; +335,19; +373,17; +697,14; +713,7; +722,18; +780,56; +905,12; +1028,11; +1071,7; +1204,7; +1272,7; +1285,12; +1311,9; +1343,15; +1535,16; +1563,14; +1599,10` | Both findings; also traced repeat suffix scope, break behavior, edit ordering, helper rollback, interpolation and unchanged type parsing. |
| `Assets/CoreAIMods/Runtime/LuauDownlevel/README.md` | 1-103 | `+2,8; +15,7; +28,40; +71,17; +89,15` | Limits, iterator responsibilities, standalone protected-metatable limitation and supported-subset exclusions compared with code. |
| `Assets/CoreAIMods/Runtime/Scripting/IScriptEngine.cs` | 1-73 | `+3,13` | Additive optional resolver interface leaves existing engine implementers source-compatible. |
| `Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsScriptEngine.cs` | 1-204 | `+12,7; +52,9` | Advertised resolver name matches the installed backend constant; state/run/coroutine contracts read. |
| `Assets/CoreAIMods/Tests/EditMode/LuauDownlevelerEditModeTests.cs` | 1-940 | `+61,29; +294,7; +367,11; +418,7; +426,7; +434,7; +450,7; +458,7; +466,7; +508,19; +758,10; +797,144` | New and adjusted assertions read, including budget/nesting interaction, malformed Unicode, resolver names and identity checks. |
| `Assets/CoreAIMods/Tests/EditMode/LuauDownlevelerExecutionEditModeTests.cs` | 1-557 | `+36,9; +51,11; +204,183` | All tests/helper code read; host setup already permits PR-08-002's regression case. No test execution. |
| `dev-docs/audits/2026-10-23/07-mods-luau-downlevel.md` | 1-238 | `+1,238` | Entire historical audit read. Its ten baseline findings and receipts were not counted as candidate defects. |
| `dev-docs/fix-wave-2026-10-24/07-luau.md` | 1-52 | `+1,52` | Entire repair receipt read, including negative/positive historical probes and pending fixture rerun; none constitutes this review's proof. |

## Contextual reads and limits

- Root `AGENTS.md` and `Docs/ARCHITECTURE_RULES.md` read completely; the group-08 manifest agrees with the exact 12 assigned paths, 1,209 additions and 197 deletions. A scoped rules-file search found no additional `AGENTS.md`.
- `Docs/CoreAIMods/SCRIPT_ENGINE_SEAM.md`: lines 1-52, state reuse and replacement contracts.
- `LuaCsModRuntime.cs`: lines 1990-2065, downlevel before state execution, registry application, both startup routes and original source retention. `LuaCsAiEnvelopeProcessor.cs`: lines 85-155, resolver forwarding and failure reporting. `LuaCsGameToolExecutor.cs`: lines 715-780, async entry, compile gate and prepared state. These paths were checked at the pinned candidate after search discovery.
- `LuaCsSecureEnvironment.cs`: focused lines 19-21, 212-265, 769-833 and 1430-1495. Traced resolver installation before/after registry application and before both engine entries, actual table/userdata metatable lookup, multiple-argument passthrough, cached `next`, and the counted/yield-fenced call entry. Its complete sandbox/guard implementation was not audited.
- `LuaCsRbxSchedulerAdapter.cs`: lines 440-500; `LuaCsRbxApiBindings.cs`: lines 1990-2045. Traced reserved resolver restoration before chunk loading and immediate startup scheduling. Other scheduler/module/loading paths were not exhaustively reviewed.
- `ModsContractRegressionTests.cs`: lines 345-537, context only. Read the trusted helper cases for persistent load/reload, one-off tool, core AI envelope, registry/global replacement, ordinary triples, protected table/userdata, finite budget, cancellation, errors and yield rejection. Those are assertions inspected, not observed passes. Table-valued protection/decoy behavior was reasoned from direct actual-metatable access, not executed.
- Baseline comparisons additionally inspected the old repeat lowering, cast loop and quoted-string scanner. Broad symbol searches were only discovery; primary coverage claims above come from pinned `git show`/`git diff`, not mutable worktree source or other reviewers' reports.

## Uncounted hypotheses and verification gaps

- **Chained assertion suspicion withdrawn from findings:** `IsLabelAhead` also matches `value :: any :: number`, which the baseline mini-parser accepted. That alone does not establish valid upstream Luau syntax. No upstream grammar/compiler proof is available under this review's restrictions, so this is not counted as an introduced supported-input defect.
- **LF-CR escaped newline:** the changed scanner accepts a paired CR-LF but not paired LF-CR: after a backslash plus LF, the remaining CR now triggers the new raw-CR refusal at `LuauLexer.cs:259`. The baseline scanner passed this through. Proposed probe: both quote types with source constructed as `"return " + quote + "a\\" + "\n\r" + "b" + quote`. Confirm supported upstream/bundled-VM newline semantics before treating it as an actionable runtime regression. No local vendor lexer source was found in the scoped dependency search, and no VM probe was run; this is not a third finding.
- Reserved `__luau_t` collisions, invalid repeat conditions referencing skipped locals, raw multiline backticks, unsupported outer-vararg if-expressions and the documented standalone protected-metatable fallback are not counted. No evidence makes them newly introduced supported-input failures here.
- Portable/Unity compilation, actual exception/result values, load/performance, native stack/heap ceilings, IL2CPP and WebGL behavior remain unverified. Bounded synchronous code and absence of new Domain threading/IO were inspected; device responsiveness is not established by those observations.

## Verification receipt and handoff

Read-only inspection used `git show`, pinned `git diff` (including full hunk enumeration), `rg`, file enumeration and PowerShell text/JSON reads. Inventory: 12 files, 76 context hunks, 1,209 additions, 197 deletions. One malformed source-read command and initial nonexistent context paths were corrected; no build/test/runtime command was attempted. Historical green aggregates were not adopted as evidence.

CHANGED: only `dev-docs/pr-review-2026-10-25/08-luau.md`; the scope/header was written before deep inspection and updated at the source-review milestone.

VERIFY OUTPUT: primary source/diff reading complete. Direct report checks found 12/12 ledger paths, 76/76 hunk entries, two finding headings, zero trailing-whitespace lines, zero Cyrillic characters and a final newline. Scoped `git diff --check` returned exit 0; because the report is untracked, the direct text checks are the meaningful format check. Findings remain static inferences with proposed regression proofs.

NOT DONE: root must adjudicate/reproduce findings, lease any fixes, run regression suites and device gates, and manage git. No production/test fix or candidate verification was performed here.

CONCERNS: one P2 regression and one P3 configuration defect above; uncounted cast/newline hypotheses. Context outside the listed ranges was not exhaustively audited.

QUESTIONS: none; no clarification was needed.
