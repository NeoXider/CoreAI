# Runtime Correctness, Concurrency & Resource Management Audit — post-5.8.10 code

Date: 2026-07-16
Auditor: automated code audit (runtime robustness dimension)
Scope commits: 222e6eae (multi-endpoint routing), 92681445 (portable readiness probes), fa37a523 (Qwen spell tool hardening)

## Scope & goal alignment

Audited first-party code only, prioritizing the unreleased 5.9.0 work:

- `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/LlmClientRegistry.cs` (~1200 new lines)
- `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/LlmEndpointClientFactory.cs`
- `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/LlmEndpointRegistryPersistence.cs`
- `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/RoutingLlmClient.cs`
- `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/UnityWebRequestOpenAiReadinessProbe.cs`
- `Assets/CoreAI/Runtime/Core/Features/Llm/HttpClientOpenAiReadinessProbe.cs`
- `Assets/CoreAI/Runtime/Core/Features/Llm/HttpClientOpenAiTransport.cs` (loopback/external client split)
- `Assets/CoreAI/Runtime/Core/Features/LlmRouting/*` (contracts, readiness policy)
- `Assets/CoreAiUnity/Runtime/Source/Composition/LlmPipelineInstaller.cs`, `LlmUnityAutostartEntryPoint.cs`
- `Assets/CoreAiUnity/Runtime/Source/Features/Chat/CoreAiRoutingUiController.cs`, `CoreAiChatPanel.cs` (routing additions)
- `Assets/CoreAIHub/Runtime/HubSettingsPage.cs` (endpoint management UI)
- `Assets/CoreAI.Demos/QwenDemo/*` (GenieDemo, SpellcraftDemo, QwenDemoShared)

Goal alignment: the routing/probe design matches the framework's "survive messy reality" promise well — staged zero-downtime endpoint replacement, drain-before-release, portable readiness probes with a Unity (WebGL-safe) default adapter, and honest rejection of unimplementable `CancelInFlight` removal. The confirmed problems below are concentrated in endpoint host lifecycle and polling-based waits.

## Confirmed problems

### 1. HIGH — Owned LLMUnity host leaks when a Ready endpoint is deactivated via re-save
`Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/LlmClientRegistry.cs:599-644` (`AddOrUpdateEndpointAsync`)

When a descriptor with `Active = false` (and `KeepWarm = false`) is saved over an existing **Ready** endpoint, `stageReplacement` evaluates to false (it requires `copy.Active`), so line 617 `_runtimeEndpoints[id] = runtime` silently drops the old `RuntimeEndpoint`. The old instance's `ActivationTask` is already complete, so the cancel branch (lines 593-597) is skipped, and — unlike `SetEndpointActiveAsync` (lines 692-693) and `RemoveEndpointAsync` (lines 762-763) — **no `RequestOwnedHostRelease(oldRuntime)` is ever issued**. `Dispose()` cannot recover it either: it only iterates the current dictionaries (line 222).

Failure scenario: user opens Hub Settings, unchecks "Active" on a running LLMUnity endpoint, presses "Save endpoint" (`HubSettingsPage.SaveEndpoint` → `LlmEndpointRegistryUiController.SaveEndpointAsync` → `AddOrUpdateEndpointAsync`). The UI reports the endpoint as Inactive, but the native llama.cpp server keeps running, the host GameObject stays active, and VRAM/CPU stay allocated for the rest of the session. Re-activating later takes the "already active host" path (`ownsHostActivation == false` in the factory), so a config change then throws "Cannot reconfigure an already-active LLMUnity host" even though CoreAI itself started that host.

Suggested fix: inside the lock, when `_runtimeEndpoints[id]` is overwritten by a different instance, capture the replaced runtime and call `RequestOwnedHostRelease` on it after the lock (exactly as `SetEndpointActiveAsync` does), regardless of the new descriptor's `Active` flag.

### 2. MEDIUM — `Task.Yield()` busy-wait loops hot-spin when entered off the Unity main thread
- `LlmClientRegistry.cs:142-154` (`ActivatingEndpointClient.AwaitWithoutCancellingSharedActivation`)
- `LlmClientRegistry.cs:1129-1132` (`ReleaseOwnedHostAfterDrainAsync` in-flight drain)
- `LlmEndpointClientFactory.cs:297-308` (`WaitUntilReadyAsync`)

