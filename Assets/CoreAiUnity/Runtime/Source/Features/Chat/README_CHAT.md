# 🗨️ CoreAI Universal Chat Module

Built-in AI chat for any Unity game. Works out of the box with UI Toolkit.

Regardless of UI, you can call the LLM from any script via the static **`CoreAi`** facade — see [COREAI_SINGLETON_API.md](../../../Docs/COREAI_SINGLETON_API.md) (cheat sheet, beginner FAQ, tips for experienced developers).

**Sending in two modes**

- **Chat panel** — the player types in the field and presses the button or **Shift+Enter** (key behavior is configured in `CoreAiChatConfig`, see *Manual setup*). Replies appear in the same bubbles; streaming follows the flags.
- **Code without the panel** — “send” = `CoreAi.AskAsync("...")` / `CoreAi.StreamAsync` with the same text. Under the hood it uses the same `CoreAiChatService` as the panel while there is a single `CoreAILifetimeScope` in the scene.

The “UI vs code” table is in [COREAI_SINGLETON_API](../../../Docs/COREAI_SINGLETON_API.md) (subsection *Sending messages: convenient from UI and from code*).

## Quick start (1 click)

The menu `CoreAI → Setup → Create Chat Demo Scene` creates a ready scene `Assets/CoreAiUnity/Scenes/CoreAiChatDemo.unity` with all required objects (camera, light, EventSystem, `CoreAILifetimeScope`, `UIDocument` + `CoreAiChatPanel` with default `CoreAiChatConfig_Demo`). Press Play and chat with the model.

## Manual setup (2 steps)

### 1. Create a config
`Assets → Create → CoreAI → Chat Config`

