# CoreAI provider, transport, and streaming audit

Audited commit: `69d5bcc37502cd4bb464a3bf9ac0c53b1624b804` (HEAD and origin/main verified locally). Audit date: 2026-10-23. Model: gpt-6.1-sol; reasoning effort: max.

## Status and boundaries

Source review completed within the explicit coverage below; this report was written incrementally. No provider, Unity, device, integration, or performance pass is claimed. The human explicitly authorized this audit document under `dev-docs/audits/2026-10-23/`, overriding the repository prohibition on audit reports. This is the only file this auditor may write. Root owns git and coordinates all builds/tests; no delegation is used.

Scope: CoreAI LLM providers, HTTP/OpenAI request/response transport, SSE, timeout/retry/circuit-breaker decorators, routing and request option propagation; Unity adapter transports, registry/factory/bootstrap/settings/readiness and the WebGL fetch contract; orchestration stream filters and directly relevant tests/configuration. Tool/skill registry belongs to report 02; general host lifecycle belongs to report 04. Those modules are inspected only when necessary to establish a transport call chain.

Excluded execution: builds/tests using shared output, Unity/MCP, native model activation, services, installs, live LLM requests, real benchmarks, browser/device runs. All source/code/assets are read only. The preexisting untracked `Assets/Epic Toon FX/Upgrade/ETFX 2019.4.24f1 URP Upgrade.unitypackage.meta` is preserved.

## Reviewed evidence

- `AGENTS.md` and `Docs/ARCHITECTURE_RULES.md`: runtime/WebGL requirements, configurable seams, cancellation and threading rules, audit restrictions and their explicit override.
- Package entry points reviewed: `Assets/CoreAI/README.md` (overview, composition, provider/stream properties and limitations) and `Assets/CoreAiUnity/README.md` (provider/WebGL surfaces, typed receipts and limitations). Transport details: `Assets/CoreAiUnity/Docs/HTTP_TRANSPORT_SPEC.md`; streaming call-chain/filter overview: `Assets/CoreAiUnity/Docs/STREAMING_ARCHITECTURE.md` sections 1-3.
- `TODO.md`: inspected LLM/transport/streaming/resilience entries to distinguish existing tracked debt from new findings.

Exact source coverage follows. A directory plus its listed filenames denotes the exact repository-relative paths. This was targeted source inspection, not a claim that every line in the scoped directories or every test method was reviewed.

