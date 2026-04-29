# Changelog

## [v1.2.1] — 2026-04-29

### AllowedToolNames semantics + streaming facade

- **Breaking (narrow):** `AiTaskRequest.AllowedToolNames` / `LlmCompletionRequest`: **`null`** still means “do not filter role tools”; a **non-null empty array** now means “attach **no** tools” (chat-only allowlist), matching lesson-slot “no quiz/dnd this turn” use cases.
- `AiOrchestrator.FilterToolsForRequest` implements the above; docs updated (`LLM_ROUTING.md`, `LESSON_ORCHESTRATION.md`, `AiTaskRequest` XML).
- **`CoreAi.StreamChunksAsync(AiTaskRequest, CancellationToken)`** (Unity façade) forwards to `CoreAiChatService.SendMessageStreamingAsync` so hosts can pass `AllowedToolNames` / `ForcedToolMode` on the same code path as `RunTaskAsync`.
- **Tests:** `RunTaskAsync_EmptyAllowedToolNames_SendsNoTools`, `RunStreamingAsync_UsesSameToolFiltering_AsRunTaskAsync`.
- **EditMode:** `CoreServicesInstallerEditModeTests` — no invalid `GlobalMessagePipe.SetProvider(null)` in TearDown (MessagePipe does not support null).

Package version **`1.2.1`**; align `com.nexoider.coreaiunity` to **`1.2.2`**.

## [v1.2.0] — 2026-04-29

### RedoSchool lesson/practice orchestration APIs

- Added per-role runtime context providers on `AgentMemoryPolicy` so lesson slots can inject context without UI prompt-spaghetti.
- Added `AllowedToolNames` filtering and chat-only tool suppression on `AiTaskRequest`/`LlmCompletionRequest`.
- Added `ILlmToolCallHistory`, `ScriptedLlmClient`, `LlmToolResultEnvelope`, and `IAgentTurnTraceSink` for deterministic tests, structured tool results, and diagnostics.
- Package version **`1.2.0`**; aligned with `com.nexoider.coreaiunity` **`1.2.0`**.

## [v1.1.0] — 2026-04-29

### Portable LLM routing and policy contracts

- ✨ **Portable routing model** — added `LlmRouteProfile`, `LlmRouteRule`, `LlmRouteTable`, `ILlmRouteResolver`, and `LlmRouteResolver` under `CoreAI.Core`; `LlmExecutionMode.Stub` is now an alias for offline deterministic responses.
- ✨ **Portable registry and policy contracts** — added `ILlmClientRegistry`, `ILlmAuthContextProvider`, `ILlmEntitlementPolicy`, `LlmEntitlementDecision`, `ILlmUsageSink`, and `LlmUsageRecord`.
- ✨ **Provider error DTO** — added `LlmProviderError` for stable backend/provider codes such as `quota_exceeded`, `subscription_required`, `model_not_allowed`, and `rate_limited`.
- 📝 **Docs:** added `Assets/CoreAI/Docs/LLM_ROUTING.md`.
- 🔧 Package version **`1.1.0`**; aligned with `com.nexoider.coreaiunity` **`1.1.0`**.

## [v1.0.3] — 2026-04-29

### Unity chat UX alignment

- 🔧 Package version **`1.0.3`**; aligned with `com.nexoider.coreaiunity` **`1.0.3`**.

## [v1.0.2] — 2026-04-28

### Long context and tool-call identity

- ✨ **Conversation context management** — added portable `IConversationContextManager`, `ConversationContextSnapshot`, and `IConversationSummaryStore` contracts for long-running chat history compaction.
- ✨ **Deterministic summary fallback** — `DeterministicConversationContextManager` keeps recent messages in chat history and moves older turns into a `## Conversation Summary` system section without requiring an extra LLM call.
- ✨ **Tool-call identity** — added `LlmToolCallInfo` with `CallId`, `TraceId`, role, tool name, and sanitized arguments. Tool lifecycle events now expose `Info` while preserving `ToolName` and `ArgumentsJson` accessors.
- 🔧 Package version **`1.0.2`**; aligned with `com.nexoider.coreaiunity` **`1.0.2`**.

## [v1.0.1] — 2026-04-28

### Production runtime extension points

- ✨ **LLM usage telemetry** — added portable `LlmUsageReported` contract for token accounting and quota integrations.
- ✨ **Typed LLM errors** — `LlmErrorCode`, `LlmClientException`, and structured error fields on completion/stream chunks let UI and retry code handle quota, auth, rate-limit, timeout, and backend failures without parsing strings.
- ✨ **Runtime prompt context** — `IAiPromptContextProvider` lets projects append per-request context to prompts without mutating static role configuration.
- ✨ **Scoped memory contracts** — `AgentMemoryScope`, `IAgentMemoryScopeProvider`, and `ScopedAgentMemoryStoreDecorator` allow user/session/topic isolation while preserving role-only keys by default.
- ✨ **Tool lifecycle events** — added portable `LlmToolCallStarted`, `LlmToolCallCompleted`, and `LlmToolCallFailed` contracts for diagnostics and gameplay integrations.
- 🔧 Package version **`1.0.1`**; aligned with `com.nexoider.coreaiunity` **`1.0.1`**.

## [v1.0.0] — 2026-04-28

### Stable LLM mode contracts

- ✨ **`LlmExecutionMode`** — portable public mode contract for `Auto`, `LocalModel`, `ClientOwnedApi`, `ClientLimited`, `ServerManagedApi`, and `Offline`.
- ✨ **LLM routing events** — added portable `LlmBackendSelected`, `LlmRequestStarted`, and `LlmRequestCompleted` message contracts for Unity MessagePipe integration without adding MessagePipe dependencies to `CoreAI.Core`.
- 🔧 Package version **`1.0.0`**; aligned with `com.nexoider.coreaiunity` **`1.0.0`**.

