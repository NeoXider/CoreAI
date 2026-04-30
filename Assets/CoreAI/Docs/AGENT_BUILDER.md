# 🏗️ Agent Builder — Custom Agent Constructor

## Overview

**AgentBuilder** is a fluent API for quickly creating custom agents with unique tools, prompts, and operating modes. It makes it easy to add new NPCs to a game without changing the CoreAI core.

### Capabilities

- ✅ **Unique tools** — any `ILlmTool` for a specific agent
- ✅ **Three response modes** — `ChatOnly`, `ToolsAndChat`, `ToolsOnly`
- ✅ **Memory** — persistent agent memory (write/append/clear)
- ✅ **Chat history** — automatic saving of conversation context
- ✅ **Per-agent output budget** — `WithMaxOutputTokens(...)` for roles that should stay short or verbose
- ✅ **Minimal code** — 3–5 lines per agent
- ✅ **Single MEAI pipeline** — the same tool calling for HTTP API and LLMUnity

---

## Quick start

### 1. Create an agent

```csharp
var merchant = new AgentBuilder("Blacksmith")
    .WithSystemPrompt("You are a blacksmith. When player asks to buy, call get_inventory first.")
    .WithTool(new InventoryLlmTool(myInventoryProvider))
    .WithMemory()  // Persistent memory
    .WithMode(AgentMode.ToolsAndChat)
    .Build();

// Registers the agent's tools, memory, and chat history in the shared policy.
// The orchestrator uses the policy so that when the "Blacksmith" role is invoked,
// the correct tools and settings are wired automatically.
merchant.ApplyToPolicy(policy);
```

### 2. Configure the backend (unified settings)

```
Unity → Create → CoreAI → CoreAI Settings
```

In the Inspector, choose **LLM Backend**:
- **Auto** — picks LLMUnity or HTTP API automatically
- **LlmUnity** — local GGUF model
- **OpenAiHttp** — HTTP API (LM Studio, OpenAI, Qwen)
- **Offline** — no model (stub)

### 3. Invoke the agent

**🟢 Simplest — `Ask` (fire-and-forget, no async):**

```csharp
// One line! No await, no container.
merchant.Ask("Show me your swords");

// With a callback when done (receives the text response):
merchant.Ask("Show me your swords", (response) => Debug.Log(response));
```

> 💡 `Ask` uses the global `CoreAIAgent.Orchestrator` — it auto-initializes at scene start with `CoreAILifetimeScope`. Ideal for UI buttons, events, and `MonoBehaviour`.

**🟡 Async — `AskAsync` (with await):**

```csharp
// Returns the model's text response:
string response = await merchant.AskAsync("Show me your swords");
Debug.Log(response);

// With an explicit orchestrator (for tests / custom setups):
var orch = container.Resolve<IAiOrchestrationService>();
string response2 = await merchant.AskAsync(orch, "Show me your swords");
```

**🔴 Advanced — full control:**

```csharp
// Via the orchestrator directly:
await orch.RunTaskAsync(new AiTaskRequest
{
    RoleId = "Blacksmith",
    Hint = "Show me your swords",
    Priority = 10,
    SourceTag = "npc_dialogue"
});

// Or via a manual LLM client (for tests / custom pipeline):
var client = MeaiLlmClient.CreateHttp(coreAiSettings, logger, memoryStore);
var result = await client.CompleteAsync(new LlmCompletionRequest
{
    AgentRoleId = "Blacksmith",
    SystemPrompt = merchant.SystemPrompt,
    UserPayload = "Show me your swords",
    Tools = merchant.Tools
});
```

> 🛡️ **Built-in spam protection (call cancellation):**
> Both methods (`Ask` and `AskAsync`) automatically pass `CancellationScope = Agent.RoleId` to the orchestrator.
> That means **if you call `merchant.Ask()` again while the first request is still generating, the old request is forcibly stopped (Cancelled)** and the new one runs. This saves CPU and tokens on double-clicks or message spam to the same NPC.

---

## 📋 Ready-made recipes — copy and use

> 💡 Each recipe is **complete** working code. Copy, rename, done.

### Recipe 1: Blacksmith (sells items + remembers purchases)

```csharp
// 1. Create the agent
var blacksmith = new AgentBuilder("Blacksmith")
    .WithSystemPrompt(@"You are a blacksmith NPC. When player asks to buy, 
FIRST call get_inventory tool. Then respond in-character with items and prices.
Remember what the player bought using memory.")
    .WithTool(new InventoryLlmTool(myInventoryProvider))
    .WithMemory()
    .WithChatHistory()
    .Build();

// 2. Register (via global CoreAIAgent.Policy or your own policy)
blacksmith.ApplyToPolicy(CoreAIAgent.Policy);

// 3. Invoke (one line!)
blacksmith.Ask("What do you have?");
```

