# `CoreAi` — static API for everyone

A single class **`CoreAI.CoreAi`** — one entry point to the LLM and orchestrator. You do not need to know VContainer, write your own singleton, or resolve services on the scene by hand.

| Audience | What you get |
|------|----------------|
| **Beginner** | Copy the 3 steps below → `await CoreAi.AskAsync(...)` in a script on an object. |
| **Experienced developer** | The same static API for prototypes and UI; for larger architecture — `TryGet*` + DI, see [Professional stack](#5-professional-stack). |

---

## Minimum for beginners (3 steps)

1. **Scene with CoreAI** — menu **CoreAI → Setup → Create Chat Demo Scene** or **CoreAI → Create Scene Setup** (includes `CoreAILifetimeScope`).
2. **Backend** — in `CoreAISettings` set HTTP (LM Studio) or LLMUnity; see [QUICK_START](QUICK_START.md).
3. **Code** — on any `MonoBehaviour`:

```csharp
using CoreAI;

public class MyNpc : MonoBehaviour
{
    async void OnPlayerTalk()
    {
        if (!CoreAi.IsReady) { Debug.LogWarning("No CoreAILifetimeScope on scene"); return; }

        string reply = await CoreAi.AskAsync("How are you?");
        Debug.Log(reply);
    }
}
```

Streaming “like chat” — one loop line:

```csharp
await foreach (string part in CoreAi.StreamAsync("Tell me about the quest", "PlayerChat"))
    uiLabel.text += part;
```

Done. The `"PlayerChat"` role must match `AgentBuilder` / chat config if you configured agents.

### Sending messages: convenient in UI and from code

| How you interact | What you press / write | Where the request goes |
|------------------|------------------------|------------------|
| **Chat window** (`CoreAiChatPanel`) | Send button, **Shift+Enter** (default) or **Enter** — depending on `CoreAiChatConfig.SendOnShiftEnter` | `CoreAiChatService` → same `ILlmClient` as `CoreAi` |
| **Script** (NPC, quest, “Ask” button) | `CoreAi.AskAsync("text")` or `CoreAi.StreamAsync` — that is how a user request is sent to the LLM | Same `CoreAiChatService` inside `CoreAi` |

Both paths use **one** `CoreAILifetimeScope` registered on the scene and **one** backend configuration. The only difference is UX: in the panel you type in a field; in code you pass a string to a method. Brushes/streaming/roles — see [README_CHAT](../Runtime/Source/Features/Chat/README_CHAT.md) and [STREAMING_ARCHITECTURE](STREAMING_ARCHITECTURE.md).

**Summary:** for the player in chat — built-in panel; for game logic without a widget — `CoreAi`. Together **CoreAI + CoreAiUnity** cover “convenient everywhere”: demo scene in one click, hotkeys in the Inspector, and one line of `CoreAi` on any `MonoBehaviour`.

---

## Quick cheat sheet (all methods)

| Method | Returns | When to use |
|-------|------------|-------------------|
| `AskAsync` | `Task<string?>` | You need the **full answer as one string** (logic, save, simple NPC). |
| `StreamAsync` | `IAsyncEnumerable<string>` | **Live text** in UI (label, TMP, UI Toolkit). |
| `StreamChunksAsync` | `IAsyncEnumerable<LlmStreamChunk>` | You need **IsDone, Error, usage** per chunk. |
| `SmartAskAsync` | `Task<string?>` | Both **stream to UI** and **full string** at the end (analytics, quests). Stream mode follows the settings hierarchy. |
| `OrchestrateAsync` | `Task<string?>` | Full **game pipeline**: session snapshot, authority, queue, validation, **publishing a command** to the bus. |
| `OrchestrateStreamAsync` | `IAsyncEnumerable<LlmStreamChunk>` | Same, but **tokens as they generate** + final publish after the stream. |
| `OrchestrateStreamCollectAsync` | `Task<string>` | Stream + **assemble full text** + `onChunk` for UI. |
| `StopAgent` | `void` | **Cancel generation** and running agent tasks. |
| `ClearContext` | `void` | **Clear memory** (chat + long-term). |
| `IsReady` | `bool` | Whether the API can be called (scope + services). |
| `Invalidate()` | `void` | After **scene change** or in tests — clear cache. |
| `TryGetChatService` / `TryGetOrchestrator` | `bool` | **No exceptions**: check before a UI button or optional AI. |
| `GetChatService` / `GetOrchestrator` / `GetSettings` | services | Direct access when you need full control. |

Detailed scenarios — section [When to use what](#2-when-to-use-what-in-detail) below.

---

## 1. For beginners: common questions

**Why does it fail or nothing happens?**  
Ensure the **active** scene has a GameObject with **`CoreAILifetimeScope`**. After `LoadScene` call `CoreAi.Invalidate()` or check `CoreAi.IsReady` / `CoreAi.TryGetChatService(out _)`.

**How is `AskAsync` different from `OrchestrateAsync`?**  
- `AskAsync` — **chat**: prompt + role history, text answer.  
- `OrchestrateAsync` — **game task**: session snapshot, roles like Creator, publishing a **JSON command** to the game bus. For “talk to NPC” you usually use `Ask` / `Stream`.

**Can I call outside `async void`?**  
You can from `Start` with `StartCoroutine` + wrapper, but simpler — **`async void` on the Unity main thread** or **UniTask**. Do not use `Task.Run` — LLM calls must stay on the **main thread** (see [STREAMING_ARCHITECTURE](STREAMING_ARCHITECTURE.md)).

**Where do I get `roleId`?**  
The same id as in `AgentBuilder("...")` and `CoreAiChatConfig`. Often `"PlayerChat"`.

---

## 2. When to use what (in detail)

| Layer | Method | What it does | When to pick it |
|------|-------|------------|-------------|
| **Chat** | `CoreAi.AskAsync` | Waits for full answer, chat history by role. | Simple dialogue, log, “one line”. |
| **Chat** | `CoreAi.StreamAsync` | String chunks. | Caption / chat, “typing” effect. |
| **Chat** | `CoreAi.StreamChunksAsync` | `LlmStreamChunk` with metadata. | Errors, `IsDone`, tokens. |
| **Chat** | `CoreAi.SmartAskAsync` | Chooses stream or not; `onChunk` + full text. | UI + saving full answer. |
| **Orchestrator** | `CoreAi.OrchestrateAsync` | Snapshot → prompt → authority → queue → validation → **ApplyAiGameCommand**. | Creator / Programmer agents, scenarios with commands. |
| **Orchestrator** | `CoreAi.OrchestrateStreamAsync` | Same, but tokens along the way. | Long quest text + command at the end. |
| **Orchestrator** | `CoreAi.OrchestrateStreamCollectAsync` | Stream + accumulate `string` + `onChunk`. | Combine live UI and post-processed string. |

---

## 3. Code recipes

### 3.1. Simple question — one line

```csharp
string answer = await CoreAi.AskAsync("Hi! How old are you?");
Debug.Log(answer);
```

### 3.2. Streaming in UI Toolkit / TextMeshPro

```csharp
label.text = "";
await foreach (string chunk in CoreAi.StreamAsync("Tell a joke", "PlayerChat"))
    label.text += chunk;
```

### 3.3. Smart: chunks in UI and full text in a variable

```csharp
string full = await CoreAi.SmartAskAsync(
    "Tell a story",
    roleId: "PlayerChat",
    onChunk: c => label.text += c);

SaveToPlayerJournal(full);
```

Override streaming: `uiStreamingOverride: false` — force full response in one piece.

### 3.4. Safe call (no try when AI is absent)

```csharp
if (CoreAi.TryGetChatService(out var chat))
{
    string reply = await chat.SendMessageAsync("Hi", "PlayerChat", ct);
}
else
{
    // AI disabled or scene without scope — show default NPC text
}
```

### 3.4b. Agent control API

```csharp
// Stop generation (e.g. Stop button in UI)
CoreAi.StopAgent("PlayerChat");

// Clear chat history but keep long-term memory (facts, quests)
CoreAi.ClearContext("PlayerChat", clearChatHistory: true, clearLongTermMemory: false);

// Full hard reset (amnesia)
CoreAi.ClearContext("PlayerChat", clearChatHistory: true, clearLongTermMemory: true);
```

### 3.5. Orchestrator: command into the game

```csharp
var task = new AiTaskRequest
{
    RoleId = "Creator",
    Hint = "Generate JSON spawn command",
    Priority = 5,
    CancellationScope = "creator"
};

string json = await CoreAi.OrchestrateAsync(task);
```

### 3.6. Orchestrator with stream to a status line

```csharp
var task = new AiTaskRequest { RoleId = "Creator", Hint = "Explain the step" };

string full = await CoreAi.OrchestrateStreamCollectAsync(task,
    onChunk: c => statusLine.text += c);
```

---

## 4. Lifecycle and scenes

- **`CoreAi`** caches a reference to `CoreAILifetimeScope` and services.
- **`SceneManager.sceneLoaded` / `OnDestroy` on unload** — call **`CoreAi.Invalidate()`**, otherwise you may keep a stale container.
- **EditMode / PlayMode tests** — in `[SetUp]`: `CoreAi.Invalidate()`.
- **`GetSettings()`** — may return `null` if the scope is not ready yet; for global defaults also use static `CoreAISettings` from the portable core if configured.

---

## 5. Professional stack

**Static API is not an “anti-pattern” for CoreAI:** it is the **official facade** over VContainer. It:

- forwards calls to `CoreAiChatService` and `IAiOrchestrationService` without duplicating logic;
- respects the same `ILlmClient`, queue, logs, and metrics as manual resolution.

**When to keep `CoreAi` everywhere:** prototypes, tools, scene `MonoBehaviour`, menu buttons, tutorial scenes.

**When to inject interfaces (DI):** large codebase, **unit tests** without a scene, multiple scopes, strict module isolation. Pattern:

```csharp
// Registration (in your LifetimeScope)
builder.Register<QuestAiController>(Lifetime.Transient)
    .WithParameter<Func<CoreAiChatService?>>(() => {
        if (CoreAi.TryGetChatService(out var s)) return s;
        return null;
    });
// or
builder.Register<QuestAiController>(Lifetime.Transient)
    .WithParameter<ILlmClient>(c => c.Resolve<ILlmClient>());
```

`CoreAi.GetChatService()` remains a convenient **adapter** at the “object script ↔ core” boundary.

**Extending behavior:** register a wrapper in the container; if it is the same type `CoreAiChatService.TryCreateFromScene` expects, you may need explicit registration — for fine control use **direct** `IObjectResolver` in your `LifetimeScope` and call services from there; the `CoreAi` facade stays valid for the **default** path.

---

## 6. Main thread (required)

```csharp
// OK — from MonoBehaviour, main thread
async void OnEnable() {
  await foreach (var c in CoreAi.StreamAsync("Hi")) t.text += c;
}

// DO NOT — worker thread + UnityWebRequest
_ = Task.Run(() => _ = CoreAi.AskAsync("x"));
```

---

## 7. Related docs

| Document | Contents |
|----------|------------|
| [QUICK_START](QUICK_START.md) | Install, scene, backend |
| [README_CHAT](../Runtime/Source/Features/Chat/README_CHAT.md) | Chat panel, styles, events |
| [STREAMING_ARCHITECTURE](STREAMING_ARCHITECTURE.md) | SSE, LLMUnity, orchestrator stream, limits |
| [DOCS_INDEX](DOCS_INDEX.md) | Full documentation map |

**Version:** see `Assets/CoreAiUnity/package.json` — in release changelogs for `CoreAi`, see *Singleton API* / *Orchestrator streaming*.
