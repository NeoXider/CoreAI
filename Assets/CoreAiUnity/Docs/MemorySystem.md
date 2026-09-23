# 🧠 Agent memory system

## Two memory types

### Type 1: MemoryTool (function call) — EXPLICIT MEMORY

**How it works:**
1. **Microsoft.Extensions.AI (MEAI)** `AIFunction`s run inside CoreAI's tool loop (`SmartToolCallingChatClient` / the streaming loop in `MeaiLlmClient`) through `ToolExecutionPolicy`
2. `MemoryTool.CreateAIFunction()` creates the `AIFunction` (exposed to a role through `MemoryLlmTool`)
3. The model calls the function using a **single JSON format**: `{"name": "memory", "arguments": {"action": "write", "content": "..."}}`
4. `ToolExecutionPolicy` validates the arguments and runs `MemoryTool.ExecuteAsync()`
5. On the next request the orchestrator sends the memory as ordered **tail messages** of the chat history (`## Memory`), never inside the cacheable system prompt

**Pipeline:**
```
LLM request (ILlmClient → OpenAiChatLlmClient, HTTP API or LLMUnity's local server)
                    ↓
            [Model: {"name": "memory", "arguments": {...}}]
                    ↓
            ToolExecutionPolicy → AIFunction (MemoryTool) executes
                    ↓
            [Tool result returned to the model]
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

Two levels of "clear": the model-facing `memory(action=clear)` is a **versioned mutation** — it empties only the memory **document** (`MemoryMutationPlan.Change("")`), so the row survives with empty memory plus a rollback version (see `ListVersions`/`Revert`) and chat history/transcripts survive; the model cannot erase the user's conversation record. The store-level `IAgentMemoryStore.Clear(roleId)` is a harder reset that also drops the version history and the system-prompt snapshot: on `FileAgentMemoryStore` it **removes the role key entirely** when the file holds no persisted chat history/transcripts, but **preserves the conversation** (wiping only the memory fields) when the file also carries persisted chat history; a corrupt file is replaced with an explicit cleared marker so the next load starts clean.

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

// Configure default action per role (every built-in role defaults to Append)
policy.ConfigureRole("CoreMechanicAI", defaultAction: MemoryToolAction.Append);
policy.ConfigureRole("Creator", defaultAction: MemoryToolAction.Write);
```

---

### Type 2: ChatHistory — RECENT DIALOGUE

**How it works (every backend):**
1. The role has `WithChatHistory` enabled in `AgentMemoryPolicy` (default for every built-in role except Programmer).
2. For each request `AiOrchestrator` reads the role's history once from `IAgentMemoryStore.GetChatHistory` (window cap `MaxChatHistoryMessages`, default 30), lets `IConversationContextManager` prune and fold older turns, and sends the result as chat messages in `LlmCompletionRequest.ChatHistory`.
3. After the turn it appends the user message and the visible assistant reply to the store.
4. The history reaches disk only with `PersistChatHistory` (`AgentBuilder.WithChatHistory(..., persistBetweenSessions: true)`).

**When to use:**
- ✅ PlainChat / SmartChat — conversation context with the player
- ✅ AINpc — sequential NPC lines
- ✅ When the model “forgets” what was in previous messages

**Do not use when:**
- ❌ You need control over **what** the model sees (prefer MemoryTool)
- ❌ Saving tokens (ChatHistory sends the recent raw turns; older ones are folded into a summary)
- ❌ The model does not support a long context

---

## Comparison

| Aspect | MemoryTool (Type 1) | ChatHistory (Type 2) |
|--------|-------------------|---------------------|
| **Who decides** | Model calls the function | Code saves automatically |
| **Control** | Model chooses **what** to remember | **Everything** is saved |
| **Size** | Compact (model summarizes) | Full (all messages) |
| **Tokens** | Saves (important only) | Spends (full history) |
| **LLMUnity** | Works | Works |
| **HTTP/OpenAI** | Works | Works |
| **Persistence** | `Persistent`: `FileAgentMemoryStore`; `SessionOnly`: process memory | `Persistent`: `FileAgentMemoryStore`; `SessionOnly`: process memory |

