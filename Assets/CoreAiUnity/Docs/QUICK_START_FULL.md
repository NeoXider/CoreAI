# 🚀 Quick Start: Run LM Studio → Run the scene → Send a command

**Document version:** 1.0 | **Date:** April 2026

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
    model = "qwen3.5-4b"
    messages = @(@{ role = "user"; content = "Say hello" })
} | ConvertTo-Json -Depth 3

Invoke-RestMethod -Uri "http://localhost:1234/v1/chat/completions" `
    -Method POST -Body $body -ContentType "application/json"
```

---

## 4. Configuring the Unity project (2 minutes)

### 4.1 Open the project

1. **Unity Hub** → **Add** → select the `CoreAI` folder
2. Open the project (Unity **6000.0+**)

### 4.2 Open the scene

```
Menu: CoreAI → Development → Open _mainCoreAI scene
```

Or in the Project window:

```
Assets/CoreAiUnity/Scenes/_mainCoreAI.unity
```

### 4.3 Configure CoreAISettings

1. In the Project window find: `Assets/CoreAiUnity/Resources/CoreAISettings.asset`
2. Or create: **Create → CoreAI → CoreAI Settings**
3. In the Inspector configure:

```
┌─────────────────────────────────────────────┐
│  CoreAI Settings                             │
│                                              │
│  🎯 LLM Backend:    [OpenAiHttp]      ▼     │
│                                              │
│  🌐 HTTP API:                                │
│     Base URL:    http://localhost:1234/v1     │
│     API Key:     (empty)                     │
│     Model:       qwen3.5-4b                  │
│     Temperature: 0.2                         │
│     Max Tokens:  4096                        │
│     Timeout:     120                         │
│                                              │
│  ⚙️ General:                                 │
│     LLM Timeout: 30                          │
│     Max Concurrent: 2                        │
│                                              │
│  [🔗 Test Connection]                        │
│                                              │
└─────────────────────────────────────────────┘
```

### 4.4 Test the connection

Click **🔗 Test Connection** in the Inspector.

Expected result:

```
✅ HTTP API: Connected
   Model: qwen3.5-4b
   Response: "OK"
   Latency: 0.3s
```

---

## 5. Running the scene and sending a command (2 minutes)

### 5.1 Press ▶ Play

In Unity, click **Play** (▶).

In the Unity Console you should see:

```
[CoreAI] VContainer + MessagePipe... ready.
[CoreAI] Backend: OpenAiHttp → http://localhost:1234/v1
[CoreAI] Registered tools: memory, execute_lua, world_command, get_inventory, ...
```

### 5.2 Send a command from code

**Option A: From your own script**

```csharp
using CoreAI;
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

**Option B: Via hotkey (already on the scene)**

1. In Play Mode, press **F9**
2. That invokes the **Programmer** agent via `CoreAiLuaHotkey`
3. Check the logs:

```
[LLM ▶] traceId=abc123 role=Programmer
[LLM ◀] traceId=abc123 312 tokens, 1.8s
[Lua] Execution succeeded: "Hello from AI!"
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
merchant.Ask("Show me swords", (response) => {
    Debug.Log($"Merchant: {response}");
});
```

---

## 6. Verifying the result

### What you should see

```
┌─ Unity Console ──────────────────────────────────────────────┐
│                                                               │
│ [CoreAI] Backend: OpenAiHttp → http://localhost:1234/v1       │
│ [LLM ▶] traceId=abc123 role=Programmer                       │
│ [LLM ◀] traceId=abc123 247 tokens, 1.2s                      │
│ [MEAI] Tool call detected: name=execute_lua                   │
│ [Lua] Executing: report("Hello from AI!")                     │
│ [Lua] Execution succeeded                                     │
│ ✅ AI task completed!                                         │
│                                                               │
└───────────────────────────────────────────────────────────────┘
```

### If something does not work

| Issue | Quick fix |
|----------|----------------|
| `Backend: Stub` | Confirm LM Studio is running |
| `Connection refused` | Check port 1234 in LM Studio |
| `Empty response` | Raise Timeout to 120 s |
| `Tool call not recognized` | Model too small; use 4B+ |

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
✅ _mainCoreAI scene loaded
✅ CoreAISettings → Backend = OpenAiHttp
✅ CoreAISettings → Base URL = http://localhost:1234/v1
✅ Test Connection = ✅ Connected
✅ Play → F9 → "Hello from AI!" in the logs

🎉 Done! Move on to building your own agents.
```

---

## 🔀 Alternatives

### Option B: Without LM Studio (LLMUnity — embedded model)

To run a model **inside Unity** (no external server):

1. CoreAISettings → Backend = **LlmUnity** (or **Auto**)
2. On the scene, find **LlmManager** with `LLM` + `LLMAgent`
3. In the **LLM** Inspector, download a model (LLMUnity Download button)
4. Press Play

> ⚠️ LLMUnity runs the model inside the Unity process — slower, but no external tools.

### Option C: Cloud API (OpenAI, Qwen API)

```
CoreAISettings → Backend = OpenAiHttp
   Base URL: https://api.openai.com/v1
   API Key: sk-xxxxxxxxxxxxx
   Model: gpt-4o-mini
```

or

```
CoreAISettings → Backend = OpenAiHttp
   Base URL: https://dashscope.aliyuncs.com/compatible-mode/v1
   API Key: sk-xxxxxxxxxxxxx
   Model: qwen-max
```

---

> 🚀 **CoreAI** — make your game smarter. One agent at a time.
