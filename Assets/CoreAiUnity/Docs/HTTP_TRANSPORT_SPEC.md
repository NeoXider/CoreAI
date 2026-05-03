# HTTP LLM transport (Core vs Unity WebGL)

Portable **`CoreAI.Core`** exposes **`IOpenAiHttpTransport`** for OpenAI-compatible **`POST /chat/completions`**.

| Implementation | Assembly | When used | SSE streaming |
|----------------|----------|-----------|----------------|
| **`HttpClientOpenAiTransport`** | Core | Editor, standalone, mobile, any target where **`System.Net.Http`** is valid | Yes (`OpenSseResponseStreamAsync`) |
| **`UnityWebRequestOpenAiTransport`** | CoreAI.Source | **`UNITY_WEBGL && !UNITY_EDITOR`**, default when **`WebGlNativeStreaming`** is off | No — full JSON + **simulated** stream |
| **`FetchSseOpenAiTransport`** + **`CoreAiSseFetch.jslib`** | CoreAI.Source | **`UNITY_WEBGL && !UNITY_EDITOR`** when **`WebGlNativeStreaming`** is on | Yes — browser **`fetch`** reads SSE incrementally |

**Composition:** **`MeaiLlmClient.CreateHttp`** selects the transport and constructs **`MeaiOpenAiChatClient(settings, transport)`**.

**WebGL player:** without **`WebGlNativeStreaming`**, **`UnityWebRequest`** does not deliver SSE incrementally — use native streaming or disable streaming for chat (see **`STREAMING_WEBGL_TODO.md`**).

**Editor tests:** **`MeaiOpenAiChatClientEditorTestHooks.HttpClientFactory`** is honored inside **`HttpClientOpenAiTransport`** only.

**Platform defines:** Convenience ctor **`MeaiOpenAiChatClient(settings, log)`** exists when **`!UNITY_WEBGL || UNITY_EDITOR`** so Edit Mode keeps **`HttpClient`** mocks even if the active build target is WebGL.

**Follow-up:** optional **`.jslib`** **`fetch`** bridge — implemented as **`CoreAiSseFetch.jslib`** / **`FetchSseOpenAiTransport`** behind **`CoreAISettingsAsset.WebGlNativeStreaming`** (see **`STREAMING_ARCHITECTURE.md`**).
