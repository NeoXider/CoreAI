# Runtime invocation completion, 2026-10-27

## Task and ownership

- GOAL: Carry an explicit editor invocation's limits and cancellation to the concrete Lua VM that actually executes after an active-world replacement. Exercise the editor adapter through the production Hub service, factory, trusted actor, and real Lua VM.
- SCOPE: `Assets/CoreAIMods/Runtime/Infrastructure/RbxWorldPackageContracts.cs`; new `Assets/CoreAIMods/Tests/EditMode/ActiveWorldInvocationBudgetEditModeTests.cs`; new `Assets/CoreAIMods/Tests/EditMode/CodeEditorRuntimeEditModeTests.cs`; this report. The authorized adapter file was inspected and needed no production change.
- Change nothing else. Existing dirty changes, including source-store strict capability forwarding in the contracts file, were preserved. No Git mutation, Unity/MCP action, asset/meta write, nested worker, or shared portable build was performed.

## Decisions before implementation

- Real goal: The editor's short policy must follow actual dispatch across a world swap. Opening a scope must not bind it permanently to the outgoing runtime.
- Implied requirements: Correct runtime selection; finite instruction-budget regressions; cancellation before mutation; LIFO nesting; logical execution-context isolation; restoration after failure; receipt identity and persistence truth; exception isolation for UI observers; existing authority and ownership checks.
- Conventions: Existing `ILuaModRuntime` / `IBudgetedLuaModRuntime`, `LuaCsModRuntime.BeginInvocation`, real `CoreAiModsInstaller` composition, current ACL world version, production local host identity, existing Hub persistence receipts, explicit C# types, English prose, XML/WHY comments, and `COREAI_LUA` test guards.
- Out of scope: Source/world transaction serialization, compare-and-swap storage, permanent frame budgets, game/player-specific integration, new interpreter machinery, later-resume policy changes, and changing ownership/capability authorization.
- Most likely mistake pre-empted: Applying a scope only when it opens, or updating the active runtime globally. The facade stores immutable policy nodes and enters a captured concrete runtime's scope only around an executing dispatch.
- Reuse: Existing runtime guards, production world replacement, real Hub save/reload transaction, source expectations, and trusted identity were used directly. Test-only memory stores record actual observable mutations and persistence behavior; they do not replace the Hub service or VM.

## CHANGED

### Active runtime facade

`ActiveLuaModRuntime.BeginInvocation` now owns an `AsyncLocal` LIFO chain of immutable limits/token policies. It validates missing limits, pre-cancellation, and active controller lifetime. Each `LoadMod`, both `ReloadMod` overloads, `ImportMod`, and `TryRevertMod` captures the concrete executing runtime and enters its invocation scope immediately around that call. Disposal restores the prior facade policy; no scoped policy leaves existing unscoped host behavior changed.

The facade's existing source admission, attribution preparation/rollback, host authority, capability, and concrete runtime behavior remain in their existing paths. Imports retain their existing false-on-execution-failure contract.

### Real production-world regression fixture: 9 cases

`ActiveWorldInvocationBudgetEditModeTests` uses `CoreAiModsInstaller`, `FileLuaModSourceStore`, `IRbxWorldRuntimeService.LoadConfirmedAsync`, the retained runtime facade, and the production actor. It proves that replacement creates a different concrete `LuaCsModRuntime`.

- Scope opened before replacement: initial load, default reload, and explicit-mode reload each encounter a typed `LuaStepBudgetException` cause under 1,000 instructions. The same finite 20,000-iteration source then succeeds after scope disposal.
- Import after replacement is refused under the short policy, creates no loaded mod, and succeeds unscoped.
- Revert after replacement trips the short policy and retains the prior healthy source; the same revision succeeds after disposal.
- Nested policies enforce LIFO, restore the outer policy after another world swap, and support repeated disposal.
- Cancellation after replacement refuses dispatch; the next unscoped load succeeds.
- Missing limits and pre-cancelled scope creation cannot replace an active outer policy.
- A separately captured logical execution context remains independent of the caller's short policy.

