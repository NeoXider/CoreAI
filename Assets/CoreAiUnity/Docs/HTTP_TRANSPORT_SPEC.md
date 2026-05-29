# HTTP LLM transport (Core vs Unity WebGL)

Portable **`CoreAI.Core`** exposes **`IOpenAiHttpTransport`** for OpenAI-compatible **`POST /chat/completions`**.

| Implementation | Assembly | When used | SSE streaming |
|----------------|----------|-----------|----------------|
| **`HttpClientOpenAiTransport`** | Core | Editor, standalone, mobile, any target where **`System.Net.Http`** is valid | Yes (`OpenSseResponseStreamAsync`) |
| **`UnityWebRequestOpenAiTransport`** | CoreAI.Source | **`UNITY_WEBGL && !UNITY_EDITOR`**, default when **`WebGlNativeStreaming`** is off | No — full JSON + **simulated** stream |
| **`FetchSseOpenAiTransport`** + **`CoreAiSseFetch.jslib`** | CoreAI.Source | **`UNITY_WEBGL && !UNITY_EDITOR`** when **`WebGlNativeStreaming`** is on | Yes — browser **`fetch`** reads SSE incrementally |

**Composition:** **`MeaiLlmClient.CreateHttp`** selects the transport and constructs **`MeaiOpenAiChatClient(settings, transport)`**.

**WebGL player:** without **`WebGlNativeStreaming`** (or when it is **`false`**), **`UnityWebRequest`** does not deliver SSE incrementally — use the fetch bridge (**default `true`** on new **`CoreAISettingsAsset`** since **v1.6.13**) or disable streaming for chat (see **`STREAMING_WEBGL_TODO.md`**).

**Editor tests:** **`MeaiOpenAiChatClientEditorTestHooks.HttpClientFactory`** is honored inside **`HttpClientOpenAiTransport`** only.

**Platform defines:** Convenience ctor **`MeaiOpenAiChatClient(settings, log)`** exists when **`!UNITY_WEBGL || UNITY_EDITOR`** so Edit Mode keeps **`HttpClient`** mocks even if the active build target is WebGL.

**Follow-up:** **`CoreAiSseFetch.jslib`** **`fetch`** bridge ships behind **`CoreAISettingsAsset.WebGlNativeStreaming`** (see **`STREAMING_ARCHITECTURE.md`**). Optional DevTools **`console.log`** in the jslib is commented by default (**v1.6.19**); **`console.warn`** on read / **`fetch`** errors remains. Since **v2.6.0**, the bridge calls C# through guarded `open` / `chunk` / `done` / `error` wrappers so callback failures do not escape as browser `Uncaught undefined` main-loop errors; `data: [DONE]` is treated as the stream terminator; and abort can be invoked safely even when the browser controller is already gone.
