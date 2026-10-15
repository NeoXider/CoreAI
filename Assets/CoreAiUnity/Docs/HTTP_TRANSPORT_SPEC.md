# HTTP LLM transport (Core vs Unity WebGL)

Portable **`CoreAI.Core`** exposes **`IOpenAiHttpTransport`** for OpenAI-compatible **`POST /chat/completions`**.

| Implementation | Assembly | When used | SSE streaming |
|----------------|----------|-----------|----------------|
| **`HttpClientOpenAiTransport`** | `CoreAI.Core` | Editor, standalone, mobile, any target where **`System.Net.Http`** is valid | Yes (`OpenSseResponseStreamAsync`) |
| **`UnityWebRequestOpenAiTransport`** | `CoreAI.Source` | WebGL player (**`UNITY_WEBGL && !UNITY_EDITOR`**): every request when **`WebGlNativeStreaming`** is off; non-streaming requests when it is on | No — full JSON + **simulated** stream |
| **`FetchSseOpenAiTransport`** + **`CoreAiSseFetch.jslib`** | `CoreAI.Source` | WebGL player, streaming requests when **`WebGlNativeStreaming`** is on | Yes — browser **`fetch`** reads SSE incrementally |
| **`WebGlCompositeOpenAiTransport`** | `CoreAI.Source` | WebGL player when **`WebGlNativeStreaming`** is on (the default): routes streaming to the fetch bridge and non-streaming to `UnityWebRequestOpenAiTransport` | Yes |

The Unity transports compile only with the `COREAI_LLM` define; the fetch bridge and the composite also only for the WebGL player.

**Composition:** `MeaiLlmClient.CreateHttp(..., supportsNativeToolCalling: decision.Native, memoryStore: store)`
selects the transport and passes the explicit endpoint capability into `MeaiOpenAiChatClient`.
`MeaiLlmClient`, its factories, and `OpenAiChatLlmClient` no longer have a channel default: the required
`bool supportsNativeToolCalling` sits before the optional `memoryStore`. This is an intentional
API change; when updating a third-party host, pass its setting or probe result, not a random constant.
Existing low-level `MeaiOpenAiChatClient(settings, transport, ...)` constructors keep the ordinary
OpenAI API contract with native tools; the new overload takes the capability explicitly.

### Single-request tool ban

`ForcedToolMode=None` — a local execution ban for Native and Text, including streaming mode.
The request performs one provider turn, builds no unused bindings, and starts no
approval or empty-response recovery loop. JSON examples are preserved as text; unsolicited
native calls are not executed. Native passes `tool_choice=none`; Text still strips
unsupported tool fields from the HTTP body, keeping the ban on the CoreAI side.

### Channel selection and text mode

The runtime factory first checks server readiness. Then `ToolChannel=Auto` makes one probe with a
tool declaration at activation — the same for HTTP and LLMUnity. Accepted `tools` enables Native;
a jinja rejection enables Text; a transport/other error keeps activation at Text and writes the reason to a warning.
`Native` and `Text` skip the capability probe. Caller cancellation interrupts activation even when
the probe adapter returned a result after cancellation. The probe checks schema acceptance by the server,
while the model's own tool-choice accuracy is verified separately.

Text keeps local `AIFunction` entries for execution via MEAI but removes outgoing `tools`, `tool_choice`,
and `parallel_tool_calls`. The HTTP adapter removes them **after** `ExtraBodyJson`, so extra parameters
do not bring the unsupported channel back onto the wire. This works the same for ordinary responses, SSE, and
follow-up requests after tool execution. Native keeps the standard MEAI `ToolMode` modes.

Prose call parsing and response cleanup use one rule: a `Text` endpoint or the explicit
`AllowTextShapedToolCallsOnNativeEndpoint = true`. An unavailable native binding alone
**does not** enable text parsing: tutorial JSON examples are preserved. Cleanup is limited to
declared tool names; an unknown name stays text. A required call without a usable
executable binding returns `InvalidRequest` before any model request; an optional one is skipped
with a diagnostic. When rounds are exhausted, the stream makes one final request without tools;
if there is neither a successful action nor a usable result, the terminal chunk carries the error,
`ProviderError`, and traces of failed calls. A successful tool preserves the existing valid
completion even with an empty final response.

### Waiting for HTTP in Unity

Streaming retry disposes each provider enumerator once. If the request already failed, a secondary
disposal failure cannot replace caller cancellation, a typed provider error, or an emitted terminal
error chunk. For thrown primary failures, the cleanup exception is retained in
`Exception.Data[ClientLimitedLlmClientDecorator.StreamDisposeExceptionDataKey]`; an optional diagnostic
sink also receives it. A cleanup-only failure still propagates. Text or executed tool output remains
the commit boundary: cleanup never authorizes replay after that boundary.

`ErrorCode` classifies a streaming failure even when its optional `Error` text is empty, including
chunks emitted by the default `ILlmClient` completion-to-stream adapter. Permanent codes are never
retried; retry exhaustion preserves the original transient error chunk and HTTP/retry metadata.
Once the caller's token is cancelled nothing is retried or sent to the fallback backend: every fault,
error chunk included, is reported as the caller's cancellation (`LlmCancellation`); a layer that has to
throw raises `OperationCanceledException` with the original fault as its `InnerException`.
Null control entries are ignored before commit. A stream containing only null entries still reaches
the bounded empty-response retry policy when its enumeration ends.

Readiness, the tool probe, and the ordinary HTTP transport use a single `AsyncOperation.completed` wait.
It does not poll `isDone` in a loop and does not occupy a frame. An already completed operation is also accepted safely;
the event rules are described in [Unity Scripting API](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/AsyncOperation-completed.html).
The cancellation callback only completes the wait; `Abort`, unsubscription, and request release happen in the saved
Unity context. A pre-cancelled request creates no network operation. Unity API is not moved into `Task.Run`.

