# CoreAI Unity Documentation Index

This is the main index for the Unity package. Use it when you are setting up the
package, debugging a scene, adding tools, or trying to understand how the runtime
is wired.

Package manifests:

- Unity layer: [`com.nexoider.coreaiunity`](../package.json)
- Portable core: [`com.nexoider.coreai`](../../CoreAI/package.json)
- Repository entry point: [Docs/README.md](../../../Docs/README.md)

## Read By Goal

| Goal | Start Here | Then Read |
|---|---|---|
| Get a working scene | [QUICK_START.md](QUICK_START.md) | [COREAI_SETTINGS.md](COREAI_SETTINGS.md), [README_CHAT](../Runtime/Source/Features/Chat/README_CHAT.md) |
| Use CoreAI from code | [COREAI_SINGLETON_API.md](COREAI_SINGLETON_API.md) | [AGENT_BUILDER](../../CoreAI/Docs/AGENT_BUILDER.md) |
| Add tools or agents | [TOOL_CALL_SPEC.md](TOOL_CALL_SPEC.md) | [TOOL_CALLING_BEST_PRACTICES](../../CoreAI/Docs/TOOL_CALLING_BEST_PRACTICES.md), [MEAI_TOOL_CALLING](../../CoreAI/Docs/MEAI_TOOL_CALLING.md) |
| Debug streaming or WebGL | [STREAMING_ARCHITECTURE.md](STREAMING_ARCHITECTURE.md) | [HTTP_TRANSPORT_SPEC.md](HTTP_TRANSPORT_SPEC.md), [STREAMING_WEBGL_TODO.md](STREAMING_WEBGL_TODO.md) |
| Understand architecture | [ARCHITECTURE.md](ARCHITECTURE.md) | [DEVELOPER_GUIDE.md](DEVELOPER_GUIDE.md), [DGF_SPEC.md](DGF_SPEC.md) |
| Work with memory | [MemorySystem.md](MemorySystem.md) | [MEMORY_STORE_CUSTOM_BACKENDS.md](MEMORY_STORE_CUSTOM_BACKENDS.md) |
| Expose Lua or world commands | [WORLD_COMMANDS.md](WORLD_COMMANDS.md) | [LUA_GAME_API](../../CoreAI/Docs/LUA_GAME_API.md), [LUA_BEST_PRACTICES_RU](../../CoreAI/Docs/LUA_BEST_PRACTICES_RU.md), [LUA_SANDBOX_SECURITY](../../CoreAI/Docs/LUA_SANDBOX_SECURITY.md) |
| Run or extend tests | [../Tests/PlayMode/README.md](../Tests/PlayMode/README.md) | Test-specific docs listed below |

## First Run

| Document | Purpose |
|---|---|
| [QUICK_START.md](QUICK_START.md) | Minimal path from install to Play Mode chat. |
| [QUICK_START_EN.md](QUICK_START_EN.md) | Compact English quick start (agent in 10 minutes). |
| [QUICK_START_FULL.md](QUICK_START_FULL.md) | Longer walkthrough with LM Studio and first command. |
| [EXAMPLES.md](EXAMPLES.md) | Copy-paste gameplay examples: NPCs, quests, narration, tools. |
| [COREAI_SETTINGS.md](COREAI_SETTINGS.md) | Inspector settings, routing modes, models, timeouts, streaming. |
| [LLMUNITY_SETUP_AND_MODELS.md](LLMUNITY_SETUP_AND_MODELS.md) | Local GGUF setup, LLMUnity, OpenAI-compatible HTTP backends. |
| [TROUBLESHOOTING.md](TROUBLESHOOTING.md) | Common setup, backend, WebGL, tool-call, and logging problems. |

## Chat And Streaming

