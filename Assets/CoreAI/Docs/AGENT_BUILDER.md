# рџЏ—пёЏ Agent Builder вЂ” Custom Agent Constructor

## Overview

**AgentBuilder** is a fluent API for quickly creating custom agents with unique tools, prompts, and operating modes. It makes it easy to add new NPCs to a game without changing the CoreAI core.

### Capabilities

- вњ… **Unique tools** вЂ” any `ILlmTool` for a specific agent
- вњ… **Skills** вЂ” named tool+instruction groups with per-request activation (**v2.0+**)
- вњ… **Three response modes** вЂ” `ChatOnly`, `ToolsAndChat`, `ToolsOnly`
- вњ… **Memory** вЂ” persistent agent memory (write/append/clear)
- вњ… **Chat history** вЂ” automatic saving of conversation context
- вњ… **Per-agent output budget** вЂ” `WithMaxOutputTokens(...)` for roles that should stay short or verbose
- вњ… **Minimal code** вЂ” 3вЂ“5 lines per agent
- вњ… **Single MEAI pipeline** вЂ” the same tool calling for HTTP API and LLMUnity

---

## Skills (v2.1)

`SkillSet` вЂ” a group of tools + instructions that the model loads **on demand** via two meta-tools:
- `read_skill(skill_name)` вЂ” load instructions + tool schemas
- `call_skill_tool(tool_name, arguments_json)` вЂ” execute a skill's tool

The model always sees exactly **2 meta-tools** regardless of how many skills/tools exist. This keeps the token count constant even with hundreds of tools across dozens of skills.

### Creating skills

```csharp
// name, description (catalog), instructions (loaded via read_skill), tools
var crafting = new SkillSet("Crafting",
    "Forge weapons and armor from raw materials",
    "1. Call get_recipes to see available recipes.\n" +
    "2. Check inventory via check_inventory.\n" +
    "3. Craft via craft_item with recipe_id and quality.",
    new DelegateLlmTool("get_recipes", "List recipes", (string type) => ...),
    new DelegateLlmTool("craft_item", "Craft an item", (string id, float q) => ...));

// Without instructions вЂ” model relies on tool descriptions
var combat = new SkillSet("Combat", "Fight enemies",
    new DelegateLlmTool("attack", "Attack target", (string target) => ...));
```

### From file

```csharp
// Instructions from a .txt file
var quiz = SkillSet.FromFile("Quiz", "Quizzes and tests",
    "Assets/Skills/quiz.txt", tool1, tool2);

// From TextAsset (Resources, WebGL)
var quiz = SkillSet.FromTextContent("Quiz", "Quizzes", textAsset.text, tool1, tool2);
```

### SkillSetAsset вЂ” via Inspector

`Create в†’ CoreAI в†’ Skill Set Asset` вЂ” ScriptableObject for designers.

| Field | Purpose |
|-------|---------|
| **Skill Name** | Name shown in the catalog |
| **Description** | Short one-liner |
| **Instructions Asset** | `.txt` / `.md` TextAsset with full instructions |
| **Inline Instructions** | Or type directly in Inspector (used if TextAsset is null) |

```csharp
[SerializeField] SkillSetAsset craftingAsset;

void Start()
{
    SkillSet skill = craftingAsset.BuildSkillSet(
        new DelegateLlmTool("get_recipes", "List recipes", (string type) => ...),
        new DelegateLlmTool("craft_item", "Craft item", (string id) => ...));

    new AgentBuilder("GameMaster")
        .WithSkill(skill)
        .Build()
        .ApplyToPolicy(policy);
}
```

### Registering skills

```csharp
var gm = new AgentBuilder("GameMaster")
    .WithSystemPrompt("You are a Game Master. Read the relevant skill before using its tools.")
    .WithSkill(crafting)
    .WithSkill(combat)
    .WithMode(AgentMode.ToolsAndChat)
    .Build();
gm.ApplyToPolicy(policy);

// Or in bulk:
new AgentBuilder("RPG").WithSkills(crafting, combat, lore).Build();
```

