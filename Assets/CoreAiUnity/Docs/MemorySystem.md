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

**Supported actions (single format):**
```json
{"name": "memory", "arguments": {"action": "write", "content": "Craft#1: Iron Blade damage:45"}}
{"name": "memory", "arguments": {"action": "append", "content": "Craft#2: Steel Longsword damage:72"}}
{"name": "memory", "arguments": {"action": "clear"}}
{"name": "memory", "arguments": {"action": "str_replace", "old_text": "damage:45", "new_text": "damage:50"}}
{"name": "memory", "arguments": {"action": "insert", "anchor": "Crafts:", "content": "Craft#3: Frost Axe damage:61"}}
{"name": "memory", "arguments": {"action": "delete", "old_text": "obsolete fact"}}
{"name": "memory", "arguments": {"action": "rename", "old_text": "Crafts", "new_text": "Craft History"}}
```

Granular edits operate on the canonical `AgentMemoryState.Memory` document that prompt assembly and inspectors already read. Edits are exact and case-sensitive:

- `str_replace`: replaces the first exact `old_text` with `new_text` (or `content`). Set `replace_all: true` to replace every exact match.
- `insert`: adds `content` before a 1-based `line`, after the first line containing `anchor`, or at the end when neither is supplied.
- `delete`: removes the first exact `old_text` (or `content`). Set `replace_all: true` to remove every exact match.
- `rename`: renames the first leading section/key label `old_text:` or `# old_text:` to `new_text:` (or `content:`).

Every successful memory mutation records a bounded audit snapshot on `AgentMemoryState.Versions`: version number, UTC timestamp, action, full `contentAfter`, and a short size-delta note. Stores that preserve the full `AgentMemoryState` retain those snapshots; custom stores can persist them alongside `Memory`. Use `IAgentMemoryStore.ListVersions(roleId)` to inspect retained snapshots and `IAgentMemoryStore.Revert(roleId, version)` to restore one; revert itself creates a new version.

**When to use:**
- ✅ CoreMechanicAI — craft history
- ✅ Creator — design decisions
- ✅ Programmer — saved Lua formulas
- ✅ Analyzer — recommendations and observations

**Default configuration:**
```csharp
// AgentMemoryPolicy enables MemoryTool for most built-in roles.
// PlainChat / SmartChat are built-in chat roles: PlainChat has MemoryTool off; SmartChat has it on; both persist ChatHistory.
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
- ✅ PlainChat / SmartChat — conversation context with the player
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

**v1.5.2:** deterministic compaction folds older turns into **`## Conversation Summary`**. **`RegisterCorePortable()`** defaults to **`InMemoryConversationSummaryStore`** (per-role summaries for the process); Unity **`CoreAILifetimeScope`** overrides with **`FileConversationSummaryStore`** (`%persistentDataPath%/CoreAI/ConversationSummaries`) for persistence across launches. **`FileAgentMemoryStore`** implements **`IConversationTranscriptStore`** (structured `ConversationEntry` rows; optional tool lines for future MEAI hooks).

**v1.5.3:** optional **LLM-assisted compaction** (Kilocode-style). When **`ICoreAISettings.EnableLlmContextCompaction`** is `true` on **`CoreAISettingsAsset`**, overflowing history may be summarized by an auxiliary LLM call instead of the deterministic bullet rollup. The system is gated at two levels:

| Level | Toggle | Default |
|-------|--------|---------|
| **Global** | `CoreAISettingsAsset.EnableLlmContextCompaction` | `false` |
| **Per-role** | `AgentMemoryPolicy.RoleMemoryConfig.UseLlmContextCompaction` | `true` for most roles; `false` for **Programmer** |

Per-role override: **`AgentBuilder.WithLlmContextCompaction(bool)`** or **`AgentMemoryPolicy.ConfigureLlmContextCompaction(roleId, bool)`**. When the global toggle is off, all roles use deterministic compaction regardless of their per-role setting.

Compaction calls route through `ILlmClient.CompleteAsync` with role id **`__CoreAI_ContextCompaction`** and configurable options (`LlmContextCompactionOptions`). If the auxiliary LLM call fails, the system falls back to the deterministic bullet summary.

**Separation from the main system prompt:** The **full** orchestrator system string (built-in/custom role prompt, universal prefix, `## Memory`, `## Tool Contract`, etc.) is **not** fed into compaction. Only **persisted chat lines** (`IAgentMemoryStore.GetChatHistory` — typically `user` / `assistant` turns) plus the **prior rolling summary** are packed into that completion’s **`UserPayload`**; **`ChatHistory` on that request is `null`**. The compaction call uses **`LlmContextCompactionOptions.SystemPrompt`** (compact “you are a summarizer” instructions), which is unrelated to e.g. `Teacher`/`Creator` prose. After compaction, **`AiOrchestrator`** appends the new summary under **`## Conversation Summary`** into the **main** system prompt for the **primary** model turn — that block is downstream output; it is not sent back through the compaction LLM unless it later ages into history as normal assistant/user text.

