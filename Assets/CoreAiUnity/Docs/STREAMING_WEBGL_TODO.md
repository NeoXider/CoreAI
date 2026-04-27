# TODO — WebGL SSE streaming in `MeaiOpenAiChatClient`

**Status:** Known regression; not scheduled for 0.25.3. An application-side workaround is available.

**Affected code:** `Runtime/Source/Features/Llm/Infrastructure/MeaiOpenAiChatClient.cs` → `MeaiOpenAiChatClient.CompleteStreamingAsync` (or equivalent streaming entry point in your tree).

---

## 1. Symptoms

In a **built WebGL player**, with streaming enabled (`CoreAiChatConfig.EnableStreaming = true`,
`CoreAISettings.EnableStreaming = true`, agent using the HTTP OpenAI / OpenAI-compatible backend):

- The LLM request runs for seconds (expected — remote generation);
- `LoggingLlmClientDecorator` logs **`chunks=1`** for a response tens–hundreds of characters long
  (i.e. no real delta chunks; one terminal chunk with full `content`);
- In CoreAI chat:
  - the reply bubble **never appears** (looks like the AI said nothing);
  - the typing indicator (`. → .. → ... → .`) **spins indefinitely** until the page is reloaded;
- In **Editor / Standalone** the issue **does not reproduce** — streaming yields real delta chunks and the UI updates live.

Example WebGL log:

```
[CoreAI] [Llm] LLM ▶ (stream) traceId=… role=Teacher backend=RoutingLlmClient→OpenAiHttp
[CoreAI] [Llm] LLM ◀ (stream) wallMs=15848 chunks=1 | tokens n/a | outChars=85
  content (85 chars): Hello! Happy to help with Python…
[CoreAI] [MessagePipe] ApplyAiGameCommand … payload=Hello! Happy to help…
```

---

## 2. Root cause

**`UnityWebRequest` on WebGL does not support HTTP chunked / incremental SSE delivery.**

Under the hood, the Unity WebGL player uses JavaScript `XMLHttpRequest` through an emscripten wrapper.
Unlike .NET `HttpClient` (Standalone / Editor), the Unity `XMLHttpRequest` wrapper uses
`responseType="arraybuffer"` and does not invoke an `onprogress` callback with incremental
payload — data is available **only** in `onload` after the request fully completes.

Therefore in `MeaiOpenAiChatClient.CompleteStreamingAsync`:

1. The `/v1/chat/completions` request with `stream: true` is sent correctly — the server really streams SSE;
2. The browser receives all `data: {...}` events and buffers them in the response body;
3. `UnityWebRequestAsyncOperation.completed` fires only at the very end;
4. At that moment `MeaiOpenAiChatClient.ParseSseStream` parses the entire buffer at once and
   yields a single `LlmStreamChunk` with final `Text` + `IsDone = true`.

As a result, `await foreach` in `CoreAiChatPanel.SendStreamingAsync` receives
**exactly one chunk**, with `IsDone = true`:

- The branch `if (!string.IsNullOrEmpty(chunk.Text))` should run (`StartStreaming` + `AppendToStreaming`),
- The branch `if (chunk.IsDone)` should also run (`FinishStreaming` + `HideTypingIndicator`).

In theory that should work. In practice, on current 0.25.x WebGL, **the first branch may not run
before the pipeline advances** — hypothesis: either `Text` is unset
(full reply goes to `_chatService` via `ApplyAiGameCommand` without yielding), or a race in
`_thinkFilter.ProcessChunk` drops a prefix. Precise localization is in section 3.

---

## 3. Fix plan

### 3.1. Diagnostics (required first iteration)

- [ ] Add `Debug.Log` in `CoreAiChatPanel.SendStreamingAsync` immediately before
      `if (!string.IsNullOrEmpty(chunk.Text))` dumping `chunk.Text?.Length`,
      `chunk.IsDone`, `chunk.Error`, `chunk.UsageOutputTokens` — capture on a WebGL build
      and confirm what chunk actually arrives.