### Invoking

```csharp
await orch.RunTaskAsync(new AiTaskRequest {
    RoleId = "GameMaster",
    Hint = "Craft me an iron sword"
});
// Model: sees catalog в†’ read_skill("Crafting") в†’ call_skill_tool("get_recipes", "{}") в†’ response
```

### How it works

1. `WithSkill()` stores the `SkillSet` вЂ” tools are **not** added to the model's tool list
2. `ApplyToPolicy()` registers `SkillRuntimeContextProvider` (catalog in prompt) + `read_skill` + `call_skill_tool`
3. Model sees the catalog (skill names + descriptions), calls `read_skill(name)` to load instructions + tool schemas
4. Model calls `call_skill_tool(tool_name, arguments_json)` to execute tools through the proxy
5. Token overhead: **constant** (2 meta-tools) regardless of skill/tool count

### API

| Member | Description |
|--------|-------------|
| `Name` | Skill name |
| `Description` | Short description (catalog) |
| `Instructions` | Full instructions (on-demand) |
| `Tools` | Tools in this skill |
| `ToolNames` | `string[]` of tool names |
| `MergeToolNames(params SkillSet[])` | Merge names from multiple skills |
| `FromFile(name, desc, path, tools)` | Create from file |
| `FromTextContent(name, desc, text, tools)` | Create from text |

### Best practices вЂ” when to use skills vs direct tools

**Use `WithSkill()` when:**
- The agent has **many tools** it doesn't always need (e.g. 5 skills Г— 4 tools = 20 tools, but any single request uses 2вЂ“4)
- Tools require **detailed instructions** (complex protocols, multi-step workflows, secret parameters)
- You want to **reduce context window usage** вЂ” only 2 meta-tools are sent regardless of total tool count
- Different game scenarios activate **different tool subsets** (crafting vs combat vs trading)

**Use `WithTool()` directly when:**
- The agent has **few tools** (1вЂ“3) that it always needs
- The tool is simple enough that its description is self-sufficient
- You need **minimum latency** вЂ” direct tools skip the read_skill в†’ call_skill_tool round-trip

**Mixing both:**
```csharp
var agent = new AgentBuilder("GameMaster")
    .WithTool(memoryTool)            // always needed в†’ direct
    .WithSkill(craftingSkill)        // used sometimes в†’ on-demand
    .WithSkill(combatSkill)          // used sometimes в†’ on-demand
    .Build();
// Model sees: memory (direct) + read_skill + call_skill_tool = 3 tools total
```

### Context optimization tips

| Technique | Savings | How |
|-----------|---------|-----|
| **Skills for rarely-used tools** | ~50-100 tokens per hidden tool | Move tools into skills, model loads only what it needs |
| **Short tool descriptions** | ~10-20 tokens per tool | Use concise descriptions: "List recipes" not "Returns a list of all available crafting recipes in JSON format" |
| **File-based instructions** | 0 tokens until read | `SkillSet.FromFile()` вЂ” instructions load only when `read_skill` is called |
| **Skill grouping** | Fewer read_skill calls | Group related tools into one skill (e.g. all crafting tools together) |

**Token math example:**
- 10 skills Г— 5 tools = 50 tools
- Without skills: ~50 tools Г— ~80 tokens/tool = **~4,000 tokens** per request
- With skills: 2 meta-tools Г— ~80 tokens + catalog ~200 tokens = **~360 tokens** per request
- **Saving: ~91%** of tool-related context

### How the proxy works (for advanced users)

