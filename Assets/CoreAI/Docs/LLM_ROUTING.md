# CoreAI LLM Routing

`CoreAI.Core` owns the portable routing and policy model. Unity, servers, and other hosts adapt these contracts to concrete clients. **Doc index:** [`README.md`](README.md).

## Execution Modes

`LlmExecutionMode` values:

- `Auto` (default value) — the legacy backend selection rules, kept for existing scenes and serialized assets.
- `LocalModel` — local model adapter, such as LLMUnity or a future non-Unity local runtime.
- `ClientOwnedApi` — direct OpenAI-compatible API with a key owned by the user or application developer.
- `ClientLimited` — OpenAI-compatible API with client-side usage limits or policy checks (`ClientLimitedLlmClientDecorator`).
- `ServerManagedApi` — production backend/proxy owns provider keys, subscriptions, quotas, model allowlists, usage, and audit.
- `Offline` / `Stub` — deterministic fallback for tests and demos.

`ClientLimitedLlmClientDecorator` reserves a slot before the request starts, so concurrent calls
cannot bypass the session limit. A failed attempt returns exactly one slot: this covers exceptions,
cancellation, and stream errors even after a partial response, including errors without an `ErrorCode` and iterator-release
failures. An empty stream and a stream of only `null` items also do not consume the limit. A successful response consumes
a slot; if the consumer simply closed the stream after a partial response, the slot stays taken. Explicit cancellation
by the caller differs from such closing and returns the slot after the inner stream finishes.

The limiter counts requests and does not introduce a second timer: the outer `TimeoutLlmClientDecorator` is responsible
for bounded waiting and observing late completion of a client that ignores cancellation.
The inner iterator is released sequentially after `MoveNextAsync`; it must not be released
in parallel with a still-running read. A standalone limiter requires a cooperative inner
client; if it hangs forever in reading or resource release, an outer timeout boundary is needed.
If reading and release both finish with exceptions, the caller receives the original read exception:
the cleanup error is kept in its `Data[ClientLimitedLlmClientDecorator.StreamDisposeExceptionDataKey]`.
This preserves the failure cause for auth handling and retries without losing cleanup diagnostics.

## Portable Contracts

### Runtime endpoints and agent profiles (5.9)

`ILlmEndpointRegistry` separates three concepts that were previously conflated:

- an **endpoint** is a concrete HTTP, LLMUnity, or Offline provider;
- a **profile** names an endpoint and its fallback policy;
- a **role assignment** selects the profile used by an agent.

Endpoints can be added, updated, activated, kept warm, or removed while other endpoints remain active.
Use `AgentBuilder.WithLlmProfile("profile-id")` for an agent default or
`AiTaskRequest.RoutingProfileId` for a single call. Explicit request selection wins over the agent and
role assignment. Leaving the Chat selector on **Automatic** sends no explicit profile override, so this
normal resolver order remains intact.

The registry is valid with any endpoint count:

- **0 endpoints:** routing continues through the configured legacy/default client (including Offline);
- **1 endpoint:** assign its generated/named profile to a role, or select it explicitly;
- **many endpoints:** different roles and concurrent requests may use different HTTP APIs and LLMUnity hosts.

#### Switching preserves the conversation

Routing is separate from role-keyed agent state. Reassigning a role to another profile, or selecting an
endpoint for one request, changes only the client used by subsequent requests. It does not recreate the
agent or clear its conversation history, long-term memory, registered tools, or policy configuration. The
same role therefore continues the same conversation after an endpoint/provider switch; only the response
backend changes. In-flight requests retain the endpoint generation on which they started.

`Active` controls whether new requests may route to an endpoint. `KeepWarm` may keep an inactive endpoint
loaded and Ready for a later switch, but does not make it routable. Activating a persisted endpoint starts
its readiness sequence again after process restart — but when the execution mode is `Offline`, persisted
Active/KeepWarm endpoints are restored for display/explicit activation only and are **not** auto-activated,
so an Offline restore never boots a local model or HTTP host behind the user's back. HTTP endpoints prefer a successful OpenAI-compatible
`GET {BaseUrl}/models` probe. If that optional route returns `404` or `405`, CoreAI falls back to a minimal
`POST {BaseUrl}/chat/completions` probe and accepts a handler-level response; authentication failures,
missing completion routes, server errors, and network failures still fail readiness. LLMUnity does not expose `/v1/models`: its endpoints first
complete native warmup, then probe `POST /v1/chat/completions`. An HTTP response proves that the local socket
and route accept connections; `401`/`403` remain readiness failures when authentication is configured. A
first request resolved during activation awaits that shared readiness task instead of racing model startup.

