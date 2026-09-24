# 🔧 Troubleshooting Guide — CoreAI

A guide to resolving typical issues when working with CoreAI.

---

## Table of contents

- [🤖 Problem: Model does not respond](#-problem-model-does-not-respond)
- [📜 Problem: Lua failed](#-problem-lua-failed)
- [🧠 Problem: Memory is not written](#-problem-memory-is-not-written)
- [🔧 Problem: Tool call does not work](#-problem-tool-call-does-not-work)
- [🌍 Problem: World command is not executed](#-problem-world-command-is-not-executed)
- [⏳ Problem: Tests hang](#-problem-tests-hang)
- [⏳ PlayMode: HTTP 500 from LM Studio / local API](#-playmode-http-500-from-lm-studio--local-api)
- [🌐 WebGL: HTTP API blocked (CORS)](#-webgl-http-api-blocked-cors)
- [🚫 Build fails: `[CoreAI] Build aborted: … API key`](#-build-fails-coreai-build-aborted--api-key)
- [🔌 Problem: DI / VContainer errors](#-problem-di--vcontainer-errors)
- [📊 Diagnostics: How to enable verbose logs](#-diagnostics-how-to-enable-verbose-logs)

---

## 🤖 Problem: Model does not respond

### Symptoms
- Empty response from the LLM (`"Empty response from LLM"`)
- Request timeout
- `StubLlmClient` instead of a real model
- No `LLM >` / `LLM <` lines in the console
- In **Offline** or **stub** fallback, the chat shows huge JSON or echoes the full system-style payload — expected from **`1.5.18`**: conversational roles (chat, `Teacher`-style ids, etc.) get a **single line** from **Offline Custom Response** (or `[stub] Offline…`); **Chat** requests with `SourceTag = Chat` receive a **trimmed error message** from the orchestrator when the model returns `Ok: false`, instead of an empty string.

### Diagnostics

**Step 1: Check which backend is selected**

CoreAI does not print a single "backend" line at startup. Check it one of these ways:

- **CoreAI → Setup → Validate Scene** warns `neither OpenAI HTTP settings nor LLMAgent found (will fallback to StubLlmClient)`.
- **🔗 Test Connection** on the `CoreAISettings` asset probes the configured backend.
- In code, `CoreAiBackend.Status` returns the active mode, base URL and model (`IsLive` tells whether a scope is running), and every routed request publishes `LlmBackendSelected` (MessagePipe) with `ExecutionMode` and `ClientType` — a `StubLlmClient` or `OfflineLlmClient` there means the silent fallback below.

```csharp
Debug.Log(CoreAiBackend.Status);   // e.g. "ClientOwnedApi (my-model @ http://localhost:1234/v1)"
```

Every request also logs `LLM > traceId=… role=… backend=…` when it is sent; no such line means the request never reached the LLM pipeline.

**Step 2: Narrow down by backend**

---

### 🔌 LLMUnity does not respond

| Check | How to verify | Fix |
|----------|--------------|---------|
| LLMAgent on scene? | Hierarchy → look for an object with `LLMAgent` | Create an `LLMAgent` on the scene |
| LLM component present? | Inspector LLMAgent → is there an `LLM`? | Add the `LLM` component |
| GGUF file exists? | Inspector LLM → Model Path | Download the model via LLMUnity or LM Studio |
| Service running? | `LLM.started` is `true` (see the snippet below) | Increase **Startup Timeout (sec)** in CoreAISettings → LLMUnity |
| Enough VRAM? | Task Manager → GPU Memory | Use a smaller model (4B instead of 9B) or lower **GPU Layers** |

```csharp
// Programmatic check:
var agent = Object.FindFirstObjectByType<LLMAgent>();
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
| URL correct? | CoreAISettings → Essentials → Base URL | No trailing `/`: `http://localhost:1234/v1` |
| Model set? | CoreAISettings → Essentials → Model | Required for client-owned modes — an empty model fails the request with a configuration error |
| Model loaded? | LM Studio → Status = "Loaded" | Load the model in LM Studio |
| API key required? | OpenAI → yes, LM Studio → no | For LM Studio leave API Key **empty** |
| Port open? | `Test-NetConnection localhost -Port 1234` | Check the firewall |

**Quick check via PowerShell:**
```powershell
# API reachability
Invoke-RestMethod -Uri "http://localhost:1234/v1/models" -Method GET

# Sample request
$body = @{
    model = "your-model-id"
    messages = @(@{ role = "user"; content = "Say OK" })
} | ConvertTo-Json -Depth 3

Invoke-RestMethod -Uri "http://localhost:1234/v1/chat/completions" `
    -Method POST -Body $body -ContentType "application/json"
```

**Typical fix:**
```
1. Start LM Studio
2. Load the model
3. Enable Local Server (port 1234)
4. CoreAISettings → LLM Backend = OpenAiHttp (or LLM Mode = ClientOwnedApi)
5. CoreAISettings → Base URL = http://localhost:1234/v1, Model = the loaded model id
6. Click "🔗 Test Connection"
```

---

### 🔇 Stub instead of model (silent fallback)

**Why Stub was chosen:**
1. Backend = Auto, but neither LLMUnity nor HTTP is available
2. LLMAgent not found on scene and HTTP URL not configured
3. `COREAI_LLM` define is missing (the LLM pipeline is a manual positive opt-in)
4. Package `ai.undream.llm` is not installed (`COREAI_HAS_LLMUNITY` not defined) — LLMUnity backend unavailable

**Fix:**
```
CoreAISettings → LLM Backend = OpenAiHttp (or LlmUnity)
```

Or ensure Auto mode has at least one available backend:
```
CoreAISettings → LLM Backend = Auto
CoreAISettings → Auto Priority = HTTP First  ← if HTTP is primary
```

---

### ⏱️ Request timeout

```
[CoreAiChatPanel] Turn interrupted (reason=timeout): LLM request timed out.
```

On the streaming path the pipeline also logs `LLM ~ (stream) traceId=… | cancelled`; a transport-level timeout with the caller still waiting is logged as `LLM x traceId=… | …`.

**Fix:** Increase the timeout (default `120`):
```
CoreAISettings → Advanced Settings → General → LLM Timeout (sec) = 300
```

For large models (9B+) or weaker hardware you may need 120–300 seconds. The HTTP-level **Timeout (sec)** on the HTTP tab is capped by this value.

> **How the deadline works:** `CoreAiChatService` enforces an idle watchdog (`IdleTimeoutDeadline`, UniTask PlayerLoop-driven and WebGL-safe, re-armed by every streamed chunk and tool-call event), and `TimeoutLlmClientDecorator` bounds calls made outside the chat service. A host deadline must be passed on its own token (`CoreAiChatExternalSubmitOptions.DeadlineCancellationToken`, or the `CoreAiChatService` overloads with a `deadlineToken`) to be reported as a timeout; a `CancelAfter` on the caller's token is reported as a cancellation (`reason=cancelled`, no bubble by default). See [`STREAMING_ARCHITECTURE.md`](STREAMING_ARCHITECTURE.md) §8.

---

## 📜 Problem: Lua failed

Lua runs through the `execute_lua` / `manage_mods` tools of `com.neoxider.coreaimods` (Lua-CSharp, `LuaCsSecureEnvironment` + `LuaCsExecutionGuard`). The tool result — including the Lua error — goes back to the model, which may fix the code and call the tool again. One-off `execute_lua` chunks have no separate automatic repair loop (unless your host wires `LuaCsAiEnvelopeProcessor` itself); for persistent mods, the optional `CoreAiLuaModAutoRepair` component schedules a bounded Programmer repair of a mod that keeps failing.

### Symptoms
- `[ToolCall] … tool=execute_lua status=FAIL …` in the log, with the Lua error in `result=` (enable **Log Results**)
- The model repeats `execute_lua` with corrected code, or gives up
- `sandbox: EXCEEDED_HARD_LIMIT_STEPS (…)`, `sandbox: Lua exceeded … ms.` or `sandbox: EXCEEDED_MEMORY_BUDGET (… bytes)` in the tool result

### Diagnostics

**Type 1: Lua syntax error**

The tool result carries the parser message (for example `unexpected symbol near 'end'`).

**Cause:** The model generated invalid Lua.

**Fix:**
- The model sees the error and may retry on its own; the consecutive-failure guard (`MaxToolCallRetries`, default 3) ends the loop with a final summary turn
- If that fails → improve the Programmer prompt with examples of valid Lua
- Or use a stronger model (4B+ instead of 2B)

---

**Type 2: Calling a non-existent function**

The tool result says the script tried to call a `nil` value (for example `custom_function`).

**Cause:** Lua tries to call a function that is not in the whitelist API.

**Fix:** Implement `CoreAI.Ai.LuaCs.ILuaCsGameRuntimeBindings` and register the function on `LuaCsApiRegistry`:

```csharp
using System;
using CoreAI.Ai.LuaCs;
using CoreAI.Sandbox.LuaCs;
using UnityEngine;

public sealed class MyGameBindings : ILuaCsGameRuntimeBindings
{
    public void RegisterGameplayApis(LuaCsApiRegistry registry)
    {
        registry.Register("custom_function", new Action<string>(msg => Debug.Log($"Custom: {msg}")));
    }
}
```

Pass the bindings to the Lua surface you construct (`LuaCsGameToolExecutor` / `LuaCsAiEnvelopeProcessor`), or, for a mod stack built with `LuaCsModRuntimeFactory.Create`, register the same functions through `LuaCsModStackOptions.AdditionalGameplayBindings`. Reference implementation: `Assets/CoreAI.Demos/ModdableUnits/Scripts/UnitForgeLuaBindings.cs`. The default `RegisterCoreAiMods` composition does not accept custom bindings yet.

See [LUA_BEST_PRACTICES.md](../../CoreAI/Docs/LUA_BEST_PRACTICES.md) for capability gating and anti-patterns.

Or state in the Programmer prompt which functions are available:
```
Available Lua API: report(string), add(a,b), coreai_world_spawn(...), ...
Do NOT use any other functions.
```

---

**Type 3: Infinite loop (step, time or memory limit)**
```
sandbox: EXCEEDED_HARD_LIMIT_STEPS (<max steps>)
sandbox: Lua exceeded <N> ms.
sandbox: EXCEEDED_MEMORY_BUDGET (<budget> bytes)
```

A raw `coroutine.create` coroutine cut at its own per-resume limit reports `sandbox: Lua coroutine resume exceeded <N> ms.` to the code that resumed it; a mod's scheduler thread reports the same kind of trip as `BUDGET_EXCEEDED` with the bound and the author's line. None of these can be caught by `pcall`/`xpcall` inside the run that tripped. Before the 2026-09-24 audit fixes the step line started `LuaCsSecureEnvironment:` and the time line had no prefix; host code classifies a trip by type (`LuaCsExecutionGuard.IsStepBudgetTrip`, `IsMemoryBudgetTrip`), never by this text.

**Cause:** Lua contains an infinite loop or a very heavy operation.

**Fix:**
- `LuaCsExecutionGuard` aborts via wall-clock, step and allocation limits
- The per-resume budget every coroutine arms is set on `CoreAiModsLifetimeScope` (**Lua coroutine resume budget**); `<= 0` restores CoreAI's defaults
- Host code can move the wall-clock half live with `ScriptContext:SetTimeout(seconds)`

---

**Type 4: Repeated failures end the turn**

**Cause:** Every `execute_lua` call in several consecutive tool batches failed (`MaxToolCallRetries`, default 3), or the host-wired `LuaCsAiEnvelopeProcessor` used up `MaxLuaRepairRetries` (default 3).

**Fix:**
1. Improve the Programmer system prompt (add examples)
2. Use a stronger model
3. Verify the whitelist API is correct
4. Raise `CoreAISettings.MaxToolCallRetries` (or `MaxLuaRepairRetries` for the envelope path) only after the above

---

## 🧠 Problem: Memory is not written

### Symptoms
- The agent “forgets” information between calls
- File `persistentDataPath/CoreAI/AgentMemory/<stem>.json` (memory document; the conversation is in `<stem>.history.jsonl`) is empty or not created — `<stem>` is the role id, or `scope-v1-<sha256>` when a memory scope provider is set
- Memory does not appear in the request (it is sent in the ordered tail of the chat history, not in the system prompt)

### Diagnostics

**Step 1: Check that memory is enabled for the role**

```csharp
// By default the memory tool is ON (default action: append) for every built-in role
// (Creator, Builder, Analyzer, Programmer, AINpc, CoreMechanicAI, SmartChat, Merchant)
// and OFF for PlainChat. Custom roles get it through AgentBuilder.WithMemory().

var policy = container.Resolve<AgentMemoryPolicy>();
Debug.Log($"Memory enabled for Creator: {policy.IsMemoryEnabled("Creator")}");
```

**Step 2: Check that the model calls the tool**

Enable tool-call logging:
```
CoreAISettings → Advanced Settings → Debug → Log Tool Calls / Log Arguments / Log Results = ✅
```

You should see in logs:
```
[ToolCall] traceId=… role=Creator tool=memory status=OK dur=…ms args={"action":"write",…} result=…
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
| NullAgentMemoryStore | Expected when using **`RegisterCorePortable()`** without **`suppressDefaultAgentMemoryStore: true`** and no host **`IAgentMemoryStore`**. With **`CoreAILifetimeScope`**, `Persistent` resolves a scoped facade over **`FileAgentMemoryStore`** on all players (WebGL included), while `SessionOnly` resolves it over **`InMemoryAgentMemoryStore`**. If you still see **`NullAgentMemoryStore`**, check custom DI / duplicate registrations. |
| File not created | Check permissions on `persistentDataPath` |
| ChatHistory not working | Chat history works on every backend. Check `AgentMemoryPolicy` for the role (`WithChatHistory`, default on except for Programmer) and, for restore after restart, `PersistChatHistory` (`AgentBuilder.WithChatHistory(..., persistBetweenSessions: true)`) |
| WebGL: nothing survives a page reload, or the console shows `[CoreAiWebGlPersistence] This page did not enable automatic persistentDataPath synchronization` | The web template does not pass **`config.autoSyncPersistentDataPath = true`** to `createUnityInstance()`. Unity's stock templates ship that line **commented out**, and since Unity 6.3 it is the only channel that persists `persistentDataPath` — CoreAI no longer drives the deprecated manual `FS.syncfs`. Add the line to your own template or run **`CoreAI/Setup/Install WebGL Template`** (copies the template shipped in the package into `Assets/WebGLTemplates/CoreAI` and selects it); `CoreAIWebGlPersistentDataSyncBuildGuard` fails the build when it is missing, unless the project defines **`COREAI_WEBGL_NO_PERSISTENCE`** for the Web platform. |
| WebGL: `memory action=write` fails after ~30 s while the data is actually saved | Fixed. CoreAI used to await an `FS.syncfs` completion callback that Unity 6.3 never delivers, so every durability wait ran into the caller's timeout. Durability is now answered immediately; rebuild the player after upgrading **`com.neoxider.coreaiunity`**. |

### Clearing all CoreAI file persistence (Editor)

To reset **everything** CoreAI stores under `persistentDataPath` (memory, persisted chat, summaries, Lua/version JSON — see **`CoreAiPersistentPaths`**): stop Play Mode, then **CoreAI → Delete All Persistent Saves...**. This deletes the whole **`persistentDataPath/CoreAI`** folder. **Project ScriptableObjects** under `Assets/` are **not** affected.

---

## 🔧 Problem: Tool call does not work

### Symptoms
- Model returns text instead of a tool call
- `[ToolCall] … status=FAIL` lines in the log
- The turn ends with a tools-disabled summary after repeated failures
- **`SmartToolCallingChatClientEditModeTests`** (or runtime) stops after **3 LLM rounds** despite successful tools — fixed in **`com.neoxider.coreai` 1.5.15**: MEAI **`ChatMessage.Contents`** is a non-generic **`IList`**; enumerating native **`FunctionCallContent`** must not rely on **`SelectMany` + `Enumerable.Empty<AIContent>()`** over that property.
- Edit Mode logs show tools **`status=FAIL`** with **`get_isPlaying can only be called from the main thread`** (execution count stays **0**) — fixed from **`com.neoxider.coreaiunity` 1.5.16** onward; current hardening uses the **`UnityMainThreadLlmAsyncMarshaler`** Editor play-state mirror (`RuntimeInitializeOnLoadMethod`, **`Application.onBeforeRender`**, and **`EditorApplication.update`**) plus **`ManagedThreadId`** gate so worker threads never call **`Application.isPlaying`** directly.

### Diagnostics

**Type 1: The call arrives as text**

**Cause:** On an endpoint with a native tool channel CoreAI takes calls only from the provider's `tool_calls`; JSON written into the answer is shown as text. A local server whose model writes calls as text needs the text channel.

**Fix:**
1. For LLMUnity or a runtime endpoint whose server rejects `tools`, set the tool channel to `Text` (`LlmUnityToolChannel` on the settings asset, or `LlmEndpointDescriptor.ToolChannel`).
2. For a proxy that advertises native tools but answers with JSON text, set `LlmCompletionRequest.AllowTextShapedToolCallsOnNativeEndpoint = true`.
3. Use a larger model (4B+ recommended).

**Type 2: Tool not registered**
```
Error: Unknown tool 'my_custom_tool'. Available tools: [memory, world_command, ...]
```

**Fix:** Ensure the tool is added to the agent:
```csharp
var agent = new AgentBuilder("MyAgent")
    .WithTool(new MyCustomTool())  // ← add tool
    .Build();
```

The list shows callable names: for a multi-function wrapper such as `camera` it lists the function names (`camera_capture`, `camera_look`, …), which are the names the model must use.

**Type 3: Missing or mistyped arguments**
```
Error: Tool 'weather_command' is missing required argument(s): action. Retry the same tool call with JSON arguments matching this schema: {...}
Error: Argument 'enabled' does not match the expected type for tool 'toggle_light': ...
```

**Cause:** The model omitted a required argument, sent `null`, or sent a value that cannot bind to the parameter type. The tool body did not run and the model may retry. An empty string is **not** refused — validate it in the tool (see [TOOL_AUTHORING_GUIDE](TOOL_AUTHORING_GUIDE.md#required-arguments-null-is-missing-empty-is-present)).

**Type 4: The model repeats the same call**

**Cause:** The model re-emits a call that already succeeded in an earlier turn of the same request.

**Fix:** `ToolExecutionPolicy` does not execute such a cross-turn echo; the model receives `{"ok": true, "duplicate": true, …}` instead. If it keeps happening — improve the prompt.

**Type 5: Agent stopped: exceeded maximum of N tool-call roundtrips**
```
[SmartToolCall] Role 'X' hit the tool-call roundtrip cap (20, from global ICoreAISettings.MaxToolCallRoundtrips) and was stopped …
Agent stopped: exceeded maximum of 20 tool-call roundtrips
```

**Cause:** The task needed **more** tool roundtrips than the cap allows. One roundtrip is one LLM call plus one tool-execution batch. Common for free-build and visual agents that emit 24+ `spawn` calls, or a Programmer iterating Lua.

**Fix:** raise or remove the cap at the right scope:
- Per agent: `new AgentBuilder("Builder").WithMaxToolCallRoundtrips(0)`; `0` = unlimited.
- Per call: `new AiTaskRequest { MaxToolCallRoundtrips = 0 }`.
- Global: `CoreAISettings.MaxToolCallRoundtrips = 40;` or raise it in the CoreAI settings asset.

Priority is per-call → per-agent → per-role policy (`AgentMemoryPolicy`) → global. The built-in **Programmer**, **Creator** and **Builder** roles set the per-role step to `0` = unlimited, so they are uncapped by default.

---

## 🌍 Problem: World command is not executed

### Symptoms
- Objects do not spawn
- `[World] Unknown prefabKey 'X'. Available primitives: …` (or `[World] prefab not found: 'X'.`) in logs
- The object named in `coreai_world_spawn({ prefab = ..., name = ... })` never appears (the call only publishes a command; the executor logs why it failed)

### Diagnostics

**Issue 1: Prefab registry not assigned**
```
[World] prefab registry not assigned
```

**Fix:**
1. Create → CoreAI → World → Prefab Registry
2. Add prefabs with keys
3. Select `CoreAILifetimeScope` → **Add Lua / World Commands Module**, then assign the asset on the child `CoreAiLuaWorldModule` (see [WORLD_COMMANDS.md](WORLD_COMMANDS.md))

**Issue 2: Prefab key not found**
```
[World] Unknown prefabKey 'Boss'. Available primitives: …
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
$env:COREAI_OPENAI_TEST_MODEL = "your-model-id"
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

## 🌐 WebGL: HTTP API blocked (CORS)

### Symptoms

- Browser devtools: **Cross-Origin Request Blocked** / **CORS policy** when calling your OpenAI-compatible API from a WebGL build.
- **`UnityWebRequest`** fails or returns an empty body even though the same URL works from the desktop player or Postman.

### Cause

Unity WebGL uses the browser **Fetch** stack ([Unity Manual — Web networking](https://docs.unity3d.com/6000.0/Documentation/Manual/webgl-networking.html)). Cross-origin responses must include **`Access-Control-Allow-Origin`** (and usually **`Access-Control-Allow-Headers`** for `Authorization`, `Content-Type`, etc.). When **`WebGlNativeStreaming`** is on (default), streaming uses **`FetchSseOpenAiTransport`** + **`CoreAiSseFetch.jslib`**; otherwise **`UnityWebRequestOpenAiTransport`**.

### Fix

1. Host the LLM API with CORS enabled for your game’s origin, **or** put a **same-origin** reverse proxy in front of the model (e.g. `https://yourgame.com/api/v1` → LM Studio).
2. For local dev, browser extensions or a tiny proxy that adds CORS headers are common; shipping builds need a real server config.
3. See **`HTTP_TRANSPORT_SPEC.md`** for transport selection.
4. **Preflight + wildcard ACAO:** if the console reports *`credentials mode is 'include'` … `Access-Control-Allow-Origin` must not be the wildcard `*`* — keep **`SameOriginCredentials`** **off** on **`CoreAISettingsAsset`** (default **`fetch` `credentials: 'omit'`** since **`com.neoxider.coreaiunity` 1.6.16**; **`Authorization: Bearer …`** is still sent). Turn **`SameOriginCredentials`** **on** only when you intentionally need **`same-origin`** cookie behaviour.

---

## 🚫 Build fails: `[CoreAI] Build aborted: … API key`

The player build stops before compilation with a `BuildFailedException`. This is deliberate: CoreAI refuses to ship a provider key inside the player.

- **Any platform** — a `CoreAISettings` asset under a `Resources/` folder (default `Assets/Resources/CoreAISettings.asset`) has a non-empty `apiKey` or `secondaryApiKey`. Even a local placeholder such as `lm-studio` fails.
- **WebGL only** — a non-empty `ApiKey` combined with `ClientOwnedApi`, `ClientLimited`, or `ServerManagedApi`.

**Fix:** clear the key on the asset named in the message and supply it at runtime (`CoreAiBackend.SetApiKey`, an environment variable, secure storage), or move the settings asset out of `Resources/` and assign it on `CoreAILifetimeScope`. Run `CoreAI/Validate Production Settings` to see the findings without starting a build. Full detail: [WEBGL_BUILD_TROUBLESHOOTING.md](WEBGL_BUILD_TROUBLESHOOTING.md).

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
   - Llm Routing Manifest (optional)
   - Lua / World Commands module with its prefab registry (optional child `CoreAiLuaWorldModule`)

```
Hierarchy:
└── CoreAILifetimeScope  ← Root LifetimeScope
    ├── LLM (LLM + LLMAgent)
    ├── GameManager
    └── ... your objects
```

---

## 📊 Diagnostics: How to enable verbose logs

### Turn on full diagnostics quickly

```
CoreAISettings → Advanced Settings → Debug:
  ✅ Log LLM Input / Log LLM Output — prompt and response previews
  ✅ Log Tool Calls / Log Arguments / Log Results — one [ToolCall] line per call
  ✅ MEAI Debug Logging      — MEAI pipeline logs
  ✅ HTTP Debug Logging       — raw HTTP requests
  ✅ Log Orchestration Metrics — orchestrator metrics
```

### What to look for in logs

| Pattern | Meaning |
|---------|----------|
| `LLM > traceId=… role=… backend=…` | Request sent (`LLM > (stream)` on the streaming path) |
| `LLM < traceId=… wallMs=… \| tokens…` | Response received |
| `LLM x traceId=… \| <error>` | Request failed (including transport timeouts; a caller or deadline cancel is not logged here) |
| `LLM ~ traceId=… \| … retry n/m` | Retry after a recoverable failure; `LLM ~ (stream) … \| cancelled` for a cancelled stream |
| `[ToolCall] traceId=… tool=<name> status=OK\|FAIL` | One tool call (with `args=` / `result=` when enabled) |
| `[ToolPolicy] <name> rejected: …` | Arguments refused before the tool ran |
| `ApplyAiGameCommand traceId=… type=…` | Command routed by `AiGameCommandRouter` |
| `[Lua report] …` | `report(...)` called from Lua |
| `[World] …` | World command diagnostics (`[World] prefab registry not assigned`, `[World] list_prefabs: …`) |
| `[CoreAiChatPanel] Turn interrupted (reason=timeout\|cancelled)` | Chat turn ended by a deadline or a stop |

### Filter by TraceId

Each request gets a unique `TraceId`. Use it to trace the command path:

```
Unity console filter: "abc123"

LLM > traceId=abc123 role=Programmer backend=…
[ToolCall] traceId=abc123 role=Programmer tool=execute_lua status=OK dur=12ms
LLM < traceId=abc123 role=Programmer backend=… wallMs=2100 | …
ApplyAiGameCommand traceId=abc123 type=AiEnvelope role=Programmer …
```

---

## 🚑 Quick problem checklist

```
❓ Model silent?
  → Check LLM Backend / LLM Mode and Model in CoreAISettings
  → Check that LM Studio / LLMAgent is running
  → Click "🔗 Test Connection"

❓ Empty response?
  → Increase LLM Timeout (120+)
  → Enable Keep Alive for LLMUnity
  → Check LLM > / LLM < / LLM x logs

❓ Tool call not firing?
  → Enable Log Tool Calls (Debug tab)
  → Check that the tool is added to the agent
  → Use a 4B+ model for reliable tool calling

❓ Lua failing?
  → Check whitelist API in the prompt
  → Read the execute_lua result in the [ToolCall] line
  → Register missing functions via ILuaCsGameRuntimeBindings

❓ Memory not saving?
  → Check AgentMemoryPolicy for the role
  → Check that the model calls the memory tool
  → Check persistentDataPath

❓ Object not spawning?
  → Assign CoreAiPrefabRegistryAsset on CoreAiLuaWorldModule
  → Add prefab key to the registry
  → Check [World] logs

❓ Tests hanging?
  → Keep Alive = true
  → Startup Timeout = 120
  → For CI: use Stub backend
```

---

## Problem: Reasoning model returns empty content

### Symptoms

- Streaming tests or chat demos wait for visible chunks, then finish with an empty final response.
- The local OpenAI-compatible server logs show `reasoning_content` or long thinking output, but `content` is empty.
- Smaller `max_tokens` values make the issue easier to reproduce.

### Cause

Some Qwen/DeepSeek-style thinking models can spend the whole output budget on reasoning tokens before they emit user-visible content. In that state the model is reachable and generating, but CoreAI has no visible assistant text or tool payload to apply.

### Fix

1. In `CoreAISettings.asset`, leave **Reasoning Mode** as **Provider Default** unless the provider needs an explicit override.
2. For Qwen OpenAI-compatible endpoints that support it, set **Reasoning Mode** to **Disabled**. CoreAI sends `enable_thinking=false` and `chat_template_kwargs.enable_thinking=false`.
3. Keep **Max Output Tokens** high enough for the scenario (or leave **Override Max Output Tokens** off). The local 27B Qwen test profile uses `20000` output tokens and a `128000` context-window hint.
4. If the provider supports it, use **Thinking Budget Tokens** to cap reasoning. `0` omits the field.

---
> 📖 **Related documents:**
> - [COREAI_SETTINGS.md](COREAI_SETTINGS.md) — all settings
> - [DEVELOPER_GUIDE.md](DEVELOPER_GUIDE.md) — architecture
> - [LLMUNITY_SETUP_AND_MODELS.md](LLMUNITY_SETUP_AND_MODELS.md) — LLM setup