```
User: "Craft me an iron sword"
  в†“
Model sees system prompt with catalog:
  - Crafting вЂ” Forge weapons and armor
  - Combat вЂ” Fight enemies
  в†“
Model calls: read_skill("Crafting")
  в†’ Returns: instructions + tool schemas:
    { tool_name: "get_recipes", parameters: [{name: "type", type: "string"}] }
    { tool_name: "craft_item", parameters: [{name: "recipe_id", type: "string"}] }
  в†“
Model calls: call_skill_tool("get_recipes", "{\"type\": \"sword\"}")
  в†’ Proxy finds get_recipes delegate, parses JSON, invokes it
  в†’ Returns: [{recipe_id: "iron_sword_01", materials: [...]}]
  в†“
Model calls: call_skill_tool("craft_item", "{\"recipe_id\": \"iron_sword_01\"}")
  в†’ Proxy routes to craft_item
  в†’ Returns: {success: true, item: "Iron Sword"}
  в†“
Model: "Your Iron Sword has been forged! вљ”пёЏ"
```

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
Unity в†’ Create в†’ CoreAI в†’ CoreAI Settings
```

In the Inspector, choose **LLM Backend**:
- **Auto** вЂ” picks LLMUnity or HTTP API automatically
- **LlmUnity** вЂ” local GGUF model
- **OpenAiHttp** вЂ” HTTP API (LM Studio, OpenAI, Qwen)
- **Offline** вЂ” no model (stub)

### 3. Invoke the agent

**рџџў Primary вЂ” `AskAsync` (await):**

```csharp
// Returns the model's text response:
string response = await merchant.AskAsync("Show me your swords");
Debug.Log(response);

// With an explicit orchestrator (for tests / custom setups):
var orch = container.Resolve<IAiOrchestrationService>();
string response2 = await merchant.AskAsync(orch, "Show me your swords");
```

**рџџЎ Convenience вЂ” `AskWithCallback` (fire-and-forget, no async):**

```csharp
// One line! No await, no container. Errors are logged, not thrown.
merchant.AskWithCallback("Show me your swords");

// With a callback when done (receives the text response):
merchant.AskWithCallback("Show me your swords", (response) => Debug.Log(response));
```

> рџ’Ў Both use the global `CoreAIAgent.Orchestrator` вЂ” it auto-initializes at scene start with `CoreAILifetimeScope`. `AskWithCallback` is for callback-style call sites (UI buttons, UnityEvents); the old `Ask(...)` is an `[Obsolete]` alias of it.
> The callback is marshaled to the caller's `SynchronizationContext` when one exists (for example, the Unity main thread); when called from a thread without a `SynchronizationContext`, the callback may run on a background thread and must not touch `UnityEngine` APIs.

**рџ”ґ Advanced вЂ” full control:**

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

> рџ›ЎпёЏ **Built-in spam protection (call cancellation):**
> Both methods (`AskWithCallback` and `AskAsync`) automatically pass `CancellationScope = Agent.RoleId` to the orchestrator.
> That means **if you call `merchant.AskWithCallback()` again while the first request is still generating, the old request is forcibly stopped (Cancelled)** and the new one runs. This saves CPU and tokens on double-clicks or message spam to the same NPC.

---

## рџ“‹ Ready-made recipes вЂ” copy and use

> рџ’Ў Each recipe is **complete** working code. Copy, rename, done.

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
blacksmith.AskWithCallback("What do you have?");
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
storyteller.AskWithCallback("Tell me a story", (s) => Debug.Log(s));
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
master.AskWithCallback("Players say the game is hard. Multiply damage in calculate_damage() by 5!");

// The model will call execute_lua ("function calculate_damage() return 50 end")
// and from the next frame your game damage becomes 50!
```

---

## Agent response modes

### 1. ChatOnly вЂ” chat only

The agent **does not use tools**. It only replies with text based on the system prompt and chat history.

**When to use:** `PlainChat`, `SmartChat`, storyteller, guide NPC

```csharp
var storyteller = new AgentBuilder("Storyteller")
    .WithSystemPrompt("You are a campfire storyteller. Share tales about the game world.")
    .WithChatHistory()  // Remember prior lines
    .WithMode(AgentMode.ChatOnly)
    .Build();
```

**Behavior:**
- вќЊ Does not call tools
- вњ… Replies with text
- вњ… Remembers chat history (if enabled)

---

