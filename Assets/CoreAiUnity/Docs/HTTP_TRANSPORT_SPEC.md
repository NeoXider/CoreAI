# HTTP LLM transport (Core vs Unity WebGL)

Portable **`CoreAI.Core`** exposes **`IOpenAiHttpTransport`** for OpenAI-compatible **`POST /chat/completions`**.

| Implementation | Assembly | When used | SSE streaming |
|----------------|----------|-----------|----------------|
| **`HttpClientOpenAiTransport`** | Core | Editor, standalone, mobile, any target where **`System.Net.Http`** is valid | Yes (`OpenSseResponseStreamAsync`) |
| **`UnityWebRequestOpenAiTransport`** | CoreAI.Source | **`UNITY_WEBGL && !UNITY_EDITOR`**, default when **`WebGlNativeStreaming`** is off | No — full JSON + **simulated** stream |
| **`FetchSseOpenAiTransport`** + **`CoreAiSseFetch.jslib`** | CoreAI.Source | **`UNITY_WEBGL && !UNITY_EDITOR`** when **`WebGlNativeStreaming`** is on | Yes — browser **`fetch`** reads SSE incrementally |

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

Readiness, the tool probe, and the ordinary HTTP transport use a single `AsyncOperation.completed` wait.
It does not poll `isDone` in a loop and does not occupy a frame. An already completed operation is also accepted safely;
the event rules are described in [Unity Scripting API](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/AsyncOperation-completed.html).
The cancellation callback only completes the wait; `Abort`, unsubscription, and request release happen in the saved
Unity context. A pre-cancelled request creates no network operation. Unity API is not moved into `Task.Run`.


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
