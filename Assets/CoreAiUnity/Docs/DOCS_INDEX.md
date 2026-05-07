# 📚 CoreAI Documentation — Index

Pick the track that matches your current goal. Every link lands on a self-contained guide; you don't need to read them in order.

> **Manifests:** [`com.nexoider.coreai`](../../CoreAI/package.json) (`1.7.5`) · [`com.nexoider.coreaiunity`](../package.json) (`1.7.5`)
> **Repo README:** [../../../README.md](../../../README.md) · **Changelog:** [../CHANGELOG.md](../CHANGELOG.md)

---

## 🟢 Beginner — get something on screen

Install → open scene → connect LLM → press Play.

| # | Document | You will learn |
|---|----------|----------------|
| 1 | [QUICK_START.md](QUICK_START.md) | Install, open `_mainCoreAI.unity`, wire up a backend |
| 1b | [QUICK_START_FULL.md](QUICK_START_FULL.md) | Full 10-min walkthrough: LM Studio → Unity → first command |
| 1a | [COREAI_SINGLETON_API.md](COREAI_SINGLETON_API.md) | 🎯 **One class for everyone** — `CoreAi.AskAsync` / `Stream` / `TryGet*` (beginner + pro guide) |
| 1c | [README_CHAT](../Runtime/Source/Features/Chat/README_CHAT.md) | 💬 Drop-in chat panel + demo scene in one click |
| 2 | [AGENT_BUILDER](../../CoreAI/Docs/AGENT_BUILDER.md) | 🏗️ Build an NPC in 3 lines, agent modes, recipes |
| 3 | [COREAI_SETTINGS](COREAI_SETTINGS.md) | ⚙️ LLM modes, routing profiles, models, timeout, streaming toggle |
| 4 | [CHAT_TOOL_CALLING](CHAT_TOOL_CALLING.md) | 🛒 Worked example: merchant NPC with inventory |
| 4b | [EXAMPLES](EXAMPLES.md) | 📖 Enemies, crafting, auto-repair, merchant, guard |

---

## 💬 Chat & Streaming — new in 0.20

| Document | Topic |
|----------|-------|
| [COREAI_SINGLETON_API](COREAI_SINGLETON_API.md) | 🎯 One-line API: `CoreAi.AskAsync` / `StreamAsync` / `OrchestrateAsync` |
| [README_CHAT](../Runtime/Source/Features/Chat/README_CHAT.md) | `CoreAiChatPanel`, `CoreAiChatConfig`, FAB/collapse, **hotkeys (0.25.3+)**, **persisted session (0.25.4+)**, **`SubmitMessageFromExternalAsync` (0.25.5+)**, reliable **Stop** path (0.25.6+), streaming hierarchy; **default window ~650×910**, **flush-right scrollbar**, optional **`coreai-long-request-hint`** (long-turn status) |
| [STREAMING_ARCHITECTURE](STREAMING_ARCHITECTURE.md) | SSE / LLMUnity → `ThinkBlockStreamFilter` → UI; orchestrator streaming; cancellation; known limits |
| [STREAMING_WEBGL_TODO](STREAMING_WEBGL_TODO.md) | WebGL: **`UnityWebRequest`** vs optional **`WebGlNativeStreaming`** (`fetch` jslib); history + verification checklist |
| [WEBGL_BUILD_TROUBLESHOOTING](WEBGL_BUILD_TROUBLESHOOTING.md) | WebGL **Player** build: LLVM **OOM** during IL2CPP, `IOException` under `ProjectSettings/Packages`, StreamingAssets preprocess log |
| [HTTP_TRANSPORT_SPEC](HTTP_TRANSPORT_SPEC.md) | **`IOpenAiHttpTransport`** — `HttpClient` vs `UnityWebRequest` (WebGL); simulated streaming |

---

## 🟡 Intermediate — tools, memory, roles

| # | Document | You will learn |
|---|----------|----------------|
| 5 | [TOOL_CALL_SPEC](TOOL_CALL_SPEC.md) | 🔧 All built-in tools: memory, Lua, world, inventory, scene, camera |
| 5b | [JSON_COMMAND_FORMAT](JSON_COMMAND_FORMAT.md) | 📋 JSON command format per role (reference) |
| 6 | [MemorySystem](MemorySystem.md) | 🧠 `MemoryTool` vs `ChatHistory`, per-role config |
| 6a | [MEMORY_STORE_CUSTOM_BACKENDS](MEMORY_STORE_CUSTOM_BACKENDS.md) | 💾 `IAgentMemoryStore`: PlayerPrefs, cloud save, composite (offline-first) |
| 7 | [AI_AGENT_ROLES](AI_AGENT_ROLES.md) | 🤖 5 built-in roles, model selection strategy |
| 8 | [WORLD_COMMANDS](WORLD_COMMANDS.md) | 🌍 Spawn/move/scene control from sandboxed Lua |
| 9 | [LLMUNITY_SETUP_AND_MODELS](LLMUNITY_SETUP_AND_MODELS.md) | 📦 LLMUnity, GGUF, OpenAI HTTP, Lua pipeline |
| 9b | [TROUBLESHOOTING](TROUBLESHOOTING.md) | 🔧 Model silent, Lua crashed, memory not written, **PlayMode HTTP 500 / LM Studio (0.25.7+)**; **CoreAI → Delete All Persistent Saves...** clears `persistentDataPath/CoreAI` |

