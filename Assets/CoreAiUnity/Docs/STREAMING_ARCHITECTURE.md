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
 │ • IOpenAiHttpTransport                      │ • LLMAgent.Chat       │
 │   – HttpClient (default)                   │ • ConcurrentQueue     │
 │   – UnityWebRequest (WebGL player)        │ • frame callbacks     │
 │ • SSE + simulated stream (see §2)          │                       │
 └────────┬─────────┘                         └───────────┬──────────┘
          │                                               │
          └──────────────► LLM backend ◄──────────────────┘
```

Key files:

| Layer | File |
|-------|------|
| Filter (portable) | `Assets/CoreAI/Runtime/Core/Features/Orchestration/ThinkBlockStreamFilter.cs` |
| Wrapper | `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/MeaiLlmClient.cs` |
| HTTP client + transport | `Assets/CoreAI/Runtime/Core/Features/Llm/MeaiOpenAiChatClient.cs` |
| HTTP transports | `HttpClientOpenAiTransport.cs`, `UnityWebRequestOpenAiTransport.cs` (Unity) |
| LLMUnity | `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/LlmUnityMeaiChatClient.cs` |
| Tool execution policy (portable) | `Assets/CoreAI/Runtime/Core/Features/Llm/ToolExecutionPolicy.cs` |
| Non-streaming tool loop (portable) | `Assets/CoreAI/Runtime/Core/Features/Llm/SmartToolCallingChatClient.cs` |
| UI | `Assets/CoreAiUnity/Runtime/Source/Features/Chat/CoreAiChatPanel.cs` |
| Service | `Assets/CoreAiUnity/Runtime/Source/Features/Chat/CoreAiChatService.cs` |

---

## 2. Transport details

### HTTP (OpenAI-compatible, LM Studio, vLLM, Ollama)

**Default (Editor, standalone, mobile):** `MeaiOpenAiChatClient` uses **`IOpenAiHttpTransport`** with **`HttpClientOpenAiTransport`**. **`GetStreamingResponseAsync`** sends `stream: true`, opens **`HttpCompletionOption.ResponseHeadersRead`**, and parses **SSE** lines. Prefixes **`data: `** and **`data:`** (no space) are accepted. When **`choices[0].delta.content`** is empty, the parser may use **`choices[0].message`** or **`choices[0].text`**. After response headers arrive, the client logs **HTTP status** and **Content-Type** before reading the body; a stream that ends with no parsed deltas emits a **Warn** diagnostic.

**WebGL player:** the browser forbids **`System.Net` / `HttpClient`**. By default **`UnityWebRequestOpenAiTransport`** implements **`IOpenAiHttpTransport`** with **`SupportsSseStreaming = false`** — the chat client uses **non-streaming** JSON completion and **simulates** **`ChatResponseUpdate`** yields (Unity **`UnityWebRequest`** does not deliver SSE incrementally). When **`CoreAISettingsAsset.WebGlNativeStreaming`** is **enabled**, **`FetchSseOpenAiTransport`** + **`CoreAiSseFetch.jslib`** use **`fetch`** with **`ReadableStream`** so **`SupportsSseStreaming = true`** and incremental SSE can reach **`MeaiOpenAiChatClient`** (still validate in a real browser build). Servers must expose **CORS** when cross-origin. See [`HTTP_TRANSPORT_SPEC.md`](HTTP_TRANSPORT_SPEC.md). Verbose **`console.log`** in **`CoreAiSseFetch.jslib`** (open / response / done) is **commented out by default** (**v1.6.19**); **`console.warn`** remains for read / network errors. Since **v2.6.0**, all JS-to-C# bridge callbacks are guarded so callback failures are bridge warnings, not browser main-loop exceptions; `data: [DONE]` is a terminal SSE sentinel; and abort is safe when a user stops generation near stream completion. Uncomment the logs in the jslib for DevTools tracing.

- **Timeouts** → long streams use a **per-read stall budget** (see **`RequestTimeoutSeconds`**) on the **`HttpClient`** path; **`HttpClient.Timeout`** on the streaming client is kept high.
- **Cancellation** → cooperative via **`CancellationToken`**.
- **Errors** → logged; failures surface as **`LlmClientException`** / terminal stream chunks where supported.

### Local (LLMUnity GGUF)

`LlmUnityMeaiChatClient.GetStreamingResponseAsync` calls `LLMAgent.Chat(prompt, callback)`. The delta is pushed onto a `ConcurrentQueue<string>` from LLMUnity's worker and drained on the Unity main thread via `await foreach`.

- **Cancellation** → cooperative; the async loop checks the token every iteration.
- **Think blocks** — the `<think>` regex-per-chunk that used to live here was removed in 0.20.2; filtering happens centrally in `MeaiLlmClient`.

---

## 3. Think-block filter

Reasoning models (DeepSeek-R1, Qwen3 thinking, o1-class) emit chain-of-thought inside `<think>…</think>` tags. **OpenAI-compatible HTTP (LM Studio, vLLM, etc.)** may instead stream a separate `delta.reasoning_content` field; `MeaiOpenAiChatClient` does **not** forward that to MEAI/Chat (it never becomes `update.Text` for the think filter). The tag-based filter only sees **in-content** tags.

Idle / stall budgets for HTTP SSE are enforced in **`MeaiOpenAiChatClient`** (read-loop timeouts aligned with **`RequestTimeoutSeconds`**), separate from **`HttpClient.Timeout`** on the streaming client (kept high so long generations are not cut off at the transport level).

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
new AgentBuilder("SmartChat")
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
bool useStream = service.IsStreamingEnabled("SmartChat", uiFallback: true);
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
await foreach (var chunk in service.SendMessageStreamingAsync("Hello", "SmartChat", ct))
{
    if (!string.IsNullOrEmpty(chunk.Text)) label.text += chunk.Text;
    if (chunk.IsDone) break;
}
```