**LLMUnity as a local OpenAI server (since v5.0.8):** the LLMUnity backend no longer calls `LLMAgent.Chat()` in-process. Instead the `LLM` component runs the GGUF model as its **built-in OpenAI-compatible server** (`llm.remote = true` + `CoreAISettingsAsset.LlmUnityServerPort`, default 13333, set **before** the native service initializes) and CoreAI drives it through the **native HTTP pipeline** (`OpenAiChatLlmClient` over `LlmUnityServerHttpSettings` → `POST http://localhost:{port}/v1/chat/completions`). This yields **native structured `tool_calls`** (server-side jinja + grammar) and SSE streaming, identical to LM Studio / any OpenAI backend — replacing the old prompt-injected, regex-parsed text tool calls. Context management is still **only** CoreAI's backend-agnostic compaction (above), which builds the whole prompt and sends it as ordinary OpenAI messages; the server never manages history. `agent.overflowStrategy` is still forced to `None` for tidiness, but it is moot now — the agent's in-process `Chat()` path is no longer used at all.

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
| **Creator** | ✅ | Write | ✅ | ❌ | Keeps short session continuity by default while durable design decisions still belong in compact MemoryTool facts. |
| **Analyzer** | ✅ | Append | ✅ | ❌ | Keeps recent discussion context, but summarized observations should still go through MemoryTool or structured telemetry. |
| **Programmer** | ✅ | Append | ✅ | ❌ | Recent dialogue is retained by default; deterministic repair inputs remain the authoritative code context. |
| **CoreMechanicAI** | ✅ | Append | ✅ | ❌ | Retains recent mechanic discussion while deterministic craft history/results stay in compact MemoryTool memory. |
| **AINpc** | ✅ | Append | ✅ | ❌ | Sequential NPC lines now keep recent conversation by default; persistence remains opt-in for named/long-lived NPCs. |
| **PlainChat** | ❌ | - | ✅ | ✅ | Simple drop-in chat; session restore after restart. |
| **SmartChat** | ✅ | Append | ✅ | ✅ | Chat + MemoryTool for durable facts; session restore after restart. |

**Implementation note:** `AgentMemoryPolicy.RoleMemoryConfig` and `AgentBuilder` now default `WithChatHistory` to **true** with `MaxChatHistoryMessages = 30`, but **`PersistChatHistory` remains false** unless you pass `true` (for example **`PlainChat`** / **`SmartChat`** entries in the policy constructor, or `ConfigureChatHistory` / `AgentBuilder.WithChatHistory(..., persistBetweenSessions: true)`). This improves continuity without implying disk chat persistence. Use `AgentBuilder.WithoutChatHistory()` or `ConfigureChatHistory(roleId, enabled: false, ...)` for token-sensitive/tool-only roles.

**Token-cost note:** ChatHistory sends recent raw turns, so it costs more prompt tokens than compact MemoryTool facts. Keep durable facts in MemoryTool and disable ChatHistory explicitly for roles that must stay deterministic or very small.

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

### Example 2: PlainChat — dialogue context (ChatHistory)

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
    RoleId = "PlainChat",
    Hint = "My name is Alex"
});
// Saved: user="My name is Alex", assistant="Nice to meet you, Alex!"

// Dialogue 2 — the model REMEMBERS the name
await orchestrator.RunTaskAsync(new AiTaskRequest
{
    RoleId = "PlainChat",
    Hint = "What is my name?"
});
// Model answers: "Your name is Alex" (sees history from up to 20 messages)
```

### Example 3: Disable memory for a role

```csharp
var policy = new AgentMemoryPolicy();
policy.DisableMemoryTool("Merchant");    // PlainChat already has MemoryTool disabled by default
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
| `NullAgentMemoryStore.cs` | Stub (saves nothing) — portable default when **`RegisterCorePortable`** is used without a host store; not the default for **`CoreAILifetimeScope`** (which registers **`FileAgentMemoryStore`**, WebGL player included since **v1.6.19**) |
| `FileAgentMemoryStore.cs` | Unity: JSON files under persistentDataPath |
| `MEMORY_STORE_CUSTOM_BACKENDS.md` | PlayerPrefs / cloud / composite `IAgentMemoryStore` patterns |
| `AiOrchestrator.cs` | Orchestrator: injects memory into system prompt |
| `MeaiLlmUnityClient.cs` | LLMUnity with MEAI: MemoryTool (Type 1) and ChatHistory (Type 2) |

### Clearing saves in the Editor

**CoreAI → Delete All Persistent Saves...** (only when **not** in Play Mode) deletes the entire **`Application.persistentDataPath/CoreAI`** tree — **AgentMemory** (memory + persisted chat JSON), **ConversationSummaries**, **LuaScriptVersions**, **DataOverlayVersions**. Use for a clean persistence baseline while testing.
