# Streaming Architecture

How CoreAI streams tokens from LLMs into your UI — end-to-end, with every layer you can override.

> **TL;DR.** Both HTTP SSE and local LLMUnity paths produce `IAsyncEnumerable<LlmStreamChunk>`. The chunks are scrubbed by a single stateful `ThinkBlockStreamFilter` (tag-safe across chunk boundaries) and delivered to `CoreAiChatPanel` on the Unity main thread. Whether streaming is used at all is decided by a **three-layer flag hierarchy** — UI → per-agent → global.

> **Streaming is the default execution path for everything, not just chat.** Non-interactive agent task execution (`AiOrchestrator.RunTaskAsync`) also obtains each completion via the streaming path when `EnableStreaming` is on — through a `CompleteForTaskAsync` helper that drives `CompleteStreamingAsync` and collapses the chunks into an `LlmCompletionResult`. Tasks therefore run through the same execute-as-you-stream tool path as chat (`RunStreamingAsync`). It falls back to non-streaming `CompleteAsync` only when `EnableStreaming` is disabled. See [§6](#6-orchestrator-streaming).

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
                   │  • tool loop + ToolExecutionPolicy       │
                   │  • ThinkBlockStreamFilter (stateful)     │
                   │  • yields final IsDone=true chunk        │
                   └──────────────┬───────────────────────────┘
                                  │  MEAI ChatResponseUpdate
                                  ▼
                   ┌──────────────────────────────────────────┐
                   │   MeaiOpenAiChatClient (HTTP, SSE)       │
                   │  • IOpenAiHttpTransport                  │
                   │    – HttpClient (default)                │
                   │    – fetch SSE / UnityWebRequest (WebGL) │
                   │  • SSE + simulated stream (see §2)       │
                   └──────────────┬───────────────────────────┘
                                  │  POST /v1/chat/completions
                                  ▼
          ┌───────────────────────┴───────────────────────┐
          │                                               │
 ┌────────▼─────────────────┐              ┌──────────────▼─────────────┐
 │ Remote / local HTTP API  │              │ LLMUnity built-in server   │
 │ (OpenAI, LM Studio, ...) │              │ (local GGUF, port 13333)   │
 └──────────────────────────┘              └────────────────────────────┘
```

Key files:

| Layer | File |
|-------|------|
| Filter (portable) | `Assets/CoreAI/Runtime/Core/Features/Orchestration/ThinkBlockStreamFilter.cs` |
| Wrapper | `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/MeaiLlmClient.cs` |
| HTTP client + transport | `Assets/CoreAI/Runtime/Core/Features/Llm/MeaiOpenAiChatClient.cs` |
| HTTP transports | `HttpClientOpenAiTransport.cs` (Core); WebGL player: `FetchSseOpenAiTransport.cs` + `Runtime/Plugins/WebGL/CoreAiSseFetch.jslib`, `UnityWebRequestOpenAiTransport.cs`, `WebGlCompositeOpenAiTransport.cs` (Unity) — see [HTTP_TRANSPORT_SPEC](HTTP_TRANSPORT_SPEC.md) |
| LLMUnity endpoint adapter | `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/LlmUnityServerHttpSettings.cs` (wired in `LlmPipelineInstaller`, `LlmClientRegistry`, `LlmEndpointClientFactory`) |
| Tool execution policy (portable) | `Assets/CoreAI/Runtime/Core/Features/Llm/ToolExecutionPolicy.cs` |
| Non-streaming tool loop (portable) | `Assets/CoreAI/Runtime/Core/Features/Llm/SmartToolCallingChatClient.cs` |
| UI | `Assets/CoreAiUnity/Runtime/Source/Features/Chat/CoreAiChatPanel.cs` |
| Service | `Assets/CoreAiUnity/Runtime/Source/Features/Chat/CoreAiChatService.cs` |

---

## 2. Transport details

### HTTP (OpenAI-compatible, LM Studio, vLLM, Ollama)

**Default (Editor, standalone, mobile):** `MeaiOpenAiChatClient` uses **`IOpenAiHttpTransport`** with **`HttpClientOpenAiTransport`**. **`GetStreamingResponseAsync`** sends `stream: true`, opens **`HttpCompletionOption.ResponseHeadersRead`**, and parses **SSE** lines. Prefixes **`data: `** and **`data:`** (no space) are accepted. When **`choices[0].delta.content`** is empty, the parser may use **`choices[0].message`** or **`choices[0].text`**. After response headers arrive, the client logs **HTTP status** and **Content-Type** before reading the body.

How a stream ends is checked, not assumed:

- **Terminal frame required.** `data: [DONE]` or a chunk with a `finish_reason` ends the stream. A body that closes without either is a failure (`LlmClientException`, `BackendUnavailable`: "LLM SSE ended before a terminal completion frame.").
- **HTTP 200 with zero parsed deltas** (typically only keep-alive comments) is retried, up to three attempts in all, then the turn falls back to **one non-streaming completion**. The same fallback follows a 408 / 429 / 5xx on stream open once the rate-limit retry is spent.
- **No generation progress.** An attempt that shows no semantic progress (text, reasoning, tool-call fragments or a backend `generation_progress` frame) within 15 s of opening is aborted and handled like an empty stream; for `ServerManagedApi` that window is `RequestTimeoutSeconds`.
- **No usable output.** A terminated stream with no visible text and no tool call ends as `EmptyResponse`.

**WebGL player:** the browser forbids **`System.Net` / `HttpClient`**. `MeaiLlmClient.CreateHttp` picks the transport from **`CoreAISettingsAsset.WebGlNativeStreaming`** (default **on**):

- **On** — `WebGlCompositeOpenAiTransport`: streaming requests go through **`FetchSseOpenAiTransport`** + **`CoreAiSseFetch.jslib`** (browser **`fetch`** with a **`ReadableStream`**, incremental SSE through the same parser as above); non-streaming requests go through **`UnityWebRequestOpenAiTransport`**.
- **Off** — **`UnityWebRequestOpenAiTransport`** alone (**`SupportsSseStreaming = false`**): `MeaiOpenAiChatClient` makes a **non-streaming** JSON completion and **simulates** the **`ChatResponseUpdate`** stream, and `CoreAiChatService.IsStreamingEnabled` returns `false` in the player (one warning in the log). `CoreAI/Validate Production Settings` and a WebGL build warn when global streaming is on and this flag is off.

Servers must allow **CORS** when cross-origin. Bridge details (credentials mode, watchdog, guarded callbacks, abort, logging): [`HTTP_TRANSPORT_SPEC.md`](HTTP_TRANSPORT_SPEC.md).

- **Timeouts** → `RequestTimeoutSeconds` bounds the wait for response headers and, as a **per-read stall budget**, every gap between lines of the body (`LlmErrorCode.Timeout`: "LLM SSE stalled"). The shared streaming `HttpClient` has no client timeout (`Timeout.InfiniteTimeSpan`), so a long generation is not cut off at the transport level.
- **Cancellation** → cooperative via **`CancellationToken`**.
- **Errors** → logged; failures surface as **`LlmClientException`** / terminal stream chunks where supported.
- **Stream-open send retry** → a transport-level send failure when opening the stream (for example a stale pooled keep-alive connection) is retried a few times with a short backoff; see [HTTP_TRANSPORT_SPEC](HTTP_TRANSPORT_SPEC.md).

### Verifying WebGL streaming

Editor and Standalone runs never load `CoreAiSseFetch.jslib`. Before shipping a change to the transports or to chat streaming, run these PlayMode tests in a **WebGL player** (Test Runner, PlayMode, run on the WebGL player), not only in the Editor:

- **`CoreAiSseFetchWebGlBridgePlayModeTests`** (`Assets/CoreAiUnity/Tests/PlayMode/FastNoLlm`, compiled only for the WebGL player, no backend needed). `BridgeSelfTest_RoundTripsPayloadAndDone` sends an SSE payload ending in `data: [DONE]` through the jslib self-test entry point and asserts that C# receives the text unchanged, the done callback fires and no error is reported. `AbortWithoutActiveController_DoesNotThrow` asserts that aborting a call id with no active `fetch` does not throw.
- **`CoreAiChatDemoRealModelWebGlPlayModeTests`** (`Assets/CoreAiUnity/Tests/PlayMode/LlmVerification`, requires `COREAI_LLM`, categories `RealLlm` and `WebGL`). It loads the `CoreAiChatDemo` scene against the endpoint in the shared `CoreAISettings` asset (or the one named by `COREAI_TEST_BASE_URL` / `COREAI_TEST_MODEL`, when set) and talks to `SmartChat` under a test-scoped memory key that teardown erases.
  - `CoreAiChatDemo_RealModel_StreamsStopAndRecovers`: partial text appears in the streaming bubble before the first turn completes and the final answer is non-empty; a long second turn is stopped with `CoreAiChatPanel.StopAgent()`, returns `null`, the panel unlocks within 10 s and the bubble text no longer changes; a third turn returns a non-empty answer.
  - `CoreAiChatDemo_RealModel_StopCancelsTheTurnAndChatRecovers`: the same Stop contract on a turn stopped the frame after admission, without needing a sampled stream.
  - Both tests skip (not fail) when the settings asset is Offline. The streaming test also skips when streaming is disabled for `SmartChat` (in the WebGL player this means `WebGlNativeStreaming` is off) or when the endpoint delivers a whole answer before a frame can sample it. A turn that errors, ends empty, ignores Stop or never settles fails.

### Local (LLMUnity GGUF)

LLMUnity runs the GGUF as its built-in OpenAI-compatible server (`llm.remote = true`, `LlmUnityServerPort`, default 13333). CoreAI talks to it through `OpenAiChatLlmClient` over `LlmUnityServerHttpSettings` (`POST /v1/chat/completions`), so local streaming is ordinary SSE through the HTTP path above — the same parser, cancellation and think-block filtering as a remote API. The server exposes no `/v1/models`, so the model name is passed explicitly. Not available on WebGL.

- **Tool channel** → from `LlmUnityToolChannel` on the settings asset (legacy client) or `LlmEndpointDescriptor.ToolChannel` (runtime endpoints). `Auto` means native `tool_calls` on the legacy client (the bundled LlamaLib server accepts `tools`; the client is built before the server exists, so there is no probe) and one probe request on activation for runtime endpoints; `Text` forces text-shaped extraction for a server build that rejects `tools`.
- **Think blocks** → filtered centrally in `MeaiLlmClient`.

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

Those blocks must never reach the UI, but:

- Opening and closing tags can arrive in **separate chunks** (e.g. `"<thi"` + `"nk>…"`).
- A stray `<` that is **not** part of a `<think>` tag must still be rendered.
- The stream can end in the middle of `<think>` — we must flush cleanly.

`CoreAI.Ai.ThinkBlockStreamFilter` solves all three. It's a pure C# state machine (`MeaiLlmClient` and `CoreAiChatPanel` already run one; you need your own only when you consume a raw MEAI stream):

```csharp
ThinkBlockStreamFilter filter = new ThinkBlockStreamFilter();

await foreach (ChatResponseUpdate chunk in client.GetStreamingResponseAsync(...))
{
    string visible = filter.ProcessChunk(chunk.Text);
    if (!string.IsNullOrEmpty(visible))
        ui.Append(visible);
}

string tail = filter.Flush(); // empty in normal termination
if (!string.IsNullOrEmpty(tail)) ui.Append(tail);
```

Covered by `ThinkBlockStreamFilterEditModeTests`, including split-tag boundary cases.

### Response and persistence boundary

- Build visible incremental and final text only from `LlmStreamChunk.Text`.
- **One stream carries several assistant messages.** After every tool round the model speaks again, and
  those replies arrive as one continuous chunk sequence. `LlmStreamChunk.StartsNewMessage` marks the
  first visible chunk of each reply after the first. Concatenating chunks blindly glues the end of one
  reply to the start of the next — in production a student read
  `…Check yourself:**Turn finished — waiting for the student's answer on the card.**`, two messages fused into one.
  Use `StreamedMessageJoiner.Append(...)`: it is the single owner of the separation rule (blank line
  between replies, nothing before the first, no third blank line when the reply already ended with a
  paragraph). Never infer the boundary from punctuation — a reply may legitimately end with a colon
  and begin with a lowercase letter, so any heuristic is wrong in both directions.
- Treat `LlmStreamChunk.ReasoningText` as optional, ephemeral diagnostics. It may feed an explicitly
  diagnostic “thinking” view, but it must never be concatenated into the assistant answer.
- A reasoning-only stream is not an answer. It ends with no visible text and an `EmptyResponse` terminal
  chunk; CoreAI does not promote accumulated reasoning.
- `LlmStreamChunk.GenerationInProgress` is a liveness signal from the backend (an SSE `generation_progress`
  frame): no `Text`, no `ReasoningText`. Treat it as a reason to keep showing a waiting state, never as
  first content, and never persist it.
- Never append `ReasoningText` to MemoryTool, `IAgentMemoryStore` chat history, generated notes,
  `ApplyAiGameCommand`, assistant traces, analytics payloads, or other automatic records. Those sinks use
  visible `Text` only. Durable reasoning diagnostics, if a host truly needs them, require a separate
  opt-in store with explicit redaction and retention.

---

## 4. Configuration — 3-layer hierarchy

`CoreAiChatService.IsStreamingEnabled` checks the layers top-down:

1. In the **WebGL player**, without `CoreAISettingsAsset.WebGlNativeStreaming` the answer is `false` (see [§2](#2-transport-details)).
2. A **UI** value of `false` disables streaming.
3. Otherwise a **per-agent override**, when set, decides — `true` streams even if the global flag is off, `false` never streams.
4. With no override, the **global** flag decides.

| Priority | Layer | Where | Default |
|----------|-------|-------|---------|
| 1 (highest) | **UI toggle** | `CoreAiChatConfig.EnableStreaming` (Inspector) | `true` |
| 2 | **Per-agent override** | `AgentBuilder.WithStreaming(bool)` → `AgentMemoryPolicy.SetStreamingEnabled(roleId, bool?)` | *(unset)* |
| 3 | **Global** | `CoreAISettingsAsset.EnableStreaming` (Inspector: **Essentials → Global streaming**) / static `CoreAISettings.EnableStreaming` | `true` |

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
CoreAiChatService service = CoreAiChatService.TryCreateFromScene();
bool useStream = service.IsStreamingEnabled("SmartChat", uiFallback: true);
```

Covered by `CoreAiChatServiceEditModeTests`.

---

## 5. UI integration

`CoreAiChatPanel` keeps its own `ThinkBlockStreamFilter` and resets it at the start of every streamed turn. A subclass can add a per-role gate by overriding `ShouldUseStreamingForRole(roleId, uiConfigWantsStreaming)`; the panel streams only when that and `CoreAiChatService.IsStreamingEnabled` both agree. As chunks arrive:

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

`CoreAi.StreamAsync` yields visible text only and drops `StartsNewMessage`, so it suits roles without tools. For a role that calls tools, enumerate `CoreAi.StreamChunksAsync` and join with `StreamedMessageJoiner` as in the service example above.

---

## 6. Orchestrator streaming

Streaming is not limited to `CoreAiChatService`; it flows through the full AI pipeline (`IAiOrchestrationService`) for **both** interactive chat (`RunStreamingAsync`) **and** non-interactive task execution (`RunTaskAsync`, via the `CompleteForTaskAsync` helper) — streaming is the default path whenever `EnableStreaming` is on.

`CoreAiChatService.SendMessageStreamingAsync` is a thin layer **over** `IAiOrchestrationService.RunStreamingAsync` (the registered `QueuedAiOrchestrator`), so a chat turn gets the full pipeline — 4-layer prompt prefix and ordered tail, `IAuthorityHost.CanRunAiTasks`, the priority queue with `MaxConcurrent` and `CancellationScope`, structured validation, `ApplyAiGameCommand` publication and `IAiOrchestrationMetrics`. What the service adds:

| Service adds | Detail |
|--------------|--------|
| Chat request defaults | `SourceTag = "Chat"`, the actor context of the role, `CancellationScope` = the actor's session id (string overloads) |
| Idle deadline | `LlmRequestTimeoutSeconds`, re-armed by every chunk and tool-call event (§8) |
| Host deadline | optional `deadlineToken`, kept apart from the caller token (§8) |
| Interruption mapping | a fired deadline at a live caller becomes `LlmOperationTimeoutException`; an error chunk after a caller stop becomes `OperationCanceledException` |

Use `CoreAi.OrchestrateStreamAsync(task)` for agent workflows (Creator / Programmer / Mechanic) and `CoreAi.StreamAsync("text")` for simple chat.

Inside `AiOrchestrator.RunStreamingAsync`:

1. Build snapshot + prompt composer (shared with `RunTaskAsync`, factored into `BuildRequestAsync`).
2. Create `LlmCompletionRequest` with tools, history, temperature.
3. `await foreach` on `ILlmClient.CompleteStreamingAsync` (already includes `ThinkBlockStreamFilter` in `MeaiLlmClient`); forward `ReasoningText` for diagnostics without adding it to visible text.
4. Accumulate only `Text` in a `StringBuilder` (required for step 5).
5. When the stream ends — validate and publish only accumulated visible text, append only that text to chat history, and record metrics.

`QueuedAiOrchestrator.RunStreamingAsync` forwards through its own single-reader producer/consumer queue (`AsyncChunkQueue`: a `ConcurrentQueue` plus a reusable `ManualResetValueTaskSourceCore` wake-up, so parking and waking the reader allocate nothing), respecting `MaxConcurrent` and `CancellationScope`.

---

## 7. Streaming tool-calling

Streaming tool-calling uses a **dual-path architecture**:

### Path 1: Text-based extraction (fallback)

The fallback for endpoints without a native tool channel (a local server whose model writes tool calls as text) — see [Prose is interpreted only on the fallback path](#prose-is-interpreted-only-on-the-fallback-path).
`MeaiLlmClient.TryExtractToolCallsFromText` delegates to the portable `LlmToolCallTextExtractor`, which recognises **four** text shapes, not only JSON. They differ in how much evidence of a *call* the shape itself carries, and the guards differ accordingly:

| Shape | Example | Evidence it is a call | Recognised |
|-------|---------|-----------------------|------------|
| JSON | `{"name":"memory","arguments":{…}}` (`arguments_json` string accepted too; multiple objects per reply) | the `name` + `arguments` structure | by shape; with a registry only for a declared name |
| XML (Hermes / Qwen-Agent) | `<function=memory><parameter=action>clear</parameter></function>` | the XML tags | by shape; with a registry only for a declared name |
| Function call | `read_skill("Alchemy")`, `world_command(action='spawn', x=1)` — the **whole** reply | **none**: `print("Hello, world!")` is the same string | **only with a registry, and only for a declared name** |
| Memory pseudo-write | `Action=write content="…"` ending its line | the `Action=write` keyword | by shape; maps to `memory`, so with a registry only if `memory` is declared |

Guards common to every shape:

- fenced code blocks (` ```...``` `) are ignored — **including an unclosed trailing fence**: a reply cut off by the token limit inside a ```` ```json ```` example must not execute the example;
- JSON wrapped in backticks or matching quotes is a citation, not a command;
- placeholder names (`<tool_name>`) are rejected;
- partial/malformed JSON is skipped here; truncation repair lives in `TryBuildMalformedTextToolCall`, behind the same channel gate.

**Registry of declared tool names.** `LlmToolCallTextExtractor.TryExtract(text, knownToolNames, …)` and `StripForDisplay(text, knownToolNames)` take the names of the tools the request declared (`ILlmTool.Name`). Invariant: **a call is an address to a declared tool** — any other name stays visible text in every shape, because it cannot execute anyway and hiding it takes a line of the lesson away from the learner. Without a registry (the legacy overloads) JSON, XML and the pseudo-write are still recognised by shape, but **function-call syntax never fires**: with no registry `read_skill("x")` and `print("x")` are indistinguishable, and a Python tutor's one-line answer used to become a call to a non-existent tool `print` — the model got `Unknown tool`, the learner an empty bubble. There is deliberately no stop-list of Python names (`print`/`input`/`len`); it would end at the first new lesson. The runtime call sites pass the registry: `MeaiLlmClient` hands its policy's `KnownToolNames` (`declaredToolNames`) to `TryPortableToolExtract`, the hybrid hold and the display strip, `SmartToolCallingChatClient` passes `KnownToolNames`, and `AiOrchestrator` strips leaked calls against the request's tools. Function-call syntax is therefore recognised for declared tools on the fallback path. The names include the function names of `IAIFunctionsLlmTool` wrappers (for example `camera_look`), so text-shaped calls to them are extracted and stripped too.

### Path 2: Native SSE `delta.tool_calls`

For cloud providers (OpenAI, Anthropic via OpenRouter) that emit `delta.tool_calls` in SSE chunks.
`MeaiOpenAiChatClient.ExtractDeltaUpdate` parses `choices[0].delta.tool_calls` and emits `FunctionCallContent` in `ChatResponseUpdate`.

If the SSE stream contains `FunctionCallContent`, `MeaiLlmClient` uses native detection instead of text extraction.

### Prose is interpreted only on the fallback path

**One decision, taken by CHANNEL, governs everything the loop does with the model's prose:** holding it back, reading tool calls out of it, and repairing truncated JSON in it. All three run only when **`Tools`** is non-empty, **`ForcedToolMode`** is not `None`, **and** the endpoint has no native tool channel (**`SupportsNativeToolCalling == false`**) or the request sets the escape hatch below (`LlmToolChannelPolicy.InterpretsProse`). Declared tools that bound no executable function do **not** switch parsing on: a failed local binding does not change the endpoint's protocol. A remote OpenAI-compatible endpoint is native, so its text streams live, delta by delta, and is never parsed.

**The channel is declared where the endpoint is created, never guessed.** `MeaiLlmClient.CreateHttp` and the `OpenAiChatLlmClient` constructors take a **required** `supportsNativeToolCalling` (no default). Remote OpenAI-compatible clients pass `true`; every place that builds a **local llama.cpp / LLMUnity** endpoint passes the resolved channel decision (`LlmToolChannelResolution`, `toolChannel.Native`): `LlmEndpointClientFactory` (runtime endpoints probe the server on activation when `ToolChannel = Auto`), the LlmUnity profile in `LlmClientRegistry`, and `LlmPipelineInstaller`'s LLMUnity client (no probe — the client is built before the server exists, so `LlmUnityToolChannel = Auto` means native, because the bundled LlamaLib server has a native channel). Set the channel to `Text` for a server build that rejects `tools`: its model then calls a tool by writing JSON in the answer, and prose interpretation stays ON. Wrong in that direction and every local-model tool stops working with no error anywhere; wrong in the other and a teacher's JSON example is executed as a command. See [HTTP_TRANSPORT_SPEC](HTTP_TRANSPORT_SPEC.md).

Two reasons, both from real use:

- **Correctness.** Reading prose for calls means acting on what the model *said* instead of on the channel it said it through. A tutor explaining JSON — the everyday job of a programming teacher — writes an object shaped exactly like a call, and the engine executed the example instead of showing it. Shape can never separate an example from a command; the channel can. The malformed-JSON repair is the sharpest case: it *reconstructs* a truncated object out of prose and runs it.
- **Feel.** The hold begins at the first still-open `{`, which a Python teacher types constantly (a dict, a set, an f-string), so prose froze mid-sentence and then arrived in a lump.

Behind this gate: the hybrid hold and its span scanning (`GetHybridSafeSegments`, `GetFirstIncompleteBraceStart`, `FindToolCallJsonSpans`), `TryExtractToolCallsFromText` (Path 1), `TryBuildMalformedTextToolCall`, the `<think>`-block tool-call diagnostic, `SmartToolCallingChatClient`'s own text extraction on the non-streaming path (constructor flag `allowTextShapedToolCalls`, default **false**), and in `AiOrchestrator` both `LlmToolCallTextExtractor.StripForDisplay` and the tool-result repetition filter. Deliberately NOT behind it: execution of `FunctionCallContent` that arrived on the provider's own channel, and `LlmResponseSanitizer.StripLeadingSystemPromptEcho` (an identity match against the prompt we sent, not a guess about content).

**Escape hatch:** **`AllowTextShapedToolCallsOnNativeEndpoint = true`** (on `AiTaskRequest`, copied to `LlmCompletionRequest`) restores prose interpretation for an endpoint that advertises a native channel and then answers with JSON in the text (proxies in front of local models do this). The symptom that calls for it is a tool that never runs while the reply contains its call. Off by default; nothing in the runtime sets it.

There is no second escape hatch: `BufferFullStreamingIterationWhenToolsDeclared` (buffer the whole assistant iteration before any **`LlmStreamChunk.Text`**) was removed in 7.35.0 and has no replacement. The hold has exactly one cause: we hold prose because we are parsing it as tool calls (`hybridToolJsonHold == interpretProseAsToolCalls`); visible prose keeps streaming live on every tool turn. After Path 1 extraction or Path 2 **`delta.tool_calls`**, any assistant prose that was not yet forwarded is reconciled against the JSON-stripped held tail and emitted as trailing **`Text`** chunks so short prefixes (for example “Working…”) are not lost.

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
| Argument preflight | Missing / `null` required arguments and arguments that cannot bind to the parameter type are refused before the tool body runs (retry allowed); see [TOOL_AUTHORING_GUIDE](TOOL_AUTHORING_GUIDE.md#required-arguments-null-is-missing-empty-is-present). |
| Duplicate detection | Signature-based (name + arguments hash). Only a **cross-turn echo** is suppressed — a call whose signature an *earlier* turn of the same request already registered after making progress. Identical calls issued together in one batch all execute. Per-tool `AllowDuplicates` flag overrides. |
| Consecutive error tracking | Counts batches/turns in which **every** call failed; a partly successful batch resets it. When `MaxToolCallRetries` is reached (or the roundtrip cap is hit) the loop makes one final tools-disabled summary turn. |
| Notification | Every tool execution fires `IToolCallEventPublisher.PublishStarted/Completed/Failed` (portable) → `MessagePipeToolCallEventPublisher` adapter → `GlobalMessagePipe`. Also calls `IToolExecutionNotifier.NotifyToolExecuted` → `CoreAiToolExecutionNotifier` adapter → `CoreAi.NotifyToolExecuted`. |

### Stop / clear guarantees

All stop paths of `CoreAiChatPanel` (the send button in stop mode, Esc, the public `StopAgent()`) run one private stop routine:

- It has an `_isStopping` re-entrancy guard — concurrent Escape + button click cannot double-fire.
- The send button stays enabled while a request is running (when **Enable Stop Generation** is on); it is the stop control in that state.
- It advances the turn generation, calls `CoreAi.StopAgent(roleId)` (falling back to `CoreAiChatService.StopAgent`), cancels the active request CTS, replaces the root CTS and resets streaming/typing/sending UI state — even when the static `CoreAi` path is unavailable.
- **`ClearChat()`** runs the same stop before clearing the feed and history.

## 8. Timeout & retry architecture

### Timeout enforcement

> **Rule:** The Unity UI and portable pipeline both enforce the configured request timeout. Every Unity-side deadline must be PlayerLoop-driven so it remains live in WebGL.

`CancellationTokenSource.CancelAfter()` and finite `Task.Delay` rely on managed timer support that is **non-functional in WebGL**, causing indefinite hangs.

The active arrangement is:
- `AiOrchestrator` **passes `cancellationToken` through** without adding a timer.
- `CoreAiChatService.SendMessageAsync`, `SendMessageResultAsync` and `SendMessageStreamingAsync` create a linked `CancellationTokenSource` guarded by an **idle watchdog** (`CoreAiChatService.IdleTimeoutDeadline`): a UniTask `CancelAfterSlim(…, DelayType.Realtime)` timer driven by the PlayerLoop. Every streamed chunk and every tool-call start/finish/failure for the turn's role **re-arms** it (the timer restarts for a full window; a failed re-arm never cancels the turn), so a multi-step turn is cancelled only after a real stall of `LlmRequestTimeoutSeconds`, never because its steps add up. The vision path (`AskWithCameraAsync`) uses a single `CancelAfterSlim` without re-arming because it is a single provider call.
- The caller's token and the deadlines are kept apart. `AiTaskRequest.CallerCancellationToken` is the caller's token only; `AiTaskRequest.DeadlineCancellationToken` is the idle deadline joined with an optional **host deadline** (the overloads that take a `deadlineToken`, which `CoreAiChatPanel` feeds from `CoreAiChatExternalSubmitOptions.DeadlineCancellationToken`); the operation token links all of them. A deadline that fires while the caller's token is alive ends the turn with `LlmOperationTimeoutException` (buffered and streaming) and the orchestrator records `DeadlineCancellation`; a caller stop stays a cancellation even when a deadline fired too.
- `TimeoutLlmClientDecorator` provides the portable pipeline bound; `LlmPipelineInstaller` injects `UnityMainThreadLlmAsyncMarshaler`, whose `DelayAsync` is also UniTask PlayerLoop-driven.
- `LoggingLlmClientDecorator` and `RetryingStreamingLlmClientDecorator` use that same injected delay for retry backoff.
- The timeout value comes from `ICoreAISettings.LlmRequestTimeoutSeconds` (new asset/options and static fallback default: 0, disabled; existing serialized values are retained).

```csharp
// Inside CoreAiChatService.SendMessageStreamingAsync(request, ct, deadlineToken) (simplified)
request.CallerCancellationToken = ct;              // the caller's token only
request.DeadlineCancellationToken = deadlineToken; // host deadline (may be None)
float timeoutSec = _settings?.LlmRequestTimeoutSeconds ?? 0f;
if (timeoutSec > 0)
{
    idleCts = new CancellationTokenSource();
    anyDeadline = LinkDeadlines(idleCts.Token, deadlineToken, out deadlineCts); // idle + host deadline
    request.DeadlineCancellationToken = anyDeadline;
    timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct, anyDeadline);
    deadline = new IdleTimeoutDeadline(idleCts, timeoutSec); // one PlayerLoop watchdog per turn
    effectiveCt = timeoutCts.Token;
}
// for each chunk of _orchestrator.RunStreamingAsync(request, effectiveCt):
//   a cancellation while a deadline fired and ct is alive -> throw new LlmOperationTimeoutException();
//   an error chunk after ct was cancelled                 -> OperationCanceledException (caller stop)
deadline?.Rearm(); // per chunk: restart the idle window
```

### Retry centralization

> **Rule:** Network-level retries (HTTP 429, 5xx, exponential backoff) are handled **exclusively** by `LoggingLlmClientDecorator` (plus the pre-commit streaming retry in `RetryingStreamingLlmClientDecorator`). The orchestrator invokes the LLM client exactly once per request. No retry — and no `FallbackLlmClientDecorator` hop — happens after the caller cancelled; the fault propagates as that cancellation (`LlmCancellation`). `CircuitBreakerLlmClientDecorator` is not in the default Unity chain; when a host adds it, a caller cancel records no failure and releases its half-open probe slot.

Because the orchestrator itself never retries, a failure does not multiply into orchestrator retries × decorator retries network requests.

### Error propagation

`CoreAiChatService` does not swallow exceptions. Errors from `AiOrchestrator` → `LoggingLlmClientDecorator` → `ILlmClient` propagate to `CoreAiChatPanel`, which classifies them with `LlmCancellation`: a timeout goes to `ResolveTimeoutMessage(false)`, a cancellation to `ResolveCancelledMessage()` (default: nothing is added to the chat), a thrown failure to `ResolveErrorMessage` (default `LlmErrorPresentation.ToUserMessage`) and an error inside a stream chunk to `ResolveStreamErrorMessage` (default: the chunk error without its `HTTP error NNN:` prefix). The log keeps the diagnostic text. See [ARCHITECTURE — Timeout & Retry Rule](ARCHITECTURE.md#timeout--retry-rule-v151).

## 9. Known limitations

- **No output-length timeout** — there is a per-request cancellation token but no *total response length* guard. Add one externally if you need it.
- **Mobile** — HTTP streaming behaviour depends on the OS / Mono / IL2CPP stack; measure before shipping.
- **Partial SSE `tool_calls`** — Cloud providers may split tool call arguments across multiple SSE chunks. Split-argument accumulation across chunks **is implemented**: `MeaiOpenAiChatClient.SseToolCallAccumulator` buffers per-index argument fragments (each `delta.tool_calls[index]` keeps its own `StringBuilder`; `Feed` appends fragments, `DrainCompleted` surfaces every call whose argument JSON is already complete so it can run while the model is still generating, and `Flush` emits the rest at the end of the stream). Remaining caveats are handled defensively: parallel/duplicate index entries each accumulate independently, and malformed or truncated argument JSON is not dropped — it is surfaced under the `__raw_arguments` / `__parse_error` markers (`ToolCallArgumentMarkers`), and `ToolExecutionPolicy` refuses that call without running the tool and asks the model to resend complete JSON.
- **WebGL — incremental SSE** — In the **WebGL player**, keep **`CoreAISettingsAsset.WebGlNativeStreaming`** on (the default; assets created before v1.6.13 may still have it off) so **`FetchSseOpenAiTransport`** + **`CoreAiSseFetch.jslib`** deliver real **`fetch`** streaming. If it is off, the player uses **`UnityWebRequest`** and non-streaming completions: the full reply lands at once. Both paths obey the browser's CORS rules, and the backend must not buffer SSE for the bridge to show tokens live. Verification: [Verifying WebGL streaming](#verifying-webgl-streaming).

Related deep dives: [LUA_SANDBOX_SECURITY](../../CoreAI/Docs/LUA_SANDBOX_SECURITY.md) · [TOOL_CALLING_BEST_PRACTICES](../../CoreAI/Docs/TOOL_CALLING_BEST_PRACTICES.md).

### Provider response wait

`ICoreAISettings.LlmResponseWaitTimeoutSeconds` defaults to 180 seconds. `LlmEndpointDescriptor.ResponseWaitTimeoutSeconds` and `IOpenAiHttpSettings.ResponseWaitTimeoutSeconds` override it per endpoint (null inherits, zero disables). The semantic timer starts before headers, resets on nonempty text/reasoning/tool-call deltas, and bounds every gap, including first-token silence. SSE comments and processing metadata do not reset it. Five minutes of continuously streaming reasoning are valid with no total ceiling. Buffered calls apply the same wait to the whole response. A breach raises `LlmErrorCode.Timeout` and never silently restarts the turn through the removed 15-second starvation watchdog.

The optional total ceiling (`RequestTimeoutSeconds`, default zero) remains independent and is not re-armed by deltas. The browser fetch bridge enforces that total ceiling; the C# parser enforces semantic response wait through the injected PlayerLoop marshaler, without threads or blocking waits. Chat/orchestrator idle windows, tool deadlines and benchmark scenario deadlines keep their separate behavior. Existing serialized total limits remain unchanged during migration.
