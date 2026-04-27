# 🧠 Agent memory system

## Two memory types

### Type 1: MemoryTool (function call) — EXPLICIT MEMORY

**How it works:**
1. **Microsoft.Extensions.AI (MEAI)** integration via `FunctionInvokingChatClient`
2. `MemoryTool.CreateAIFunction()` creates an `AIFunction` for MEAI
3. The model calls the function using a **single JSON format**: `{"name": "memory", "arguments": {"action": "write", "content": "..."}}`
4. MEAI `FunctionInvokingChatClient` recognizes the call and runs `MemoryTool.ExecuteAsync()`
5. On the next request memory is **injected into the system prompt**

**MEAI pipeline:**
```
LLM Request → FunctionInvokingChatClient → LLMAgent
                    ↓
            [Model: {"name": "memory", "arguments": {...}}]
                    ↓
            AIFunction (MemoryTool) executes
                    ↓
            [Tool result returned]
                    ↓
            Final response → AiOrchestrator
```

**Three actions (single format):**
```json
{"name": "memory", "arguments": {"action": "write", "content": "Craft#1: Iron Blade damage:45"}}
{"name": "memory", "arguments": {"action": "append", "content": "Craft#2: Steel Longsword damage:72"}}
{"name": "memory", "arguments": {"action": "clear"}}
```

**When to use:**
- ✅ CoreMechanicAI — craft history
- ✅ Creator — design decisions
- ✅ Programmer — saved Lua formulas
- ✅ Analyzer — recommendations and observations

**Default configuration:**
```csharp
// AgentMemoryPolicy enables MemoryTool for most built-in roles.
// PlayerChat is the drop-in chat role: MemoryTool is off, persistent ChatHistory is on.
var policy = new AgentMemoryPolicy();

// Disable for a specific role
policy.DisableMemoryTool("Merchant");

// Enable for all
policy.SetMemoryToolForAll(enabled: true);

// Configure default action per role
policy.ConfigureRole("CoreMechanicAI", defaultAction: MemoryToolAction.Append);
policy.ConfigureRole("Creator", defaultAction: MemoryToolAction.Write);
```

---

### Type 2: ChatHistory (LLMUnity) — FULL CONTEXT

**How it works:**
1. `MeaiLlmUnityClient` is called with `useChatHistory: true`
2. On `CompleteAsync()`:
   - Loads the last 20 messages from `IAgentMemoryStore.GetChatHistory()`
   - Inserts them into `LLMAgent.AddToHistory()`
   - Calls `Chat(addToHistory: true)`
   - Saves user + assistant messages back to the store

**When to use:**
- ✅ PlayerChat — conversation context with the player
- ✅ AINpc — sequential NPC lines
- ✅ When the model “forgets” what was in previous messages

**Do not use when:**
- ❌ You need control over **what** the model sees (prefer MemoryTool)
- ❌ Saving tokens (ChatHistory sends the **entire** history)
- ❌ The model does not support a long context

---

## Comparison

| Aspect | MemoryTool (Type 1) | ChatHistory (Type 2) |
|--------|-------------------|---------------------|
| **Who decides** | Model calls the function | Code saves automatically |
| **Control** | Model chooses **what** to remember | **Everything** is saved |
| **Size** | Compact (model summarizes) | Full (all messages) |
| **Tokens** | Saves (important only) | Spends (full history) |
| **LLMUnity** | Always works | Only with `useChatHistory: true` |
| **HTTP/OpenAI** | Works | ❌ No (needs chat object) |
| **Persistence** | ✅ FileAgentMemoryStore | ✅ FileAgentMemoryStore |