- [ ] Verify `MeaiOpenAiChatClient.ParseSseStream` on WebGL: does it yield delta chunks
      **or** only a final `IsDone` chunk with accumulated `Text`?

### 3.2. Solution A — native JS bridge for SSE (correct long-term fix)

Implement an emscripten plugin (`.jslib`) under `Runtime/Plugins/WebGL/` that:

1. Opens `fetch(url, { method: 'POST', body, headers })` with a `ReadableStream` response body;
2. Reads `response.body.getReader()` and invokes a C# callback via `[DllImport("__Internal")]` / `dynCall_*` on each chunk;
3. C# enqueues strings in `ConcurrentQueue<string>` and yields them as `IAsyncEnumerable<LlmStreamChunk>`.

Pros: real browser-grade streaming. Cons: new WebGL plugin, non-WebGL fallback, and
`#if UNITY_WEBGL && !UNITY_EDITOR` branching in `MeaiOpenAiChatClient`.

A template exists in the LLMUnity package (`undream.llmunity` uses a similar fetch bridge for model downloads) — reuse as a reference.

### 3.3. Solution B — graceful degradation (minimal cost)

If a full SSE bridge is out of scope, detect WebGL in `MeaiOpenAiChatClient.CompleteStreamingAsync`
and force an explicit non-streaming fallback:

```csharp
#if UNITY_WEBGL && !UNITY_EDITOR
    // UnityWebRequest on WebGL does not stream SSE incrementally — use synchronous CompleteAsync,
    // wrap the result in one Text + IsDone chunk. UI gets an honest "no real streaming" signal.
    var full = await CompleteAsync(request, ct);
    yield return new LlmStreamChunk { Text = full, IsDone = true };
    yield break;
#endif
```

Additionally adjust `CoreAiChatPanel.SendStreamingAsync` so that when `chunks=1 && IsDone`,
`AddMessage` is always reached via `AppendToStreaming` and `HideTypingIndicator`
(add a sanity check that the bubble is present in `MessageScroll.Children`).

### 3.4. Solution C — UI-level fallback (simplest)

In `CoreAiChatPanel`, add `protected virtual bool ShouldUseStreamingForRole(string roleId, bool uiFallback)`
and override on WebGL to return `false`. Then `SendToAI` takes the non-streaming path
(`SendNonStreamingAsync`), which is reliable: `await CompleteAsync` → `AddMessage` →
`HideTypingIndicator`.

RedoSchool currently does this at app level via reflection
(`ChatPanelController.ForceNonStreamingOnWebGl`) — worth lifting into the library.

---

## 4. Proposed rollout

1. **Now (0.25.3):** document (this file) + application-side workaround.
2. **0.26.0 (minor):** Solution C — `ShouldUseStreamingForRole` virtual hook + default
   implementation returning `false` under `#if UNITY_WEBGL && !UNITY_EDITOR`. Removes the
   infinite typing animation for all CoreAI consumers on WebGL.
3. **0.27.0 (minor):** Solution A — real fetch-SSE bridge via `.jslib`. Gate with an optional
   flag in `CoreAISettings.WebGlNativeStreaming`. Keep the older non-streaming path as fallback.

---

## 5. Related files

- `Runtime/Source/Features/Llm/Infrastructure/MeaiOpenAiChatClient.cs` — SSE parser implementation
- `Runtime/Source/Features/Chat/CoreAiChatPanel.cs` — consumer of `IAsyncEnumerable<LlmStreamChunk>`
- `Runtime/Source/Features/Chat/CoreAiChatService.cs` — `SendMessageStreamingAsync`,
  thin wrapper over `IAiOrchestrationService.RunStreamingAsync`
- `Docs/STREAMING_ARCHITECTURE.md` — overall streaming architecture; add a **WebGL SSE** subsection when fixed
- `Assets/_source/Features/ChatUI/Presentation/Controllers/ChatPanelController.cs` (RedoSchool) —
  example client workaround forcing non-streaming via reflection on `_enableStreaming`