| Document | Purpose |
|---|---|
| [COREAI_SINGLETON_API.md](COREAI_SINGLETON_API.md) | `CoreAi.AskAsync`, streaming, orchestration, and helper access. |
| [README_CHAT](../Runtime/Source/Features/Chat/README_CHAT.md) | Drop-in chat panel, config assets, hotkeys, stop path, persisted session. |
| [STREAMING_ARCHITECTURE.md](STREAMING_ARCHITECTURE.md) | SSE, LLMUnity streaming, think-block filtering, cancellation, UI flow. |
| [STREAMING_WEBGL_TODO.md](STREAMING_WEBGL_TODO.md) | Current WebGL SSE status, fetch bridge, fallback behavior, verification checklist. |
| [HTTP_TRANSPORT_SPEC.md](HTTP_TRANSPORT_SPEC.md) | OpenAI-compatible transport contracts: `HttpClient`, `UnityWebRequest`, WebGL fetch. |
| [WEBGL_BUILD_TROUBLESHOOTING.md](WEBGL_BUILD_TROUBLESHOOTING.md) | WebGL player build issues, IL2CPP memory, package/settings file problems. |

## Tools, Memory, And Roles

| Document | Purpose |
|---|---|
| [TOOL_CALL_SPEC.md](TOOL_CALL_SPEC.md) | Built-in tools, schemas, examples, and tool-call patterns. |
| [TOOL_CALLING_BEST_PRACTICES](../../CoreAI/Docs/TOOL_CALLING_BEST_PRACTICES.md) | Naming, idempotency, result envelopes, duplicate calls, test checklist. |
| [CHAT_TOOL_CALLING.md](CHAT_TOOL_CALLING.md) | Worked merchant/inventory example for chat tool calling. |
| [JSON_COMMAND_FORMAT.md](JSON_COMMAND_FORMAT.md) | JSON command format reference for role-driven commands. |
| [MemorySystem.md](MemorySystem.md) | Agent memory, chat history, memory tools, and per-role config. |
| [MEMORY_STORE_CUSTOM_BACKENDS.md](MEMORY_STORE_CUSTOM_BACKENDS.md) | Custom `IAgentMemoryStore` implementations: local, cloud, composite. |
| [AI_AGENT_ROLES.md](AI_AGENT_ROLES.md) | Built-in roles and model-selection strategy. |
| [WORLD_COMMANDS.md](WORLD_COMMANDS.md) | Sandboxed Lua/world commands for spawn, move, animation, audio, and scene control. |

## Portable Core Deep Dives

| Document | Purpose |
|---|---|
| [CoreAI portable docs](../../CoreAI/Docs/README.md) | Index for host-agnostic CoreAI documentation. |
| [AGENT_BUILDER](../../CoreAI/Docs/AGENT_BUILDER.md) | Fluent agent configuration, tools, modes, memory, and skills. |
| [ENGINE_AGNOSTIC_TOOLS](../../CoreAI/Docs/ENGINE_AGNOSTIC_TOOLS.md) | How to keep tool logic portable and free of Unity dependencies. |
| [LLM_ROUTING](../../CoreAI/Docs/LLM_ROUTING.md) | Portable routing modes, policy hooks, usage sinks, and timeouts. |
| [MEAI_TOOL_CALLING](../../CoreAI/Docs/MEAI_TOOL_CALLING.md) | MEAI pipeline from `ILlmTool` to `AIFunction` and forced tool modes. |
| [MEAI_TOKENS_FACT_VS_ESTIMATE](../../CoreAI/Docs/MEAI_TOKENS_FACT_VS_ESTIMATE.md) | Provider usage facts, client estimates, SSE usage, timeout boundaries. |
| [LUA_SANDBOX_SECURITY](../../CoreAI/Docs/LUA_SANDBOX_SECURITY.md) | Lua sandbox boundary, escape tests, binding rules, host checklist. |
| [LUA_GAME_API](../../CoreAI/Docs/LUA_GAME_API.md) | Capabilities, mods, world API, Full tier, `execute_lua` / `manage_mods`. |
| [LUA_BEST_PRACTICES_RU](../../CoreAI/Docs/LUA_BEST_PRACTICES_RU.md) | Do's and don'ts: slots, extensions, MoonSharp, LLM context (RU). |
| [MOONSHARP_NATIVE_APIS_RU](../../CoreAI/Docs/MOONSHARP_NATIVE_APIS_RU.md) | Native MoonSharp vs custom CoreAI code (RU). |