### Recipe 2: Storyteller (chat only, no tools)

```csharp
var storyteller = new AgentBuilder("Storyteller")
    .WithSystemPrompt("You are a campfire storyteller. Share tales about the game world.")
    .WithChatHistory()           // Remembers the conversation
    .WithTemperature(0.7f)       // More creative replies
    .WithMaxOutputTokens(512)    // Cap this role's response length
    .WithMode(AgentMode.ChatOnly)
    .Build();

storyteller.ApplyToPolicy(CoreAIAgent.Policy);

// Fire-and-forget with callback (logs the response):
storyteller.Ask("Tell me a story", (s) => Debug.Log(s));
```

### Recipe 3: Guard (fires an action on trigger)

```csharp
var guard = new AgentBuilder("Guard")
    .WithSystemPrompt(@"You are a city guard. 
If the player admits to a crime, you MUST call the 'alarm' tool immediately.")
    .WithEventTool("alarm", "Sound the alarm when player confesses a crime")
    .WithChatHistory()
    .Build();

guard.ApplyToPolicy(CoreAIAgent.Policy);

// In any script, subscribe to the event:
CoreAiEvents.Subscribe("alarm", () => audioSource.PlayOneShot(alarmSound));
```

### Recipe 4: Background analyzer (tool calls only, no text)

```csharp
var analyzer = new AgentBuilder("SessionAnalyzer")
    .WithSystemPrompt("Analyze session telemetry. Save key observations to memory.")
    .WithMemory(MemoryToolAction.Append)
    .WithTemperature(0.0f)         // Strictly deterministic
    .WithMode(AgentMode.ToolsOnly) // Does not reply with text
    .Build();

analyzer.ApplyToPolicy(policy);
```

### Recipe 5: Game Master (generates game mechanics on the fly)

Combining `AgentBuilder` and `LuaLlmTool` lets agents **write and change game rules at runtime.** If your game keeps some logic in a global `SecureLuaEnvironment` (e.g. damage calculation, spawn odds, or item prices), you can expose that environment to a Game Master agent.

```csharp
// 1. You have a shared Lua sandbox the game uses for damage
SecureLuaEnvironment sandbox = new();
sandbox.RunChunk(sandbox.CreateScript(new LuaApiRegistry()), "function calculate_damage() return 10 end");

// 2. Create a tool for the agent with access to that sandbox
var master = new AgentBuilder("GameMaster")
    .WithSystemPrompt("You are the GameMaster. You manage game mechanics. Change lua functions on the fly based on player complaints.")
    // Pass our executor
    .WithTool(new LuaLlmTool(new MySharedLuaExecutor(sandbox), settings, logger))
    // Allow the agent to change mechanics multiple times in a row
    .WithAllowDuplicateToolCalls(true)
    .WithMode(AgentMode.ToolsOnly)
    .Build();

master.ApplyToPolicy(CoreAIAgent.Policy);

// In-game the player complains it's too hard...
master.Ask("Players say the game is hard. Multiply damage in calculate_damage() by 5!");

// The model will call execute_lua ("function calculate_damage() return 50 end")
// and from the next frame your game damage becomes 50!
```

---

## Agent response modes

### 1. ChatOnly — chat only

The agent **does not use tools**. It only replies with text based on the system prompt and chat history.

**When to use:** `PlayerChat`, storyteller, guide NPC

```csharp
var storyteller = new AgentBuilder("Storyteller")
    .WithSystemPrompt("You are a campfire storyteller. Share tales about the game world.")
    .WithChatHistory()  // Remember prior lines
    .WithMode(AgentMode.ChatOnly)
    .Build();
```

**Behavior:**
- ❌ Does not call tools
- ✅ Replies with text
- ✅ Remembers chat history (if enabled)

---

### 2. ToolsAndChat — tools + chat (default)

The agent **calls tools** when it needs data, then **replies with text** based on the results.

**When to use:** merchant, crafter, advisor, quest giver

```csharp
var merchant = new AgentBuilder("Merchant")
    .WithSystemPrompt("You are a shopkeeper. Check inventory before offering items.")
    .WithTool(new InventoryLlmTool(inventoryProvider))
    .WithMemory()  // Memory: what the player bought
    .WithChatHistory()  // History: prior conversations
    .WithMode(AgentMode.ToolsAndChat)
    .Build();
```