Or use the static `CoreAi` singleton (see [`COREAI_SINGLETON_API.md`](COREAI_SINGLETON_API.md)) — no manual service resolution:

```csharp
await foreach (string chunk in CoreAi.StreamAsync("Hello", "SmartChat"))
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

### Hybrid hold when tools are declared (v1.7.3+)

When **`Tools`** is non-empty, **`MeaiLlmClient.CompleteStreamingAsync`** applies the **hybrid JSON hold** to **bound** and **unbound** tool registrations alike — unless **`LlmCompletionRequest.BufferFullStreamingIterationWhenToolsDeclared`** is **`true`**, which buffers the entire assistant iteration before any **`LlmStreamChunk.Text`** (escape hatch for exotic delta fragmentation). Live tokens stop before a suspected text-shaped tool JSON object; after Path 1 extraction or Path 2 **`delta.tool_calls`**, any assistant prefix that was not yet forwarded is reconciled against the cleaned, JSON-stripped text and emitted as trailing **`Text`** chunks so short prefixes (for example “Working…”) are not lost.

### Shared execution policy

Both streaming and non-streaming paths use `ToolExecutionPolicy` for:

| Guarantee | Description |
|-----------|-------------|
| Duplicate detection | Signature-based (name + arguments hash). Blocks repeated identical calls within one request cycle. Per-tool `AllowDuplicates` flag overrides. |
| Consecutive error tracking | Counter resets on success, increments on failure. Agent aborts at `MaxToolCallRetries` threshold. |
| Notification | Every tool execution fires `IToolCallEventPublisher.PublishStarted/Completed/Failed` (portable) → `MessagePipeToolCallEventPublisher` adapter → `GlobalMessagePipe`. Also calls `IToolExecutionNotifier.NotifyToolExecuted` → `CoreAiToolExecutionNotifier` adapter → `CoreAi.NotifyToolExecuted`. |

### Stop / clear guarantees

- **`StopActiveGeneration()`** has a `_isStopping` re-entrancy guard — concurrent Escape + button click cannot double-fire.
- **Send button stop mode (0.25.6+)** stays enabled while a request is running; the button is the stop control in that state, so click events must reach `StopActiveGeneration()`.
- **`StopAgent()`** delegates to `StopActiveGeneration()` and additionally resets the root CTS and cleans up UI.
- **Cancellation cleanup (0.25.6+)** cancels the active request CTS and resets streaming/sending UI state even when the static `CoreAi.StopAgent(roleId)` path is unavailable.
- **`ClearChat()`** calls `StopActiveGeneration()` before clearing history.

## 8. Timeout & retry architecture (v1.5.1)

### Timeout enforcement

> **Rule:** Timeout responsibility lives exclusively in the **Unity layer** (`CoreAiChatService`), not in the portable layer (`AiOrchestrator`, `LoggingLlmClientDecorator`).

Before v1.5.1, `AiOrchestrator` and `LoggingLlmClientDecorator` both used `CancellationTokenSource.CancelAfter()` to enforce request timeouts. This relies on `System.Threading.Timer`, which is **non-functional in WebGL** (Emscripten single-threaded model, no native timer callbacks), causing indefinite hangs.

In v1.5.1:
- `AiOrchestrator` and `LoggingLlmClientDecorator` **pass `cancellationToken` through** without wrapping it in timeout-linked sources.
- `CoreAiChatService.SendMessageAsync` and `SendMessageStreamingAsync` create a linked `CancellationTokenSource` with **`CancelAfterSlim(TimeSpan)`** from `Cysharp.Threading.Tasks` (UniTask), which uses Unity's `PlayerLoop` — fully WebGL-compatible.
- The timeout value comes from `ICoreAISettings.LlmRequestTimeoutSeconds` (default: 300s).

```csharp
// Inside CoreAiChatService.SendMessageAsync (simplified)
float timeoutSec = _settings?.LlmRequestTimeoutSeconds ?? 0f;
if (timeoutSec > 0)
{
    timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    timeoutCts.CancelAfterSlim(TimeSpan.FromSeconds(timeoutSec)); // UniTask PlayerLoop
    effectiveCt = timeoutCts.Token;
}
string result = await _orchestrator.RunTaskAsync(request, effectiveCt);
```

### Retry centralization

> **Rule:** Network-level retries (HTTP 429, 5xx, exponential backoff) are handled **exclusively** by `LoggingLlmClientDecorator`. The orchestrator invokes the LLM client exactly once per request.

Before v1.5.1, `AiOrchestrator.RunTaskAsync` had its own `for (attempt...)` retry loop, creating an `M × N` retry multiplier (e.g., 2 orchestrator retries × 3 decorator retries = 6 actual network requests on a single failure).

### Error propagation

`CoreAiChatService` no longer swallows exceptions. Errors from `AiOrchestrator` → `LoggingLlmClientDecorator` → `ILlmClient` propagate to `CoreAiChatPanel`, which catches `Exception` and displays the error message to the user.

## 9. Known limitations

- **No output-length timeout** — there is a per-request cancellation token but no *total response length* guard. Add one externally if you need it.
- **Mobile** — HTTP streaming behaviour depends on the OS / Mono / IL2CPP stack; measure before shipping.
- **Partial SSE `tool_calls`** — Cloud providers may split tool call arguments across multiple SSE chunks. The current implementation only handles complete `delta.tool_calls` with both `name` and fully-formed `arguments` in a single chunk. Progressive accumulation across chunks is not yet implemented.
- **WebGL — incremental SSE** — In the **WebGL player**, use **`CoreAISettingsAsset.WebGlNativeStreaming`** (**on** by default for new assets since **v1.6.13**) so **`FetchSseOpenAiTransport`** + **`CoreAiSseFetch.jslib`** deliver real **`fetch`** streaming. If **`false`**, **`UnityWebRequest`** may buffer the body (`LLM ◀ (stream) chunks=1` or non-streaming path). Legacy workaround (**`CoreAiChatConfig.EnableStreaming = false`**) is only for hosts that cannot support fetch/CORS — see **`STREAMING_WEBGL_TODO.md`**.

Related deep dives: [LUA_SANDBOX_SECURITY](../../CoreAI/Docs/LUA_SANDBOX_SECURITY.md) · [TOOL_CALLING_BEST_PRACTICES](../../CoreAI/Docs/TOOL_CALLING_BEST_PRACTICES.md).