**v1.5.2:** deterministic compaction folds older turns into **`## Conversation Summary`**. **`RegisterCorePortable()`** defaults to **`InMemoryConversationSummaryStore`** (per-role summaries for the process). Unity **`CoreAILifetimeScope`** defaults to `AgentMemoryPersistenceMode.Persistent`: `FileConversationSummaryStore` on desktop and an in-memory summary on WebGL; memory/chat/transcript use `FileAgentMemoryStore` on both. Call `SetAgentMemoryPersistenceMode(AgentMemoryPersistenceMode.SessionOnly)` before build to keep all four data sets in memory and create no memory/summary files. Both `FileAgentMemoryStore` and `InMemoryAgentMemoryStore` implement **`IConversationTranscriptStore`**.

**v1.5.3:** optional **LLM-assisted compaction** (Kilocode-style). When **`ICoreAISettings.EnableLlmContextCompaction`** is `true` on **`CoreAISettingsAsset`**, overflowing history may be summarized by an auxiliary LLM call instead of the deterministic bullet rollup. The system is gated at two levels:

| Level | Toggle | Default |
|-------|--------|---------|
| **Global** | `CoreAISettingsAsset.EnableLlmContextCompaction` | `false` |
| **Per-role** | `AgentMemoryPolicy.RoleMemoryConfig.UseLlmContextCompaction` | `true` for most roles; `false` for **Programmer** |

Per-role override: **`AgentBuilder.WithLlmContextCompaction(bool)`** or **`AgentMemoryPolicy.ConfigureLlmContextCompaction(roleId, bool)`**. When the global toggle is off, all roles use deterministic compaction regardless of their per-role setting.

Compaction calls route through `ILlmClient.CompleteAsync` with role id **`__CoreAI_ContextCompaction`** and configurable options (`LlmContextCompactionOptions`). If the auxiliary LLM call fails, the system falls back to the deterministic bullet summary.

**Separation from the main system prompt:** The **full** orchestrator system string (built-in/custom role prompt, universal prefix, tool contract) and the memory tail are **not** fed into compaction. Only **persisted chat lines** (`IAgentMemoryStore.GetChatHistory` — typically `user` / `assistant` turns) plus the **prior rolling summary** are packed into that completion’s **`UserPayload`**; **`ChatHistory` on that request is `null`**. The compaction call uses **`LlmContextCompactionOptions.SystemPrompt`** (compact “you are a summarizer” instructions), which is unrelated to e.g. `Teacher`/`Creator` prose. After compaction, **`AiOrchestrator`** sends the new summary as a **`## Conversation Summary`** system-role message in the ordered tail of **`ChatHistory`** for the **primary** model turn (never in the cacheable system prefix) — that block is downstream output; it is not sent back through the compaction LLM unless it later ages into history as normal assistant/user text.

**LLMUnity as a local OpenAI server (since v5.0.8):** the LLMUnity backend no longer calls `LLMAgent.Chat()` in-process. Instead the `LLM` component runs the GGUF model as its **built-in OpenAI-compatible server** (`llm.remote = true` + `CoreAISettingsAsset.LlmUnityServerPort`, default 13333, set **before** the native service initializes) and CoreAI drives it through the **native HTTP pipeline** (`OpenAiChatLlmClient` over `LlmUnityServerHttpSettings` → `POST http://localhost:{port}/v1/chat/completions`). This yields **native structured `tool_calls`** (server-side jinja + grammar) and SSE streaming, identical to LM Studio / any OpenAI backend — replacing the old prompt-injected, regex-parsed text tool calls. Context management is still **only** CoreAI's backend-agnostic compaction (above), which builds the whole prompt and sends it as ordinary OpenAI messages; the server never manages history. `agent.overflowStrategy` is still forced to `None` for tidiness, but it is moot now — the agent's in-process `Chat()` path is no longer used at all.