**Behavior:**
- ✅ Calls tools when needed
- ✅ Replies with text based on tool output
- ✅ Remembers memory and chat history
- ✅ By default uses a streaming override for stable tool calling in stream (single-cycle), unless you set `WithStreaming(...)` explicitly

**Example workflow:**
```
Player: "What do you have?"
  ↓
Merchant: {"name": "get_inventory", "arguments": {}}  ← calls tool
  ↓
Tool: [Iron Sword(50), Potion(25), Armor(100)]        ← receives data
  ↓
Merchant: "I have an Iron Sword for 50 coins..."     ← replies from data
```

---

### 3. ToolsOnly — tools only

The agent **only calls tools**. It does not reply with text to the player. Used for background tasks.

**When to use:** background analyzer, auto-crafter, telemetry collector

```csharp
var analyzer = new AgentBuilder("BackgroundAnalyzer")
    .WithSystemPrompt("Analyze session telemetry and detect anomalies.")
    .WithTool(new TelemetryLlmTool(telemetryProvider))
    .WithMode(AgentMode.ToolsOnly)
    .Build();
```

**Behavior:**
- ✅ Calls tools
- ❌ Does not reply with text (or minimal reply)
- ✅ Suited for automated tasks
- ✅ By default uses a streaming override for tool calling in the streaming pipeline, unless you set `WithStreaming(...)` explicitly

---

## Memory vs chat history

### Memory — persistent memory

**What it is:** Long-term agent memory. Persists across sessions.

**Use for:**
- What the player bought
- Which quests were completed
- Important world facts

**Control:**
```csharp
// The model manages memory via tool calls
{"name": "memory", "arguments": {"action": "write", "content": "Player bought Iron Sword"}}
{"name": "memory", "arguments": {"action": "append", "content": "Player is friendly"}}
{"name": "memory", "arguments": {"action": "clear"}}
```

**Enable:**
```csharp
var agent = new AgentBuilder("Merchant")
    .WithMemory()  // Default: Append
    .WithMemory(MemoryToolAction.Write)  // Or: Write (overwrite)
    .Build();
```

---

### ChatHistory — conversation history

**What it is:** Full dialogue context for a role. The framework **automatically** appends user and assistant turns to `IAgentMemoryStore` (same abstraction as MemoryTool). For **LLMUnity**, messages are also fed into `LLMAgent` during the play session so the model sees prior lines.

**Use for:**
- Remember what the player asked five minutes ago
- Context for follow-up replies
- Continuity across multiple `RunTaskAsync` calls in one session

**Enable:**
```csharp
var agent = new AgentBuilder("Storyteller")
    .WithChatHistory()  // In-memory for this process; not written to disk unless you opt in (see below)
    .Build();

// Persist custom chat roles across app restarts (Unity: same JSON files as MemoryTool under persistentDataPath).
// Built-in PlayerChat already has persistent ChatHistory enabled by AgentMemoryPolicy defaults.
var agentPersistent = new AgentBuilder("Teacher")
    .WithChatHistory(persistBetweenSessions: true)
    .Build();
```

**How it works:**
```
Request 1: "Tell me about the forest"
  → Saved in ChatHistory (store + LLMUnity agent history when applicable)

Request 2: "What was the forest about?"
  → ChatHistory is injected into context
  → The agent remembers the prior exchange
```

**Authoritative docs (Unity integration):** see package **[MemorySystem.md](../../CoreAiUnity/Docs/MemorySystem.md)** (architecture) and **[README_CHAT.md](../../CoreAiUnity/Runtime/Source/Features/Chat/README_CHAT.md)** (UI restore, `Load Persisted Chat On Startup`). For custom backends (PlayerPrefs, cloud), see **[MEMORY_STORE_CUSTOM_BACKENDS.md](../../CoreAiUnity/Docs/MEMORY_STORE_CUSTOM_BACKENDS.md)**.

---

### Both together

```csharp
var merchant = new AgentBuilder("Merchant")
    .WithMemory()         // Long-term: what the player bought
    .WithChatHistory()    // Conversation context (enable persist if you need it after restart)
    .Build();
```

| | Memory (MemoryTool) | ChatHistory |
|--|---------------------|-------------|
| **Backed by** | `IAgentMemoryStore` — default Unity: `FileAgentMemoryStore` JSON field `memory` | Same store — field `chatHistoryJson` (plus in-process history for LLMUnity) |
| **Across app restarts** | Yes, when using the default file store (or any persistent `IAgentMemoryStore`) | Yes for built-in **`PlayerChat`** by default; for custom roles use **`WithChatHistory(..., persistBetweenSessions: true)`** (and UI loads history if you use `CoreAiChatPanel`; see README_CHAT) |
| **Control** | Model via `memory` tool call | Automatic append of user/assistant messages |
| **Use for** | Facts, purchases, quests | Conversation context |

