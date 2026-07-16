# Audit: Dynamic API / Multi-Endpoint LLM Routing (unreleased 5.9.0)

Date: 2026-07-16. Scope: commits `222e6eae` (runtime multi-endpoint routing), `92681445` (portable readiness probes), `fa37a523` (Qwen spell tool hardening). First-party code only. No code was modified.

## Scope & goal alignment

The stated product intent: an agent can switch API/endpoint at runtime, keeping conversation history, memory, and tool capabilities, with reusable endpoint presets usable from both the Hub UI and code.

What was reviewed:

- `Assets/CoreAI/Docs/LLM_ROUTING.md`
- `Assets/CoreAI/Runtime/Core/Features/LlmRouting/LlmEndpointContracts.cs`, `ILlmClientRegistry.cs`, `LlmEndpointReadiness.cs`
- `Assets/CoreAI/Runtime/Core/Features/Llm/HttpClientOpenAiTransport.cs`, `HttpClientOpenAiReadinessProbe.cs`
- `Assets/CoreAI/Runtime/Core/Features/Orchestration/AiOrchestrator.cs`, `AiTaskRequest.cs`
- `Assets/CoreAI/Runtime/Core/Features/AgentMemory/AgentBuilder.cs`, `AgentConfigExtensions.cs`
- `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/LlmClientRegistry.cs`, `LlmEndpointClientFactory.cs`, `LlmEndpointRegistryPersistence.cs`, `RoutingLlmClient.cs`, `UnityWebRequestOpenAiReadinessProbe.cs`
- `Assets/CoreAiUnity/Runtime/Source/Composition/LlmPipelineInstaller.cs`, `LlmUnityAutostartEntryPoint.cs`
- `Assets/CoreAiUnity/Runtime/Source/Features/Chat/CoreAiRoutingUiController.cs`, `CoreAiChatPanel.cs`
- `Assets/CoreAIHub/Runtime/HubSettingsPage.cs`, `HubChatPage.cs`

High-level verdict: the architecture (portable contracts in CoreAI.Core, Unity registry with generation-based lifecycle, drain-based removal, session-key-free persistence) is well designed, and the core promise "history/memory survive an endpoint switch" holds because history, memory, and tool registrations are keyed by role, not by endpoint. However, the request pipeline was only partially taught about explicit profiles: tool-calling strategy and context budgeting still resolve role-only, and the retry path re-feeds the routing sentinel back into the resolver, which breaks retries in the default (no runtime endpoints) configuration.

## Confirmed problems

### 1. HIGH — Retry attempts break when the resolved profile id is fed back as an explicit profile (`"fallback"` sentinel)

- Files: `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/RoutingLlmClient.cs:188-191`, `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/LlmClientRegistry.cs:494-506` and `:883-899`, `Assets/CoreAI/Runtime/Core/Features/Llm/LoggingLlmClientDecorator.cs:149-159`, `Assets/CoreAI/Runtime/Core/Features/Llm/RetryingStreamingLlmClientDecorator.cs:86-93`.
- Mechanism: `RoutingLlmClient.Prepare` mutates the request in place:

  ```csharp
  string requestedProfile = request.RoutingProfileId;
  ILlmClient inner = _registry.ResolveClientForRole(request.AgentRoleId, requestedProfile);
  request.RoutingProfileId = _registry.ResolveProfileIdForRole(request.AgentRoleId, requestedProfile);
  ```

  When no runtime profile matches (the default install: no endpoints, no manifest routing), `ResolveProfileIdForRole` returns the literal sentinel `"fallback"` (`LlmClientRegistry.cs:496`, `:505`). Both retry decorators sit ABOVE `RoutingLlmClient` (`LlmPipelineInstaller.cs:72-94`) and re-invoke the inner client with the SAME request object (`LoggingLlmClientDecorator.cs:159` non-streaming HTTP retry; `RetryingStreamingLlmClientDecorator.cs:93` streaming pre-commit retry). On the retry, `Prepare` treats `"fallback"` as an explicit profile id (`ResolveRuntimeProfileIdLocked` returns any non-empty explicit id verbatim, `LlmClientRegistry.cs:885-889`), no profile named `fallback` exists, and `ResolveClientForRole` returns `RoutingUnavailableClient` (`LlmClientRegistry.cs:372-375`), which fails with `LlmErrorCode.RoutingError`: "LLM routing profile 'fallback' is unavailable."