Configure in the Inspector:
- **Role ID** — agent role (`PlayerChat`, `Teacher`, your custom one)
- **Header Title** — chat header
- **Welcome Message** — welcome message
- **Session / history** (since 0.25.4) — see [session restore](#persisted-chat-session)
- **Programmatic submit** (since 0.25.5) — see [`SubmitMessageFromExternalAsync`](#programmatic-chat-submit)
- **Enable Streaming** — streamed generation of replies
- **Send On Shift+Enter** — send hotkey
- **Hotkeys** (since 0.25.3) — see [below](#chat-hotkeys)

### 2. Add to the scene

1. Create a `GameObject` with `UIDocument`
2. Assign UXML: `Packages/com.nexoider.coreaiunity/Runtime/Source/Features/Chat/UI/CoreAiChat.uxml`
3. Add the `CoreAiChatPanel` component
4. Assign your `CoreAiChatConfig`
5. **Done!** The chat uses the current CoreAI backend

<a id="persisted-chat-session"></a>

## Session restore (history after restart) — since 0.25.4

By default **`CoreAiChatPanel`** on enable (`OnEnable`) loads saved chat history for the **`Role ID`** from the config into the message list.

| Field in **Chat Config** | Purpose |
|--------------------------|---------|
| **Load Persisted Chat On Startup** | When enabled (default **yes**) — before the welcome message, history is read from **`IAgentMemoryStore`** (`FileAgentMemoryStore`: `persistentDataPath/CoreAI/AgentMemory/<RoleId>.json`, field `chatHistoryJson`). |
| **Max Persisted Messages For Ui** | How many **last** messages to show on load; **0** = all saved. |

**Requirements:** for the role, **`WithChatHistory`** and **`PersistChatHistory`** must be enabled in `AgentMemoryPolicy`. The built-in **`PlayerChat`** role has this enabled by default, so the drop-in chat restores its session after app restart. For custom chat roles (e.g. `Teacher`), call `AgentBuilder.WithChatHistory(..., persistBetweenSessions: true)`; otherwise history is not written to disk — there is nothing to load (only **Welcome Message** remains).

**Welcome message:** if after load the scroll area **already has** messages, **Welcome Message** is not added (to avoid duplicating “Hello!” on top of the dialog). If there is no history, the welcome message is shown as before.

**Repeated `OnEnable`:** before hydration the list is **cleared**, then the store is read again — duplicates do not accumulate when toggling the panel object.

**Extension:** override **`HydrateStartupMessagesFromStore`** or **`TryAppendPersistedChatHistoryFromStore`** if you need a custom message source.

**Custom persistence:** chat hydration reads whatever `IAgentMemoryStore.GetChatHistory` returns (default: `FileAgentMemoryStore`). To use **PlayerPrefs** or **cloud** for the same contract (session + MemoryTool), see [`Docs/MEMORY_STORE_CUSTOM_BACKENDS.md`](../../../../Docs/MEMORY_STORE_CUSTOM_BACKENDS.md).

## Panel collapse (FAB) — since 0.21.7

On narrow screens (width ≤ 720 or height ≤ 560) the chat **starts collapsed** by default: only the circular **`coreai-chat-fab`** button is visible in the bottom-right. The **`coreai-chat-collapse`** button (`—`) in the header collapses the panel back to the FAB.

- **Persistence:** collapsed/expanded choice is stored in `PlayerPrefs` under `CoreAI.Chat.Collapsed` (integer: `1` = collapsed). If the key is unset, the mobile layout defaults to collapsed.
- **API from code:**
  - `bool IsCollapsed { get; }`
  - `void SetCollapsed(bool collapsed, bool persist = true)` — expand before a cutscene or collapse after; with `persist: false` the state is not written to `PlayerPrefs`.
- **UXML:** elements `coreai-chat-collapse` (in `coreai-chat-header`) and `coreai-chat-fab` (root, before `coreai-chat-root`).
- **USS:** `.coreai-chat-header-btn`, `.coreai-collapsed` on the container, `.coreai-chat-fab` / `.coreai-chat-fab-icon`.

Custom layout: if you **copy** UXML into your project, add the same element names or override bindings in a subclass of `CoreAiChatPanel` (override `BindUI` and call `base.BindUI()` or duplicate the logic).

<a id="chat-hotkeys"></a>

## FAB / Esc hotkeys (configured in `CoreAiChatConfig`) — since 0.25.3

All toggles are in the **CoreAI → Chat Config** asset on the panel (`CoreAiChatPanel.config`):

| Field | Purpose |
|-------|---------|
| **Enable Open Chat Keyboard Shortcut** | When off — collapsed (FAB) chat opens **only by clicking** the FAB, no key. |
| **Open Chat Hotkey** | `KeyCode` to open collapsed chat (default **C**). For **A–Z**, both key code and typed character are considered (without Ctrl / Cmd / Alt). |
| **Enable Escape Chat Shortcuts** | When off — **Esc** is not handled by the panel (useful if Esc is fully reserved for FPS / pause). |

### From code (on top of config)

`CoreAiChatPanel` has **runtime overrides** (they take precedence over `CoreAiChatConfig` until cleared):

| Method / property | Purpose |
|-------------------|---------|
| `SetRuntimeEscapeChatShortcutsEnabled(false)` | Fully disable Esc for chat (stop generation + collapse) without changing the asset. |
| `SetRuntimeEscapeChatShortcutsEnabled(null)` | Follow **Enable Escape Chat Shortcuts** in config again. |
| `SetRuntimeOpenChatKeyboardShortcutEnabled(bool?)` | Enable/disable the key to open collapsed chat. |
| `SetRuntimeOpenChatHotkey(KeyCode?)` | Change the open key at runtime. |
| `ClearRuntimeHotkeyOverrides()` | Clear all three overrides. |
| `EffectiveOpenChatKeyboardShortcutEnabled`, `EffectiveOpenChatHotkey`, `EffectiveEscapeChatShortcutsEnabled` | Effective behavior (config + overrides). |

Example: give Esc only to the player while the world map is open:

```csharp
void OnWorldMapOpened()
{
    chatPanel.SetRuntimeEscapeChatShortcutsEnabled(false);
}

void OnWorldMapClosed()
{
    chatPanel.SetRuntimeEscapeChatShortcutsEnabled(null); // or true
}
```

**Default behavior (both flags on):**

- While chat is **collapsed** — the configured key opens the panel (handled in `OnRootKeyDown` at `TrickleDown` from `UIDocument` root).
- While chat is **expanded** — **Esc** first stops active generation (if a request/stream is in progress), otherwise **collapses** the panel to the FAB.
- Additionally **`Update()`** polls **Legacy `Input.GetKeyDown`** only when the UITK root has **no** focused element (`Root.focusController.focusedElement == null`) — so it coexists with character control when focus is not in UI. On **WebGL**, the same `Update()` still resets `WebGLInput.captureAllKeyboardInput`.

**Limitation:** if *Player Settings* use only the **New Input System** without Legacy, `Input.*` is unavailable — the `Update` branch is skipped silently; keys still work while keyboard focus is in the UITK tree (or add your own input layer / `Both` in Active Input Handling).

**Gameplay integration:** after each `SetCollapsed`, `protected virtual void OnCollapsedStateChanged(bool collapsed)` is called — in a subclass you can hook pause, cursor, etc., without pulling game controllers into CoreAI.

Subclasses that override **`Update()`** must call **`base.Update()` first** (otherwise you lose the WebGL fix and hotkey polling).

<a id="programmatic-chat-submit"></a>

## Programmatic submit from code (cutscene, quest, world button) — since 0.25.5

Get a reference to the panel (`GetComponent<CoreAiChatPanel>()`, scene singleton, etc.) and call:

```csharp
using CoreAI.Chat;
using System.Threading;
using System.Threading.Tasks;

// Normal path: user bubble in chat + LLM request (same as after typing in the field)
string? reply = await chatPanel.SubmitMessageFromExternalAsync(
    "Tell me about the quest",
    cancellationToken: CancellationToken.None);

// Silent call: do not duplicate text in UI, orchestrator only
var opt = new CoreAiChatExternalSubmitOptions { AppendUserMessageToChat = false };
reply = await chatPanel.SubmitMessageFromExternalAsync("Secret context for the model", opt);

// Narrative in UI only, no LLM (simulated assistant reply)
var fake = new CoreAiChatExternalSubmitOptions
{
    AppendUserMessageToChat = true,
    SimulatedAssistantReply = "Welcome to the city!"
};
reply = await chatPanel.SubmitMessageFromExternalAsync("…", fake);
```

| Field `CoreAiChatExternalSubmitOptions` | Default | Purpose |
|---------------------------------------|---------|---------|
| **`AppendUserMessageToChat`** | `true` | Add a **user** bubble with the request text before the turn. |
| **`SimulatedAssistantReply`** | `null` | If set to a non-empty string — **LLM is not called**; an assistant bubble with this text is appended (after think strip and `FormatResponseText`). |

**Return value:** assistant reply string (including simulated), or `null` if the panel is busy with another request, text after `OnMessageSending` is empty, or the operation was cancelled.

**Hooks:** `OnMessageSending` is still invoked; for a real reply — `OnResponseReceived` / **`OnAiResponseCompleted`**.

## Stopping generation (Stop) — since 0.22.0

Since **0.25.5** there is no separate “stop” button in the **header** — stop only via the send button and Esc (below).
Since **0.25.6** the stop path is hardened for streaming and fast backends/stub: the button stays enabled while it shows `X`, busy state is set until the first `await`, and the active request CTS is cancelled even if `CoreAi.StopAgent(roleId)` is unavailable.

Since **0.25.14**:

- **Send (Enter) vs stop:** While a turn is in progress, pressing **Enter** (or your configured send shortcut in the text field) does **not** stop generation — it is ignored until the turn completes. **Stop** is only the send button while it shows **`X`**, plus **Esc** when **Enable Escape Chat Shortcuts** is on. This prevents accidental cancellation during the orchestrator tail (e.g. chat history append + `ApplyAiGameCommand`) after the last streamed token.
- **Busy until enumerator ends:** The panel treats the request as active until the streaming **enumerator** fully finishes (not merely when the model emits a terminal chunk). That keeps the UI and orchestrator in sync with `QueuedAiOrchestrator` / `AiOrchestrator` post-stream work.

During active generation `CoreAiChatPanel` switches the send button to stop mode:

- button label: `X` instead of `>`;
- tooltip: `Stop generation (Esc)`;
- style: red (`.coreai-chat-send-button-stop`).

Stop the current reply in two ways:

- press the send button again (in `Stop` mode);
- press `Esc` while the chat is generating (if **Enable Escape Chat Shortcuts** is on in `CoreAiChatConfig`).

Under the hood this calls `CoreAi.StopAgent(roleId)` and cancels the active request `CancellationToken`, so both current generation and queued orchestrator tasks for that role stop.
After stop, `CoreAiChatPanel` immediately clears streaming UI (`FinishStreaming` / `HideTypingIndicator`) and resets `_isSending` / `_isStreaming`; covered by `CoreAiChatPanelEditModeTests` and `CoreAiChatPanelStopPlayModeTests`.

### Persisted history + assistant display (since 0.25.14)

- **Hydrated user messages:** If the store contains the composer JSON shape (`{"telemetry":...,"hint":"...","ai_task_source":"..."}`), the UI shows only the **`hint`** string in the user bubble (same text the player typed conceptually).
- **Assistant bubbles:** Leading spaces and newlines at the start of a reply are trimmed for display so the first line does not sit under a blank “gap”.

## Clearing context from UI

The **`*`** button in the header (`coreai-chat-clear`) calls `ClearChat()`:

- clears messages in the UI;
- by default clears only chat history (`clearChatHistory: true`, `clearLongTermMemory: false`).

For manual granular control, use the overload:

```csharp
// Short-term context only (same as default clear button)
chatPanel.ClearChat(clearChatHistory: true, clearLongTermMemory: false);

// Full reset: short-term context + long-term memory
chatPanel.ClearChat(clearChatHistory: true, clearLongTermMemory: true);

// Long-term memory only
chatPanel.ClearChat(clearChatHistory: false, clearLongTermMemory: true);
```

## Prompt architecture (3 layers)

| Layer | Source | Example |
|-------|--------|---------|
| 1 | `CoreAISettings.universalSystemPromptPrefix` | "Keep answers short. Do not discuss forbidden topics." |
| 2 | `.txt` file via `AgentPromptsManifest` | `TeacherSystemPrompt.txt` |
| 3 | `AgentBuilder.WithSystemPrompt()` | "You are teaching a student about: for loops" |

Final prompt = `Layer 1` + `\n` + `Layer 2` + `\n\n` + `Layer 3`

### Overriding universalPrefix

By default **universalPrefix applies to all roles**. For a fully custom prompt without shared rules, use `.WithOverrideUniversalPrefix()`:

```csharp
// Regular agent — prefix + base + additional (all 3 layers)
new AgentBuilder("Teacher")
    .WithSystemPrompt("You are a Python teacher.")
    .Build();

// Custom agent — WITHOUT universalPrefix (base + additional only)
new AgentBuilder("JsonParser")
    .WithSystemPrompt("You are a strict JSON parser.")
    .WithOverrideUniversalPrefix()  // ← prefix skipped
    .Build();
```

## Streaming (both backends)

| Backend | Mechanism | Real streaming? |
|---------|-----------|-----------------|
| **HTTP API** (OpenAI, LM Studio) | SSE (`stream: true`) → parsing `data:` chunks | ✅ Yes (Standalone / Editor); ⚠️ not on WebGL — see below |
| **LLMUnity** (local GGUF) | `LLMAgent.Chat(callback)` → deltas via ConcurrentQueue | ✅ Yes |

> ⚠️ **WebGL caveat (0.25.x).** In a built WebGL player the `UnityWebRequest` wrapper
> (emscripten `XMLHttpRequest`) does not deliver SSE incrementally — all chunks arrive in one block at
> the end of the request (`chunks=1` in `LLM ◀ (stream)` logs). Because of that the typing indicator may hang
> and the bubble may not appear. **Workaround:** under `#if UNITY_WEBGL && !UNITY_EDITOR` force
> `CoreAiChatConfig.EnableStreaming = false` (any non-streaming path works correctly).
> Full fix plan (including `.jslib` fetch bridge) — [`STREAMING_WEBGL_TODO.md`](../../../../Docs/STREAMING_WEBGL_TODO.md).

### Streaming + Tool Calling (single-cycle)

If in a streaming response the model first emits tool-call JSON, CoreAI runs a single cycle:

1. receives the stream and detects tool-call payload;
2. runs the matching tool;
3. appends the tool result to dialog history;
4. continues generation with the next streaming step.

Tool JSON is not rendered in UI: the player only sees the final readable assistant reply.

This behavior is on by default for roles with tools:

- `AgentMode.ToolsAndChat`
- `AgentMode.ToolsOnly`

For `AgentMode.ChatOnly` the usual streaming flag hierarchy applies (UI / per-agent / global).

### Streaming settings hierarchy

Priority order (highest to lowest):

1. **UI flag** — `CoreAiChatConfig.EnableStreaming` (chat panel Inspector). If off → always non-streaming; other layers ignored.
2. **Per-agent override** — `AgentBuilder.WithStreaming(true/false)` (registered in `AgentMemoryPolicy`).
3. **Global** — `ICoreAISettings.EnableStreaming` (checkbox in `CoreAISettings.asset`).

```csharp
// Example: chat agent always streams regardless of global setting
new AgentBuilder("PlayerChat")
    .WithSystemPrompt("You are a friendly assistant.")
    .WithStreaming(true)
    .Build();

// Example: parser agent never streams (needs full JSON at once)
new AgentBuilder("JsonParser")
    .WithSystemPrompt("You are a strict JSON parser.")
    .WithOverrideUniversalPrefix()
    .WithStreaming(false)
    .Build();
```

Programmatic check of effective value:

```csharp
var chatService = CoreAiChatService.TryCreateFromScene();
bool useStream = chatService.IsStreamingEnabled("PlayerChat", uiFallback: true);
```

### Think-block filtering

Reasoning models (DeepSeek, Qwen3) emit `<think>...</think>` blocks. CoreAI automatically:
- **When streaming**: shared stateful filter `CoreAI.Ai.ThinkBlockStreamFilter` removes blocks **even if open/close tags are split across SSE chunks**. While the model “thinks”, the typing indicator is shown.
- **Non-streaming**: regex strips `<think>` blocks from the final reply.
- **Tool calls**: not shown in chat (handled inside the MEAI pipeline, including streaming single-cycle).

> Streaming must be invoked from the **Unity main thread** (from a coroutine, `async void`, `UniTask`, or a normal async method in UI). Wrapping `CompleteStreamingAsync` in `Task.Run` will throw `"Create can only be called from the main thread"` because `UnityWebRequest` is created off the main thread.

## Extension via inheritance

```csharp
public class MyGameChatPanel : CoreAiChatPanel
{
    // Intercept send (validation, modification)
    protected override string OnMessageSending(string text)
    {
        if (text.Contains("bad word")) return null; // cancel
        return text;
    }

    // Post-process reply (markdown, analytics)
    protected override string FormatResponseText(string rawText)
    {
        return MarkdownRenderer.Render(rawText);
    }

    // Fully custom message layout
    protected override VisualElement CreateMessageBubble(string text, bool isUser)
    {
        var bubble = base.CreateMessageBubble(text, isUser);
        // Add your classes, animations, icons...
        return bubble;
    }
}
```

## Programmatic use (no UI)

### Option 1 — static `CoreAi` facade (recommended)

```csharp
// Synchronous-style await
string answer = await CoreAi.AskAsync("Hello!", roleId: "Teacher");

// Streaming (chunks as generated)
await foreach (string chunk in CoreAi.StreamAsync("Tell me about Python", "Teacher"))
    myTextLabel.text += chunk;

// Smart — picks mode, calls onChunk
string full = await CoreAi.SmartAskAsync(
    "Question", "Teacher", onChunk: c => myTextLabel.text += c);

// Full orchestrator pipeline (history + authority + publish command):
string json = await CoreAi.OrchestrateAsync(
    new AiTaskRequest { RoleId = "Creator", Hint = "spawn JSON" });

// Streaming orchestrator:
await foreach (var chunk in CoreAi.OrchestrateStreamAsync(
    new AiTaskRequest { RoleId = "Creator", Hint = "explain" }))
{
    if (!string.IsNullOrEmpty(chunk.Text)) statusLabel.text += chunk.Text;
    if (chunk.IsDone) break;
}
```

Details: [`COREAI_SINGLETON_API.md`](../../../Docs/COREAI_SINGLETON_API.md).

### Option 2 — direct service access

```csharp
var chatService = CoreAiChatService.TryCreateFromScene();

// Non-streaming
string response = await chatService.SendMessageAsync("Hello!", "Teacher");

// Streaming
await foreach (var chunk in chatService.SendMessageStreamingAsync("Tell me about Python", "Teacher"))
{
    myTextLabel.text += chunk.Text;
}
```

## Agent setup

```csharp
// In your LifetimeScope:
var config = new AgentBuilder("Teacher")
    .WithSystemPrompt("You are a Python teacher for school students.")  // Layer 3
    .WithChatHistory(4096, persistBetweenSessions: true)
    .WithMemory()
    .Build();
config.ApplyToPolicy(policy);
```

## VContainer / MessagePipe integration

In VContainer projects the recommended architecture:

```
CoreAiChatPanel (UI) → events → ChatPresenter (VContainer)
    → MessagePipe → SendMessageUseCase (Application)
```

`CoreAiChatPanel` raises `OnUserMessageSent` and `OnAiResponseCompleted`.
`ChatPresenter` (VContainer `IStartable`) subscribes and routes via `MessagePipe`.

## Custom styles

Create your own `.uss` file and assign it to `CoreAiChatPanel.customStyleSheet`.
All CSS classes use the `coreai-` prefix to avoid clashes:

| Class | Description |
|-------|-------------|
| `.coreai-chat-container` | Chat container |
| `.coreai-chat-container.coreai-collapsed` | Container hidden (FAB mode) |
| `.coreai-chat-header` | Header |
| `.coreai-chat-header-btn` | Collapse button in header |
| `.coreai-chat-fab` | Floating “open chat” button |
| `.coreai-chat-fab-icon` | Icon inside FAB |
| `.coreai-ai-message` | AI bubble |
| `.coreai-user-message` | User bubble |
| `.coreai-streaming-active` | Active streaming |
| `.coreai-chat-send-button` | Send button |
| `.coreai-typing-message` | “Typing…” indicator |

## Events

```csharp
var chatPanel = GetComponent<CoreAiChatPanel>();
chatPanel.OnUserMessageSent += (text) => Analytics.Track("chat_message", text);
chatPanel.OnAiResponseCompleted += (response) => Analytics.Track("ai_response", response);
```