---

## Quick Actions and Events (no classes)

### 1. WithEventTool (beginner-friendly)

Lets the agent raise a global `CoreAiEvents` event that any `MonoBehaviour` can subscribe to.

**Agent setup (one line):**
```csharp
var agent = new AgentBuilder("Storyteller")
    .WithEventTool("trigger_scare", "Use this to scare the player suddenly") // No payload
    .WithEventTool("give_gold", "Give gold to player", hasStringPayload: true) // With payload
    .Build();
```

**Any script in the game:**
```csharp
void Start() 
{
    // Agent raised an event with no parameters:
    CoreAiEvents.Subscribe("trigger_scare", () => {
        audioSource.PlayOneShot(jumpscare);
    });

    // Agent raised an event with a parameter:
    CoreAiEvents.Subscribe("give_gold", (payload) => {
        int amount = int.Parse(payload);
        player.AddGold(amount);
    });
}
```

### 2. WithAction (advanced)

Passes any C# `Delegate` (`Action` or `Func`) straight into the agent. **MEAI** parses the delegate arguments and gives the model the correct JSON schema. No custom classes required.

```csharp
var agent = new AgentBuilder("Helper")
    // Parameterless method
    .WithAction("heal_player", "Heals the player fully", () => player.Heal())
    
    // Method with parameters (the agent infers amount(int) and item(string))
    .WithAction("give_item", "Gives an item", (int amount, string item) => {
        inventory.Add(item, amount);
    })
    .Build();
```

> 💡 **How does the model know when to call Action/Event?**
> No special system prompt for triggers is generated — everything goes through standard **tool calling**.
> For the model to call your tool reliably, do two things:
> 1. **Give a clear `description` for the tool.** The model reads it and understands intent (e.g. *"Use this ONLY IF player is dying"*).
> 2. **Spell out rules in the agent's `WithSystemPrompt`.** If you add at least one Action or Event, it is **strongly recommended** to add instructions on when to use that tool. For example:
>    `.WithSystemPrompt("You are a guard. If the player admits to a crime, you MUST call the 'alarm' tool immediately.")`

> ❓ **What's the difference between WithAction and WithEventTool?**
> - **`WithAction`** — wires a specific C# delegate. The agent invokes your method directly (e.g. `() => player.Heal()`). Good for direct actions with a clear outcome.
> - **`WithEventTool`** — only publishes on the `CoreAiEvents` bus via `CoreAiEvents.Publish()`. The agent does not know who handles it. Useful for decoupling: the agent fires `trigger_scare` while handlers live on audio, VFX spawners, etc.

---

## Building a custom tool (via classes)

### Step-by-step

**Step 1: Create a tool class**

```csharp
// Must implement ILlmTool
public class MyTool : ILlmTool
{
    // 1. Unique name (used by the model to invoke)
    public string Name => "my_tool_name";

    // 2. Description (the model reads this to know when to call)
    public string Description => "Description of what the tool does";

    // 3. JSON schema for parameters (if any)
    public string ParametersSchema => "{}"; // No parameters

    // 4. Create AIFunction — this runs when the tool is invoked
    public AIFunction CreateAIFunction()
    {
        return AIFunctionFactory.Create(
            async (CancellationToken ct) =>
            {
                // Your code here
                return new { result = "success" };
            },
            Name,           // Function name
            Description     // Description
        );
    }
}
```

**Step 2: Add the tool to the agent**

```csharp
var agent = new AgentBuilder("MyAgent")
    .WithSystemPrompt("You are an agent with custom tools.")
    .WithTool(new MyTool())  // ← add tool
    .WithMemory()
```

> 💡 **Tool design for token savings:**
> - Use **clear names** (`spawn_quiz`, `get_inventory`) — the model should grasp intent immediately.
> - Keep **short descriptions** (one line) — `Description` is sent on every request.
> - Use **short parameter keys** (`q`, `opts`, `correct` instead of `question_text`, `answer_options`, `correct_answer_indexes`) — can save 30–50% tokens per call.
> - Prefer **indices over strings** (`"correct": [1]` instead of `"correct": ["full answer text"]`).
> - Set **defaults in code** so the model does not fill rarely used fields.
>
> More detail: [TOOL_CALL_SPEC.md](../../CoreAiUnity/Docs/TOOL_CALL_SPEC.md)

### Generation temperature, output tokens, and duplicate tool calls

