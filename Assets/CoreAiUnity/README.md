# 🎮 CoreAI Unity — where the LLM meets your scene

> **Imagine:** your NPC merchant checks inventory, haggles on price, applies a discount, and processes the purchase — all through real function calls to your game code, streamed token-by-token into a chat bubble. No scripted branches. No fake replies. **That's this package.**

This is the **Unity half** of CoreAI: MEAI clients, VContainer wiring, UI Toolkit chat, streaming filters, production error diagnostics, and Editor menus that spare you copy-paste.

| Package | Depends on | Status |
|---------|-----------|--------|
| `com.nexoider.coreaiunity` — [`package.json`](package.json) (`version`) | `com.nexoider.coreai` — [core `package.json`](../CoreAI/package.json) | ✅ Production |

**Changelog:** [CHANGELOG.md](CHANGELOG.md) (release notes; keep `version` in `package.json` in sync when you ship).

*Languages:* [English](../../README.md) · [Russian](../../README_RU.md)

> **First time?** Open [DOCS_INDEX](Docs/DOCS_INDEX.md) or go straight to [QUICK_START](Docs/QUICK_START.md). **Need a one-liner from code?** See [COREAI_SINGLETON_API](Docs/COREAI_SINGLETON_API.md).

---

## Contents

| | |
|---|--|
| **CoreAi** | Static `Ask` / `Stream` / orchestration — section below |
| **Agent** | `AgentBuilder`, tools, memory |
| **Chat** | One-click demo + `CoreAiChatPanel` |
| **Streaming** | HTTP / LLMUnity, filters, cancel |
| **Long chat context** | Token budget summaries, **`## Conversation Summary`**, optional LLM rollup, per-role compaction toggles · [MemorySystem](Docs/MemorySystem.md) · [CHANGELOG `1.5.3`+](CHANGELOG.md) |
| **LLM modes** | `LocalModel`, `ClientOwnedApi`, `ClientLimited`, `ServerManagedApi`, mixed routing |
| **Docs · Tests · Install** | End of this file |

---

## 🎯 `CoreAi` — one static entry point (new in 0.21)

Call the LLM from **any** script without DI boilerplate:

```csharp
using CoreAI;

string reply = await CoreAi.AskAsync("Hello!");
await foreach (var chunk in CoreAi.StreamAsync("Tell a story", "PlayerChat"))
    label.text += chunk;
if (CoreAi.TryGetChatService(out var chat)) { /* optional AI */ }
```

**Full guide** (beginner checklist + pro patterns): [COREAI_SINGLETON_API](Docs/COREAI_SINGLETON_API.md)

---

## Changelog

Release notes and **version bumps** live in **[CHANGELOG.md](CHANGELOG.md)** only (this file does not duplicate them). Bump **`version`** in [`package.json`](package.json) when you ship.

Current release line: **`1.5.11`** for **`com.nexoider.coreaiunity`** and **`com.nexoider.coreai`** — Play Mode assemblies split (**`FastNoLlm`** / **`LlmVerification`** / **`Scenarios`**, plus **`Shared`/`LlmInfra`**); index **[`Tests/PlayMode/README.md`](../Tests/PlayMode/README.md)**. **`1.5.10`:** responsive HTTP poll (**`UniTask.Yield`**), **CAIU001**, UPM **author**. Bump **`version`** in [`package.json`](package.json) when you ship.

---

## 🏗️ Build an agent

```csharp
var blacksmith = new AgentBuilder("Blacksmith")
    .WithSystemPrompt("You are a blacksmith. Sell weapons and remember purchases.")
    .WithTool(new InventoryLlmTool(myInventory))
    .WithMemory()
    .WithMode(AgentMode.ToolsAndChat)
    .WithStreaming(true)          // per-agent override (0.20+)
    .Build();

blacksmith.ApplyToPolicy(CoreAIAgent.Policy);
await blacksmith.Ask("Show me your swords");
```

**Long chats (1.5+):** turn on **`WithChatHistory()`** — older turns collapse into **`## Conversation Summary`** under a **`HistoryTokenBudget`**. Optionally enable **`Enable LLM Context Compaction`** on [**`CoreAISettings`**](Docs/COREAI_SETTINGS.md) for auxiliary summarizer calls (`__CoreAI_ContextCompaction` role); use **`AgentBuilder.WithLlmContextCompaction(false)`** for tool-heavy coding agents (**`Programmer`** defaults off in built-in roles).

Docs: [AGENT_BUILDER](../CoreAI/Docs/AGENT_BUILDER.md) · [TOOL_CALL_SPEC](Docs/TOOL_CALL_SPEC.md) · [MemorySystem](Docs/MemorySystem.md)

---

## 💬 Add chat UI in 1 click

```
CoreAI → Setup → Create Chat Demo Scene
```

Generates a ready scene with `CoreAILifetimeScope`, `CoreAiChatPanel`, panel settings and a `CoreAiChatConfig_Demo` asset. Just set your backend in `CoreAISettings` and press **Play**.

Manual setup, configuration hierarchy and styling: [README_CHAT](Runtime/Source/Features/Chat/README_CHAT.md).

---

## 🔧 Built-in tools