### 2. ToolsAndChat вЂ” tools + chat (default)

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
- вњ… Calls tools when needed
- вњ… Replies with text based on tool output
- вњ… Remembers memory and chat history
- вњ… By default uses a streaming override for stable tool calling in stream (single-cycle), unless you set `WithStreaming(...)` explicitly

**Example workflow:**
```
Player: "What do you have?"
  в†“
Merchant: {"name": "get_inventory", "arguments": {}}  в†ђ calls tool
  в†“
Tool: [Iron Sword(50), Potion(25), Armor(100)]        в†ђ receives data
  в†“
Merchant: "I have an Iron Sword for 50 coins..."     в†ђ replies from data
```

---

### 3. ToolsOnly вЂ” tools only

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
- вњ… Calls tools
- вќЊ Does not reply with text (or minimal reply)
- вњ… Suited for automated tasks
- вњ… By default uses a streaming override for tool calling in the streaming pipeline, unless you set `WithStreaming(...)` explicitly

---

## Memory vs chat history

### Memory вЂ” persistent memory

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

### ChatHistory вЂ” conversation history

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
// Built-in PlainChat / SmartChat already have persistent ChatHistory enabled by AgentMemoryPolicy defaults.
var agentPersistent = new AgentBuilder("Teacher")
    .WithChatHistory(persistBetweenSessions: true)
    .Build();
```

**How it works:**
```
Request 1: "Tell me about the forest"
  в†’ Saved in ChatHistory (store + LLMUnity agent history when applicable)

Request 2: "What was the forest about?"
  в†’ ChatHistory is injected into context
  в†’ The agent remembers the prior exchange
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
| **Backed by** | `IAgentMemoryStore` вЂ” default Unity: `FileAgentMemoryStore` JSON field `memory` | Same store вЂ” field `chatHistoryJson` (plus in-process history for LLMUnity) |
| **Across app restarts** | Yes, when using the default file store (or any persistent `IAgentMemoryStore`) | Yes for built-in **`PlainChat`** / **`SmartChat`** by default; for custom roles use **`WithChatHistory(..., persistBetweenSessions: true)`** (and UI loads history if you use `CoreAiChatPanel`; see README_CHAT) |
| **Control** | Model via `memory` tool call | Automatic append of user/assistant messages |
| **Use for** | Facts, purchases, quests | Conversation context |

---

## Quick Actions and Events (no classes)

**Recommendation:** start with **`WithAction`** whenever the tool maps to a concrete C# callback вЂ” **MEAI** infers the JSON schema from the delegate. Use **`WithEventTool`** when you want loose coupling via **`CoreAiEvents`**. Reserve a custom **`ILlmTool`** class ([next section](#building-a-custom-tool-via-classes)) for advanced control (custom schemas, portability, or non-delegate wiring).

### 1. WithAction (recommended for direct C# tools)

Passes any C# `Delegate` (`Action` or `Func`) into the agent pipeline. **Microsoft.Extensions.AI** builds the tool schema from the delegate parameters вЂ” no handwritten JSON Schema for normal cases.

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

### 2. WithEventTool (decoupled events)

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

> рџ’Ў **How does the model know when to call Action/Event?**
> No special system prompt for triggers is generated вЂ” everything goes through standard **tool calling**.
> For the model to call your tool reliably, do two things:
> 1. **Give a clear `description` for the tool.** The model reads it and understands intent (e.g. *"Use this ONLY IF player is dying"*).
> 2. **Spell out rules in the agent's `WithSystemPrompt`.** If you add at least one Action or Event, it is **strongly recommended** to add instructions on when to use that tool. For example:
>    `.WithSystemPrompt("You are a guard. If the player admits to a crime, you MUST call the 'alarm' tool immediately.")`

> вќ“ **What's the difference between WithAction and WithEventTool?**
> - **`WithAction`** вЂ” wires a specific C# delegate. The agent invokes your method directly (e.g. `() => player.Heal()`). Good for direct actions with a clear outcome.
> - **`WithEventTool`** вЂ” only publishes on the `CoreAiEvents` bus via `CoreAiEvents.Publish()`. The agent does not know who handles it. Useful for decoupling: the agent fires `trigger_scare` while handlers live on audio, VFX spawners, etc.

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

    // 4. Create AIFunction вЂ” this runs when the tool is invoked
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
    .WithTool(new MyTool())  // в†ђ add tool
    .WithMemory()