#### Duplicate tool calls
By default CoreAI **disallows** calling the same tool with identical arguments repeatedly in a row (`AllowDuplicateToolCalls = false`). This protects small local models (2B, 4B) from infinite loops. For stronger models (API or 30B+ local), duplicates can be useful — for example a watchdog agent that polls a status tool until it returns "ready", or an animation agent that legitimately re-fires the same `play_animation` call.

There are **three** layers, evaluated from broadest to narrowest:

| Layer | Where | Default | Effect |
|------|------|------|------|
| Global | `CoreAISettings.AllowDuplicateToolCalls` | `false` (reject) | Baseline for every agent that does not override |
| Per-role | `AgentBuilder.WithAllowDuplicateToolCalls(bool)` | unset → falls back to global | Wins over the global setting |
| Per-tool | `ILlmTool.AllowDuplicates` | `false` | If `true`, that *specific* tool is exempt regardless of role/global setting (used by tools like `world_command` and `execute_lua`) |

Examples:

```csharp
// Strong model that polls a status tool — let it re-call.
var watchdog = new AgentBuilder("Watchdog")
    .WithAllowDuplicateToolCalls(true)
    .Build();

// Small model that occasionally loops — keep the guard on for this agent
// even if the global default is true.
var planner = new AgentBuilder("Programmer")
    .WithAllowDuplicateToolCalls(false)
    .Build();
```

When a duplicate is rejected, the policy returns a synthetic tool result of:

> `Error: You just executed this exact same tool call with the exact same arguments on the previous step. Do not repeat identical steps. Proceed to the NEXT step or provide a final text response.`

The trace surfaces it as `source=duplicate` in the per-call diagnostic line:

```
[ToolCall] traceId=… role=… tool=memory status=FAIL dur=0ms …
LLM ◀ … | tools=[memory(fail,0ms,duplicate)]
```

If you see this line repeatedly, that's the signal to either (a) flip `WithAllowDuplicateToolCalls(true)` for that agent, (b) mark the specific tool with `AllowDuplicates = true`, or (c) tighten the system prompt to stop the model retrying.

> 💡 *Note: For some tools (e.g. `world_command` → `play_animation` or `execute_lua`), duplicates are always allowed at the tool level.*

#### Generation temperature

Temperature controls **creativity**. The global default is `CoreAISettings.Temperature` (default **0.1**), but you can override per agent.

| Value | Behavior | When to use |
|----------|-----------|-------------------|
| `0.0` | Fully deterministic | Strict JSON, code, math |
| `0.1` | Minimal variance | **Default** — tool calling, crafting |
| `0.3` | Light variance | NPC dialogue, analytics |
| `0.7` | Creative | Storyteller, content generation |
| `1.0+` | Maximum randomness | Rarely, creative tasks only |

```csharp
// Low temperature (strict JSON)
var mechanic = new AgentBuilder("CoreMechanic")
    .WithSystemPrompt("Calculate crafting stats. Output JSON only.")
    .WithTemperature(0.0f)  // Always deterministic
    .Build();

// Typical NPC dialogue temperature
var npc = new AgentBuilder("Guard")
    .WithSystemPrompt("You are a city guard. Greet players.")
    .WithTemperature(0.3f)  // Light variance
    .WithChatHistory()
    .Build();

// No override — uses global temperature (0.1)
var creator = new AgentBuilder("Creator")
    .WithSystemPrompt("You are the Creator agent...")
    .Build();  // Temperature = 0.1 from CoreAISettings
```

> 💡 **Tip:** for tool calling use `0.0–0.2`. Higher temperature makes the model more likely to “improvise” instead of following the format.

#### Per-agent output token budget

Use `WithMaxOutputTokens(int? tokens)` when a role needs a stable response length without setting `AiTaskRequest.MaxOutputTokens` on every call.

```csharp
var shortNpc = new AgentBuilder("Guard")
    .WithSystemPrompt("You are a city guard. Reply in one or two short sentences.")
    .WithMaxOutputTokens(128)
    .Build();

var planner = new AgentBuilder("QuestPlanner")
    .WithSystemPrompt("Plan quest beats with concise bullet points.")
    .WithMaxOutputTokens(1024)
    .Build();
```

Priority through the orchestrator is: `AiTaskRequest.MaxOutputTokens` (per-call) → `AgentBuilder.WithMaxOutputTokens` (per-agent) → `CoreAISettings.MaxTokens` (global) → provider default. Direct `LlmCompletionRequest.MaxOutputTokens` still wins when you call an `ILlmClient` yourself.

**Step 3: The model calls the tool when needed**