### `HttpClientOpenAiTransport`

**Client lifecycle:** **`HttpClientOpenAiTransport`** reuses **shared** `HttpClient` instances over an **`HttpClientHandler`** instead of creating/disposing a client per request (per-request disposal leaves sockets in `TIME_WAIT` and risks ephemeral-port exhaustion under load). There is one bounded (non-streaming) and one streaming client, each in two variants: loopback URLs use a handler with `UseProxy = false`, so local LLM traffic never goes through a system proxy or VPN filter. (`HttpClientHandler` is used rather than `SocketsHttpHandler` so the transport stays valid on Unity's default .NET Standard 2.0 profile; connection pooling is handled by the runtime's ServicePoint layer.) The shared clients' `Timeout` is `InfiniteTimeSpan`; per-request limits use a linked **`CancellationTokenSource`**: the whole non-streaming request, and only the time to response headers for a stream. The transport raises **`OperationCanceledException`** when such a limit fires; while the caller's token is still alive, **`MeaiOpenAiChatClient`** turns it into **`LlmClientException`** with **`LlmErrorCode.Timeout`**, so retry and fallback treat it as a timeout, not a user cancellation. The shared client is never disposed by the streaming path.

**Stream-open reliability:** **`MeaiOpenAiChatClient`** **retries** a transport-level send failure when opening the SSE stream (up to three attempts in all, with a 300 ms then 600 ms backoff). Reusing a **shared** `HttpClient` means a pooled keep-alive connection the local server has already closed can surface on the next send as `System.Net.Http`'s "An error occurred while sending the request"; a fresh attempt opens a new connection, so this does not fail the whole request. A genuinely-down backend still surfaces promptly as **`BackendUnavailable`** once the bounded retries are exhausted.

**Editor tests:** **`MeaiOpenAiChatClientEditorTestHooks.HttpClientFactory`** is honored inside **`HttpClientOpenAiTransport`** only.

**Platform defines:** Convenience ctor **`MeaiOpenAiChatClient(settings, log)`** exists when **`!UNITY_WEBGL || UNITY_EDITOR`** so Edit Mode keeps **`HttpClient`** mocks even if the active build target is WebGL.

### WebGL fetch bridge (`CoreAiSseFetch.jslib`)

**`UnityWebRequest`** does not deliver SSE incrementally, so token streaming in the WebGL player needs the fetch bridge: keep **`CoreAISettingsAsset.WebGlNativeStreaming`** on (the default; assets created before v1.6.13 may still have it off). With it off, `CoreAiChatService` uses non-streaming HTTP in the player. Behaviour and player verification: [STREAMING_ARCHITECTURE — Transport details](STREAMING_ARCHITECTURE.md#2-transport-details).

The bridge ships in `com.neoxider.coreaiunity` (`Runtime/Plugins/WebGL/CoreAiSseFetch.jslib`) and is used behind **`CoreAISettingsAsset.WebGlNativeStreaming`**. It is a byte pipe: it forwards the decoded response body to C# unchanged, and `MeaiOpenAiChatClient` owns SSE framing, `data: [DONE]`, `tool_calls` and `finish_reason`.

- **Guarded callbacks.** C# is called through guarded `open` / `chunk` / `done` / `error` wrappers, so a callback failure becomes a `console.warn`, not a browser `Uncaught` main-loop error. After `done` or `error`, later callbacks for the same call are dropped.
- **Safe abort.** `CoreAi_FetchSseAbort` for a call that has already finished (no active controller) does nothing; an active abort runs on the next browser tick so it cannot throw into the Unity code that requested it.
- **Credentials.** `fetch` is sent with `credentials: 'omit'` unless **Fetch credentials (same-origin)** (`sameOriginCredentials`, off by default; Inspector: **Advanced Settings → WebGL**) asks for `same-origin`.
- **Logging.** The DevTools `console.log` lines (open / response / done) are commented out in the jslib; uncomment them for tracing. `console.warn` stays on for read and network errors.
- **`Content-Type: application/json` is always sent.** **`FetchSseTransportProtocol.BuildHeaderString`** guarantees the header even when the caller supplies none — without it, the browser defaults to `text/plain;charset=UTF-8`, which Groq tolerates but LM Studio's Express server hard-resets as "Failed to fetch".
- **Rolling body-inactivity watchdog.** The per-request **`OpenAiHttpPostRequest.TransportTimeoutSeconds`** (derived from **`IOpenAiHttpSettings.RequestTimeoutSeconds`**) bounds both the pre-header wait and inactivity **during** the SSE body: the jslib re-arms a `setTimeout` after every delivered read (`armIdleWatchdog`), so a server that sends headers and then stalls mid-stream aborts instead of hanging forever. On fire, the jslib reports reason `"Timeout"`, and **`FetchSseOpenAiTransport`** surfaces it as a **typed** **`LlmClientException(LlmErrorCode.Timeout)`** instead of a fake HTTP-0 "CORS/network" failure.
- **429 retry window from the error body.** On WebGL, `fetch` cannot read the `Retry-After` header (CORS strips it), so `MeaiOpenAiChatClient.ResolveRateLimitBackoffMs` falls back to parsing the window out of the error body text (Groq's `"Please try again in 14.017s"`, minutes+seconds supported, capped at 20s, +250ms margin) before falling back further to `2s * retryIndex`.
- **Per-callback string allocations are freed.** Every `stringToNewUTF8` used to marshal a chunk/open/error string into wasm is freed (`_free`) right after the dynCall returns, so a long session does not grow the wasm heap chunk by chunk.