---

## 🔴 Architecture — how it works inside

DI, threading, spec, pipelines.

| # | Document | You will learn |
|---|----------|----------------|
| 10 | [DEVELOPER_GUIDE](DEVELOPER_GUIDE.md) | 🗺️ Code map, LLM → commands pipeline, PR checklist; **child scope + `GlobalMessagePipe` for LLM subscribers** |
| 10a | [ARCHITECTURE](ARCHITECTURE.md) | Clean architecture layers, LLM modes, MessagePipe; **§ child LifetimeScope vs `GlobalMessagePipe`**; **§ source comments (English, `TODO`/`HACK`)** |
| 10c | [CODE_AUDIT_AND_FOLLOWUPS](CODE_AUDIT_AND_FOLLOWUPS.md) | Comment/language backlog and logic notes from manual audits |
| 10b | [COMMAND_FLOW_DIAGRAM](COMMAND_FLOW_DIAGRAM.md) | 🗺️ Diagram: how a command travels through the system |
| 11 | [DGF_SPEC](DGF_SPEC.md) | 📐 Normative spec: DI, threads, authority, §9.4 main-thread rules |
| 12 | [MEAI_TOOL_CALLING](../../CoreAI/Docs/MEAI_TOOL_CALLING.md) | 🛠️ MEAI pipeline: `ILlmTool` → `AIFunction` → `FunctionInvokingChatClient` |
| 12a | [LLM_ROUTING](../../CoreAI/Docs/LLM_ROUTING.md) | 🔀 Portable routing modes, policy hooks, usage sinks, **timeouts vs HTTP** |
| 12b | [MEAI_TOKENS_FACT_VS_ESTIMATE](../../CoreAI/Docs/MEAI_TOKENS_FACT_VS_ESTIMATE.md) | 📊 Provider `usage` vs pre-request estimates; SSE `stream_options`; HTTP vs orchestrator timeouts; tool diagnostics |
| 12c | [CoreAI/Docs/README](../../CoreAI/Docs/README.md) | 📑 Index of every guide under `Assets/CoreAI/Docs` |
| 13 | [MULTIPLAYER_AI](MULTIPLAYER_AI.md) | 🌐 Multiplayer + AI: host authority, replication |
| 14 | [GameTemplateGuides/INDEX](GameTemplateGuides/INDEX.md) | 📚 Per-title guides: networking, orchestration, roles |

---

## 🧪 Tests — documentation

| Document | Tests | Scope |
|----------|-------|-------|
| [CraftingMemory_README](../Tests/PlayMode/Scenarios/CraftingMemory_README.md) | 5 | 🤖 Crafting workflow: Creator → CoreMechanic → Programmer |
| [Play Mode layout](../Tests/PlayMode/README.md) | — | **FastNoLlm** vs **LlmVerification** vs **Scenarios** (+ **Shared**, **LlmInfra**) |
| `ThinkBlockStreamFilterEditModeTests` | 24 | Streaming `<think>` filter, including split-tag cases |
| `SecureLuaSandboxEditModeTests` | — | Sandbox escape: `io`/`os`/`debug`/`load`/`loadfile`/`dofile`, step/timeout guard |
| `SmartToolCallingChatClientEditModeTests` | — | Duplicate detection, `AllowDuplicates`, missing tools, exceptions |
| `InGameLlmChatServiceEditModeTests` | — | Sliding-window rate limiter |
| `CoreAiChatServiceEditModeTests` | — | Streaming enablement hierarchy (UI → per-agent → global) |
| `LuaExecutionPipelineEditModeTests` | 8 | Lua sandbox: exec success/failure, repair loop, role isolation |
| `MultiAgentCraftingWorkflowPlayModeTests` | 2 | Full workflow over a live LLM |
| `CraftingMemoryViaLlmUnityPlayModeTests` | 1 | Local GGUF: 4 crafts + determinism |
| `CraftingMemoryViaOpenAiPlayModeTests` | 2 | HTTP: 4 crafts + 2 quick crafts |
| `CoreAiChatPanelStopPlayModeTests` | 1 | `StopAgent()` cancels active request CTS, clears sending/streaming |
| `AgentMemoryWithRealModelPlayModeTests` | 1 | Real LLM memory write + recall (**Ignore** on recall 5xx after retries, 0.25.7+) |

---

## 🎮 Example game (`Assets/_exampleGame`)

| Document | Purpose |
|----------|---------|
| [UNITY_SETUP](../../_exampleGame/Docs/UNITY_SETUP.md) | Step-by-step RogueliteArena scene setup |
| [ARENA_ARCHITECTURE_AND_AI](../../_exampleGame/Docs/ARENA_ARCHITECTURE_AND_AI.md) | Arena architecture for multiplayer + AI roles |
| [README](../../_exampleGame/README.md) | Concept, stack, folder layout |
| [ROGUELITE_PLAYBOOK](../../_exampleGame/Docs/ROGUELITE_PLAYBOOK.md) | Gameplay: run loop, meta progression |

---

## 🎬 Demo & media

| Document | Purpose |
|----------|---------|
| [DEMO_RECORDING_GUIDE](DEMO_RECORDING_GUIDE.md) | Video/GIF capture scenarios, tools, `DemoRunner` script |

---

## 🗺️ Roadmap

Live plan and recently-found gaps: [../../../TODO.md](../../../TODO.md).