## [v0.25.14] — 2026-04-27

### Release

- 🔧 Version **0.25.14**; release train aligned with `com.nexoider.coreaiunity` **0.25.14** (see Unity package changelog for `CoreAiChatPanel` UX fixes).

## [v0.25.13] — 2026-04-27

### MEAI tool argument binding

- 🐛 **`CompatibilityLlmTool` native argument binding** — the MEAI executor parameter is now named `ingredients`, matching the JSON schema. Valid model calls such as `{"ingredients":["Fire","Earth"]}` no longer fail before reaching the tool with a missing `ingredientsObj` argument.
- 🧪 **EditMode coverage:** added an `AIFunction.InvokeAsync` regression for `check_compatibility` using the public `ingredients` argument name.
- 📝 **`MEAI_TOOL_CALLING.md`** — documents that .NET `AIFunction` parameter names must match `ILlmTool.ParametersSchema` property names.
- 🔧 Version **`0.25.13`**; `com.nexoider.coreaiunity` aligned to **`0.25.13`**.

## [v0.25.12] — 2026-04-27

### Queue scheduling hardening

- 🐛 **`QueuedAiOrchestrator` latest-wins scopes** — `CancellationScope` now cancels older active and pending work as soon as a newer task with the same scope is enqueued, including streaming tasks.
- 🐛 **Queue fairness and cancellation** — equal priorities are FIFO, streaming and non-streaming tasks share one effective priority order, and pending tasks observe external cancellation before they start.
- 🧪 **EditMode coverage:** queue tests now cover priority ordering, FIFO tie-breaking, active and pending scope cancellation, pending external cancellation, `CancelTasks(scope)`, and shared sync/stream priority.
- 🔧 Version **`0.25.12`**; `com.nexoider.coreaiunity` aligned to **`0.25.12`**.

## [v0.25.11] — 2026-04-27

### Tool contract hardening

- ✨ **`AiOrchestrator` tool contract injection** — roles with registered tools now get a compact `## Tool Contract` block in the system prompt that lists available tools, schemas, and rules: call tools through the tool interface when requested, pass required arguments structurally, and do not claim registered tools are unavailable. This nudges local models toward real tool calls without weakening tests.
- 🐛 **Structured retry keeps tool context** — the structured-response retry path now preserves `Tools`, `ChatHistory`, `ForcedToolMode`, `RequiredToolName`, and `MaxOutputTokens` from the original request instead of retrying with text-only context.
- 🧪 **EditMode coverage:** orchestrator regression test verifies that tool-enabled roles receive the tool contract, required-tool hint, and parameter schema in `LlmCompletionRequest.SystemPrompt`.
- 🔧 Version **`0.25.11`**; `com.nexoider.coreaiunity` aligned to **`0.25.11`**.

## [v0.25.10] — 2026-04-27

### Agent memory policy defaults

- 🔧 **`AgentMemoryPolicy.RoleMemoryConfig` constructor** — default `persistChatHistory` is now **`false`**. Built-in agent roles that use only the two-argument form (`MemoryTool` + default action) therefore do **not** imply cross-session chat persistence when `WithChatHistory` is off (matches the role table in docs and `AgentBuilderChatHistoryEditModeTests`). **`PlayerChat`** still sets `persistChatHistory: true` explicitly in the policy constructor.
- 🔧 Version **`0.25.10`**; `com.nexoider.coreaiunity` aligned to **`0.25.10`**.

## [v0.25.9] — 2026-04-27

### Per-agent MaxOutputTokens (additive)

- ✨ **`AgentBuilder.WithMaxOutputTokens(int? tokens)`** — persistent per-agent response token cap for roles that should stay short (NPC chat) or intentionally verbose (planners) without setting the limit on every call.
- ✨ **`AgentMemoryPolicy.RoleMemoryConfig.MaxOutputTokens`** + **`SetMaxOutputTokens(roleId, int?)`** — policy-level storage for the per-role override. `null` / non-positive values clear the override.
- 🔧 **Priority via orchestrator:** `AiTaskRequest.MaxOutputTokens` (per-call) → `AgentBuilder.WithMaxOutputTokens` / policy (per-agent) → `ICoreAISettings.MaxTokens` (global fallback in the Unity LLM client) → provider default. Direct `LlmCompletionRequest.MaxOutputTokens` remains the highest priority when calling an `ILlmClient` directly.
- 🧪 **EditMode coverage:** orchestrator tests for per-agent forwarding, per-call override priority, and unset role fallback.
- 🔧 Version bumped to **`0.25.9`** so `com.nexoider.coreai` and `com.nexoider.coreaiunity` publish with matching package versions.

## [v0.25.4] — 2026-04-27

### ✨ Unified MaxTokens fallback (additive)

