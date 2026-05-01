# HTTP LLM transport (Core vs Unity WebGL)

Portable **`CoreAI.Core`** exposes **`IOpenAiHttpTransport`** for OpenAI-compatible **`POST /chat/completions`**.

| Implementation | Assembly | When used | SSE streaming |
|----------------|----------|-----------|----------------|
| **`HttpClientOpenAiTransport`** | Core | Editor, standalone, mobile, any target where **`System.Net.Http`** is valid | Yes (`OpenSseResponseStreamAsync`) |
| **`UnityWebRequestOpenAiTransport`** | CoreAI.Source | **`UNITY_WEBGL && !UNITY_EDITOR`** (browser player) | No — **`MeaiOpenAiChatClient`** uses full JSON completion and **simulated** **`ChatResponseUpdate`** yields |

**Composition:** **`MeaiLlmClient.CreateHttp`** selects the transport and constructs **`MeaiOpenAiChatClient(settings, transport)`**.

**Editor tests:** **`MeaiOpenAiChatClientEditorTestHooks.HttpClientFactory`** is honored inside **`HttpClientOpenAiTransport`** only.

**Platform defines:** Convenience ctor **`MeaiOpenAiChatClient(settings, log)`** exists when **`!UNITY_WEBGL || UNITY_EDITOR`** so Edit Mode keeps **`HttpClient`** mocks even if the active build target is WebGL.

**Follow-up (optional):** true SSE in the browser can implement **`IOpenAiHttpTransport`** with **`SupportsSseStreaming = true`** using a **`.jslib`** **`EventSource`** / **`fetch`** reader — see [`STREAMING_ARCHITECTURE.md`](STREAMING_ARCHITECTURE.md).
