# CoreAI LLM Routing

`CoreAI.Core` owns the portable routing and policy model. Unity, servers, and other hosts adapt these contracts to concrete clients. **Doc index:** [`README.md`](README.md).

## Execution Modes

- `LocalModel` — local model adapter, such as LLMUnity or a future non-Unity local runtime.
- `ClientOwnedApi` — direct OpenAI-compatible API with a key owned by the user or application developer.
- `ClientLimited` — client path with local or server-enforced request/prompt limits.
- `ServerManagedApi` — production backend/proxy owns provider keys, subscriptions, quotas, model allowlists, usage, and audit.
- `Offline` / `Stub` — deterministic fallback for tests and demos.

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

`Active` controls whether new requests may route to an endpoint. `KeepWarm` may keep an inactive endpoint
loaded and Ready for a later switch, but does not make it routable. Activating a persisted endpoint starts
its readiness sequence again after process restart. HTTP endpoints prefer a successful OpenAI-compatible
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

- `LlmRouteProfile` describes a profile id, execution mode, model alias, context window, response cap, and capabilities.
- `LlmRouteRule` maps role patterns to profile ids. Exact role ids, prefix patterns ending with `*`, and `*` wildcard are supported.
- `LlmRouteTable` stores profiles and rules and validates duplicate/missing profile references.
- `ILlmRouteResolver` resolves an agent role to a route profile.
- `ILlmClientRegistry` is the portable role-to-client registry contract used by host adapters.
- `LlmProviderError` maps stable backend codes such as `quota_exceeded`, `subscription_required`, `model_not_allowed`, and `rate_limited` to `LlmErrorCode`.
- `LlmUsageRecord` and `ILlmUsageSink` provide portable usage accounting **contracts**. Free CoreAI does not register a default sink — use a custom adapter or rely on the backend to record usage. CoreAiPro ships a backend `BackendUsageSink` adapter.
- `ILlmEntitlementPolicy` and `LlmEntitlementDecision` provide portable subscription/quota/allowlist **contracts**. Free CoreAI does not run a client-side entitlement decorator — the backend (`ServerManagedApi`) is the source of truth and surfaces decisions through `LlmErrorCode.QuotaExceeded` / `RateLimited` / etc. CoreAiPro ships a backend `BackendEntitlementPolicy` adapter that calls `GET /entitlements`.
- `ILlmAuthContextProvider` exposes auth/session context for server-managed routes. Register via `LlmAuthContextRegistry.SetProvider(...)`; `MeaiOpenAiChatClient` reads it on every request and emits `X-Tenant-Id` / `X-User-Id` / `X-Session-Id` headers.
- `LlmRequestContext` (AsyncLocal) carries the per-request idempotency key, role id, and trace id. `MeaiLlmClient` populates a frame on every `CompleteAsync`/`CompleteStreamingAsync`; HTTP transports emit `Idempotency-Key`, `X-Request-Id`, `X-Coreai-Role`. The same key is reused across decorator retries (e.g. `RefreshOnUnauthorizedDecorator`) so the backend can deduplicate without double-billing.
- `IRequestHeaderProvider` (on `IOpenAiHttpSettings.HeaderProvider`) exposes a per-settings hook for additional static headers (defaults to `null` on built-in adapters).

## Runtime Policy Integration

Lesson and practice orchestrators can keep routing portable while adding per-turn policy:

- `AgentMemoryPolicy.SetRuntimeContextProvider(roleId, provider)` injects role-specific runtime context before each request. Per-role context is appended before global `IAiPromptContextProvider` sections.
- `AiTaskRequest.AllowedToolNames` narrows the role's tools for the current lesson slot: **`null`** = offer all registered tools; **empty array** = offer no tools; **non-empty** = allowlist only.
- `AiTaskRequest.ForcedToolMode = None` sends no tools for theory/chat-only turns.
- `ScriptedLlmClient`, `ILlmToolCallHistory`, `LlmToolResultEnvelope`, and `IAgentTurnTraceSink` support deterministic orchestration tests without network/model dependencies.

## Host Boundary

`CoreAI.Core` ships the portable OpenAI-compatible HTTP path: `IOpenAiHttpTransport`,
`HttpClientOpenAiTransport`, and `MeaiOpenAiChatClient`. It also ships
`ILlmEndpointReadinessProbe`, `LlmEndpointReadinessRequest`, the shared status policy, and
`HttpClientOpenAiReadinessProbe`. A plain .NET host can therefore construct both the chat client and endpoint
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

Production games such as RedoSchool should put provider keys and quota enforcement behind `ServerManagedApi`. The Unity client sends a user/session token to the backend; the backend performs entitlement, calls the provider, records usage, and returns stable provider errors.

## Timeouts, HTTP transport, and completion events

- **Orchestrator / chat window:** `ICoreAISettings.LlmRequestTimeoutSeconds` is enforced by `CoreAiChatService` (`CancelAfterSlim`, WebGL-safe) for both streaming and non-streaming chat calls.
- **HTTP per request:** `IOpenAiHttpSettings.RequestTimeoutSeconds` caps a single `MeaiOpenAiChatClient` round-trip. On Unity, `CoreAISettingsAsset.EffectiveHttpRequestTimeoutSeconds` applies `min(RequestTimeoutSeconds, ceil(LlmRequestTimeoutSeconds))` so the transport does not outlive the orchestrator cancel window (see [`MEAI_TOKENS_FACT_VS_ESTIMATE.md`](MEAI_TOKENS_FACT_VS_ESTIMATE.md), §3).
- **Typed timeout vs cancel:** When only the library timeout fires, callers may receive `LlmOperationTimeoutException`. `RoutingLlmClient` publishes `LlmRequestCompleted` with `LlmErrorCode.Timeout` vs `Cancelled`. Transport/internal timeouts — including a header phase that never completes and the timeout decorator's own linked token — surface as typed `Timeout` on both streaming and non-streaming paths; `Cancelled` is reported only when the caller actually cancelled, so timeouts stay retry/fallback-eligible (see [`MEAI_TOKENS_FACT_VS_ESTIMATE.md`](MEAI_TOKENS_FACT_VS_ESTIMATE.md), §4).
- **Retry/fallback replay guard:** a failed completion that carries evidence of an **executed** tool call is not replayed by the HTTP retry loop or the fallback chain — the failure propagates instead of re-mutating the world. Rejected tool calls (duplicate-suppressed, parse errors, unknown tool names, schema/argument-conversion failures) are treated as never-invoked and do not block retries or fallback.
- **Usage accounting:** `LlmUsageRecord` / `ILlmUsageSink` and `LlmUsageReported` (MessagePipe) complement routing; token counts from HTTP usage are described in [`MEAI_TOKENS_FACT_VS_ESTIMATE.md`](MEAI_TOKENS_FACT_VS_ESTIMATE.md).
