# 🤖 CoreAI — AI Agents for Dynamic Games

**Living NPCs, procedural content, dynamic mechanics** — all driven by AI, right during gameplay.

> Imagine a game that adapts not just with numbers, but with logic and situations: different threats, different pacing, different world "character". CoreAI makes this a reality.

---

## ✨ What CoreAI Can Do

### 🏗️ Create AI Agents in 3 Lines

```csharp
var merchant = new AgentBuilder("Blacksmith")
    .WithSystemPrompt("You are a blacksmith. Sell weapons and remember purchases.")
    .WithTool(new InventoryLlmTool(myInventory))  // Knows their stock
    .WithMemory()                                  // Remembers buyers
    .WithMode(AgentMode.ToolsAndChat)              // Tools + chat
    .Build();
```

**3 Agent Modes:** 🛒 ToolsAndChat · 🤖 ToolsOnly · 💬 ChatOnly

---

### 🔧 AI Calls Tools (Function Calling)

AI doesn't just generate text — it **calls code** for real actions:

| Tool | What it does | Who uses it |
|------|-------------|-------------|
| 🧠 **MemoryTool** | Persistent memory between sessions | All agents |
| 📜 **LuaTool** | Executes Lua scripts | Programmer AI |
| 🎒 **InventoryTool** | Gets NPC inventory | Merchant AI |
| ⚙️ **GameConfigTool** | Reads/writes game configs | Creator AI |

**Create your own:**
```csharp
public class WeatherLlmTool : ILlmTool
{
    public string Name => "get_weather";
    public string Description => "Get current weather.";
    public AIFunction CreateAIFunction() => AIFunctionFactory.Create(
        async ct => await _provider.GetWeatherAsync(ct), "get_weather", "Get weather.");
}
```

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

### 🔄 Tool Call Retry — AI Learns from Mistakes

Small models (Qwen3.5-0.8B) sometimes forget the format. CoreAI automatically gives **3 retries** + checks fenced Lua blocks immediately.

---

## 🏛️ Architecture

The repository consists of **two packages**:

| Package | What's inside | Dependencies |
|---------|--------------|--------------|
| **[com.nexoider.coreai](Assets/CoreAI)** | Portable core — pure C# **without** Unity | VContainer, MoonSharp |
| **[com.nexoider.coreaiunity](Assets/CoreAiUnity)** | Unity layer — DI, LLM, MessagePipe, tests | Depends on `coreai` |

```
┌─────────────────────────────────────────────────────────────┐
│                      Player / Game                           │
└──────────────────────┬──────────────────────────────────────┘
                       ↓
┌─────────────────────────────────────────────────────────────┐
│                   AiOrchestrator                              │
│  • Priority queue  • Retry logic  • Tool calling              │
└──────────────────────┬──────────────────────────────────────┘
                       ↓
┌─────────────────────────────────────────────────────────────┐
│                     LLM Client                               │
│  • LLMUnity (local GGUF)  • OpenAI HTTP  • Stub             │
└──────────────────────┬──────────────────────────────────────┘
                       ↓
┌─────────────────────────────────────────────────────────────┐
│                   AI Agents                                  │
│  🛒 Merchant  📜 Programmer  🎨 Creator  📊 Analyzer        │
│  🗡️ CoreMechanic  💬 PlayerChat  + Your custom ones!        │
└──────────────────────┬──────────────────────────────────────┘
                       ↓
┌─────────────────────────────────────────────────────────────┐
│                   Tools (ILlmTool)                           │
│  🧠 Memory  📜 Lua  🎒 Inventory  ⚙️ GameConfig  + Yours!   │
└──────────────────────┬──────────────────────────────────────┘
                       ↓
┌─────────────────────────────────────────────────────────────┐
│                   Game World                                 │
│  • Lua Sandbox (MoonSharp)  • MessagePipe  • DI (VContainer)│
└─────────────────────────────────────────────────────────────┘
```

---

## 🚀 Quick Start

### 1. Install Packages (UPM)

```
Window → Package Manager → + → Add package from git URL…

https://github.com/NeoXider/CoreAI.git?path=Assets/CoreAI
https://github.com/NeoXider/CoreAI.git?path=Assets/CoreAiUnity
```

### 2. Open the Scene

```
Assets/CoreAiUnity/Scenes/_mainCoreAI.unity → Play
```

### 3. Create Your Agent

```csharp
var storyteller = new AgentBuilder("Storyteller")
    .WithSystemPrompt("You are a campfire storyteller. Share tales about the world.")
    .WithMemory()
    .WithChatHistory()
    .WithMode(AgentMode.ChatOnly)
    .Build();
```

---

## 📚 Documentation

| Document | What's inside |
|----------|--------------|
| 📖 [CoreAI README](Assets/CoreAI/README.md) | General overview + AgentBuilder |
| 🏗️ [AGENT_BUILDER.md](Assets/CoreAI/Docs/AGENT_BUILDER.md) | Agent builder guide |
| 🔧 [TOOL_CALL_SPEC.md](Assets/CoreAiUnity/Docs/TOOL_CALL_SPEC.md) | Tool calling specification |
| 🛒 [CHAT_TOOL_CALLING.md](Assets/CoreAiUnity/Docs/CHAT_TOOL_CALLING.md) | Merchant NPC with inventory |
| 🧠 [MemorySystem.md](Assets/CoreAiUnity/Docs/MemorySystem.md) | Agent memory system |
| 🗺️ [DEVELOPER_GUIDE.md](Assets/CoreAiUnity/Docs/DEVELOPER_GUIDE.md) | Code map, architecture |
| 🤖 [AI_AGENT_ROLES.md](Assets/CoreAiUnity/Docs/AI_AGENT_ROLES.md) | Agent roles and prompts |
| 📋 [CHANGELOG.md](Assets/CoreAI/CHANGELOG.md) | Version history |

---

## 🧪 Tests

```
Unity → Window → General → Test Runner
  ├── EditMode — 191 tests (fast, no LLM)
  └── PlayMode — 12+ tests (with real LLM)
```

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

> 🎮 **CoreAI** — Make your game smarter. One agent at a time.