| Directory | Files and reviewed portions |
| --- | --- |
| `Assets/CoreAI/Runtime/Core/Features/Llm/` | `HttpClientOpenAiTransport.cs`, `IOpenAiHttpTransport.cs`, `IOpenAiHttpSettings.cs`, `HttpClientOpenAiReadinessProbe.cs`: request/open/read/timeout/header/disposal contracts; `TimeoutLlmClientDecorator.cs`: executable deadline, race, cancellation and cleanup paths; `RetryingStreamingLlmClientDecorator.cs`: executable retry/commit/disposal paths; `CircuitBreakerLlmClientDecorator.cs`: admission, generation/probe release, both completion paths and result classification; `LoggingLlmClientDecorator.cs`: non-stream retry/receipts and stream forwarding/error bookkeeping; `ClientLimitedLlmClientDecorator.cs`: request reservation, stream cancellation/disposal and quota release; `LlmRequestOptions.cs`, `LlmRequestContext.cs`: option shapes and logical-request context. |
| `Assets/CoreAI/Runtime/Core/Features/Llm/` | `MeaiOpenAiChatClient.cs`: 249-963, 964-1110, 1249-1309, 1368-1448, 1471-1640 (HTTP/SSE call paths, timeout budget, headers, error mapping/redaction and non-stream parsing); 1880-2250 (SSE parsing, reasoning/progress/terminal classification); 2478-2550, 2597-2700, 2816-2895, 2949-3075, 3140-3203 (tool-call identity/order/drain/flush/argument buffers); 3554-3646 (base options, extra-body precedence and typed cap preservation). `SmartToolCallingChatClient.cs`: native loop entry and provider/tool/final-summary call sites located; per-invocation policy creation and finally receipt preservation read at 350-419. |
| `Assets/CoreAI/Runtime/Core/Features/LlmRouting/` | `LlmRouteResolver.cs`: ordering, exact/prefix/wildcard matching; `LlmEndpointContracts.cs`: timeout fields and descriptor validation 84-111, 177-238; `ILlmClientRegistry.cs`: snapshot/resolve contracts; `LlmEndpointReadiness.cs`: probe/result/policy contracts. |
| `Assets/CoreAI/Runtime/Core/Features/Orchestration/` | `ILlmClient.cs`: typed errors/cancellation helpers, provider response state, result/chunk metadata and default stream adapter; `ThinkBlockStreamFilter.cs`, `ToolResultEchoStreamFilter.cs`: executable buffering/filter/flush/reset paths. |
| `Assets/CoreAI/Runtime/Core/` | `CoreAISettings.cs`: global settings resolution and timeout properties; `ICoreAISettings.cs`, `CoreAISettingsOptions.cs`: timeout defaults/copy fields; `ILlmAsyncMarshaler.cs`: portable scheduling contract. |
| `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/` | `FetchSseOpenAiTransport.cs`, `FetchSseTransportProtocol.cs`, `UnityWebRequestOpenAiTransport.cs`, `UnityWebRequestOpenAiReadinessProbe.cs`, `WebGlCompositeOpenAiTransport.cs`: executable transport, callback, cancellation, queue, read, header and cleanup paths. `UnityMainThreadLlmAsyncMarshaler.cs`: built-player branch and PlayerLoop delay at 1-59; editor-only context/fault handling selected ranges. |
| `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/` | `OpenAiChatLlmClient.cs`, `RefreshOnUnauthorizedDecorator.cs`, `FallbackLlmClientDecorator.cs`: completion/stream wrappers and settings/commit checks. `ServerManagedLlmClient.cs`: settings/auth forwarding and scoped header snapshot composition. `MeaiLlmClient.cs`: HTTP factories 28-169; non-stream tool setup/failure/result conversion 190-385; streaming cap 492-534; stream policy/loop/catch/tool-drain/terminal paths 536-1630 selected ranges; request settings/options 2838-2950. Full tool execution policy and bindings remain report 02's scope. |
| `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/` | `LlmClientRegistry.cs`: activation/request wrappers 109-273; route/health resolution 426-644; activation/release 1233-1452 selected ranges. `LlmEndpointClientFactory.cs`: HTTP/readiness/tool-channel configuration 40-254, native branch/platform and configuration/release sections 256-594 selected ranges. `LlmRoutingManifest.cs`: profile/mode/route conversion 93-177. `LlmUnityModelBootstrap.cs`, `LlmUnityHostConfigurator.cs`, `ConfigurableLlmAgentProvider.cs`: executable model-selection/bootstrap/configuration paths; no native execution. |
| `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/` | `CoreAISettingsAsset.cs`: serialized deadline defaults, accessors/effective timeout and options-copy sites; `OpenAiHttpLlmSettings.cs`: inherited/explicit timeout fields, override properties and conversion sites; `LlmUnityServerHttpSettings.cs`: timeout/settings forwarding. |
| WebGL/configuration | `Assets/CoreAiUnity/Runtime/Plugins/WebGL/CoreAiSseFetch.jslib`: complete fetch pump/callback/abort implementation; `Assets/Resources/CoreAISettings.asset`: timeout/output/stream/credentials fields only; `Assets/CoreAiUnity/Settings/LlmRoutingManifest.asset`: enabled routing with empty profiles/routes. Credential values were not printed. |

Related test coverage inspected (all under `Assets/CoreAiUnity/Tests/EditMode/` unless otherwise stated):