```

> рџ’Ў **Tool design for token savings:**
> - Use **clear names** (`spawn_quiz`, `get_inventory`) вЂ” the model should grasp intent immediately.
> - Keep **short descriptions** (one line) вЂ” `Description` is sent on every request.
> - Use **short parameter keys** (`q`, `opts`, `correct` instead of `question_text`, `answer_options`, `correct_answer_indexes`) вЂ” can save 30вЂ“50% tokens per call.
> - Prefer **indices over strings** (`"correct": [1]` instead of `"correct": ["full answer text"]`).
> - Set **defaults in code** so the model does not fill rarely used fields.
>
> More detail: [TOOL_CALL_SPEC.md](../../CoreAiUnity/Docs/TOOL_CALL_SPEC.md)

### Generation temperature, output tokens, and duplicate tool calls

#### Duplicate tool calls
By default CoreAI **disallows** calling the same tool with identical arguments repeatedly in a row (`AllowDuplicateToolCalls = false`). This protects small local models (2B, 4B) from infinite loops. For stronger models (API or 30B+ local), duplicates can be useful вЂ” for example a watchdog agent that polls a status tool until it returns "ready", or an animation agent that legitimately re-fires the same `play_animation` call.

There are **three** layers, evaluated from broadest to narrowest:

| Layer | Where | Default | Effect |
|------|------|------|------|
| Global | `CoreAISettings.AllowDuplicateToolCalls` | `false` (reject) | Baseline for every agent that does not override |
| Per-role | `AgentBuilder.WithAllowDuplicateToolCalls(bool)` | unset в†’ falls back to global | Wins over the global setting |
| Per-tool | `ILlmTool.AllowDuplicates` | `false` | If `true`, that *specific* tool is exempt regardless of role/global setting (used by tools like `world_command` and `execute_lua`) |

Examples:

```csharp
// Strong model that polls a status tool вЂ” let it re-call.
var watchdog = new AgentBuilder("Watchdog")
    .WithAllowDuplicateToolCalls(true)
    .Build();

// Small model that occasionally loops вЂ” keep the guard on for this agent
// even if the global default is true.
var planner = new AgentBuilder("Programmer")
    .WithAllowDuplicateToolCalls(false)
    .Build();
```

When a duplicate is rejected, the policy returns a synthetic tool result of:

> `Error: You just executed this exact same tool call with the exact same arguments on the previous step. Do not repeat identical steps. Proceed to the NEXT step or provide a final text response.`

The trace surfaces it as `source=duplicate` in the per-call diagnostic line:

```
[ToolCall] traceId=вЂ¦ role=вЂ¦ tool=memory status=FAIL dur=0ms вЂ¦
LLM в—Ђ вЂ¦ | tools=[memory(fail,0ms,duplicate)]
```

If you see this line repeatedly, that's the signal to either (a) flip `WithAllowDuplicateToolCalls(true)` for that agent, (b) mark the specific tool with `AllowDuplicates = true`, or (c) tighten the system prompt to stop the model retrying.

> рџ’Ў *Note: For some tools (e.g. `world_command` в†’ `play_animation` or `execute_lua`), duplicates are always allowed at the tool level.*

#### Generation temperature

Temperature controls **creativity**. The global default is `CoreAISettings.Temperature` (default **0.1**), but you can override per agent.

| Value | Behavior | When to use |
|----------|-----------|-------------------|
| `0.0` | Fully deterministic | Strict JSON, code, math |
| `0.1` | Minimal variance | **Default** вЂ” tool calling, crafting |
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

// No override вЂ” uses global temperature (0.1)
var creator = new AgentBuilder("Creator")
    .WithSystemPrompt("You are the Creator agent...")
    .Build();  // Temperature = 0.1 from CoreAISettings
```