---

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                     AiOrchestrator                          │
│                                                             │
│  ┌───────────────────┐    ┌──────────────────────────────┐  │
│  │ Type 1: MemoryTool│    │  Type 2: ChatHistory         │  │
│  │                   │    │  (LLMUnity only)              │  │
│  │ 1. Reads memory   │    │                               │  │
│  │    from store     │    │ 1. Loads last 20 messages    │  │
│  │ 2. Injects into   │    │    into LLMAgent               │  │
│  │    system prompt  │    │ 2. Chat(addToHistory: true)    │  │
│  │ 3. Model writes   │    │ 3. Saves user+assistant        │  │
│  │    {"tool":"mem"} │    │    to store                    │  │
│  │ 4. Persists       │    │                               │  │
│  └───────────────────┘    └──────────────────────────────┘  │
└─────────────────────────────────────────────────────────────┘
         ↓                              ↓
┌────────────────────────────────────────────────┐
│              IAgentMemoryStore                 │
│                                                │
│  TryLoad(roleId) → AgentMemoryState          │
│  Save(roleId, state)                         │
│  Clear(roleId)                               │
│  AppendChatMessage(roleId, role, content)    │
│  GetChatHistory(roleId, maxMessages)         │
└────────────────────────────────────────────────┘
         ↓
┌──────────────────────┐    ┌──────────────────────────┐
│ InMemoryStore        │    │ FileAgentMemoryStore     │
│ (tests, Dictionary)  │    │ (Unity, persistentData)  │
└──────────────────────┘    └──────────────────────────┘
```

---

## Custom persistence (PlayerPrefs, cloud)

The same `IAgentMemoryStore` contract backs **both** MemoryTool and ChatHistory. The default implementation is `FileAgentMemoryStore` (local JSON). For **PlayerPrefs**, **cloud saves** (REST, UGS, Steam, PlayFab, …), or a **local + upload** composite, implement or wrap `IAgentMemoryStore` and register it in DI instead of `FileAgentMemoryStore`.

See **[MEMORY_STORE_CUSTOM_BACKENDS.md](MEMORY_STORE_CUSTOM_BACKENDS.md)** for constraints, debounced upload, conflict handling, and wiring notes.

---

## Memory configuration by role

These are **default policy choices**, not hard limits. The key distinction:

- **MemoryTool** stores compact facts/decisions the model deliberately chooses to preserve.
- **ChatHistory** stores raw dialogue turns. It is useful for conversations, but can be noisy or stale for state-changing agents.

| Role | MemoryTool | Default action | ChatHistory default | Persisted chat default | Why |
|------|:----------:|:--------------:|:-------------------:|:----------------------:|-----|
| **Creator** | ✅ | Write | ❌ | ❌ | Creator should make decisions from the current world snapshot + compact durable facts, not stale raw chat. Enable ChatHistory only for an interactive designer/co-author session. |
| **Analyzer** | ✅ | Append | ❌ | ❌ | Analyzer should consume telemetry/snapshots and store summarized observations. Raw chat history can bias analysis and waste tokens; use MemoryTool or structured telemetry for trends. |
| **Programmer** | ✅ | Append | ❌ | ❌ | Repair context is passed explicitly (`LuaRepairPreviousCode`, errors, version store). Full chat is usually unnecessary and can confuse code generation. |
| **CoreMechanicAI** | ✅ | Append | ❌ | ❌ | Needs deterministic facts such as craft history/results; compact MemoryTool is better than raw dialogue. |
| **AINpc** | ✅ | Append | ❌ | ❌ | Defaults stay conservative because NPC roles vary: bark-only NPCs need no chat history; named NPCs can opt in. |
| **PlayerChat** | ❌ | - | ✅ | ✅ | Drop-in chat is a conversation UI; users expect visible session restore after restart. |

**Implementation note:** `AgentMemoryPolicy.RoleMemoryConfig` defaults `WithChatHistory` to **false** and **`PersistChatHistory` to false** unless you pass `true` (for example `PlayerChat` in the policy constructor, or `ConfigureChatHistory` / `AgentBuilder.WithChatHistory(..., persistBetweenSessions: true)`). That keeps agent roles on MemoryTool-only defaults without implying disk chat persistence.

Recommended opt-ins:

```csharp
// Interactive creator/designer assistant: keep the working conversation for the current design session.
new AgentBuilder("Creator")
    .WithMemory(MemoryToolAction.Write)
    .WithChatHistory(4096, persistBetweenSessions: false)
    .Build();