- `RefreshOnUnauthorizedDecoratorEditModeTests.cs`: refresh/header snapshot cases and their fakes, 1-294.
- `PermanentLlmFailureEditModeTests.cs`: thrown refusals, fallback composition, transient controls and fakes, 1-306.
- `ResilienceFeaturesEditModeTests.cs`: fallback cases and representative error/streaming fakes at 522-1040, selected ranges.
- `RetryFallbackToolTraceSuppressionEditModeTests.cs`: invoked/rejected receipt distinctions, fallback receipt cases and default adapter accounting at 514-859, selected ranges; test names elsewhere were searched.
- `RetryingStreamingLlmClientDecoratorEditModeTests.cs`: targeted cases for pre-commit/commit, cancellation, synchronous-open failure, code-only errors and disposal; remaining methods located by name, not a full fixture review.
- `MeaiOpenAiChatClientSseEditModeTests.cs`: timeout/response evidence cases at 246-615 selected ranges; malformed completion at 1065-1099; terminal/error/partial-EOF cases at 1373-1598 selected ranges; accumulator malformed/flush cases at 1928/2090 and associated names. The entire 2800+ line fixture was not reviewed.
- `MeaiStreamingToolCallEditModeTests.cs`: post-tool and next-roundtrip failure cases read at 505-569; other test names for native early execution and text-channel behavior located. `MeaiOpenAiChatClientTransportHeadersEditModeTests.cs`, `OpenAiChatLlmClientEditModeTests.cs`, `MeaiLlmClientEditModeTests.cs`: targeted option/header/auth/reasoning searches only.
- `MeaiOpenAiWebGlTransportEditModeTests.cs`, `CoreAiSseFetchJslibEditModeTests.cs`: complete executable mock/source-guard cases; `FetchSseTransportProtocolEditModeTests.cs`: header/cancellation contract cases located and inspected selectively.
- `LlmClientRegistryLeaseEditModeTests.cs`: request/stream/no-request admission cases and fakes at 15-182, 250-257; disposed-registry test names. `RoutingLlmClientEditModeTests.cs`: cancellation/health/error publication cases and fakes, selected ranges. `LlmRoutingRegressionEditModeTests.cs`: endpoint failure/recovery and stale-generation cases at 436-512.
- `CircuitBreakerLlmClientDecoratorEditModeTests.cs`, `ThinkBlockStreamFilterEditModeTests.cs`, `ThinkBlockFilterEditModeTests.cs`, `TimeoutLlmClientDecoratorEditModeTests.cs`, `TransportTimeoutClassificationEditModeTests.cs`: targeted failure/probe/cancel/partial-tag/deadline case discovery and selected assertions; not full-fixture review.
- `LlmConfigurationDefectsEditModeTests.cs`: adapters 25-65, timeout/options cases 141-158; `LlmPipelineInstallerEditModeTests.cs`, `RuntimeEndpointRequestSettingsEditModeTests.cs`: targeted settings/wire propagation searches.
- `Assets/CoreAI/Tests/EditMode/LlmClientDecoratorContractEditModeTests.cs`: complete reflection guard; `CoreAuditFindingsEditModeTests.cs`, `LlmRequestSettingsCompatibilityEditModeTests.cs`: relevant timeout/cancellation/settings case discovery.
- `Assets/CoreAiUnity/Tests/Node~/fetch_sse_jslib_test.js`: mock fetch/callback/timeout/abort case discovery only, not executed. `tools/portable/Tests/CoreAI.Portable.Tests.csproj`: linked-fixture and `COREAI_LLM` configuration inspected to formulate the root check.

Excluded from complete review: unrelated skill/tool registry, full tool-policy implementation, general host/scene lifecycle, endpoint-store persistence internals, complete native LLMUnity dependency internals, all provider-specific reasoning dialect serializers, unused routing entitlement/usage sink implementations, UI and benchmark modules, unlisted source/test ranges and all build/player generated artifacts. No package-wide pass is claimed.

## Confirmed actionable findings

### LLM-001 — P1 — Auth refresh replays a completed tool side effect on the non-streaming path

Status: new finding; high confidence from the full source call chain. No runtime reproduction executed.

Evidence: `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/RefreshOnUnauthorizedDecorator.cs:70` accepts any failed `AuthExpired` completion for refresh, and `:82` invokes the whole inner completion again without checking `ExecutedToolCalls`. Production wiring is `Assets/CoreAiUnity/Runtime/Source/Composition/LlmPipelineInstaller.cs:392`. `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/MeaiLlmClient.cs:200` creates a fresh tool client for each invocation; `:240-245` converts a later provider exception to a failure retaining receipts; `:363-375` preserves both `AuthExpired` and those receipts. `Assets/CoreAI/Runtime/Core/Features/Llm/SmartToolCallingChatClient.cs:350` creates the per-invocation policy and `:411-415` retains its receipts even on failure.

Trigger: a server-managed non-streaming turn executes a non-idempotent tool, then its follow-up model request returns 401/403; the registered refresher succeeds. The wrapper invokes the original request again with a fresh tool policy, so the same purchase/spawn/append can execute twice. A stable HTTP idempotency header does not itself deduplicate local tool execution. This occurs inside the auth wrapper, before the outer fallback/logging receipt guards can prevent replay.

Observed guard: the streaming wrapper tracks only visible text (`Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/RefreshOnUnauthorizedDecorator.cs:93`, `:127-130`, `:146`), also ignoring receipts from replaceable streaming clients. The built-in MEAI streaming path has its own cumulative execution counter (`Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/MeaiLlmClient.cs:631`, `:1001`, `:1178-1192`), so the confirmed built-in trigger above is specifically non-streaming; no reset-per-roundtrip defect is claimed.

