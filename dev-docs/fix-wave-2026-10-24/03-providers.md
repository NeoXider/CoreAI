# Provider repair work record

Date: 2026-10-24. Model: `gpt-6.1-sol`; reasoning effort: `max`. Baseline: `69d5bcc37502cd4bb464a3bf9ac0c53b1624b804`. Lease: module 03, transferred from the settled CLI provider worker. Root owns all builds/tests, Git, release metadata and Unity operations. No execution pass is claimed here.

## Goal and scope

Preserve typed provider failure metadata and tool side-effect receipts through every supported adapter and decorator; avoid replay after commitment; bound the WebGL bridge; make malformed SSE observable; pair every admitted registry operation with one release. Authentication policy remains host-owned. The memory owner owns all skill-named files, `CallSkillToolLlmTool`, `ToolExecutionPolicy`, stores and actor helpers. This owner only joins their existing role-binding API in `MeaiLlmClient`.

Before planning: correctness, cancellation, ordering, reentrancy, replay safety, bounded memory, cleanup and observability apply. Existing public APIs and optional-module boundaries must survive. No live provider, benchmark, build, Unity/MCP, asset, metadata or shared project-file change is in scope.

## Per-finding intake and regression acceptance

| ID | Review/reproduction and disposition | Proposed source files | Regression acceptance |
| --- | --- | --- | --- |
| LLM-001 | Confirmed framework defect: auth refresh can replay a failure after a tool receipt; partial fix requires semantic review. | `RefreshOnUnauthorizedDecorator.cs` | Completion and receipt-only streaming failures preserve original auth metadata/receipts and invoke the inner client once; pre-effect auth failures still refresh. |
| LLM-002 | Confirmed framework defect: the default completion adapter previously discarded failure receipts. | `ILlmClient.cs` | Default adapter preserves receipts, usage, response state and retry metadata; retry/fallback do not replay an executed mutation. |
| LLM-003 | Confirmed framework defect: code-only errors were treated as empty streams by fallback. Logging and health predicates require the same code-or-text rule. | `FallbackLlmClientDecorator.cs`, `LoggingLlmClientDecorator.cs` | Permanent/cancelled code-only failures never call fallback; transient pre-commit failures may do so; failure telemetry remains failure. |
| LLM-004 | Confirmed framework defect: browser pump and managed queue had no backlog bound. Safe deterministic abort is the chosen compatible contract; browser throughput/memory remain unmeasured. | `FetchSseOpenAiTransport.cs`, `FetchSseTransportProtocol.cs`, `CoreAiSseFetch.jslib` | Configured byte/chunk overflow terminates with a typed error, cancels the reader once, clears timers/bridge state and does not concatenate an oversized upstream chunk. |
| LLM-005 | Confirmed framework defect: shared release state on a reusable registry handle lost concurrent releases and could decrement before admission. | `LlmClientRegistry.cs` | Unused handle disposal keeps zero leases; overlapping completion/stream/mixed calls each release once, including cancellation and early iterator disposal. |
| LLM-006 | Confirmed framework defect: malformed expected SSE payloads could be silently discarded before a valid terminal frame. | `MeaiOpenAiChatClient.cs` | Valid partial output followed by malformed JSON or malformed known fields fails with a redacted typed error, never clean DONE; comments/usage/unknown extensions remain allowed. |
| LLM-007 | Confirmed framework defect: code-only routing failures published success/health recovery. | `RoutingLlmClient.cs` | Completion event, usage attribution and endpoint health retain the original failure across null/control/usage frames and early disposal. |
| MEM-009 join | Confirmed cross-owner integration: bind every invocation tool before policy and function construction, using a per-request cloned effective list. | `MeaiLlmClient.cs` only | Both stream modes use the same request-bound list for policy and function construction; trusted role and catalog metadata are preserved without shared catalog mutation. |

Matching existing provider fixtures in the lease may be updated; new regressions belong in `Assets/CoreAiUnity/Tests/EditMode/ProviderContractRegressionTests.cs`. Browser mock regressions belong in `Assets/CoreAiUnity/Tests/Node~/fetch_sse_jslib_test.js`. User-facing contracts belong in the package `HTTP_TRANSPORT_SPEC.md` and `STREAMING_ARCHITECTURE.md`. Any additional shared-file need must be sent to root.

## Progress and checks

- Intake read the assignment, repository instructions, normative architecture rules and all seven audit findings. The prior worker's provider source changes are present; no tests or docs are accepted as complete.
- First milestone: this record created before any source edits. Next: review every partial owner diff, finish missing contracts and regressions, then freeze source and send root exact serial check filters.
- Build/test/Unity/WebGL-player/live-provider results: not run by this owner, per root's explicit operation lease.

Before acceptance: read the actual scoped diff, inspect failure/cancellation/cleanup paths, and report exact changed files, unexecuted checks and remaining platform gates. Root will attach executable evidence.

## Source checkpoint and handoff

