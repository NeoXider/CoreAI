# WebGL SSE Streaming Status

**See also:** [WebGL build troubleshooting](WEBGL_BUILD_TROUBLESHOOTING.md) (LLVM OOM, `IOException` under `ProjectSettings/Packages`, StreamingAssets guard log).

> **Current status (2.5.0):** the original WebGL SSE blocker is closed. New `CoreAISettingsAsset` instances enable `WebGlNativeStreaming` by default, and WebGL player builds can use `CoreAiSseFetch.jslib` + `FetchSseOpenAiTransport` for incremental browser `fetch` streaming. Keep this file as historical context and a player verification checklist because existing links still point here.

**Update (v1.6.13):** new **`CoreAISettingsAsset`** instances default **`WebGlNativeStreaming`** to **`true`** (Resources presets aligned). When **`false`**, the player still uses **`UnityWebRequestOpenAiTransport`** (no incremental SSE).

**Update (v1.6.0):** an optional **`.jslib`** + **`FetchSseOpenAiTransport`** path exists behind **`CoreAISettingsAsset.WebGlNativeStreaming`**. It uses browser **`fetch`** for incremental SSE; the default **`UnityWebRequestOpenAiTransport`** path still does **not** stream incrementally. **Editor / PlayMode** do not exercise the native plugin — verify in a **WebGL player** build.

**Status (historical):** Timeout and retry hangs **fixed in v1.5.1** — `CancelAfter` replaced with UniTask `CancelAfterSlim` (PlayerLoop-based, WebGL-compatible). When **`WebGlNativeStreaming`** is **`false`**, **`UnityWebRequest`** does not deliver SSE incrementally; **`CoreAiChatService`** forces non-streaming HTTP in the WebGL player.

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

## 3. Historical fix plan

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

### 3.4. Solution C — UI-level fallback (historical) + fetch bridge (current)

**Originally (CoreAI v1.5.21):** `CoreAiChatService.IsStreamingEnabled` could force **non-streaming** on the WebGL player when incremental SSE over **`UnityWebRequest`** was unsupported, avoiding a broken “typing forever” UI when the transport could not deliver chunks.

**Current (v1.6.13+):** enable **`CoreAISettingsAsset.WebGlNativeStreaming`** (default **on** for new settings assets). **`MeaiLlmClient.CreateHttp`** then uses **`FetchSseOpenAiTransport`** + **`CoreAiSseFetch.jslib`** (`fetch` + **`ReadableStream`**), and **`CoreAiChatService.IsStreamingEnabled`** allows streaming when that flag is **on** (still subject to per-role / UI overrides). When the flag is **off**, **`UnityWebRequestOpenAiTransport`** is used — no real incremental SSE; the client may use **non-streaming** completion and **simulate** stream updates. See [`STREAMING_ARCHITECTURE.md`](STREAMING_ARCHITECTURE.md) and [`HTTP_TRANSPORT_SPEC.md`](HTTP_TRANSPORT_SPEC.md).

**`CoreAiChatPanel.ShouldUseStreamingForRole`** defaults to the chat config’s streaming preference; WebGL transport gating lives in **`CoreAiChatService.IsStreamingEnabled`** (single source of truth).

---

## 4. Rollout status

1. **Shipped:** document + **`WebGlNativeStreaming`** + **`CoreAiSseFetch.jslib`** / **`FetchSseOpenAiTransport`** (**v1.6.0+**, default **on** for new settings assets since **v1.6.13**).
2. **Obsolete:** the old plan for **`ShouldUseStreamingForRole`** default **`false`** on WebGL only — transport choice is centralized in **`CoreAiChatService.IsStreamingEnabled`** + **`WebGlNativeStreaming`** instead.
3. **Still valid:** keep **`UnityWebRequest`** fallback when **`WebGlNativeStreaming`** is **off**; validate CORS / credentials for your LLM host.

---

## 5. Related files

- `Runtime/Source/Features/Llm/Infrastructure/MeaiOpenAiChatClient.cs` — SSE parser implementation
- `Runtime/Source/Features/Chat/CoreAiChatPanel.cs` — consumer of `IAsyncEnumerable<LlmStreamChunk>`
- `Runtime/Source/Features/Chat/CoreAiChatService.cs` — `SendMessageStreamingAsync`,
  thin wrapper over `IAiOrchestrationService.RunStreamingAsync`
- `Docs/STREAMING_ARCHITECTURE.md` — **WebGL SSE** subsection (fetch bridge + when **`UnityWebRequest`** path applies)
- `Assets/_source/Features/ChatUI/Presentation/Controllers/ChatPanelController.cs` (RedoSchool) —
  example client workaround forcing non-streaming via reflection on `_enableStreaming`