All three use `while (...) await Task.Yield();`. On the main thread with the Unity synchronization context this is a frame-paced poll (acceptable). But the LLM pipeline deliberately uses `ConfigureAwait(false)` throughout (`SmartToolCallingChatClient`, `ToolExecutionPolicy`, `TimeoutLlmClientDecorator`, `LoggingLlmClientDecorator`, `RetryingStreamingLlmClientDecorator` — and the QwenDemo comment "LLM tool delegates run on MEAI's worker thread" confirms requests execute on pool threads). When `ActivatingEndpointClient.CompleteAsync`/`CompleteStreamingAsync` is invoked from a thread-pool continuation (e.g. any tool-loop roundtrip after the first), `Task.Yield()` re-queues to the pool with no delay, producing a 100%-CPU spin on a worker thread for the entire endpoint activation — for LLMUnity that is a full native model load (tens of seconds). The drain loop can likewise spin for the duration of a long-lived SSE stream.

Suggested fix: replace polling with `Task.WhenAny(activation, cancellationTcs.Task)` using a `CancellationToken.Register` TCS (the pattern already used correctly in `AwaitActivationForCallerAsync`, lines 1195-1218), or at minimum `Task.Delay(50, token)` instead of `Task.Yield()`.

### 3. MEDIUM — `Changed` event can fire synchronously while `_gate` is held (verified sync-completion path)
`LlmClientRegistry.cs:636` → `BeginActivationLocked` → `ActivateAfterHostReleaseAsync` → `ActivateRuntimeAsync:1101`

`BeginActivationLocked` is called inside `lock (_gate)` (from `AddOrUpdateEndpointAsync` line 636, `SetEndpointActiveAsync` line 687). For endpoint kinds whose activation completes synchronously (Offline: `LlmEndpointClientFactory.ActivateAsync` cases at lines 76-83 return without awaiting; also the immediate-throw failure paths), the entire `ActivateRuntimeAsync` body — including `Changed?.Invoke()` at line 1101 — executes synchronously on the caller's stack **while the outer `_gate` is still held**. Subscribers are UI Toolkit refreshers (`HubSettingsPage.RefreshEndpointManagement`, `CoreAiChatPanel.RefreshApiProfileControls`) that immediately call back into `GetEndpoints`/`GetProfiles`/`GetRoleProfile`. Today this works only because `Monitor` is re-entrant on the same thread; any subscriber that blocks on another thread that needs `_gate`, or a future switch to a non-reentrant lock, deadlocks. It also means UI event handlers run at an unexpected registry-internal point.

Suggested fix: collect the fact that a change occurred and invoke `Changed` strictly after all locks are released (the code already does this for the async path — only the synchronous-completion path violates it).

### 4. MEDIUM — Non-atomic route resolution in `RoutingLlmClient.Prepare`
`Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/RoutingLlmClient.cs:183-206` (and `PreflightAnnotate` 39-50)

`Prepare` makes four independent, separately locked registry calls: `ResolveClientForRole`, `ResolveProfileIdForRole`, `ResolveContextWindowForRole`, `ResolveExecutionModeForRole`. A concurrent endpoint/profile switch (Hub save, fallback-chain state change, staged replacement publishing) between those calls yields an inconsistent tuple: the actual client from endpoint A with the profile id / context window / execution mode of endpoint B. Consequences: wrong token-budget compaction (`ContextWindowTokens`), wrong usage attribution in `LlmUsageReported`, misleading `LlmBackendSelected` diagnostics. Not memory-unsafe, but exactly the "concurrent switch while a request streams" class of bug.

Suggested fix: add a single registry method returning an atomic resolution snapshot (client + profileId + contextWindow + mode) computed under one `_gate` acquisition.

### 5. LOW — Hub `SaveEndpoint` cancels and immediately disposes the previous operation's CTS
`Assets/CoreAIHub/Runtime/HubSettingsPage.cs:599-601` (also `OnDestroyed` 124-126)