---

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                     AiOrchestrator                          │
│                                                             │
│  ┌───────────────────┐    ┌──────────────────────────────┐  │
│  │ Type 1: MemoryTool│    │  Type 2: ChatHistory         │  │
│  │                   │    │  (every backend)             │  │
│  │ 1. Reads memory   │    │                              │  │
│  │    from store     │    │ 1. Reads recent history once │  │
│  │ 2. Sends it as    │    │ 2. Prunes / folds old turns  │  │
│  │    tail messages  │    │ 3. Sends them as ChatHistory │  │
│  │ 3. Model calls    │    │ 4. Appends user + assistant  │  │
│  │    the memory tool│    │    to the store              │  │
│  │ 4. Persists       │    │                              │  │
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
│ InMemoryAgentMemoryStore │ │ FileAgentMemoryStore     │
│ (SessionOnly, process)   │ │ (Persistent, Unity file)│
└──────────────────────┘    └──────────────────────────┘
```

---

## Custom persistence (PlayerPrefs, cloud)

The same `IAgentMemoryStore` contract backs **both** MemoryTool and ChatHistory. `CoreAILifetimeScope` defaults to `FileAgentMemoryStore` (local JSON); `AgentMemoryPersistenceMode.SessionOnly` selects `InMemoryAgentMemoryStore` before the container is built. For **PlayerPrefs**, **cloud saves** (REST, UGS, Steam, PlayFab, …), or a **local + upload** composite, implement or wrap `IAgentMemoryStore` and register it in DI instead of the built-in backing.

See **[MEMORY_STORE_CUSTOM_BACKENDS.md](MEMORY_STORE_CUSTOM_BACKENDS.md)** for constraints, debounced upload, conflict handling, and wiring notes.

---

## Memory configuration by role

These are **default policy choices**, not hard limits. The key distinction:

- **MemoryTool** stores compact facts/decisions the model deliberately chooses to preserve.
- **ChatHistory** stores raw dialogue turns. It is useful for conversations, but can be noisy or stale for state-changing agents.

| Role | MemoryTool | Default action | ChatHistory default | Persisted chat default | Why |
|------|:----------:|:--------------:|:-------------------:|:----------------------:|-----|
| **Creator** | ✅ | Append | ✅ | ❌ | Keeps short session continuity by default while durable design decisions still belong in compact MemoryTool facts. |
| **Builder** | ✅ | Append | ✅ | ❌ | Like Creator; builds run with unlimited tool-call roundtrips. |
| **Analyzer** | ✅ | Append | ✅ | ❌ | Keeps recent discussion context, but summarized observations should still go through MemoryTool or structured telemetry. |
| **Programmer** | ✅ | Append | ❌ | ❌ | History is off by default (chat-source requests borrow short-term history for that run only); deterministic repair inputs remain the authoritative code context. |
| **CoreMechanicAI** | ✅ | Append | ✅ | ❌ | Retains recent mechanic discussion while deterministic craft history/results stay in compact MemoryTool memory. |
| **AINpc** | ✅ | Append | ✅ | ❌ | Sequential NPC lines now keep recent conversation by default; persistence remains opt-in for named/long-lived NPCs. |
| **PlainChat** | ❌ | - | ✅ | ✅ | Simple drop-in chat; session restore after restart. |
| **SmartChat** | ✅ | Append | ✅ | ✅ | Chat + MemoryTool for durable facts; session restore after restart. |
| **Merchant** | ✅ | Append | ✅ | ❌ | Merchant NPC dialogue; persistence is opt-in. |

**Implementation note:** `AgentMemoryPolicy.RoleMemoryConfig` and `AgentBuilder` now default `WithChatHistory` to **true** with `MaxChatHistoryMessages = 30`, but **`PersistChatHistory` remains false** unless you pass `true` (for example **`PlainChat`** / **`SmartChat`** entries in the policy constructor, or `ConfigureChatHistory` / `AgentBuilder.WithChatHistory(..., persistBetweenSessions: true)`). This improves continuity without implying disk chat persistence. Use `AgentBuilder.WithoutChatHistory()` or `ConfigureChatHistory(roleId, enabled: false, ...)` for token-sensitive/tool-only roles.

**Token-cost note:** ChatHistory sends recent raw turns, so it costs more prompt tokens than compact MemoryTool facts. Keep durable facts in MemoryTool and disable ChatHistory explicitly for roles that must stay deterministic or very small.

### "The conversation is empty after a page reload / app restart" — expected, not a lost write

Worth stating next to the symptom, because the memory *document* does come back while the transcript
does not, which reads like a bug.

- The **memory document** (MemoryTool facts) is always written to disk, so it survives a restart.
- The **chat transcript** reaches disk only when the role sets `PersistChatHistory` /
  `WithChatHistory(persistBetweenSessions: true)`. `AppendChatMessage(..., persistToDisk: false)`
  means "this process only" and never reaches the file, not even during compaction.
- A **chat-source request against a role with history off** (the built-in `Programmer`) borrows
  short-term history for that run only: `AiOrchestrator.ResolveRoleConfigForRequest` enables
  `WithChatHistory` and explicitly leaves `PersistChatHistory = false`, so nothing is written and the
  next process starts from an empty transcript. Seen in a browser as a post-reload turn going out
  with 3 messages where the pre-reload turn carried 16-19.
- Restoring the *visible* panel history additionally needs `Load Persisted Chat On Startup` on
  `CoreAiChatPanel` (see `README_CHAT.md`).

To carry a conversation across a restart, use `PlainChat` / `SmartChat`, or opt the role in with
`WithChatHistory(persistBetweenSessions: true)`. Pinned by
`AiOrchestratorHistoryEditModeTests.RunTaskAsync_ChatSource_EnablesShortTermHistory_ForProgrammer`,
`AgentBuilderChatHistoryEditModeTests` and `FileAgentMemoryStoreEditModeTests`.

The Rbx world has the same shape and the same answer: instances a Lua chunk creates live in the
running world only. `WorldStateManager` saves registered scene objects, not Rbx instances, and world
packages are captured and restored explicitly — a process restart intentionally returns to the
previously selected world plus the default source set rather than promoting an in-process session
(`Docs/CoreAIMods/WORLD_PACKAGE.md`).

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
    Hint = "Craft a weapon from Iron + Fire Crystal and remember the result with the memory tool."
});

// The model calls: {"name":"memory","arguments":{"action":"append","content":"Craft#1: Iron Fireblade damage:45 fire:15"}}
// On the next request the model SEES this memory in the request tail
```