When the model decides your tool is needed, it returns:
```json
{"name": "my_tool_name", "arguments": {}}
```

CoreAI recognizes this, runs `MyTool.CreateAIFunction()`, and returns the result to the model.

---

### Basic tool (no parameters)

```csharp
public class WeatherLlmTool : ILlmTool
{
    private readonly IWeatherProvider _weather;

    public WeatherLlmTool(IWeatherProvider weather)
    {
        _weather = weather;
    }

    public string Name => "get_weather";
    
    public string Description => "Get current weather in the game world.";
    
    public string ParametersSchema => "{}";

    public AIFunction CreateAIFunction()
    {
        return AIFunctionFactory.Create(
            async (CancellationToken ct) => 
            {
                var weather = await _weather.GetCurrentAsync(ct);
                return new { weather.Temperature, weather.Condition, weather.IsRaining };
            },
            "get_weather",
            "Get current weather in the game world.");
    }
}
```

### Tool with parameters

```csharp
public class CraftItemTool : ILlmTool
{
    public string Name => "craft_item";
    
    public string Description => "Craft an item from ingredients.";
    
    public string ParametersSchema => 
        "{" +
        "  \"type\": \"object\"," +
        "  \"properties\": {" +
        "    \"ingredient1\": {\"type\": \"string\", \"description\": \"First ingredient\"}," +
        "    \"ingredient2\": {\"type\": \"string\", \"description\": \"Second ingredient\"}" +
        "  }," +
        "  \"required\": [\"ingredient1\", \"ingredient2\"]" +
        "}";

    public AIFunction CreateAIFunction()
    {
        return AIFunctionFactory.Create(
            async (string ingredient1, string ingredient2, CancellationToken ct) => 
            {
                var result = await CraftingSystem.CraftAsync(ingredient1, ingredient2, ct);
                return new { result.ItemName, result.Quality, result.Success };
            },
            "craft_item",
            "Craft an item from two ingredients.");
    }
}
```

---

## Full examples

### Merchant with full setup

```csharp
public static class MyGameAgents
{
    public static AgentConfig CreateMerchant(IInventoryProvider inventory)
    {
        return new AgentBuilder("Merchant")
            .WithSystemPrompt(@"You are a shopkeeper NPC. 
When player asks to buy or browse, FIRST call get_inventory tool.
Then respond in-character with items and prices.
Remember what the player bought using memory.")
            .WithTool(new InventoryLlmTool(inventory))
            .WithMemory(MemoryToolAction.Append)
            .WithChatHistory()
            .WithMode(AgentMode.ToolsAndChat)
            .Build();
    }

    public static AgentConfig CreateQuestGiver(IQuestProvider quests)
    {
        return new AgentBuilder("QuestGiver")
            .WithSystemPrompt(@"You give quests to players.
When player asks for quests, call get_quests tool.
Track completed quests in memory.")
            .WithTool(new QuestsLlmTool(quests))
            .WithMemory(MemoryToolAction.Append)
            .WithChatHistory()
            .WithMode(AgentMode.ToolsAndChat)
            .Build();
    }

    public static AgentConfig CreateStoryteller()
    {
        return new AgentBuilder("Storyteller")
            .WithSystemPrompt("You are a campfire storyteller. Share tales about the world.")
            .WithChatHistory()
            .WithMode(AgentMode.ChatOnly)
            .Build();
    }

    public static AgentConfig CreateBackgroundAnalyzer(ITelemetryProvider telemetry)
    {
        return new AgentBuilder("BackgroundAnalyzer")
            .WithSystemPrompt("Analyze telemetry and detect anomalies.")
            .WithTool(new TelemetryLlmTool(telemetry))
            .WithMode(AgentMode.ToolsOnly)
            .Build();
    }
}
```

### Registering in the game

```csharp
void SetupAgents()
{
    var policy = new AgentMemoryPolicy();
    
    // Custom agents
    MyGameAgents.CreateMerchant(GameServices.Inventory).ApplyToPolicy(policy);
    MyGameAgents.CreateQuestGiver(GameServices.Quests).ApplyToPolicy(policy);
    MyGameAgents.CreateStoryteller().ApplyToPolicy(policy);
    MyGameAgents.CreateBackgroundAnalyzer(GameServices.Telemetry).ApplyToPolicy(policy);
    
    // Store policy in the DI container
    container.RegisterInstance(policy);
}
```

### Calling an agent

```csharp
async Task AskMerchant(string playerMessage)
{
    var orch = container.Resolve<AiOrchestrator>();
    // Response comes directly from AskAsync:
    string response = await merchant.AskAsync(orch, playerMessage);
    Debug.Log(response);
}
```