- Failure scenario: legacy/default routing (the most common configuration, including local llama.cpp/LM Studio), provider returns a retryable 429/5xx or an empty stream → the retry that is supposed to save the turn instead fails deterministically with a routing error. The same pinning also means a request first served by a fallback profile stays pinned to it on retry even if the primary recovered.
- Suggested fix: never write the sentinel back into the request (only assign `request.RoutingProfileId` when a real profile resolved), or make `Prepare` capture the ORIGINAL requested profile on the request (e.g. a separate `RequestedProfileId` input field, per TODO line 324 "make RoutingProfileId an input hint" — the input/output split was collapsed into a single mutable field). Also teach `ResolveRuntimeProfileIdLocked` to ignore explicit ids that are unknown, falling back to normal resolution instead of hard-failing.

### 2. HIGH — Native-vs-text tool-calling strategy ignores the routed profile

- Files: `Assets/CoreAI/Runtime/Core/Features/Orchestration/AiOrchestrator.cs:1410`, `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/RoutingLlmClient.cs:53-57`.
- Code: `bool supportsNativeToolCalling = _llm?.SupportsNativeToolCallingForRole(roleId) == true;` and in the routing client:

  ```csharp
  public bool SupportsNativeToolCallingForRole(string agentRoleId)
  {
      ILlmClient inner = _registry.ResolveClientForRole(agentRoleId);   // no explicit profile
      return inner?.SupportsNativeToolCallingForRole(agentRoleId) == true;
  }
  ```

  There is no overload taking the explicit profile, and the orchestrator has `task.RoutingProfileId` in scope at `AppendToolContract` but cannot pass it.
- Failure scenario: an agent built with `WithLlmProfile("cloud")` (or a per-request `RoutingProfileId`, or the Chat API selector before the role assignment persists) whose ROLE default resolves to a different backend gets the wrong tool contract: text tool-contract prompt appended although the routed endpoint does native `tool_calls` (prompt bloat, duplicated contract, models emitting JSON text instead of tool calls), or — worse — no text contract when routed to a text-parser backend, so tools are silently unusable. This directly contradicts "keeps its capabilities" and the project's memory that 4B-model tool tests are fragile exactly at this boundary.
- Suggested fix: add `SupportsNativeToolCallingForRole(string roleId, string explicitProfileId)` to the routing chain (decorators are pure delegators, easy to thread through) and pass `task.RoutingProfileId` at `AiOrchestrator.cs:1410`. Note also `ActivatingEndpointClient.SupportsNativeToolCallingForRole` (`LlmClientRegistry.cs:118`) returns `Kind != Offline` — optimistic `true` for LLMUnity endpoints whose real client would report text-parser mode.

### 3. HIGH — Prompt/history budgeting ignores the routed endpoint's context window; the descriptor's `ContextWindowTokens` never constrains the actual prompt

- Files: `Assets/CoreAI/Runtime/Core/Features/Orchestration/AiOrchestrator.cs:122-124` (`int contextWindowTokens = roleConfig.ContextTokens > 0 ? roleConfig.ContextTokens : _settings.ContextWindowTokens;`), `RoutingLlmClient.cs:49,191`.
- `RoutingLlmClient.Prepare` sets `request.ContextWindowTokens` from the routed endpoint, but that happens AFTER the orchestrator has already compacted/trimmed history against the role/global window, and a repo-wide search shows no downstream consumer that re-trims by `request.ContextWindowTokens` (it feeds diagnostics only; the LLMUnity native `contextSize` is configured separately at activation, `LlmEndpointClientFactory.cs:338`).
- Failure scenario: conversation runs on a 128K cloud endpoint (default `ContextWindowTokens` is 128K), user/agent switches to a local 8K endpoint whose descriptor honestly declares `ContextWindowTokens = 8192` → the next prompt is budgeted against 128K, overflows the 8K server, and is rescued only by the generic context-overflow retry loop (multiplicative 0.75 shrink per retry, bounded by `MaxContextOverflowRetries`) — several wasted round-trips per turn or a hard failure, on every turn. The reverse switch (small → large) never benefits from the larger window.
- Suggested fix: resolve the effective window in `BuildRequestBundleAsync` via `ILlmClientRegistry.ResolveContextWindowForRole(roleId, task.RoutingProfileId)` (the registry API already exists and is profile-aware) as `min(roleConfig/global, endpoint)`.

### 4. MEDIUM — Runtime HTTP endpoints silently drop provider-behavior settings the legacy backend honors