Existing tests reviewed: `Assets/CoreAiUnity/Tests/EditMode/RefreshOnUnauthorizedDecoratorEditModeTests.cs:25-43`, `:46-66`, `:69-122` cover successful refresh, scoped headers, and ordinary streams, with no post-tool auth failure. Existing fallback receipt tests in `Assets/CoreAiUnity/Tests/EditMode/RetryFallbackToolTraceSuppressionEditModeTests.cs:582-640` exercise a different decorator and do not guard this inner replay.

Fix direction: enforce a commit boundary in auth refresh too. Refreshing credentials for a future request is safe; replaying this request after any executed tool is not. Preserve the original failed result/terminal chunk and all receipts after that boundary, including when a custom client reports receipts without text.

Regression recommendation: wrap a real `MeaiLlmClient` over a deterministic fake `IChatClient`; first response calls a counter-incrementing tool, next response throws `AuthExpired`, refresher returns true. Assert exactly one increment, one logical completion invocation, original auth failure and receipt preserved. Add streaming receipt-only and non-streaming equivalent cases directly against the decorator.

### LLM-002 — P1 — The default completion-to-stream adapter drops receipts on failure and enables replay

Status: new finding; high confidence from contract and consumer source. Affects replaceable `ILlmClient` implementations using the documented default adapter; no claim that the built-in MEAI override uses this adapter.

Evidence: `Assets/CoreAI/Runtime/Core/Features/Orchestration/ILlmClient.cs:955-966` maps a failed completion to a terminal chunk but omits `ExecutedToolCalls`; the success branch explicitly copies them at `:981`. `Assets/CoreAI/Runtime/Core/Features/Llm/RetryingStreamingLlmClientDecorator.cs:207-210` retries a transient error when it is not committing, and its commit check at `:344-347` depends on text or receipts. `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/FallbackLlmClientDecorator.cs:226-234` likewise admits fallback when the failed chunk has no receipts.

Trigger and impact: inject a completion-only client that returns `Ok=false`, `BackendUnavailable`, a nonempty error, and a receipt for an already executed mutation. Calling its default streaming method erases that receipt. Streaming retry invokes the mutation again; fallback can execute the turn on another provider. The supported provider replacement seam therefore loses the safety information that both decorators require.

Existing guard/tests: `Assets/CoreAiUnity/Tests/EditMode/RetryFallbackToolTraceSuppressionEditModeTests.cs:611-640` protects an explicitly constructed streaming error chunk that already has receipts. Its default-adapter case at `:831-859` covers successful token accounting only. `Assets/CoreAiUnity/Tests/EditMode/RetryingStreamingLlmClientDecoratorEditModeTests.cs:368` covers code-only permanent failure from the adapter, without tool receipts. No inspected case covers this failed-result-to-chunk conversion.

Fix direction: copy executed receipts and relevant completion metadata on failure as well as success; use one metadata conversion helper to prevent branch drift. Retain fail-safe receipt handling in consumers.

Regression recommendation: a completion-only fake increments a counter once and returns a transient failure plus an executed receipt. Drain its default stream through retry and fallback separately and together. Assert the original receipt survives, the primary is called once, the secondary never runs, and the original failure classification/status/retry metadata survive.

### LLM-003 — P2 — Fallback treats a code-only permanent terminal error as an empty success and switches providers

Status: new finding; high confidence from source; no runtime reproduction executed.

Evidence: `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/FallbackLlmClientDecorator.cs:226` requires a nonempty `Error` string to classify a retryable error. Its commit predicate at `:293-297` also ignores `ErrorCode`. A terminal chunk with only `ErrorCode=PaymentRequired`, `AuthExpired`, `InvalidRequest`, or `Cancelled` reaches `:244-252`, which unconditionally marks the primary failed and authorizes secondary execution at `:280-289`. Such chunks are valid: `Assets/CoreAI/Runtime/Core/Features/Orchestration/ILlmClient.cs:955-965` forwards the optional failure text unchanged.

Trigger and impact: a completion-only replacement provider returns a permanent failed result without diagnostic text. Its default streaming adapter emits a code-only terminal. A configured fallback performs an unrelated secondary request instead of returning the refusal, potentially crossing provider/cost boundaries. A nonterminal code-only error is also forwarded and can be followed by fallback on stream end.

