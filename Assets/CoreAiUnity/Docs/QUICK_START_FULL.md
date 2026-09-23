# 🚀 Quick Start: Run LM Studio → Run the scene → Send a command

A step-by-step guide from zero to a working AI agent in **10 minutes**.

---

## Contents

1. [Installing LM Studio](#1-installing-lm-studio-2-minutes)
2. [Downloading a model](#2-downloading-a-model-3-minutes)
3. [Starting the local server](#3-starting-the-local-server-1-minute)
4. [Configuring the Unity project](#4-configuring-the-unity-project-2-minutes)
5. [Running the scene and sending a command](#5-running-the-scene-and-sending-a-command-2-minutes)
6. [Verifying the result](#6-verifying-the-result)
7. [What’s next?](#7-whats-next)

---

## 1. Installing LM Studio (2 minutes)

### Download LM Studio

1. Go to [https://lmstudio.ai](https://lmstudio.ai)
2. Download the installer for your OS (Windows / macOS / Linux)
3. Install and launch

```
💡 LM Studio is a free tool for running LLM models locally.
   It provides an OpenAI-compatible HTTP API.
```

### System requirements

| | Minimum | Recommended |
|--|---------|-------------|
| **RAM** | 8 GB | 16 GB+ |
| **GPU (VRAM)** | 4 GB | 8 GB+ |
| **Disk** | 5 GB (for a 4B model) | 20 GB+ |

---

## 2. Downloading a model (3 minutes)

### Recommended models

| Model | Size | Tool calling | Best for |
|--------|--------|:------------:|----------|
| ⭐ **Qwen3.5-4B** (Q4_K_M) | ~2.5 GB | ✅ Excellent | **Best starting point** |
| **Qwen3.5-2B** (Q4_K_M) | ~1.5 GB | ⚠️ Basic | Weaker hardware |
| **Gemma 4 26B** | ~15 GB | ✅ Excellent | Powerful hardware |
| **Qwen3.5-35B MoE** | ~20 GB | ✅ Excellent | Production |

### Step by step

1. In LM Studio, click **🔍 Search** (or the search icon)
2. Enter `Qwen3.5-4B`
3. Pick the **GGUF** build with **Q4_K_M** quantization
4. Click **Download** and wait for it to finish

```
📦 Download size: ~2.5 GB for Qwen3.5-4B Q4_K_M
   Download time: 2–5 minutes (depends on your connection)
```

---

## 3. Starting the local server (1 minute)

### Load the model

1. Open the **💬 Chat** tab (or **Local Server**)
2. In the top dropdown, select the downloaded model (Qwen3.5-4B)
3. Wait until it loads (the status line shows “Model loaded”)

### Start the server

1. Open the **🖥️ Local Server** tab (`<->` icon)
2. Click **Start Server**
3. Confirm status: **Server running on port 1234**

```
✅ Server is running!
   URL: http://localhost:1234/v1
   Model: Qwen3.5-4B-Q4_K_M
   Status: Ready
```

### Optional check

Open PowerShell and run:

```powershell
# Check the server
Invoke-RestMethod -Uri "http://localhost:1234/v1/models"

# Sample request
$body = @{
    model = "qwen3.5-4b"   # the model id listed by /v1/models
    messages = @(@{ role = "user"; content = "Say hello" })
} | ConvertTo-Json -Depth 3

Invoke-RestMethod -Uri "http://localhost:1234/v1/chat/completions" `
    -Method POST -Body $body -ContentType "application/json"
```

---

## 4. Configuring the Unity project (2 minutes)

### 4.1 Open the project

1. **Unity Hub** → **Add** → select the `CoreAI` folder
2. Open the project (Unity **6000.0+**; custom UI Toolkit elements use `[UxmlElement]` / `[UxmlAttribute]`,
   the only UXML path left in Unity 6.6+)

### 4.2 Open a scene

For the chat panel:

```
Menu: CoreAI → Setup → Create Chat Demo Scene
```

It creates `Assets/CoreAiUnity/Scenes/CoreAiChatDemo.unity` with `CoreAILifetimeScope` and the chat panel.

For the F9 Programmer hotkey used in §5.2 Option B, open the example game instead:

```
Menu: CoreAI → Development → Example Game → Open RogueliteArena scene
```

(`Assets/CoreAiUnity/Scenes/_mainCoreAI.unity` is an internal development harness, not a starting point.)

### 4.3 Configure CoreAISettings

1. Open the menu **CoreAI → Settings** — it selects `Assets/Resources/CoreAISettings.asset` and creates
   it if it does not exist yet. (The asset is never created automatically on package import; use this
   menu, or **CoreAI → Setup → Create Default Assets** for the whole default set.)
2. Or create manually: **Create → CoreAI → CoreAI Settings**
3. In the Inspector configure:

```
┌─────────────────────────────────────────────┐
│  CoreAI Settings                             │
│                                              │
│  Essentials                                  │
│     LLM Backend: [OpenAiHttp]         ▼     │
│     Base URL:    http://localhost:1234/v1     │
│     API Key:     (empty)                     │
│     Model:       qwen3.5-4b  (required)      │
│                                              │
│  Advanced Settings                           │
│   HTTP:    Timeout (sec): 120                │
│   General: Temperature 0.1 (override off)    │
│            Max Output Tokens (override off)  │
│            LLM Timeout (sec): 120            │
│            Max Concurrent: 2                 │
│                                              │
│  [🔗 Test Connection]                        │
│                                              │
└─────────────────────────────────────────────┘
```

### 4.4 Test the connection

Click **🔗 Test Connection** in the Inspector.

Expected result:

```
Connection succeeded.

Base URL: http://localhost:1234/v1
Model: qwen3.5-4b
Response: "OK"
```

---

## 5. Running the scene and sending a command (2 minutes)

### 5.1 Press ▶ Play

In Unity, click **Play** (▶).

In the Unity Console you should see:

```
VContainer + MessagePipe (GlobalMessagePipe) + filtered ILog are registered.
```

(With category and CoreAI prefixes enabled in `GameLogSettings`, the line carries the `[CoreAI]` prefix.)

### 5.2 Send a command from code

**Option A: From your own script**

```csharp
using CoreAI.Ai;
using UnityEngine;
using VContainer;

public class MyGameController : MonoBehaviour
{
    [Inject] private IAiOrchestrationService _orchestrator;

    async void Start()
    {
        // Ask the Programmer agent to generate Lua
        await _orchestrator.RunTaskAsync(new AiTaskRequest
        {
            RoleId = "Programmer",
            Hint = "Write a Lua script that reports 'Hello from AI!'"
        });
        
        Debug.Log("✅ AI task completed!");
    }
}
```

**Option B: Via hotkey (RogueliteArena example scene)**

1. Open the `RogueliteArena` scene (§4.2); its `ExampleRogueliteEntry` adds `CoreAiLuaHotkey` at runtime
2. In Play Mode, press **F9** — that queues a **Programmer** task
3. The Programmer runs Lua through the `execute_lua` tool (needs `com.neoxider.coreaimods` and `COREAI_LUA`)
4. Check the logs (turn on **Log Tool Calls** in CoreAISettings → Advanced Settings → Debug):

```
LLM > traceId=abc123 role=Programmer backend=…
[ToolCall] traceId=abc123 role=Programmer tool=execute_lua status=OK dur=…ms
[Lua report] lua from game F9
LLM < traceId=abc123 role=Programmer backend=… wallMs=… | …
```

**Option C: Create a custom agent**

```csharp
// Create a merchant — three lines!
var merchant = new AgentBuilder("Merchant")
    .WithSystemPrompt("You are a friendly weapon merchant. Greet customers warmly.")
    .WithTool(new InventoryLlmTool(myInventory))
    .WithMemory()
    .Build();

merchant.ApplyToPolicy(CoreAIAgent.Policy);

// Send a message:
merchant.AskWithCallback("Show me swords", (response) => {
    Debug.Log($"Merchant: {response}");
});
```

> The callback is marshaled to the caller's `SynchronizationContext` when one exists (for example, the Unity main thread); when called from a thread without a `SynchronizationContext`, the callback may run on a background thread and must not touch `UnityEngine` APIs.

---

## 6. Verifying the result

### What you should see

```
┌─ Unity Console ──────────────────────────────────────────────┐
│                                                               │
│ LLM > traceId=abc123 role=Programmer backend=…                │
│ [ToolCall] traceId=abc123 tool=execute_lua status=OK          │
│ [Lua report] Hello from AI!                                   │
│ LLM < traceId=abc123 role=Programmer wallMs=1200 | …          │
│ ✅ AI task completed!                                         │
│                                                               │
└───────────────────────────────────────────────────────────────┘
```

### If something does not work

| Issue | Quick fix |
|----------|----------------|
| No `LLM >` line / stub replies | Confirm LM Studio is running and LLM Backend + Model are set; run **CoreAI → Setup → Validate Scene** |
| `Connection refused` | Check port 1234 in LM Studio |
| `Empty response` / timeout | Raise **LLM Timeout (sec)** (default 120) |
| `[ToolCall] … status=FAIL` repeatedly | Model too small; use 4B+ |

> 📖 Details: [TROUBLESHOOTING.md](TROUBLESHOOTING.md)

---

## 7. What’s next?

### 📚 Step-by-step guides

| Task | Document |
|--------|----------|
| Build your own agent | [AGENT_BUILDER.md](../../CoreAI/Docs/AGENT_BUILDER.md) |
| Control the world from Lua | [WORLD_COMMANDS.md](WORLD_COMMANDS.md) |
| Configure memory | [MemorySystem.md](MemorySystem.md) |
| Add a custom tool | [TOOL_CALL_SPEC.md](TOOL_CALL_SPEC.md) |
| Understand architecture | [DEVELOPER_GUIDE.md](DEVELOPER_GUIDE.md) |
| Roles and prompts | [AI_AGENT_ROLES.md](AI_AGENT_ROLES.md) |
| Browse examples | [EXAMPLES.md](EXAMPLES.md) |

### 🎯 Try this

1. **Build an NPC merchant** with inventory → [CHAT_TOOL_CALLING.md](CHAT_TOOL_CALLING.md)
2. **Craft weapons** via CoreMechanicAI → [EXAMPLES.md](EXAMPLES.md)
3. **Spawn enemies** via World Commands → [WORLD_COMMANDS.md](WORLD_COMMANDS.md)
4. **Run tests** → Window → Test Runner → EditMode → Run All

---

## 📋 Quick start checklist

```
✅ LM Studio installed
✅ Model downloaded (Qwen3.5-4B Q4_K_M)
✅ Server running on port 1234
✅ Unity project open
✅ Chat demo scene (or RogueliteArena) loaded
✅ CoreAISettings → LLM Backend = OpenAiHttp
✅ CoreAISettings → Base URL = http://localhost:1234/v1, Model set
✅ Test Connection = "Connection succeeded."
✅ Play → send a chat message (or F9 in RogueliteArena) → LLM > / LLM < in the logs

🎉 Done! Move on to building your own agents.
```

---

## 🔀 Alternatives

### Option B: Without LM Studio (LLMUnity — embedded model)

To run a model **inside Unity** (no external server):

1. Install LLMUnity: **CoreAI → Setup → Modules → LLMUnity → Enable + Update to latest** (it is not installed with CoreAI)
2. CoreAISettings → LLM Backend = **LlmUnity** (or **Auto**)
3. Pick a model in **GGUF Model**, or add a host with **CoreAI → Setup → Create LLMUnity Objects (LLM + LLMAgent)** and download a model in the **LLM** Inspector
4. Press Play

> ⚠️ LLMUnity runs the model inside the Unity process (through its built-in OpenAI-compatible server) — no external tools, but it shares the machine with the game.

### Option C: Cloud API (OpenAI, Qwen API)

```
CoreAISettings → LLM Backend = OpenAiHttp
   Base URL: https://api.openai.com/v1
   API Key: sk-xxxxxxxxxxxxx
   Model: gpt-4o-mini
```

or

```
CoreAISettings → LLM Backend = OpenAiHttp
   Base URL: https://dashscope.aliyuncs.com/compatible-mode/v1
   API Key: sk-xxxxxxxxxxxxx
   Model: qwen-max
```

> ⚠️ **Editor only.** A key typed into `Assets/Resources/CoreAISettings.asset` (or any `CoreAISettings`
> asset inside a `Resources/` folder) **aborts every player build** — `Resources` assets are packed into
> the player and the key is recoverable from the shipped bundle. Clear the field before building and
> inject the key at runtime (`CoreAiBackend.SetApiKey`, environment variable, secure storage), or use
> `ServerManagedApi` so the key never leaves your backend.

---

> 🚀 **CoreAI** — make your game smarter. One agent at a time.