> рџ’Ў **Tip:** for tool calling use `0.0вЂ“0.2`. Higher temperature makes the model more likely to вЂњimproviseвЂќ instead of following the format.

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

Priority through the orchestrator is: `AiTaskRequest.MaxOutputTokens` (per-call) в†’ `AgentBuilder.WithMaxOutputTokens` (per-agent) в†’ `CoreAISettings.MaxTokens` (global) в†’ provider default. Direct `LlmCompletionRequest.MaxOutputTokens` still wins when you call an `ILlmClient` yourself.

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
| `AskWithCallback(message, onDone?)` | рџџў Fire-and-forget convenience, optional `Action<string>` | `merchant.AskWithCallback("Hi", (s) => print(s))` |
| `AskAsync(message)` | рџџЎ Async (returns `Task<string>`) | `await merchant.AskAsync("Hi")` |
| `AskAsync(orch, message)` | рџ”ґ Async with explicit orchestrator | `await merchant.AskAsync(orch, "Hi")` |

> рџ’Ў The primary idiom is awaitable `AskAsync`; `AskWithCallback` exists for callback-style call sites (UnityEvents, legacy code). The old `Ask(message, onDone?)` still compiles but is `[Obsolete]`.
> The callback is marshaled to the caller's `SynchronizationContext` when one exists (for example, the Unity main thread); when called from a thread without a `SynchronizationContext`, the callback may run on a background thread and must not touch `UnityEngine` APIs.

### RoleId (typed role identifiers)

Roles are plain strings under the hood, but instead of magic literals use the `RoleId` struct or `BuiltInAgentRoleIds` constants. `RoleId` converts implicitly to/from `string`, so it works everywhere a role string is expected:

```csharp
var merchant = new AgentBuilder(RoleId.Merchant) ... ;   // built-in role
var custom   = new AgentBuilder(new RoleId("Blacksmith")) ... ; // custom role
await CoreAi.AskAsync("Hi", roleId: RoleId.SmartChat);
```

Built-in statics: `RoleId.Creator`, `RoleId.Analyzer`, `RoleId.Programmer`, `RoleId.AiNpc`, `RoleId.CoreMechanic`, `RoleId.PlainChat`, `RoleId.SmartChat`, `RoleId.Merchant`. `roleId.IsBuiltIn` tells whether the id matches a built-in role.

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
в”Њв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”ђ
в”‚                       AgentBuilder                            в”‚
в”њв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”¤
в”‚  new AgentBuilder("Merchant")                                в”‚
в”‚    .WithSystemPrompt("You are a shopkeeper...")  в†ђ prompt    в”‚
в”‚    .WithTool(new InventoryLlmTool(...))          в†ђ tools     в”‚
в”‚    .WithMemory()                                 в†ђ memory     в”‚
в”‚    .WithChatHistory()                            в†ђ history    в”‚
в”‚    .WithMode(AgentMode.ToolsAndChat)             в†ђ mode       в”‚
в”‚    .Build()                                        в†“         в”‚
в””в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”
                              в†“
в”Њв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”ђ
в”‚                       AgentConfig                             в”‚
в”њв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”¤
в”‚  RoleId: "Merchant"                                          в”‚
в”‚  SystemPrompt: "You are a shopkeeper..."                     в”‚
в”‚  Tools: [InventoryLlmTool, MemoryLlmTool]                    в”‚
в”‚  Mode: ToolsAndChat                                          в”‚
в””в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”
                              в†“
в”Њв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”ђ
в”‚               AgentConfig.ApplyToPolicy(policy)               в”‚
в”њв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”¤
в”‚  policy.SetToolsForRole("Merchant", [tools])                 в”‚
в”‚  policy.EnableMemoryTool("Merchant")                         в”‚
в”‚  policy.EnableChatHistory("Merchant")                        в”‚
в””в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”
                              в†“
