# 🌊 Streaming Architecture

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

Reasoning models (DeepSeek-R1, Qwen3 thinking, o1-class) emit chain-of-thought inside `<think>…</think>` tags. Those blocks must never reach the UI, but:

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

Или через статический синглтон `CoreAi` (см. [`COREAI_SINGLETON_API.md`](COREAI_SINGLETON_API.md)) — без ручного резолва сервисов:

```csharp
await foreach (string chunk in CoreAi.StreamAsync("Hello", "PlayerChat"))
    label.text += chunk;
```

---

## 6. Orchestrator streaming

Stream идёт не только через `CoreAiChatService`, но и через полный AI-пайплайн (`IAiOrchestrationService`). Разница:

| Слой | `CoreAiChatService.SendMessageStreamingAsync` | `IAiOrchestrationService.RunStreamingAsync` |
|------|-----------------------------------------------|---------------------------------------------|
| Prompt composer | ❌ (использует явные system+user) | ✅ 3-layer prompt composer |
| Authority check | ❌ | ✅ `IAuthorityHost.CanRunAiTasks` |
| Очередь + MaxConcurrent | ❌ | ✅ `QueuedAiOrchestrator` (порционно, по приоритету) |
| `CancellationScope` (отмена предыдущей задачи с тем же ключом) | ❌ | ✅ |
| Structured validation | ❌ | ✅ (после стрима) |
| Publish `ApplyAiGameCommand` | ❌ | ✅ (после полного ответа) |
| Метрики | ❌ | ✅ `IAiOrchestrationMetrics` |

Используйте `CoreAi.OrchestrateStreamAsync(task)` для агентских сценариев (Creator / Programmer / Mechanic) и `CoreAi.StreamAsync("text")` — для простого чата.

Внутри `AiOrchestrator.RunStreamingAsync`:

1. Собирает snapshot + prompt composer (common с `RunTaskAsync`, выделено в `BuildRequest`).
2. Создаёт `LlmCompletionRequest` с tools/history/temperature.
3. Await-foreach по `ILlmClient.CompleteStreamingAsync` (уже включает `ThinkBlockStreamFilter` в `MeaiLlmClient`).
4. Аккумулирует полный текст в `StringBuilder` (нужен для шага 5).
5. Когда стрим кончился — structured validation, публикация `ApplyAiGameCommand`, запись в chat history, метрики.

`QueuedAiOrchestrator.RunStreamingAsync` прокидывает через собственную producer/consumer-очередь (`AsyncChunkQueue` на `SemaphoreSlim + ConcurrentQueue` — без `System.Threading.Channels`, который недоступен в Unity-сборке), соблюдая `MaxConcurrent` и `CancellationScope`.

---

## 7. Known limitations

- **Streaming + tool calling** — `SmartToolCallingChatClient.GetStreamingResponseAsync` currently proxies to the inner client. Duplicate detection, retry policy and consecutive-error defence only apply to the non-streaming path. Full tool-calling over a streaming transport is tracked in `TODO.md` (v2.1 roadmap).
- **No output-length timeout** — there is a per-request cancellation token but no *total response length* guard. Add one externally if you need it.
- **Mobile** — `UnityWebRequest` SSE streaming has been tested on Desktop and Editor. On mobile, behaviour depends on the OS HTTP stack; measure before shipping.

Related deep dives: [LUA_SANDBOX_SECURITY (TODO)](LUA_SANDBOX_SECURITY.md) · [TOOL_CALLING_BEST_PRACTICES (TODO)](TOOL_CALLING_BEST_PRACTICES.md).
