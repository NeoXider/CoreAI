# Changelog — `com.nexoider.coreaiunity`

Unity host: **CoreAI.Source** build, EditMode / PlayMode tests, Editor menus, documentation. Depends on **`com.nexoider.coreai`**.

## [1.4.0] - 2026-04-30

### 🛡️ Resilience: HTTP retry with Retry-After + exponential backoff

Inspired by [Kilo (OpenCode)](https://github.com/nicholasgriffintn/kilo) `retry.ts` — production-grade HTTP retry at the transport layer, independent of the tool-calling retry loop.

- **`MeaiOpenAiChatClient.BuildHttpException`** — parses `Retry-After-Ms` header (millisecond precision, used by Azure / LiteLLM) with priority over `Retry-After` (seconds). Both convert to `LlmClientException.RetryAfterSeconds`.
- **`LoggingLlmClientDecorator`** — new retry loop for `RateLimited` (429) and `BackendUnavailable` (5xx):
  - Attempts: `settings.MaxLlmRequestRetries` (injected via `LlmPipelineInstaller`).
  - Delay: server `Retry-After` header → exponential backoff `2s → 4s → 8s → 16s → 30s` (capped).
  - Log: `LLM ↺ traceId=… | RateLimited — retry 1/2 after 30s`.
  - Tool-call errors are **not affected** — same immediate, count-based retry as before.
- **`LlmPipelineInstaller`** — passes `settings.MaxLlmRequestRetries` to `LoggingLlmClientDecorator`.

### 🔧 Resilience: TryRepairToolName — automatic tool name casing repair

Inspired by Kilo's `experimental_repairToolCall` hook — model writes `MEMORY` instead of `memory`, system silently fixes it before execution.

- **`ToolExecutionPolicy.TryRepairToolName`** — case-insensitive lookup among registered `ILlmTool` names. Returns a new `FunctionCallContent` with the corrected name, or `null` if the tool is genuinely unknown.
- Called in `ExecuteSingleAsync` before `AIFunction` resolution — completely transparent to calling code.
- When no tools are registered (e.g. tools only in `ChatOptions`), skips repair and passes through.
- On unknown tool: structured error with available tool names for model self-correction.

### 🧪 Tests

**EditMode (12 new tests):**
- `TryRepairToolName_ExactMatch_ReturnsSameFc` — exact match passes through.
- `TryRepairToolName_WrongCase_ReturnsRepaired` — `MEMORY` → `memory`.
- `TryRepairToolName_MixedCase_ReturnsRepaired` — `Spawn_Quiz` → `spawn_quiz`.
- `TryRepairToolName_UnknownTool_ReturnsNull` — genuinely unknown tool.
- `TryRepairToolName_NullFc_ReturnsNull` — null guard.
- `ExecuteSingle_WrongCaseName_IsRepaired` — end-to-end: `MEMORY` executes successfully.
- `ExecuteSingle_TrulyUnknownTool_ReturnsFailed` — error with available tool list.
- `ComputeBackoff_ExponentialCurve_CappedAt30` — backoff curve: `2→4→8→16→30`.
- `ToolCallInMiddleOfLongText_PrefixAndSuffixPreserved`.
- `CodeBlockFollowedByRealToolCall_OnlyRealCallExtracted`.
- `ToolCallWithArrayArguments_ExtractedCorrectly`.
- `CleanedText_IsTrimmable_NoLeadingTrailingJson`.

**PlayMode (3 new hybrid real-LLM tests — `ToolNameRepairPlayModeTests.cs`):**
- `WrongCasing_Repair_ToolExecuted_RealLlmContinues` — scripted `MEMORY` → repair → tool executes → real LLM responds.
- `UnknownTool_ErrorFedBack_RealLlmSelfCorrects` — scripted unknown tool → error in chat history → real LLM self-corrects.
- `MixedCaseWithTextPrefix_ToolRepaired_TextPreserved` — `"Working on it... {\"name\":\"Memory\",...}"` → repair + prefix preserved.

### Coverage matrix

All changes work across **every LLM mode**: `Auto`, `Local Model`, `Client Owned Api`, `Client Limited`, `Server Managed Api`. All modes delegate internally to `MeaiLlmClient` → `SmartToolCallingChatClient` → `ToolExecutionPolicy`.

### Meta

- Package **`1.4.0`**. Dependency **`com.nexoider.coreai 1.4.0`** (bumped — adds `TryRepairToolName`, retry backoff).


### Tool-calling test coverage: chain, parallel, native+text, fail/success reset

Adds the scenarios that the 1.3.0 fix did not pin down explicitly. Code paths from 1.3.0 are unchanged — these tests guard the **edges** so future regressions in any one of them surface fast.

- **`ToolCallExtractionParityEditModeTests.NonStreaming_ChainOfTwoToolsThenText_ExecutesBothAndStripsAll`** — model emits `tool_a → tool_b → "Done."` across three iterations (text-shape JSON each time). Both tools execute exactly once, both traces captured in order, the final assistant text contains `"Done."` and **neither** tool's JSON.
- **`ToolCallExtractionParityEditModeTests.NonStreaming_TwoParallelToolCalls_BothExecuteInSameIteration`** — single response returns two native `FunctionCallContent` items. `ToolExecutionPolicy.ExecuteBatchAsync` runs both; trace list has both with `source=native`; loop terminates after the second iteration's text reply.
- **`ToolCallExtractionParityEditModeTests.NonStreaming_NativeToolCallWithTextPrefix_NativeWins_TextNotLeaked`** — response has both a `TextContent` containing pseudo-JSON and a real `FunctionCallContent`. Native path takes priority (text-extraction is gated on `nativeCalls.Count == 0`), so no phantom call is invented from the prefix.
- **`ToolCallExtractionParityEditModeTests.Streaming_FailureThenSuccess_ResetsConsecutiveErrorsAndContinues`** — flaky tool fails on iteration 1, succeeds on iteration 2 (different args), text on iteration 3. Confirms `ToolExecutionPolicy.RecordSuccess()` resets the counter so the third turn doesn't trip max-errors. Final `IsDone` chunk carries `[fail, success]` traces and `Error == null`.

### Meta

- Package **`1.3.1`**. Dependency **`com.nexoider.coreai 1.3.0`** (unchanged — this is a tests-only release).

## [1.3.0] - 2026-04-30

### Tool calling: stream/non-stream parity + diagnostics

Unifies the tool-calling cycle so providers emitting tool calls as **JSON-in-text** (Ollama, llama.cpp, LM Studio, some Qwen builds) behave identically across streaming and non-streaming paths. Production symptom: `memory` tool emitted as text after a goodbye line, JSON leaked into the chat panel, no persistence happened.

- **`SmartToolCallingChatClient.GetResponseAsync`** — non-streaming loop now also scans every `MEAI.TextContent` in the response for tool-call JSON and feeds the resulting `FunctionCallContent` through the same `ToolExecutionPolicy` path as native calls. The cleaned text replaces the raw assistant text on the next iteration so the model does not see its own JSON twice. Public read-only **`LastExecutedToolCalls`** mirrors what the streaming path returns.
- **`MeaiLlmClient.CompleteStreamingAsync`** — extraction gate switched from "AIFunction count > 0" to "request.Tools count > 0". When a tool was *requested* but no AIFunction was bound (e.g., `MemoryLlmTool` with `IAgentMemoryStore == null`), the loop now strips the JSON, logs a warning, and emits cleaned text instead of leaking the raw tool call. A startup warning fires once when this mismatch is detected.
- **`MeaiLlmClient.BuildAIFunctions`** — now logs a warning when `MemoryLlmTool` is requested for a role but `_memoryStore` is `null`, instead of silently dropping it.
- **`AiOrchestrator`** (defense-in-depth) — both sync and streaming paths now run **`LlmToolCallTextExtractor.StripForDisplay`** on the assistant text before persisting to chat history or publishing **`ApplyAiGameCommand`**. Logs `tool-call JSON leaked through extraction; stripped for chat/envelope` if the strip changed anything.

### Diagnostics: per-call log line + tail summary

- **`ToolExecutionPolicy`** — emits a dedicated `[ToolCall]` log line after every tool invocation (native, text-extracted, missing, duplicate). Format: `[ToolCall] traceId=… role=… tool=memory status=OK dur=12ms args={…} result=…`. Honours **`ICoreAISettings.LogToolCalls`** / `LogToolCallArguments` / `LogToolCallResults` independently of the verbose `LogMeaiToolCallingSteps` switch.
- **`LlmToolCallTrace`** (new portable struct in `CoreAI.Ai`) — one `(name, success, durationMs, source)` record per call. Source is one of `native` / `text` / `duplicate` / `missing`.
- **`LlmCompletionResult.ExecutedToolCalls`** / **`LlmStreamChunk.ExecutedToolCalls`** — same trace list, populated for both paths (final chunk only on streaming).
- **`LoggingLlmClientDecorator`** — appends `tools=[memory(ok,12ms),other(fail,0ms,duplicate)]` to the final `LLM ◀` line. Empty tail when no tools fired so plain text turns stay one-line.

### Portable extractor

- **`CoreAI.Ai.LlmToolCallTextExtractor`** (new in `com.nexoider.coreai 1.3.0`) — engine-agnostic `TryExtract` / `StripForDisplay`, available to anything that depends on the portable core. Existing **`MeaiLlmClient.TryExtractToolCallsFromText`** / `StripEmbeddedToolCallJsonForDisplay` keep their public surface for backward compatibility.

### Tests

- **EditMode:** `ToolCallExtractionParityEditModeTests` — non-streaming text-shaped tool execution + strip; missing-tool synthetic trace; streaming JSON-strip when AIFunction not bound; pass-through when no tools requested; `FormatExecutedTools` rendering; portable-extractor multi-match.

### Meta

- Package **`1.3.0`**. Dependency **`com.nexoider.coreai 1.3.0`** (bumped — adds `LlmToolCallTextExtractor` and `LlmToolCallTrace` / `ExecutedToolCalls`).

## [1.2.6] - 2026-04-30

### Composition: `GlobalMessagePipe` in minimal PlayMode fixtures

- **`GlobalMessagePipeMinimalBootstrap.EnsureInitializedForLlmDiagnostics`** — registers MessagePipe brokers for `LlmRequestStarted` / `LlmRequestCompleted` / `LlmUsageReported` / `LlmToolCallStarted` / `LlmToolCallCompleted` / `LlmToolCallFailed` / `LlmBackendSelected` and calls **`GlobalMessagePipe.SetProvider`** when no provider exists yet. **`ToolExecutionPolicy`** otherwise skips publishing tool-call events (`GlobalMessagePipe.IsInitialized` guard).
- **`TestAgentSetup.Initialize`** — invokes the bootstrap at start so PlayMode tests without `CoreAILifetimeScope` can subscribe to **`GlobalMessagePipe.GetSubscriber<LlmToolCallCompleted>()`** and observe real tool traffic.
- **`TestAgentSetup` orchestrator** — uses **`CoreAISettingsAsset.Instance`** (when present) as **`ICoreAISettings`** for `AiOrchestrator` so timeouts and logging flags match the HTTP/MEAI client settings.
- **PlayMode:** `AgentMemoryOpenAiApiPlayModeTests` — verbose LLM logging toggle, explicit `AgentMemoryState.Memory` assertions (non-empty write, append preserves baseline + marker, clear removes row), orchestrator reply logging.
- **EditMode:** `GlobalMessagePipeMinimalBootstrapEditModeTests` — idempotent bootstrap + publish/subscribe smoke for `LlmToolCallCompleted`.
- **Docs:** `ARCHITECTURE.md`, `DEVELOPER_GUIDE.md` — note bootstrap + PlayMode `TestAgentSetup` behaviour.
- Package **`1.2.6`**. Dependency **`com.nexoider.coreai 1.2.1`** (unchanged).

## [1.2.5] - 2026-04-30

### Chat: hide leaked tool-call JSON in assistant bubble (LLMUnity / text-shaped tools)

- **`MeaiLlmClient.TryExtractToolCallsFromText`** — second pass runs **`FindToolCallJsonSpans`** on **raw** assistant text when the first pass (which ignores brace characters inside fenced `` ``` `` blocks ) finds nothing, so JSON wrapped as `` ```json ... ``` `` is discoverable again and tools still execute in-stream.
- **`MeaiLlmClient.StripEmbeddedToolCallJsonForDisplay`** — host UI can strip any remaining leaked JSON using the same rules (no tool execution).

### Meta

- Package **`1.2.5`**. Dependency **`com.nexoider.coreai 1.2.1`** (unchanged).

## [1.2.4] - 2026-04-29

### Docs + tests: custom chat roles and `ToolsOnly`

- **`README_CHAT.md`** — section *Custom roles — not locked to “one persona”* (`CoreAiChatConfig.RoleId`, registering multiple roles, **`AgentMode.ToolsOnly`** expectations, host-only `BuildAiTaskRequest` policy, EN + RU `<details>` summary). Cross-links to tool policy and streaming/tool sections.
- **EditMode:** `CoreAiChatPanelBuildRequestEditModeTests` — default `BuildAiTaskRequest` shape (`RoleId`, `Hint`, `SourceTag`, `AllowedToolNames` null) and subclass allowlist injection.
- **PlayMode:** `CoreAiChatPanelBuildRequestPlayModeTests` — same checks in a player frame (no LLM; complements EditMode for lifecycle/domain differences).
- Package **`1.2.4`**. Dependency **`com.nexoider.coreai 1.2.1`** (unchanged).

## [1.2.3] - 2026-04-29

### Docs: chat host hook for tool policy (`BuildAiTaskRequest`)

- **`CoreAiChatPanel.BuildAiTaskRequest(string, string)`** — clarified in xmldocs: default minimal `AiTaskRequest` (`RoleId` + `Hint` + `SourceTag=Chat`); hosts override to inject **tool policy** (`AllowedToolNames`, `ForcedToolMode`, `RequiredToolName`, etc.). The same override is used for **typed UI sends** and **`SubmitMessageFromExternalAsync`** (both build the request through this method).
- **`README_CHAT.md`** — new subsection *Custom `AiTaskRequest` (tool policy)* describing the override pattern and parity with streaming / orchestrator.
- **`IChatRequestConfigurator`** — xmldocs corrected: no longer reference non-existent `CoreAiChatExternalSubmitOptions.ConfigureRequest` or claim registration on `CoreAiChatPanel`; the interface remains a **preview** contract for future DI-style wiring; until then **`BuildAiTaskRequest`** is the supported extension point.
- Package **`1.2.3`**. Dependency **`com.nexoider.coreai 1.2.1`** (unchanged).

## [1.2.2] - 2026-04-29

### Streaming parity + `AllowedToolNames` empty = no tools

- **`CoreAi.StreamChunksAsync(AiTaskRequest, CancellationToken)`** — forwards to `CoreAiChatService.SendMessageStreamingAsync` so hosts pass `AllowedToolNames` / `ForcedToolMode` on streaming turns.
- Depends on **`com.nexoider.coreai 1.2.1`** (orchestrator: empty allowlist strips tools; see Core CHANGELOG).
- **EditMode:** `AiOrchestratorHistoryEditModeTests` — empty allowlist + sync vs streaming tool parity; `CoreServicesInstallerEditModeTests` — TearDown no longer calls `SetProvider(null)`.

## [1.2.1] - 2026-04-29

### WebGL packaging + DI regression test

- **UPM `link.xml`** at package root `Assets/CoreAiUnity/link.xml` (the monorepo file `Assets/link.xml` is **not** inside `path=Assets/CoreAiUnity`, so consumers need the copy in the package folder).
- **EditMode:** `CoreServicesInstallerEditModeTests.RegisterCore_Builds_AndResolves_IAiGameCommandSink_As_MessagePipeSink` — guards `RegisterCore` + factory-registered `IAiGameCommandSink` against VContainer constructor-analysis failures on IL2CPP.
- **Docs:** WebGL / IL2CPP note in `DEVELOPER_GUIDE.md` §2.1.
- Package **`1.2.1`**. Dependency **`com.nexoider.coreai 1.2.0`** (unchanged).

## [1.2.0] - 2026-04-29

### WebGL / IL2CPP DI

- **`MessagePipeAiCommandSink`** — registered via an explicit factory in `CoreServicesInstaller` so VContainer does not rely on constructor metadata analysis (fixes `VContainerException: Type does not found injectable constructor` on WebGL builds). `[Preserve]` on the sink and `link.xml` entry for `CoreAI.Source` avoid managed-code stripping edge cases.

### RedoSchool orchestration support

- Added Unity DI registration for the default tool-call history and no-op agent trace sink.
- Added EditMode coverage for per-role runtime context, allowed tool filtering, chat-only tool suppression, scripted LLM responses, structured tool result envelopes, and tool-call history.
- Package **`1.2.0`**. Dependency **`com.nexoider.coreai 1.2.0`**.

## [1.1.0] - 2026-04-29

### Portable LLM routing adapter

- ✨ **Manifest to core route table** — `LlmRoutingManifest` now converts profiles and rules into portable `LlmRouteTable`.
- 🔧 **Registry uses core resolver** — `LlmClientRegistry` keeps Unity-specific client construction, but route matching now goes through `CoreAI.Core` `LlmRouteResolver`.
- ✨ **Production policy surface** — Unity can now build on core entitlement, usage, auth context, and provider error contracts while keeping ScriptableObjects, VContainer, HTTP/SSE, and LLMUnity in the Unity package.
- 🧪 **EditMode coverage:** added route resolver priority, route table validation, manifest conversion, provider error mapping, and usage aggregation tests.
- 🔧 Package **`1.1.0`**. Dependency **`com.nexoider.coreai 1.1.0`**.

## [1.0.3] - 2026-04-29

### Chat UX and HTTP model selection

- 🐛 **Stop button availability** — chat Stop is now available for any active request, including non-streaming requests and the tail after the final streaming chunk.
- 🔧 **Enter/Shift+Enter default** — new chat configs use `Enter` to send and `Shift+Enter` for a newline. Legacy Shift+Enter-to-send remains available through `CoreAiChatConfig.SendOnShiftEnter`.
- ✨ **HTTP model presets** — `CoreAISettingsAssetEditor` keeps the free-form model field and adds a preset dropdown for common OpenAI-compatible model ids.
- 🧪 **EditMode coverage:** updated chat config defaults and added hotkey contract regressions.
- 📝 **Docs:** chat README updated for the new send/newline behavior.
- 🔧 Package **`1.0.3`**. Dependency **`com.nexoider.coreai 1.0.3`**.

## [1.0.2] - 2026-04-28

### Long context and tool-call identity

- ✨ **Context compaction in orchestration** — `AiOrchestrator` now asks the portable context manager to prepare chat history. Recent turns stay in `ChatHistory`; older turns become a `## Conversation Summary` system section when the token budget is tight.
- ✨ **Tool lifecycle identity** — `ToolExecutionPolicy` publishes `LlmToolCallInfo` with `CallId` for start/completed/failed events, making async and parallel diagnostics correlate to the exact provider tool call.
- 🧪 **EditMode coverage:** added regressions for deterministic context summary behavior and awaited async tool execution.
- 📝 **Docs:** architecture and developer guide updated for context management and tool-call event identity.
- 🔧 Package **`1.0.2`**. Dependency **`com.nexoider.coreai 1.0.2`**.

## [1.0.1] - 2026-04-28

### Production runtime extension points

- ✨ **ServerManagedApi production path** — added `ServerManagedLlmClient` and `ServerManagedAuthorization.SetProvider(...)` so WebGL and SaaS projects can call a backend proxy with dynamic user/session tokens while keeping provider keys off the client.
- ✨ **Usage and typed error propagation** — `RoutingLlmClient` now publishes `LlmUsageReported`, forwards typed `LlmErrorCode` values, and maps HTTP auth/quota/rate-limit/backend failures into stable categories.
- ✨ **Runtime prompt context and scoped memory** — Unity composition can consume the new Core contracts for per-request context and user/session/topic memory isolation.
- ✨ **Tool lifecycle observability** — Unity registers brokers and publishes tool start/completed/failed events around MEAI tool execution.
- ✨ **Production diagnostics** — `CoreAI/Validate Production Settings` and the settings inspector warn when WebGL is configured with `ClientOwnedApi` and a non-empty API key.
- 🧪 **EditMode coverage:** targeted production-extension run passed `12/12` for routing usage events, ServerManaged auth hook, scoped memory, and runtime prompt context.
- 📝 **Docs:** architecture, settings, developer guide, and changelogs updated for production extension points.
- 🔧 Package **`1.0.1`**. Dependency **`com.nexoider.coreai 1.0.1`**.

## [1.0.0] - 2026-04-28

### LLM execution modes and routing

- ✨ **Four public LLM modes** — `LocalModel`, `ClientOwnedApi`, `ClientLimited`, and `ServerManagedApi` are now first-class runtime concepts over the existing LLMUnity / OpenAI-compatible HTTP / Offline clients.
- ✨ **Single-mode and mixed-mode routing** — `CoreAISettingsAsset` configures a simple global mode, while `LlmRoutingManifest` profiles can keep several modes active at once for different roles in the same scene.
- ✨ **ClientLimited guard** — added local request and prompt-size limits through `ClientLimitedLlmClientDecorator`.
- ✨ **MessagePipe observability** — Unity registers brokers for `LlmBackendSelected`, `LlmRequestStarted`, and `LlmRequestCompleted`; `RoutingLlmClient` publishes routing diagnostics for UI subscribers.
- 🔧 **Editor UX** — `CoreAISettingsAssetEditor` exposes the public LLM mode field, single-mode vs routing-profile guidance, ClientLimited limit fields, and ServerManagedApi key-safety guidance.
- 🧪 **EditMode coverage:** focused tests for settings helpers, routing metadata/events, ClientLimited limits, and mixed-mode manifest resolution. Targeted run: 16/16 passed.
- 📝 **Docs:** architecture, settings, quick start, developer guide, docs index, chat README, package READMEs, and changelogs updated for the 1.0.0 mode surface.
- 🔧 Package **`1.0.0`**. Dependency **`com.nexoider.coreai 1.0.0`**.

## [0.25.14] - 2026-04-27

### CoreAiChatPanel (streaming, stop, history, display)

- 🐛 **Second message no longer cancels the first** — streaming stays “busy” until the full `RunStreamingAsync` enumerator completes (including orchestrator post-work after the last token). Enter no longer triggers the stop path while a turn is still finishing; stop remains on the send button (`X`) and Esc (when enabled).
- 🐛 **Per-turn request CTS** — avoids a race where the previous turn’s `finally` could dispose the active linked token for a new message.
- 🐛 **Persisted chat UI** — user rows saved as composer JSON (`hint`, `telemetry`, …) hydrate as the **`hint`** text instead of raw JSON.
- 🐛 **Assistant bubble layout** — leading whitespace/newlines from the model are trimmed for display so empty gaps do not appear above the first line.
- 🧪 **EditMode:** `FormatPersistedMessageForUi`, `NormalizeAssistantDisplayText` regressions.
- 📝 **`README_CHAT.md`** — documents send vs stop semantics, streaming completion, persisted `hint`, display trimming, and an in-editor screenshot (`chat-readme-example.png`) with the chat panel next to Unity Console (`[CoreAI] [Llm]`, MessagePipe).
- 🔧 Package **`0.25.14`**. Dependency **`com.nexoider.coreai 0.25.14`**.

## [0.25.13] - 2026-04-27

### MEAI compatibility tool binding

- 🐛 **`CompatibilityLlmTool` native argument binding** — the MEAI executor parameter is now named `ingredients`, matching the JSON schema. Valid model calls such as `{"ingredients":["Fire","Earth"]}` no longer fail before reaching the tool with a missing `ingredientsObj` argument.
- 🧪 **EditMode coverage:** added an `AIFunction.InvokeAsync` regression for `check_compatibility` using the public `ingredients` argument name.
- 🧪 **PlayMode stability:** `CoreAiChatServiceIntegrationPlayModeTests` now falls back to the returned task result when a streaming callback receives no text chunks, avoiding false failures on providers that emit only terminal chunks for short answers.
- 📝 **`MEAI_TOOL_CALLING.md`** — documents that .NET `AIFunction` parameter names must match `ILlmTool.ParametersSchema` property names.
- 🔧 Package **`0.25.13`**. Dependency **`com.nexoider.coreai 0.25.13`**.

## [0.25.12] - 2026-04-27

### Queue scheduling stability

- 🐛 **`QueuedAiOrchestrator` latest-wins scopes** — `CancellationScope` now cancels older active and pending work as soon as a newer task with the same scope is enqueued, including streaming tasks.
- 🐛 **Queue fairness and cancellation** — equal priorities are FIFO, streaming and non-streaming tasks share one effective priority order, and pending tasks observe external cancellation before they start.
- 🧪 **EditMode coverage:** added queue regressions for FIFO priority ties, pending scope supersession, pending external cancellation, pending stream supersession, `CancelTasks(scope)` for pending streams, and shared sync/stream priority.
- 📝 **`DEVELOPER_GUIDE.md`** — documents the queue contract: `MaxConcurrent`, `Priority`, `CancellationScope`, `CancelTasks(scope)`, and sync/stream scheduling.
- 🔧 Package **`0.25.12`**. Dependency **`com.nexoider.coreai 0.25.12`**.

## [0.25.11] - 2026-04-27

### Tool calling stability + world tool hardening

- ✨ **CoreAI tool contract prompt** — `AiOrchestrator` now injects a concise tool contract whenever a role has registered tools, so local models are nudged by the framework to call real tools with structured arguments instead of simulating them in prose.
- 🐛 **Structured retry keeps tool context** — structured-response retries preserve registered tools, chat history, forced tool mode, required tool name, and response-token budget.
- 🐛 **`WorldLlmTool` main-thread execution** — direct `world_command` tool calls now marshal `ICoreAiWorldCommandExecutor.TryExecute(...)` through `UniTask.SwitchToMainThread` instead of forcing `Task.Run`. This avoids ThreadPool execution for Unity-facing world executors and aligns direct tool calls with the MessagePipe router contract.
- 🔧 **`WorldLlmTool` tool contract hardening** — descriptions now explicitly require `targetName` for animation commands such as `list_animations`, and invalid/missing argument responses use one centralized valid-action list plus action-specific missing-parameter messages.
- 🧪 **EditMode coverage:** added regressions for orchestrator tool-contract injection, `WorldLlmTool` missing `targetName` feedback, and world executor thread handling.
- 📝 **`MEAI_TOOL_CALLING.md` / `WORLD_COMMANDS.md` / `DEVELOPER_GUIDE.md`** — documented the orchestrator-level tool contract, direct world-command main-thread execution, and beginner/pro MessagePipe extension points.
- 🔧 Package **`0.25.11`**. Dependency **`com.nexoider.coreai 0.25.11`**.

## [0.25.10] - 2026-04-27

### File-backed memory store + docs

- 🐛 **`FileAgentMemoryStore.ClearChatHistory`** — after dropping in-memory chat for a role, the internal “history loaded” flag is reset so the **same store instance** can call `GetChatHistory` / `AppendChatMessage` again without `KeyNotFoundException` (regression covered by **`FileAgentMemoryStoreEditModeTests.ClearChatHistory_SameStoreInstance_GetChatHistory_IsSafe`**).
- 📝 **`MemorySystem.md`** — notes that `RoleMemoryConfig` defaults treat persisted chat as off unless chat history is enabled or set explicitly (see **`com.nexoider.coreai` 0.25.10**).
- 📝 **`MEMORY_STORE_CUSTOM_BACKENDS.md`** — custom `IAgentMemoryStore` implementations should invalidate any per-role RAM cache when implementing `ClearChatHistory`, same contract as the reference file store.
- 🔧 Package **`0.25.10`**. Dependency **`com.nexoider.coreai 0.25.10`**.

## [0.25.9] - 2026-04-27

### Per-agent MaxOutputTokens + LLMUnity asmdef wiring helper

- ✨ **Per-agent output budget:** `AgentBuilder.WithMaxOutputTokens(int? tokens)` stores a role-level response token cap in `AgentMemoryPolicy.RoleMemoryConfig.MaxOutputTokens`. Orchestrator priority is now `AiTaskRequest.MaxOutputTokens` (per-call) → per-agent (`WithMaxOutputTokens`) → global `CoreAISettings.MaxTokens` → provider default.
- 🐛 **LLMUnity package detection:** all CoreAI asmdefs use the real UPM package name **`ai.undream.llm`** in `versionDefines` (`COREAI_HAS_LLMUNITY`). The assembly references remain `undream.llmunity.Runtime` / `.Editor`, which are the assembly names exposed by the package.
- ✨ **`CoreAISettingsAssetEditor` — LLMUnity status helper.** The LLMUnity foldout now reports whether package `ai.undream.llm` is installed and whether `COREAI_HAS_LLMUNITY` is active. If the package is installed but the define is missing, **Auto-fix asmdef wiring** updates the four CoreAI asmdefs and refreshes the AssetDatabase.
- 🧪 **EditMode:** added per-agent MaxOutputTokens priority tests in the orchestrator plumbing suite.
- 🔧 Package version **`0.25.9`**. Dependency `com.nexoider.coreai 0.25.9+`; package versions are aligned.

## [0.25.8] - 2026-04-27

### 🎛️ CoreAISettings inspector + unified MaxTokens fallback for both backends

- 🐛 **`CoreAISettingsAssetEditor` — GGUF model picker fixed.** Previously a stray `EditorGUILayout.TextField` rendered **below** the popup re-read `ggufPathProp.stringValue` and overwrote the popup's just-applied selection on the same frame (symptom: pick a model in the dropdown → it reverts to `[ Auto / Fallback ]` on next repaint). Refactored into `DrawGgufModelDropdown(SerializedProperty)`:
  - Popup lists `LLMManager.modelEntries` (already-downloaded GGUF files in LLMUnity Model Manager) plus a leading `[ Auto / Fallback ]` entry.
  - **`LLMManager.LoadFromDisk()`** is invoked on first paint (and via the new **↻** refresh button) so the popup is populated even when entries were not lazy-loaded yet.
  - **Browse…** opens `EditorUtility.OpenFilePanel` and writes the selected `.gguf` filename to the property.
  - Separate **Manual override** `DelayedTextField` for typing a filename by hand (applies on Enter / focus-loss, no longer races with the popup).
  - Empty Model Manager — informative `HelpBox` instead of an empty silent popup.
  - Without the LLMUnity package — graceful fallback to a plain `PropertyField` + `HelpBox`.
- ✨ **`Max Output Tokens` moved from HTTP-only section into General settings** with an explicit tooltip stating it now applies uniformly to both HTTP API and LLMUnity. Previously it was hidden under the HTTP API foldout, suggesting it was provider-specific — that was misleading once the field actually became consumed by both backends.
- ✨ **Unified `MaxTokens` for HTTP API and LLMUnity.** Previously `CoreAISettings.MaxTokens = 4096` was a read-only getter with **no consumer**: visible in the inspector, never applied to either backend (request stayed `null` → provider default). Now `MeaiLlmClient.ResolveMaxOutputTokens(perRequest)` back-fills `ChatOptions.MaxOutputTokens` from `ICoreAISettings.MaxTokens` (when positive) on **both** non-streaming (`CompleteAsync`) and streaming (`CompleteStreamingAsync`) paths. Both `MeaiOpenAiChatClient` (HTTP `req["max_tokens"]`) and `LlmUnityMeaiChatClient` (`_unityAgent.numPredict`) consume the same `ChatOptions` value, so behaviour is symmetric.
- ✨ **Per-call override via `AiTaskRequest.MaxOutputTokens` (`int?`)** — symmetric with `ForcedToolMode`/`RequiredToolName`. Forwarded by `AiOrchestrator` (`RunTaskAsync`, `RunStreamingAsync`, structured-retry) into `LlmCompletionRequest.MaxOutputTokens`. Application code can still call the LLM client directly with `LlmCompletionRequest.MaxOutputTokens` for finer control.
- 🔧 **Effective priority:** `LlmCompletionRequest.MaxOutputTokens` (per-request direct call) → `AiTaskRequest.MaxOutputTokens` (per-call via orchestrator) → `ICoreAISettings.MaxTokens` (global default in `CoreAISettings.asset`) → provider default. Set `MaxTokens = 0` in the asset to opt out of the global fallback.
- 🧪 **`MaxTokensFallbackEditModeTests`** — 4 new tests through `MeaiLlmClient` covering: settings-default fallback (non-streaming + streaming), per-request override wins, `MaxTokens=0` leaves provider default. Existing 552 EditMode tests continue to pass.
- 🔧 **TODO (next):** dual-backend at runtime (primary + secondary, per-role routing via existing `RoutingLlmClient` + `LlmRoutingManifest`). Captured in [`TODO.md`](../../TODO.md).
- 🔧 Package version **`0.25.8`**. Dependency `com.nexoider.coreai 0.25.4+` (new `MaxTokens` interface member with default-impl, new `AiTaskRequest.MaxOutputTokens`).

## [0.25.7] - 2026-04-27

### 🔧 Editor bootstrap + PlayMode resilience to HTTP 5xx

- 🔧 **`CoreAIBuildMenu`** — auto-creation of `CoreAISettings.asset` is deferred to **`EditorApplication.delayCall`**: avoid duplicating or overwriting the asset in the same frame as domain reload; if the file already exists on disk but import has not picked it up yet — **`ImportAsset(ForceSynchronousImport)`** instead of creating a new asset with defaults.
- 🧪 **PlayMode (real model):** `AgentMemoryWithRealModelPlayModeTests` — up to **3** recall attempts with **`WaitForSecondsRealtime(1s)`** between them; after retries, an empty response (HTTP 5xx from LM Studio, etc.) yields **`Assert.Ignore`** with a short hint instead of failing on an empty command sink; orchestrator unchanged.
- 📝 **`TROUBLESHOOTING.md`** — section **PlayMode: HTTP 500 from LM Studio / local API** (symptoms, cause, checklist).
- 🔧 Package version **`0.25.7`**.

## [0.25.6] - 2026-04-27

### 💬 Chat UI — stop during streaming and “fast” backends

- 🐛 **`CoreAiChatPanel`** — busy flag for UI send is set **before** the first `await`; streaming sets `_isStreaming` right after `Task.Yield()` (also reset in `finally` / on `Stop`) so the **X** button and `StopActiveGeneration` are not lost on stub / zero-delay backends; after cancel, **`FinishStreaming` + `HideTypingIndicator`** run.
- 🐛 **Stop button** — `TrySendInput()` handles the active request first, then the global lock; send stays enabled while generating because in that state it is the stop control.
- 📝 **Docs** — updated `README.md`, `CoreAiUnity/README.md`, `README_CHAT.md`, `DEVELOPER_GUIDE.md`, `STREAMING_ARCHITECTURE.md`, `DOCS_INDEX.md`.
- 🧪 **PlayMode:** `CoreAiChatPanelStopPlayModeTests` asserts active streaming/request cancels CTS and clears busy state via public `StopAgent()`.
- 🔧 Package version **`0.25.6`**.

## [0.25.5] - 2026-04-26

### 💬 Chat UI — header without duplicate Stop, programmatic API

- 🧹 **`CoreAiChat.uxml`** — removed **`coreai-chat-stop`** from the header: stopping generation remains the **send button** in **X** mode plus **Esc** (as in 0.22.0), no duplicate header control.
- ✨ **`CoreAiChatPanel.SubmitMessageFromExternalAsync(messageText, options, cancellationToken)`** — for code-driven flows (cutscenes, quests, world buttons): **`CoreAiChatExternalSubmitOptions.AppendUserMessageToChat`** (default `true`, user bubble), **`SimulatedAssistantReply`** — show assistant text **without calling the LLM**; returns final assistant text or `null` when the panel is busy / cancelled / empty text after **`OnMessageSending`**.
- ✨ Shared internal path **`RunAgentTurnAsync`** for UI and external submits; streaming / non-streaming return the final string to callers.
- 📝 **`README_CHAT.md`** — programmatic submit section; Stop section no longer references a header button.
- 🧪 **EditMode:** `CoreAiChatExternalSubmitOptionsEditModeTests` (option defaults); **`CoreAiChatConfigEditModeTests`** — **`LoadPersistedChatOnStartup`** / **`MaxPersistedMessagesForUi`**.
- 🔧 Package version **`0.25.5`**.

## [0.25.4] - 2026-04-26

### 💬 Chat UI — session restore on startup

- ✨ **`CoreAiChatPanel`** — after `OnEnable` and `InitService`, **`HydrateStartupMessagesFromStore()`** clears the message list and loads persisted history from **`IAgentMemoryStore`** for **`CoreAiChatConfig.RoleId`** when **`Load Persisted Chat On Startup`** is enabled (default **on**). If history is **non-empty**, the welcome line is **not** shown on top; if empty, **`Welcome Message`** shows as before.
- ✨ **`CoreAiChatConfig`** — **“Session / history”** section: **`Load Persisted Chat On Startup`**, **`Max Persisted Messages For Ui`** (0 = all messages from the store).
- ✨ **`CoreAiChatService.TryGetPersistedChatHistory`** — reads `ChatMessage[]` for UI/integrations without duplicating store access.
- 📝 **`README_CHAT.md`** — session restore section and conditions (persist chat in `AgentMemoryPolicy`, `FileAgentMemoryStore` path).
- 🔧 Package version **`0.25.4`**.
- 🐛 **`CoreAiChat.uss` + `ScrollToBottom`** — message list: **`justify-content: flex-end`** and **`min-height: 100%`** on the scroll content so short threads **stick to the bottom** (near the input) without the “welcome sat under the header then jumped down” effect on first message. Scroll-to-bottom runs **twice** on adjacent schedule ticks to account for `highValue` relayout.

### 📊 LLM — prompt budget logs (`LoggingLlmClientDecorator`)

- ✨ **`LLM ▶` / `LLM ◀`** lines include an expanded **`promptBudget`**: **system** split into total / **core** / **memory** (`## Memory` marker as in `AiOrchestrator`) / **tools** catalog estimate from `request.Tools`; **chat** (user payload); rough **estTok** and **words**; when the API returns **usage**, the same metrics appear in the suffix plus **`outWords≈`** for completions.

## [0.25.3] - 2026-04-26

### 💬 Chat UI — C / Esc hotkeys, global poll, UITK focus

- ✨ **`CoreAiChatPanel`** — while **collapsed** (FAB), **C** (Latin, no Ctrl/Cmd/Alt) opens the panel; while **expanded**, **Esc** stops active generation if a request/stream is running, otherwise **collapses** chat. **Esc** is handled at `TrickleDown` on the root (`OnRootKeyDown`) so `TextField` does not duplicate logic.
- ✨ **`Update()`** runs on **all** platforms: calls `PollChatToggleShortcuts()` (Legacy `Input.*`) when the UITK root has **no** focused element (`Root.focusController.focusedElement`) — works with character controls when UI is unfocused. On WebGL, `WebGLInput.captureAllKeyboardInput` is still cleared. Subclasses overriding **`Update()`** should call `base.Update()` first.
- 🐛 **UITK API compatibility:** use **`Root.focusController.focusedElement`** instead of `IPanel.focusedElement` (missing on some Unity versions).
- ✨ **App hook:** `protected virtual void OnCollapsedStateChanged(bool collapsed)` — invoked after each `SetCollapsed` (gameplay/cursor wiring stays in your subclass or DI).
- 📝 **`CoreAiChat.uxml`** — tooltips: “Open chat (C)”, “Collapse chat (Esc)”, “Clear history”; clear-history button uses **`*`** so it is not confused with chat hotkey **C**.
- 🧪 **Tests:** `CoreAiChatPanelEditModeTests` — `IsOpenChatHotkeyFromKeys` (C / modifiers / other keys).
- ⚙️ **`CoreAiChatConfig`** — **“Hotkeys”** options: enable/disable keyboard open while collapsed, **`OpenChatHotkey`** (`KeyCode`, default `C`), enable/disable **Esc** (stop generation / collapse). FAB tooltip and FAB letter refresh from config.
- ⚙️ **`CoreAiChatPanel` runtime API** — `SetRuntimeOpenChatKeyboardShortcutEnabled` / `SetRuntimeOpenChatHotkey` / `SetRuntimeEscapeChatShortcutsEnabled` (`null` = use config again), `ClearRuntimeHotkeyOverrides()`, **`Effective*`** properties for resolved behavior.

## [0.25.2] - 2026-04-26

### 💬 Chat UI — header emoji glyphs replaced with ASCII (empty WebGL buttons fix)

- 🐛 **`Runtime/Source/Features/Chat/UI/CoreAiChat.uxml`** — `coreai-chat-stop` now renders as `■` (Geometric Shapes U+25A0, present in LiberationSans / default TMP fallback) instead of `⏹` (Misc Technical U+23F9, missing from default WebGL fonts and drew as an empty rectangle).
- **Context:** shipped WebGL players do not load emoji fallbacks (Noto Color Emoji, etc.), so emoji-plane symbols (U+1F300–U+1FAFF, parts of U+2600–U+27BF, U+23F0–U+23FF) render as `□` or disappear inside round buttons. ASCII / Latin-1 / Geometric Shapes (U+2580–U+25FF) are present in the default font asset — hence the move to `■`.
- **Compatibility:** cosmetic only, no API changes. Projects overriding button text in custom UXML are unaffected. Tooltip unchanged (“Stop agent and generation”), so UX stays clear.
- **Known TODO (out of scope for 0.25.2):** on WebGL, `UnityWebRequest` does not deliver SSE incrementally, so `OpenAiChatLlmClient.CompleteStreamingAsync` can deliver one terminal chunk instead of a stream — streaming UI appears stuck (“no typed reply + endless typing animation”). Details and fix plan: [`Docs/STREAMING_WEBGL_TODO.md`](Docs/STREAMING_WEBGL_TODO.md). App workaround: force `CoreAiChatConfig.EnableStreaming = false` under `#if UNITY_WEBGL && !UNITY_EDITOR` (example: `RedoSchool/Features/ChatUI/Presentation/Controllers/ChatPanelController.cs`).

## [0.25.1] - 2026-04-26

### 💬 Chat UI — WebGL TextField focus persistence (“focus lasts one frame” fix)

- 🐛 **`CoreAiChatPanel.Update()` (WebGL only)** keeps `WebGLInput.captureAllKeyboardInput = false` every frame.
  - **Symptom:** in a WebGL build, clicking the chat `TextField` focuses for exactly one frame, then focus drops and typing fails. Not reproduced in the Editor.
  - **Cause:** the WebGL player periodically flips `captureAllKeyboardInput` back to `true` (JS keyboard handler re-attach on canvas focus return, scene switches, DOM input churn under UITK `TextField` focus). The previous one-shot `ConfigureWebGlKeyboardInput()` in `Awake()` ran only once at panel startup.
  - **Fix:** `CoreAiChatPanel` adds `protected virtual void Update()` under `#if UNITY_WEBGL && !UNITY_EDITOR` that compares `WebGLInput.captureAllKeyboardInput` to `false` and resets when it diverges. Cheap (one bool compare; write only on change); stripped in Editor / Standalone.
  - **Not restored:** the `FocusOutEvent` loop from 0.21.4 (it fought the caret — see 0.21.6). The Update watchdog targets the capture flag, not UITK focus itself.
  - **Override-friendly:** `protected virtual`; subclasses can extend (extra WebGL watchdogs) calling `base.Update()` first.

### 🎮 Input compatibility — Legacy Input Manager + new Input System Package

- 🐛 **`OrchestrationDashboard` crashed** with `Active Input Handling = Input System Package (New)`. Direct `Input.GetKeyDown()` throws `InvalidOperationException` when the legacy Input Manager is disabled, breaking the metrics panel every frame.
- ✅ **New helper `IsToggleKeyPressedThisFrame()`** wraps both stacks via `#if ENABLE_LEGACY_INPUT_MANAGER` / `#if ENABLE_INPUT_SYSTEM && COREAI_HAS_INPUT_SYSTEM`. With `Both`, both paths run (legacy first as fast path).
- ✅ **`CoreAI.Source.asmdef`** declares a soft dependency on `Unity.InputSystem` in `references` + `versionDefines` (`com.unity.inputsystem >= 1.0.0` → `COREAI_HAS_INPUT_SYSTEM`). If Input System is not installed, `using UnityEngine.InputSystem;` and all new-input code strip out cleanly.
- ✅ **`KeyCode → Key` mapping** via `ToInputSystemKey()` covers F1–F12, BackQuote, Tab, Escape, Enter, Space (dashboard use; unsupported keys return `Key.None` — extend on demand).
- ⚠️ **Compatibility:** legacy-only projects behave as in 0.25.0. New Input System projects should install `com.unity.inputsystem` (versionDefines enable the branch automatically).

## [0.25.0] - 2026-04-26

### Forced Tool Mode (provider tool_choice) — deterministic tool calls

- **`MeaiLlmClient.ApplyForcedToolMode`** maps the new `LlmToolChoiceMode` (introduced in `com.nexoider.coreai 0.25.0`) onto Microsoft.Extensions.AI `ChatOptions.ToolMode`:
  - `Auto` → provider default (model decides),
  - `RequireAny` → `ChatToolMode.RequireAny`,
  - `RequireSpecific` → `ChatToolMode.RequireSpecific(name)` (validated against the available `AIFunction` set; falls back to `RequireAny` with a warning if the named tool isn't present),
  - `None` → `ChatToolMode.None`.
- **Streaming + forced tools fixed for multi-round loops.** `MeaiLlmClient.CompleteStreamingAsync` applies the forced mode only on the **first** iteration; after each tool result is fed back to the model, options are cloned with `ChatToolMode.Auto` (`CloneOptionsWithAutoToolMode`), so the model can finalise with text instead of being pinned into an infinite tool-call loop.
- **Both code paths supported.** Forced tool mode flows through both `CompleteAsync` (non-streaming, via `SmartToolCallingChatClient`) and `CompleteStreamingAsync` (streaming, native + text-based tool extraction).
- **Tool-call JSON stays out of streaming text by default.** The existing native (SSE `delta.tool_calls`) and text-based extraction paths already strip tool-call JSON before yielding text chunks; `ForcedToolMode` does not change that.
- 🧪 **Tests:** new `ForcedToolModeEditModeTests` verify forced-mode mapping, RequireSpecific validation, and the per-iteration reset in streaming.
- **HTTP SSE `reasoning_content` (Qwen / LM Studio)** — `MeaiOpenAiChatClient.ExtractDeltaUpdate` applies deltas so reasoning chains in a separate field do not leak into visible `content`; `ParseResponse` is documented as “`message.content` only for assistant text”. EditMode: `MeaiOpenAiChatClientSseEditModeTests`; PlayMode: `Streaming_ThinkBlocks_StrippedFromResponse` timing aligned with `RequestTimeoutSeconds` plus margin.
- 🔧 Bumped package versions to `0.25.0`. Dependency: `com.nexoider.coreai 0.25.0+`.

## [0.24.2] - 2026-04-26

### HTTP error diagnostics & policy hardening

- **HTTP 400 response body logging** — `MeaiOpenAiChatClient` now includes the API's response body in error messages for both non-streaming and SSE paths. Previously, only the status code was logged (e.g., `HTTP/1.1 400 Bad Request`), making it impossible to diagnose *why* the API rejected a request. Now the full rejection reason (e.g., `model not found`, `invalid tool schema`) is visible in the log.
- **`ToolExecutionPolicy` safety normalization** — `maxConsecutiveErrors` is now clamped to `Math.Max(1, value)` in the constructor. Passing `0` or negative values previously made `IsMaxErrorsReached` immediately `true`, causing agents to abort before executing any tools.
- **Documentation refresh** — both root `README.md` and `Assets/CoreAiUnity/README.md` updated to v0.24.2 with vivid "imagine this" descriptions of the AI pipeline, accurate version badges, and production-ready framing.
- Bumped package versions to `0.24.2`.

## [0.24.1] - 2026-04-26

### SSE tool-call accumulation & UI stop fix

- **`SseToolCallAccumulator`** — new stateful accumulator in `MeaiOpenAiChatClient` that properly collects `delta.tool_calls` spread across multiple SSE chunks (cloud providers like OpenAI split `id`+`name` in chunk 1 and `arguments` fragments across chunks 2..N). Flushed at stream end into `FunctionCallContent`. Removes the "Partial SSE tool_calls" known limitation.
- **UI stop deduplication** — removed redundant `AddMessage("Stopped by user")` from `StopAgent()`. The `OperationCanceledException` handler in `SendToAI` already displays the stop message via `_stopRequestedByUser` flag, eliminating double feedback.
- **New PlayMode tests** — `StreamingToolCallingPlayModeTests`:
  - `Streaming_WithToolCapablePrompt_CompletesSuccessfully` — smoke test for full tool-capable pipeline.
  - `Streaming_EarlyCancellation_StopsCleanly` — validates clean cancellation mid-stream.
  - `Streaming_ThenNonStreaming_NoStateContamination` — verifies no state leaks between streaming and non-streaming modes.
- Updated `STREAMING_ARCHITECTURE.md` — removed "not yet implemented" known limitation for partial SSE accumulation, documented `SseToolCallAccumulator` lifecycle.
- Bumped package versions to `0.24.1`.

## [0.24.0] - 2026-04-26

### Streaming tool-calling hardening

- **`ToolExecutionPolicy`** — new shared class for tool execution guarantees (duplicate detection, consecutive error tracking, `CoreAi.NotifyToolExecuted`). Both streaming (`MeaiLlmClient`) and non-streaming (`SmartToolCallingChatClient`) paths now delegate to this single policy, eliminating behavior divergence.
- **Hardened `TryExtractToolCallsFromText`** — pattern-aware JSON parser replaces naive `firstBrace/lastBrace` approach:
  - Supports multiple tool calls in a single text response.
  - Ignores JSON inside fenced code blocks (` ```...``` `) to prevent false positives.
  - Only matches JSON objects containing both `"name"` and `"arguments"` keys.
  - Gracefully skips malformed/partial JSON.
- **Native SSE `delta.tool_calls`** — `MeaiOpenAiChatClient` now parses `choices[0].delta.tool_calls` from cloud providers (OpenAI, Anthropic via OpenRouter). Text-based extraction remains the primary fallback for local models (Ollama, llama.cpp, LM Studio).
- **Stop/Clear race fix** — unified `StopActiveGeneration()` with `_isStopping` reentrance guard; `StopAgent()` now delegates to the same internal path, eliminating potential double-stop from concurrent Escape + button click.
- **New tests:**
  - `ToolExecutionPolicyEditModeTests` — 14 tests: duplicate detection (global, per-tool, reset), error counter, batch execution, max errors, safety normalization.
  - `TryExtractToolCallsFromTextTests` — 11 tests: single/multi tool, code block protection, malformed JSON, nested braces, edge cases.
- Updated `STREAMING_ARCHITECTURE.md` — new §7 "Streaming tool-calling" documenting dual-path architecture and execution policy guarantees.
- Bumped package versions to `0.24.0` (`com.nexoider.coreaiunity` and dependency on `com.nexoider.coreai`).

## [0.23.3] - 2026-04-26

### Composition stability and streaming test coverage

- Fixed duplicate CoreAI bootstrap in `CoreAIGameEntryPoint`: repeated `Start()` calls are now idempotent and do not reinitialize `CoreAIAgent`.
- Added graceful duplicate-start warning in Composition logs to make accidental double-container startup visible.
- Added new EditMode tests for composition guard: `CoreAIGameEntryPointEditModeTests`.
- Expanded streaming + tool-calling tests in `MeaiLlmClientEditModeTests`:
  - keeps visible prefix text while suppressing tool JSON from UI;
  - terminates with explicit terminal error when tool-loop iteration limit is exceeded.
- Bumped package version to `0.23.3` and synced dependency to `com.nexoider.coreai` `0.23.3`.

## [0.23.2] - 2026-04-26

### Chat stop reliability

- Fixed non-stream HTTP cancellation in `MeaiOpenAiChatClient.GetResponseAsync`: when `Esc` or stop button cancels the active request, UnityWebRequest is now aborted immediately instead of waiting for full response timeout.

## [0.23.1] - 2026-04-26

### Packaging and release pin

- Bumped `com.nexoider.coreaiunity` to `0.23.1`.
- Pinned dependency `com.nexoider.coreai` to `0.23.1` to force package consumers to pick the build with streaming/tool-calling reliability fixes.

## [0.23.0] - 2026-04-26

### LLM Streaming + Tool Calling (single cycle)

- Added unified streaming tool-cycle in `MeaiLlmClient.CompleteStreamingAsync`: stream assistant output, detect tool-call JSON, execute tools, append tool result messages, continue generation in the same request flow.
- Tool-call JSON is now suppressed from chat UI during streaming; the user sees only human-readable assistant text.
- Added EditMode coverage in `MeaiLlmClientEditModeTests` for scenario: `streamed tool JSON -> tool execution -> continued streamed text`.
- Updated agent defaults for tool modes: `ToolsAndChat` and `ToolsOnly` now enable per-role streaming by default (can still be overridden with `AgentBuilder.WithStreaming(...)`).
- Strengthened HTTP streaming cancellation in `MeaiOpenAiChatClient`: active request is aborted both on token cancellation and on early enumerator disposal.
- Stabilized PlayMode tests: `Streaming_CancellationToken_StopsStream` uses fallback timed cancellation; `MemoryTool_AppendsMemory` now retries with strict tool-only prompt before failing.
- Added complex behavior scenario test in dedicated folder: `Tests/PlayModeTest/Scenarios/Complex/MerchantBehaviorChatWithToolsPlayModeTests.cs`.
- Updated package versions to `0.23.0` (`com.nexoider.coreaiunity` and dependency on `com.nexoider.coreai`).

## [0.22.0] - 2026-04-25

### ✨ Agent Control API — Full agent lifecycle control

A new level of control over agents: stop, clear memory, subscribe to tool invocations.

### 💬 Chat UI — stop generation from the UI

- 🛑 **Stop via the send button.** While the model is generating a reply, `coreai-chat-send` switches to stop mode (`■`) and calls `CoreAi.StopAgent(roleId)` plus cancellation of the active token.
- ⌨️ **Stop via `Esc`.** During active generation, pressing `Esc` in the chat stops the current request the same way as the button.
- 🎨 **Busy-state visual cue.** The send button gets a dedicated red style (`.coreai-chat-send-button-stop`) to make it clear it now acts as the stop control.
- 🧪 **EditMode tests.** Added `CoreAiChatPanelEditModeTests` (Escape detection and send/stop button state rendering).
- 📝 **Docs.** Updated `README_CHAT.md` and `DEVELOPER_GUIDE.md` (sections on stopping generation from the UI).

#### Stopping the agent (`CoreAi.StopAgent`)
- **`CoreAi.StopAgent(string cancellationScope)`** — atomically cancels all current and pending orchestrator tasks (`QueuedAiOrchestrator`).
- Cancels the `CancellationToken` for active generations and clears the internal queue for the given scope (usually `roleId`).
- Safe to call from any thread.

#### Clearing context (`CoreAi.ClearContext`)
- **`CoreAi.ClearContext(string roleId, bool clearChatHistory = true, bool clearLongTermMemory = true)`** — granular reset of agent memory.
- `clearChatHistory = true` — clears short-term chat history (session context).
- `clearLongTermMemory = true` — clears long-term memory (agent state/facts via `MemoryTool`).
- You can combine flags: chat only, long-term memory only, or both.

#### Subscribing to tools (`CoreAi.OnToolExecuted`)
- **`CoreAi.OnToolExecuted`** — global event fired when the model invokes a tool through the MEAI pipeline.
- Delegate: `ToolExecutedHandler(string roleId, string toolName, IDictionary<string, object?> arguments, object? result)`.
- Ideal for: playing sounds, triggering VFX, analytics, logging.
- Wrapped in `try/catch` — subscriber errors do not tear down the LLM pipeline.

#### Chat clear button in the UI
- 🆕 Added a **🗑** button to the `CoreAiChatPanel` header (on the right, before the collapse control).
- By default it clears UI messages and short-term chat history (`ClearChat()` → `CoreAi.ClearContext(roleId, true, false)`).
- For a full reset (chat + long-term memory), use `ClearChat(clearChatHistory: true, clearLongTermMemory: true)`.
- **`ClearChat(bool clearChatHistory, bool clearLongTermMemory)`** — new overload with granular control.

#### `SmartToolCallingChatClient` constructor
- Added a required `roleId` parameter — passed to `CoreAi.NotifyToolExecuted` on every tool invocation.
- ⚠️ **Breaking:** all direct `SmartToolCallingChatClient` constructions now require `roleId` before `maxConsecutiveErrors`.

### 📝 Documentation
- Updated `DEVELOPER_GUIDE.md`: new "Control API" section with code examples for `StopAgent`, `ClearContext`, and `OnToolExecuted`.

### 🧪 Tests
- New EditMode tests: `ClearContext_ClearsOnlyChatHistory`, `ClearContext_ClearsOnlyLongTermMemory`, `OnToolExecuted_FiresOnToolCall`.
- All `SmartToolCallingChatClient*` tests updated — `roleId` parameter added.
- Test `CancelTasks_SpecificScope_CancelsActiveAndPendingTasks` for the orchestrator.

## [0.21.9] - 2026-04-25

### ✨ Agent Control API (task cancellation and context clearing)

- Public methods were added to the core (via the `CoreAi` facade) to stop agent work and reset its memory.
- **`CoreAi.StopAgent(string cancellationScope)`**: Cancels the `CancellationToken` for all current generations and clears the `QueuedAiOrchestrator` internal queue for tasks with the given scope.
- **`CoreAi.ClearContext(string roleId)`**: Programmatically clears chat history (`IAgentMemoryStore.ClearChatHistory`) and internal memory (`MemoryTool`) for the specified agent role.
- Documentation updated (`DEVELOPER_GUIDE.md`).

## [0.21.8] - 2026-04-25

### 🔧 LLMUnity — automatic package detection (`COREAI_HAS_LLMUNITY`)

- 🐛 **Fixed compile error `CS0246: MeaiLlmUnityClient` in projects without LLMUnity.** Code depending on `undream.llmunity` was previously gated behind `#if !COREAI_NO_LLM`, which was never defined automatically. As a result, if LLMUnity was not installed (and `COREAI_NO_LLM` was not set manually), `LLMUnity` type references still compiled and then failed.
- ✨ **`versionDefines` in asmdefs.** All four assembly definition files (`CoreAI.Source`, `CoreAI.Editor`, `CoreAI.Tests`, `PlayModeTest`) now include a `versionDefines` block that defines `COREAI_HAS_LLMUNITY` automatically when the `ai.undream.llm` package is present in the project.
- ♻️ **Preprocessor guard refactor.** All LLMUnity-dependent `#if` conditions were changed from `!COREAI_NO_LLM && !UNITY_WEBGL` to `COREAI_HAS_LLMUNITY && !UNITY_WEBGL`:
  - `MeaiLlmUnityClient.cs`, `LlmUnityModelBootstrap.cs`, `LlmUnityMeaiChatClient.cs`, `LlmUnityAutoDisableIfNoModel.cs` — entire file wrapped.
  - `MeaiLlmClient.cs` — `using LLMUnity` and `CreateLlmUnity()` method.
  - `ILlmAgentProvider.cs` — `SceneLlmAgentProvider`.
  - `LlmPipelineInstaller.cs` — fallback when LLMUnity is absent → `StubLlmClient`.
  - `RoutingLlmClient.cs` — inner client type resolution.
  - `LlmClientRegistry.cs` — `LlmBackendKind.LlmUnity` case.
  - `CoreAISettingsAssetEditor.cs` — Editor-only `LLMManager` / `LLMAgent` checks.
  - `CoreAIBuildMenu.cs` — `TryCreateLlmUnityObjects`.
  - **PlayMode tests:** `AllToolCallsPlayModeTests`, `PlayModeLlmUnityTestHarness`, `PlayModeProductionLikeLlmFactory.LlmUnityWarmup`, `MeaiLlmClientPlayModeTests`, `SharedLlmUnity`, `PlayModeProductionLikeLlmTestSupport`, `LlmUnityGlobalSetup`, `TestAgentSetup`.
  - **EditMode tests:** `MeaiToolCallsEditModeTests` — `#if` guard for `LlmUnityMeaiChatClient.TryParseToolCallFromText`.
- 📝 **`COREAI_NO_LLM` remains** as a manual opt-out to disable all LLM functionality (HTTP + LLMUnity). `COREAI_HAS_LLMUNITY` is the new automatic environment detection.

### Dependencies

- Bumped dependency on `com.nexoider.coreai` to **0.21.8**

## [0.21.7] - 2026-04-23

### 💬 Chat UI — collapse to a floating action button (FAB)

- ✨ **Chat collapse.** The `CoreAiChatPanel` header now includes a `coreai-chat-collapse` (`—`) button. When collapsed, the container is hidden via the `.coreai-collapsed` class and a round floating `coreai-chat-fab` appears in the bottom-right corner — clicking it expands the chat again and returns focus to the `InputField`.
- 📱 **Auto-collapse on small screens.** On startup, when the screen is `≤ 720×560`, the chat starts collapsed by default so it does not cover the game world; the user opens it by tapping the FAB. User choice (collapsed vs expanded) is persisted in `PlayerPrefs` and overrides the default on later launches.
- 🧩 **API.** Public method `SetCollapsed(bool collapsed, bool persist = true)` plus `IsCollapsed` — for programmatic control from game code or gameplay (for example, collapse the chat during a cutscene).
- 🎨 **USS.** New classes: `.coreai-chat-header-btn` (round header button), `.coreai-chat-container.coreai-collapsed` (hidden state), `.coreai-chat-fab` / `.coreai-chat-fab-icon` (floating button + icon).
- 📦 **UXML.** Added `coreai-chat-collapse` (in the header) and `coreai-chat-fab` (on the root panel, default `display: none`).
- ✅ Linter clean; existing WebGL input-focus fixes from `0.21.6` preserved.

## [0.21.6] - 2026-04-23

### 💬 Chat UI — removed forced focus (WebGL caret flicker fix)

- 🐛 **Removed `PointerDown`/`PointerUp` force-focus on `InputField`.** Forcing focus to the inner `unity-text-input` on every click/tap in WebGL fought UI Toolkit’s own focus management. Focus bounced between the outer `TextField` and its inner editor every frame, which caused:
  - border flicker (focused/unfocused every frame),
  - missing caret (`|` invisible because re-focus reset cursor position),
  - broken combos like `Ctrl+A` (selection cleared),
  - dropped characters when typing fast.
- 🐛 **Removed the `FocusOutEvent` loop.** Auto-restore on any `FocusOut` created a loop: “focus left → force back → left again”. UITK now handles focus on clicks.
- ✅ **Kept only what WebGL needs:**
  - `WebGLInput.captureAllKeyboardInput = false` — Unity does not steal keyboard from the browser;
  - `SendButton.focusable = false` — send does not steal focus after click;
  - one-shot `InputField.Focus()` in `TrySendInput` / `SendToAI.finally` so you can type the next message immediately.
- ♻️ **`FocusInputField()` simplified** to plain `InputField.Focus()` — manual inner `unity-text-input` lookup was unnecessary and harmful.

## [0.21.4] - 2026-04-23

### 💬 Chat UI — WebGL input focus hardening

- 🐛 **`WebGLInput.captureAllKeyboardInput = false`** on chat `Awake`. In WebGL builds Unity defaults to capturing all keyboard events from the browser, so UITK `TextField` inside the runtime panel lost focus and “ate” characters.
- 🐛 **PointerDown/PointerUp on `InputField`:** force focus to the inner `unity-text-input` on any click/tap. Focus no longer sticks to the outer `TextField` composite.
- 🐛 **`FocusOutEvent` auto-restore:** if focus drops for a reason other than send (common for multiline `TextField` on WebGL), it returns on the next tick.
- 🐛 **No focus stealers:** message-history `ScrollView`, header title, and header icon are `focusable = false` so clicks do not pull focus off the `TextField`.

## [0.21.3] - 2026-04-23

### 💬 Chat UI — WebGL typing stability

- 🐛 **Fixed focus loss after send-button submit.** `CoreAiChatPanel.BindUI()` sets `SendButton.focusable = false` so keyboard focus does not stick on send and the next keystrokes go to the `TextField`.
- 🐛 **Stabilized typing the next message.** Explicit `FocusInputField()` plus non-focusable send removes the WebGL case where some characters failed after the first send.

## [0.21.2] - 2026-04-23

### 💬 Chat UI — input focus

- 🐛 **Focus returns to the text field after send.** `CoreAiChatPanel.TrySendInput` / `SendToAI.finally` now focus the inner `unity-text-input` (not the outer `TextField` shell). Previously, after the first send in multi-line mode focus stayed on the outer shell and keystrokes did not reach the editor until you clicked again.
- ✨ **`CoreAiChatPanel.FocusInputField()`** — private helper wrapping `TextField.textInputUssName` lookup. Used when clearing the field after send and in `SendToAI` `finally` so you can keep typing after the assistant finishes.

## [0.21.1] - 2026-04-23

### 💬 Chat UI polish & layout stability

- 💅 **ScrollView layout:** overlap/shrink fix — `ScrollView` shrinks correctly in the column (`min-height: 0`); header/input/typing no longer squash when content is huge (`flex-shrink: 0`).
- 💅 **Scrollbar theming (UI Toolkit / Unity 6):** explicit `Scroller` styles (`.unity-scroll-view__vertical-scroller`, `.unity-scroller__tracker`, `.unity-scroller__dragger`); arrow buttons hidden (`.unity-scroller__low-button` / `.unity-scroller__high-button`) so the default bright bar no longer bleeds through.
- 💅 **InputField readability:** stronger selectors for inner `TextField` classes across Unity versions; caret/selection colors tuned so player input stays readable on dark theme.
- 🔧 **Scroll bottom padding:** last bubble no longer hides under typing/input.

### ⏱️ Timeouts

- ⏱️ **`CoreAISettingsAsset.LlmRequestTimeoutSeconds`:** default raised from **15s → 120s** (streaming/tool-calling on local/slow models often needs more time).

### 🧪 Tests (PlayMode)

- 🧪 **`CraftingMemoryViaLlmUnityPlayModeTests`:** test no longer fails when the backend ran tool calls but never returned a final `AiEnvelope` — item name is recovered from memory (prompt contract).
- 🧪 **`CraftingMemoryViaOpenAiPlayModeTests`:** determinism check is now asserted (craft #4 must match craft #2), not only logged. Prompt memory uses canonical `Craft #N - Name made from X + Y` so the model can repeat by ingredients.
- 🧪 **`CraftingMemoryItemNameExtractor`:** more tolerant of free-form model text (quotes, “crafted with quality”, bold markdown, etc.).

## [0.21.0] - 2026-04-23

### 🎯 `CoreAi` singleton — unified one-line entry point

Previously, calling the LLM from game code meant knowing VContainer (`container.Resolve<CoreAiChatService>()`), rolling your own singleton, or calling `CoreAiChatService.TryCreateFromScene()` every time. Now there is one static class that covers the common paths.

- ✨ **`CoreAI.CoreAi`** (static facade, `Assets/CoreAiUnity/Runtime/Source/Api/CoreAi.cs`) — lazy thread-safe singleton auto-resolved from the first `CoreAILifetimeScope` in the scene.
  - `CoreAi.TryGetChatService(out CoreAiChatService?)` / `CoreAi.TryGetOrchestrator(out IAiOrchestrationService?)` — **non-throwing**, handy for UI buttons and optional AI (unlike `Get*` which throws `InvalidOperationException`).
  - `CoreAi.AskAsync(message, roleId, ct)` → `Task<string>` — simple chat.
  - `CoreAi.StreamAsync(message, roleId, ct)` → `IAsyncEnumerable<string>` — streamed text chunks.
  - `CoreAi.StreamChunksAsync(message, roleId, ct)` → `IAsyncEnumerable<LlmStreamChunk>` — stream with metadata (`IsDone`, `Error`, usage).
  - `CoreAi.SmartAskAsync(message, roleId, onChunk, uiStreamingOverride, ct)` — chooses stream vs sync from the flag hierarchy, returns full text, invokes `onChunk` per fragment.
  - `CoreAi.OrchestrateAsync(AiTaskRequest, ct)` — full orchestrator pipeline (snapshot → prompt composer → authority → queue → structured policy → publish `ApplyAiGameCommand` → metrics).
  - `CoreAi.OrchestrateStreamAsync(AiTaskRequest, ct)` — streaming variant of the same pipeline.
  - `CoreAi.OrchestrateStreamCollectAsync(task, onChunk, ct)` — stream + accumulate full text + `onChunk`, returns `string`.
  - `CoreAi.IsReady` / `CoreAi.Invalidate()` / `CoreAi.GetChatService()` / `CoreAi.GetOrchestrator()` / `CoreAi.GetSettings()` — cache control and direct service access.
- ✨ **`QueuedAiOrchestrator.RunStreamingAsync`** — streaming through the orchestrator queue honoring `MaxConcurrent` and `CancellationScope`. Portable producer/consumer queue on `SemaphoreSlim + ConcurrentQueue` (no `System.Threading.Channels`, which is not available in this Unity build).

### 🧪 Tests

- ✅ **`AiOrchestratorStreamingEditModeTests`** (new file, 5 tests):
  - `DefaultFallback_EmitsSingleTextChunkThenDone` — default interface implementation yields exactly 2 chunks (text + terminal).
  - `DefaultFallback_EmptyResult_EmitsErrorChunk` — empty result → terminal with `Error="empty result"`.
  - `QueuedAiOrchestrator_Streaming_DelegatesRealChunks` — 4 deltas → 5 chunks (4 + terminal), not 1 (fallback).
  - `QueuedAiOrchestrator_Streaming_RespectsMaxConcurrent` — two parallel streams with `MaxConcurrent=1` both complete.
  - `QueuedAiOrchestrator_Streaming_ExternalCancellation_EmitsCancelledTerminal` — cancel mid-stream → terminal with `Error="cancelled"`.
- ✅ **`CoreAiFacadeEditModeTests`** (new file, 7 tests):
  - `IsReady_WithoutLifetimeScope_ReturnsFalse`.
  - `Invalidate_DoesNotThrow_WhenCalledMultipleTimes`.
  - `GetSettings_WithoutLifetimeScope_ReturnsNull`.
  - `GetChatService_WithoutLifetimeScope_ThrowsInvalidOperation` — clear message.
  - `GetOrchestrator_WithoutLifetimeScope_ThrowsInvalidOperation`.
  - `TryGetChatService_WithoutLifetimeScope_ReturnsFalse` / `TryGetOrchestrator_WithoutLifetimeScope_ReturnsFalse`.

### 📚 Docs

- ✨ **`Assets/CoreAiUnity/Docs/COREAI_SINGLETON_API.md`** — full facade reference: **beginner block** (3 steps + FAQ), **method cheat sheet**, **pro stack** (when to keep static vs DI), `TryGet*`, threading, extensions.
- 🔧 **`STREAMING_ARCHITECTURE.md`** — new §`6. Orchestrator streaming` comparing `CoreAiChatService.SendMessageStreamingAsync` vs `IAiOrchestrationService.RunStreamingAsync` (authority, structured validation, queue, publish command, metrics).
- 🔧 **`QUICK_START.md`** — “One-line alternative — `CoreAi` singleton” section.
- 🔧 **`DOCS_INDEX.md`** — link to `COREAI_SINGLETON_API` under Chat & Streaming.
- 🔧 **`README_CHAT.md`** — programmatic usage documented both via `CoreAi` (recommended) and direct `CoreAiChatService`.

## [0.20.3] - 2026-04-23

### 🐛 Chat panel & streaming hotfix

- 🐛 **Streaming was invisible in the UI (regression).** In the chain `LoggingLlmClientDecorator` → `RoutingLlmClient` → `OpenAiChatLlmClient` / `MeaiLlmUnityClient`, no link overrode `CompleteStreamingAsync`. The `ILlmClient` default fallback always ran: it called `CompleteAsync` and emitted **one** terminal chunk after generation finished — users saw “Typing…” then the full answer with no streaming effect.
  - **`OpenAiChatLlmClient.CompleteStreamingAsync`** → delegates to `MeaiLlmClient.CompleteStreamingAsync` (SSE via `UnityWebRequest`, `ThinkBlockStreamFilter`).
  - **`MeaiLlmUnityClient.CompleteStreamingAsync`** → delegates to `MeaiLlmClient.CompleteStreamingAsync` (LLMUnity callback → `ConcurrentQueue`).
  - **`RoutingLlmClient.CompleteStreamingAsync`** → picks inner client by `AgentRoleId` and forwards chunks with `await foreach`.
  - **`LoggingLlmClientDecorator.CompleteStreamingAsync`** → forwards chunks without buffering while appending to `StringBuilder` for the final log (`LLM ◀ (stream) … chunks=N | tokens … | content …`). `LlmRequestTimeoutSeconds` applies to the whole stream and becomes a terminal chunk `Error = "LLM stream timeout (Ns)"`.
- 🐛 **Shift+Enter in multi-line `TextField` did not send.** `KeyDownEvent` used the Bubble phase (default), so UITK’s multiline `TextField` consumed Enter as newline before our handler. Callback is now `TrickleDown.TrickleDown`, and key handling includes `KeyCode.KeypadEnter` and `character == '\n' | '\r'` for IME/keyboard mappings.

### 💅 Typing indicator

- 💅 **Typing animation is plain dots `...`** instead of a long “typing…” prefix. `CoreAiChatConfig.TypingIndicatorText` defaults empty; dots animate `. → .. → ... → .` every 400 ms with padded width so the bubble does not jump. Classic prefix text can be set in the Inspector if desired.

### 🧪 Tests

- ✅ **`LoggingLlmClientDecoratorEditModeTests`** extended:
  - `Streaming_DelegatesRealChunks_NotSingleShotFallback` — four real delta chunks from a mock yield five user-visible chunks (4 + terminal), not one fallback shot.
  - `Streaming_LogsStartAndFinish` — asserts log contains `LLM ▶ (stream)`, `LLM ◀ (stream)`, `chunks=2`, and `traceId`.
- ✅ **`RoutingLlmClientEditModeTests`** (new file, 3 tests):
  - `Streaming_RoutesToInnerClient_ForRole` — router picks the right client and hits the streaming path (not `CompleteAsync`).
  - `Streaming_UsesFallbackClient_ForUnknownRole` — unknown role → legacy fallback.
  - `Streaming_NullRequest_YieldsErrorChunk` — null request yields one terminal error chunk without breaking `IAsyncEnumerable`.

## [0.20.2] - 2026-04-23

### 🗨️ Chat & Streaming

- ✨ **Streaming works for both backends** — HTTP API (SSE) and LLMUnity (`LLMAgent.Chat(callback)` + `ConcurrentQueue` with delta diff under lock). Duplicate regex `<think>` filtering removed from `LlmUnityMeaiChatClient`; single `ThinkBlockStreamFilter` at `MeaiLlmClient`.
- ✨ **`CoreAISettingsAsset.enableStreaming`** — global Inspector toggle (“General settings”). Turn off to force non-streaming (debugging or backends without streaming).
- ✨ **`CoreAiChatService.IsStreamingEnabled(roleId, uiFallback)`** — effective flag from hierarchy: UI (`CoreAiChatConfig.EnableStreaming`) → per-agent (`AgentBuilder.WithStreaming`) → global (`CoreAISettings.EnableStreaming`).
- ✨ **`CoreAiChatPanel`** honors all three layers; disabling any layer forces non-streaming for the panel.

### 🎬 Demo Scene

- ✨ **`CoreAI → Setup → Create Chat Demo Scene`** — new menu item; creates `Assets/CoreAiUnity/Scenes/CoreAiChatDemo.unity` with:
  - `Main Camera`, `Directional Light`, `EventSystem`;
  - `CoreAILifetimeScope` wired to `CoreAISettings`, `AgentPromptsManifest`, `LlmRoutingManifest`, `PrefabRegistry`, `GameLogSettings`;
  - `UIDocument` with `CoreAiChat.uxml` + `CoreAiChat.uss` and `PanelSettings` (1920×1080, ScaleWithScreenSize);
  - `CoreAiChatPanel` + demo asset `CoreAiChatConfig_Demo.asset`.
- ✨ **`CoreAI → Setup → Open Chat Demo Scene`** — opens the created scene in one click.

### 🧪 Tests

- ✅ **`ThinkBlockStreamFilterEditModeTests`** — full coverage of `CoreAI.Ai.ThinkBlockStreamFilter`: split tags (one char at a time), multiple blocks, `Flush()` / `Reset()`, case insensitivity, pseudo-tags (`<b>`, `2 < 3`), unclosed `<think>`, long reasoning (50+ chunks).
- ✅ **`CoreAiChatServiceEditModeTests`** — `IsStreamingEnabled` hierarchy (UI → per-agent → global) for both overloads (`uiFallback` / `uiOverride`), `SendMessageAsync` / `SendMessageStreamingAsync` / `SendMessageSmartAsync` with fake `ILlmClient`.
- ✅ **`CoreAiChatConfigEditModeTests`** — ScriptableObject defaults (including `EnableStreaming == true`).
- ✅ `CoreAISettingsAssetEditModeTests` — default `EnableStreaming` assertion added.
- ✅ **`SecureLuaSandboxEditModeTests`** — `SecureLuaEnvironment.StripRiskyGlobals` per removed global (`io`, `os`, `debug`, `load`, `loadfile`, `dofile`, `require`), plus `LuaExecutionGuard` (timeout / max steps / fast code / non-function argument) and `LuaCoroutineHandle` (Resume/Kill/budgetPerResume).
- ✅ **`LuaToolEditModeTests`** — `LuaTool.ExecuteAsync` (success, empty code, null code, executor throws, cancellation), `CreateAIFunction`, constructor null-arg validation, `LuaLlmTool` metadata (`Name`, `AllowDuplicates`, `Description`, `ParametersSchema`).
- ✅ `SmartToolCallingChatClientEditModeTests` — duplicate detection (`allowDuplicateToolCalls=false`), per-tool `AllowDuplicates=true`, `tool not found`, `tool throws exception`.
- ✅ `InGameLlmChatServiceEditModeTests` — rate limiter: window overflow, `maxRequestsPerWindow=0` (disabled), rejected request not stored, sliding window.

### Dependencies

- Bumped dependency on `com.nexoider.coreai` to **0.20.2**

## [0.20.1] - 2026-04-23

### 🐛 Streaming Fixes

- 🐛 **Fixed `Create can only be called from the main thread`** in `StreamingPlayModeTests`. `Streaming_ReturnsChunks_WithDoneFlag`, `Streaming_CancellationToken_StopsStream`, `Streaming_ThinkBlocks_StrippedFromResponse`, and `ThreeLayerPrompt_AllLayersApplied` used to wrap `await foreach` in `Task.Run`, so `UnityWebRequest` / `DownloadHandlerBuffer` were created on the thread pool and failed. Streaming and `CompleteAsync()` now run as async methods on the Unity main thread via `UnitySynchronizationContext`.
- 🐛 **Stream cancellation:** `MeaiOpenAiChatClient.GetStreamingResponseAsync()` now calls `webReq.Abort()` when `CancellationToken.IsCancellationRequested`, not only throwing `OperationCanceledException` (important for OpenRouter/remote HTTP where sessions may keep billing).
- 🔧 **`MeaiLlmClient.CompleteStreamingAsync()`** rewritten around stateful `ThinkBlockStreamFilter` (old per-chunk regex missed split `<think>` / `</think>` across SSE chunks). Guarantees a final `IsDone=true` chunk.
- 🔧 **`CoreAiChatPanel`** — local state machine replaced with shared `ThinkBlockStreamFilter` (DRY between UI and LLM layers).

### Tests

- ✅ All four `StreamingPlayModeTests` pass (previously all four failed).
- ✅ 27 EditMode tests (`ThinkBlockFilterEditModeTests` + `StreamingAndPromptsEditModeTests`) pass.

### Dependencies

- Bumped dependency on `com.nexoider.coreai` to **0.20.1**

## [0.20.0] - 2026-04-23

### 🗨️ Universal Chat Module (NEW)
- ✨ **`CoreAiChatConfig`** — ScriptableObject chat settings in Inspector (`Assets → Create → CoreAI → Chat Config`): roleId, title, welcome, icons, streaming on/off, sizes, input limits.
- ✨ **`CoreAiChatPanel`** — MonoBehaviour + UI Toolkit controller: works out of the box, streaming + non-streaming, think-block filtering, virtual hooks (`OnMessageSending`, `OnResponseReceived`, `FormatResponseText`, `CreateMessageBubble`).
- ✨ **`CoreAiChatService`** — chat without UI: streaming, history, 3-layer prompts. `TryCreateFromScene()` for DI resolve.
- ✨ **UXML/USS template** — `CoreAiChat.uxml` + `CoreAiChat.uss` dark theme, `coreai-` class prefix.
- ✨ **Think-block filtering** — streaming state machine hides `<think>...</think>`; typing indicator while the model “thinks”.
- 📚 **`README_CHAT.md`** — quick start, extension, programmatic API, custom styles.

### Streaming API
- ✨ **Real SSE streaming in `MeaiOpenAiChatClient`** — `stream: true`, parsing `data:` lines and `delta.content` chunks.
- ✨ **`MeaiLlmClient.CompleteStreamingAsync()`** — streams via `IChatClient.GetStreamingResponseAsync()` with automatic `<think>` filtering.
- 🔧 **DRY `MeaiOpenAiChatClient`** — `BuildMessagesPayload()` and `BuildToolsPayload()` shared by `GetResponseAsync` and `GetStreamingResponseAsync`.

### 3-Layer Prompt Architecture
- 🔧 **`AiPromptComposer`** — constructor extended with `AgentMemoryPolicy` and `ICoreAISettings` for 3-layer prompt build.

### Tests
- 🧪 **EditMode**: `StreamingAndPromptsEditModeTests` (13 tests: 3-layer composition, AgentMemoryPolicy, LlmStreamChunk, default streaming fallback).
- 🧪 **EditMode**: `ThinkBlockFilterEditModeTests` (10 tests: regex + state machine for `<think>` blocks).
- 🧪 **PlayMode**: `StreamingPlayModeTests` (4 tests: streaming chunks, cancellation, think-block stripping, 3-layer prompt with a real LLM).

### Dependencies
- Bumped dependency on `com.nexoider.coreai` to **0.20.0**

## [0.19.1] - 2026-04-14

### Fixes & Stability

- 🐛 **Duplicate tool-call protection:** clarified how `MeaiLlmClient` resets failed-call counters within a session. Per-request `executedSignatures` fully isolates each call.
- 🔧 **Test harness `Agent.cs`:**
  - Test phrases exposed in the Inspector `[TextArea]` for live scenario tweaks and to avoid the LLM looping on identical prompts.
  - Added `ClearMemory()` to deliberately clear history (reset bot context between button presses so the model does not anchor on prior mistakes).
- 📝 **Docs:** clarified `SceneLlmAgentProvider` with `DontDestroyOnLoad` — you need an `LLMAgent` component in the scene or a registered `LlmUnityAgentName`.

### Dependencies

- Bumped dependency on `com.nexoider.coreai` to **0.19.1**

## [0.19.0] - 2026-04-10

### Crafting & Validation

- ✨ **`CompatibilityChecker`** — ingredient compatibility checks (2/3/4+ item rules, groups, custom validators)
- ✨ **`CompatibilityLlmTool`** — `ILlmTool` wrapper for function calling
- ✨ **`JsonSchemaValidator`** — validates JSON from the LLM (types, ranges, enums)
- 🧪 **45+ EditMode tests** (`CompatibilityAndSchemaEditModeTests.cs`)
- 🧪 **3 PlayMode tests** (`CompatibilityToolPlayModeTests.cs`) with a real LLM

### Dependencies

- Bumped dependency on `com.nexoider.coreai` to **0.19.0**

## [0.18.0] - 2026-04-10

### Architecture — LifetimeScope Decomposition & DI Cleanup

- 🔧 **`CoreAILifetimeScope.Configure()`** — split from 200+ lines into modular installers:
  - `LlmPipelineInstaller` — LLM clients, routing, logging decorator, orchestrator metrics.
  - `WorldCommandsInstaller` — Lua bindings, prefab registry, world executor, game config store.
  - `Configure()` is now ~40 lines with clear sections.
- ✨ **`ILlmAgentProvider` / `SceneLlmAgentProvider`** — abstraction for resolving `LLMAgent` with lazy caching. Removed `FindFirstObjectByType<LLMAgent>` from the DI composition root.
- 🔧 **`CoreAISettings.Instance = settings`** — replaces the 17-line `SyncToStaticSettings()` block. The static `CoreAISettings` proxy now delegates to the DI instance automatically.
- ❌ **`SyncToStaticSettings()`** — removed (replaced by `CoreAISettings.Instance = settings`).
- 🧪 **Tests**:
  - `CoreAISettingsSyncEditModeTests` — rewritten for `Instance` delegation (4 tests instead of 1).
  - `LuaAiEnvelopeProcessorEditModeTests` — cleanup updated via `ResetOverrides()`.

### Dependencies

- Bumped dependency on `com.nexoider.coreai` to **0.18.0**

## [0.16.0] - 2026-04-09

### PlayMode Tools & Editor
- ✨ **`SceneLlmTool`** — runtime scene inspection tool. Lets the LLM search/analyze hierarchy and adjust `Transform` on `GameObject`s safely on the main thread via UniTask.
- ✨ **`CameraLlmTool`** — vision tool for PlayMode screenshots (`capture_camera`) returning a Base64 JPEG `dataUri`.
- 🛠 **`CoreAiPrefabRegistryAsset` automation** — `OnValidate` fills `Key` from AssetDatabase GUID and syncs `Name` when prefabs are assigned in the Inspector.

## [0.15.0] - 2026-04-09

### Tool Calling Engine
- ✨ **Robust JSON extraction** — rewrote tool-call parsing in `LlmUnityMeaiChatClient.TryParseToolCallFromText`. Fragile regex removed; flexible brace scanning (`IndexOf('{')`).
- ⚙️ **Reasoning-mode stripping** — preprocess responses before tool parsing: strips `<think>...</think>` chains so JSON parsing does not break on “thinking aloud” (DeepSeek/Qwen).

### Editor UX
- ✨ **Auto asset bootstrap** — `[InitializeOnLoadMethod]` in `CoreAIBuildMenu` ensures required `ScriptableObject` assets exist when the project loads.
- ✨ **Quick Settings menu** — **CoreAI → Settings** jumps to the global `CoreAISettings.asset`.

## [0.13.0] - 2026-04-09

### Action / Event System
- ✨ `DelegateLlmTool`, `CoreAiEvents`, and `AgentBuilder` extensions (via `com.nexoider.coreai 0.13.0`).
- 📝 Updated `TOOL_CALL_SPEC.md` and `AGENT_BUILDER.md` with examples and trigger prompting.
- 🧪 **EditMode tests** for `CoreAiEvents` and `AgentBuilder.WithAction`.
- 🧪 **PlayMode test** `CustomAgentsPlayModeTests.CustomAgent_Helper_WithAction` for `DelegateLlmTool`.

## [0.12.0] - 2026-04-08

### Unified Logger (`ILog`)

- 🔧 **UnityLog** — `ILog` implementation from CoreAI.Core; maps `LogTag` → `GameLogFeature`
- 🔧 **CoreServicesInstaller** — registers `ILog` (`UnityLog`) as DI singleton and sets `Log.Instance`
- 🔧 **GameLoggerUnscopedFallback** — automatic `Log.Instance` fallback before DI init
- 🔧 **CoreAIGameEntryPoint** — migrated from `IGameLogger` to `ILog`
- 🔧 **WorldTool** — logging migrated to `ILog` with `LogTag.World`
- ❌ Removed manual `Log.Instance` wiring from `CoreAILifetimeScope`
- 🔧 **`MemoryToolAction` unification** (Core 0.12.0) — single enum; `AgentBuilder.WithMemory()` applies correctly via `policy.ConfigureRole()`.
- ℹ️ `IGameLogger` kept as an internal Unity-layer interface (`FilteringGameLogger`, `GameLogSettingsAsset` unchanged)

### Dependencies

- Bumped dependency on `com.nexoider.coreai` to **0.12.0**

---

## [0.11.0] - 2026-04-07

### Universal System Prompt Prefix

- ✨ **`CoreAISettingsAsset.universalSystemPromptPrefix`** — Inspector field (“General settings”)
- ✨ **`CoreAISettings.UniversalSystemPromptPrefix`** — static property for programmatic override
- ✨ **`SyncToStaticSettings()`** — synced on startup from `CoreAILifetimeScope`
- ✨ Prefix applies automatically to all agents (built-in and custom)

### Temperature (shared across backends)

- ✨ **`CoreAISettingsAsset.temperature`** — default changed from `0.2` to `0.1`
- ✨ **`CoreAISettings.Temperature`** — static property (default `0.1`)
- ✨ Temperature applies to LLMUnity and HTTP API
- ✨ **`AgentBuilder.WithTemperature(float)`** — per-agent override
- ✨ **`AgentConfig.Temperature`** — config property
- ✨ Inspector field “Temperature” under “General settings”

### MaxToolCallIterations (no longer hard-coded)

- ✨ **`CoreAISettingsAsset.maxToolCallIterations`** — Inspector field (default 2)
- ✨ **`CoreAISettings.MaxToolCallIterations`** — static property
- ✨ **`MeaiLlmClient`** reads from settings instead of hard-coded `MaximumIterationsPerRequest = 2`

## [0.7.0] - 2026-04-06

### Unified MEAI tool-calling format (MAJOR)

**All tool calls now use one MEAI function-calling shape**

#### Added
- ✨ **`LuaTool`**: MEAI `AIFunction` for Programmer Lua execution
- ✨ **`LuaLlmTool`**: `ILlmTool` wrapper for Lua
- ✨ **`InventoryTool`**: MEAI `AIFunction` for Merchant inventory reads
- ✨ **`InventoryLlmTool`**: `ILlmTool` wrapper for inventory
- ✨ **Merchant agent**: NPC merchant with tools (`get_inventory` + memory)
- ✨ **`AgentBuilder`**: fluent builder for custom agents/tools
- ✨ **`AgentMode`**: `ToolsOnly`, `ToolsAndChat`, `ChatOnly`
- ✨ **`WithChatHistory()`**: session dialog context in RAM
- ✨ **`WithMemory()`**: persistent memory across sessions (JSON file)
- ✨ **Tool-call retry**: up to 3 automatic attempts on failed tool calls with error feedback (`CoreAISettings.MaxToolCallRetries`)

#### Changed
- 🔧 **`LlmUnityMeaiChatClient.TryParseToolCallFromText`**: normalized to `{"name": "...", "arguments": {...}}`
- 🔧 **All tools via MEAI**: Memory + Lua flow through `FunctionInvokingChatClient`
- 🔧 **`ProgrammerResponsePolicy` simplified**: no fenced-block checks
- 🔧 **`AgentMemoryPolicy.SetToolsForRole()`**: attach custom tools to a role
- 🔧 **Prompts updated**: Programmer + Merchant use the unified format

#### Removed
- ❌ **`AgentMemoryDirectiveParser`**: removed — MEAI pipeline only
- ❌ **Fallback parsing in `AiOrchestrator`**: memory via `FunctionInvokingChatClient`
- ❌ **Fenced blocks** (```memory, ```lua): not used for tool calls

#### Breaking changes
- **Programmer** now calls the `execute_lua` tool instead of fenced ```lua blocks
- **Memory tool** shape: `{"tool": "memory", ...}` → `{"name": "memory", "arguments": {...}}`
- **`MaxLuaRepairRetries`** (formerly `MaxLuaRepairGenerations`) changed from 4 → 3

#### Tests
- ✨ **`AgentBuilderEditModeTests`** — 8 builder tests
- ✨ **`CustomAgentsPlayModeTests`** — 3 custom-agent tests (Merchant, Analyzer, Storyteller)
- 🔧 **`MeaiToolCallsEditModeTests`** — MemoryTool, LuaTool, JSON parsing
- 🔧 **`LuaExecutionPipelineEditModeTests`** — expected retries updated (4→3)
- 🔧 **`RoleStructuredResponsePolicyEditModeTests`** — Programmer allows any text
- 🔧 All PlayMode tests updated for unified tool format v0.7.0

#### Documentation
- 📝 **`AGENT_BUILDER.md`** — full builder guide
- 📝 **`TOOL_CALL_SPEC.md`** — updated tool spec
- 📝 **`CHAT_TOOL_CALLING.md`** — Merchant NPC tool calling
- 📝 **`DEVELOPER_GUIDE.md`** — refreshed sections

### Dependencies

- Bumped dependency on `com.nexoider.coreai` to **0.7.0**

---

## [0.6.1] - 2026-04-06

### Tool-calling fallback for LLMs without structured `tool_calls`

- 🔧 **`LlmUnityMeaiChatClient.TryParseToolCallFromText`**: fallback parses JSON tool calls from plain model text
- 🔧 **Qwen3.5-2B support**: model returns tool JSON as text, not structured `tool_call` — now detected and converted to `FunctionCallContent` for MEAI
- 🔧 **Recognized shapes:**
  - `{"tool": "memory", "action": "write", "content": "..."}`
  - `{"name": "memory", "arguments": {...}}`
  - ```json\n{...}\n``` fenced blocks

### Fixes

- ✅ **Memory tool works**: `FunctionInvokingChatClient` recognizes the call and runs `MemoryTool.ExecuteAsync()`
- ✅ **Memory persists across calls**: Craft 2 sees Craft 1 memory

### Documentation

- Troubleshooting sections updated in `LLMUNITY_SETUP_AND_MODELS.md`

---

## [0.6.0] - 2026-04-05

### Microsoft.Extensions.AI full integration

- ✨ **`MeaiLlmUnityClient`**: full Microsoft.Extensions.AI integration for LLMUnity
- ✨ **`FunctionInvokingChatClient`**: MEAI automatic tool calling
- ✨ **`IChatClient` implementation**: internal wrapper over `LLMAgent`
- ✨ **`MemoryTool.CreateAIFunction()`**: builds MEAI `AIFunction`

### Removed

- ❌ **`LlmUnityLlmClient`**: replaced by `MeaiLlmUnityClient`
- ❌ **`MeaiChatClientAdapter`**: removed — integration is `MeaiLlmUnityClient`

### Documentation

- Updated: `MemorySystem.md`, `DEVELOPER_GUIDE.md`, `DGF_SPEC.md`, `LLMUNITY_SETUP_AND_MODELS.md`

### Dependencies

- Bumped dependency on `com.nexoider.coreai` to **0.6.0**

---

## [0.5.0] - 2026-04-05

### LLM response validation

- ✨ **Role-specific validation policies**: six classes validating each role’s output
- ✨ **`CompositeRoleStructuredResponsePolicy`**: routes validation by `roleId`
- ✨ **20 new EditMode tests**: broad policy coverage
- ✅ **Automatic retry**: failed validation triggers a follow-up request with hints

### GameConfig system

- ✨ **`UnityGameConfigStore`**: `IGameConfigStore` backed by ScriptableObjects
- ✨ **DI wiring**: registered in `CoreAILifetimeScope`
- ✨ **EditMode tests**: nine tests (policy, read, update, round-trip)
- ✨ **PlayMode tests**: three tests (AI read/modify/write, no access, multi-key)
- ✨ **`GAME_CONFIG_GUIDE.md`**: developer guide

### Analyzer tests

- ✨ **`AnalyzerEditModeTests`**: ten tests (prompts, telemetry, validation, orchestrator)

### Tests

- ✨ **`RoleStructuredResponsePolicyEditModeTests.cs`**: 20 policy tests
- ✨ **`GameConfigEditModeTests.cs`**: nine tests for `GameConfigTool` / `GameConfigPolicy`
- ✨ **`GameConfigPlayModeTests.cs`**: three tests with a real AI
- ✨ **`AnalyzerEditModeTests.cs`**: ten Analyzer-role tests

### Dependencies

- Bumped dependency on `com.nexoider.coreai` to **0.5.0**

---

## [0.4.0] - 2026-04-05

### Tool calling support

- ✨ **`LlmUnityLlmClient.SetTools()`**: LLMUnity tool calling
- ✨ **Tools in system prompt**: tools appended to the model system prompt
- ✨ **`OpenAiChatLlmClient` tools**: OpenAI-compatible `tools` array support

### Architecture

- Shared **`ILlmClient`** surface for:
  - **OpenAI HTTP** (CoreAI) — tools in JSON body
  - **LLMUnity** (CoreAI Unity) — tools in system prompt
- **`CoreAILifetimeScope`** registers tool-capable clients

### Tests

- ✨ Tests updated for tool calling
- PlayMode coverage for LLMUnity + memory tool

---

## [0.3.0] - 2026-04-04

### MEAI integration

- Updated for **Microsoft.Extensions.AI** function calling
- Agent system prompts use the MEAI format
- Tests updated for the MEAI pipeline

### Tests

- ✨ **`MemoryToolMeaiEditModeTests.cs`**: eight MEAI integration tests
- ✅ PlayMode tests updated for JSON/MEAI format
- ✅ Removed legacy `AgentToolCallParser` tests
- **+50 tests** overall for MEAI coverage

### Documentation

- **`AI_AGENT_ROLES.md`**: roles updated for MEAI
- New MEAI function-calling guides

## [0.2.0] - 2026-04-04

### Layout

- **CoreAI.Source** sources live under **`Assets/CoreAiUnity/Runtime/Source/`** (previously under `Packages/com.nexoider.coreai/Runtime/Source/`). UPM dependencies for this package: **MessagePipe**, **MessagePipe.VContainer**, **UniTask**, **LLMUnity** (plus **`com.nexoider.coreai`** transitively).

### Logging (release requirement)

- **Editor:** menu/setup messages go through **`CoreAIEditorLog`** (single `Debug.*` choke point for the Editor layer).
- **Tests:** version stores and LLM helpers use **`NullGameLogger`** or **`GameLoggerUnscopedFallback`** — no raw **`Debug.Log`** in core test logic.

### Other

- Version aligned with **`com.nexoider.coreai` 0.1.3** (`package.json` dependency).

## [0.1.2] - earlier

Baseline Unity host package. See git history.