Endpoint descriptors, profiles, and role assignments are persisted by the Unity host. Session credentials
are deliberately excluded. For `AddOrUpdateEndpointAsync`, a `null` `sessionApiKey` preserves the existing
in-memory key, an explicit empty string clears it, and a non-empty value replaces it. Persisted
`SecretReference` is resolved through `ILlmEndpointSecretProvider`; the Unity default treats the reference
as an environment-variable name. Applications may inject another provider for a platform keychain or
authenticated backend.

#### Route snapshots, endpoint health, and descriptor behavior fields (5.9)

- `ILlmClientRegistry.ResolveRouteForRole(roleId, explicitProfileId)` returns an atomic
  `LlmRoleRouteSnapshot` — client, effective profile id, context window, `MaxOutputTokens`, execution mode, `IsRouted` —
  observed together so a concurrent switch cannot mix one endpoint's client with another's metadata.
  `IsRouted == false` marks the reserved `"fallback"` diagnostic (legacy backend, settings-owned window).
- Profile-aware `ILlmClient` capability queries — `SupportsNativeToolCallingForRole(roleId, profileId)` and
  `ResolveContextWindowTokensForRole(roleId, profileId)` — let the orchestrator's tool strategy and context
  budgeting follow the endpoint a request is actually routed to; all built-in decorators forward them.
- `ILlmClientRegistry.ReportRouteFailure(profileId, generation, errorCode, error)` lets routing clients
  report endpoint-level failures (`AuthExpired`, `PaymentRequired`, `BackendUnavailable`) so registries surface
  degraded health on the endpoint snapshot instead of keeping a stale Ready appearance; a later success clears
  it. After a caller cancel only the permanent refusals (`AuthExpired`, `PaymentRequired`) are still reported,
  found through the fault's `InnerException` chain when a decorator wrapped it (`LlmCancellation.FindClientException`);
  a `BackendUnavailable` or any transient fault that happened while the caller was stopping is not. Reports are
  **generation-stamped** (`LlmRoleRouteSnapshot.Generation`, echoed back from the route the request started
  on): a report whose generation no longer matches the endpoint's current generation is dropped, so a late
  completion from a replaced endpoint cannot mutate its successor's health; a report with generation `0` is
  ignored. Both streaming and non-streaming paths publish the report. Default: no-op for legacy registries.
- `LlmEndpointDescriptor` request settings — nullable `Temperature`, `MaxTokens`, `OverrideMaxTokens`,
  `ReasoningEffort`, `ReasoningDialect`, legacy `ReasoningMode`, `ThinkingBudgetTokens`, and `ExtraBodyJson` —
  reach the HTTP request for both HTTP and LLMUnity endpoints and travel with the persisted descriptor.
  LLMUnity uses its own descriptor's reasoning and provider body; an Auto dialect resolves to `QwenTemplate`.
  `Validate()` rejects an HTTP endpoint with an empty or whitespace model (`HTTP endpoint model is required.`).
  `DeriveEndpointSlug` / `EnsureUniqueEndpointId` provide portable endpoint-id derivation.

