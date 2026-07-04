# HTTP LLM transport (Core vs Unity WebGL)

Portable **`CoreAI.Core`** exposes **`IOpenAiHttpTransport`** for OpenAI-compatible **`POST /chat/completions`**.

| Implementation | Assembly | When used | SSE streaming |
|----------------|----------|-----------|----------------|
| **`HttpClientOpenAiTransport`** | Core | Editor, standalone, mobile, any target where **`System.Net.Http`** is valid | Yes (`OpenSseResponseStreamAsync`) |
| **`UnityWebRequestOpenAiTransport`** | CoreAI.Source | **`UNITY_WEBGL && !UNITY_EDITOR`**, default when **`WebGlNativeStreaming`** is off | No — full JSON + **simulated** stream |
| **`FetchSseOpenAiTransport`** + **`CoreAiSseFetch.jslib`** | CoreAI.Source | **`UNITY_WEBGL && !UNITY_EDITOR`** when **`WebGlNativeStreaming`** is on | Yes — browser **`fetch`** reads SSE incrementally |

**Composition:** **`MeaiLlmClient.CreateHttp`** selects the transport and constructs **`MeaiOpenAiChatClient(settings, transport)`**.

**WebGL player:** without **`WebGlNativeStreaming`** (or when it is **`false`**), **`UnityWebRequest`** does not deliver SSE incrementally — use the fetch bridge (**default `true`** on new **`CoreAISettingsAsset`** since **v1.6.13**) or disable streaming for chat (see **`STREAMING_WEBGL_TODO.md`**).

**Client lifecycle (since v3.0.0):** **`HttpClientOpenAiTransport`** reuses **shared** `HttpClient` instances (one bounded, one streaming) over an **`HttpClientHandler`** instead of creating/disposing a client per request — earlier per-request disposal left sockets in `TIME_WAIT` and risked ephemeral-port exhaustion under load. (`HttpClientHandler` is used rather than `SocketsHttpHandler` so the transport stays valid on Unity's default .NET Standard 2.0 profile; connection pooling is handled by the runtime's ServicePoint layer.) Per-request timeouts are enforced with a linked **`CancellationTokenSource`** (the shared client's `Timeout` is `InfiniteTimeSpan`), so a timeout now surfaces as **`OperationCanceledException`** without an inner `TimeoutException`. The shared client is never disposed by the streaming path.

**Stream-open reliability:** **`MeaiOpenAiChatClient`** **retries** a transport-level send failure when opening the SSE stream (bounded — a few quick backoff retries). Reusing a **shared** `HttpClient` means a pooled keep-alive connection the local server has already closed can surface on the next send as `System.Net.Http`'s "An error occurred while sending the request"; a fresh attempt simply opens a new connection, so this no longer fails the whole request. A genuinely-down backend still surfaces promptly as **`BackendUnavailable`** once the bounded retries are exhausted.

**Editor tests:** **`MeaiOpenAiChatClientEditorTestHooks.HttpClientFactory`** is honored inside **`HttpClientOpenAiTransport`** only.

**Platform defines:** Convenience ctor **`MeaiOpenAiChatClient(settings, log)`** exists when **`!UNITY_WEBGL || UNITY_EDITOR`** so Edit Mode keeps **`HttpClient`** mocks even if the active build target is WebGL.

**Follow-up:** **`CoreAiSseFetch.jslib`** **`fetch`** bridge ships behind **`CoreAISettingsAsset.WebGlNativeStreaming`** (see **`STREAMING_ARCHITECTURE.md`**). Optional DevTools **`console.log`** in the jslib is commented by default (**v1.6.19**); **`console.warn`** on read / **`fetch`** errors remains. Since **v2.6.0**, the bridge calls C# through guarded `open` / `chunk` / `done` / `error` wrappers so callback failures do not escape as browser `Uncaught undefined` main-loop errors; `data: [DONE]` is treated as the stream terminator; and abort can be invoked safely even when the browser controller is already gone.

**Fetch bridge hardening (4.19.0):**

- **`Content-Type: application/json` is always sent.** **`FetchSseTransportProtocol.BuildHeaderString`** guarantees the header even when the caller supplies none — without it, the browser defaults to `text/plain;charset=UTF-8`, which Groq tolerates but LM Studio's Express server hard-resets as "Failed to fetch".
- **Rolling body-inactivity watchdog.** The per-request **`OpenAiHttpPostRequest.TransportTimeoutSeconds`** (derived from **`IOpenAiHttpSettings.RequestTimeoutSeconds`**) now bounds not just the pre-header wait but also inactivity **during** the SSE body: the jslib re-arms a `setTimeout` after every delivered read (`armIdleWatchdog`), so a server that sends headers and then stalls mid-stream aborts instead of hanging forever. On fire, the jslib reports reason `"Timeout"`, and **`FetchSseOpenAiTransport`** surfaces it as a **typed** **`LlmClientException(LlmErrorCode.Timeout)`** instead of a fake HTTP-0 "CORS/network" failure.
- **429 retry window from the error body.** On WebGL, `fetch` cannot read the `Retry-After` header (CORS strips it), so `MeaiOpenAiChatClient.ResolveRateLimitBackoffMs` falls back to parsing the window out of the error body text (Groq's `"Please try again in 14.017s"`, minutes+seconds supported, capped at 20s, +250ms margin) before falling back further to `2s * retryIndex`.
- **Per-callback string allocations are freed.** Every `stringToNewUTF8` used to marshal a chunk/open/error string into wasm is freed (`_free`) right after the dynCall returns; previously every streamed chunk leaked on the wasm heap until the tab ran out of memory on long sessions.
