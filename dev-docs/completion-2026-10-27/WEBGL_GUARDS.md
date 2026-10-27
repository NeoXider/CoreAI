# WebGL transport and synchronous assertion guard repair - 2026-10-27

Status: source changes frozen for root verification. Scope is the two named C# files and this report. No Unity run, build, test process, shared output mutation or Git mutation was performed by this worker.

## Goal and invariants

Close the gateway transport and code-editor fixture violations reported in `artifacts/pr-review-20261025/unity-completion-edit-final1-failures.json` without weakening either guard. Preserve the portable .NET and Editor transport API, real request deadlines, response/cancellation disposal, all existing test assertions, and the test runner's original synchronization context.

## Changes

- `Assets/CoreAI/Runtime/Core/Features/Llm/Gateway/HttpClientGatewayTransport.cs`: the outer compilation condition is now `COREAI_LLM && (!UNITY_WEBGL || UNITY_EDITOR)`. The entire HttpClient transport is excluded from the WebGL player. Its timer-backed `CancelAfter` remains unchanged for supported .NET/Editor/standalone targets. No primitive allowlist or scanner exception was added.
- `Assets/CoreAI/Tests/EditMode/CodeEditorContractsEditModeTests.cs`: fixture-local `[SetUp]` saves `SynchronizationContext.Current` and detaches it; `[TearDown]` restores the saved context. The Python stub test still synchronously unwraps its completed task and retains every existing assertion. The guard's failure message names async assertions generically, but its implementation also detects this unwrap shape.

The factory already rejects a missing host service transport under `UNITY_WEBGL && !UNITY_EDITOR`. `UnityGatewayClientBinding` supplies `UnityWebRequestGatewayTransport` by default. Those files were read and left unchanged. The Unity transport already uses a player-loop deadline; this repair introduces no alternate timer.

| Symbols with `COREAI_LLM` enabled | HttpClient transport included |
| --- | --- |
| Plain .NET, no Unity symbols | Yes |
| Editor, including a WebGL-selected Editor target | Yes |
| Non-WebGL standalone player | Yes |
| WebGL player without `UNITY_EDITOR` | No |

## Verification evidence and remaining execution

Source inspection confirmed that the WebGL scanner's existing three-valued condition evaluator understands `&&`, `||`, negation and parentheses; it proves the new outer condition false for a WebGL player even while treating `COREAI_LLM` as unknown. The existing fixture guard detects `[SetUp]` plus `SetSynchronizationContext(null)` in the same fixture. Both guard implementations and all assertions remain unchanged.

Read-back commands completed with exit code 0: `Get-Content` for both changed source files, `Get-FileHash -Algorithm SHA256` for both, and `rg` over the factory, Unity binding and portable test project. The portable test project already links both `CodeEditorContractsEditModeTests.cs` and `GatewayServiceClientEditModeTests.cs`.

Root owns the actual acceptance runs:

- Unity EditMode filters `CoreAI.Tests.EditMode.WebGlUnsafeAsyncPrimitivesEditModeTests` and `CoreAI.Tests.EditMode.SynchronousAsyncAssertionDeadlockGuardEditModeTests`: expect all guard tests to pass after other root-owned violations are also repaired.
- Unity EditMode filter `CoreAI.Core.Tests.EditMode.CodeEditorContractsEditModeTests`: expect every original contract assertion to pass and the fixture context to be restored after each test.
- `dotnet test tools/portable/Tests/CoreAI.Portable.Tests.csproj --filter "FullyQualifiedName~GatewayServiceClientEditModeTests|FullyQualifiedName~CodeEditorContractsEditModeTests"`: expect zero failures, including existing transport cancellation/disposal regressions.
- Root's full portable Core and full Unity EditMode gates remain required; this report does not claim their results.

## Frozen source identity

| File | SHA-256 |
| --- | --- |
| `Assets/CoreAI/Runtime/Core/Features/Llm/Gateway/HttpClientGatewayTransport.cs` | `9e09c16ed70d1b242fe3528adf8e2aeb30aa86b811bd8c8ce278d47a3977e21f` |
| `Assets/CoreAI/Tests/EditMode/CodeEditorContractsEditModeTests.cs` | `5a20627b0a047abb23c2336678c36b7fdc9f6ed3c2cf40a616fe265099c5e7a8` |

No additional concerns or questions within the assigned scope. The separate `HubModEditorPage` WebGL violation visible in the original failure receipt remains outside this worker's ownership.
