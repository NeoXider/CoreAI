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
{"name": "memory", "arguments": {"action": "read"}}
{"name": "memory", "arguments": {"action": "write", "content": "Craft#1: Iron Blade damage:45"}}
{"name": "memory", "arguments": {"action": "append", "content": "Craft#2: Steel Longsword damage:72"}}
{"name": "memory", "arguments": {"action": "clear"}}
{"name": "memory", "arguments": {"action": "str_replace", "old_text": "damage:45", "new_text": "damage:50"}}
{"name": "memory", "arguments": {"action": "insert", "anchor": "Crafts:", "content": "Craft#3: Frost Axe damage:61"}}
{"name": "memory", "arguments": {"action": "delete", "old_text": "obsolete fact"}}
{"name": "memory", "arguments": {"action": "rename", "old_text": "Crafts", "new_text": "Craft History"}}
```

Granular edits operate on the canonical `AgentMemoryState.Memory` document that prompt assembly and inspectors already read. Edits are exact and case-sensitive:

- `read`: returns the current memory document with its latest version number.
- `append`: adds `content` on a new line; a line that is already present is not added twice.
- `str_replace`: replaces the first exact `old_text` with `new_text` (or `content`). Set `replace_all: true` to replace every exact match.
- `insert`: adds `content` before a 1-based `line`, after the first line containing `anchor`, or at the end when neither is supplied.
- `delete`: removes the first exact `old_text` (or `content`). Set `replace_all: true` to remove every exact match.
- `rename`: renames the first leading section/key label `old_text:` or `# old_text:` to `new_text:` (or `content:`).

Every successful memory mutation records a bounded audit snapshot on `AgentMemoryState.Versions` (the newest 30 by default, `AgentMemoryState.DefaultMaxMemoryVersions`): version number, UTC timestamp, action, full `contentAfter`, and a short size-delta note. Stores that preserve the full `AgentMemoryState` retain those snapshots; custom stores can persist them alongside `Memory`. Use the `AgentMemoryStoreExtensions` methods `store.ListVersions(roleId)` to inspect retained snapshots and `store.Revert(roleId, version)` to restore one; revert itself creates a new version.

Two levels of "clear": the model-facing `memory(action=clear)` is a **versioned mutation** — it empties only the memory **document** (`MemoryMutationPlan.Change("")`), so the row survives with empty memory plus a rollback version (see `ListVersions`/`Revert`) and chat history/transcripts survive; the model cannot erase the user's conversation record. The store-level `IAgentMemoryStore.Clear(roleId)` is a harder reset that also drops the version history and the system-prompt snapshot: on `FileAgentMemoryStore` it **deletes the role's memory document** (`<stem>.json`) and leaves the conversation file (`<stem>.history.jsonl`) untouched. An unreadable memory document is deleted too, so `Clear` is also the way to give a role working memory back after its file was corrupted. Chat is cleared separately with `ClearChatHistory(roleId)`.

**When to use:**
- ✅ CoreMechanicAI — craft history
- ✅ Creator — design decisions
- ✅ Programmer — saved Lua formulas
- ✅ Analyzer — recommendations and observations

**Default configuration:**
```csharp
// AgentMemoryPolicy enables MemoryTool for every built-in role except PlainChat.
// PlainChat / SmartChat are built-in chat roles: PlainChat has MemoryTool off; SmartChat has it on; both persist ChatHistory.
AgentMemoryPolicy policy = new AgentMemoryPolicy();

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
1. The role has `WithChatHistory` enabled in `AgentMemoryPolicy` (default for every built-in role except Programmer; a request with `SourceTag = "Chat"` turns it on for Programmer for that run only).
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

**Storage.** Deterministic compaction folds older turns into **`## Conversation Summary`**. **`RegisterCorePortable()`** defaults to **`InMemoryConversationSummaryStore`** (per-role summaries for the process). Unity **`CoreAILifetimeScope`** defaults to `AgentMemoryPersistenceMode.Persistent` on every player, WebGL included: summaries go to `FileConversationSummaryStore` (`persistentDataPath/CoreAI/ConversationSummaries`), memory/chat/transcript to `FileAgentMemoryStore` (`persistentDataPath/CoreAI/AgentMemory`). Call `SetAgentMemoryPersistenceMode(AgentMemoryPersistenceMode.SessionOnly)` before build to keep all four data sets in memory and create no memory/summary files. Both `FileAgentMemoryStore` and `InMemoryAgentMemoryStore` implement **`IConversationTranscriptStore`**.