- File: `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/LlmEndpointClientFactory.cs:91-109` (`BuildHttp`).
- The endpoint client is built from a bare `OpenAiHttpOptions { ..., MaxTokens = 0 }`: `ReasoningMode` stays `ProviderDefault`, `ThinkingBudgetTokens` 0, `ExtraBodyJson` empty, `MaxTokens` unlimited — while the legacy client is built from `CoreAISettingsAsset` and honors all of these. `LlmEndpointDescriptor` has no fields for them, and the Hub endpoint editor exposes none.
- Failure scenario: a project that disables reasoning (`ReasoningMode`) to stop 4B "empty response" runaways, or caps `MaxTokens`, loses those guarantees the moment an agent is routed to a runtime endpoint — same model, different behavior, hard to diagnose. This weakens "continues with a new model or provider [with the same capabilities]".
- Suggested fix: either inherit `ReasoningMode`/`ThinkingBudget`/`MaxTokens` from `ICoreAISettings` in `BuildHttp`, or add them to `LlmEndpointDescriptor` (and the Hub editor) so presets are complete.

### 5. MEDIUM — `Ready` is sticky: no health demotion and no per-request profile fallback after activation

- Files: `LlmClientRegistry.cs:901-938` (`TryResolveReadyRuntimeEndpointLocked` — fallback chain consulted only when an endpoint is not Ready/Active), `RoutingLlmClient.cs:74-98` (errors published, never fed back to the registry).
- Once an endpoint reaches `Ready`, nothing ever moves it to `Failed`/degraded: a provider that starts returning 401 (rotated key), 404 (model removed), or connection failures keeps its `Ready` snapshot; the Hub keeps showing "Ready"; `FallbackProfileIds` never engage because they are evaluated only against lifecycle state at resolution time; retries (finding the same Ready endpoint) hammer the same broken endpoint; the legacy dual-backend `FallbackLlmClientDecorator` sits INSIDE the legacy fallback client only (`LlmPipelineInstaller.cs:136-140`), so routed endpoints get no request-level failover at all. TODO.md line 328-329 acknowledges "per-profile fallback + limits" as unresolved, but the readiness/UI staleness is not acknowledged.
- Failure scenario: mid-conversation 401 on the routed endpoint → every turn errors; user sees "Ready" in Hub Settings; the fallback profile they configured is never used.
- Suggested fix: at minimum record last-error/last-success on the endpoint snapshot (Hub already displays `snapshot.Error`); optionally demote to a `Degraded` state on repeated auth/transport failures so the existing fallback-chain resolution starts working, with periodic re-probe.

### 6. MEDIUM — `AssignRoleProfile(rolePattern, …)` accepts and persists patterns that never match

- Files: `LlmClientRegistry.cs:838-853` (parameter named `rolePattern`, stored verbatim), `:883-899` (`ResolveRuntimeProfileIdLocked` matches exact role id then `"*"` only), `LlmEndpointRegistryPersistence.cs:13-17` (`LlmPersistedRoleProfile.RolePattern`).
- `LLM_ROUTING.md` line 53 advertises prefix patterns ("prefix patterns ending with `*`") for `LlmRouteRule`, and the runtime API mirrors the naming — but a runtime assignment `AssignRoleProfile("npc.*", "cloud")` is stored under the literal key `"npc.*"` and never matches role `"npc.merchant"`; no error, no warning.
- Suggested fix: implement prefix matching in `ResolveRuntimeProfileIdLocked` (mirror `LlmRouteResolver`), or rename the parameter to `roleId` and reject `*`-suffixed input.

### 7. MEDIUM — An agent pinned via `WithLlmProfile` to a removed/renamed/typo profile fails hard with no fallback

- Files: `LlmClientRegistry.cs:366-375` (unknown explicit profile → `RoutingUnavailableClient`), `:797-835` (`RemoveProfile` cleans `_runtimeRoleProfiles` only), `AgentBuilder.cs` (`WithLlmProfile` does no validation), `HubSettingsPage.cs:646-650`.
- `RemoveProfile`/`RemoveEndpointAsync` re-point or clear ROLE assignments, but `AgentConfig.LlmProfileId` is a plain string held by game code; after removal every `AskAsync` on that agent sends the stale explicit id and receives `LlmErrorCode.RoutingError` forever. The Hub removal confirmation says "their routing will return to Automatic/default" — true for role assignments, false for builder-pinned agents. Explicit-wins is a defensible design, but there is no diagnostic ("unknown profile 'x'; known profiles: …") and no opt-in soft fallback.
- Suggested fix: log a distinct warning naming the unknown profile on first resolution, and document that `WithLlmProfile` ids are not lifecycle-managed; consider `RoutingUnavailableClient` falling back to role/default resolution when the explicit id is unknown (vs. known-but-down).

### 8. LOW — Routing diagnostics are recorded before/without resolution

