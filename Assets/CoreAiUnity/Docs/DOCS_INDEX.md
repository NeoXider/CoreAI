# CoreAI Unity Documentation Index

This is the main index for the Unity package. Use it when you are setting up the
package, debugging a scene, adding tools, or trying to understand how the runtime
is wired.

Package manifests:

- Unity layer: [`com.neoxider.coreaiunity`](../package.json)
- Portable core: [`com.neoxider.coreai`](../../CoreAI/package.json)
- Repository entry point: [Docs/README.md](../../../Docs/README.md)

## Read By Goal

| Goal | Start Here | Then Read |
|---|---|---|
| Get a working scene | [QUICK_START.md](QUICK_START.md) | [COREAI_SETTINGS.md](COREAI_SETTINGS.md), [README_CHAT](../Runtime/Source/Features/Chat/README_CHAT.md) |
| Configure or switch LLM endpoints | [RUNTIME_BACKEND_SWITCHING.md](RUNTIME_BACKEND_SWITCHING.md) | [LLM_ROUTING](../../CoreAI/Docs/LLM_ROUTING.md), [COREAI_SETTINGS.md](COREAI_SETTINGS.md) |
| Use CoreAI from code | [COREAI_SINGLETON_API.md](COREAI_SINGLETON_API.md) | [AGENT_BUILDER](../../CoreAI/Docs/AGENT_BUILDER.md) |
| Add tools or agents | [TOOL_CALL_SPEC.md](TOOL_CALL_SPEC.md) | [TOOL_CALLING_BEST_PRACTICES](../../CoreAI/Docs/TOOL_CALLING_BEST_PRACTICES.md), [MEAI_TOOL_CALLING](../../CoreAI/Docs/MEAI_TOOL_CALLING.md) |
| Debug streaming or WebGL | [STREAMING_ARCHITECTURE.md](STREAMING_ARCHITECTURE.md) | [HTTP_TRANSPORT_SPEC.md](HTTP_TRANSPORT_SPEC.md), [STREAMING_WEBGL_TODO.md](STREAMING_WEBGL_TODO.md) |
| Understand architecture | [ARCHITECTURE.md](ARCHITECTURE.md) | [DEVELOPER_GUIDE.md](DEVELOPER_GUIDE.md), [DGF_SPEC.md](DGF_SPEC.md) |
| Work with memory | [MemorySystem.md](MemorySystem.md) | [MEMORY_STORE_CUSTOM_BACKENDS.md](MEMORY_STORE_CUSTOM_BACKENDS.md) |
| Expose Lua or world commands | [WORLD_COMMANDS.md](WORLD_COMMANDS.md) | [FIRST_MOD](../../CoreAI/Docs/FIRST_MOD.md), [LUA_GAME_API](../../CoreAI/Docs/LUA_GAME_API.md), [LUA_BEST_PRACTICES](../../CoreAI/Docs/LUA_BEST_PRACTICES.md), [LUA_SANDBOX_SECURITY](../../CoreAI/Docs/LUA_SANDBOX_SECURITY.md) |
| Run or extend tests | [../Tests/README.md](../Tests/README.md) | Test-specific docs listed below |
| Ship to players / budget costs | [SHIPPING_PLAYER_MACHINES.md](SHIPPING_PLAYER_MACHINES.md) | [CLOUD_COST_BUDGETING.md](CLOUD_COST_BUDGETING.md), [RELEASE_CHECKLIST.md](RELEASE_CHECKLIST.md) |

## First Run

