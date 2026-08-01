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
  `LlmRoleRouteSnapshot` — client, effective profile id, context window, execution mode, `IsRouted` —
  observed together so a concurrent switch cannot mix one endpoint's client with another's metadata.
  `IsRouted == false` marks the reserved `"fallback"` diagnostic (legacy backend, settings-owned window).
- Profile-aware `ILlmClient` capability queries — `SupportsNativeToolCallingForRole(roleId, profileId)` and
  `ResolveContextWindowTokensForRole(roleId, profileId)` — let the orchestrator's tool strategy and context
  budgeting follow the endpoint a request is actually routed to; all built-in decorators forward them.
- `ILlmClientRegistry.ReportRouteFailure(profileId, generation, errorCode, error)` lets routing clients
  report endpoint-level failures (`AuthExpired`, `BackendUnavailable`) so registries surface degraded health
  on the endpoint snapshot instead of keeping a stale Ready appearance; a later success clears it. Reports are
  **generation-stamped** (`LlmRoleRouteSnapshot.Generation`, echoed back from the route the request started
  on): a report whose generation no longer matches the endpoint's current generation is dropped, so a late
  completion from a replaced endpoint cannot mutate its successor's health; a report with generation `0` is
  ignored. Both streaming and non-streaming paths publish the report. Default: no-op for legacy registries.
- `LlmEndpointDescriptor` behavior fields for HTTP endpoints — `MaxTokens`, `ReasoningMode`,
  `ThinkingBudgetTokens`, `ExtraBodyJson` — are validated by `Validate()` and travel with the persisted
  descriptor. `DeriveEndpointSlug` / `EnsureUniqueEndpointId` provide portable endpoint-id derivation.

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

### Provider-specific request body (7.0)

Для произвольных OpenAI-compatible полей используйте публичный AOT/WebGL-safe API на
`OpenAiHttpOptions`, `OpenAiHttpLlmSettings` или `CoreAISettingsAsset`:

```csharp
settings.SetProviderBodyParameter("provider", new JObject
{
    ["order"] = new JArray("cloudflare/fp8"),
    ["allow_fallbacks"] = false
});
settings.SetProviderBodyParameter("session_id", "coreai-teacher-v3");
settings.RemoveProviderBodyParameter("session_id");
```

`JObject`/`JArray` позволяют передавать вложенные структуры без `dynamic` и reflection. Объекты
сериализуются компактно с рекурсивной сортировкой ключей; порядок массивов сохраняется. Передача C# `null`
удаляет ключ, `JValue.CreateNull()` отправляет JSON `null`. Операция атомарна: невалидный исходный JSON,
duplicate property или reserved key оставляет прежний `ExtraBodyJson` без изменений. CoreAI защищает
`model`, `messages`, `temperature`, `max_tokens`, `stream`, `stream_options`, `tools`, `tool_choice`, потому что
эти поля строит transport/orchestrator.

Raw `ExtraBodyJson` остаётся совместимым advanced escape hatch и исторически может переопределить даже
reserved field. Новому application-коду следует использовать safe setters. Тексты исключений safe API не
содержат JSON values/body, поэтому provider secret не попадает в ошибку.

Для OpenRouter `session_id` — непрозрачный id приложения/когорты агента, например `coreai-teacher-v3`, а не
student/user id и не PII. Малое фиксированное шардирование допустимо только как осознанный throughput trade-off.
Комбинация `provider.order: ["cloudflare/fp8"]` и `provider.allow_fallbacks: false` полезна для измерения одного
endpoint, но отключает failover. Физический cache остаётся scoped к provider/account/model/route; CoreAI не
обещает один cache между endpoint-ами.

У прямого DeepSeek API другое поле: `user_id` является границей KV-cache/content-safety/scheduling. CoreAI его
не устанавливает. Не передавайте туда student id или PII; per-student opaque `user_id` также намеренно разделит
provider cache на учеников и уберёт общий прогрев role prefix. Если такая privacy-изоляция не требуется, оставьте
поле пустым; если требуется — выберите стабильную непрозрачную tenant/cohort-гранулярность осознанно.

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
For endpoint setup, runtime switching, and the Hub Settings editor, see
[`RUNTIME_BACKEND_SWITCHING.md`](../../CoreAiUnity/Docs/RUNTIME_BACKEND_SWITCHING.md).

Production games such as RedoSchool should put provider keys and quota enforcement behind `ServerManagedApi`. The Unity client sends a user/session token to the backend; the backend performs entitlement, calls the provider, records usage, and returns stable provider errors.

## Timeouts, HTTP transport, and completion events

- **Orchestrator / chat window:** `ICoreAISettings.LlmRequestTimeoutSeconds` is enforced by `CoreAiChatService` (`CancelAfterSlim`, WebGL-safe) for both streaming and non-streaming chat calls.
- **HTTP per request:** `IOpenAiHttpSettings.RequestTimeoutSeconds` caps a single `MeaiOpenAiChatClient` round-trip. On Unity, `CoreAISettingsAsset.EffectiveHttpRequestTimeoutSeconds` applies `min(RequestTimeoutSeconds, ceil(LlmRequestTimeoutSeconds))` so the transport does not outlive the orchestrator cancel window (see [`MEAI_TOKENS_FACT_VS_ESTIMATE.md`](MEAI_TOKENS_FACT_VS_ESTIMATE.md), §3).
- **Typed timeout vs cancel:** When only the library timeout fires, callers may receive `LlmOperationTimeoutException`. `RoutingLlmClient` publishes `LlmRequestCompleted` with `LlmErrorCode.Timeout` vs `Cancelled`. Transport/internal timeouts — including a header phase that never completes and the timeout decorator's own linked token — surface as typed `Timeout` on both streaming and non-streaming paths; `Cancelled` is reported only when the caller actually cancelled, so timeouts stay retry/fallback-eligible (see [`MEAI_TOKENS_FACT_VS_ESTIMATE.md`](MEAI_TOKENS_FACT_VS_ESTIMATE.md), §4).
- **Retry/fallback replay guard:** a failed completion that carries evidence of an **executed** tool call is not replayed by the HTTP retry loop or the fallback chain — the failure propagates instead of re-mutating the world. Rejected tool calls (duplicate-suppressed, parse errors, unknown tool names, schema/argument-conversion failures) are treated as never-invoked and do not block retries or fallback.
- **Usage accounting:** `LlmUsageRecord` / `ILlmUsageSink` and `LlmUsageReported` (MessagePipe) complement routing; token counts from HTTP usage are described in [`MEAI_TOKENS_FACT_VS_ESTIMATE.md`](MEAI_TOKENS_FACT_VS_ESTIMATE.md).
