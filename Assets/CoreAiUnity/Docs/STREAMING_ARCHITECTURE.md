# Streaming Architecture

How CoreAI streams tokens from LLMs into your UI — end-to-end, with every layer you can override.

> **TL;DR.** Both HTTP SSE and local LLMUnity paths produce `IAsyncEnumerable<LlmStreamChunk>`. The chunks are scrubbed by a single stateful `ThinkBlockStreamFilter` (tag-safe across chunk boundaries) and delivered to `CoreAiChatPanel` on the Unity main thread. Whether streaming is used at all is decided by a **three-layer flag hierarchy** — UI → per-agent → global.

**From any script (beginners and pros):** use the static API `CoreAi.StreamAsync` / `CoreAi.SmartAskAsync` — they delegate to `CoreAiChatService` and the same chunk pipeline. Full guide: [`COREAI_SINGLETON_API.md`](COREAI_SINGLETON_API.md). Orchestrator streaming (`CoreAi.OrchestrateStreamAsync`) is documented in [§6](#6-orchestrator-streaming) below.

---

## 1. Pipeline

```
                   ┌──────────────────────────────────────────┐
                   │        Caller (CoreAiChatPanel,          │
                   │  CoreAiChatService, AgentBuilder.Ask...) │
                   └──────────────┬───────────────────────────┘
                                  │  IAsyncEnumerable<LlmStreamChunk>
                                  ▼
                   ┌──────────────────────────────────────────┐
                   │        MeaiLlmClient (wrapper)           │
                   │  • routing (LLMUnity / OpenAI HTTP)      │
                   │  • ThinkBlockStreamFilter (stateful)     │
                   │  • yields final IsDone=true chunk        │
                   └──────────────┬───────────────────────────┘
                                  │  MEAI ChatResponseUpdate
                                  ▼
          ┌───────────────────────┴───────────────────────┐
          │                                               │
 ┌────────▼─────────┐                         ┌───────────▼──────────┐
 │ MeaiOpenAiChat   │                         │ LlmUnityMeaiChatClient│
 │  Client (HTTP)   │                         │  (local GGUF)         │
 │ • UnityWebRequest│                         │ • LLMAgent.Chat       │
 │ • SSE "data:"    │                         │ • ConcurrentQueue     │
 │ • Abort() on CT  │                         │ • frame callbacks     │
 └────────┬─────────┘                         └───────────┬──────────┘
          │                                               │
          └──────────────► LLM backend ◄──────────────────┘
```

Key files:

| Layer | File |
|-------|------|
| Filter (portable) | `Assets/CoreAI/Runtime/Core/Features/Orchestration/ThinkBlockStreamFilter.cs` |
| Wrapper | `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/MeaiLlmClient.cs` |
| HTTP SSE | `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/MeaiOpenAiChatClient.cs` |
| LLMUnity | `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/LlmUnityMeaiChatClient.cs` |
| Tool execution policy | `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/ToolExecutionPolicy.cs` |
| Non-streaming tool loop | `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/SmartToolCallingChatClient.cs` |
| UI | `Assets/CoreAiUnity/Runtime/Source/Features/Chat/CoreAiChatPanel.cs` |
| Service | `Assets/CoreAiUnity/Runtime/Source/Features/Chat/CoreAiChatService.cs` |

---

## 2. Transport details

### HTTP (OpenAI-compatible, LM Studio, vLLM, Ollama)

`MeaiOpenAiChatClient.GetStreamingResponseAsync` uses `UnityWebRequest` with a streaming `DownloadHandlerBuffer`, sends `stream: true`, and parses `data: {...}` lines as Server-Sent Events.

- **Cancellation** → `webReq.Abort()` is called as soon as `CancellationToken.IsCancellationRequested` becomes true. No orphan connection stays open.
- **Errors** → logged, wrapped into a final `IsDone=true` chunk so UI can dismiss the typing indicator cleanly.
- **Main thread** → `UnityWebRequest` **must** be created on the Unity main thread. Wrapping the streaming call in `Task.Run` throws *"Create can only be called from the main thread"*.

### Local (LLMUnity GGUF)

`LlmUnityMeaiChatClient.GetStreamingResponseAsync` calls `LLMAgent.Chat(prompt, callback)`. The delta is pushed onto a `ConcurrentQueue<string>` from LLMUnity's worker and drained on the Unity main thread via `await foreach`.

- **Cancellation** → cooperative; the async loop checks the token every iteration.
- **Think blocks** — the `<think>` regex-per-chunk that used to live here was removed in 0.20.2; filtering happens centrally in `MeaiLlmClient`.

---

## 3. Think-block filter

Reasoning models (DeepSeek-R1, Qwen3 thinking, o1-class) emit chain-of-thought inside `<think>…</think>` tags. **OpenAI-compatible HTTP (LM Studio, vLLM, etc.)** may instead stream a separate `delta.reasoning_content` field; `MeaiOpenAiChatClient` does **not** forward that to MEAI/Chat (it never becomes `update.Text` for the think filter). The tag-based filter only sees **in-content** tags.

`UnityWebRequest.timeout` (mapped to `RequestTimeoutSeconds` on the HTTP settings asset) is a **whole-request** time budget from request start, not a per-chunk or idle timeout — long reasoning phases count against the same limit as the final answer.

Those blocks must never reach the UI, but:

- Opening and closing tags can arrive in **separate chunks** (e.g. `"<thi"` + `"nk>…"`).
- A stray `<` that is **not** part of a `<think>` tag must still be rendered.
- The stream can end in the middle of `<think>` — we must flush cleanly.

`CoreAI.Ai.ThinkBlockStreamFilter` solves all three. It's a pure C# state machine:

```csharp
var filter = new ThinkBlockStreamFilter();

await foreach (var chunk in client.GetStreamingResponseAsync(...))
{
    string visible = filter.ProcessChunk(chunk.Text);
    if (!string.IsNullOrEmpty(visible))
        ui.Append(visible);
}

string tail = filter.Flush(); // empty in normal termination
if (!string.IsNullOrEmpty(tail)) ui.Append(tail);
```

Covered by **24 EditMode tests** (`ThinkBlockStreamFilterEditModeTests`) including split-tag boundary cases.

---

## 4. Configuration — 3-layer hierarchy

Streaming is enabled when **every** layer agrees. First `false` wins.

| Priority | Layer | Where | Default |
|----------|-------|-------|---------|
| 1 (highest) | **UI toggle** | `CoreAiChatConfig.EnableStreaming` (Inspector) | `true` |
| 2 | **Per-agent override** | `AgentBuilder.WithStreaming(bool)` → `AgentMemoryPolicy.SetStreamingEnabled(role, bool)` | *(unset)* |
| 3 | **Global** | `CoreAISettings.EnableStreaming` (ScriptableObject / static `CoreAISettings.EnableStreaming`) | `true` |

### Examples

```csharp
// Always stream this NPC even if the project default is non-streaming
new AgentBuilder("PlayerChat")
    .WithSystemPrompt("You are a friendly guide.")
    .WithStreaming(true)
    .Build();

// Never stream — caller wants the full JSON in one shot
new AgentBuilder("JsonParser")
    .WithSystemPrompt("You output a strict JSON object.")
    .WithStreaming(false)
    .Build();

// Resolve effective value
var service = CoreAiChatService.TryCreateFromScene();
bool useStream = service.IsStreamingEnabled("PlayerChat", uiFallback: true);
```

Covered by `CoreAiChatServiceEditModeTests`.

---

## 5. UI integration

`CoreAiChatPanel.SendToAI` owns an instance of `ThinkBlockStreamFilter` per message. As chunks arrive:

1. The typing indicator stays visible while `filter.ProcessChunk(...)` returns empty (model is still inside `<think>`).
2. As soon as visible text appears, the current bubble is swapped from "typing" to "streaming" and incrementally grows.
3. On cancellation or error, the bubble is finalised with what we have, and the HTTP request is aborted if applicable.

Programmatic consumers can bypass the panel entirely:

```csharp
await foreach (var chunk in service.SendMessageStreamingAsync("Hello", "PlayerChat", ct))
{
    if (!string.IsNullOrEmpty(chunk.Text)) label.text += chunk.Text;
    if (chunk.IsDone) break;
}
```

Or use the static `CoreAi` singleton (see [`COREAI_SINGLETON_API.md`](COREAI_SINGLETON_API.md)) — no manual service resolution:

```csharp
await foreach (string chunk in CoreAi.StreamAsync("Hello", "PlayerChat"))
    label.text += chunk;
```

---

## 6. Orchestrator streaming

Streaming is not limited to `CoreAiChatService`; it also flows through the full AI pipeline (`IAiOrchestrationService`). Differences:

| Layer | `CoreAiChatService.SendMessageStreamingAsync` | `IAiOrchestrationService.RunStreamingAsync` |
|-------|-----------------------------------------------|---------------------------------------------|
| Prompt composer | No (explicit system + user) | Yes — 3-layer prompt composer |
| Authority check | No | Yes — `IAuthorityHost.CanRunAiTasks` |
| Queue + `MaxConcurrent` | No | Yes — `QueuedAiOrchestrator` (fair, by priority) |
| `CancellationScope` (cancel prior task with same key) | No | Yes |
| Structured validation | No | Yes (after stream completes) |
| Publish `ApplyAiGameCommand` | No | Yes (after full response) |
| Metrics | No | Yes — `IAiOrchestrationMetrics` |

Use `CoreAi.OrchestrateStreamAsync(task)` for agent workflows (Creator / Programmer / Mechanic) and `CoreAi.StreamAsync("text")` for simple chat.

Inside `AiOrchestrator.RunStreamingAsync`:

1. Build snapshot + prompt composer (shared with `RunTaskAsync`, factored into `BuildRequest`).
2. Create `LlmCompletionRequest` with tools, history, temperature.
3. `await foreach` on `ILlmClient.CompleteStreamingAsync` (already includes `ThinkBlockStreamFilter` in `MeaiLlmClient`).
4. Accumulate full text in a `StringBuilder` (required for step 5).
5. When the stream ends — structured validation, publish `ApplyAiGameCommand`, append chat history, record metrics.

`QueuedAiOrchestrator.RunStreamingAsync` forwards through its own producer/consumer queue (`AsyncChunkQueue` on `SemaphoreSlim` + `ConcurrentQueue` — no `System.Threading.Channels`, which is unavailable in this Unity build), respecting `MaxConcurrent` and `CancellationScope`.

---

## 7. Streaming tool-calling (v0.24.0+)

Since v0.24.0, streaming tool-calling uses a **dual-path architecture**:

### Path 1: Text-based JSON extraction (primary)

The primary mechanism, designed for local models (Ollama, llama.cpp, LM Studio) that output tool calls as text.
`MeaiLlmClient.TryExtractToolCallsFromText` scans the accumulated visible text for JSON objects containing both `"name"` and `"arguments"` keys.

- Multi-tool: extracts multiple tool calls from a single response
- False-positive protection: ignores JSON inside fenced code blocks (` ```...``` `)
- Pattern-aware: only matches JSON with required `name` + `arguments` structure
- Graceful: partial/malformed JSON is silently skipped

### Path 2: Native SSE `delta.tool_calls` (enhancement)

For cloud providers (OpenAI, Anthropic via OpenRouter) that emit `delta.tool_calls` in SSE chunks.
`MeaiOpenAiChatClient.ExtractDeltaUpdate` parses `choices[0].delta.tool_calls` and emits `FunctionCallContent` in `ChatResponseUpdate`.

If the SSE stream contains `FunctionCallContent`, `MeaiLlmClient` uses native detection instead of text extraction.

### Shared execution policy

Both streaming and non-streaming paths use `ToolExecutionPolicy` for:

| Guarantee | Description |
|-----------|-------------|
| Duplicate detection | Signature-based (name + arguments hash). Blocks repeated identical calls within one request cycle. Per-tool `AllowDuplicates` flag overrides. |
| Consecutive error tracking | Counter resets on success, increments on failure. Agent aborts at `MaxToolCallRetries` threshold. |
| Notification | Every tool execution fires `CoreAi.NotifyToolExecuted(roleId, toolName, args, result)`. |

### Stop / clear guarantees

- **`StopActiveGeneration()`** has a `_isStopping` re-entrancy guard — concurrent Escape + button click cannot double-fire.
- **Send button stop mode (0.25.6+)** stays enabled while a request is running; the button is the stop control in that state, so click events must reach `StopActiveGeneration()`.
- **`StopAgent()`** delegates to `StopActiveGeneration()` and additionally resets the root CTS and cleans up UI.
- **Cancellation cleanup (0.25.6+)** cancels the active request CTS and resets streaming/sending UI state even when the static `CoreAi.StopAgent(roleId)` path is unavailable.
- **`ClearChat()`** calls `StopActiveGeneration()` before clearing history.

## 8. Known limitations

- **No output-length timeout** — there is a per-request cancellation token but no *total response length* guard. Add one externally if you need it.
- **Mobile** — `UnityWebRequest` SSE streaming has been tested on Desktop and Editor. On mobile, behaviour depends on the OS HTTP stack; measure before shipping.
- **Partial SSE `tool_calls`** — Cloud providers may split tool call arguments across multiple SSE chunks. The current implementation only handles complete `delta.tool_calls` with both `name` and fully-formed `arguments` in a single chunk. Progressive accumulation across chunks is not yet implemented.
- **WebGL — `UnityWebRequest` does not deliver SSE incrementally** *(0.25.x, regression report)*. In a built WebGL player, the emscripten `XMLHttpRequest` wrapper typically delivers the response body only in `onload`, so SSE chunks from an OpenAI / HTTP provider accumulate in the browser and reach `MeaiOpenAiChatClient.ParseSseStream` as one buffer. Symptoms: log `LLM ◀ (stream) chunks=1` for responses tens–hundreds of characters long; in `CoreAiChatPanel` the typing indicator never clears and the bubble never appears. **Workaround:** `CoreAiChatConfig.EnableStreaming = false` under `#if UNITY_WEBGL && !UNITY_EDITOR` (example: `Assets/_source/Features/ChatUI/Presentation/Controllers/ChatPanelController.cs` in the RedoSchool project). **Full fix plan:** [`STREAMING_WEBGL_TODO.md`](STREAMING_WEBGL_TODO.md): in 0.26.0 — `protected virtual ShouldUseStreamingForRole()` hook defaulting to `false` on WebGL; in 0.27.0 — a real fetch-SSE bridge via `.jslib`.

Related deep dives: [LUA_SANDBOX_SECURITY (TODO)](LUA_SANDBOX_SECURITY.md) · [TOOL_CALLING_BEST_PRACTICES (TODO)](TOOL_CALLING_BEST_PRACTICES.md).