- Files: `LoggingLlmClientDecorator.cs:105-108` — `if (_inner is ILlmPreflightAnnotator routing)` is dead in the production wiring because `_inner` is `RetryingStreamingLlmClientDecorator` (does not implement/delegate `ILlmPreflightAnnotator`), so the `LLM > … backend=` line at `:116-118` prints the pre-routing profile (usually empty). `AiOrchestrator.cs:1131` hard-codes `RoutingProfileId = ""` into `AgentTurnTrace`, so turn traces never record which profile served the turn.
- Suggested fix: delegate `ILlmPreflightAnnotator` through `RetryingStreamingLlmClientDecorator`, and copy the resolved profile into the trace from the completion result/request after the call.

## Potential problems / risks (unverified)

### A. MEDIUM (unverified) — `Changed` event and UI handlers have no main-thread guarantee

- Files: `LlmClientRegistry.cs:1101` (`Changed?.Invoke()` from the activation continuation), `CoreAiRoutingUiController.cs:55-59` (pass-through), `HubSettingsPage.cs:417`, `CoreAiChatPanel.cs:684` (handlers mutate UI Toolkit elements directly).
- In the default flows (Hub button, autostart) activation starts on the Unity main thread and `UnityWebRequestOpenAiReadinessProbe`'s `Task.Yield` loop keeps continuations on the Unity SynchronizationContext, so it works. But `ILlmEndpointRegistry` is a public portable API; any caller invoking `AddOrUpdateEndpointAsync` from a thread-pool context (e.g. after `ConfigureAwait(false)`) makes `Changed` fire off the main thread and the Hub/Chat handlers touch UI Toolkit off-thread. Suggested fix: marshal `Changed` to the main thread in `LlmEndpointRegistryUiController` (the Unity-owned seam).

### B. MEDIUM (unverified) — Busy-wait activation awaits can hot-spin off the main thread

- Files: `LlmClientRegistry.cs:142-155` (`AwaitWithoutCancellingSharedActivation`: `while (!activation.IsCompleted) { …; await Task.Yield(); }`), `LlmEndpointClientFactory.cs:297-308` (`WaitUntilReadyAsync`, same pattern).
- On the Unity context this is ~1 iteration/frame (fine); resumed on the thread pool it is a hot yield loop for the entire model-load window (tens of seconds for a 4B GGUF), burning a core. Suggested fix: `Task.WhenAny(activation, Task.Delay(-1, cancellationToken))` — the registry already uses exactly this pattern in `AwaitActivationForCallerAsync` (`LlmClientRegistry.cs:1195-1218`); reuse it.

### C. LOW-MEDIUM (unverified) — Readiness probe false negatives / one-shot activation probe