## Architecture

| Document | Purpose |
|---|---|
| [ARCHITECTURE.md](ARCHITECTURE.md) | Clean architecture layers, MessagePipe, LLM modes, source-comment rules. |
| [DEVELOPER_GUIDE.md](DEVELOPER_GUIDE.md) | Code map, request pipeline, extension points, PR checklist. |
| [DGF_SPEC.md](DGF_SPEC.md) | Normative spec for DI, threading, authority, and main-thread rules. |
| [COMMAND_FLOW_DIAGRAM.md](COMMAND_FLOW_DIAGRAM.md) | Diagram of how a command travels through the system. |
| [MULTIPLAYER_AI.md](MULTIPLAYER_AI.md) | Multiplayer AI authority and replication notes. |
| [SCRIPTABLE_OBJECTS.md](SCRIPTABLE_OBJECTS.md) | ScriptableObject assets used by the package and their roles. |
| [GAME_CONFIG_GUIDE.md](GAME_CONFIG_GUIDE.md) | Letting AI change game parameters through GameConfig assets. |
| [GameTemplateGuides/INDEX.md](GameTemplateGuides/INDEX.md) | Per-title guide index. |

## Tests

| Document Or Test | Scope |
|---|---|
| [../Tests/PlayMode/README.md](../Tests/PlayMode/README.md) | Play Mode test layout and backend requirements. |
| [../Tests/PlayMode/Scenarios/CraftingMemory_README.md](../Tests/PlayMode/Scenarios/CraftingMemory_README.md) | Crafting memory workflow scenario. |
| [TESTING_TOOL_CALLING.md](TESTING_TOOL_CALLING.md) | How to run and extend tool-calling tests. |
| `ThinkBlockStreamFilterEditModeTests` | Streaming `<think>` filter and split-tag cases. |
| `SecureLuaSandboxEditModeTests` | Lua sandbox escape coverage. |
| `SmartToolCallingChatClientEditModeTests` | Duplicate detection, missing tools, exceptions, retry behavior. |
| `InGameLlmChatServiceEditModeTests` | Sliding-window rate limiter. |
| `CoreAiChatServiceEditModeTests` | Streaming enablement hierarchy. |
| `LuaExecutionPipelineEditModeTests` | Lua success/failure, repair loop, role isolation. |

## Example Game And Media

| Document | Purpose |
|---|---|
| [Assets/_exampleGame/README.md](../../_exampleGame/README.md) | RogueliteArena concept, stack, and folder layout. |
| [UNITY_SETUP.md](../../_exampleGame/Docs/UNITY_SETUP.md) | Step-by-step example scene setup. |
| [ARENA_ARCHITECTURE_AND_AI.md](../../_exampleGame/Docs/ARENA_ARCHITECTURE_AND_AI.md) | Arena architecture for multiplayer and AI roles. |
| [ROGUELITE_PLAYBOOK.md](../../_exampleGame/Docs/ROGUELITE_PLAYBOOK.md) | Run loop, progression, and gameplay notes. |
| [DEMO_RECORDING_GUIDE.md](DEMO_RECORDING_GUIDE.md) | Video/GIF capture scenarios and demo runner notes. |

## Roadmap

Live backlog and recently closed documentation debt: [../../../TODO.md](../../../TODO.md).

## Russian Notes

| Document | Purpose |
|---|---|
| [DOCS_INDEX_RU.md](DOCS_INDEX_RU.md) | Russian version of this index. |
| [KNOWN_ISSUES_RU.md](KNOWN_ISSUES_RU.md) | Accepted warning debt and known project-level issues. |
| [RELEASE_CHECKLIST_RU.md](RELEASE_CHECKLIST_RU.md) | Pre-commit/pre-release checklist for both packages. |