- `LlmRouteProfile` describes a profile id, execution mode, model alias, context window, response cap, and capabilities.
- `LlmRouteRule` maps role patterns to profile ids. Exact role ids, prefix patterns ending with `*`, and `*` wildcard are supported.
- `LlmRouteTable` stores profiles and rules and validates duplicate/missing profile references.
- `ILlmRouteResolver` resolves an agent role to a route profile.
- `ILlmClientRegistry` is the portable role-to-client registry contract used by host adapters.
- `LlmProviderError` maps stable backend codes such as `quota_exceeded`, `subscription_required`, `model_not_allowed`, and `rate_limited` to `LlmErrorCode`.
- `LlmUsageRecord` and `ILlmUsageSink` provide portable usage accounting **contracts**. The Unity installer registers the no-op `NullLlmUsageSink`, and no CoreAI pipeline layer calls the sink: record usage in your own adapter (for example from the `LlmUsageReported` message) or on the backend.
- `ILlmEntitlementPolicy` and `LlmEntitlementDecision` provide portable subscription/quota/allowlist **contracts**. The Unity installer registers `AllowAllLlmEntitlementPolicy`, and no client-side decorator enforces entitlement: the backend (`ServerManagedApi`) is the source of truth and surfaces decisions through `LlmErrorCode.QuotaExceeded` / `RateLimited` / `PaymentRequired`.
- `ILlmAuthContextProvider` exposes auth/session context for server-managed routes. Register via `LlmAuthContextRegistry.SetProvider(...)`; `MeaiOpenAiChatClient` reads it on every request and emits `X-Tenant-Id` / `X-User-Id` / `X-Session-Id` headers.
- `LlmRequestContext` (AsyncLocal) carries the per-request idempotency key, role id, and trace id. `MeaiLlmClient` populates a frame on every `CompleteAsync`/`CompleteStreamingAsync`; HTTP transports emit `Idempotency-Key`, `X-Request-Id`, `X-Coreai-Role`. The same key is reused across decorator retries (e.g. `RefreshOnUnauthorizedDecorator`) so the backend can deduplicate without double-billing.
- `IRequestHeaderProvider` (on `IOpenAiHttpSettings.HeaderProvider`) exposes a per-settings hook for additional static headers (defaults to `null` on built-in adapters).

### Provider-specific request body (7.0)

For arbitrary OpenAI-compatible fields, use the public AOT/WebGL-safe API on
`OpenAiHttpOptions`, `OpenAiHttpLlmSettings` or `CoreAISettingsAsset`:

```csharp
settings.SetProviderBodyParameter("provider", new JObject
{
    ["order"] = new JArray("cloudflare/fp8"),
    ["allow_fallbacks"] = false
});
settings.SetProviderBodyParameter("session_id", "coreai-teacher-v3");
settings.RemoveProviderBodyParameter("session_id");
```

`JObject`/`JArray` allow passing nested structures without `dynamic` and reflection. Objects
serialize compactly with recursive key sorting; array order is preserved. Passing C# `null`
removes the key, `JValue.CreateNull()` sends JSON `null`. The operation is atomic: invalid source JSON,
duplicate property or reserved key leaves the previous `ExtraBodyJson` unchanged. CoreAI protects
`model`, `messages`, `stream`, `stream_options`, `tools` and `tool_choice` because these fields define
request structure. Sampling and provider reasoning keys remain writable. At request time, typed settings
own only the fields they actually write: an unset typed temperature or reasoning effort leaves a provider
body value intact. An explicit zero output cap removes both token-cap spellings; a positive typed cap
removes the competing spelling. Conflicts produce one warning per HTTP client. These rules apply equally
to streaming and non-streaming requests.

Raw `ExtraBodyJson` remains an advanced escape hatch for fields without a typed override. New application code should use the safe setters. Safe API exception texts do not
contain JSON values/body, so a provider secret never leaks into an error.

For OpenRouter, `session_id` is an opaque application/agent-cohort id, for example `coreai-teacher-v3`, not
a student/user id and not PII. Small fixed sharding is acceptable only as a deliberate throughput trade-off.
The combination of `provider.order: ["cloudflare/fp8"]` and `provider.allow_fallbacks: false` is useful for measuring a single
endpoint, but disables failover. The physical cache stays scoped to provider/account/model/route; CoreAI does not
promise one shared cache across endpoints.

The direct DeepSeek API has a different field: `user_id` is a KV-cache/content-safety/scheduling boundary. CoreAI does
not set it. Do not pass a student id or PII there; a per-student opaque `user_id` would also deliberately split
the provider cache by student and remove the shared role-prefix warmup. If such privacy isolation is not needed, leave
the field empty; if it is needed, choose a stable opaque tenant/cohort granularity deliberately.