Existing guard/tests: `Assets/CoreAiUnity/Tests/EditMode/PermanentLlmFailureEditModeTests.cs:78-101` tests a thrown payment failure; `Assets/CoreAiUnity/Tests/EditMode/ResilienceFeaturesEditModeTests.cs:639-657` tests an error chunk with `Error="primary-error"`. The retry decorator already checks `ErrorCode` independently at `Assets/CoreAI/Runtime/Core/Features/Llm/RetryingStreamingLlmClientDecorator.cs:192`, with dedicated tests at `Assets/CoreAiUnity/Tests/EditMode/RetryingStreamingLlmClientDecoratorEditModeTests.cs:368-423`; that outer protection cannot undo a fallback that happened inside it.

Fix direction: classify errors with `ErrorCode != None || nonempty Error` before the empty/done logic. Preserve permanent and cancellation chunks; retryable failures may fall back only before text/tool commitment. Ignore null control chunks consistently.

Regression recommendation: parameterize default-adapter and direct-stream fakes across permanent, cancellation, and transient codes with null/empty text. Assert no secondary call for permanent/cancelled codes, one allowed fallback for transient codes before commitment, and none after a tool receipt or visible text.

### LLM-004 — P2 — The WebGL fetch pipe has no bounded backlog or backpressure

Status: new finding; high confidence in the missing bound. Real browser memory/throughput impact has not been measured.

Evidence: `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/FetchSseOpenAiTransport.cs:240` creates an unbounded `ConcurrentQueue<string>`; `:287-292` always enqueues every browser fragment. No byte count or high-water limit is consulted. Its consumer dequeues only during managed reads at `:531-555`. `Assets/CoreAiUnity/Runtime/Plugins/WebGL/CoreAiSseFetch.jslib:199-226` keeps invoking `reader.read()` and scheduling another pump regardless of managed consumption. The public callback contract at `:21` has no consumption acknowledgement. `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/MeaiLlmClient.cs:1074-1075` can await a tool while enumeration of provider updates is suspended.

Trigger and impact: the browser keeps receiving a long/fast response while a tool or another consumer of the public transport waits. The C# queue retains the entire unread response; a slow consumer can repeatedly accumulate more than it drains. This bypasses network-stream backpressure and can exhaust the browser/WASM heap. The visible-text cap in `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/MeaiLlmClient.cs:497-513` operates after dequeue/parsing and cannot bound this queue. Default total timeout is zero, and progress-aware response wait is not a queue capacity limit.

Observed guards: browser reads yield with `setTimeout`, disposal aborts fetch, and terminal callbacks remove static state. Those prevent a blocking read or permanent dictionary entry; they do not bound pending payloads. Tests reviewed: `MeaiOpenAiWebGlTransportEditModeTests.cs` tests mock non-SSE fallback and ordinary completion dispatch; `CoreAiSseFetchJslibEditModeTests.cs` checks source strings for safe callback wrappers and abort guards, without a slow-reader scenario.

Executed in-memory probe: the actual jslib, with only Emscripten callback macros replaced and a fake fetch/virtual timer queue, forwarded all 512 × 1024-byte fragments without any C# read/acknowledgement. Output: `{"probe":"actual jslib with in-memory fetch and callbacks; no C# reads","reads":513,"delivered":512,"bytes":524288,"done":1,"errors":0}`. This establishes eager forwarding in that JavaScript harness, not Unity/browser memory behavior.

Fix direction: add an explicit bounded byte budget and a nonblocking consumer acknowledgement/pause mechanism, or fail the request with a typed capacity error and abort fetch when the limit is reached. Make the limit configurable; do not add synchronous waits or threads to WebGL.

Regression recommendation: virtual-timer JS fixture plus a WebGL player test with a managed reader paused behind a gate. Feed beyond the configured backlog cap and assert bounded retained bytes, pause/resume or a typed terminal failure, exactly one abort/terminal callback, prompt cancellation, and cleanup of both dictionaries.

### LLM-005 — P2 — A reusable registry client handle has shared lease state across concurrent calls

Status: new finding; high confidence from the counter transitions. Root should coordinate any overlap with report 04; ownership here is the provider-registry wrapper, not general application shutdown.

Evidence: `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/LlmClientRegistry.cs:442` returns a public `ILlmClient` handle wrapping the ready endpoint. `TrackedEndpointClient` keeps one `_leaseReleased` flag at `:196`. Each request resets it and increments the shared endpoint count at `:208-212`; both completion and streaming finally blocks call `ReleaseLease` at `:238-240` and `:258-260`. `ReleaseLease` decrements only for the first exchange from zero at `:264-269`.