// Named story NPC: preserve conversation and relationship across restarts.
new AgentBuilder("BlacksmithNPC")
    .WithMemory(MemoryToolAction.Append)
    .WithChatHistory(4096, persistBetweenSessions: true)
    .Build();

// Analyzer dashboard chat: use history only if a human is discussing the report with the analyzer.
new AgentBuilder("AnalyzerChat")
    .WithMemory(MemoryToolAction.Append)
    .WithChatHistory(4096, persistBetweenSessions: false)
    .Build();
```

---

## Usage examples

### Example 1: CoreMechanicAI — craft history (MemoryTool)

```csharp
// Setup
var policy = new AgentMemoryPolicy();
policy.ConfigureRole("CoreMechanicAI",
    useMemoryTool: true,
    defaultAction: MemoryToolAction.Append);

// Model request
await orchestrator.RunTaskAsync(new AiTaskRequest
{
    RoleId = "CoreMechanicAI",
    Hint = "Craft a weapon from Iron + Fire Crystal. " +
           "Save to memory: {\"tool\":\"memory\",\"action\":\"write\"," +
           "\"content\":\"Craft#1: Iron Fireblade damage:45 fire:15\"}"
});

// Memory saved: "Craft#1: Iron Fireblade damage:45 fire:15"
// On the next request the model SEES this memory in the system prompt
```

### Example 2: PlayerChat — dialogue context (ChatHistory)

```csharp
// LLMUnity client setup
var client = new MeaiLlmUnityClient(
    llmAgent,
    logger,
    memoryStore: fileStore,
    memoryPolicy: policy,
    useChatHistory: true  // ← Type 2: full context
);

// Dialogue 1
await orchestrator.RunTaskAsync(new AiTaskRequest
{
    RoleId = "PlayerChat",
    Hint = "My name is Alex"
});
// Saved: user="My name is Alex", assistant="Nice to meet you, Alex!"

// Dialogue 2 — the model REMEMBERS the name
await orchestrator.RunTaskAsync(new AiTaskRequest
{
    RoleId = "PlayerChat",
    Hint = "What is my name?"
});
// Model answers: "Your name is Alex" (sees history from up to 20 messages)
```

### Example 3: Disable memory for a role

```csharp
var policy = new AgentMemoryPolicy();
policy.DisableMemoryTool("Merchant");    // PlayerChat already has MemoryTool disabled by default
policy.SetMemoryToolForAll(false);        // Disable for ALL (ChatHistory only)
```

---

## Files

| File | Purpose |
|------|-----------|
| `AgentMemoryPolicy.cs` | Configuration: who uses which type |
| `IAgentMemoryStore.cs` | Store interface (+ ChatHistory methods) |
| `AgentMemoryState.cs` | State: LastSystemPrompt + Memory |
| `MemoryTool.cs` | Microsoft.Extensions.AI function for the model |
| `AgentMemoryDirectiveParser.cs` | Parses `{"tool":"memory"...}` from responses |
| `NullAgentMemoryStore.cs` | Stub (saves nothing) |
| `FileAgentMemoryStore.cs` | Unity: JSON files under persistentDataPath |
| `MEMORY_STORE_CUSTOM_BACKENDS.md` | PlayerPrefs / cloud / composite `IAgentMemoryStore` patterns |
| `AiOrchestrator.cs` | Orchestrator: injects memory into system prompt |
| `MeaiLlmUnityClient.cs` | LLMUnity with MEAI: MemoryTool (Type 1) and ChatHistory (Type 2) |
