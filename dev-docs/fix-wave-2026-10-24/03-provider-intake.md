# Provider repair work record

Scope: GOAL03, baseline `69d5bcc37502cd4bb464a3bf9ac0c53b1624b804`, target release `7.54.0`.
Root owns Git, release/version files, shared documents and every build/test execution. This worker changes only its explicit lease and runs no builds, NUnit, Unity, MCP or provider calls.

## Findings and acceptance

| ID | Reproduce and existing guard review | Disposition | Regression acceptance | Proposed files |
| --- | --- | --- | --- | --- |
| LLM-001 | Authentication refresh retries a failed completion with executed receipts; streaming tracks text but discards the original authentication error chunk before checking receipts. HTTP idempotency cannot undo local tool effects. | confirmedfix | Before effects, one refresh/retry is allowed. After effects or visible output, no replay; retain typed error, usage, status, retry hint and receipts. Cancellation propagates. | `RefreshOnUnauthorizedDecorator.cs`; `RefreshOnUnauthorizedDecoratorEditModeTests.cs`; `RetryFallbackToolTraceSuppressionEditModeTests.cs` |
| LLM-002 | Default completion-to-stream failure conversion omits receipts and usage, while retry/fallback guards rely on receipts. Success-only conversion is insufficient for replacement providers. | confirmedfix | A completion-only provider returning a transient failure with receipts runs once through retry/fallback; metadata survives success and failure conversion. | `Core/Features/Orchestration/ILlmClient.cs`; `RetryFallbackToolTraceSuppressionEditModeTests.cs`; `ProviderContractRegressionTests.cs` |
| LLM-003 | Fallback checks error text only; a code-only permanent/cancelled terminal is mistaken for empty output. Existing retry code checks code independently. | confirmedfix | Null/empty text errors keep their original permanent/cancellation classification. Transient fallback occurs only before text/receipts; null control chunks do not cause corruption. | `FallbackLlmClientDecorator.cs`; `PermanentLlmFailureEditModeTests.cs`; `ProviderContractRegressionTests.cs` |
| LLM-004 | Managed fetch queue and JS reader pump have no backlog bound. Disposal/timeout cleanup does not bound a paused consumer. | confirmedfix | Configurable byte/chunk limits bound retained backlog; overflow aborts deterministically with a typed transport failure. Oversized upstream chunks are checked before concatenation. Reader cancellation, timeout, terminal callback and lease cleanup occur once. | `FetchSseOpenAiTransport.cs`; `FetchSseTransportProtocol.cs`; `CoreAiSseFetch.jslib`; `fetch_sse_jslib_test.js`; `MeaiOpenAiWebGlTransportEditModeTests.cs`; HTTP/streaming docs |
| LLM-005 | A reusable endpoint handle shares one release flag across overlapping invocations and can decrement before admission on disposal. Finally blocks cannot repair shared flag ownership. | confirmedfix | Completion/completion, stream/stream and mixed overlap release once per admitted invocation; unused handle disposal releases none; last invocation controls retirement cleanup. | `LlmClientRegistry.cs`; `ProviderContractRegressionTests.cs` |
| LLM-006 | Malformed SSE JSON/known delta fields are swallowed; earlier output followed by a terminal signal can therefore report success. Existing missing-terminal guard cannot detect corrupted data. | confirmedfix | Visible text/tool fragments followed by malformed JSON or known schema fail observably with a typed redacted error, never clean completion. Comments, blank/usage and unknown extension frames remain supported. | `MeaiOpenAiChatClient.cs`; `MeaiOpenAiChatClientSseEditModeTests.cs`; streaming docs |
| LLM-007 | Routing records a fault code but sets failed outcome only for nonempty error text; code-only failures clear degradation and publish success. | confirmedfix | Code-only failures remain sticky through trailing control/usage and early disposal; completion, usage and registry health agree on failure and retain the provider classification. | `RoutingLlmClient.cs`; `ProviderContractRegressionTests.cs`; `LoggingLlmClientDecorator.cs` and `CircuitBreakerLlmClientDecorator.cs` only if the same predicate defect is confirmed |

These are framework contracts, independent of the host game's authentication/account policy, matchmaking and session lifetime. UH01 remains an uncounted host integration note. Unbounded parser line/tool argument size beyond the fetch backlog remains a hypothesis requiring a separate budget contract.

## Cross-owner contract

`MeaiLlmClient` must build one effective invocation tool list before policy construction and AI-function construction, binding every tool through `CallSkillToolLlmTool.BindForRole(tool, store, request.AgentRoleId)`. The list is invocation-local and used throughout both completion paths. Skill files and the memory owner's actual-schema budget implementation stay untouched.

## Progress and verification

- Intake: read repository instructions, architecture rules and provider audit. Rechecked auth refresh and default adapter source; remaining direct guard review continues before implementation.
- Reuse: repair existing provider/transport seams in place; no new library or gameplay dependency.
- Verification status: no builds/tests run. Root will receive exact portable, Source fake-provider and Node commands at the source checkpoint. Source inspection and preexisting green tests are not pass evidence.
- Proposed new regression file: `Assets/CoreAiUnity/Tests/EditMode/ProviderContractRegressionTests.cs` (portable inclusion is root-owned).
- Proposed docs: `Assets/CoreAiUnity/Docs/HTTP_TRANSPORT_SPEC.md`, `Assets/CoreAiUnity/Docs/STREAMING_ARCHITECTURE.md`.