Trigger and impact: resolve a handle once, start two overlapping calls on that handle, then let both complete. Take A → take B increments the count to two and leaves one shared flag at zero. Release A changes it to one and decrements to one; release B sees one and does nothing. Each overlapping pair can leak a lease. Later endpoint retirement waits for an already completed request until the 120-second force-release path (`:1412-1433`), delaying model cleanup/replacement and reporting false in-flight work. Disposing a freshly resolved handle before any request also decrements from zero because the flag starts at zero (`:198-201`, `:214-217`); that negative count can hide a real later request from drain.

Observed guard/tests: individual operations use finally blocks and `Interlocked`, but idempotence is per wrapper rather than per invocation. `Assets/CoreAiUnity/Tests/EditMode/LlmClientRegistryLeaseEditModeTests.cs:98-180` covers one call, one stream, and repeated resolve-without-execute; it does not overlap two requests on the same handle or dispose an unused handle. No one-shot restriction is enforced by the returned `ILlmClient` contract.

Fix direction: acquire/release a distinct per-invocation lease token, with the admission count paired exactly once with that token. Handle disposal must not release another invocation's lease or decrement before admission. Keep completion/stream cancellation and early iterator disposal paired.

Regression recommendation: resolve once; use a gated fake to overlap completion/completion, stream/stream and mixed calls; assert counts 0→2→1→0 in each case. Dispose an unused handle and assert zero. Retire the endpoint during overlap and assert the owned-host release callback waits for the last admitted request without a forced timeout.

### LLM-006 — P2 — Malformed SSE data can be discarded while the turn reports successful partial output

Status: new finding; high confidence from parser and terminal validation. No C# runtime fixture executed.

Evidence: `Assets/CoreAI/Runtime/Core/Features/Llm/MeaiOpenAiChatClient.cs:1919-1928` sends every SSE data line to `ExtractDeltaUpdate`; malformed JSON is swallowed and returned as null at `:2026-2034`. Malformed known delta fields are also swallowed by `:2095-2182`. The streaming loop tracks only whether any usable output and any terminal signal occurred (`:620-648`), and accepts that combination at `:683-726`; it records no malformed-frame state.

Trigger and impact: receive a valid visible delta, an invalid/truncated JSON `data:` payload, then `[DONE]` (or a valid finish frame followed by EOF). The invalid payload disappears; the earlier text makes `sawUsableOutput` true and the terminal makes the stream valid. The host receives a successful, silently incomplete answer. A malformed delta containing tool fragments can similarly lose instructions while the remaining prose is accepted. The existing missing-terminal check cannot detect corruption before a later valid terminal.

Observed guard/tests: `Assets/CoreAiUnity/Tests/EditMode/MeaiOpenAiChatClientSseEditModeTests.cs:1513-1542` verifies missing-terminal EOF and finish-without-DONE; `:1545-1559` verifies a well-formed error event is typed/redacted. Malformed tool argument JSON is deliberately preserved with parse-error markers (`:1928`, `:2090`), which does not cover malformed event JSON that is discarded before accumulation. Invalid non-streaming JSON is separately covered at `:1095-1098`; that is not this mixed-validity stream case.

Fix direction: distinguish harmless non-data/comment/unknown-extension frames from malformed OpenAI data payloads. Surface a typed protocol failure after a malformed expected payload, preserving already emitted text and executed receipts and forbidding replay after commitment. Never put the raw malformed payload in the exception/log.

Regression recommendation: fake streams with valid text → malformed data → DONE, valid text → malformed known delta schema → finish/EOF, and tool fragments around a malformed event. Assert terminal failure rather than clean success, partial output/receipts retained, no secondary request after commitment, and malformed secret-bearing payloads redacted. Retain normal comment/blank/usage and unknown-extension behavior.

### LLM-007 — P2 — Routing publishes success and clears endpoint degradation for a code-only stream failure

Status: new finding; high confidence from the complete outcome-publication chain. Independent of LLM-003: this defect affects routing health/telemetry even without a fallback backend.

Evidence: `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/RoutingLlmClient.cs:214-223` recognizes a nonzero error code and records `faultCode`, but `:231-235` sets `ok=false` and the published error code only when `Error` text is nonempty. On ordinary enumeration completion, `:260-263` publishes the unchanged `ok=true` and `ErrorCode=None`. `:395-398` then reports a successful route result (`None`) to the registry, clearing a prior degradation. The valid default-adapter code-only failure shape is established at `Assets/CoreAI/Runtime/Core/Features/Orchestration/ILlmClient.cs:955-965`.