`_routingCts?.Cancel(); _routingCts?.Dispose(); _routingCts = new ...` runs while a prior `SaveEndpointAsync` may still be awaiting with that CTS's token. `AwaitActivationForCallerAsync` registers on the token (`LlmClientRegistry.cs:1207`) and `HttpClientOpenAiReadinessProbe` creates linked sources from it; registering on a token whose source has been disposed is safe on current BCLs only because `Cancel()` precedes `Dispose()` (already-canceled tokens invoke callbacks inline), but this is the classic cancel-then-dispose-while-in-use anti-pattern and is fragile across Mono profile changes. Suggested fix: dispose the old CTS in the `finally` of the operation that owns it, or defer disposal until its task completes.

### 6. LOW — `CoreAiRoutingUi` static facade has no domain-reload-disabled reset
`Assets/CoreAiUnity/Runtime/Source/Features/Chat/CoreAiRoutingUiController.cs:130-150`

The project pattern is to clear process-lifetime statics in `CoreAi.ResetForSubsystemRegistration()` (`Assets/CoreAiUnity/Runtime/Source/Api/CoreAi.cs:917-928`, which clears `CoreAIGameEntryPoint` and `CoreAiEvents`). `CoreAiRoutingUi._controller` and the static `ControllerChanged` event are not included; they rely solely on `CoreAiRoutingUiAttachment.Dispose()` at scope teardown. With Enter Play Mode Options (no domain reload), an aborted/exceptional teardown leaves a stale controller wrapping a disposed registry in the next play session; `LlmClientRegistry.Dispose()` also does not clear `_runtimeEndpoints`, so the stale controller still serves last-session endpoint snapshots to freshly built UI. Suggested fix: null `CoreAiRoutingUi.Controller` (and clear `ControllerChanged`) from the existing `ResetForSubsystemRegistration` hook.

### 7. LOW — Demo turns are not cancellable and outlive the scene
`Assets/CoreAI.Demos/QwenDemo/QwenDemoShared.cs:134` (`orchestrator.RunStreamingAsync(task, CancellationToken.None)`), `GenieDemo.cs:257` / `SpellcraftDemo.cs:452` (`_ = RunAsync(...)`)

`LlmMeter.RunAsync` hardcodes `CancellationToken.None`, and both demos fire-and-forget the turn task. The per-component `_lifetimeCancellation` guards only the readiness wait in `Start()`. Exiting the scene mid-turn leaves the LLM request running (until scope disposal), and the continuation writes `_last`/`_log` and enqueues into a destroyed `MainThreadPump` (managed-only, so no exception — but the request wastes local-model compute and the run cannot be aborted from the demo UI). For a demo whose purpose is showcasing robust plumbing, threading `destroyCancellationToken`/`_lifetimeCancellation.Token` through `LlmMeter.RunAsync` would be the exemplary pattern.

## Potential problems / risks (unverified)

- **Registry management APIs implicitly require the Unity main thread.** Activation runs `UnityEngine.Object.FindObjectsByType`, `GameObject.SetActive`, `llm.Awake()` (`LlmEndpointClientFactory.cs:120-200, 347-378`) and the default probe constructs `UnityWebRequest` (`UnityWebRequestOpenAiReadinessProbe.cs:60-75`) on the caller's context. All current callers (Hub UI, DI build, autostart) are main-thread, and awaits without `ConfigureAwait(false)` bounce continuations back via the Unity sync context — but nothing asserts or documents this. A user calling `AddOrUpdateEndpointAsync` from a worker thread (or from a `ConfigureAwait(false)` continuation) would crash inside Unity APIs or spin (see Confirmed #2). Suggest a documented contract or a main-thread marshal at the entry points. (Unverified: no misbehaving caller exists in-repo today.)
- **Drain TOCTOU window.** `ReleaseOwnedHostAfterDrainAsync` observes `InFlightRequests == 0` at one instant; a caller that resolved a `TrackedEndpointClient` earlier can increment and issue a request just after the owned host was destroyed (`llm.Destroy()` in `LlmUnityOwnedHostLeases`), failing with a connection error instead of a routing error. Window is small and the request fails safely, so noted as a risk only.
- **Abandoned streaming enumerator wedges the drain.** `TrackedEndpointClient.CompleteStreamingAsync` decrements `InFlightRequests` in the iterator's `finally`, which requires enumerator disposal. All in-repo consumers dispose (verified `RoutingLlmClient` line 179, decorators), but a third-party consumer that abandons the enumerator leaves `InFlightRequests > 0` forever → `ReleaseOwnedHostAfterDrainAsync` polls indefinitely and the host is never released.
- **`HubSettingsPage.HandleBackendChanged` re-entry context** (`HubSettingsPage.cs:1107`): `Task.Yield().GetAwaiter().OnCompleted(RefreshFromStatus)` schedules onto the event-raiser's context. `CoreAiBackend.OnBackendChanged` appears to fire on the main thread today; if any backend-switch path ever raises it from a pool thread, `RefreshFromStatus` touches UI Toolkit off-thread.
- **Persistence writes on the hot path.** `SaveRuntimeState` performs synchronous file I/O (temp write + delete + move + WebGL FS sync) under `_persistenceGate` on the calling thread — typically the main thread — on every endpoint/profile/role mutation and every activation completion. With frequent toggling this can hitch frames; the revision check only skips *stale* saves, not the current one.
- **`LlmMeter` approximated token count** (`QwenDemoShared.cs:198-203`) divides text length by 4 — flagged only because a user benchmarking with the demo HUD may not notice the `CompletionTokensExact = false` distinction if the UI omits it.