| Tool | Purpose |
|------|---------|
| 🧠 `MemoryTool` | Per-role JSON memory on disk |
| 📜 `LuaTool` | Sandboxed Lua execution (steps/timeout guard, `<think>` stripped) |
| 🎒 `InventoryLlmTool` | NPC inventory queries |
| ⚙️ `GameConfigTool` | Read/modify game configs |
| 🌍 `SceneLlmTool` | Hierarchy & transforms in PlayMode |
| 📸 `CameraLlmTool` | Base64 JPEG screenshots for Vision models |

Create your own — implement `ILlmTool` and register via `AgentBuilder.WithTool(...)`.

---

## 🌊 Streaming & cancellation

- `ClientOwnedApi`, `ClientLimited`, `ServerManagedApi`: `MeaiOpenAiChatClient` (portable Core, **`HttpClient`**) parses OpenAI-compatible SSE. Cancellation disposes the active request / stream.
- `LocalModel`: `LlmUnityMeaiChatClient` bridges LLMUnity's frame callbacks to `IAsyncEnumerable`.
- Both paths run through `ThinkBlockStreamFilter` — a state machine that removes `<think>…</think>` blocks even when tags are split across chunks.

**Priority:** UI toggle → `AgentMemoryPolicy.SetStreamingEnabled(role, bool)` → `AgentBuilder.WithStreaming(bool)` → `CoreAISettings.EnableStreaming` (default `true`).

Deep dive: [STREAMING_ARCHITECTURE](Docs/STREAMING_ARCHITECTURE.md).

---

## 📖 Documentation

| Level | Documents |
|-------|-----------|
| 🟢 Beginner | [QUICK_START](Docs/QUICK_START.md) · [QUICK_START_FULL](Docs/QUICK_START_FULL.md) · [COREAI_SINGLETON_API](Docs/COREAI_SINGLETON_API.md) · [AGENT_BUILDER](../CoreAI/Docs/AGENT_BUILDER.md) · [COREAI_SETTINGS](Docs/COREAI_SETTINGS.md) · [EXAMPLES](Docs/EXAMPLES.md) |
| 💬 Chat & streaming | [README_CHAT](Runtime/Source/Features/Chat/README_CHAT.md) · [STREAMING_ARCHITECTURE](Docs/STREAMING_ARCHITECTURE.md) · [ARCHITECTURE](Docs/ARCHITECTURE.md) (context budget / compaction) |
| 🟡 Intermediate | [TOOL_CALL_SPEC](Docs/TOOL_CALL_SPEC.md) · [MemorySystem](Docs/MemorySystem.md) · [AI_AGENT_ROLES](Docs/AI_AGENT_ROLES.md) · [WORLD_COMMANDS](Docs/WORLD_COMMANDS.md) · [TROUBLESHOOTING](Docs/TROUBLESHOOTING.md) |
| 🔴 Architecture | [DEVELOPER_GUIDE](Docs/DEVELOPER_GUIDE.md) · [DGF_SPEC](Docs/DGF_SPEC.md) · [MEAI_TOOL_CALLING](../CoreAI/Docs/MEAI_TOOL_CALLING.md) · [MULTIPLAYER_AI](Docs/MULTIPLAYER_AI.md) |

Full map: [DOCS_INDEX](Docs/DOCS_INDEX.md).

---

## 📏 Recommended models

| Model | Size | Tool calling | Notes |
|-------|------|--------------|-------|
| **Qwen3.5-4B** | 4B | ✅ Excellent | Recommended local GGUF |
| **Qwen3.5-35B (MoE)** via API | 35B/3A | ✅ Excellent | Fast as 4B, accurate as 35B |
| **Gemma 4 26B** (LM Studio) | 26B | ✅ Excellent | Great over HTTP API |
| Qwen3.5-2B | 2B | ⚠️ Works | Occasional mistakes in multi-step |
| Qwen3.5-0.8B | 0.8B | ⚠️ Basic | Most tests pass |

> 🏆 **Qwen3.5-4B** passes the full PlayMode suite and is the production minimum.

---

## 🧪 Tests

```
Unity → Window → General → Test Runner
  ├── EditMode — large fast suite (no real LLM): streaming, Lua, tools, rate limit, CoreAi facade, orchestrator streaming, …
  └── PlayMode — integration tests; needs HTTP (env vars) or local GGUF
```

Details: [LLMUNITY_SETUP_AND_MODELS](Docs/LLMUNITY_SETUP_AND_MODELS.md) §7 (`COREAI_OPENAI_TEST_*` for HTTP).

---

## 📦 Install

Add via Unity Package Manager → **Add package from Git URL**:

```
https://github.com/NeoXider/CoreAI.git?path=Assets/CoreAI          # core first
https://github.com/NeoXider/CoreAI.git?path=Assets/CoreAiUnity     # then Unity layer
```

NuGet DLLs and Git dependencies for VContainer/MoonSharp/UniTask/MessagePipe/LLMUnity — see the root [README](../../README.md) §Quick Start.

---

## 🤝 Author

[Neoxider](https://github.com/NeoXider) · [NeoxiderTools](https://github.com/NeoXider/NeoxiderTools) · License: [PolyForm Noncommercial 1.0](../../LICENSE)

> 🎮 **CoreAI Unity** — stop writing dialogue trees. Wire the model once — ship chat, tools, and streaming without losing weekends to plumbing.