Trigger and impact: a provider emits a terminal code-only `AuthExpired`, `PaymentRequired` or `BackendUnavailable` chunk while the caller token is live, and the consumer drains the routing stream (directly, or through the retry layer after visible output has already committed it). The consumer sees a failure chunk but `LlmRequestCompleted` says success and the endpoint health is marked recovered. Usage publication may also mark the failed turn successful. Operators and applications relying on these events receive contradictory results. If an outer wrapper instead disposes routing immediately upon that error, the finalizer at `:272-275` reports `Cancelled` because its outcome code was never set; that branch also loses the provider's classification, although it does not take the successful health-clear branch.

Observed guard/tests: caller-cancellation normalization can synthesize text and avoid this specific branch, but a live caller retains the empty text. `Assets/CoreAiUnity/Tests/EditMode/RoutingLlmClientEditModeTests.cs:379-401` uses a nonempty timeout message; `:490` tests a thrown endpoint failure. `Assets/CoreAiUnity/Tests/EditMode/LlmRoutingRegressionEditModeTests.cs:436-483` tests non-streaming health with `Error="401 Unauthorized"`. No inspected case covers code-only streaming health.

Fix direction: use the same code-or-text error predicate for classification and outcome bookkeeping, preserving the provider code when no message was supplied. Keep failure state sticky across subsequent usage/control frames.

Regression recommendation: drain a stream containing a code-only terminal failure through a real routing wrapper with capturing publishers and a fake registry. Assert one failed completion event with the correct code, no success/health-clear report, and failed usage attribution. Repeat against a ready runtime endpoint to verify the snapshot becomes/remains degraded; cover null/empty error text and a trailing usage frame.

## Hypotheses and missing validation

Source/doc searches do not establish runtime behavior or a passing platform gate. The sections above are source-confirmed defects except for the narrowly described actual-JavaScript probe.

- Additional memory-hardening candidate: `Assets/CoreAI/Runtime/Core/Features/Llm/MeaiOpenAiChatClient.cs:769-795` has no line-length ceiling and its pending tool argument buffer at `:3058` also grows without a visible-size bound. This needs an agreed protocol/argument budget and adversarial-size validation; no extra prioritized finding or measured allocation claim is assigned here.
- Tagless leading reasoning remains ambiguous: the orphan-close filter suppresses a prefix only while no text has been published; plain initial text can already have been emitted before a later `</think>`. Existing tests intentionally preserve text after visible output. An explicit endpoint/dialect contract is needed before proposing a fail-closed change that could hide ordinary answers. Split explicit `<think>` tags and `reasoning_content` use separate guarded paths; no new confirmed reasoning leak is claimed from searches alone.
- No new idle-vs-total defect accepted in the reviewed HTTP budget: it tracks semantic response wait independently of total duration, excludes consumer waiting from the former, and marks configured deadline expiry to suppress restart. The outer `TimeoutLlmClientDecorator` intentionally uses idle semantics for streaming; verifying multi-roundtrip deadline expectations remains a test/design check, not a demonstrated bug here.
- Existing TODO, not new findings: `TODO.md:3175-3178` tracks production circuit-breaker wiring and a longer fallback chain; `:3203-3205` tracks per-profile limits/fallback and hot-swap consistency. `:3323` still lists separate SSE idle timeout despite the newer response-budget implementation; treat that entry as a reconciliation task, not evidence of a missing implementation. The 403 diagnostic-body behavior is explicitly recorded at `TODO.md:2859`; it is not reported as a new redaction regression.

## Checks actually run

- `git status --short`: exit 0; reported the preexisting ETFX `.meta` and untracked audit directory. No source changes observed at intake.
- `git rev-parse HEAD` and `git rev-parse origin/main`: exit 0; both returned the audited commit above. No fetch performed.
- Read-only `Get-Content`, `rg --files` and targeted `rg -n` searches: completed; source inspection only.
- `git diff --check -- dev-docs/audits/2026-10-23/03-core-providers-streaming.md`: exit 0, no output. The report is untracked, so this does not inspect its content; a direct whitespace/evidence-path check is also recorded below after final editing.
- Direct Python report check (read text, detect trailing whitespace, resolve every full `Assets/...cs:line` evidence reference and compare it with source length): exit 0; `REPORT_CHECK lines=221 findings=7 issues=0`. This is document validation, not C# execution or a test pass.
- `git status --short -- 'dev-docs/audits/2026-10-23/03-core-providers-streaming.md' 'Assets/Epic Toon FX/Upgrade/ETFX 2019.4.24f1 URP Upgrade.unitypackage.meta'`: exit 0; both paths remain untracked. No git mutation performed.
- Actual-javascript in-memory probe, PowerShell here-string piped to `node`: exit 0; exact output recorded under LLM-004. The executable body follows; it reads the jslib and uses no network, files for output, services, model, Unity, or C# compilation.