## What is done well

- **HttpClient hygiene:** shared static clients split loopback vs external (`HttpClientOpenAiTransport.cs`), so per-endpoint activation creates zero new sockets/handlers — no socket exhaustion when users add/remove endpoints repeatedly. The Mono `UseProxy`/`Proxy=null` landmine remains carefully documented and avoided; per-request timeout via linked CTS instead of mutating `HttpClient.Timeout`.
- **Zero-downtime endpoint replacement:** staged `_pendingEndpoints` generation with publish-on-Ready, in-flight requests pinned to their resolved client (`TrackedEndpointClient` + `Interlocked` counters), replaced generation released only after drain. Mid-stream endpoint switches do not interrupt active SSE streams, and orchestrator loop-guard/roundtrip counters are per-task, so a mid-conversation profile switch cannot reset runaway caps.
- **Honest contracts:** `LlmEndpointRemovalMode.CancelInFlight` is rejected (`return false`) rather than faking success, exactly as the contract doc requires; caller cancellation is deliberately decoupled from shared activations (`AwaitActivationForCallerAsync` with `RunContinuationsAsynchronously` and registration disposal).
- **Portable probes done right:** the readiness contract lives in core, the Unity DI default is the `UnityWebRequest` adapter (WebGL-safe, no threads, no blocking waits, `Abort()` on cancellation), and the `HttpClient` adapter is reserved for headless hosts with `ConfigureAwait(false)` and linked timeout tokens. Shared status policy (`LlmEndpointReadinessPolicy`) keeps the two adapters semantically identical.
- **WebGL paths:** endpoint clients route through `MeaiLlmClient.CreateHttp`, which selects `WebGlCompositeOpenAiTransport`/`FetchSseOpenAiTransport` on WebGL; persistence calls `CoreAiWebGlPersistence.Sync()`; LLMUnity endpoint kind is compiled out with `#if !UNITY_WEBGL`.
- **Persistence safety:** session API keys are never serialized (kept only on the in-memory `RuntimeEndpoint`), writes are temp-file + move, schema-versioned, and sanitized on load.
- **Lifecycle discipline elsewhere:** `LlmUnityAutostartEntryPoint` owns a lifetime CTS, uses realtime UniTask delays, and swallows only its own cancellation; `CoreAiChatService` timeout timers moved to `DelayType.Realtime` so `Time.timeScale = 0` no longer breaks request timeouts; Hub window calls `page.OnDestroyed()` (`CoreAiHubWindow.cs:675`) and both `HubSettingsPage` and `CoreAiChatPanel` unsubscribe from the static routing events there.
- **Demos:** `MainThreadPump` correctly marshals tool-delegate visuals to the main thread (with the worker-thread hazard explicitly documented), turn guards serialize tool turns, readiness is gated before enabling input, and `Start()` re-checks `this == null` after awaits.