### Example 2: PlainChat — dialogue context (ChatHistory)

```csharp
// Works on every backend. PlainChat already has history on (and persisted);
// for a custom role, opt in explicitly:
AgentConfig chat = new AgentBuilder("MyChat")
    .WithChatHistory(4096, persistBetweenSessions: true)  // ← Type 2
    .Build();

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
// Model answers: "Your name is Alex" (sees the recent history, up to 30 messages by default)
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
| `NullAgentMemoryStore.cs` | Stub (saves nothing) — portable default when **`RegisterCorePortable`** is used without a host store; not the default for **`CoreAILifetimeScope`** (which registers **`FileAgentMemoryStore`**, WebGL player included since **v1.6.19**) |
| `InMemoryAgentMemoryStore.cs` | Process-only memory, flat chat, structured transcript and atomic mutation backing selected by `AgentMemoryPersistenceMode.SessionOnly` |
| `FileAgentMemoryStore.cs` | Unity: `<stem>.json` (memory document) + `<stem>.history.jsonl` (conversation) under persistentDataPath |
| `MEMORY_STORE_CUSTOM_BACKENDS.md` | PlayerPrefs / cloud / composite `IAgentMemoryStore` patterns |
| `AiOrchestrator.cs` | Orchestrator: reads the history once per request, sends memory and summary as tail messages, appends the turn |
| `MemoryLlmTool.cs` | `ILlmTool` wrapper that exposes `MemoryTool` to the model |

### Clearing saves in the Editor

**CoreAI → Delete All Persistent Saves...** (only when **not** in Play Mode) deletes the entire **`Application.persistentDataPath/CoreAI`** tree — **AgentMemory** (memory documents + persisted chat), **ConversationSummaries**, **LuaScriptVersions**, **DataOverlayVersions**. Use for a clean persistence baseline while testing.