- ✨ **`ICoreAISettings.MaxTokens`** — new interface property with **default-implementation `=> 0`** (DIM, C# 8+); existing implementers (test stubs etc.) compile unchanged. Semantics: `0` / negative = "not set, fallback skipped"; positive = global LLM response token cap that the Unity layer back-fills uniformly into **both** backends (HTTP via `MeaiOpenAiChatClient` and local GGUF via `LlmUnityMeaiChatClient`).
- ✨ **`AiTaskRequest.MaxOutputTokens`** (`int?`) — per-call override, symmetric with `ForcedToolMode`/`RequiredToolName`. Forwarded by `AiOrchestrator.RunTaskAsync`, `RunStreamingAsync`, and the structured-retry path into `LlmCompletionRequest.MaxOutputTokens`.
- 🔧 **Priority**: `LlmCompletionRequest.MaxOutputTokens` (per-request direct client call) → `AiTaskRequest.MaxOutputTokens` (per-call via orchestrator) → `ICoreAISettings.MaxTokens` (global fallback) → provider default. Previously `CoreAISettings.MaxTokens` was a read-only getter with no consumer — visible in the inspector but never applied.
- 🧪 **`MaxTokensFallbackEditModeTests`** — 4 tests covering: settings-default fallback, per-request override, settings=0 leaves provider default, streaming path applies the same fallback.
- 🔧 Version bumped to **`0.25.4`** (minor — additive public API). `coreaiunity 0.25.8 → coreai 0.25.4`.

## [v0.25.7] — 2026-04-27

### Release sync with `com.nexoider.coreaiunity 0.25.7`

- 🔧 **`com.nexoider.coreai`** stays at **`0.25.3`** — no public **`CoreAI.Core`** API changes. Unity-only release: Editor `CoreAISettings` bootstrap, PlayMode recall on 5xx, `TROUBLESHOOTING`. Details: `Assets/CoreAiUnity/CHANGELOG.md` (0.25.7).

## [v0.25.3] — 2026-04-26

### Release sync with `com.nexoider.coreaiunity 0.25.3`

- 🔧 Package version bumped to `0.25.3`. Manifest dependency `coreaiunity 0.25.3 → coreai 0.25.3`.
- ✅ No **`CoreAI.Core`** public API changes — Unity-layer release only. Details: `Assets/CoreAiUnity/CHANGELOG.md` (0.25.3: chat hotkeys C/Esc, `Update` + poll when UITK has no focus, `FocusController` fix, `OnCollapsedStateChanged` hook, UXML/tooltips).

## [v0.25.2] — 2026-04-26

### Release sync with `com.nexoider.coreaiunity 0.25.2`

- 🔧 Package version bumped to `0.25.2`. Manifest dependency `coreaiunity 0.25.2 → coreai 0.25.2`.
- ✅ No `CoreAI.Core` public API changes — release sync only. See CoreAI Unity CHANGELOG 0.25.2 (UXML emoji cleanup + new `Docs/STREAMING_WEBGL_TODO.md` with a plan to fix WebGL SSE streaming in `OpenAiChatLlmClient.CompleteStreamingAsync`).

## [v0.25.1] — 2026-04-26

### Release sync — version alignment with `com.nexoider.coreaiunity 0.25.1`

- 🔧 Package version bumped to `0.25.1` to align with `com.nexoider.coreaiunity 0.25.1` (two WebGL/input fixes — see below).
- 🔧 Manifest dependency `com.nexoider.coreaiunity` now requires `com.nexoider.coreai 0.25.1` (was `0.25.0`).
- ✅ **No breaking changes to `CoreAI.Core` API** — pure release sync. Existing code using `LlmToolChoiceMode`, `AiTaskRequest.ForcedToolMode`, orchestrator, etc. continues to work.

### CoreAI Unity 0.25.1 release context (what actually changed in the Unity layer)

- 🐛 **WebGL TextField focus persistence** — `CoreAiChatPanel` keeps `WebGLInput.captureAllKeyboardInput = false` every frame (Update watchdog under `#if UNITY_WEBGL && !UNITY_EDITOR`). Fixes the “focus lasts one frame then drops” symptom in WebGL builds.
- 🐛 **Both Unity input systems** — `OrchestrationDashboard` no longer crashes with `Active Input Handling = Input System Package (New)`. `CoreAI.Source.asmdef` declares a soft dependency on `Unity.InputSystem` via `versionDefines` (`COREAI_HAS_INPUT_SYSTEM`).
- Details: `Assets/CoreAiUnity/CHANGELOG.md` (0.25.1 entry).

## [v0.25.0] — 2026-04-26

### Forced Tool Mode — deterministic tool selection per request

- ✨ **`LlmToolChoiceMode` enum** (`CoreAI.Ai`): `Auto` (default, model decides), `RequireAny` (provider must emit at least one tool call from the available set), `RequireSpecific` (provider must call a named tool — uses `RequiredToolName`), `None` (text-only response, tool calls forbidden).
- ✨ **`AiTaskRequest.ForcedToolMode` + `RequiredToolName`** — application-layer code (intent classifiers, retry pipelines) can now request guaranteed tool emission for a single call without changing the agent definition. Default is `Auto`, so existing behaviour is preserved.
- ✨ **`LlmCompletionRequest.ForcedToolMode` + `RequiredToolName`** — propagated 1-to-1 through `AiOrchestrator.RunTaskAsync`, `RunStreamingAsync` and the structured-retry path; LLM adapters in the Unity layer translate this to provider-native tool-choice (Microsoft.Extensions.AI `ChatOptions.ToolMode`).
- 🔧 **Streaming multi-round tool loop is unchanged** — `ForcedToolMode` only applies to the first iteration of a streaming session; after the first tool result is fed back, the model is reset to `Auto` so it can finalise with text instead of being pinned into an infinite tool-call loop.
- 🧪 **Tests:** new `ForcedToolModeEditModeTests` validate `LlmCompletionRequest`/`AiTaskRequest` plumbing and orchestrator forwarding.

### Release sync

- 🔧 Version bumped to `0.25.0` (minor — new public API). Dependency contract `com.nexoider.coreaiunity` `0.25.0+`.

## [v0.24.2] — 2026-04-26

### Release sync

- 🔧 Version bumped to `0.24.2` to match `com.nexoider.coreaiunity` `0.24.2`.
- 🔧 Synced Unity-layer hardening: HTTP error response body logging in `MeaiOpenAiChatClient` (both non-streaming and SSE paths), `ToolExecutionPolicy.maxConsecutiveErrors` clamped to `Math.Max(1, value)`.

## [v0.24.0] — 2026-04-26

### Streaming tool-calling hardening (release sync)

- 🔧 Version bumped to `0.24.0` to match `com.nexoider.coreaiunity` `0.24.0`.
- 🔧 Synced Unity-layer hardening: `ToolExecutionPolicy` (shared duplicate detection / error tracking), pattern-aware text JSON parser with multi-tool and code-block protection, native SSE `delta.tool_calls` parsing, stop/clear race condition fix.

## [v0.23.3] — 2026-04-26

### Release sync

- 🔧 Version bumped to `0.23.3` to match `com.nexoider.coreaiunity` `0.23.3`.
- 🔧 Synced Unity-layer reliability update: idempotent `CoreAIGameEntryPoint` startup guard prevents duplicate CoreAI initialization in scenes with accidental double composition.
- 🧪 Synced test coverage additions in Unity host: `CoreAIGameEntryPointEditModeTests` and additional streaming/tool-cycle guards in `MeaiLlmClientEditModeTests`.

## [v0.23.2] — 2026-04-26

### Release sync

- 🔧 Version bumped to `0.23.2` to match `com.nexoider.coreaiunity` `0.23.2` (includes non-stream HTTP cancellation fix used by Chat stop / Esc).

## [v0.23.1] — 2026-04-26

### Release sync

- 🔧 Version bumped to `0.23.1` to match `com.nexoider.coreaiunity` `0.23.1` and ensure downstream projects resolve the latest reliability fixes.

## [v0.23.0] — 2026-04-25

### Agent Control API UI
- ✨ **Chat UI updated.** `CoreAiChatPanel` adds a stop control that interrupts agent generation.
- ✨ **Default clear behavior.** The clear control in `CoreAiChatPanel` clears the UI and short-term chat history (`CoreAi.ClearContext(roleId, true, false)`). Full reset (including long-term memory) uses `ClearChat(clearChatHistory: true, clearLongTermMemory: true)`.
- 🔧 `com.nexoider.coreai` / `com.nexoider.coreaiunity` package versions aligned.
- 🔧 Release synced with the Unity layer for streaming + tool calling (`MeaiLlmClient` single-cycle: tool JSON suppressed in UI, tools run inside the same streaming pipeline).
- 🔧 For tool roles (`AgentMode.ToolsAndChat`, `AgentMode.ToolsOnly`) streaming is enabled per-role by default; `ChatOnly` still follows global/explicit overrides.
- 🔧 PlayMode reliability synced: stricter HTTP stream cancellation plus stabilized `Streaming_CancellationToken_StopsStream` and `MemoryTool_AppendsMemory`.

## [v0.22.0] — 2026-04-25

### Agent Control API — Full Lifecycle Management

- ✨ **Granular context clearing.** `CoreAi.ClearContext(string roleId, bool clearChatHistory, bool clearLongTermMemory)` — separate flags for chat history vs long-term memory (`MemoryTool`).
- ✨ **Tool invocation hook.** `CoreAi.OnToolExecuted` — global `ToolExecutedHandler(roleId, toolName, arguments, result)` for reactive integration (audio, VFX, analytics). Subscriber exceptions do not break the LLM pipeline.
- ✨ **`CoreAi.NotifyToolExecuted`** — internal hook invoked from `SmartToolCallingChatClient` after each successful tool call.
- ⚠️ **Breaking:** `SmartToolCallingChatClient` constructor now requires `roleId` (`string`) before `maxConsecutiveErrors`.

### Release sync

- 🔧 Version aligned with `com.nexoider.coreaiunity` **0.22.0** (Unity-layer release: `CoreAiChatPanel` stop via `Esc` and send-button stop state + tooltip). No portable-core API changes.

## [v0.21.9] — 2026-04-25

### Agent Control API
- ✨ **Stop + clear APIs.** `IAiOrchestrationService` adds `CancelTasks(string cancellationScope)`. `CoreAi` adds `CoreAi.StopAgent(string roleId)` and `CoreAi.ClearContext(string roleId)` for cancelling in-flight LLM work and clearing chat history.

## [v0.21.8] — 2026-04-25

### Release sync

- 🔧 Version aligned with `com.nexoider.coreaiunity` **0.21.8** (Unity layer: LLMUnity preprocessor guard refactor, automatic `COREAI_HAS_LLMUNITY` via `versionDefines`, fixes `CS0246` when LLMUnity is absent). No portable-core changes.

## [v0.21.7] — 2026-04-23

### Release sync

- 🔧 Version aligned with `com.nexoider.coreaiunity` **0.21.7** (Unity layer: `CoreAiChatPanel` FAB collapse, auto-collapse on small screens, `PlayerPrefs` persistence). No portable-core changes.

## [v0.21.6] — 2026-04-23

### Release sync

- 🔧 Version aligned with `com.nexoider.coreaiunity` **0.21.6** (Unity layer: removed forced `InputField` focus hacks in `CoreAiChatPanel`, WebGL caret flicker / lost keys fix). No portable-core changes.

## [v0.21.4] — 2026-04-23

### Release sync

- 🔧 Version aligned with `com.nexoider.coreaiunity` **0.21.4** (Unity layer: WebGL input focus hardening in `CoreAiChatPanel`). No portable-core changes.

## [v0.21.3] — 2026-04-23

### Release sync

- 🔧 Version aligned with `com.nexoider.coreaiunity` **0.21.3** (Unity layer: `CoreAiChatPanel` WebGL focus/typing stability). No portable-core changes.

## [v0.21.2] — 2026-04-23

### Release sync

- 🔧 Version aligned with `com.nexoider.coreaiunity` **0.21.2** (Unity layer: `TextField` focus fix in `CoreAiChatPanel` after sending a message). No portable-core changes.

## [v0.21.1] — 2026-04-23

### Release sync

- 🔧 Version aligned with `com.nexoider.coreaiunity` **0.21.1** (Unity layer: chat UI/scrollbar, timeouts, tests).

## [v0.21.0] — 2026-04-23

### Orchestrator streaming

- ✨ **`IAiOrchestrationService.RunStreamingAsync(AiTaskRequest, CancellationToken)`** — new interface member (C# 8 DIM fallback calls `RunTaskAsync` and yields one final chunk with `IsDone=true`).
- ✨ **`AiOrchestrator.RunStreamingAsync`** — real streaming implementation. Same path as `RunTaskAsync` (prompt composer, authority, memory, tools, structured validation) but emits chunks as they arrive and publishes `ApplyAiGameCommand` only after the stream completes. Shared request build logic moved to private `BuildRequest`.
- ✨ **Structured validation** runs on the fully accumulated text after streaming ends. On failure, emits a terminal `LlmStreamChunk` with `Error = "structured validation failed: ..."` (no automatic stream retry — caller decides).
- 📚 **`RunStreamingAsync` contract** warns: any wrapper over `IAiOrchestrationService` (queue, logging, timeout, authority) must override this method explicitly or the DIM fallback silently disables streaming.

## [v0.20.3] — 2026-04-23

### Streaming pipeline — end-to-end visibility fix
- 🐛 **Critical: streaming was invisible in the UI.** `ILlmClient.CompleteStreamingAsync()` has a default interface implementation that falls back to `CompleteAsync()` and emits the whole answer as **one** terminal chunk after generation. Wrappers that did not override the method hid real streaming. Fixed in `CoreAiUnity` (see its CHANGELOG).
- 📝 `ILlmClient.CompleteStreamingAsync()` docs now warn that decorators (logging, routing, timeouts) **must** override streaming explicitly or the DIM fallback kills streaming.

## [v0.20.2] — 2026-04-23

### Streaming Configuration
- ✨ **`ICoreAISettings.EnableStreaming`** — global switch for LLM response streaming (SSE for HTTP API, callback queue for LLMUnity). Default `true`.
- ✨ **`AgentBuilder.WithStreaming(bool)`** — per-agent override of the global flag (e.g. chat NPC forced streaming vs strict JSON parser / tool-only non-streaming).
- ✨ **`AgentMemoryPolicy.SetStreamingEnabled(roleId, bool?)`** and **`IsStreamingEnabled(roleId, fallback)`** — per-role override storage and effective flag resolution.
- ✨ **`AgentConfig.EnableStreaming`** (`bool?`) — nullable override propagated to policy via `ApplyToPolicy()`.
- 🔧 **Precedence** (highest to lowest): UI (`CoreAiChatConfig.EnableStreaming`) → per-agent (`AgentBuilder.WithStreaming`) → global (`CoreAISettings.EnableStreaming`).

## [v0.20.1] — 2026-04-23

### Streaming Robustness

- ✨ **`ThinkBlockStreamFilter`** (`CoreAI.Ai`) — reusable stateful filter that strips `<think>...</think>` from the LLM stream. Unlike regex, handles tags split across chunks (common with DeepSeek / Qwen).
  - `ProcessChunk(string)` — process a chunk, return only visible text.
  - `Flush()` — end the stream (return trailing text if the model cut off mid-response).
  - `Reset()` — reuse the same instance.

### Streaming API
- 📝 **Stream contract:** `ILlmClient.CompleteStreamingAsync()` always ends with a final chunk `IsDone=true` (even on empty model output) so callers can close the UI reliably.
- 📚 `ILlmClient.CompleteStreamingAsync()` docs note implementations should run on Unity’s main thread (`UnityWebRequest`).

## [v0.20.0] — 2026-04-23

### Streaming API
- ✨ **`LlmStreamChunk`** — stream chunk type with `Text`, `IsDone`, `Error`, and usage stats.
- ✨ **`ILlmClient.CompleteStreamingAsync()`** — new interface member returning `IAsyncEnumerable<LlmStreamChunk>`. Default implementation falls back to `CompleteAsync()` with a single chunk.
- ✨ **`MeaiLlmClient.CompleteStreamingAsync()`** — real streaming via `IChatClient.GetStreamingResponseAsync()` with `<think>` filtering.

### 3-Layer Prompt Architecture
- 🔧 **Bug fix:** `AgentBuilder.WithSystemPrompt()` did not register prompts in `IAgentSystemPromptProvider`, so AgentBuilder prompts were ignored and AiOrchestrator always used ManifestProvider.
- ✨ **Three-layer system prompt** in `AiPromptComposer.GetSystemPrompt()`:
  - **Layer 1:** `CoreAISettings.universalSystemPromptPrefix` — shared rules for all agents
  - **Layer 2:** Base prompt from ManifestProvider / ResourcesProvider (`.txt` assets)
  - **Layer 3:** Extra prompt from `AgentBuilder.WithSystemPrompt()` (via `AgentMemoryPolicy`)
- 🔧 **`AgentBuilder.Build()`** — no longer appends `universalPrefix` (handled in `AiPromptComposer`)
- 🔧 **`AgentConfig.ApplyToPolicy()`** — registers system prompt via `policy.SetAdditionalSystemPrompt()`
- ✨ **`AgentMemoryPolicy.SetAdditionalSystemPrompt()` / `TryGetAdditionalSystemPrompt()`** — stores AgentBuilder extra prompts
- ✨ **`AgentBuilder.WithOverrideUniversalPrefix()`** — disable `universalPrefix` per role (parsers, validators, fully custom prompts)
- ✨ **`AgentMemoryPolicy.SetOverrideUniversalPrefix()` / `IsUniversalPrefixOverridden()`** — per-role universal prefix control

### Breaking Changes
- **`AiPromptComposer` constructor** — optional `AgentMemoryPolicy` and `ICoreAISettings` parameters (backward compatible with `= null`)
- **`universalPrefix`** now applies to all roles by default (opt out with `.WithOverrideUniversalPrefix()`)

## [v0.19.3] — 2026-04-22

### Prompt Optimization
- 🔧 **Removed duplicate tool-calling rules** from all seven built-in agent prompts (C# constants + `.txt` resources). Saves ~100–150 tokens per request — rules already live in `UniversalSystemPromptPrefix`.
- 📝 **Prompt wording:** added response length limits for AiNpc (1–3 sentences) and PlayerChat (1–5 sentences).
- 🔧 **Native tool calling:** dropped legacy manual JSON tool-formatting guidance from `Agent.cs` and `AllToolCallsPlayModeTests.cs`; samples and tests use native `MEAI` function calling.

### Editor UX
- ✨ **`CoreAI/Create Scene Setup`** — Unity menu action for quick scene wiring:
  - Adds `CoreAILifetimeScope` with assigned assets
  - Generates default assets (Settings, LogSettings, PromptsManifest, etc.)
  - Creates `LLM` + `LLMAgent` when using LLMUnity backend (or Auto+LlmUnityFirst)
  - Duplicate guard and Undo (Ctrl+Z)

### Stability
- 🐛 **HTTP timeout logging:** `MeaiOpenAiChatClient` — timeout/network issues downgraded from `LogError` to `LogWarning` so PlayMode tests stay green in Unity Test Runner.
- 🐛 **PlayMode tests:** fixed `AllToolCalls_MemoryTool_WriteAppendClear` failure from conflicting text JSON prompts vs native tool calls.
- 🛡️ **UI safety:** `try/catch` in `async void OnSendClicked` (`InGameChatPanel.cs`) to avoid silent UI crashes on network errors.

### Documentation
- 📚 **READMEs (EN + RU)** — full dependency install guide:
  - NuGet DLLs (Microsoft.Extensions.AI, etc.) with version table
  - Git URL packages and transitive deps (VContainer, MoonSharp, LLMUnity, UniTask, MessagePipe)
  - New steps: Create Scene Setup, LLM backend setup
- 🔗 **Link fix:** repaired broken relative links in `README_RU.md` for GitHub repo home navigation.

## [v0.19.2] — 2026-04-14

### Changed
- **AgentMemory:** smarter `ChatHistory` trimming before the LLM client. History is capped by message count (`MaxChatHistoryMessages`, default 30) and approximate token budget (`ContextTokens / 2`). Reduces HTTP context blow-ups and huge bills while older turns stay in JSON.
- **AgentBuilder:** optional `maxChatHistoryMessages` on `.WithChatHistory()`.

## [v0.19.1] — 2026-04-14

### Fixes & Stability
- 🐛 **Duplicate tool-call guard:** documented how `MeaiLlmClient` resets failed-call counters per session; `executedSignatures` scoping isolates each request.
- 🔧 **`Agent.cs` test harness:**
  - Test phrases exposed in Inspector `[TextArea]` for live scenario tweaks and to avoid identical-prompt loops.
  - Added `ClearMemory()` to reset history between button presses so the model does not anchor on prior mistakes.
- 📝 **Docs:** clarified `SceneLlmAgentProvider` with `DontDestroyOnLoad` — needs an `LLMAgent` component or registered agent name.

## [v0.19.0] — 2026-04-10

### Crafting & Validation

- ✨ **`CompatibilityChecker`** — ingredient compatibility checks for CoreMechanicAI
  - Rules for arbitrary element counts (pairs, triples, quads, …)
  - `CompatibilityRule.Pair()` and `CompatibilityRule.Group()` factory helpers
  - Element groups (IronOre → Metal, WaterFlask → Water) with automatic resolution
  - Custom validators (`ICompatibilityValidator`) for game logic
  - Weighted scoring: rules covering more elements win
- ✨ **`CompatibilityLlmTool`** — `ILlmTool` wrapper for function calling (LLM can validate before crafting)
- ✨ **`JsonSchemaValidator`** — LLM JSON validation without external deps
  - Required fields and types (string, number, integer, boolean, array, object)
  - Numeric ranges (min/max) and enums
  - Strips markdown fences (`` `json...` ``)
  - `ToPromptDescription()` — schema blurb for system prompts
- 🧪 **45+ EditMode tests** for CompatibilityChecker, JsonSchemaValidator, and CompatibilityLlmTool

## [v0.18.0] — 2026-04-10

### Architecture — DI Migration

- 🔧 **`CoreAISettings` → static proxy** — no longer stores independent field copies; reads delegate to DI-registered `ICoreAISettings Instance`.
  - Direct field writes kept for backward compatibility (override wins over Instance).
  - Added `CoreAISettings.ResetOverrides()` for tests.
- 🔧 **`LuaAiEnvelopeProcessor`** — takes `ICoreAISettings` via constructor (optional). No longer reads `CoreAISettings.MaxLuaRepairRetries` at init.
- ❌ **Removed** `SyncToStaticSettings()` — replaced with `CoreAISettings.Instance = settings`.

## [v0.16.0] — 2026-04-09

### PlayMode Tools & Editor
- ✨ **`SceneLlmTool`** — runtime scene inspection for the LLM:
  - `find_objects` — find GameObjects by name/tag
  - `get_hierarchy` — list children
  - `get_transform` / `set_transform` — position, rotation, scale
- ✨ **`CameraLlmTool`** — vision tool: PlayMode `capture_camera` screenshots as Base64 JPEG `dataUri` (multimodal models like LLaVA / gpt-4o).
- 🛠 **Threading** — both tools marshal Unity API work via `UniTask.SwitchToMainThread()` to avoid MEAI background-thread crashes.
- 🛠 **`CoreAiPrefabRegistryAsset` automation** — `OnValidate` fills `Key` from AssetDatabase GUID and syncs `Name` when prefabs are assigned in the Inspector.

## [v0.15.0] — 2026-04-09

### Tool Calling Engine
- ✨ **Robust JSON extraction** — rewrote tool-call parsing in `LlmUnityMeaiChatClient.TryParseToolCallFromText`. Fragile regex removed; brace scanning (`IndexOf('{')`) tolerates missing closing fences (\`\`\`) and braces inside string args. PlayMode `MemoryTool_AppendsMemory` passes.
- ⚙️ **Reasoning-mode stripping** — preprocess strips `<think>...</think>` before JSON parse so “thinking aloud” (DeepSeek) does not break tool JSON.

### Editor UX
- ✨ **Auto plugin load** — `[InitializeOnLoadMethod]` in `CoreAIBuildMenu` generates required `ScriptableObject` assets (`CoreAiSettingsAsset`, routing manifests, permissions) under `Settings/` and `Resources/` on project load / import.
- ✨ **Quick Settings** — **CoreAI → Settings** menu opens the global `CoreAISettings.asset` singleton.

## [v0.14.0] — 2026-04-09
### Agent Memory & Persistence
- ✨ **Persistent chat history** — full dialog context survives between play sessions.
  - `WithChatHistory(persistToDisk: true)` on `AgentBuilder` (or `RoleMemoryConfig`) enables disk persistence.
  - Files live under `Application.persistentDataPath/CoreAI/AgentMemory/`.
  - Orchestrator reloads JSON on restart; ephemeral fallback when disk persistence is off.
- 🧪 PlayMode `ChatHistoryPlayModeTests` cover context restore after scene/engine “restart”.

## [v0.13.0] — 2026-04-09
### Action / Event System
- ✨ **`DelegateLlmTool`** — generic `ILlmTool` that exposes any C# delegate (Action/Func) to the LLM via MEAI with JSON schema inferred from the signature.
- ✨ **`CoreAiEvents`** — tiny built-in static pub/sub bus linking agents to game code without extra deps.
- ✨ **`AgentBuilder` extensions:**
  - `WithAction(name, description, delegate)` — wire a method straight to the agent.
  - `WithEventTool(name, description, hasStringPayload)` — emit triggers on `CoreAiEvents`.
- 🧪 EditorMode `CoreAiEventsEditModeTests`.

## [v0.12.0] — 2026-04-08

### Architecture
- **Single `ILog` logger** — collapsed the dual-logger setup
  - `ILog` adds `Debug/Info/Warn/Error(msg, tag)`
  - `LogTag` subsystem strings (`Core`, `Llm`, `Lua`, `Memory`, `Config`, `World`, `Metrics`, `Composition`, `MessagePipe`)
  - `Log.Instance` static + VContainer DI both supported
  - `NullLog` default no-op for tests / pre-DI

- **`MemoryToolAction` unification** — one enum definition
  - Moved to `MemoryToolAction.cs`
  - Removed duplicates from `AgentBuilder.cs` and `AgentMemoryPolicy.cs`
  - `AgentBuilder.WithMemory(defaultAction)` now applies correctly

### Changed
- **Core tool classes** use `ILog` tags:
  - `MemoryTool` → `LogTag.Memory`
  - `LuaTool` → `LogTag.Lua`
  - `GameConfigTool` → `LogTag.Config`
  - `InventoryTool` → `LogTag.Llm`
- `CoreAIGameEntryPoint` — `IGameLogger` → `ILog`
- `CoreServicesInstaller` — registers `ILog` (`UnityLog`) and sets `Log.Instance`
- `GameLoggerUnscopedFallback` — bridges `Log.Instance` before DI boots
- Removed manual `Log.Instance` wiring from `CoreAILifetimeScope` (now in `CoreServicesInstaller`)

### Unity implementation
- `UnityLog` — `ILog` impl mapping `LogTag` to `GameLogFeature` flags
- `IGameLogger` kept for Unity layer (`FilteringGameLogger`, `GameLogSettingsAsset`)
- Tag filtering still driven by `GameLogSettingsAsset` in the Inspector

## [v0.11.0] — 2026-04-07

### Added
- **Universal system prompt prefix** — shared preamble for every agent
  - `CoreAISettings.UniversalSystemPromptPrefix` static property for code-driven setup
  - Prepended to **every** system prompt (built-in and custom)
  - Centralizes cross-model rules without duplication
  - `BuiltInAgentSystemPromptTexts.WithUniversalPrefix()` helper
  - `BuiltInDefaultAgentSystemPromptProvider` applies it automatically
  - `AgentBuilder.Build()` applies it to custom agents
- **Global sampling temperature** — `CoreAISettings.Temperature` (default **0.1**) for all agents and both backends (LLMUnity + HTTP API)
- **`AgentBuilder.WithTemperature(float)`** — per-agent override; `AgentConfig.Temperature` stores it (defaults to `CoreAISettings.Temperature`)
- **`MaxToolCallIterations`** — moved from hardcode to `CoreAISettings.MaxToolCallIterations` (default 2); caps tool rounds per request; `MeaiLlmClient` reads the setting

## [v0.10.0] — 2026-04-06

### Added
- **WorldCommand as MEAI tool call** — LLM-driven world control via function calling
  - `IWorldCommandExecutor` — engine-agnostic contract in **CoreAI**
  - `WorldTool.cs` — MEAI `AIFunction` (CoreAiUnity)
  - `WorldLlmTool.cs` — `ILlmTool` wrapper (CoreAiUnity)
  - Actions: `spawn`, `move`, `destroy`, `load_scene`, `reload_scene`, `bind_by_name`, `set_active`, `play_animation`, `show_text`, `apply_force`, `spawn_particles`, `list_objects`
- **`list_objects`** — enumerate scene hierarchy objects (name, position, active, tag, layer, child count) with optional name filter
- **`play_animation`** — play clips on Animator or legacy Animation via `Animator.runtimeAnimatorController.animationClips`
- **`list_animations`** — list available clips from the AnimatorController; resolve targets by `instanceId` or `targetName`
- **`targetName` on commands** — name-based targeting alongside `instanceId` for move/destroy/set_active/play_animation/apply_force/spawn_particles (`_instances` first, then `GameObject.Find`)
- `WorldToolEditModeTests.cs` / `WorldCommandPlayModeTests.cs` — coverage for world tools
- **Inspector debug logging on `CoreAISettingsAsset`**
  - `LogLlmInput` — prompts (system/user) + tools
  - `LogLlmOutput` — model replies + tool results
  - `EnableHttpDebugLogging` — raw HTTP JSON
- `tool_call_id` on tool messages for LM Studio
- Idempotent `MemoryTool.append` to stop duplicate appends when the model loops

### Changed
- `MeaiOpenAiChatClient` — tool results read from `msg.Contents`
- `MemoryTool.ExecuteAsync` — returns JSON strings for correct serialization
- `TestAgentSetup` — adds `WorldExecutor` for PlayMode
- Dropped `LogAssert.Expect` for connection errors in PlayMode (only when host is down)

### Fixed
- Tool results were empty (`[tool]` content) — fixed `Contents` extraction
- LM Studio 400 — required `tool_call_id` on tool messages
- Memory append triple-writes — idempotency guard
- Write test flakiness — clarified hint text

---

## [v0.9.0] — 2026-04-06

### Added
- `MeaiLlmClient` — single MEAI client for every backend
  - `MeaiLlmClient.CreateHttp(settings, logger, memoryStore)` — HTTP API
  - `MeaiLlmClient.CreateLlmUnity(unityAgent, logger, memoryStore)` — local GGUF
- `MeaiOpenAiChatClient` — MEAI `IChatClient` for HTTP
- `LlmUnityMeaiChatClient` — MEAI `IChatClient` for LLMUnity (split out)
- `OfflineLlmClient` — deterministic canned replies per role (replaces stub)
- `CoreAISettings.ContextWindowTokens` — default context size (8192)
- `AgentBuilder.WithChatHistory(int?)` — inherit or override history window
- `AgentConfig.ContextWindowTokens` / `WithChatHistory`
- `CoreAISettingsAsset.AutoPriority` — `LlmUnityFirst` vs `HttpFirst`
- Inspector **🔗 Test Connection** button
- `Docs/MEAI_TOOL_CALLING.md` — architecture notes

### Changed
- `MeaiLlmUnityClient` / `OpenAiChatLlmClient` — thin factories delegating to `MeaiLlmClient`
- PlayMode tests build `CoreAISettingsAsset` through the factory
- `LlmBackendType.Stub` → `LlmBackendType.Offline`
- `AGENT_BUILDER.md` — client creation examples
- Removed duplicate docs: `MEAI_FUNCTION_CALLING.md`, `README_MEAI.md`

### Architecture
- Shared MEAI pipeline for HTTP + LLMUnity
- `FunctionInvokingChatClient` handles automatic tool calling
- No manual text parsing for tool calls

---

## [v0.8.0] — 2026-04-06

### Added
- `CoreAISettingsAsset` — single ScriptableObject settings singleton
- `IOpenAiHttpSettings` — adapter interface for HTTP settings
- `OpenAiChatLlmClient(CoreAISettingsAsset)` constructor
- `CoreAISettingsAssetEditor` — custom Inspector
- Default `CoreAISettings.asset` in Resources
- LLMUnity options: `DontDestroyOnLoad`, `StartupTimeout`, `KeepAlive`
- Auto priority: LlmUnityFirst / HttpFirst

---

## [v0.7.0] — 2026-04-06

### Added
- Unified MEAI tool-calling format
- `LuaTool.cs` + `LuaLlmTool.cs`
- `InventoryTool.cs` + `InventoryLlmTool.cs`
- `CoreAISettings.cs` (static)
- `AgentBuilder` — fluent builder for custom agents
- `WithChatHistory()` — dialog history retention
- `WithMemory()` — persistent memory
- `AgentMode` — ToolsOnly, ToolsAndChat, ChatOnly
- Merchant NPC sample with tools

### Removed
- `AgentMemoryDirectiveParser` — superseded by the MEAI pipeline
