# 🎮 CoreAI Unity — where the LLM meets your scene

> **Imagine:** your NPC merchant checks inventory, haggles on price, applies a discount, and processes the purchase — all through real function calls to your game code, streamed token-by-token into a chat bubble. No scripted branches. No fake replies. **That's this package.**

This is the **Unity half** of CoreAI: MEAI clients, VContainer wiring, UI Toolkit chat, streaming filters, production error diagnostics, and Editor menus that spare you copy-paste.

| Package | Depends on | Status |
|---------|-----------|--------|
| `com.nexoider.coreaiunity` **v0.25.5** — [`package.json`](package.json) | `com.nexoider.coreai` **v0.25.3** | ✅ Production |

*Languages:* [English](../../README.md) · [Русский](../../README_RU.md)

> **First time?** Open [DOCS_INDEX](Docs/DOCS_INDEX.md) or go straight to [QUICK_START](Docs/QUICK_START.md). **Need a one-liner from code?** See [COREAI_SINGLETON_API](Docs/COREAI_SINGLETON_API.md).

---

## Contents

| | |
|---|--|
| **CoreAi** | Static `Ask` / `Stream` / orchestration — section below |
| **Agent** | `AgentBuilder`, tools, memory |
| **Chat** | One-click demo + `CoreAiChatPanel` |
| **Streaming** | HTTP / LLMUnity, filters, cancel |
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

## 🆕 What's new (0.22 → 0.25)

- ⌨️ **0.25.3** — **Chat hotkeys + config.** Свёрнутый чат открывается с клавиатуры (по умолчанию **C**, можно сменить `KeyCode` или отключить), развёрнутый — **Esc** (стоп генерации / сворачивание, опционально отключается). Опрос Legacy `Input` в `Update`, когда нет фокуса UITK; хук `OnCollapsedStateChanged`. Подробности: [`README_CHAT.md`](Runtime/Source/Features/Chat/README_CHAT.md#chat-hotkeys).
- 🧹 **0.25.2** — **WebGL header buttons cleanup + STREAMING_WEBGL_TODO.** В `CoreAiChat.uxml` иконка stop-кнопки переведена с `⏹` (emoji, не рендерится в дефолтных WebGL-шрифтах — отсюда пустые круглые кнопки в браузере) на `■` (Geometric Shapes U+25A0, входит в LiberationSans / стандартный TMP fallback). Опубликован новый [`Docs/STREAMING_WEBGL_TODO.md`](Docs/STREAMING_WEBGL_TODO.md) с диагностикой и планом фикса WebGL SSE-стриминга в `MeaiOpenAiChatClient.CompleteStreamingAsync` (`UnityWebRequest` на WebGL отдаёт SSE одним терминальным чанком — `chunks=1` + симптом «бесконечная typing-анимация и нет ответа»). Workaround на стороне приложения — принудительно `EnableStreaming = false` под `#if UNITY_WEBGL && !UNITY_EDITOR`.
- 🩹 **0.25.1** — **WebGL TextField focus persistence + dual-input-system support.** `CoreAiChatPanel` под `#if UNITY_WEBGL && !UNITY_EDITOR` каждый кадр держит `WebGLInput.captureAllKeyboardInput = false` — устранён симптом «фокус 1 кадр и слетает» в собранном WebGL-билде. `OrchestrationDashboard` совместим с `Active Input Handling = Input System Package (New)` (раньше падал `InvalidOperationException`); `CoreAI.Source.asmdef` объявляет soft-зависимость `Unity.InputSystem` через `versionDefines` (`COREAI_HAS_INPUT_SYSTEM`).
- 🎯 **0.25.0** — **Forced tool mode.** `LlmToolChoiceMode` (`Auto`/`RequireAny`/`RequireSpecific`/`None`) на `AiTaskRequest`/`LlmCompletionRequest`. Streaming honours forced mode только на первой итерации (потом сброс в `Auto`). `CoreAiChatPanel.BuildAiTaskRequest` virtual hook для подмешивания полей в подклассах.
- 🔧 **0.24.2** — HTTP errors now show the API response body (not just `400 Bad Request`); `ToolExecutionPolicy.maxConsecutiveErrors` clamped to ≥ 1; docs refresh.
- 🧩 **0.24.0–0.24.1** — `SseToolCallAccumulator` for cloud SSE, `ToolExecutionPolicy` shared between streaming/non-streaming, hardened JSON extraction with code-block protection, UI stop deduplication.
- 🎮 **0.22** — Agent Control API: `StopAgent`, `ClearContext`, `OnToolExecuted`, chat clear 🗑 button, Escape-to-stop.
- 🎯 **0.21** — `CoreAi` static facade (`AskAsync`/`StreamAsync`), orchestrator streaming, collapse-to-FAB.
- 💬 **0.20** — Universal chat panel, HTTP+LLMUnity streaming, `ThinkBlockStreamFilter`, 3-layer streaming flags.

Full list: [CHANGELOG.md](CHANGELOG.md).

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

- HTTP: `MeaiOpenAiChatClient` parses OpenAI-compatible SSE. Cancellation aborts `UnityWebRequest` immediately.
- Local: `LlmUnityMeaiChatClient` bridges LLMUnity's frame callbacks to `IAsyncEnumerable`.
- Both paths run through `ThinkBlockStreamFilter` — a state machine that removes `<think>…</think>` blocks even when tags are split across chunks.

**Priority:** UI toggle → `AgentMemoryPolicy.SetStreamingEnabled(role, bool)` → `AgentBuilder.WithStreaming(bool)` → `CoreAISettings.EnableStreaming` (default `true`).

Deep dive: [STREAMING_ARCHITECTURE](Docs/STREAMING_ARCHITECTURE.md).

---

## 📖 Documentation

| Level | Documents |
|-------|-----------|
| 🟢 Beginner | [QUICK_START](Docs/QUICK_START.md) · [QUICK_START_FULL](Docs/QUICK_START_FULL.md) · [COREAI_SINGLETON_API](Docs/COREAI_SINGLETON_API.md) · [AGENT_BUILDER](../CoreAI/Docs/AGENT_BUILDER.md) · [COREAI_SETTINGS](Docs/COREAI_SETTINGS.md) · [EXAMPLES](Docs/EXAMPLES.md) |
| 💬 Chat & streaming | [README_CHAT](Runtime/Source/Features/Chat/README_CHAT.md) · [STREAMING_ARCHITECTURE](Docs/STREAMING_ARCHITECTURE.md) |
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
