# Hub Save bounded execution and WebGL guard follow-up

Status: source changes frozen for root verification. No Unity process, build, EditMode run, Git staging, commit, or publication was performed by this worker.

## Goal and observed failures

The supplied full EditMode receipt reported 8,268 tests: 8,239 passed, 14 failed, and 15 skipped. Ten failures in `HubModPagesRefreshEditModeTests` were stopped by the production refusal to execute an `IHubModService` without `IBudgetedHubModService`. Its positive Save fixture still advertised the legacy interface only. The production WebGL primitive guard also rejected the synchronous unwrap in `HubModEditorPage.SaveCore`.

Evidence inspected: `artifacts/pr-review-20261025/unity-completion-edit-final1-failures.json`. These are baseline results supplied by the root; they do not verify the changed source.

## Changed

- `LuaCodePlaygroundRuntime.Run` is the explicit synchronous entry point for the existing bounded initial VM execution and persistence transaction. `RunAsync` preserves its public signature and returns `Task.FromResult(Run(...))`. Result publication, cancellation, exception classification, identity, active-run fencing, and cleanup retain the existing flow.
- `HubModEditorPage.SaveCore` calls `Run` directly. No awaitable unwrap, worker, timer, or deferred execution was added. Source preconditions, overwrite review, result matching, retained drafts, and Save control restoration keep their original behavior.
- Positive Hub Save cases use `BudgetedCountingHubModService`. This derived fixture validates limits with `LuaModInvocationLimits`, checks cancellation, requires an active invocation before saving, and disposes its scoped guard on every completed path. Existing source mutation, reload-mode, failed/unconfirmed persistence, conflict, close-guard, and retry assertions remain.
- The unbudgeted `CountingHubModService` remains available for the legacy refusal case and unrelated page refresh/delete cases. A new regression verifies that Hub Save returns Unsupported without mutation or persistence and preserves the draft for an unbudgeted service.
- Hub Save regressions additionally check guard disposal and restored Save, language, and text editing controls after successful, failed, cancelled, and unconfirmed saves. Their configured limits remain 100 ms and 500,000 steps.
- The production-VM fixture's shared runner now calls `Run`, so its existing save/reload, short-budget, cancellation, source conflict, persistence retry, observer, disposal, reentrancy, unsupported command, and Python refusal cases cover the synchronous path. Four new direct/compatibility and successful/cancelled combinations run with a non-pumping synchronization context, checking caller-thread observers, no posted/sent callbacks, immediate compatibility-task completion, preserved identity, and real source/state effects.

## Verification performed

- Read every worker diff against the pre-existing staged candidate.
- `git diff --check -- <four scoped C# paths>`: exit 0, no whitespace errors.
- Scoped runtime search for `GetAwaiter().GetResult`, `.Result`, blocking `.Wait`, `CancelAfter`, `Task.Run`, and `async void`: no matches in either changed runtime file.
- Confirmed the checkout is `D:/Git/CoreAI`, branch `demo`.

## Not done and required root checks

No compile or executable test result is claimed by this worker. The root owns all execution and must run:

1. The WebGL unsafe primitive and synchronous async assertion guards.
2. `HubModPagesRefreshEditModeTests`, including the new legacy refusal and retained retry/back-guard cases.
3. `CodeEditorRuntimeEditModeTests`, including the new non-pumping-context cases and direct VM short-budget/cancellation/save paths.
4. Existing typing/no-auto-execution coverage.
5. The full EditMode suite after the focused checks pass.

The fake guard proves editor transaction scoping and cleanup; actual budget enforcement is covered by the production VM fixture. Browser/player execution is a separate gate and was not exercised.

## Frozen code SHA-256

| File | SHA-256 |
| --- | --- |
| `Assets/CoreAIMods/Runtime/HubIntegration/HubModEditorPage.cs` | `FC5F6C284ED1F178FDCBEF6974C1DF2D64056ACC7F191481C751FBE214AEC137` |
| `Assets/CoreAIMods/Runtime/HubIntegration/CodeEditing/LuaCodePlaygroundRuntime.cs` | `81C0B38BCA083F66369ABA12257438E080E814C36D05B120C78949D538F342FF` |
| `Assets/CoreAIMods/Tests/EditMode/LuaSyntaxHighlighterEditModeTests.cs` | `13BB274FBD8EDF7FF5AA69A51776758489A50CA4E225E2AC432E6192A1AF7E63` |
| `Assets/CoreAIMods/Tests/EditMode/CodeEditorRuntimeEditModeTests.cs` | `9FA855747727F463FC294017F744180FC0AAA4932BFAC03F55CC610E84A954BD` |

## Concerns and questions

- The final native tests are still pending; static checks cannot establish runtime acceptance.
- No implementation question remains. The root decides acceptance and publication from current executable receipts.
