# 🚀 CoreAI — Quick Start

Minimal path from an empty project to a working **LLM + orchestrator + Lua + Chat UI** inside the Editor.

> Looking for a full 10-minute walkthrough (LM Studio → Unity → first command)? See [QUICK_START_FULL.md](QUICK_START_FULL.md).

---

## 1. Requirements

| Item | Value |
|------|-------|
| **Unity** | As pinned in `ProjectSettings/ProjectVersion.txt` (currently **6000.4.x**). |
| **Disk / network** | For local LLMUnity: space for a GGUF model and time for the first download. |
| **Optional** | LM Studio, OpenAI, or any OpenAI-compatible HTTP server. |

---

## 2. Install the packages

Via Unity Package Manager → **Add package from Git URL** (in this order):

```
https://github.com/NeoXider/CoreAI.git?path=Assets/CoreAI          # portable core
https://github.com/NeoXider/CoreAI.git?path=Assets/CoreAiUnity     # Unity layer
```

NuGet DLLs, VContainer/MoonSharp/UniTask/MessagePipe/LLMUnity Git dependencies — see the root [README §Quick Start](../../../README.md#-quick-start).

---

## 3. Create a scene (one click)

You have **two** options:

### 3a. Classic AI-only scene

```
CoreAI → Create Scene Setup
```

Creates `CoreAILifetimeScope` with every required asset (`CoreAISettings`, `GameLogSettings`, `AgentPromptsManifest`, `CoreAiPrefabRegistry`, `LlmRoutingManifest`, `AiPermissions`). If the backend is `LlmUnity`, it also adds `LLM` and `LLMAgent` GameObjects.

### 3b. Chat-ready demo scene (recommended for first run)

```
CoreAI → Setup → Create Chat Demo Scene
```

Produces `Assets/CoreAiUnity/Scenes/CoreAiChatDemo.unity` with `CoreAILifetimeScope`, a `UIDocument` + `CoreAiChatPanel` and a prepared `CoreAiChatConfig_Demo.asset`. Press **Play** and chat.

---

## 4. Connect an LLM backend

Open `Resources/CoreAISettings` (or create one via **Create → CoreAI → Core AI Settings**) and pick a backend:

### A. Local — LLMUnity (good for offline tests & shipping)

1. **Backend Type** → `LlmUnity` (or `Auto`).
2. Select a GGUF model on the `LlmManager` object (for example **Qwen3.5-4B**). If the list is empty, download a model via the LLMUnity Model Manager.
3. On play, `CoreAILifetimeScope` discovers `LLMAgent` automatically.

> LLMUnity is installed automatically as a Unity Package Manager dependency. Plugin reference: [LLMUnity on GitHub](https://github.com/undreamai/LLMUnity).

### B. HTTP API — LM Studio / OpenAI / vLLM / Ollama

1. **Backend Type** → `OpenAiHttp`.
2. **Api Base Url** → e.g. `http://localhost:1234/v1` (LM Studio).
3. **Model** → the model name your server exposes (e.g. `qwen3.5-4b`).
4. **Api Key** → only required for OpenAI itself.

> 💡 Not sure which to pick? Install [LM Studio](https://lmstudio.ai), load **Qwen3.5-4B** or **Gemma 4 26B**, start the local server, and choose HTTP API in Unity. That's the fastest path to a working setup.

---

## 5. Global toggles

`CoreAISettings.asset` exposes the project-wide defaults:

| Setting | Purpose |
|---------|---------|
| `Backend Type` | `LlmUnity` / `OpenAiHttp` / `Auto` / `Stub` |
| `Model` / `Api Base Url` / `Api Key` | Backend credentials |
| `Temperature` / `Max Tokens` / `Request Timeout` | Generation controls |
| `Enable Streaming` | Global streaming default (new in 0.20) |
| `Universal System Prompt Prefix` | Prepended to every agent's system prompt |

Streaming priority is **UI toggle → per-agent `AgentBuilder.WithStreaming(bool)` → global**. Details: [STREAMING_ARCHITECTURE.md](STREAMING_ARCHITECTURE.md).

---

## 6. Build your first agent

```csharp
var storyteller = new AgentBuilder("Storyteller")
    .WithSystemPrompt("You are a campfire storyteller.")
    .WithMemory()
    .WithChatHistory()
    .WithMode(AgentMode.ChatOnly)
    .WithStreaming(true)
    .Build();

storyteller.ApplyToPolicy(CoreAIAgent.Policy);
await storyteller.Ask("Tell me about the ruins to the east.");
```

Full reference with ready-made recipes: [AGENT_BUILDER](../../CoreAI/Docs/AGENT_BUILDER.md).

### One-line alternative — `CoreAi` singleton

Если у вас на сцене уже есть `CoreAILifetimeScope` (шаг 3), можно обойтись без `AgentBuilder` и DI — все виды вызова доступны через статический фасад `CoreAi`:

```csharp
// Синхронный:
string reply = await CoreAi.AskAsync("Привет!", roleId: "PlayerChat");

// Стриминг (живой UI):
await foreach (string chunk in CoreAi.StreamAsync("Расскажи анекдот", "PlayerChat"))
    label.text += chunk;

// Полный оркестратор-пайплайн (history + authority + publish command):
string json = await CoreAi.OrchestrateAsync(
    new AiTaskRequest { RoleId = "Creator", Hint = "spawn JSON" });
```

Полный справочник — [COREAI_SINGLETON_API](COREAI_SINGLETON_API.md).

### Sanity check in Play mode

1. Press **Play**.
2. The console prints `VContainer + MessagePipe ... ready.`.
3. Call `myAgent.Ask("Hello");` from any script — you should see a request/response round-trip in the console.

> If `ILlmClient` cannot find an `LLMAgent` and HTTP is disabled, `StubLlmClient` takes over and returns canned replies. Configure §4A or §4B for real model output.

---

## 7. Tests

| Suite | How to run | What it covers |
|-------|------------|----------------|
| **CoreAI.Tests** (EditMode) | Window → General → Test Runner → EditMode | 83 tests: prompts, Lua sandbox, streaming filter, rate limiter, tool-calling duplicates |
| **CoreAI.PlayModeTests** | Test Runner → PlayMode | End-to-end with a real LLM. Uses `COREAI_OPENAI_TEST_*` env vars (see [LLMUNITY_SETUP_AND_MODELS §7](LLMUNITY_SETUP_AND_MODELS.md)). |

---

## 8. Next steps

| Doc | What it covers |
|-----|----------------|
| [DOCS_INDEX](DOCS_INDEX.md) | Full documentation map |
| [COREAI_SINGLETON_API](COREAI_SINGLETON_API.md) | One-line `CoreAi.AskAsync` / `StreamAsync` / `OrchestrateAsync` |
| [README_CHAT](../Runtime/Source/Features/Chat/README_CHAT.md) | Customising `CoreAiChatPanel`, styles, events |
| [STREAMING_ARCHITECTURE](STREAMING_ARCHITECTURE.md) | How streaming works end-to-end |
| [DEVELOPER_GUIDE](DEVELOPER_GUIDE.md) | Architecture, data flow, PR checklist |
| [AI_AGENT_ROLES](AI_AGENT_ROLES.md) | Roles, model selection strategy |
| [TROUBLESHOOTING](TROUBLESHOOTING.md) | Model silent, Lua crash, memory not written |

---

**Version:** 0.21.7 — `CoreAi` singleton API, orchestrator streaming, chat collapse/FAB on small screens, docs for beginners & pros.

**First script from scratch?** Read [COREAI_SINGLETON_API](COREAI_SINGLETON_API.md) — 3 steps + copy-paste `AskAsync` / `StreamAsync`.
