<p align="center">
  <img src="Images/header_concept_2.png" alt="CoreAI Banner" width="100%">
</p>

# <img src="Docs/Images/coreai_icon.png" alt="CoreAI Icon" width="40" height="40" align="absmiddle"> CoreAI — LLM agents that play your game

**CoreAI is a Unity framework for LLM-powered NPCs and agents that call your game code** — function calling, tools, persistent memory, and runtime Lua — running on a **local 4 GB model** or any **OpenAI-compatible API**. No cloud keys required, no scripted dialogue trees.

*Read this in other languages: [English](README.md) · [Russian](README_RU.md).*

[![EditMode tests](https://img.shields.io/badge/EditMode-911%20passing-brightgreen)](Assets/CoreAiUnity/Tests/EditMode)
[![Unity](https://img.shields.io/badge/Unity-6000.0%2B-black)](https://unity.com/releases/editor)
[![Runs on](https://img.shields.io/badge/runs%20on-local%204B%20GGUF%20or%20any%20OpenAI%20API-blue)](#-recommended-models)
[![License](https://img.shields.io/badge/license-PolyForm%20Noncommercial%201.0-blue)](LICENSE)

> ### 🎬 Imagine this
> A player walks up to a blacksmith NPC and types _"Got any fire swords?"_. The NPC **calls your inventory code**, finds nothing, then **replies in-character**: _"Fresh out of fire blades, but I can forge one if you bring me a Fire Crystal."_ The player crafts it — the **Programmer agent writes Lua**, the **CoreMechanic agent rolls stats**, and a unique _Flame Sword_ lands in their inventory. All at runtime, streaming token-by-token into a chat bubble, on a local model. **That's CoreAI.**

### Why CoreAI?

LLM-in-a-game demos are everywhere; **shipping** one is the hard part. CoreAI is the missing production layer between a chat box and your gameplay: the model doesn't just talk — it **calls your C# and Lua**, and the framework handles the messy reality of small local models (wrong tool casing, split streaming tags, runaway loops, rate limits, context overflow) so your game doesn't break.

- 🆓 **Works out of the box, scales to production** — one-liner `await CoreAi.AskAsync("…")` for your first feature; full `AgentBuilder` + orchestrator + per-role routing when you need it.
- 🧩 **No forced heavy dependencies** — LLM and Lua are **optional modules** (`COREAI_NO_LLM` / `COREAI_NO_LUA`, auto-detected from installed packages). Install only what your game uses.
- 🛡️ **Built for small local models** — auto-repairs tool-name casing, retries with feedback, survives streaming `<think>` splits, caps runaway generation. The full PlayMode suite passes on a local **Qwen3.5-4B** GGUF.

### Is it for you?

| You are… | Start with | Time to first result |
|----------|-----------|----------------------|
| 🟢 **New / prototyping** | `CoreAI → Setup → Create Chat Demo Scene` → Play, or `await CoreAi.AskAsync("…")` | ~5 min |
| 🔵 **Building a real game** | `AgentBuilder` + tools + `IAiOrchestrationService`, per-role LLM routing | grows with you |

> 🚀 **30-second start:** install (below) → `CoreAI → Setup → Create Chat Demo Scene` → press **Play** → type. Jump to [Quick Start](#-quick-start).

<details>
<summary><b>Full feature list</b></summary>

- 🧠 **Agents that call your code** — real function calling with tool retry, auto-repair, and memory.
- 🛡️ **Self-healing resilience** — `TryRepairToolName` auto-fixes tool name casing; HTTP retry reads `Retry-After` with exponential backoff.
- 🌊 **Streaming that survives split tags** — stateful SSE accumulation handles fragmented `<think>` blocks and tool calls across chunks.
- 🧭 **LLM modes for production** — `LocalModel`, `ClientOwnedApi`, `ClientLimited`, `ServerManagedApi`, or mixed per-role routing profiles.
- 🗜️ **Smart long-chat context** — token-budget-aware history, rolling `## Conversation Summary`, optional auxiliary-LLM compaction, per-role toggles.
- 🎯 **Self-Service Skills** — group tools by domain; the model sees only 2 meta-tools (`read_skill` + `call_skill_tool`) instead of hundreds (~91% token savings).
- 🛡️ **Production guardrails** — tool-result truncation, per-tool timeout, runaway cap, loop guard, context trim, `RateLimiterMetrics` — all in the Inspector.
- 🔄 **Dual-backend auto-fallback** — primary HTTP backend fails → retry on a secondary automatically. Perfect for local model + cloud fallback.

</details>

**Releases:** `version` in [core `package.json`](Assets/CoreAI/package.json) and [Unity `package.json`](Assets/CoreAiUnity/package.json) (same semver). **Notes:** [Unity changelog](Assets/CoreAiUnity/CHANGELOG.md) · [Core changelog](Assets/CoreAI/CHANGELOG.md).

---

## Contents

| | Section |
|---|---------|
| [Changelog](#changelog) | Unity + core release notes (single source of truth) |
| [Three ways to call the LLM](#-three-ways-in-ui--coreai--agents) | Chat UI · `CoreAi` · agents / orchestrator |
| [What CoreAI can do](#-what-coreai-can-do) | Agents, tools, Lua, memory · long-chat budget & optional smart compaction (`v1.5+`) |
| [Architecture](#%EF%B8%8F-architecture) | Two packages, diagram |
| [Quick Start](#-quick-start) | NuGet, UPM, scene |
| [Documentation](#-documentation) | Map of docs |
| [Tests](#-tests) | EditMode & PlayMode |

---

## Changelog

Per-release notes, migration hints, and **version numbers** are maintained only in the changelogs (so this README does not need duplicate edits on every ship):

- **[`com.nexoider.coreaiunity` CHANGELOG](Assets/CoreAiUnity/CHANGELOG.md)** — Unity layer: Editor, chat UI, PlayMode tests, docs.
- **[`com.nexoider.coreai` CHANGELOG](Assets/CoreAI/CHANGELOG.md)** — portable core and release-sync lines.

The **`version`** field in each package’s `package.json` is the authoritative semver for that package.

---

## 🧭 Three ways in: UI · CoreAi · agents

| You are building… | Start here | One line |
|-------------------|------------|----------|
| **In-game chat for players** | `CoreAI → Setup → Create Chat Demo Scene` + `CoreAiChatPanel` | Play and type |
| **Any script, no DI yet** | `using CoreAI;` → `await CoreAi.AskAsync("…")` or `StreamAsync` | [COREAI_SINGLETON_API](Assets/CoreAiUnity/Docs/COREAI_SINGLETON_API.md) |
| **Full agent + tools + orchestrator** | `AgentBuilder` + `IAiOrchestrationService` | [AGENT_BUILDER](Assets/CoreAI/Docs/AGENT_BUILDER.md) |

All three paths share the same `CoreAILifetimeScope` and LLM backend when the scene is set up once.

---

## ✨ What CoreAI Can Do

### 🏗️ Create AI Agents in 3 Lines

```csharp
var merchant = new AgentBuilder("Blacksmith")
    .WithSystemPrompt("You are a blacksmith. Sell weapons and remember purchases.")
    .WithTool(new InventoryLlmTool(myInventory))  // Knows their stock
    .WithMemory()                                  // Remembers buyers
    .WithMaxOutputTokens(512)                      // Per-agent reply budget
    .Build();                                      // → AgentConfig (in-memory blueprint)

// Attach that blueprint to the global policy created at startup (CoreAILifetimeScope).
// The orchestrator looks up tools/system prompt by RoleId ("Blacksmith") from this policy.
merchant.ApplyToPolicy(CoreAIAgent.Policy);

// Ask* uses CoreAIAgent.Orchestrator (same startup wiring). Needs Play + CoreAILifetimeScope on scene.
merchant.Ask("Show me your swords");
merchant.Ask("Show me your swords", (response) => Debug.Log(response));
```

- **`Build()`** — returns `AgentConfig` (role id, tools, prompts, mode). Still unknown to the runtime until registered.
- **`ApplyToPolicy(CoreAIAgent.Policy)`** — writes into the live `AgentMemoryPolicy` so **`RunTask` / tool routing** can find this role's tools and merged prompts. Without it, `"Blacksmith"` is just a string the model never gets the right stack for.
- **`Ask` / `AskAsync`** — thin wrappers over **`CoreAIAgent.Orchestrator`** → `AiTaskRequest` with `RoleId` from the config. Same idea as resolving `IAiOrchestrationService` from DI — see [COREAI_SINGLETON_API](Assets/CoreAiUnity/Docs/COREAI_SINGLETON_API.md).

**3 Agent Modes:** 🛒 ToolsAndChat · 🤖 ToolsOnly · 💬 ChatOnly

### 🎯 Self-Service Skills — Agents Load Tools On Demand

When your agent has dozens of tools across different domains (crafting, combat, trading, quests), sending all of them every request wastes tokens. **Skills** solve this:

```csharp
// Define skills — each is a group of tools + instructions
var crafting = new SkillSet("Crafting",
    "Forge weapons and armor from raw materials",
    "1. Call get_recipes to list recipes.\n2. Call craft_item to craft.",
    new DelegateLlmTool("get_recipes", "List recipes", (string type) => ...),
    new DelegateLlmTool("craft_item", "Craft item", (string id) => ...));

var combat = new SkillSet("Combat", "Fight enemies", "Call attack with target.",
    new DelegateLlmTool("attack", "Attack target", (string target) => ...));

// Register skills — model sees only 2 meta-tools, not all skill tools
var gm = new AgentBuilder("GameMaster")
    .WithSystemPrompt("You are a Game Master in a fantasy RPG.")
    .WithSkill(crafting)
    .WithSkill(combat)
    .Build();
gm.ApplyToPolicy(policy);
```

**How it works:**
1. Model sees a lightweight **catalog** (skill names + descriptions) in the system prompt
2. Model calls `read_skill("Crafting")` → gets full instructions + tool schemas
3. Model calls `call_skill_tool("get_recipes", "{}")` → proxy routes to real tool
4. **Token overhead: constant** (2 meta-tools) regardless of total skill/tool count

> 💡 **50 tools across 10 skills?** Without skills: ~4,000 tokens. With skills: ~360 tokens. **91% savings.**

Mix direct tools and skills freely: `WithTool(memory)` + `WithSkill(crafting)` — memory is always visible, crafting loads on demand.

Docs: [AGENT_BUILDER.md §Skills](Assets/CoreAI/Docs/AGENT_BUILDER.md) · [COREAI_SINGLETON_API](Assets/CoreAiUnity/Docs/COREAI_SINGLETON_API.md)

### 💬 Drop-in Chat UI

Add an NPC chat to any scene in minutes — no custom UI code required:

```
CoreAI → Setup → Create Chat Demo Scene
```

This generates `Assets/CoreAiUnity/Scenes/CoreAiChatDemo.unity` with a pre-wired `CoreAiChatPanel` (UI Toolkit + UXML/USS, dark theme by default; **default floating size ~650×910**, **flush-right scrollbar**, optional **long-turn hint** under the typing row), `CoreAiChatConfig_Demo.asset` and a fully configured `CoreAILifetimeScope` — press **Play** and chat.

```csharp
// Same stack as the panel — pick your style:
await foreach (var chunk in CoreAi.StreamAsync("Hello", "SmartChat"))
    Debug.Log(chunk);

// Or explicit service (e.g. from DI in tests):
var service = CoreAiChatService.TryCreateFromScene();
await foreach (var chunk in service.SendMessageStreamingAsync("Hello", "SmartChat"))
    if (!string.IsNullOrEmpty(chunk.Text)) Debug.Log(chunk.Text);
```

**Streaming pipeline:** HTTP SSE **or** LLMUnity callback → stateful `ThinkBlockStreamFilter` (strips `<think>` blocks split across chunks) → typing indicator → bubble. Cancellation cancels the in-flight HTTP **`HttpClient`** request / enumerator on the MEAI path.

Docs: [README_CHAT.md](Assets/CoreAiUnity/Runtime/Source/Features/Chat/README_CHAT.md) · [STREAMING_ARCHITECTURE.md](Assets/CoreAiUnity/Docs/STREAMING_ARCHITECTURE.md)

---

### ⏳ Powerful Lua Coroutine Execution
Now CoreAI allows Lua scripts (like dynamically parsed world logic) to execute as asynchronous coroutines inside Unity:
```lua
-- Runs securely across multiple frames relying on Unity's Time
local start_time = time_now()
while time_now() - start_time < 2.0 do
    coroutine.yield()
end
```
Automatically maps APIs like `time_delta()`, `time_scale()`, and hooks securely via an internal `InstructionLimitDebugger` budget that yields processing back to Unity so you can run heavy computations without freezing the main thread.

---

### 🔧 AI Calls Tools (Function Calling)

AI doesn't just generate text — it **calls code** for real actions:

| Tool | What it does | Who uses it |
|------|--------------|-------------|
| 🌍 **WorldCommandTool** | Spawns, moves, modifies objects in the world | Creator AI |
| ⚡ **Action/Event Tool** | Calls any C# method or triggers an Event | All Agents |
| 🧠 **MemoryTool** | Saves/reads memory between sessions | All Agents |
| 📜 **LuaTool** | Executes Lua scripts | Programmer AI |
| 🎒 **InventoryTool** | Gets NPC inventory | Merchant AI |
| ⚙️ **GameConfigTool** | Reads/modifies game configs | Creator AI |
| 🎭 **SceneLlmTool** | Read and change hierarchy/transform in PlayMode | All Agents |
| 📸 **CameraLlmTool** | Captures screenshots (Base64 JPEG) for Vision | All Agents |

**Create your own:**
```csharp
public class WeatherLlmTool : ILlmTool
{
    public string Name => "get_weather";
    public string Description => "Get current weather.";
    public IEnumerable<AIFunction> CreateAIFunctions() 
    {
        yield return AIFunctionFactory.Create(
            async ct => await _provider.GetWeatherAsync(ct), "get_weather", "Get weather.");
    }
}
```

> 💡 **Design tools for token economy:** use short parameter keys (`q` instead of `question_text`), concise descriptions, indexes instead of strings, and smart defaults. Details: [TOOL_CALL_SPEC.md](Assets/CoreAiUnity/Docs/TOOL_CALL_SPEC.md)

---

### 🎮 Dynamic Mechanics — AI Changes the Game Live

```
Player: "Craft a weapon from Iron and Fire Crystal"
  ↓
CoreMechanicAI: "Iron + Fire Crystal → Flame Sword, damage 45"
  ↓
Programmer AI: execute_lua → create_item("Flame Sword", "weapon", 75)
               add_special_effect("fire_damage: 15")
  ↓
✨ Player receives a unique item!
```

---

### 🧠 Memory — AI Remembers Everything

| | Memory | ChatHistory |
|--|--------|-------------|
| **Storage** | JSON file on disk | In LLMAgent (RAM) |
| **Duration** | Between sessions | Current conversation |
| **For what** | Facts, purchases, quests | Conversation context |

---

### 🗜️ Long conversations — budget, summaries & optional “smart compaction”

When **`WithChatHistory()`** fills the model window, CoreAI keeps a fresh **tail** of messages and folds older turns into **`## Conversation Summary`** in the system prompt (deterministic rollup by default). Newer releases add:

| Feature | What you get |
|--------|----------------|
| **Context budget** | `IContextBudgetPolicy` / `HistoryTokenBudget` — system + user + tools shrink what fits in history fairly. |
| **Persisted summaries** | **`InMemoryConversationSummaryStore`** (process) or **`FileConversationSummaryStore`** (disk under Unity’s **`persistentDataPath`**) — summaries accrue across turns. |
| **LLM-assisted compaction** *(opt-in)* | Extra **`CompleteAsync`** on role **`__CoreAI_ContextCompaction`** to rewrite the rolling summary (enable globally on **`CoreAISettingsAsset`**, then tune per agent). |
| **Per-role defaults** | **`AgentBuilder`** agents default **on**; built-in **`Programmer`** defaults **off** (cheaper truncation for tool-heavy Lua roles). **`WithLlmContextCompaction(false)`** to opt out. |

```csharp
// Custom agent: long chat + smart rollup when global toggle is enabled
new AgentBuilder("LoreKeeper")
    .WithChatHistory(8192, persistBetweenSessions: true)
    .WithLlmContextCompaction(true) // default anyway; explicit for docs
    .Build()
    .ApplyToPolicy(policy);

// Programmer-style role: deterministic only for this builder agent
new AgentBuilder("ToolsFirst")
    .WithChatHistory(4096)
    .WithLlmContextCompaction(false)
    .Build();
```

Deep dive: [CHANGELOG (Core `v1.5.2–1.5.3`)](Assets/CoreAI/CHANGELOG.md) · [MemorySystem](Assets/CoreAiUnity/Docs/MemorySystem.md) · [ARCHITECTURE](Assets/CoreAiUnity/Docs/ARCHITECTURE.md) · [COREAI_SETTINGS](Assets/CoreAiUnity/Docs/COREAI_SETTINGS.md).

---

### 🔄 Tool Call Retry + Self-Healing — AI Learns from Mistakes

Small models (Qwen3.5-2B) sometimes forget the format or case of tool names. CoreAI automatically:

- 🔧 **Repairs tool name casing** — `TryRepairToolName` silently maps `MEMORY` → `memory`, `Spawn_Quiz` → `spawn_quiz` before execution fails.
- ♻️ **Retries on failure** — up to **3 retries** with error feedback injected into chat history so the model self-corrects.
- 🌐 **Retries HTTP errors** — `429 (Rate Limited)` and `5xx` responses trigger automatic retry with `Retry-After` header support or exponential backoff (**2s → 4s**, configurable in Inspector).
- ✅ **Checks fenced Lua blocks** immediately.

```
Model says: {"name":"MEMORY", ...}
     ↓ TryRepairToolName
Executes: {"name":"memory", ...}  ← silently fixed, no error shown
```

---

### 📏 Recommended Models

| Model | Size | Tool Calling | When to use |
|-------|------|--------------|-------------|
| **Qwen3.5-4B** | 4B | ✅ Great | **Recommended** for local GGUF |
| **Qwen3.5-35B (MoE) API** | 35B/3A | ✅ Excellent | **Ideal** via API — fast & accurate |
| **Gemma 4 26B (via LM Studio)** | 26B | ✅ Excellent | Great via HTTP API |
| **LM Studio / OpenAI API** | Any | ✅ Excellent | External models via HTTP — best choice |
| Qwen3.5-2B | 2B | ⚠️ Works | Works, but sometimes makes mistakes |
| Qwen3.5-0.8B | 0.8B | ⚠️ Basic | Most tests pass, struggles with multi-step |

> 💡 **Recommendation: Qwen3.5-4B locally or Qwen3.5-35B (MoE) via API**  
> MoE models (Mixture of Experts) activate only 3B parameters per inference — fast as 4B, accurate as 35B.

### 🧪 PlayMode Test Results by Model Size

All CoreAI PlayMode tests have been verified on real LLM backends. Results:

| Test Category | 0.8B | 2B | 4B+ |
|--------------|------|-----|------|
| Memory Tool (write/append/clear) | ✅ Pass | ✅ Pass | ✅ Pass |
| Custom Agents (tool calling) | ✅ Pass | ✅ Pass | ✅ Pass |
| World Commands (list/play/spawn) | ✅ Pass | ✅ Pass | ✅ Pass |
| Execute Lua (single tool) | ✅ Pass | ✅ Pass | ✅ Pass |
| Multi-Agent Workflow (Creator→Mechanic→Programmer) | ⚠️ Partial | ✅ Pass | ✅ Pass |
| Crafting Memory (multi-step: memory + lua) | ⚠️ Partial | ⚠️ Mostly | ✅ Pass |
| Chat History (persistent context) | ❌ Too small | ⚠️ Mostly | ✅ Pass |
| Player Chat (NPC dialogue) | ✅ Pass | ✅ Pass | ✅ Pass |

> 🏆 **Qwen3.5-4B passes ALL tests.** This is the recommended minimum for production use.  
> 📊 **Qwen3.5-0.8B passes most tests** — impressive for its size! Struggles only with complex multi-step tool calling chains.  
> 📈 **2B is a solid middle ground** — occasional mistakes in multi-step scenarios, but mostly reliable.

---

## 💡 Integration Examples & Ideas

How can you use CoreAI in your game? Here are some "Brainrot-free" ideas:

1.  **The "Alive" Merchant**: Instead of a static list, the blacksmith remembers that you sold him a legendary dragon scale yesterday. He might offer you a "special deal" on a Dragon-slaying sword today.
2.  **Autonomous Game Master**: Let the AI monitor the player's health and resources. If the player is struggling, the GM might "whisper" a hint or "accidentally" spawn a health potion nearby via `WorldCommandTool`.
3.  **Real-time Lore Narrator**: As the player enters a new biome, the AI generates unique lore based on the current weather, time of day, and the player's equipped items.
4.  **AI-Driven Procedural Quests**: Quests are no longer `Kill X Wolves`. An AI King asks you to `Investigate why the wolves are glowing`, and you can actually *interview* the wolves (who might be under a Lua-driven spell).
5.  **Voice/Chat-to-Action**: "Make it rain fire!" -> The AI parses the intent, checks the `WeatherTool`, and executes the corresponding Lua script to trigger a firestorm.

---

## 🏛️ Architecture

The repository consists of **two packages**:

| Package | What's inside | Dependencies |
|---------|--------------|--------------|
| **[com.nexoider.coreai](Assets/CoreAI)** | Portable core — pure C# **without** Unity | VContainer, MoonSharp |
| **[com.nexoider.coreaiunity](Assets/CoreAiUnity)** | Unity layer — DI, LLM, MEAI, MessagePipe, tests | Depends on `coreai` |

```
┌─────────────────────────────────────────────────────────────┐
│                      Player / Game                           │
└──────────────────────┬──────────────────────────────────────┘
                       ↓
┌─────────────────────────────────────────────────────────────┐
│                   AiOrchestrator                              │
│  • Priority queue  • JSON strip (defense-in-depth)            │
└──────────────────────┬──────────────────────────────────────┘
                       ↓
┌─────────────────────────────────────────────────────────────┐
│              LoggingLlmClientDecorator                        │
│  • HTTP retry (429/5xx)  • Retry-After  • Exp. backoff        │
└──────────────────────┬──────────────────────────────────────┘
                       ↓
┌─────────────────────────────────────────────────────────────┐
│                     LLM Client (MeaiLlmClient)               │
│  • LLMUnity (local GGUF)  • OpenAI HTTP  • Stub             │
│  • TryExtractToolCallsFromText (JSON-in-text → tool call)    │
└──────────────────────┬──────────────────────────────────────┘
                       ↓
┌─────────────────────────────────────────────────────────────┐
│               SmartToolCallingChatClient                      │
│  • TryRepairToolName (MEMORY → memory)                       │
│  • Duplicate detection  • Consecutive error tracking          │
└──────────────────────┬──────────────────────────────────────┘
                       ↓
┌─────────────────────────────────────────────────────────────┐
│                   AI Agents                                  │
│  🛒 Merchant  📜 Programmer  🎨 Creator  📊 Analyzer        │
│  🗡️ CoreMechanic  💬 SmartChat  + Your custom ones!        │
└──────────────────────┬──────────────────────────────────────┘
                       ↓
┌─────────────────────────────────────────────────────────────┐
│                   Tools (ILlmTool)                           │
│  🧠 Memory  📜 Lua  🎒 Inventory  ⚙️ GameConfig  + Yours!   │
│  🎯 SkillSet → read_skill + call_skill_tool (on-demand)     │
└──────────────────────┬──────────────────────────────────────┘
                       ↓
┌─────────────────────────────────────────────────────────────┐
│                   Game World                                 │
│  • Lua Sandbox (MoonSharp)  • MessagePipe  • DI (VContainer)│
└─────────────────────────────────────────────────────────────┘
```

---

## 🚀 Quick Start

### 1. Install NuGet DLLs (required)

CoreAI uses [Microsoft.Extensions.AI](https://www.nuget.org/packages/Microsoft.Extensions.AI) for the LLM pipeline. Copy these DLLs into your project's `Assets/Packages/` folder (download from NuGet or copy from this repo's `Assets/Packages/`):

| NuGet Package | Version | Required by |
|---------------|---------|-------------|
| `Microsoft.Extensions.AI` | 10.4.1 | CoreAI Core |
| `Microsoft.Extensions.AI.Abstractions` | 10.4.1 | CoreAI Core |
| `Microsoft.Bcl.AsyncInterfaces` | 10.0.4 | System dependency |
| `System.Text.Json` | 10.0.4 | JSON serialization |
| `System.Text.Encodings.Web` | 10.0.4 | System dependency |
| `System.Numerics.Tensors` | 10.0.4 | System dependency |
| `Microsoft.Extensions.Logging.Abstractions` | 10.0.4 | Logging |
| `Microsoft.Extensions.DependencyInjection.Abstractions` | 10.0.4 | DI |
| `System.Diagnostics.DiagnosticSource` | 10.0.4 | System dependency |

> 💡 **Easiest way:** Clone this repo and copy the entire `Assets/Packages/` folder into your project.

### 2. Add Git dependencies to manifest.json (required)

Unity Package Manager does not transitively pull every Git dependency for you.

**Preferred:** After CoreAiUnity is in the project, use **CoreAI → Setup → Install Git Dependencies**. It merges any *missing* package keys only — pins you manage by hand stay untouched.

**Manual alternative:** Edit `Packages/manifest.json` and add these entries under `"dependencies"`:

```json
    "jp.hadashikick.vcontainer": "https://github.com/hadashiA/VContainer.git?path=VContainer/Assets/VContainer#1.17.0",
    "org.moonsharp.moonsharp": "https://github.com/moonsharp-devs/moonsharp.git?path=/interpreter#upm/beta/v3.0",
    "com.cysharp.messagepipe": "https://github.com/Cysharp/MessagePipe.git?path=src/MessagePipe.Unity/Assets/Plugins/MessagePipe",
    "com.cysharp.messagepipe.vcontainer": "https://github.com/Cysharp/MessagePipe.git?path=src/MessagePipe.Unity/Assets/Plugins/MessagePipe.VContainer",
    "com.cysharp.unitask": "https://github.com/Cysharp/UniTask.git?path=src/UniTask/Assets/Plugins/UniTask",
    "ai.undream.llm": "https://github.com/undreamai/LLMUnity.git",
```

*(After saving the file, Unity will automatically download VContainer, MoonSharp, UniTask, MessagePipe, and LLMUnity).*

### 3. Install CoreAI packages (Git URL)
**Unity Editor →** Window → Package Manager → `+` → **Add package from git URL…**

**Step 1 — Core engine (pure C#, no UnityEngine):**
```text
https://github.com/NeoXider/CoreAI.git?path=Assets/CoreAI
```

**Step 2 — Unity layer (MonoBehaviour, LLM clients, tools):**
```text
https://github.com/NeoXider/CoreAI.git?path=Assets/CoreAiUnity
```

### 4. Setup scene

**Recommended first try (chat UI + scope):**

```
CoreAI → Setup → Create Chat Demo Scene
```

**Bare wiring (lifetime scope + settings assets — no demo UI):**

```
CoreAI → Setup → Create Bare Scene (advanced)
```

Either path can:
- ✅ Create `CoreAILifetimeScope` on the scene
- ✅ Ensure required assets (`CoreAISettings`, `GameLogSettings`, `AgentPromptsManifest`, etc.)
- ✅ Assign references on the scope
- ✅ Optionally add `LLM` + `LLMAgent` when the backend is LLMUnity (bare-scene wizard)

### 5. Configure LLM backend

Open settings: **CoreAI → Settings** and choose your backend:

| Backend | Setup |
|---------|-------|
| **LLMUnity** (local) | Download a GGUF model (e.g. Qwen3.5-4B) via LLMUnity Model Manager |
| **HTTP API** (LM Studio, OpenAI) | Set `API Base URL` and `API Key` in Settings |
| **Auto** | CoreAI picks the best available backend automatically |

### 6. Create your first agent

```csharp
var storyteller = new AgentBuilder("Storyteller")
    .WithSystemPrompt("You are a campfire storyteller. Share tales about the world.")
    .WithMemory()
    .WithChatHistory()
    .WithMode(AgentMode.ChatOnly)
    .Build();
```

> 📖 **Full setup guide with LLM configuration:** [QUICK_START.md](Assets/CoreAiUnity/Docs/QUICK_START.md)  
> 🏗️ **Agent Builder reference + ready recipes:** [AGENT_BUILDER.md](Assets/CoreAI/Docs/AGENT_BUILDER.md)

---

## 📚 Documentation

**Language:** In-depth Markdown under [`Assets/CoreAiUnity/Docs/`](Assets/CoreAiUnity/Docs/) and [`Assets/CoreAI/Docs/`](Assets/CoreAI/Docs/) is maintained in **English**. The portable `CoreAI` package docs are English-only except clearly marked `_RU` redirect/plan stubs. [README_RU.md](README_RU.md) mirrors this page for Russian navigation; follow the linked guides for detail.

Start from the index and pick the level that matches your goal:

> 🧭 **[Docs/README.md](Docs/README.md)** — repository documentation entry point.
> 🧭 **[DOCS_INDEX.md](Assets/CoreAiUnity/Docs/DOCS_INDEX.md)** — CoreAI Unity map (Beginner → Intermediate → Architecture).

**Reset CoreAI file persistence (Editor):** **CoreAI → Delete All Persistent Saves...** (exit Play Mode first) deletes **`persistentDataPath/CoreAI`** — agent memory, persisted chat JSON, summaries (desktop), Lua/data-overlay version files. Project assets under `Assets/` are untouched. See [TROUBLESHOOTING.md](Assets/CoreAiUnity/Docs/TROUBLESHOOTING.md).

### Getting started

| Document | What's inside |
|----------|--------------|
| 🚀 [QUICK_START.md](Assets/CoreAiUnity/Docs/QUICK_START.md) | Install → open scene → connect LLM → Play |
| 🚀 [QUICK_START_FULL.md](Assets/CoreAiUnity/Docs/QUICK_START_FULL.md) | Full 10-min walkthrough: LM Studio → Unity → first command |
| 🎯 [COREAI_SINGLETON_API.md](Assets/CoreAiUnity/Docs/COREAI_SINGLETON_API.md) | **`CoreAi`** one-liners — beginners + pros |
| 🏗️ [AGENT_BUILDER.md](Assets/CoreAI/Docs/AGENT_BUILDER.md) | Build an NPC in 3 lines · modes · ready-made recipes |
| ⚙️ [COREAI_SETTINGS.md](Assets/CoreAiUnity/Docs/COREAI_SETTINGS.md) | Backends, models, timeout, streaming toggle |

### Chat & streaming

| Document | What's inside |
|----------|--------------|
| 💬 [README_CHAT.md](Assets/CoreAiUnity/Runtime/Source/Features/Chat/README_CHAT.md) | Drop-in `CoreAiChatPanel` + demo scene |
| 🌊 [STREAMING_ARCHITECTURE.md](Assets/CoreAiUnity/Docs/STREAMING_ARCHITECTURE.md) | SSE / LLMUnity → filters → UI · orchestrator streaming |
| 📊 [MEAI_TOKENS_FACT_VS_ESTIMATE.md](Assets/CoreAI/Docs/MEAI_TOKENS_FACT_VS_ESTIMATE.md) | Provider `usage` vs pre-flight estimates; SSE `include_usage`; HTTP vs orchestrator timeouts |
| 🔒 [LUA_SANDBOX_SECURITY.md](Assets/CoreAI/Docs/LUA_SANDBOX_SECURITY.md) | Lua sandbox boundary, removed APIs, execution limits, binding rules |

### Tools, memory, roles

| Document | What's inside |
|----------|--------------|
| 🔧 [TOOL_CALL_SPEC.md](Assets/CoreAiUnity/Docs/TOOL_CALL_SPEC.md) | Tool-calling specification |
| 🛒 [CHAT_TOOL_CALLING.md](Assets/CoreAiUnity/Docs/CHAT_TOOL_CALLING.md) | Merchant NPC with inventory |
| 🧠 [MemorySystem.md](Assets/CoreAiUnity/Docs/MemorySystem.md) | Agent memory (disk + chat history) |
| 🤖 [AI_AGENT_ROLES.md](Assets/CoreAiUnity/Docs/AI_AGENT_ROLES.md) | Agent roles & prompts |

### Architecture

| Document | What's inside |
|----------|--------------|
| 🗺️ [DEVELOPER_GUIDE.md](Assets/CoreAiUnity/Docs/DEVELOPER_GUIDE.md) | Code map, LLM→commands flow, PR checklist |
| 📐 [DGF_SPEC.md](Assets/CoreAiUnity/Docs/DGF_SPEC.md) | Normative spec: DI, threads, authority |
| 🛠️ [MEAI_TOOL_CALLING.md](Assets/CoreAI/Docs/MEAI_TOOL_CALLING.md) | MEAI pipeline: `ILlmTool` → `AIFunction` → `FunctionInvokingChatClient` |
| 🧰 [TOOL_CALLING_BEST_PRACTICES.md](Assets/CoreAI/Docs/TOOL_CALLING_BEST_PRACTICES.md) | Tool schema, idempotency, duplicate calls, SkillSet organization |
| 🔀 [LLM_ROUTING.md](Assets/CoreAI/Docs/LLM_ROUTING.md) | Portable routing: modes, policy, usage sinks, timeouts |
| 📑 [CoreAI/Docs/README.md](Assets/CoreAI/Docs/README.md) | Index of all portable CoreAI markdown guides |
| 📋 [CHANGELOG.md](Assets/CoreAI/CHANGELOG.md) · [CHANGELOG (Unity)](Assets/CoreAiUnity/CHANGELOG.md) | Version history |

---

## 🧪 Tests

```
Unity → Window → General → Test Runner
  ├── EditMode — large fast suite (no real LLM): prompts, streaming, Lua sandbox,
  │              tools, rate limit, CoreAi facade, tool name repair, backoff math,
  │              orchestrator streaming, tool call extraction parity, …
  └── PlayMode — integration tests with a configured HTTP or local GGUF backend
                 ├── FullPipelineResiliencePlayModeTests — streaming/non-streaming
                 │   tool calls with no-JSON-leak guarantees, memory write/read,
                 │   orchestrator merchant + inventory, trace diagnostics
                 ├── ToolNameRepairPlayModeTests — hybrid scripted+real-LLM:
                 │   wrong casing repair, unknown tool self-correction, mixed-case
                 └── StreamingToolCallingPlayModeTests — cancel, state parity
```

Run EditMode first in CI; PlayMode is optional and needs a backend (env vars for HTTP — see [LLMUNITY_SETUP_AND_MODELS](Assets/CoreAiUnity/Docs/LLMUNITY_SETUP_AND_MODELS.md)). Real-model memory recall may **Ignore** (not fail) if the local OpenAI-compatible server returns **HTTP 5xx** — see [TROUBLESHOOTING](Assets/CoreAiUnity/Docs/TROUBLESHOOTING.md).

---

## 🌐 Multiplayer and Singleplayer

- **Singleplayer:** Same pipeline, AI works locally
- **Multiplayer:** AI logic on host, clients receive agreed outcomes

**One template — for both solo campaign and coop.**

---

## 🤝 Author and Community

**Author:** [Neoxider](https://github.com/NeoXider)  
**Ecosystem:** [NeoxiderTools](https://github.com/NeoXider/NeoxiderTools)  
**License:** [PolyForm Noncommercial 1.0.0](LICENSE) (commercial use — separate license)

**Contact:** neoxider@gmail.com | [GitHub Issues](https://github.com/NeoXider/CoreAI/issues)

---

> 🎮 **CoreAI** — stop writing dialogue trees. Ship agents that *think*, *call your code*, and *remember* — from a local 4B model or a cloud API, your choice.