---

## API reference

### AgentBuilder

| Method | Description | Example |
|-------|----------|--------|
| `WithSystemPrompt(string)` | Set system prompt | `.WithSystemPrompt("You are...")` |
| `WithTool(ILlmTool)` | Add a tool | `.WithTool(new InventoryLlmTool(...))` |
| `WithTools(IEnumerable<ILlmTool>)` | Add multiple tools | `.WithTools(tools)` |
| `WithAction(string, string, Delegate)` | ADD tool from C# delegate | `.WithAction("heal", "desc", () => Heal())` |
| `WithEventTool(string, string, bool)` | ADD tool that publishes an event | `.WithEventTool("alarm", "desc")` |
| `WithMemory(MemoryToolAction)` | Enable memory | `.WithMemory()` or `.WithMemory(MemoryToolAction.Write)` |
| `WithChatHistory()` | Enable chat history | `.WithChatHistory()` |
| `WithTemperature(float)` | Override temperature | `.WithTemperature(0.0f)` |
| `WithMaxOutputTokens(int?)` | Override response token budget | `.WithMaxOutputTokens(256)` |
| `WithMode(AgentMode)` | Set mode | `.WithMode(AgentMode.ToolsAndChat)` |
| `WithAllowDuplicateToolCalls(bool)` | Allow repeated identical tool calls | `.WithAllowDuplicateToolCalls(true)` |
| `Build()` | Build `AgentConfig` | `.Build()` |

### AgentConfig

| Property | Type | Description |
|----------|-----|----------|
| `RoleId` | string | Unique agent ID |
| `SystemPrompt` | string | System prompt (with Universal Prefix if configured) |
| `Tools` | IReadOnlyList<ILlmTool> | Tool list |
| `Mode` | AgentMode | Operating mode |
| `Temperature` | float | Generation temperature |
| `MaxOutputTokens` | int? | Per-agent response token cap; null = fallback |

| Method | Description | Example |
|-------|----------|--------|
| `ApplyToPolicy(policy)` | Register agent in policy | `merchant.ApplyToPolicy(CoreAIAgent.Policy)` |
| `Ask(message, onDone?)` | 🟢 Fire-and-forget, optional `Action<string>` | `merchant.Ask("Hi", (s) => print(s))` |
| `AskAsync(message)` | 🟡 Async (returns `Task<string>`) | `await merchant.AskAsync("Hi")` |
| `AskAsync(orch, message)` | 🔴 Async with explicit orchestrator | `await merchant.AskAsync(orch, "Hi")` |

### CoreAI (static facade)

| Property | Type | Description |
|----------|-----|----------|
| `CoreAIAgent.Orchestrator` | IAiOrchestrationService | Global orchestrator (auto-init) |
| `CoreAIAgent.Policy` | AgentMemoryPolicy | Global policy (auto-init) |

### AgentMode

| Value | Description |
|----------|----------|
| `ToolsOnly` | Tools only (no text) |
| `ToolsAndChat` | Tools + text (default) |
| `ChatOnly` | Text only (no tools) |

### MemoryToolAction

| Value | Description |
|----------|----------|
| `Write` | Replace memory entirely |
| `Append` | Append to existing memory |
| `Clear` | Clear memory |

---

## Architecture

```
┌──────────────────────────────────────────────────────────────┐
│                       AgentBuilder                            │
├──────────────────────────────────────────────────────────────┤
│  new AgentBuilder("Merchant")                                │
│    .WithSystemPrompt("You are a shopkeeper...")  ← prompt    │
│    .WithTool(new InventoryLlmTool(...))          ← tools     │
│    .WithMemory()                                 ← memory     │
│    .WithChatHistory()                            ← history    │
│    .WithMode(AgentMode.ToolsAndChat)             ← mode       │
│    .Build()                                        ↓         │
└──────────────────────────────────────────────────────────────┘
                              ↓
┌──────────────────────────────────────────────────────────────┐
│                       AgentConfig                             │
├──────────────────────────────────────────────────────────────┤
│  RoleId: "Merchant"                                          │
│  SystemPrompt: "You are a shopkeeper..."                     │
│  Tools: [InventoryLlmTool, MemoryLlmTool]                    │
│  Mode: ToolsAndChat                                          │
└──────────────────────────────────────────────────────────────┘
                              ↓
┌──────────────────────────────────────────────────────────────┐
│               AgentConfig.ApplyToPolicy(policy)               │
├──────────────────────────────────────────────────────────────┤
│  policy.SetToolsForRole("Merchant", [tools])                 │
│  policy.EnableMemoryTool("Merchant")                         │
│  policy.EnableChatHistory("Merchant")                        │
└──────────────────────────────────────────────────────────────┘
                              ↓
┌──────────────────────────────────────────────────────────────┐
│                    AiOrchestrator                              │
├──────────────────────────────────────────────────────────────┤
│  RunTaskAsync("Merchant", "What do you have?")               │
│    ↓                                                          │
│  → FunctionInvokingChatClient → tools=[inventory, memory]    │
│    ↓                                                          │
│  → Model: {"name": "get_inventory", "arguments": {}}         │
│    ↓                                                          │
│  → InventoryTool executes → [Iron Sword(50), Potion(25)]     │
│    ↓                                                          │
│  → Model: "I have Iron Sword for 50 coins..."                │
│    ↓                                                          │
│  → ChatHistory saves: user + assistant messages              │
└──────────────────────────────────────────────────────────────┘
```