## Runtime Policy Integration

Lesson and practice orchestrators can keep routing portable while adding per-turn policy:

- `AgentMemoryPolicy.SetRuntimeContextProvider(roleId, provider)` injects role-specific runtime context before each request. Per-role context is appended before global `IAiPromptContextProvider` sections.
- `AiTaskRequest.AllowedToolNames` narrows the role's tools for the current lesson slot: **`null`** = offer all registered tools; **empty array** = offer no tools; **non-empty** = allowlist only.
- `AiTaskRequest.ForcedToolMode = None` sends no tools for theory/chat-only turns.
- `ScriptedLlmClient`, `ILlmToolCallHistory`, `LlmToolResultEnvelope`, and `IAgentTurnTraceSink` support deterministic orchestration tests without network/model dependencies.

## Host Boundary

`CoreAI.Core` ships the portable OpenAI-compatible HTTP path: `IOpenAiHttpTransport`,
`HttpClientOpenAiTransport`, and `MeaiOpenAiChatClient` (compiled when `COREAI_LLM` is defined; the
portable DLL defines it). It also ships `ILlmEndpointReadinessProbe`, `LlmEndpointReadinessRequest`, the
shared status policy, and `HttpClientOpenAiReadinessProbe` (also `COREAI_LLM`). A plain .NET host can therefore construct both the chat client and endpoint
readiness pipeline directly without Unity. `ModelsThenCompletions` checks `/models` first and falls back to
the completion handler only when `/models` is unsupported (`404`/`405`); `CompletionsOnly` is available for
embedded servers that expose no models route. Loopback URLs bypass the system proxy so local model sockets
remain local; external URLs retain the platform proxy policy.
Readiness probes do not follow redirects and never treat `3xx` as a ready handler, preventing credentials
from crossing origins during endpoint activation.

`CoreAiUnity` owns Unity runtime integration: endpoint registry lifecycle and persistence, `CoreAISettingsAsset`,
LLMUnity native model startup/readiness, WebGL `UnityWebRequest`/Fetch transports, Hub/Chat UI, and VContainer
registration. It supplies `UnityWebRequestOpenAiReadinessProbe` for Unity/WebGL and injects it into endpoint
activation. Search/configuration of `LLMAgent`, `LLM.WaitUntilReady()`, host leases, and llama.cpp unload stay
strictly Unity-owned; the HTTP readiness contract and policy do not depend on Unity.
For endpoint setup, runtime switching, and the Hub Settings editor, see
[`RUNTIME_BACKEND_SWITCHING.md`](../../CoreAiUnity/Docs/RUNTIME_BACKEND_SWITCHING.md).

A production host should put provider keys and quota enforcement behind `ServerManagedApi`. The Unity client sends a user/session token to the backend; the backend performs entitlement, calls the provider, records usage, and returns stable provider errors.

## Timeouts, HTTP transport, and completion events