**Isolation.** Every store is reached through a scoped decorator (`ScopedAgentMemoryStoreDecorator` and its summary/transcript siblings) that maps a role id to a storage key. With the default empty `AgentMemoryScope` the key is the role id, so each role has one memory and one history per process. A host serving several users supplies an `IAgentMemoryScopeProvider` (tenant / user / session / topic); a non-empty scope is stored under `scope-v1-<SHA-256>`, a named actor under `actor-v1-<SHA-256>`, so no raw id reaches a file name. Details: [ARCHITECTURE.md — Runtime Context And Memory Scope](ARCHITECTURE.md#runtime-context-and-memory-scope).

**LLM-assisted compaction (optional).** When **`ICoreAISettings.EnableLlmContextCompaction`** is `true` on **`CoreAISettingsAsset`**, overflowing history may be summarized by an auxiliary LLM call instead of the deterministic bullet rollup. The system is gated at two levels:

| Level | Toggle | Default |
|-------|--------|---------|
| **Global** | `CoreAISettingsAsset.EnableLlmContextCompaction` | `false` |
| **Per-role** | `AgentMemoryPolicy.RoleMemoryConfig.UseLlmContextCompaction` | `true` for most roles; `false` for **Programmer** |

Per-role override: **`AgentBuilder.WithLlmContextCompaction(bool)`** or **`AgentMemoryPolicy.ConfigureLlmContextCompaction(roleId, bool)`**. When the global toggle is off, all roles use deterministic compaction regardless of their per-role setting.

Compaction calls route through `ILlmClient.CompleteAsync` with role id **`__CoreAI_ContextCompaction`** and configurable options (`LlmContextCompactionOptions`). If the auxiliary LLM call fails, the system falls back to the deterministic bullet summary.

**Separation from the main system prompt:** The **full** orchestrator system string (built-in/custom role prompt, universal prefix, tool contract) and the memory tail are **not** fed into compaction. Only **persisted chat lines** (`IAgentMemoryStore.GetChatHistory` — typically `user` / `assistant` turns) plus the **prior rolling summary** are packed into that completion’s **`UserPayload`**; **`ChatHistory` on that request is `null`**. The compaction call uses **`LlmContextCompactionOptions.SystemPrompt`** (compact “you are a summarizer” instructions), which is unrelated to e.g. `Teacher`/`Creator` prose. After compaction, **`AiOrchestrator`** sends the new summary as a **`## Conversation Summary`** system-role message in the ordered tail of **`ChatHistory`** for the **primary** model turn (never in the cacheable system prefix) — that block is downstream output; it is not sent back through the compaction LLM unless it later ages into history as normal assistant/user text.

**Visible cuts (since 7.46.0).** Text CoreAI shortens before it reaches a model or a store carries a marker that names the count, and the cut is logged with its numbers (log previews and on-screen previews are not listed):

| Where | Limit | Marker in the text | Log (tag `Llm`) |
|---|---|---|---|
| Deterministic summary bullet (and the LLM-assisted bullet fallback) | 280 chars per folded message | `…[+N chars]` | one Info line per fold: `Folded X message(s) … Y bullet(s) clipped to 280 chars: T chars total -> K kept, D dropped.` |
| Compaction payload line | `LlmContextCompactionOptions.MaxPerMessageChars` (800) | `…[+N chars]` | one line per compaction call with the clipped-line count and dropped chars |
| Compaction payload | `MaxPayloadChars` (12000) | none: whole lines are added while they fit, the closing instruction always goes in | same line, as a Warning naming the deferred messages; the fold marker stops at the last line sent, so the next compaction folds the rest (they are not in that turn's prompt) |
| Compacted summary | `MaxSummaryChars` (4000, marker included) | `…[+N chars]` | Warning with total / kept / dropped |
| Summary sent with the request | `SummaryTokenBudget` reserve and the explicit `MaxRolledSummaryTokens` cap | leading `…` | Warning naming both sources: `~A by the context manager's MaxRolledSummaryTokens cap, ~B to fit the R-token request reserve; ~S tokens sent.` The cap fits one token short, so a bounded summary is not cut again on the next turn |
| Prompt history window | history token budget / `MaxChatHistoryMessages` | — | one line per turn when stored messages do not reach the model verbatim: `history window: N stored message(s) -> K sent verbatim[, P pruned (superseded tool results / exact duplicates)][, M folded into the rolling summary (~T tokens sent) \| , M left out by the window with no rolling summary …][; ≥1 older message(s) not sent (MaxChatHistoryMessages=N)].` Info for pruned / folded, Warning for a loss (the `MaxChatHistoryMessages` cap warns once per role and cap, then Info); messages the compactor deferred are counted as `D deferred to the next compaction`, not as folded. With summarization off the store applies `MaxChatHistoryMessages`; the orchestrator reads one extra message to detect it and sends the cap |
| Tool-loop replies | 200 chars of the last failure in the "tool calls in a row failed" reply; `MaxResponseChars` | `…[+N chars]` / `…[response truncated at M chars: +N chars]` | Info per cut |
| Lua mods (`manage_mods get_source`, revision previews, Lua result summaries / errors) | 16000 / 240 / `MaxResultSummaryLength` / `MaxErrorMessageLength` | `--[[ …[+N chars] ]]` / `…[+N chars]` | Info per cut (get_source, results, errors) |
| Tool description in the tool contract / schema in a retry hint | 500 / 1200 chars | `…[+N chars]` (depends only on the text, so the cached prefix stays byte-identical) | Info once per tool and length |
| Stored Lua / data snapshot in a versioning prompt | 6000 / 8000 / 5000 chars | `…[+N chars]` on its own line inside the fence | Info once per snapshot |
| Lua repair error / failed code | 500 / 1200 chars | `…[+N chars]` | Info on each repair |
| Tool results stored in history | 2000 chars per `Full` detail (head + tail), 240 per compact line | `...[truncated N chars]...` / `…[+N chars]` | one Info line per stored block with entries / total / kept / dropped |

Summaries persisted before 7.46.0 still re-detect their fold point: the legacy probes keep matching the old `...` bullets.

**LLMUnity as a local OpenAI server:** the LLMUnity backend does not call `LLMAgent.Chat()` in-process. Instead the `LLM` component runs the GGUF model as its **built-in OpenAI-compatible server** (`llm.remote = true` + `CoreAISettingsAsset.LlmUnityServerPort`, default 13333, set **before** the native service initializes) and CoreAI drives it through the **native HTTP pipeline** (`OpenAiChatLlmClient` over `LlmUnityServerHttpSettings` → `POST http://localhost:{port}/v1/chat/completions`). This yields **native structured `tool_calls`** (server-side jinja + grammar) and SSE streaming, identical to LM Studio / any OpenAI backend. Context management is **only** CoreAI's backend-agnostic compaction (above), which builds the whole prompt and sends it as ordinary OpenAI messages; the server never manages history. `agent.overflowStrategy` is forced to `None` (`LlmUnityHostConfigurator`); it has no effect because the agent's in-process `Chat()` path is not used.

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

**Implementation note:** `AgentMemoryPolicy.RoleMemoryConfig` and `AgentBuilder` default `WithChatHistory` to **true** with `MaxChatHistoryMessages = 30`, but **`PersistChatHistory` remains false** unless you pass `true` (for example **`PlainChat`** / **`SmartChat`** entries in the policy constructor, or `ConfigureChatHistory` / `AgentBuilder.WithChatHistory(..., persistBetweenSessions: true)`). This improves continuity without implying disk chat persistence. Use `AgentBuilder.WithoutChatHistory()` or `ConfigureChatHistory(roleId, enabled: false, ...)` for token-sensitive/tool-only roles.

**Token-cost note:** ChatHistory sends recent raw turns, so it costs more prompt tokens than compact MemoryTool facts. Keep durable facts in MemoryTool and disable ChatHistory explicitly for roles that must stay deterministic or very small.

### "The conversation is empty after a page reload / app restart" — expected, not a lost write

Worth stating next to the symptom, because the memory *document* does come back while the transcript
does not, which reads like a bug.

- The **memory document** (MemoryTool facts) is always written to disk, so it survives a restart.
- The **chat transcript** reaches disk only when the role sets `PersistChatHistory` /
  `WithChatHistory(persistBetweenSessions: true)`. `AppendChatMessage(..., persistToDisk: false)`
  means "this process only" and never reaches the file, not even during compaction.
- A **chat-source request** (`SourceTag = "Chat"`) **against a role with history off** (the built-in
  `Programmer`) borrows short-term history for that run only: `AiOrchestrator.ResolveRoleConfigForRequest`
  enables `WithChatHistory` and explicitly leaves `PersistChatHistory = false`, so nothing is written
  to disk and the next process starts from an empty transcript.
- Restoring the *visible* panel history additionally needs **Load Persisted Chat On Startup** (on by
  default) in the panel's `CoreAiChatConfig` (see `README_CHAT.md`).

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
AgentMemoryPolicy policy = new AgentMemoryPolicy();
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
AgentMemoryPolicy policy = new AgentMemoryPolicy();
policy.DisableMemoryTool("Merchant");    // PlainChat already has MemoryTool disabled by default
policy.SetMemoryToolForAll(false);        // Disable for ALL (ChatHistory only)
```

---

## Files

| File | Purpose |
|------|-----------|
| `AgentMemoryPolicy.cs` | Configuration: who uses which type |
| `IAgentMemoryStore.cs` | Store interface (+ ChatHistory methods) |
| `AgentMemoryState.cs` | State: Memory, LastSystemPrompt, Versions, system-prompt memory snapshot |
| `MemoryTool.cs` | Microsoft.Extensions.AI function for the model |
| `NullAgentMemoryStore.cs` | Stub (saves nothing) — portable default when **`RegisterCorePortable`** is used without a host store; not the default for **`CoreAILifetimeScope`** (which registers **`FileAgentMemoryStore`**, WebGL player included) |
| `InMemoryAgentMemoryStore.cs` | Process-only memory, flat chat, structured transcript and atomic mutation backing selected by `AgentMemoryPersistenceMode.SessionOnly` |
| `FileAgentMemoryStore.cs` | Unity: `<stem>.json` (memory document) + `<stem>.history.jsonl` (conversation) under `persistentDataPath/CoreAI/AgentMemory`; keeps at most 500 chat messages and 2000 transcript rows per key by default (**Chat History Message Cap** / **Transcript Entry Cap** on `CoreAILifetimeScope`) |
| `AgentMemoryScope.cs` | Tenant/user/session/topic scope and the single role-to-storage-key mapping |
| `ScopedAgentMemoryStoreDecorator.cs` | The `IAgentMemoryStore` the container hands out: applies the scope key, and `ClearChatHistory` also clears the rolling summary |
| `MEMORY_STORE_CUSTOM_BACKENDS.md` | PlayerPrefs / cloud / composite `IAgentMemoryStore` patterns |
| `AiOrchestrator.cs` | Orchestrator: reads the history once per request, sends memory and summary as tail messages, appends the turn |
| `MemoryLlmTool.cs` | `ILlmTool` wrapper that exposes `MemoryTool` to the model |

### Clearing saves in the Editor

**CoreAI → Delete All Persistent Saves...** (only when **not** in Play Mode) deletes the entire **`Application.persistentDataPath/CoreAI`** tree. The confirmation dialog names **AgentMemory** (memory documents + persisted chat), **ConversationSummaries**, **LuaScriptVersions** and **DataOverlayVersions**, but every other CoreAI folder there goes too (skills, Lua mods and mod logs, world state, the audit log, saved endpoint settings). Use for a clean persistence baseline while testing.