The transferred partial changes were reviewed in every leased file and completed. All seven findings remain confirmed framework defects; implementation and named regression cases are present, with C# execution pending root's receipt. MEM-009 is joined through one read-only invocation tool list per request before policy/function construction, including malformed text-call recovery. Root approved keeping original tools when no memory store exists; no global memory fallback was introduced. Root will exercise the cross-module actor/role binding scenario.

LLM-004 uses bounded outstanding UTF-8 bytes and fragments on both sides of the bridge. Fully consumed fragments acknowledge capacity. Oversized upstream values are rejected before decoding; exceeding a limit aborts safely with `BackendUnavailable`, rather than pausing an arbitrarily large upstream chunk or dropping output. Browsers without a streaming reader fail without an unbounded whole-body allocation. The existing UnityWebRequest transport choice remains available. Managed status-zero opens classify the bridge diagnostic immediately so a synchronous open continuation cannot dispose the state before a pre-header typed timeout is preserved.

The registry now creates an idempotent release token per operation. Reusable handle disposal is neutral; operation completion, cancellation and iterator disposal own their separate release. No live-provider behavior, browser heap measurement, throughput, native activation, Android/IL2CPP transport, or player ABI pass is claimed.

Exact changed files:

- `Assets/CoreAI/Runtime/Core/Features/Orchestration/ILlmClient.cs`
- `Assets/CoreAI/Runtime/Core/Features/Llm/LoggingLlmClientDecorator.cs`
- `Assets/CoreAI/Runtime/Core/Features/Llm/MeaiOpenAiChatClient.cs`
- `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/FallbackLlmClientDecorator.cs`
- `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/FetchSseOpenAiTransport.cs`
- `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/FetchSseTransportProtocol.cs`
- `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/LlmClientRegistry.cs`
- `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/MeaiLlmClient.cs`
- `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/RefreshOnUnauthorizedDecorator.cs`
- `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/RoutingLlmClient.cs`
- `Assets/CoreAiUnity/Runtime/Plugins/WebGL/CoreAiSseFetch.jslib`
- `Assets/CoreAiUnity/Tests/EditMode/MeaiOpenAiChatClientSseEditModeTests.cs`
- `Assets/CoreAiUnity/Tests/EditMode/ProviderContractRegressionTests.cs`
- `Assets/CoreAiUnity/Tests/Node~/fetch_sse_jslib_test.js`
- `Assets/CoreAiUnity/Docs/HTTP_TRANSPORT_SPEC.md`
- `Assets/CoreAiUnity/Docs/STREAMING_ARCHITECTURE.md`
- `dev-docs/fix-wave-2026-10-24/03-providers.md`

Root reported `node Assets/CoreAiUnity/Tests/Node~/fetch_sse_jslib_test.js` exit 0, `ALL PASS`; receipt: `artifacts/fix-wave-20261024/fetch-sse-console.log`. The ABI and Node files were frozen before that run and have not changed afterward. Root linked `ProviderContractRegressionTests.cs` into the portable project. The six core cases cover failure metadata, flag-only failures, retry suppression and code-only logging; Source-only cases use the existing `UNITY_5_3_OR_NEWER` symbol. Six SSE malformed-data cases plus one comments/extensions/usage control were added to the existing portable fixture.

Requested root checks, sequentially:

```powershell
dotnet test tools/portable/Tests/CoreAI.Portable.Tests.csproj -c Release --filter "FullyQualifiedName~ProviderContractRegressionTests|FullyQualifiedName~MeaiOpenAiChatClientSseEditModeTests|FullyQualifiedName~RetryingStreamingLlmClientDecoratorEditModeTests|FullyQualifiedName~LoggingLlmClientDecoratorEditModeTests|FullyQualifiedName~CircuitBreakerLlmClientDecoratorEditModeTests"
```

Unity LLM/full filters: `ProviderContractRegressionTests`, `RefreshOnUnauthorizedDecoratorEditModeTests`, `RetryFallbackToolTraceSuppressionEditModeTests`, `PermanentLlmFailureEditModeTests`, `LlmClientRegistryLeaseEditModeTests`, `RoutingLlmClientEditModeTests`, `LlmRoutingRegressionEditModeTests`, `MeaiStreamingToolCallEditModeTests`, `RuntimeEndpointRequestSettingsEditModeTests`, `LlmPipelineInstallerEditModeTests`, `MeaiOpenAiWebGlTransportEditModeTests` and `FetchSseTransportProtocolEditModeTests`. Required observations: one admitted invocation after executed receipts, no permanent-code fallback, original failure metadata, failed completion/usage/health publication, and lease counts `0 -> 2 -> 1 -> 0` across completion/completion, stream/stream and both mixed orderings. Cancellation and repeated iterator disposal must return to zero.

WebGL player checks remain separate: actual native bridge ABI, managed slow-reader overflow, cancellation/disposal cleanup, and no-SSE transport selection. The unbounded parser line/tool-argument hypothesis from the audit was not promoted to a new requirement or silently implemented.