- **Orchestrator / chat window:** `ICoreAISettings.LlmRequestTimeoutSeconds` is enforced by `CoreAiChatService` (`CancelAfterSlim`, WebGL-safe) for both streaming and non-streaming chat calls, as an idle (no-progress) budget.
- **Host deadline:** a host that runs its own deadline passes it separately through the three-argument overloads `CoreAiChatService.SendMessageAsync(AiTaskRequest, CancellationToken ct, CancellationToken deadlineToken)`, `SendMessageResultAsync(request, ct, deadlineToken)` and `SendMessageStreamingAsync(request, ct, deadlineToken)`; `CoreAiChatPanel` forwards `CoreAiChatExternalSubmitOptions.DeadlineCancellationToken` the same way. The request then carries `AiTaskRequest.CallerCancellationToken` (the caller's token only) and `AiTaskRequest.DeadlineCancellationToken` (the idle deadline joined with the host deadline). A deadline that fires while the caller is still waiting ends the turn as `LlmOperationTimeoutException` on both paths and is recorded as `AiLlmCompletionOutcome.DeadlineCancellation`; a caller stop stays a cancellation even if the deadline fired too. The two-argument overloads are unchanged.
- **HTTP per request:** `IOpenAiHttpSettings.RequestTimeoutSeconds` caps a single `MeaiOpenAiChatClient` round-trip. On Unity, `CoreAISettingsAsset.EffectiveHttpRequestTimeoutSeconds` applies `min(RequestTimeoutSeconds, ceil(LlmRequestTimeoutSeconds))` so the transport does not outlive the orchestrator cancel window (see [`MEAI_TOKENS_FACT_VS_ESTIMATE.md`](MEAI_TOKENS_FACT_VS_ESTIMATE.md), §3).
- **Typed timeout vs cancel:** When only the library timeout fires, callers may receive `LlmOperationTimeoutException`. `RoutingLlmClient` publishes `LlmRequestCompleted` with `LlmErrorCode.Timeout` vs `Cancelled`. Transport/internal timeouts — including a header phase that never completes and the timeout decorator's own linked token — surface as typed `Timeout` on both streaming and non-streaming paths; a timeout is never reported as `Cancelled` while the caller is still waiting, so timeouts stay retry/fallback-eligible (see [`MEAI_TOKENS_FACT_VS_ESTIMATE.md`](MEAI_TOKENS_FACT_VS_ESTIMATE.md), §4). The rule lives in one place, `LlmCancellation` (since 7.44.0): a cancelled caller token wins over everything — any exception observed after the caller cancelled, typed or untyped, timeout or not, is `Cancelled` for the user-facing code, the completion metric and the circuit breaker; with the caller alive, `LlmOperationTimeoutException` (also inside an `AggregateException`) is a timeout and any other `OperationCanceledException` is a cancellation (someone else stopped the work: `CoreAi.StopAgent`, a cancellation scope, a disposed orchestrator). `AiOrchestrator` (both paths), `QueuedAiOrchestrator`, `RoutingLlmClient` (exceptions, results and stream chunks alike), `LoggingLlmClientDecorator` (thrown faults), `CircuitBreakerLlmClientDecorator` (no failure is recorded after a caller cancel; the half-open probe slot is released), `RetryingStreamingLlmClientDecorator`, `FallbackLlmClientDecorator`, `CoreAiChatService` and `CoreAiChatPanel` classify through it. A layer that has to throw for a fault it observed after the cancel throws `OperationCanceledException` with the fault as `InnerException` (`LlmCancellation.WrapAsCancellation`; `LlmCancellation.FindClientException` finds it again); a failed result or terminal chunk that arrives after the cancel — whatever its error code — is re-issued as a `Cancelled` copy with the error text `cancelled` (`ClassifyCode`, `LlmCompletionResult.WithError` / `LlmStreamChunk.WithError`, copy-on-write — inner clients may reuse instances). With the caller alive, a fault that is neither a timeout nor a cancellation is classified by the layer itself (its typed code, otherwise `ProviderError`). `RetryingStreamingLlmClientDecorator`, `LoggingLlmClientDecorator` and `FallbackLlmClientDecorator` never retry or fall back after a caller cancel. `CircuitBreakerLlmClientDecorator` records nothing after a caller cancel — thrown faults and `Timeout`/`ProviderError` chunks alike — while a transport timeout with the caller still waiting (a typed `LlmClientException` with `Timeout`) still counts; the default Unity pipeline does not compose the breaker, hosts add it. `AiOrchestrator` records a terminal `Timeout` as `DeadlineCancellation` however it arrived — thrown, or as a `Timeout` chunk from `TimeoutLlmClientDecorator` on the streaming path — both when the stream opens and inside the tool loop. Endpoint health is the one consumer that still looks at the fault itself (see `ReportRouteFailure` above): after a caller cancel it still reports the permanent refusals `AuthExpired` and `PaymentRequired`, and nothing else. A host that runs its own deadline keeps it on a separate token and uses the `deadlineToken` overloads (see **Host deadline** above).
- **Retry/fallback replay guard:** a failed completion that carries evidence of an **executed** tool call is not replayed by the HTTP retry loop or the fallback chain — the failure propagates instead of re-mutating the world. Rejected tool calls (duplicate-suppressed, parse errors, unknown tool names, schema/argument-conversion failures) are treated as never-invoked and do not block retries or fallback.
- **Usage accounting:** `LlmUsageRecord` / `ILlmUsageSink` and `LlmUsageReported` (MessagePipe) complement routing; token counts from HTTP usage are described in [`MEAI_TOKENS_FACT_VS_ESTIMATE.md`](MEAI_TOKENS_FACT_VS_ESTIMATE.md).

## Request sampling and reasoning

Request settings apply in this order: per-call `AiTaskRequest`, per-agent `AgentBuilder`, selected
endpoint, then global settings. Null values inherit; reasoning effort `Default` inherits. An explicit
output-token cap of zero means unlimited and prevents lower layers from supplying a cap. A route
profile's `MaxOutputTokens` supplies the endpoint default when neither call nor agent sets a cap.
Endpoint `Temperature = null` inherits, while a numeric value overrides. `OverrideMaxTokens` distinguishes
an explicit zero endpoint cap from inheritance; a positive `MaxTokens` also enables the endpoint override.

```csharp
AgentConfig config = new AgentBuilder("planner")
    .WithReasoning(LlmReasoningEffort.High, budgetTokens: 8192)
    .WithTemperature(0.2f)
    .WithSampling(new LlmSamplingOptions { TopP = 0.9f, Seed = 42 })
    .Build();
AiTaskRequest task = new AiTaskRequest
{
    RoleId = "planner",
    Reasoning = new LlmReasoningOptions(LlmReasoningEffort.None),
    Temperature = 0.1f,
    Sampling = new LlmSamplingOptions { Stop = new[] { "END" } }
};
```

`IOpenAiHttpSettings.ReasoningEffort` configures the endpoint default. `ReasoningDialect` selects
`OpenAi`, `OpenRouter`, `QwenTemplate`, `AnthropicCompat`, `GeminiCompat`, `PassthroughEffort`, or `Off`.
`Auto` recognizes OpenAI/Azure OpenAI, OpenRouter, Gemini, Anthropic, DashScope and named LLMUnity
endpoints. Unknown endpoints use the effort string unchanged. Use `QwenTemplate` explicitly for a
local Qwen server whose URL does not identify its implementation.

| Dialect | Reasoning fields written when reasoning is configured | Output cap |
|---|---|---|
| `OpenAi` | `reasoning_effort`: lower-case effort, including `none`, `minimal`, and `xhigh` | `max_completion_tokens` |
| `OpenRouter` | `reasoning.effort`, plus `reasoning.max_tokens` for a positive thinking budget | `max_tokens` |
| `QwenTemplate` | `enable_thinking` and `chat_template_kwargs.enable_thinking`; optional positive `thinking_budget`. `None` sets both flags to false. | `max_tokens` |
| `AnthropicCompat` | `thinking.type` = `disabled` for `None`, otherwise `enabled` with `budget_tokens` (at least 1024). Default budgets: Minimal/Low 1024, Medium 4096, High 8192, XHigh 16384. | `max_tokens` |
| `GeminiCompat` | `reasoning_effort`; Minimal maps to `low`, XHigh to `high` | `max_tokens` |
| `PassthroughEffort` | `reasoning_effort` unchanged, lower-case | `max_tokens` |
| `Off` | No typed reasoning fields | `max_tokens` |

Without a typed effort or legacy reasoning controls, no typed reasoning fields are added and the cap
uses `max_tokens`. `Off` leaves any manually supplied provider reasoning fields untouched.
Legacy `ReasoningMode` profiles with Auto retain the existing Qwen bytes, including on recognized
hosts; selecting a typed effort opts into dialect detection. Context compaction requests disable
reasoning by default.

MEAI callers can set `ChatOptions.TopP`, `StopSequences`, `Seed`, `ResponseFormat`,
`FrequencyPenalty` and `PresencePenalty`; both HTTP paths serialize them. Reasoning travels in
`ChatOptions.AdditionalProperties["coreai.reasoning"]` as `LlmReasoningOptions`. Request-start
telemetry and the `LLM >` log include resolved effort, dialect, temperature and output-token cap;
reported reasoning-token usage remains separate from visible output.
