# 🔧 Troubleshooting Guide — CoreAI

**Document version:** 1.0 | **Date:** April 2026

A guide to resolving typical issues when working with CoreAI.

---

## Table of contents

- [🤖 Problem: Model does not respond](#-problem-model-does-not-respond)
- [📜 Problem: Lua failed](#-problem-lua-failed)
- [🧠 Problem: Memory is not written](#-problem-memory-is-not-written)
- [🔧 Problem: Tool call does not work](#-problem-tool-call-does-not-work)
- [🌍 Problem: World command is not executed](#-problem-world-command-is-not-executed)
- [⏳ Problem: Tests hang](#-problem-tests-hang)
- [⏳ PlayMode: HTTP 500 from LM Studio / local API](#playmode-http-500-from-lm-studio--local-api)
- [🔌 Problem: DI / VContainer errors](#-problem-di--vcontainer-errors)
- [📊 Diagnostics: How to enable verbose logs](#-diagnostics-how-to-enable-verbose-logs)

---

## 🤖 Problem: Model does not respond

### Symptoms
- Empty response from the LLM (`"Empty response from LLM"`)
- Request timeout
- `StubLlmClient` instead of a real model
- No `LLM ▶` / `LLM ◀` logs in the console

### Diagnostics

**Step 1: Check which backend is selected**

On Unity startup the console should show a backend message:
```
[CoreAI] Backend: OpenAiHttp → http://localhost:1234/v1
```
or
```
[CoreAI] Backend: LlmUnity → Qwen3.5-4B
```
or
```
[CoreAI] Backend: Stub (offline mode)  ← ❌ Problem!
```

**Step 2: Narrow down by backend**

---

### 🔌 LLMUnity does not respond

| Check | How to verify | Fix |
|----------|--------------|---------|
| LLMAgent on scene? | Hierarchy → look for an object with `LLMAgent` | Create an `LLMAgent` on the scene |
| LLM component present? | Inspector LLMAgent → is there an `LLM`? | Add the `LLM` component |
| GGUF file exists? | Inspector LLM → Model Path | Download the model via LLMUnity or LM Studio |
| Service running? | Logs for `LLMUnity: started` | Increase `Startup Timeout` in CoreAISettings |
| Enough VRAM? | Task Manager → GPU Memory | Use a smaller model (4B instead of 9B) or lower `numGPULayers` |

```csharp
// Programmatic check:
var agent = FindObjectOfType<LLMAgent>();
Debug.Log($"LLMAgent found: {agent != null}");
Debug.Log($"LLM started: {agent?.llm?.started}");
```

**Typical fix:**
```
CoreAISettings → LLMUnity → ✅ Keep Alive = true
CoreAISettings → LLMUnity → Startup Timeout = 120
```

---

### 🌐 HTTP API does not respond

| Check | How to verify | Fix |
|----------|--------------|---------|
| Server running? | Browser → `http://localhost:1234/v1/models` | Start LM Studio / Ollama |
| URL correct? | CoreAISettings → HTTP API → Base URL | No trailing `/`: `http://localhost:1234/v1` |
| Model loaded? | LM Studio → Status = "Loaded" | Load the model in LM Studio |
| API key required? | OpenAI → yes, LM Studio → no | For LM Studio leave API Key **empty** |
| Port open? | `Test-NetConnection localhost -Port 1234` | Check the firewall |

**Quick check via PowerShell:**
```powershell
# API reachability
Invoke-RestMethod -Uri "http://localhost:1234/v1/models" -Method GET

# Sample request
$body = @{
    model = "qwen3.5-4b"
    messages = @(@{ role = "user"; content = "Say OK" })
} | ConvertTo-Json -Depth 3

Invoke-RestMethod -Uri "http://localhost:1234/v1/chat/completions" `
    -Method POST -Body $body -ContentType "application/json"
```

**Typical fix:**
```
1. Start LM Studio
2. Load the model (Qwen3.5-4B)
3. Enable Local Server (port 1234)
4. CoreAISettings → Backend = OpenAiHttp
5. CoreAISettings → Base URL = http://localhost:1234/v1
6. Click "🔗 Test Connection"
```

---

### 🔇 Stub instead of model (silent fallback)

**Why Stub was chosen:**
1. Backend = Auto, but neither LLMUnity nor HTTP is available
2. LLMAgent not found on scene and HTTP URL not configured
3. `COREAI_NO_LLM` define is enabled (manual opt-out)
4. Package `ai.undream.llm` is not installed (`COREAI_HAS_LLMUNITY` not defined) — LLMUnity backend unavailable

**Fix:**
```
CoreAISettings → Backend Type = OpenAiHttp (or LlmUnity)
```

Or ensure Auto mode has at least one available backend:
```
CoreAISettings → Backend Type = Auto
CoreAISettings → Auto Priority = HTTP First  ← if HTTP is primary
```

---

### ⏱️ Request timeout

```
[Error] LLM request timed out after 15 seconds
```

**Fix:** Increase the timeout:
```
CoreAISettings → ⚙️ General → LLM Timeout = 120
```

For large models (9B+) or weaker hardware you may need 120–300 seconds.

---

## 📜 Problem: Lua failed

### Symptoms
- `LuaExecutionFailed` in logs
- `[Error] MoonSharp runtime: ...` in the model output
- Endless self-heal loops (up to 3 attempts)
- `LuaExecutionGuard: step limit exceeded`

### Diagnostics

**Type 1: Lua syntax error**
```
[Error] MoonSharp: chunk_1:(3,0-4): unexpected symbol near 'end'
```

**Cause:** The model generated invalid Lua.

**Fix:**
- The system automatically retries self-heal up to 3 times
- If that fails → improve the Programmer prompt with examples of valid Lua
- Or use a stronger model (4B+ instead of 2B)

---

**Type 2: Calling a non-existent function**
```
[Error] MoonSharp runtime: attempt to call 'custom_function' (a nil value)
```

**Cause:** Lua tries to call a function that is not in the whitelist API.

**Fix:** Add the function to `IGameLuaRuntimeBindings`:
```csharp
public class MyGameBindings : IGameLuaRuntimeBindings
{
    public void RegisterBindings(Script script)
    {
        script.Globals["custom_function"] = (Action<string>)(msg => {
            Debug.Log($"Custom: {msg}");
        });
    }
}
```

Or state in the Programmer prompt which functions are available:
```
Available Lua API: report(string), add(a,b), coreai_world_spawn(...), ...
Do NOT use any other functions.
```

---

**Type 3: Infinite loop (step limit)**
```
[Warning] LuaExecutionGuard: step limit exceeded (10000 steps)
```

**Cause:** Lua contains an infinite loop or a very heavy operation.

**Fix:**
- `LuaExecutionGuard` aborts via wall-clock and step limits
- Ensure the guard is enabled (default: on)
- Tune limits if needed

---

**Type 4: Lua repair exhausted retries**
```
[Warning] Programmer repair: max retries (3) exceeded for traceId=abc123
```

**Cause:** 3 self-heal attempts were not enough.

**Fix:**
1. Increase `CoreAISettings.MaxLuaRepairRetries` (default 3)
2. Improve the Programmer system prompt (add examples)
3. Use a stronger model
4. Verify the whitelist API is correct

---

## 🧠 Problem: Memory is not written

### Symptoms
- The agent “forgets” information between calls
- File `persistentDataPath/CoreAI/AgentMemory/<RoleId>.json` is empty or not created
- Memory does not appear in the system prompt

### Diagnostics

**Step 1: Check that memory is enabled for the role**

```csharp
// By default memory is ON for:
// Creator, Analyzer, Programmer, CoreMechanicAI
// OFF for:
// PlayerChat, AINpc (they use ChatHistory)

var policy = container.Resolve<AgentMemoryPolicy>();
Debug.Log($"Memory enabled for Creator: {policy.IsMemoryToolEnabled("Creator")}");
```

**Step 2: Check that the model calls the tool**

Enable MEAI Debug Logging:
```
CoreAISettings → 🔧 Debug → MEAI Debug Logging = ✅
```

You should see in logs:
```
[MEAI] Tool call detected: name=memory, arguments={action: write, content: ...}
[MEAI] Tool result: Memory saved
```

If the tool is never called — the issue is the prompt. Add an explicit instruction:
```
You MUST save important information using the memory tool:
{"name": "memory", "arguments": {"action": "write", "content": "..."}}
```

**Step 3: Check storage**

```csharp
var store = container.Resolve<IAgentMemoryStore>();
if (store.TryLoad("Creator", out var state))
{
    Debug.Log($"Creator memory: {state.Memory}");
}
else
{
    Debug.Log("No memory found for Creator");
}
```

**Step 4: Check the file path**

```csharp
Debug.Log($"Memory path: {Application.persistentDataPath}/CoreAI/AgentMemory/");
```

| Platform | Path |
|-----------|------|
| Windows | `%APPDATA%/../LocalLow/<Company>/<Product>/CoreAI/AgentMemory/` |
| macOS | `~/Library/Application Support/<Company>/<Product>/CoreAI/AgentMemory/` |
| Android | `/data/data/<package>/files/CoreAI/AgentMemory/` |
| WebGL | IndexedDB (via Unity's persistentDataPath) |

### Typical fixes

| Issue | Fix |
|----------|---------|
| Memory disabled for role | `policy.ConfigureRole("MyRole", useMemoryTool: true)` |
| Model does not call tool | Add instruction to the prompt |
| NullAgentMemoryStore | Check DI registration for `IAgentMemoryStore` |
| File not created | Check permissions on `persistentDataPath` |
| ChatHistory not working | Ensure `useChatHistory: true` and backend = LLMUnity |

---

## 🔧 Problem: Tool call does not work

### Symptoms
- Model returns text instead of a tool call
- `Tool call not recognized` in logs
- Tool call retries exhausted

### Diagnostics

**Type 1: Wrong format from the model**
```
[Warning] Tool call not recognized, retry 1/3
```

**Cause:** The model returned a tool call in an invalid format.

**Fix:** CoreAI retries automatically up to 3 times. If that fails:
1. Use a larger model (4B+ recommended)
2. Add format to the prompt:
```
ALWAYS use this exact format for tool calls:
{"name": "tool_name", "arguments": {"param": "value"}}
```

**Type 2: Tool not registered**
```
[Error] No AIFunction found for tool name: my_custom_tool
```

**Fix:** Ensure the tool is added to the agent:
```csharp
var agent = new AgentBuilder("MyAgent")
    .WithTool(new MyCustomTool())  // ← add tool
    .Build();
```

**Type 3: Infinite tool-call loop**
```
[Warning] SmartToolCallingChatClient: duplicate tool_call detected, breaking loop
```

**Cause:** The model looped calling the same tool.

**Fix:** `SmartToolCallingChatClient` detects and breaks the loop automatically. If it keeps happening — improve the prompt.

---

## 🌍 Problem: World command is not executed

### Symptoms
- Objects do not spawn
- `[Warning] Spawn rejected: prefab key 'X' not found` in logs
- `coreai_world_spawn returned false`

### Diagnostics

**Issue 1: Prefab registry not assigned**
```
[Warning] World prefab registry not assigned
```

**Fix:**
1. Create → CoreAI → World → Prefab Registry
2. Add prefabs with keys
3. CoreAILifetimeScope → World Prefab Registry → assign the asset

**Issue 2: Prefab key not found**
```
[Warning] Spawn rejected: prefab key 'Boss' not found in registry
```

**Fix:** Add the key in `CoreAiPrefabRegistryAsset`:
- Open the asset
- Add entry: Key = "Boss", Name = "Boss", Prefab = your prefab

**Issue 3: Call not on the main thread**
```
[Error] UnityException: ... can only be called from the main thread
```

**Fix:** This is an internal error. Ensure `AiGameCommandRouter` marshals to the main thread correctly. World commands **must always** go through MessagePipe → Router.

---

## ⏳ Problem: Tests hang

### EditMode tests
```
Test hangs on "Waiting for LLM..."
```

**Cause:** EditMode tests must not call a real LLM.

**Fix:**
- Use `StubLlmClient` for EditMode
- PlayMode tests = real LLM

### PlayMode tests

**Hang on "stopping server":**
```
CoreAISettings → LLMUnity → Keep Alive = ✅ true
```

**Hang on "waiting for model":**
1. Increase `Startup Timeout` to 120–300 sec
2. Verify the model is downloaded and the GGUF path is correct
3. Use a smaller model for tests (2B instead of 9B)

**HTTP tests do not connect:**
1. Start LM Studio **before** tests
2. Set env vars:
```powershell
$env:COREAI_OPENAI_TEST_BASE = "http://localhost:1234/v1"
$env:COREAI_OPENAI_TEST_MODEL = "qwen3.5-4b"
```

### ⏳ PlayMode: HTTP 500 from LM Studio / local API

**Symptoms:** In the Unity console during PlayMode tests with a real model you see **HTTP/1.1 500** and an HTML body like `<pre>Internal Server Error</pre>` in `MeaiOpenAiChatClient` / `MeaiLlmClient` logs; the memory test fails on an empty sink or is marked **Ignored** after the recall step.

**Cause:** The response comes from **your** local OpenAI-compatible server (LM Studio, proxy at `http://…:1234/v1`), not from CoreAI memory logic. On LLM error the orchestrator does **not** publish `ApplyAiGameCommand`, so the command counter in the test stays zero.

**What to check:**
1. LM Studio (or equivalent) is running and the model is **loaded** before the test starts.
2. In `CoreAISettings.asset`, **Api Base URL** is correct with the **`/v1`** suffix.
3. No context / VRAM overload — often causes 500 on the second long request.

When the real-model tests hit a persistent recall failure they end with **`Assert.Ignore`** and an explanation so CI does not fail due to infrastructure.

---

## 🔌 Problem: DI / VContainer errors

### Symptoms
```
VContainerException: Type 'ILlmClient' is not registered
```

### Fix

1. Ensure `CoreAILifetimeScope` is on the scene
2. Ensure it is the **Root** or **Parent** for other LifetimeScopes
3. Verify all dependencies are assigned in the Inspector:
   - Core AI Settings
   - Agent Prompts Manifest (optional)
   - Game Log Settings (optional)
   - World Prefab Registry (optional)

```
Hierarchy:
└── CoreAILifetimeScope  ← Root LifetimeScope
    ├── LlmManager (LLM + LLMAgent)
    ├── GameManager
    └── ... your objects
```

---

## 📊 Diagnostics: How to enable verbose logs

### Turn on full diagnostics quickly

```
CoreAISettings → 🔧 Debug:
  ✅ MEAI Debug Logging      — MEAI pipeline logs
  ✅ HTTP Debug Logging       — raw HTTP requests
  ✅ Log Orchestration Metrics — orchestrator metrics
```

### What to look for in logs

| Pattern | Meaning |
|---------|----------|
| `LLM ▶ [traceId=...]` | Request sent |
| `LLM ◀ [traceId=...] 247 tokens, 1.2s` | Response received |
| `LLM ⏱ timeout` | Timeout |
| `[MessagePipe] traceId=...` | Command routing |
| `[MEAI] Tool call detected` | Tool call recognized |
| `[MEAI] Tool result` | Tool call result |
| `[Lua] Execution succeeded` | Lua succeeded |
| `[Lua] Execution failed` | Lua error |
| `[World] Spawn: Enemy at (10,0,5)` | World command |
| `SmartToolCallingChatClient: duplicate` | Loop detected |

### Filter by TraceId

Each request gets a unique `TraceId`. Use it to trace the command path:

```
Unity console filter: "abc123"

[abc123] LLM ▶ role=Programmer hint="Create ambush script"
[abc123] LLM ◀ 312 tokens, 2.1s
[abc123] [MessagePipe] ApplyAiGameCommand type=AiEnvelope
[abc123] [Lua] Execution succeeded: "Ambush created"
```

---

## 🚑 Quick problem checklist

```
❓ Model silent?
  → Check Backend Type in CoreAISettings
  → Check that LM Studio / LLMAgent is running
  → Click "🔗 Test Connection"

❓ Empty response?
  → Increase LLM Timeout (120+)
  → Enable Keep Alive for LLMUnity
  → Check LLM ▶ / LLM ◀ logs

❓ Tool call not firing?
  → Enable MEAI Debug Logging
  → Check that the tool is added to the agent
  → Use a 4B+ model for reliable tool calling

❓ Lua failing?
  → Check whitelist API in the prompt
  → Self-heal runs up to 3 attempts
  → Increase MaxLuaRepairRetries if needed

❓ Memory not saving?
  → Check AgentMemoryPolicy for the role
  → Check that the model calls the memory tool
  → Check persistentDataPath

❓ Object not spawning?
  → Assign CoreAiPrefabRegistryAsset
  → Add prefab key to the registry
  → Check [World] logs

❓ Tests hanging?
  → Keep Alive = true
  → Startup Timeout = 120
  → For CI: use Stub backend
```

---

> 📖 **Related documents:**
> - [COREAI_SETTINGS.md](COREAI_SETTINGS.md) — all settings
> - [DEVELOPER_GUIDE.md](DEVELOPER_GUIDE.md) — architecture
> - [LLMUNITY_SETUP_AND_MODELS.md](LLMUNITY_SETUP_AND_MODELS.md) — LLM setup
