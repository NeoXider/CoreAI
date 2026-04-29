# CoreAI LLM Routing

`CoreAI.Core` owns the portable routing and policy model. Unity, servers, and other hosts adapt these contracts to concrete clients.

## Execution Modes

- `LocalModel` — local model adapter, such as LLMUnity or a future non-Unity local runtime.
- `ClientOwnedApi` — direct OpenAI-compatible API with a key owned by the user or application developer.
- `ClientLimited` — client path with local or server-enforced request/prompt limits.
- `ServerManagedApi` — production backend/proxy owns provider keys, subscriptions, quotas, model allowlists, usage, and audit.
- `Offline` / `Stub` — deterministic fallback for tests and demos.

## Portable Contracts

- `LlmRouteProfile` describes a profile id, execution mode, model alias, context window, response cap, and capabilities.
- `LlmRouteRule` maps role patterns to profile ids. Exact role ids, prefix patterns ending with `*`, and `*` wildcard are supported.
- `LlmRouteTable` stores profiles and rules and validates duplicate/missing profile references.
- `ILlmRouteResolver` resolves an agent role to a route profile.
- `ILlmClientRegistry` is the portable role-to-client registry contract used by host adapters.
- `LlmProviderError` maps stable backend codes such as `quota_exceeded`, `subscription_required`, `model_not_allowed`, and `rate_limited` to `LlmErrorCode`.
- `LlmUsageRecord` and `ILlmUsageSink` provide portable usage accounting.
- `ILlmEntitlementPolicy` and `LlmEntitlementDecision` model subscription, quota, model allowlist, and rate-limit checks.
- `ILlmAuthContextProvider` exposes auth/session context for server-managed routes.

## Runtime Policy Integration

Lesson and practice orchestrators can keep routing portable while adding per-turn policy:

- `AgentMemoryPolicy.SetRuntimeContextProvider(roleId, provider)` injects role-specific runtime context before each request. Per-role context is appended before global `IAiPromptContextProvider` sections.
- `AiTaskRequest.AllowedToolNames` narrows the role's tools for the current lesson slot: **`null`** = offer all registered tools; **empty array** = offer no tools; **non-empty** = allowlist only.
- `AiTaskRequest.ForcedToolMode = None` sends no tools for theory/chat-only turns.
- `ScriptedLlmClient`, `ILlmToolCallHistory`, `LlmToolResultEnvelope`, and `IAgentTurnTraceSink` support deterministic orchestration tests without network/model dependencies.

## Host Boundary

`CoreAI.Core` does not create HTTP clients, read Unity assets, or know about VContainer. `CoreAiUnity` converts `LlmRoutingManifest` into `LlmRouteTable`, then uses the portable resolver while still building Unity-specific clients such as LLMUnity, OpenAI-compatible HTTP, client-limited decorators, and server-managed proxy clients.

Production games such as RedoSchool should put provider keys and quota enforcement behind `ServerManagedApi`. The Unity client sends a user/session token to the backend; the backend performs entitlement, calls the provider, records usage, and returns stable provider errors.