```javascript
const fs = require('fs');
const vm = require('vm');
let source = fs.readFileSync('Assets/CoreAiUnity/Runtime/Plugins/WebGL/CoreAiSseFetch.jslib', 'utf8');
source = source.replace(/\{\{\{\s*makeDynCall\('[^']+',\s*'([^']+)'\)\s*\}\}\}/g, '$1');
const timers = [];
const library = {};
let reads = 0, delivered = 0, bytes = 0, done = 0, errors = 0;
const sandbox = {
  LibraryManager: { library }, mergeInto: Object.assign,
  CoreAiSseFetchState: { controllers: {}, abortReasons: {} },
  UTF8ToString: value => value, stringToNewUTF8: value => value, _free: () => {},
  AbortController, TextDecoder, Uint8Array, console,
  setTimeout: fn => { timers.push(fn); return timers.length; }, clearTimeout: () => {},
  fetch: () => Promise.resolve({ status: 200, ok: true, headers: { forEach() {} },
    body: { getReader: () => ({ read: () => Promise.resolve(++reads <= 512
      ? { value: new Uint8Array(1024).fill(65), done: false } : { done: true }) }) } })
};
vm.runInNewContext(source, sandbox);
library.CoreAi_FetchSseOpen('https://example.invalid/v1/chat/completions', '{}', '', 0, 'omit', 1,
  () => {}, (id, text) => { delivered++; bytes += text.length; }, () => { done++; }, () => { errors++; });
(async () => {
  for (let tick = 0; tick < 1100 && done === 0 && errors === 0; tick++) {
    await Promise.resolve(); await Promise.resolve();
    if (timers.length) timers.shift()();
  }
  console.log(JSON.stringify({ probe: 'actual jslib with in-memory fetch and callbacks; no C# reads', reads, delivered, bytes, done, errors }));
  if (delivered !== 512 || bytes !== 524288 || done !== 1 || errors !== 0) process.exitCode = 1;
})();
```

Navigation-only failures: initial missing-file lookups, literal Windows glob arguments and an overlong line-view range produced path/null-entry errors. Corrected paths and bounded ranges were used afterward. These were not runtime checks or pass evidence.

## Checks not run and root handoff

No compilation, NUnit, Unity EditMode/PlayMode, WebGL browser, device, live provider, fault-injection integration, throughput, allocation or memory-load checks run. No source/assets changed, regressions implemented, TODO updated, tests added, git staged/committed/fetched/checked out, or Unity/server/model operations performed.

Suggested focused checks for root, sequentially and with shared output under root's control:

```powershell
dotnet test tools/portable/Tests/CoreAI.Portable.Tests.csproj -c Release --filter "FullyQualifiedName~MeaiOpenAiChatClientSseEditModeTests|FullyQualifiedName~RetryingStreamingLlmClientDecoratorEditModeTests|FullyQualifiedName~CircuitBreakerLlmClientDecoratorEditModeTests|FullyQualifiedName~TimeoutLlmClientDecoratorEditModeTests|FullyQualifiedName~TransportTimeoutClassificationEditModeTests|FullyQualifiedName~LlmClientDecoratorContractEditModeTests"
node Assets/CoreAiUnity/Tests/Node~/fetch_sse_jslib_test.js
```

Expected baseline result is zero failed tests/checks; that expectation is not an observed pass and existing fixtures do not reproduce all findings. After implementing the recommended regressions, root should coordinate the Source/Tests compile gate and the Unity-only fixtures `RefreshOnUnauthorizedDecoratorEditModeTests`, `RetryFallbackToolTraceSuppressionEditModeTests`, `ResilienceFeaturesEditModeTests`, `PermanentLlmFailureEditModeTests`, `LlmClientRegistryLeaseEditModeTests`, `RoutingLlmClientEditModeTests`, `LlmRoutingRegressionEditModeTests`, `MeaiStreamingToolCallEditModeTests`, `RuntimeEndpointRequestSettingsEditModeTests`, and `LlmConfigurationDefectsEditModeTests`. Do not treat a zero-match portable filter as verification of these unlinked Unity fixtures.

Required later platform validation: WebGL player bridge cancellation/disposal, a gated slow-reader/backlog case, no-SSE fallback, native LLMUnity activation/replacement/drain, Android/IL2CPP HTTP cancellation, and adversarial malformed/large responses. Actual frame time, allocation growth, provider reliability and model quality remain unmeasured.
