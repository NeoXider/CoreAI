# Streaming Architecture

How CoreAI streams tokens from LLMs into your UI — end-to-end, with every layer you can override.

> **TL;DR.** Both HTTP SSE and local LLMUnity paths produce `IAsyncEnumerable<LlmStreamChunk>`. The chunks are scrubbed by a single stateful `ThinkBlockStreamFilter` (tag-safe across chunk boundaries) and delivered to `CoreAiChatPanel` on the Unity main thread. Whether streaming is used at all is decided by a **three-layer flag hierarchy** — UI → per-agent → global.

> **Streaming is the default execution path for everything, not just chat.** Since the streaming-by-default change, non-interactive agent task execution (`AiOrchestrator.RunTaskAsync`) also obtains each completion via the streaming path when `EnableStreaming` is on — through a `CompleteForTaskAsync` helper that drives `CompleteStreamingAsync` and collapses the chunks into an `LlmCompletionResult`. Tasks therefore run through the same execute-as-you-stream tool path as chat (`RunStreamingAsync`). It falls back to non-streaming `CompleteAsync` only when `EnableStreaming` is disabled. See [§6](#6-orchestrator-streaming).

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
- **Stream-open send retry** → `MeaiOpenAiChatClient` **retries** a transport-level send failure when opening the SSE stream (bounded — a few quick backoff retries). A **stale pooled keep-alive connection** the local server already closed (`System.Net.Http` surfaces this as "An error occurred while sending the request") no longer fails the whole request — a fresh attempt opens a new connection. A genuinely-down backend still surfaces promptly as **`BackendUnavailable`** rather than spinning the full retry budget.

### Local (LLMUnity GGUF)

`LlmUnityMeaiChatClient.GetStreamingResponseAsync` calls `LLMAgent.Chat(prompt, callback)`. The delta is pushed onto a `ConcurrentQueue<string>` from LLMUnity's worker and drained on the Unity main thread via `await foreach`.

- **Cancellation** → cooperative; the async loop checks the token every iteration.
- **Think blocks** — the `<think>` regex-per-chunk that used to live here was removed in 0.20.2; filtering happens centrally in `MeaiLlmClient`.

---

## 3. Think-block filter

Reasoning models (DeepSeek-R1, Qwen3 thinking, o1-class) emit chain-of-thought inside `<think>…</think>` tags. **OpenAI-compatible HTTP (LM Studio, vLLM, etc.)** may instead stream a separate `delta.reasoning_content` field. `MeaiOpenAiChatClient` maps that field to MEAI `TextReasoningContent` (never `ChatResponseUpdate.Text`), and `MeaiLlmClient` exposes it as `LlmStreamChunk.ReasoningText` (never `LlmStreamChunk.Text`). The tag-based filter handles only **in-content** tags and moves their spans to the same reasoning channel.

For hybrid-thinking local models, keep `CoreAISettingsAsset` **Reasoning Mode** at **Provider Default**
unless you need to override backend behavior. **Disabled** sends provider-specific request fields such as
`enable_thinking=false` and `chat_template_kwargs.enable_thinking=false`; **Enabled** sends the same
fields with `true`. Provider Default intentionally sends no reasoning controls so model/server defaults
remain unchanged. Use **Thinking Budget Tokens** only with servers that document support for that field.

If a stream produces no visible chunks and then ends with empty assistant content, inspect raw HTTP logs:
the model may have spent the output budget on `reasoning_content`. For Qwen-style thinking models this is
a backend/model behavior issue, not a UI streaming bug. Try Reasoning Mode = Disabled before changing
prompts or tests.

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

### Response and persistence boundary (7.0.7+)

- Build visible incremental and final text only from `LlmStreamChunk.Text`.
- **One stream carries several assistant messages.** After every tool round the model speaks again, and
  those replies arrive as one continuous chunk sequence. `LlmStreamChunk.StartsNewMessage` marks the
  first visible chunk of each reply after the first. Concatenating chunks blindly glues the end of one
  reply to the start of the next — in production a student read
  `…Проверь себя:**Ход завершён — ждём ответ ученика на карточке.**`, two messages fused into one.
  Use `StreamedMessageJoiner.Append(...)`: it is the single owner of the separation rule (blank line
  between replies, nothing before the first, no third blank line when the reply already ended with a
  paragraph). Never infer the boundary from punctuation — a reply may legitimately end with a colon
  and begin with a lowercase letter, so any heuristic is wrong in both directions.
- Treat `LlmStreamChunk.ReasoningText` as optional, ephemeral diagnostics. It may feed an explicitly
  diagnostic “thinking” view, but it must never be concatenated into the assistant answer.
- A reasoning-only stream is not an answer. It ends with no visible text and an `EmptyResponse` terminal
  chunk; CoreAI does not promote accumulated reasoning.
- Never append `ReasoningText` to MemoryTool, `IAgentMemoryStore` chat history, generated notes,
  `ApplyAiGameCommand`, assistant traces, analytics payloads, or other automatic records. Those sinks use
  visible `Text` only. Durable reasoning diagnostics, if a host truly needs them, require a separate
  opt-in store with explicit redaction and retention.

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
await foreach (LlmStreamChunk chunk in service.SendMessageStreamingAsync("Hello", "SmartChat", ct))
{
    // NOT `label.text += chunk.Text` — that fuses the reply after a tool round onto the previous one.
    label.text = StreamedMessageJoiner.Append(label.text, chunk.Text, chunk.StartsNewMessage);
    if (!string.IsNullOrEmpty(chunk.ReasoningText)) diagnostics.Append(chunk.ReasoningText);
    if (chunk.IsDone) break;
}
```

`diagnostics` above is a transient diagnostic buffer, not the chat bubble, MemoryTool, ChatHistory, or a
generated note. Most consumers should ignore `ReasoningText` entirely.

Or use the static `CoreAi` singleton (see [`COREAI_SINGLETON_API.md`](COREAI_SINGLETON_API.md)) — no manual service resolution:

```csharp
await foreach (string chunk in CoreAi.StreamAsync("Hello", "SmartChat"))
    label.text += chunk;
```

---

## 6. Orchestrator streaming

Streaming is not limited to `CoreAiChatService`; it flows through the full AI pipeline (`IAiOrchestrationService`) for **both** interactive chat (`RunStreamingAsync`) **and** non-interactive task execution (`RunTaskAsync`, via the `CompleteForTaskAsync` helper) — streaming is the default path whenever `EnableStreaming` is on. Differences between the two chat-facing entry points:

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
3. `await foreach` on `ILlmClient.CompleteStreamingAsync` (already includes `ThinkBlockStreamFilter` in `MeaiLlmClient`); forward `ReasoningText` for diagnostics without adding it to visible text.
4. Accumulate only `Text` in a `StringBuilder` (required for step 5).
5. When the stream ends — validate and publish only accumulated visible text, append only that text to chat history, and record metrics.

`QueuedAiOrchestrator.RunStreamingAsync` forwards through its own producer/consumer queue (`AsyncChunkQueue` on `SemaphoreSlim` + `ConcurrentQueue` — no `System.Threading.Channels`, which is unavailable in this Unity build), respecting `MaxConcurrent` and `CancellationScope`.

---

## 7. Streaming tool-calling (v0.24.0+)

Since v0.24.0, streaming tool-calling uses a **dual-path architecture**:

### Path 1: Text-based extraction (primary)

The primary mechanism, designed for local models (Ollama, llama.cpp, LM Studio) that output tool calls as text.
`MeaiLlmClient.TryExtractToolCallsFromText` delegates to the portable `LlmToolCallTextExtractor`, which recognises **four** text shapes, not only JSON. They differ in how much evidence of a *call* the shape itself carries, and the guards differ accordingly:

| Shape | Example | Evidence it is a call | Recognised |
|-------|---------|-----------------------|------------|
| JSON | `{"name":"memory","arguments":{…}}` (`arguments_json` string accepted too; multiple objects per reply) | the `name` + `arguments` structure | by shape; with a registry only for a declared name |
| XML (Hermes / Qwen-Agent) | `<function=memory><parameter=action>clear</parameter></function>` | the XML tags | by shape; with a registry only for a declared name |
| Function call | `read_skill("Alchemy")`, `world_command(action='spawn', x=1)` — the **whole** reply | **none**: `print("Привет, мир!")` is the same string | **only with a registry, and only for a declared name** |
| Memory pseudo-write | `Action=write content="…"` ending its line | the `Action=write` keyword | by shape; maps to `memory`, so with a registry only if `memory` is declared |

Guards common to every shape:

- fenced code blocks (` ```...``` `) are ignored — **including an unclosed trailing fence**: a reply cut off by the token limit inside a ```` ```json ```` example must not execute the example;
- JSON wrapped in backticks or matching quotes is a citation, not a command;
- placeholder names (`<tool_name>`) are rejected;
- partial/malformed JSON is skipped here; truncation repair lives in `TryBuildMalformedTextToolCall`, behind the same channel gate.

**Registry of declared tool names (7.35.0).** `LlmToolCallTextExtractor.TryExtract(text, knownToolNames, …)` and `StripForDisplay(text, knownToolNames)` take the names of the tools the request declared (`ILlmTool.Name`). Invariant: **a call is an address to a declared tool** — any other name stays visible text in every shape, because it cannot execute anyway and hiding it takes a line of the lesson away from the learner. Without a registry (the legacy overloads) JSON, XML and the pseudo-write are still recognised by shape, but **function-call syntax never fires**: with no registry `read_skill("x")` and `print("x")` are indistinguishable, and a Python tutor's one-line answer used to become a call to a non-existent tool `print` — the model got `Unknown tool`, the learner an empty bubble. There is deliberately no stop-list of Python names (`print`/`input`/`len`); it would end at the first new lesson. The current call sites (`MeaiLlmClient.TryPortableToolExtract`, `SmartToolCallingChatClient.TryExtractToolCallsFromText`) still use the legacy overload; to re-enable function-call syntax for local Qwen builds pass `request.Tools.Select(t => t.Name)` there.

### Path 2: Native SSE `delta.tool_calls` (enhancement)

For cloud providers (OpenAI, Anthropic via OpenRouter) that emit `delta.tool_calls` in SSE chunks.
`MeaiOpenAiChatClient.ExtractDeltaUpdate` parses `choices[0].delta.tool_calls` and emits `FunctionCallContent` in `ChatResponseUpdate`.

If the SSE stream contains `FunctionCallContent`, `MeaiLlmClient` uses native detection instead of text extraction.

### Prose is interpreted only on the fallback path (7.35.0+)

**One decision, taken by CHANNEL, governs everything the loop does with the model's prose:** holding it back, reading tool calls out of it, and repairing truncated JSON in it. Since 7.35.0 all three run only when **`Tools`** is non-empty **and** the endpoint has no native tool channel (**`SupportsNativeToolCalling == false`**) **or** the declared tools bound nothing (**`aiTools.Count == 0`**). A remote OpenAI-compatible endpoint is native, so its text streams live, delta by delta, and is never parsed.

**The channel is declared where the endpoint is created, never guessed.** `MeaiLlmClient.CreateHttp` and the `OpenAiChatLlmClient` constructors take `supportsNativeToolCalling` (default `true` — that is what an OpenAI-compatible server means), and every place that builds a **local llama.cpp / LLMUnity** endpoint passes **`false`** explicitly: `LlmEndpointClientFactory.ActivateLlmUnityAsync`, the LlmUnity profile in `LlmClientRegistry`, and `LlmPipelineInstaller`'s LLMUnity client. That server speaks the same HTTP dialect *without* a tool channel — its model calls a tool by writing JSON in the answer — so for it prose interpretation must stay ON. Wrong in that direction and every local-model tool stops working with no error anywhere; wrong in the other and a teacher's JSON example is executed as a command.

Two reasons, both from real use:

- **Correctness.** Reading prose for calls means acting on what the model *said* instead of on the channel it said it through. A tutor explaining JSON — the everyday job of a programming teacher — writes an object shaped exactly like a call, and the engine executed the example instead of showing it. Shape can never separate an example from a command; the channel can. The malformed-JSON repair is the sharpest case: it *reconstructs* a truncated object out of prose and runs it.
- **Feel.** The hold begins at the first still-open `{`, which a Python teacher types constantly (a dict, a set, an f-string), so prose froze mid-sentence and then arrived in a lump.

Behind this gate: the hybrid hold and its span scanning (`GetHybridSafeSegments`, `GetFirstIncompleteBraceStart`, `FindToolCallJsonSpans`), `TryExtractToolCallsFromText` (Path 2), `TryBuildMalformedTextToolCall`, the `<think>`-block tool-call diagnostic, `SmartToolCallingChatClient`'s own text extraction on the non-streaming path (constructor flag `allowTextShapedToolCalls`, default **false**), and in `AiOrchestrator` both `LlmToolCallTextExtractor.StripForDisplay` and the tool-result repetition filter. Deliberately NOT behind it: execution of `FunctionCallContent` that arrived on the provider's own channel, and `LlmResponseSanitizer.StripLeadingSystemPromptEcho` (an identity match against the prompt we sent, not a guess about content).

**Escape hatch:** **`LlmCompletionRequest.AllowTextShapedToolCallsOnNativeEndpoint = true`** restores prose interpretation for an endpoint that advertises a native channel and then answers with JSON in the text (proxies in front of local models do this). The symptom that calls for it is a tool that never runs while the reply contains its call. Off by default; nothing in the runtime sets it.

There is no second escape hatch. The former **`BufferFullStreamingIterationWhenToolsDeclared`** — buffer the whole assistant iteration before any **`LlmStreamChunk.Text`** — was removed in **7.35.0**: nothing in the runtime ever set it, and full-turn buffering is precisely what the note below forbids. The hold now has exactly one cause: we hold prose because we are parsing it as tool calls (`hybridToolJsonHold == interpretProseAsToolCalls`). After Path 1 extraction or Path 2 **`delta.tool_calls`**, any assistant prose that was not yet forwarded is reconciled against the JSON-stripped held tail and emitted as trailing **`Text`** chunks so short prefixes (for example “Working…”) are not lost.

> **Never** reintroduce full-turn buffering of bound-tool turns. In 4.10.4 all bound-tool turns were buffered to hide the pre-tool preamble; that killed token-by-token streaming for the teacher chat and was reverted in 4.10.5. The hybrid hold below must keep visible prose streaming live on every tool turn.

### On-the-fly tool parsing — keep streaming live through tool calls (Kilo/Cline-style)

The hybrid hold hides **only** the tool-call JSON, never the surrounding prose/preamble. It is a streaming state machine driven by the accumulated visible text each delta:

1. **Prose (outside any tool JSON)** streams as normal token-by-token **`Text`** chunks — both before and **after** a tool call.
2. The moment a tool call begins — a native **`delta.tool_calls`** (Path 2) **or** a text-shaped opening **`{`** of a tool-call JSON object (Path 1) — only the **tool-call payload** is swapped for a “calling tool…” indicator (**`BufferedStreamingNoToolBinding`** + **`BufferedStreamingUseToolProgressHint`** marker; the host shows **`CoreAiChatConfig.StreamingToolProgressHint`**). The JSON characters themselves are never emitted as visible text.
3. A **completed** text-shaped tool-call JSON span is hidden (skipped); prose that follows the closing **`}`** **resumes streaming live** in the same turn.
4. An **incomplete** `{…` that may still grow into a tool call is held from its `{` until it closes or the turn ends.

Mechanism (`MeaiLlmClient`):

- **`GetHybridSafeSegments(text, out exclusiveSafeEnd)`** walks the accumulated visible text and returns ordered **`HybridProseSegment`** ranges: prose segments (`IsToolJson=false`, emitted live) and completed tool-JSON spans (`IsToolJson=true`, hidden). `exclusiveSafeEnd` is the start of the first still-incomplete object (the hold boundary). Built from **`GetFirstIncompleteBraceStart`** (hold boundary) + **`FindToolCallJsonSpans`** (completed spans).
- The streaming loop drains these segments each delta via the local iterator **`DrainHybridSafeSegments`**, advancing **`hybridRawExclusiveEndEmitted`** over both emitted prose and hidden JSON, and emitting the tool-progress marker the first time a hold begins.
- After the turn ends and tool calls are extracted, **`GetHybridUnemittedSuffix(visibleText, hybridRawExclusiveEndEmitted)`** returns the JSON-stripped remainder of the held tail (the only prose not yet streamed), so post-tool prose is emitted exactly once with no duplication.

Non-text-shaped (native) tool calls never appear in the visible text at all, so prose around them already streams live; the same progress marker is emitted when the first **`FunctionCallContent`** arrives.

### Shared execution policy

Execute-as-you-stream tool calls run in **parallel**, bounded by `ICoreAISettings.MaxParallelToolCalls` (default **4**). Independent calls in a turn are scheduled concurrently with a `SemaphoreSlim` of that size; state-mutating built-ins are **serialized**; results are collated back into **arrival order** at completion; `MaxParallelToolCalls <= 1` is the sequential fast-path (byte-identical to the pre-parallel loop). The streamed turn finalizes via `ToolExecutionPolicy.CompleteStreamedTurnAsync` with a drain bounded by the per-call tool timeout.

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

> **Rule:** The Unity UI and portable pipeline both enforce the configured request timeout. Every Unity-side deadline must be PlayerLoop-driven so it remains live in WebGL.

`CancellationTokenSource.CancelAfter()` and finite `Task.Delay` rely on managed timer support that is **non-functional in WebGL**, causing indefinite hangs.

The active arrangement is:
- `AiOrchestrator` **passes `cancellationToken` through** without adding a timer.
- `CoreAiChatService.SendMessageAsync` and `SendMessageStreamingAsync` create a linked `CancellationTokenSource` guarded by an **idle watchdog** (`CoreAiChatService.IdleTimeoutDeadline`): one `UniTask.Delay(DelayType.Realtime)` per idle window, driven by the PlayerLoop. Every streamed chunk and every tool-call start/finish/failure for the turn's role **re-arms** it with a single timestamp write (no allocation, safe from any thread), so a multi-step turn is cancelled only after a real stall of `LlmRequestTimeoutSeconds`, never because its steps add up. The vision path (`AskWithCameraAsync`) still uses a plain `CancelAfterSlim` because it is a single provider call.
- `TimeoutLlmClientDecorator` provides the portable pipeline bound; `LlmPipelineInstaller` injects `UnityMainThreadLlmAsyncMarshaler`, whose `DelayAsync` is also UniTask PlayerLoop-driven.
- `LoggingLlmClientDecorator` and `RetryingStreamingLlmClientDecorator` use that same injected delay for retry backoff.
- The timeout value comes from `ICoreAISettings.LlmRequestTimeoutSeconds` (default: 300s).

```csharp
// Inside CoreAiChatService.SendMessageStreamingAsync (simplified)
float timeoutSec = _settings?.LlmRequestTimeoutSeconds ?? 0f;
if (timeoutSec > 0)
{
    deadlineCts = new CancellationTokenSource();
    timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct, deadlineCts.Token);
    deadline = new IdleTimeoutDeadline(deadlineCts, timeoutSec); // one PlayerLoop watchdog per turn
    effectiveCt = timeoutCts.Token;
}
await foreach (LlmStreamChunk chunk in _orchestrator.RunStreamingAsync(request, effectiveCt))
{
    deadline?.Rearm(); // timestamp write, allocation-free
    yield return chunk;
}
```

### Retry centralization

> **Rule:** Network-level retries (HTTP 429, 5xx, exponential backoff) are handled **exclusively** by `LoggingLlmClientDecorator`. The orchestrator invokes the LLM client exactly once per request.

Before v1.5.1, `AiOrchestrator.RunTaskAsync` had its own `for (attempt...)` retry loop, creating an `M × N` retry multiplier (e.g., 2 orchestrator retries × 3 decorator retries = 6 actual network requests on a single failure).

### Error propagation

`CoreAiChatService` no longer swallows exceptions. Errors from `AiOrchestrator` → `LoggingLlmClientDecorator` → `ILlmClient` propagate to `CoreAiChatPanel`, which catches `Exception` and displays the error message to the user.

## 9. Known limitations

- **No output-length timeout** — there is a per-request cancellation token but no *total response length* guard. Add one externally if you need it.
- **Mobile** — HTTP streaming behaviour depends on the OS / Mono / IL2CPP stack; measure before shipping.
- **Partial SSE `tool_calls`** — Cloud providers may split tool call arguments across multiple SSE chunks. Split-argument accumulation across chunks **is implemented**: `MeaiOpenAiChatClient.SseToolCallAccumulator` buffers per-index argument fragments (each `delta.tool_calls[index]` keeps its own `StringBuilder`, with `Feed` appending fragments and `Flush` emitting a `FunctionCallContent` per index). Remaining caveats are handled defensively: parallel/duplicate index entries each accumulate independently, and malformed-JSON arguments are caught and surfaced as an empty argument dictionary rather than throwing.
- **WebGL — incremental SSE** — In the **WebGL player**, use **`CoreAISettingsAsset.WebGlNativeStreaming`** (**on** by default for new assets since **v1.6.13**) so **`FetchSseOpenAiTransport`** + **`CoreAiSseFetch.jslib`** deliver real **`fetch`** streaming. If **`false`**, **`UnityWebRequest`** may buffer the body (`LLM ◀ (stream) chunks=1` or non-streaming path). Legacy workaround (**`CoreAiChatConfig.EnableStreaming = false`**) is only for hosts that cannot support fetch/CORS — see **`STREAMING_WEBGL_TODO.md`**.

Related deep dives: [LUA_SANDBOX_SECURITY](../../CoreAI/Docs/LUA_SANDBOX_SECURITY.md) · [TOOL_CALLING_BEST_PRACTICES](../../CoreAI/Docs/TOOL_CALLING_BEST_PRACTICES.md).