в”Њв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”ђ
в”‚                    AiOrchestrator                              в”‚
в”њв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”¤
в”‚  RunTaskAsync("Merchant", "What do you have?")               в”‚
в”‚    в†“                                                          в”‚
в”‚  в†’ FunctionInvokingChatClient в†’ tools=[inventory, memory]    в”‚
в”‚    в†“                                                          в”‚
в”‚  в†’ Model: {"name": "get_inventory", "arguments": {}}         в”‚
в”‚    в†“                                                          в”‚
в”‚  в†’ InventoryTool executes в†’ [Iron Sword(50), Potion(25)]     в”‚
в”‚    в†“                                                          в”‚
в”‚  в†’ Model: "I have Iron Sword for 50 coins..."                в”‚
в”‚    в†“                                                          в”‚
в”‚  в†’ ChatHistory saves: user + assistant messages              в”‚
в””в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”
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

// Resilience settings:
CoreAISettings.MaxToolResultChars = 4000;     // Truncate large tool results (default 8000)
CoreAISettings.DefaultToolTimeoutMs = 15000;  // Per-tool timeout in ms (default 30000)
CoreAISettings.MaxResponseChars = 50000;      // Max response chars (default 0 = disabled)
CoreAISettings.MaxToolCallRoundtrips = 15;    // Max tool-call iterations (default 10)
```

### рџ›ЎпёЏ Resilience & Safety

Production agents face three classes of risk: tool result overflow, tool hangs, and model runaway. CoreAI has built-in protections for all three:

| Setting | Default | What it does |
|---------|---------|-------------|
| `MaxToolResultChars` | **8000** (~2000 tokens) | Soft-truncates tool results with `вЂ¦[truncated: N chars в†’ M shown]`. Prevents a single tool from overflowing the context window. |
| `DefaultToolTimeoutMs` | **30000** (30s) | Per-tool execution timeout. If a tool body hangs (e.g. HTTP to dead server), the call is cancelled and the model receives an error. |
| `MaxResponseChars` | **0** (disabled) | Hard cap on total model response text. Set to e.g. `50000` for production NPC chat to prevent runaway generation. |
| `MaxToolCallRoundtrips` | **10** | Maximum tool-call loop iterations per request. Prevents infinite tool-calling loops (model calls tools в†’ gets results в†’ calls again в†’ вЂ¦). |
| `MaxToolCallHistoryMessages` | **20** | Max tool call messages retained in the MEAI list during a single request's tool-calling loop. Prevents unbounded context growth. 0 = no limit. |

**All five** are configurable via:
- `CoreAISettings.X = value` (static C# override)
- `CoreAISettingsAsset` in Unity Inspector (under рџ›ЎпёЏ **Resilience & Safety**)
- `ICoreAISettings` interface (DI / custom settings)

**Examples:**

```csharp
// High-volume game: tools may return huge inventories
CoreAISettings.MaxToolResultChars = 4000; // ~1000 tokens max per tool result

// Aggressive timeout for realtime NPCs
CoreAISettings.DefaultToolTimeoutMs = 5000; // 5 seconds

// Cap model output for chat bubbles
CoreAISettings.MaxResponseChars = 2000;
```

**What happens on truncation/timeout:**

```
[ToolPolicy] вњ‚ Tool 'get_inventory' result truncated: 45230 в†’ 4000 chars
[ToolPolicy] вЏ± Error: Tool 'fetch_weather' timed out after 5000ms
[SmartToolCall] вљ  Max tool-call roundtrips (10) reached. Stopping.
[SmartToolCall] вњ‚ Response truncated at 2000 chars
[SmartToolCall] Trimmed 4 old tool call message(s), keeping 12 total.
```

### Tool call retry

If the model fails to emit a tool call in the correct format, the system automatically retries **3 times** (by default):

```
Attempt 1: Model returns wrong format
  в†“
System: "ERROR: Tool call not recognized. Use this format: {"name": "...", "arguments": {...}}"
  в†“
Attempt 2: Model retries
  в†“
(If still wrong)
  в†“
Attempt 3: Final attempt
  в†“