- Files: `LlmEndpointReadiness.cs:45-52`, `LlmEndpointClientFactory.cs:381-403`, `LlmUnityAutostartEntryPoint.cs` (readiness loop).
- `ShouldTryCompletions` falls back to `POST /chat/completions` only on 404/405; a `/models` route answering 429 (rate limit — plausible on OpenRouter free tiers) or 500 fails readiness outright even though completions would work. `EnsureReadyAsync` performs a single 5s probe with no retry: one transient DNS/socket blip during `AddOrUpdateEndpointAsync` marks the endpoint `Failed` until the user manually re-saves/re-activates. In the autostart loop, any status > 0 that is not "handler reached" (e.g. llama.cpp's 503 while the model is still loading) logs failure and stops polling instead of continuing until `LlmUnityStartupTimeoutSeconds`. Suggested fixes: treat 429 as handler-reached (or retryable), add a small retry budget to activation probes, and keep polling on 5xx in the autostart loop.

### D. LOW (unverified) — Endpoint activation cancels but does not roll back a half-configured shared LLMUnity host

- File: `LlmEndpointClientFactory.cs:170-198`: `ApplyNativeConfiguration` mutates `llm.port/model/contextSize/...` before activation; if the descriptor's fingerprint check passes but readiness later fails, the exact-host `LLMAgent` retains the endpoint's configuration. Mitigated by the owned-host lease (`llm.Destroy()` + `SetActive(false)` on release), which resets state for the next activation; flagged for awareness only.

## Gaps vs the stated product intent

1. **History-preserving switch: substantively met, with capability caveats.** Chat history, agent memory, and tool registrations are keyed by `roleId` (`AiOrchestrator.BuildRequestBundleAsync`, `IAgentMemoryStore`), so switching profile/endpoint mid-conversation preserves them, including via the Chat "API" selector and `AiTaskRequest.RoutingProfileId`. The caveats are Confirmed #2 (tool-contract format may not match the new endpoint), #3 (token budget still sized for the old window), and #4 (reasoning/max-token behavior silently changes) — i.e. history survives, "capabilities" only partially.
2. **Presets/registry usable from Hub AND code: mostly met, one one-way gap.** Code has full CRUD (`ILlmEndpointRegistry`: endpoints, profiles with `FallbackProfileIds`, role assignments, `AgentBuilder.WithLlmProfile`, `AiTaskRequest.RoutingProfileId`). The Hub can create/edit/remove ENDPOINTS (with auto-created same-id profile), toggle Active/KeepWarm, and assign profiles to built-in or custom roles — but it has **no profile editor**: fallback chains, multiple profiles per endpoint, profile rename/removal, and `RemoveEndpointAsync(replacementEndpointId:)` re-pointing are code-only. `HubSettingsPage.cs` contains no call to `AddOrUpdateProfile`/`RemoveProfile`. Conversely everything the Hub does is reachable from code — that direction is clean.
3. **Chat API selector has a persistent global side effect.** `CoreAiChatPanel.OnApiProfileChanged` (`CoreAiChatPanel.cs:643-661`) calls `AssignProfileToRole(ActiveRoleId, profileId)` — a persisted, registry-wide role assignment (saved to `llm-endpoints.json`) — in addition to sending the explicit `RoutingProfileId` on each request (`:2097`). A player toggling the chat dropdown permanently re-routes every agent sharing that role across sessions. The doc only promises that "Automatic sends no explicit override"; the write-through on non-Automatic selections deserves explicit documentation or a per-panel-only mode.
4. **Secrets on WebGL.** The default `ILlmEndpointSecretProvider` resolves `SecretReference` via environment variables (`LlmClientRegistry.cs:86-95`); env vars are absent in WebGL players, so a persisted endpoint that needs auth can never re-acquire its key after reload — the user must re-enter the session key every launch. Given WebGL is a shipping target, ship a documented WebGL-appropriate provider (or explicitly document this limit in `LLM_ROUTING.md`, which currently only says "Applications may inject another provider").
5. **API-key hygiene is good** (deliberately verified): session keys are held in memory only; `FileLlmEndpointRegistryStore.Sanitize/CloneDescriptor` persists `SecretReference` but never the key; the Hub session-key field is write-only with explicit clear semantics. No plaintext keys in assets/PlayerPrefs from the new feature. (The pre-existing legacy path still stores `ApiKey` in `CoreAISettingsAsset` — out of this feature's scope but worth remembering.)

## What is done well

- **Clean portable/host split.** Contracts, readiness policy, and the `HttpClient` probe live in `CoreAI.Core` with no Unity types; `CoreAiUnity` supplies `UnityWebRequestOpenAiReadinessProbe` (WebGL-safe, `redirectLimit = 0`, cooperative cancellation via `Abort()`), matching the doc's host-boundary story exactly.
- **Lifecycle rigor.** Generation-tracked endpoints, staged zero-downtime replacement (`_pendingEndpoints` swap only after the new generation is Ready, `LlmClientRegistry.cs:1074-1087`), drain-before-release of owned LLMUnity hosts (`ReleaseOwnedHostAfterDrainAsync` waits for in-flight counts), and honest rejection of `CancelInFlight` removal rather than a false success.
- **Shared-activation correctness.** `AwaitActivationForCallerAsync` lets a caller cancel its wait without cancelling the shared activation task; duplicate `AddOrUpdateEndpointAsync` calls with an identical descriptor fingerprint join the in-flight activation instead of restarting it.
- **Security-conscious probes.** No redirect following (3xx never counts as ready — credentials cannot cross origins), 401/403 always fail readiness, loopback URLs bypass the system proxy with separate shared `HttpClient` instances (fixing the Mono `Proxy=null` trap documented in the transport).
- **Persistence quality.** Versioned schema, sanitize-on-load/save, temp-file + move write, `CoreAiWebGlPersistence.Sync()` for IndexedDB, session credentials excluded by design, and the null/empty/non-empty `sessionApiKey` tri-state (preserve/clear/replace) implemented consistently between registry and Hub.
- **Test coverage exists at the right seams**: `LlmRuntimeRegistryPlayModeTests`, `LlmEndpointRegistryPersistenceEditModeTests`, `LlmEndpointReadinessEditModeTests`, `CoreAiRoutingUiEditModeTests`, `LlmEndpointContractsEditModeTests` — the registry, persistence, probe policy, and UI adapters all have dedicated tests (the gaps found above are integration seams those tests don't cross: retry×routing, orchestrator×profile).
- Docs (`LLM_ROUTING.md`) are unusually honest and accurate about semantics (Active vs KeepWarm, readiness fallback rules, secret handling), and `TODO.md` already tracks the per-profile fallback/limits question.
