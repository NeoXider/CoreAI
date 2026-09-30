# `CoreAi` — static API for everyone

A single class **`CoreAI.CoreAi`** — one entry point to the LLM and orchestrator. You do not need to know VContainer, write your own singleton, or resolve services on the scene by hand.

| Audience | What you get |
|------|----------------|
| **Beginner** | Copy the 3 steps below → `await CoreAi.AskAsync(...)` in a script on an object. |
| **Experienced developer** | The same static API for prototypes and UI; for larger architecture — `TryGet*` + DI, see [Professional stack](#5-professional-stack). |

---

## Minimum for beginners (3 steps)

1. **Scene with CoreAI** — menu **CoreAI → Setup → Create Chat Demo Scene** (full UI demo) or **CoreAI → Setup → Create Bare Scene (advanced)** (`CoreAILifetimeScope` + assets only).
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
await foreach (string part in CoreAi.StreamAsync("Tell me about the quest", "SmartChat"))
    uiLabel.text += part;
```

Done. The `"SmartChat"` role must match `AgentBuilder` / chat config if you configured agents.

### Sending messages: convenient in UI and from code

| How you interact | What you press / write | Where the request goes |
|------------------|------------------------|------------------|
| **Chat window** (`CoreAiChatPanel`) | Send button or **Enter** (default; **Shift+Enter** inserts a newline). With `CoreAiChatConfig.SendOnShiftEnter` on, **Shift+Enter** sends instead | `CoreAiChatService` → same `ILlmClient` as `CoreAi` |
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
| `AskAsync` / `StreamAsync` / `StreamChunksAsync` / `SmartAskAsync` **+ attachments** | same as above | Send **images and files** with the prompt (one `AiAttachment` or a list) — see [3.7](#37-send-prompts-images-and-files). |
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
- `OrchestrateAsync` — **game task**: session snapshot, roles like Creator, publishing a **JSON command** to the game bus. For “talk to NPC” you usually use `AskAsync` / `Stream`.

**Never on the Unity main thread:** `CoreAi.AskAsync(...).Result`, `.GetAwaiter().GetResult()`, or `.Wait()` — the LLM stack uses **player loop** / **`ToolInvocationMarshaler`** / **HTTP** paths that can deadlock if the managed main thread blocks while a thread-pool continuation waits for **main** (**v1.5.14:** tool marshaling skips **`SwitchToMainThread`** in **Edit Mode !isPlaying** specifically to keep **Test Runner / tooling** safe — still avoid blocking **main** in gameplay). Use **`await`** (e.g. **`async void`** / **UniTask** on UI events).

**Can I call outside `async void`?**  
You can from `Start` with `StartCoroutine` + wrapper, but simpler — **`async void` on the Unity main thread** or **UniTask**. Do not use `Task.Run` — LLM calls must stay on the **main thread** (see [STREAMING_ARCHITECTURE](STREAMING_ARCHITECTURE.md)).

**Where do I get `roleId`?**  
The same id as in `AgentBuilder("...")` and `CoreAiChatConfig`. Often `"SmartChat"`.

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
await foreach (string chunk in CoreAi.StreamAsync("Tell a joke", "SmartChat"))
    label.text += chunk;
```

### 3.3. Smart: chunks in UI and full text in a variable

```csharp
string full = await CoreAi.SmartAskAsync(
    "Tell a story",
    roleId: "SmartChat",
    onChunk: c => label.text += c);

SaveToPlayerJournal(full);
```

Override streaming: `uiStreamingOverride: false` — force full response in one piece.

### 3.4. Safe call (no try when AI is absent)

```csharp
if (CoreAi.TryGetChatService(out var chat))
{
    string reply = await chat.SendMessageAsync("Hi", "SmartChat", ct);
}
else
{
    // AI disabled or scene without scope — show default NPC text
}
```

### 3.4b. Agent control API

```csharp
// Stop generation (e.g. Stop button in UI)
CoreAi.StopAgent("SmartChat");

// Clear chat history but keep long-term memory (facts, quests)
CoreAi.ClearContext("SmartChat", clearChatHistory: true, clearLongTermMemory: false);

// Full hard reset (amnesia)
CoreAi.ClearContext("SmartChat", clearChatHistory: true, clearLongTermMemory: true);
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

### 3.7. Send prompts, images and files

Every text entry point takes one `AiAttachment` or a list of them (`IReadOnlyList<AiAttachment>`, e.g. an array)
right after the prompt; everything else (role, callbacks, cancellation) stays where it was. Namespaces:
`using CoreAI;` (facade + Unity helpers) and `using CoreAI.Ai;` (`AiAttachment`).

| You send | One line |
|----------|----------|
| Prompt only | `await CoreAi.AskAsync("Hello!");` |
| Prompt + one image | `await CoreAi.AskAsync("What is on screen?", Camera.main.CaptureAiAttachment());` |
| Prompt + several images | `await CoreAi.AskAsync("What changed?", new[] { before.ToAiAttachment(), after.ToAiAttachment() });` |
| Image only | `await CoreAi.AskAsync("", icon.ToAiAttachment());` |
| Text file + prompt | `await CoreAi.AskAsync("Find the bug", AiAttachment.FromText("enemy.lua", luaSource));` |
| Images + files | `await CoreAi.AskAsync("Does the code match the picture?", new[] { shot, AiAttachment.FromFile(path) });` |
| Streaming | `await foreach (string c in CoreAi.StreamAsync("Describe it", shot)) label.text += c;` |
| Smart (stream if enabled) | `await CoreAi.SmartAskAsync("Describe it", shots, "SmartChat", onChunk: c => label.text += c);` |
| A specific agent / role | `await CoreAi.AskAsync("Review", files, roleId: "Programmer");` |
| Full pipeline (commands, authority) | `await CoreAi.OrchestrateAsync(new AiTaskRequest { RoleId = "Creator", Hint = "Build this", Attachments = new[] { sketch } });` |
| The chat panel | `await panel.SubmitMessageFromExternalAsync("Look", new CoreAiChatExternalSubmitOptions { Attachments = new[] { shot } });` (blank text is allowed when there is an attachment: the bubble shows `[attachment: ...]`) |
| Without Unity (own harness, server) | `await orchestrator.RunTaskAsync("What is on it?", AiAttachment.FromFile("shot.png"));` (also `RunStreamingAsync`) |
| Ask about a picture a tool returned | `await CoreAi.AskWithImageFollowUpAsync("Is it finished?", shot.Images[0]);` (one-shot, no history) |

> **`null` in the attachment position is ambiguous.** `CoreAi.AskAsync("hi", null)` and
> `orchestrator.RunTaskAsync("hi", null)` do not compile (CS0121: the `string roleId`, `AiAttachment` and
> `IReadOnlyList<AiAttachment>` overloads all accept `null`). Omit the argument, or cast it:
> `CoreAi.AskAsync("hi", (AiAttachment)null)`. A null attachment or list is the prompt-only turn.

**Where attachments come from**

| Source | Call | Notes |
|--------|------|-------|
| `Camera` | `cam.CaptureAiAttachment(maxSide: 512)` | Offscreen render of what the camera sees (no screen-space overlay UI). |
| Whole screen incl. UI | `ScreenCapture.CaptureScreenshotAsTexture().ToAiAttachment()` | Call at end of frame (coroutine `WaitForEndOfFrame`), then `Destroy` the texture. |
| `Texture2D` | `tex.ToAiAttachment()` | Long edge 1024 by default (`maxSide: 0` = native size — a 4096² texture then costs a 64 MB readback). JPEG by default (`CaptureImageFormat.Png` for crisp UI/pixel art); works for GPU-only and compressed textures. |
| `Sprite` | `sprite.ToAiAttachment()` | Only the sprite's region, PNG by default (keeps transparency); long edge 1024 by default. |
| `RenderTexture` | `rt.ToAiAttachment()` | Current content; long edge 1024 by default (`maxSide: 0` = native). |
| `TextAsset` | `asset.ToAiAttachment("level.lua")` | Inlined as text; pass a file name with an extension for the right type. |
| File on disk | `AiAttachment.FromFile(path)` / `await AiAttachment.FromFileAsync(path)` | Type from the extension. Not for WebGL `StreamingAssets` — download the bytes and use `FromFile(name, bytes)`. |
| Bytes | `AiAttachment.Image(bytes)`, `AiAttachment.FromFile("a.json", bytes)` | The array is referenced, not copied. `Image` detects PNG/JPEG/GIF/WEBP from the bytes when no type is given and rejects a non-image type (use `FromFile`/`FromText` for text). |
| Pooled buffer / stream | `AiAttachment.Image(memory, "image/jpeg")`, `AiAttachment.FromStream(memoryStream, "image/png")` | No copy (a `MemoryStream` only when its buffer is publicly visible). See the lifetime rule below. |
| Base64 / data URL | `AiAttachment.FromBase64(b64, "image/png")`, `AiAttachment.FromDataUrl("data:image/png;base64,...")` | Decoded once; RFC 2397 parameters are fine (`data:text/plain;charset=utf-16;base64,...`). `TryFromDataUrl` does not throw. |
| String | `AiAttachment.FromText("notes.md", text)` | Inlined as-is (no encode/decode). |
| URL | `AiAttachment.ImageUri(new Uri("https://..."))` | The provider fetches it. |

**How each kind reaches the model**

- **Images** (`image/png`, `image/jpeg`, `image/webp`, `image/gif`) become native image parts — only a
  **vision-capable model** can read them (check `CoreAi.IsVisionEnabled()`; a text-only model or provider errors
  or ignores them). Any number per turn, no CoreAI cap; the provider's own limits apply. Scale big pictures down
  (`maxSide`) to save tokens.
- **Text-like files** (`text/*`, JSON, Lua, XML, YAML, TOML, SQL, JS, C#, Python, Markdown, CSV, shaders, ...) are
  inlined into the prompt as delimited blocks, so **every model** reads them. Decoding: the byte-order mark
  (UTF-8, UTF-16 LE/BE, UTF-32 LE/BE), else the `charset` of the media type (`"text/plain; charset=utf-16"`,
  `"text/plain; charset=iso-8859-1"`), else UTF-8. A file that is not text in that encoding — NUL characters (binary,
  or UTF-16 without a BOM or charset) or more than 1 in 64 undecodable characters — throws `ArgumentException`
  naming the file instead of sending mojibake. Caps: 256 KB per file, 1 MB per turn
  (`AiAttachment.MaxInlineTextBytes` / `MaxTotalInlineTextBytes`); inlined files count against the context budget
  (about 4 bytes per token) so history is trimmed to make room.
- **Anything else** (audio, video, meshes, arbitrary binary) throws `ArgumentException` when the turn is
  composed — nothing is dropped silently or pasted as base64.
- Chat history stores only a placeholder such as `[attachment: shot.jpg image/jpeg 84 KB]`, never the bytes.
- **Tools can show pictures too**: return `LlmToolImageResult` (text + images) from a tool — see
  [TOOL_AUTHORING_GUIDE](TOOL_AUTHORING_GUIDE.md#returning-images-from-a-tool). The camera tools (`camera_capture`,
  `screenshot`, `capture_camera`) do this already; `CoreAi.OnToolExecuted` then receives the `LlmToolImageResult`
  itself as `result` (cast it and read `.Images`), not a JSON string with a `dataUrl`.

**Lifetime (no copies).** CoreAI keeps your list and your buffers as they are and reads them again for every provider
request of the turn — each tool-call roundtrip, the final summary request and every orchestrator retry. Keep the
buffers and the list unchanged until the returned `Task` completes, or until the returned stream is fully enumerated
or disposed. Reuse a pooled buffer only after that.

**Cost.** A prompt-only call allocates exactly what it did before. Attachments are wrapped, never copied: several
1 MB images add a few hundred bytes per call; inlined text files are written once into the final prompt string;
each image is encoded into one data-URL string on the first provider request of the turn and that string is reused by
every later request (tool roundtrips, the summary). The single-attachment overloads wrap the attachment in a
one-element array (32 bytes); pass your own list to reuse it.

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