(If still wrong - accepts response as is)
```

This helps small models (e.g. Qwen3.5-2B) that sometimes forget the format.

### рџ”„ Dual-Backend with Auto-Fallback

Configure a secondary HTTP backend in Inspector (**рџ”„ Fallback Backend** section). When the primary backend fails, requests are automatically retried on the secondary:

```
Primary: http://127.0.0.1:1234/v1 (local Qwen3.5-4B)
Secondary: https://api.openai.com/v1 (GPT-4o)

Request в†’ Primary fails (timeout/503) в†’ Retry on Secondary в†’ Success
```

**Setup in Inspector:**

1. Open `CoreAISettings` asset
2. Toggle **Enable Fallback Backend** вњ“
3. Fill **Secondary API Base URL**, **Secondary API Key**, **Secondary Model Name**

**Setup via code:**

```csharp
// In CoreAISettingsAsset Inspector:
// enableFallbackBackend = true
// secondaryApiBaseUrl = "https://api.openai.com/v1"
// secondaryApiKey = "sk-..."
// secondaryModelName = "gpt-4o-mini"

// The pipeline auto-wraps: primary в†’ FallbackLlmClientDecorator(primary, secondary)
```

**Use cases:**
- Local model for fast/free, cloud for complex queries when local fails
- Free tier + paid fallback
- A/B model testing

---

## Recommended models

| Model | Size | Tool calling | When to use |
|--------|--------|--------------|-------------------|
| **Qwen3.5-4B** | 4B | вњ… Strong | **Recommended** for local runs |
| **Qwen3.5-35B (MoE) API** | 35B/3A | вњ… Excellent | **Ideal** via API вЂ” fast and accurate |
| **Gemma 4 26B** | 26B | вњ… Excellent | Great via LM Studio / HTTP API |
| Qwen3.5-2B | 2B | вљ пёЏ Works | Works but can err on multi-step |
| Qwen3.5-0.8B | 0.8B | вљ пёЏ Basic | Most tests pass; multi-step is harder |

> рџЏ† **Qwen3.5-4B passes ALL PlayMode tests.** Recommended minimum for production.
> рџ’Ў MoE models activate only part of the parameters (3B) вЂ” fast like 4B, accurate like 35B.

---

## Troubleshooting

### Agent does not call tools
- Ensure `Mode` is `AgentMode.ToolsAndChat` or `ToolsOnly`
- Confirm tools are passed via `.WithTool()`
- Check the system prompt вЂ” the model must know about the tools

### Memory does not persist
- Ensure `.WithMemory()` is called
- Verify the model calls the memory tool: `{"name": "memory", ...}`
- Enable logging: `GameLogger.SetFeatureEnabled(GameLogFeature.Llm, true)`

### Chat history does not work
- Ensure `.WithChatHistory()` is called on the role
- For **LLMUnity**, history is mirrored into `LLMAgent` during the session вЂ” if the list is empty, confirm the client was created with chat history enabled for that role
- For **persistence after closing the game**, built-in **`PlainChat`** / **`SmartChat`** persist chat by default. For custom roles, use `.WithChatHistory(persistBetweenSessions: true)` and a persistent `IAgentMemoryStore` (default: `FileAgentMemoryStore`). Chat is **not** written to disk when `persistBetweenSessions` is false вЂ” only in-memory for that process
- UI restore: see **[README_CHAT.md](../../CoreAiUnity/Runtime/Source/Features/Chat/README_CHAT.md)** (`Load Persisted Chat On Startup`, role policy)

---

## Related portable documentation

- [`README.md`](README.md) вЂ” index of all guides under `Assets/CoreAI/Docs`
- [`MEAI_TOOL_CALLING.md`](MEAI_TOOL_CALLING.md) вЂ” MEAI tool pipeline
- [`MEAI_TOKENS_FACT_VS_ESTIMATE.md`](MEAI_TOKENS_FACT_VS_ESTIMATE.md) вЂ” token `usage`, streaming, timeouts
- [`LLM_ROUTING.md`](LLM_ROUTING.md) вЂ” portable routing contracts