| Document | Purpose |
|---|---|
| [QUICK_START.md](QUICK_START.md) | Minimal path from install to Play Mode chat. |
| [QUICK_START_EN.md](QUICK_START_EN.md) | Compact English quick start (agent in 10 minutes). |
| [QUICK_START_FULL.md](QUICK_START_FULL.md) | Longer walkthrough with LM Studio and first command. |
| [EXAMPLES.md](EXAMPLES.md) | Copy-paste gameplay examples: NPCs, quests, narration, tools. |
| [COREAI_SETTINGS.md](COREAI_SETTINGS.md) | Inspector settings, routing modes, models, timeouts, streaming. |
| [RUNTIME_BACKEND_SWITCHING.md](RUNTIME_BACKEND_SWITCHING.md) | Runtime multi-endpoint registry, provider switching, Hub endpoint editor, and agent assignments. |
| [LLMUNITY_SETUP_AND_MODELS.md](LLMUNITY_SETUP_AND_MODELS.md) | Local GGUF setup, LLMUnity, OpenAI-compatible HTTP backends. |
| [SUBSCRIPTION_BRIDGE.md](SUBSCRIPTION_BRIDGE.md) | Bring your own subscription: power the in-game AI from a Claude Code / Codex CLI via a local OpenAI-compatible bridge (dev/testing). |
| [OPTIONAL_MODULES.md](OPTIONAL_MODULES.md) | Enable/disable Lua (Lua-CSharp) & LLMUnity via the `CoreAI ▸ Setup ▸ Modules` editor tool; defines and CI parity. |
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
| [TOOL_AUTHORING_GUIDE.md](TOOL_AUTHORING_GUIDE.md) | Writing your own tool: schema, validation, result envelope, registration. |
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
| [LLM_ROUTING](../../CoreAI/Docs/LLM_ROUTING.md) | Portable runtime endpoint registry, profiles, role/request routing, readiness, and fallback policy. |
| [MEAI_TOOL_CALLING](../../CoreAI/Docs/MEAI_TOOL_CALLING.md) | MEAI pipeline from `ILlmTool` to `AIFunction` and forced tool modes. |
| [MEAI_TOKENS_FACT_VS_ESTIMATE](../../CoreAI/Docs/MEAI_TOKENS_FACT_VS_ESTIMATE.md) | Provider usage facts, client estimates, SSE usage, timeout boundaries. |
| [LUA_SANDBOX_SECURITY](../../CoreAI/Docs/LUA_SANDBOX_SECURITY.md) | Lua sandbox boundary, escape tests, binding rules, host checklist. |
| [FIRST_MOD](../../CoreAI/Docs/FIRST_MOD.md) | Your first Lua mod in 5 minutes: load via agent/C#/TextAsset, persistence, sharing. |
| [RBX_API](../../CoreAI/Docs/RBX_API.md) | The Roblox-style API a mod builds with: `Instance.new`, datatypes, services, `BasePart.Material` / `Part.Color`, saving and loading a world, bundled samples. |
| [WORLD_PACKAGE](../../../Docs/CoreAIMods/WORLD_PACKAGE.md) | The `.world` package format, validation limits, manual slots vs. the autosave ring, the confirm/reject load flow, and runtime session replacement. |
| [PROCEDURAL_MATERIALS](../../CoreAIMods/Runtime/RbxApi/Unity/PROCEDURAL_MATERIALS.md) · [TEXTURE_MATERIALS](../../CoreAIMods/Runtime/RbxApi/Unity/TEXTURE_MATERIALS.md) | The `Enum.Material` render catalogs: all 45 items, six of them CC0 texture-backed, plus the magenta diagnostic fallback. |
| [LUA_GAME_API](../../CoreAI/Docs/LUA_GAME_API.md) | Capabilities, mods, world API, Full tier, `execute_lua` / `manage_mods`, persistence & sharing. |
| [LUA_BEST_PRACTICES](../../CoreAI/Docs/LUA_BEST_PRACTICES.md) | Do's and don'ts: slots, extensions, Lua-CSharp, LLM context. |
| [LUA_NATIVE_APIS](../../CoreAI/Docs/LUA_NATIVE_APIS.md) | Native Lua-CSharp vs custom CoreAI code. |

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
| [KNOWN_ISSUES.md](KNOWN_ISSUES.md) | Accepted warning debt and known project-level issues. |
| [RELEASE_CHECKLIST.md](RELEASE_CHECKLIST.md) | Pre-commit and pre-release checklist for the five-package graph. |
| [SHIPPING_PLAYER_MACHINES.md](SHIPPING_PLAYER_MACHINES.md) | What the player's machine needs per deployment mode: local GGUF, cloud, proxy, hybrid fallback. |
| [CLOUD_COST_BUDGETING.md](CLOUD_COST_BUDGETING.md) | Token anatomy of a turn, measuring real usage, spend caps, and a designer budget worksheet. |
| [AUDIT_LOG.md](AUDIT_LOG.md) | Immutable append-only audit log for tool calls, LLM requests, world mutations. |
| [DETERMINISM_AND_REPLAY.md](DETERMINISM_AND_REPLAY.md) | Effect-determinism contract, host-authoritative pattern, and the proposed audit-log replayer. |
| [CONTEXT_MANAGEMENT_ROADMAP.md](CONTEXT_MANAGEMENT_ROADMAP.md) | Planned work on token budget, rolling summaries, and compaction. |
| [AGENT_SESSION_INSPECTOR.md](AGENT_SESSION_INSPECTOR.md) | Inspecting a live agent session: turns, tool calls, memory reads. |
| [BACKLOG.md](BACKLOG.md) | Future work that does not block the current MVP gate. |
| [GameTemplateGuides/INDEX.md](GameTemplateGuides/INDEX.md) | Per-title guide index. |

## Tests

| Document Or Test | Scope |
|---|---|
| [../Tests/README.md](../Tests/README.md) | EditMode & PlayMode test requirements, layout, and backend needs. |
| [RUNNING_LIVE_TESTS.md](RUNNING_LIVE_TESTS.md) | Running the LIVE PlayMode suite against a real provider, and its local config file. |
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

Current closure log: [../../../TODO.md](../../../TODO.md). Future non-blocking work: [BACKLOG.md](BACKLOG.md).