---

## Testing

```csharp
[Test]
public void AgentBuilder_CreatesAgent_WithAllSettings()
{
    var config = new AgentBuilder("TestAgent")
        .WithSystemPrompt("Test prompt")
        .WithTool(new MemoryLlmTool())
        .WithChatHistory()
        .WithMode(AgentMode.ToolsAndChat)
        .Build();

    Assert.AreEqual("TestAgent", config.RoleId);
    Assert.AreEqual("Test prompt", config.SystemPrompt);
    Assert.AreEqual(1, config.Tools.Count);
    Assert.AreEqual(AgentMode.ToolsAndChat, config.Mode);
}
```

---

## Settings

### CoreAISettings

```csharp
// Before system init:
CoreAISettings.MaxLuaRepairRetries = 5;        // Max consecutive failed Lua repairs (default 3)
CoreAISettings.MaxToolCallRetries = 5;      // Max consecutive failed tool calls (default 3)
CoreAISettings.EnableMeaiDebugLogging = true; // MEAI debug logging
CoreAISettings.LlmRequestTimeoutSeconds = 600; // LLM timeout (default 300)
```

### Tool call retry

If the model fails to emit a tool call in the correct format, the system automatically retries **3 times** (by default):

```
Attempt 1: Model returns wrong format
  ↓
System: "ERROR: Tool call not recognized. Use this format: {"name": "...", "arguments": {...}}"
  ↓
Attempt 2: Model retries
  ↓
(If still wrong)
  ↓
Attempt 3: Final attempt
  ↓
(If still wrong - accepts response as is)
```

This helps small models (e.g. Qwen3.5-2B) that sometimes forget the format.

---

## Recommended models

| Model | Size | Tool calling | When to use |
|--------|--------|--------------|-------------------|
| **Qwen3.5-4B** | 4B | ✅ Strong | **Recommended** for local runs |
| **Qwen3.5-35B (MoE) API** | 35B/3A | ✅ Excellent | **Ideal** via API — fast and accurate |
| **Gemma 4 26B** | 26B | ✅ Excellent | Great via LM Studio / HTTP API |
| Qwen3.5-2B | 2B | ⚠️ Works | Works but can err on multi-step |
| Qwen3.5-0.8B | 0.8B | ⚠️ Basic | Most tests pass; multi-step is harder |

> 🏆 **Qwen3.5-4B passes ALL PlayMode tests.** Recommended minimum for production.
> 💡 MoE models activate only part of the parameters (3B) — fast like 4B, accurate like 35B.

---

## Troubleshooting

### Agent does not call tools
- Ensure `Mode` is `AgentMode.ToolsAndChat` or `ToolsOnly`
- Confirm tools are passed via `.WithTool()`
- Check the system prompt — the model must know about the tools

### Memory does not persist
- Ensure `.WithMemory()` is called
- Verify the model calls the memory tool: `{"name": "memory", ...}`
- Enable logging: `GameLogger.SetFeatureEnabled(GameLogFeature.Llm, true)`

### Chat history does not work
- Ensure `.WithChatHistory()` is called on the role
- For **LLMUnity**, history is mirrored into `LLMAgent` during the session — if the list is empty, confirm the client was created with chat history enabled for that role
- For **persistence after closing the game**, built-in `PlayerChat` is persistent by default. For custom roles, use `.WithChatHistory(persistBetweenSessions: true)` and a persistent `IAgentMemoryStore` (default: `FileAgentMemoryStore`). Chat is **not** written to disk when `persistBetweenSessions` is false — only in-memory for that process
- UI restore: see **[README_CHAT.md](../../CoreAiUnity/Runtime/Source/Features/Chat/README_CHAT.md)** (`Load Persisted Chat On Startup`, role policy)
