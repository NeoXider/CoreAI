# Genie and Spellcraft gameplay demos

Type a wish or spell in your own words and the selected model requests one guarded game action.
The HUD displays latency, usage and accepted tool calls. C# owns wish charges, mana, clamps and
Unity object access.

- `QwenGenieDemo.unity`: a wish selects `grant_gold`, `summon`, `smite` or `deny`.
- `QwenSpellcraftDemo.unity`: a spell selects an element and power through `cast_spell`.

Both scenes have a `CoreAILifetimeScope`, camera, light and controller. They use the scene scope's
**dedicated `QwenDemoSettings.asset`**. Editing an unrelated global settings asset does not change
that scene's assigned backend. The shipped preset selects local `Qwen3.5-0.8B-Q4_K_M.gguf`;
the same controllers also accept an OpenAI-compatible HTTP backend. The LLM integration is
compiled with `COREAI_LLM`.

## Run with HTTP

1. Select the scene's `CoreAILifetimeScope` and inspect its assigned settings asset. Duplicate
   that asset for your host if you need a separate configuration, then assign the duplicate.
2. Select HTTP API and configure the base URL plus a tool-capable provider model id. For the
   OpenRouter route, use `https://openrouter.ai/api/v1` and the model chosen for your run.
   Supply credentials through your host's runtime configuration; keep secrets out of published
   settings assets. A server-managed gateway may choose the model itself.
3. Open exactly one scene and enter Play Mode. Once the scope is built, valid HTTP configuration
   enables the controls without waiting for a native model host, local port or `/v1/models` probe.
4. Send a wish or spell and inspect the actual tool result on the HUD. HTTP readiness validates
   configuration and scope availability; the real turn still owns provider authentication,
   connectivity and model errors.
5. In Spellcraft, **Determinism x5** repeats the real tool path with temperature zero and displays
   its decision distribution. Provider behavior can still vary.

Both agents use `AgentMode.ToolsOnly` and disable chat history. Spellcraft requires the specific
`cast_spell` tool; Genie requires any of its four allowed tools. A successful turn must execute
exactly one expected tool successfully. Zero calls, multiple calls, an unexpected tool or a failed
tool is rejected. Assistant text alone cannot count as an action.

## Optional desktop local preset

For the shipped native preset, install `ai.undream.llm` and download
`Qwen3.5-0.8B-Q4_K_M.gguf` through LLMUnity Model Manager. The scene deliberately contains no placed
`LLM` or `LLMAgent`: CoreAI creates the host inactive, configures it, then starts it. Readiness waits
for native startup and checks the local `/v1/chat/completions` route; it does not require
`/v1/models`. A native startup failure or readiness timeout keeps the controls disabled and shows
the reason. HTTP configuration bypasses that entire native readiness path.

## WebGL

LlamaLib has no browser build, so selecting the local preset in WebGL immediately reports the
local-model limitation and leaves the controls disabled. The camera, scene and VFX rig can still
load. Select HTTP on the scene's dedicated settings asset to use a browser-compatible backend.
Configured HTTP scopes do not require a native host in WebGL; verify the deployed endpoint's CORS,
headers and streaming behavior in the built player. Configuration readiness alone is not a
successful browser request.

## Gameplay checks

`QwenDemoScenesEditModeTests` covers scene/tool contracts and layout;
`QwenDemoSafetyPlayModeTests` covers exactly-once side-effect guards without a model.
`QwenDemoReadinessBackendEditModeTests` covers the HTTP/native/offline readiness split, including
HTTP on WebGL and server-managed model selection. These fixtures do not establish live provider
or visual acceptance. Exercise each scene with its selected backend, verify its actual tool
arguments and effect, then inspect the HUD and controls interactively.

Earlier local-model captures are retained as historical examples:

- [Genie native tool call](Screenshots/QwenGenieToolsOnlyVerified.png)
- [Spellcraft native tool call](Screenshots/QwenSpellcraftToolsOnlyVerified.png)