### Real editor adapter regression fixture: 18 cases

`CodeEditorRuntimeEditModeTests` composes `LuaCodePlaygroundRuntime` with actual `LuaCsModRuntimeFactory`, `LuaCsModRuntimeHubService`, current-ACL `LuaCsRbxApiBindings`, and production actor identity. The memory source store acknowledges strict writes or explicitly supplies failure/unconfirmed behavior. Runtime automatic persistence is disabled so the Hub owns the observable receipt path.

- Successful explicit SaveAndRun executes actual state/world mutations, persists source, returns matching request IDs, and reloads with the configured startup cleanup behavior.
- Persistence failure and unconfirmed persistence preserve the distinction between executed and persisted. Retrying a failed save uses the retained real Hub receipt without executing the VM twice.
- Python on the Lua adapter, Python interpreter stub, and Lua Run-only each return Unsupported with zero state/source/delete/loaded-mod/authored-world mutations.
- Missing expectation, existence mismatch, case mismatch, newline mismatch, and stored-only source mismatch return Conflict with zero mutation and retain the prior source.
- A finite loop trips the short real-VM budget, returns diagnostics and matching IDs, and the next healthy run succeeds with its own policy.
- A yielded initial main chunk returns under the short policy; after completion, cancelling the former request does not cancel later resumes, which execute under the normal host policy.
- Pre-cancelled calls, exact Stop run-ID matching, stopping before mutation, disposal during Running status, disposal idempotence, post-disposal refusal, initialization, observer exception isolation, and reentrant-run refusal are exercised through the real adapter.

## VERIFY OUTPUT

- Read the real facade diff and both complete new fixture sources. Reviewed all five executing dispatch paths, finite failure/recovery behavior, ownership/capability paths, persistence receipts, cancellation/lifetime, and event exception isolation.
- `git diff --check -- Assets/CoreAIMods/Runtime/Infrastructure/RbxWorldPackageContracts.cs`: exit 0, no output.
- Source scan: new fixtures contain no implicit `var`, non-ASCII prose, or disallowed narrative comment lines. Both fixtures wrap source in `#if COREAI_LUA`; `NonParallelizable` is conditional outside Unity because Unity's bundled NUnit does not always expose it.
- Enumerated 18 editor fixture cases and 9 active-world fixture cases: 27 cases total.
- Source-final handoff was sent to root before compilation. Root owns new portable csproj links and the actual compile/native/portable test gates. No test pass is claimed here until those receipts exist.

## NOT DONE

- Native Unity compilation and execution, portable compilation/test execution, player/WebGL/IL2CPP/device/performance validation, and independent adversarial acceptance belong to the root verification pass and were not run by this worker.
- No adapter production change was required by source inspection. New regressions still require actual root-run evidence before acceptance.

## CONCERNS

- Exact expected-source comparison is a read-before-write precondition. It is not a cross-process atomic compare-and-swap. Hosts must serialize source/world mutation operations. Capturing a concrete runtime once selects the correct dispatch target but does not make source-store and world replacement atomic.
- Invocation limits affect only the initial main-chunk resume. Yielded continuation work and subsequent handlers/timers use the existing normal host policies. A completed editor command's Stop/cancellation does not terminate a persistent mod's later work.
- A Lua chunk can mutate live state before a later runtime failure. The tests that assert zero mutation place budget failure before mutation or exercise unsupported/conflicting/pre-cancelled paths; they do not promise universal rollback of arbitrary first-load side effects.
- Test timeout values use the maximum allowed 1,000 ms and finite loops, with instruction counts determining failure, so a broken guard cannot hang these newly added regressions.

## QUESTIONS

None. The root's dispatch architecture and exact write ownership supplied all consequential decisions.
