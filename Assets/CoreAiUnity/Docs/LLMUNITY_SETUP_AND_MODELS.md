# LLMUnity: editor verification, models in builds, OpenAI-compatible API

**Goal:** Quickly confirm **LLMUnity + CoreAI** work; which **GGUF** files to include in builds; how to switch to **OpenAI-compatible HTTP** (cloud, LM Studio, vLLM, etc.).

**From scratch:** [QUICK_START.md](QUICK_START.md). **Demo scene in the Inspector:** [../../_exampleGame/Docs/UNITY_SETUP.md](../../_exampleGame/Docs/UNITY_SETUP.md).

Related docs: [DEVELOPER_GUIDE.md](DEVELOPER_GUIDE.md) (core data flow), [AI_AGENT_ROLES.md](AI_AGENT_ROLES.md) (roles and model sizes), `CoreAILifetimeScope` (LLM backend selection).

Since 5.9, a local model is a named runtime endpoint rather than a global exclusive backend. CoreAI waits
for both the LLMUnity native `started/failed` state and its OpenAI-compatible HTTP socket before publishing
it as Ready. External HTTP endpoints prefer `GET {BaseUrl}/models`; when that optional route returns
`404`/`405`, CoreAI falls back to a minimal `POST {BaseUrl}/chat/completions` and requires a handler-level
response. Authentication, missing completion routes, server errors, and network failures still fail
readiness. LLMUnity does not implement
`/v1/models`, so after native startup CoreAI probes `POST /v1/chat/completions`; an HTTP response proves the
socket and route accept connections, except `401`/`403`, which remain failures when authentication is
configured. A request that arrives during startup awaits the shared activation rather than bypassing
readiness. This permits an LLMUnity agent and one or more HTTP agents to work concurrently.

Both normal autostart and hot endpoint activation use the same injected
`UnityWebRequestOpenAiReadinessProbe`. Its contract and status policy live in portable CoreAI; non-Unity .NET
hosts can use `HttpClientOpenAiReadinessProbe`. Only the HTTP check is shared—LLMUnity model lifecycle stays
inside CoreAiUnity.

CoreAI writes structured `[CoreAI.LLMUnity]` lifecycle logs for both runtime endpoints and the legacy
autostart path. `phase=native_startup` measures the llama.cpp/GGUF warmup itself; `phase=http_readiness`
measures the following OpenAI-compatible route probe. Completed phases include `durationMs` plus endpoint
id/display name, model filename, LLMAgent name, and port. Failures include the exception type and a
single-line message. API keys and full local model paths are never included.

For more than one local endpoint, create a distinct, named `LLMAgent`/`LLM` host for each endpoint and give
each host a unique port. Point `LlmEndpointDescriptor.UnityAgentName` at the corresponding GameObject.
Changing a running host's model or port in place is intentionally rejected: it would tear down the host used
by the currently published generation and violate zero-downtime switching. Stage the replacement on a
separate host, wait for Ready, then change the agent/profile assignment.

### Official LLMUnity documentation (Undream AI)

- Overview and API: [undream.ai/LLMUnity](https://undream.ai/LLMUnity)  
- Repository (README, Quick start, **LLM model management**): [github.com/undreamai/LLMUnity](https://github.com/undreamai/LLMUnity)  

**Quick start (short):** GameObject → **LLM** component → **Download model** or **Load model** (.gguf) → separate (or same) object → **LLMAgent** → in the Inspector, **LLM** reference to the server → in code `await llmAgent.Chat("...")`.  
Before the first request in builds with **Download on Start**, the docs recommend `await LLM.WaitUntilModelSetup();` — **MeaiLlmUnityClient** in CoreAI waits for global model setup and the **LLM** server to be ready before **Chat**.

**Model Manager (LLM Inspector):** the model list is copied into the build; the **Build** checkbox excludes a specific model from the build; the **radio** selection writes the path into **`LLM.model`** (save it **in the scene**). If several models have files on disk and `model` is empty, CoreAI may **auto-pick** one (see `LlmUnityModelBootstrap`: priority to entries with **Build** checked).

**Core AI Settings (recommended since v1.7.4):** set **GGUF Model** (and optional **Manual override**) on **`CoreAISettingsAsset`**. At runtime, **`LlmUnityHostConfigurator`** applies that hint **before** Model Manager fallback, so the loaded server matches the asset even when the scene **`LLM.model`** field was left empty. Enable **Auto-create LLM host** to spawn **`CoreAI_LLMUnity_Runtime`** (`LLM` + `LLMAgent`) when no agent exists in loaded scenes; enable **Autostart local server** to warm up llama.cpp shortly after play (first chat is faster; uses **Startup Timeout**).

**CoreAI on top of LLMUnity:** when `LLM.model` is empty, the early guard **`LlmUnityAutoDisableIfNoModel`** also tries **`CoreAISettingsAsset.GgufModelPath`** before disabling LLMUnity; if still unresolved, it disables LLMUnity so the console is not spammed with “No model file provided!”; DI then uses **`StubLlmClient`**.

---

## 1. What should be on the scene (local LLMUnity)

**Option A — minimal (v1.7.4+):** only **`CoreAILifetimeScope`** + **`CoreAISettingsAsset`** with **LLM Backend** = **LLM Unity** or **Auto** (Unity first). Turn on **Auto-create LLM host** and set **GGUF Model**; CoreAI creates **`CoreAI_LLMUnity_Runtime`** at runtime. Optionally add **Autostart local server** to load GGUF at play start.

**Option B — classic manual setup:**

1. GameObject with **`LLM`** (server/inference): a **Qwen3.5 2B** (or other) GGUF model selected; optionally **Num GPU Layers** &gt; 0 on GPU.
2. Child (or linked) object with **`LLMAgent`**: Inspector references this **`LLM`**, **Remote** off for a purely local setup.
3. **`CoreAILifetimeScope`** on the composition root: **Open Ai Http Llm Settings** empty **or** the asset has **Use Open Ai Compatible Http** disabled — then `ILlmClient` = **MeaiLlmUnityClient** → your `LLMAgent`.

CoreAI still applies **GGUF Model** from **`CoreAISettingsAsset`** to the resolved **`LLM`** when **`LLM.model`** is empty, so the Inspector asset and the scene stay aligned.

**Check:** Play Mode → console without model load errors; orchestrator/chat call `ILlmClient` (see AI-level logs).

**LLMUnity logs:** on the **LLM** component set **Log Level = All** while debugging (as in your screenshot).

**CoreAI logs (model request/response, agent role):** `CoreAILifetimeScope` registers `ILlmClient` via **`LoggingLlmClientDecorator`**. In the console look for **`[Llm]`** inside **`[CoreAI]`**:
- **`LLM ▶`** — `traceId`, role, `backend`, preview of **system** / **user** (length in characters);
- **`LLM ◀`** — same `traceId`, **`wallMs`**, tokens and **tok/s** for OpenAI-compatible HTTP (if JSON includes `usage`); for **LLMUnity**, token counts in `Chat()` are unavailable — the log shows “tokens n/a”;
- the next **`ApplyAiGameCommand`** line in **`[MessagePipe]`** carries the **same `traceId`** — trace “model → in-game command”.

Long text is truncated — limits are in `LoggingLlmClientDecorator.cs`. Legacy **Game Log Settings** without the **Llm** bit: opening the asset in the Inspector runs `OnValidate` migration (adds **Llm**), or enable it manually.

**Bottom line:** if the memory parser runs, `JsonPayload` in the router may differ from the raw **LLM ◀** block.

**Model request timeout:** on **`CoreAILifetimeScope`**, **Llm Request Timeout Seconds** (default **15**). **0** disables. The decorator passes a linked `CancellationToken`; OpenAI HTTP cancels the request; LLMUnity cancels where the code checks the token — full cancellation of a stuck native call without package support is not guaranteed.

---

## 2. Model recommendations (Qwen 3.5 GGUF)

| Profile | Model (guideline) | Build | Notes |
|--------|-------------------|------|--------|
| **Minimum / weak hardware** | **Qwen3.5 2B** Q4_K_M (or similar) | **Include in build** as the main default | Fast, low VRAM/RAM; enough for JSON and simple lines with strict prompts. |
| **Balanced** | **Qwen3.5 ~4B** Q4_K_M | Optional: second preset or DLC asset pack | Better dialogue and **Analyzer** / **AINpc** reports. |
| **Quality** | **Qwen3.5 ~9B** Q4_K_M | Optional: “High quality” profile only | Heavier memory; raise **GPU layers**. |

**Build:** in LLMUnity each model has a **Build** flag — for production usually **one** primary (2B) plus optional separate “HD” builds with 4B/9B without forcing every model into one distribution.

**Download:** **Download on Start** is handy in development; for release prefer **models bundled** with **Build** (StreamingAssets/resources) so offline behavior is predictable.

---

## 3. LLMUnity Remote mode (do not confuse with OpenAI HTTP)

On **`LLM`**, **Remote** means “host a server that clients connect to” (port and key in the Inspector).

On **`LLMAgent`**, **Remote** + **host/port** — client to an **LLMUnity-compatible** server (same UndreamAI/llama stack), **not** raw `https://api.openai.com`.

For **OpenAI-compatible** (`/v1/chat/completions`) use section 4.

---

## 4. OpenAI-compatible API (replace or complement local)

1. **Create → CoreAI → LLM → OpenAI-compatible HTTP** — ScriptableObject.
2. Fill **Api Base Url** (no trailing slash), for example:
   - `https://api.openai.com/v1`
   - `http://127.0.0.1:1234/v1` (typical LM Studio)
3. **Api Key** — required for OpenAI; often empty for a local proxy.
4. **Model** — server-side model name (`gpt-4o-mini`, `qwen2.5-7b-instruct`, …).
5. Enable **Use Open Ai Compatible Http**.
6. Drag the asset onto **`CoreAILifetimeScope` → Open Ai Http Llm Settings**.

Then **`ILlmClient` = OpenAiChatLlmClient**; scene `LLMAgent` is **not** used for core calls (you can leave it disabled).

**Wire-level parity with cloud APIs:** GGUF via in-process **`LLMAgent`** goes through **`LlmUnityMeaiChatClient`** (flattened transcript + text-shaped tool JSON, aligned between streaming and non-streaming). For the same HTTP semantics as OpenAI-compatible **`/chat/completions`** (role-separated `messages`, native `tools`, SSE streaming), keep **Use Open Ai Compatible Http** enabled and point the asset at a local server URL such as **`http://127.0.0.1:1234/v1`** (LM Studio, Ollama’s OpenAI shim, llama.cpp server). CoreAI then uses **`MeaiOpenAiChatClient`** instead of the LLMUnity adapter.

**Important:** calls run on Unity’s **main thread** (same as the LLMUnity adapter). Do not store keys in a public repository.

---

## 4.1. Tool-call channel of the local server (llama.cpp / LLMUnity)

**Fact about llama.cpp.** On its OpenAI-compatible `/v1/chat/completions` route llama.cpp supports
native tool calls: `tools` schemas are converted into a GBNF grammar, calls are parsed by the server and
returned in `tool_calls` (including as SSE deltas). There is a single condition — the server must run with a jinja template
that supports tool-use. A server **without** jinja rejects any request with `tools` with the error
`tools param requires --jinja flag` (HTTP **500**, `server_error`; in other builds — 400). The capability
is determined by the **server**, not by which client connected to it.

**Fact about LLMUnity 3.0.3.** The `LLM` component starts the server via `llmService.StartServer("", port, APIKey)`
with signature `StartServer(string host, int port, string apiKey)` — the first parameter is the host; command-line
arguments cannot be passed through the component. The model is created via `LLMService.CreateLLM(...)` with a
fixed parameter set (server command: `-m … -t -1 -np N -c … -b … --context-shift -fa off`). In the
`UndreamAI.LlamaLib` binding there is `LLMService.FromCommand(string paramsString)`, which accepts a llama.cpp parameter
string, but the `LLM` component does not use it.

**Established by a live run on 2026-09-06.** The built-in **LlamaLib v2.0.5** server (bundled with LLMUnity 3.0.3),
started with the same `LLMService_Construct` + `LLM_Start_Server` as the `LLM` component, **accepts `tools` and
returns native `tool_calls`** (`finish_reason: "tool_calls"`, SSE streaming with `tool_calls` deltas) —
its llama.cpp is built with jinja by default. `/v1/models` still answers 404. So for stock LLMUnity, native calls
**already work**; a build with a different LlamaLib version may behave differently, which is exactly why
the channel is not hard-coded.

**How CoreAI determines the channel.**

- `LlmEndpointDescriptor.ToolChannel` (`Auto` / `Native` / `Text`) — for runtime endpoints.
  An explicit value takes priority and needs no capability probe. With `Auto`, the factory
  `LlmEndpointClientFactory` for **LlmUnity and HttpOpenAi** performs a single probe after the readiness check:
  `POST /v1/chat/completions` with a declared tool and `max_tokens: 1`. Accepted — `native`;
  rejected with a jinja error — `text`; transport/other error — `text` with a warning.
  This probe checks server acceptance of the parameter, not the call quality of a specific model.
  Caller cancellation stays a cancellation and does not turn into a successful activation.
- In `Text`, the local MEAI handler keeps executing extracted calls, but outgoing HTTP requests
  do not contain `tools`, `tool_choice`, `parallel_tool_calls`, including retries and values from `ExtraBodyJson`.
  Therefore the fallback after a jinja rejection does not repeat the same unsupported request.
- `CoreAISettingsAsset.LlmUnityToolChannel` — for the legacy path (`LlmPipelineInstaller`, profiles
  `LlmClientRegistry`): the client there is built before the server starts, so there is no probe; `Auto` = `native` per the established
  fact about the specific bundled LlamaLib v2.0.5. This is the boundary of the synchronous DI adapter, not a rule for an
  arbitrary server. Explicit `Native`/`Text` are preserved; for a different build use an explicit setting
  or runtime registration with a probe after readiness.
- The choice **is never silent**: `[CoreAI.LLMUnity] phase=tool_channel status=native|text … reason="…"` for
  runtime endpoints and `[CoreAI.LLM] tool channel for endpoint "…" = native|text — …` for the other paths.

**If you need the native channel but the server does not provide it** (a different LlamaLib build, your own llama.cpp): start
llama.cpp yourself — `llama-server -m model.gguf --jinja --port 8080` (add `--chat-template …` with
tool-use if needed) — and connect it as a regular HTTP endpoint `http://127.0.0.1:8080/v1` (section 4), or set
`ToolChannel` explicitly.

---

## 5. Default system prompts

Chain: manifest (if set) → `Resources/AgentPrompts/System` → **built-in** strings in `BuiltInDefaultAgentSystemPromptProvider` / `BuiltInAgentSystemPromptTexts` (already registered in `RegisterAgentPrompts`).

Roles: **Creator, Analyzer, Programmer, AINpc, CoreMechanicAI, PlainChat, SmartChat** — see `AgentRolesAndPromptsTests`.

---

## 6. Pre-release checklist

- [ ] One primary model profile chosen (**2B** in build recommended).
- [ ] For API: OpenAI asset, key not in git, HTTPS for production.
- [ ] EditMode agent tests run; Play Mode smoke for chat and orchestrator.
- [ ] **Num GPU layers** and **context size** aligned with minimum target hardware.

---

## 7. Play Mode tests (runtime in the editor)

**How to test end-to-end behavior:** (1) **Play:** Play Mode, console filter `[Llm]` — what went to the model and what came back; `[MessagePipe]` — what was published to the game. (2) **No GPU/model:** EditMode orchestrator/parser tests (`AgentMemoryEditModeTests`, `AgentRolesAndPromptsTests`, …) with **Stub**. (3) **Real model in Play Mode:** shared helper **`PlayModeProductionLikeLlmFactory.TryCreate`** — same order as **`CoreAILifetimeScope`**: when OpenAI-compatible **HTTP** is configured (env, see below), **`OpenAiChatLlmClient`** is used; otherwise **LLMUnity** (runtime **LLM + LLMAgent**, GGUF from Model Manager: prefer **qwen** + **0.8** in the filename, else `LlmUnityModelBootstrap`). Optionally **`COREAI_PLAYMODE_LLM_BACKEND`** = `auto` | `http` | `llmunity` overrides choice for all tests that pass `preference: null` to the factory. (4) **Prompt regression:** after changing system/user templates, run the matching EditMode tests.

**`CoreAI.PlayModeTests`** assembly (in the current Unity setup some `[UnityTest]` methods also appear under **EditMode** in Test Runner — use the full class name):

| Test | Meaning |
|------|--------|
| `AiOrchestratorAllRolesPlayModeTests` | **`Orchestrator_EachBuiltInRole_PublishesEnvelope_WithStub`** — **StubLlmClient**. **`Orchestrator_EachBuiltInRole_PublishesEnvelope_WithProductionLikeLlm_Auto`** — same scenario via **`PlayModeProductionLikeLlmFactory`** (HTTP or LLMUnity). |
| `OpenAiLmStudioPlayModeTests` | Smoke **`CompleteAsync`** through the factory with forced **HTTP**; without env — **Ignored**. |
| `AgentMemoryWithRealModelPlayModeTests` | **`…_ViaProductionLikeBackend_Auto`** — Creator memory via factory (**Auto**). Separate **HTTP-only** / **LLMUnity-only** paths for narrow debugging. |

**LM Studio / OpenAI-compatible (PowerShell, before Play Mode tests):**

Explicit variables:

```powershell
$env:COREAI_OPENAI_TEST_BASE = "http://<LM_STUDIO_HOST>:1234/v1"
$env:COREAI_OPENAI_TEST_MODEL = "<id from GET http://<LM_STUDIO_HOST>:1234/v1/models>"
# if needed:
# $env:COREAI_OPENAI_TEST_API_KEY = "..."
```

Or a single flag (handy on a fixed dev machine; **do not enable in CI** without an explicit network policy):

```powershell
$env:COREAI_OPENAI_TEST_USE_PROJECT_DEFAULTS = "1"
```

That pulls constants from `PlayModeOpenAiTestConfig` in the **CoreAI.PlayModeTests** build (example: `http://192.168.56.1:1234/v1` and model `qwen3.5-35b-a3b-uncensored-hauhaucs-aggressive`). Change constants in code to match your LM Studio.

Force backend for tests using `TryCreate(preference: null)`:

```powershell
$env:COREAI_PLAYMODE_LLM_BACKEND = "http"    # or llmunity, auto
```

Then **Window → General → Test Runner → PlayMode** → run **CoreAI.PlayModeTests**.

**Important:** base URL must end with **`/v1`** (LM Studio OpenAI-compatible API).

---

## 8. Programmer and Lua (runtime execution)

- The orchestrator publishes **`AiEnvelope`** with **`JsonPayload`** = raw LLM response plus **`SourceRoleId`**, **`SourceTaskHint`**, **`LuaRepairGeneration`**, **`TraceId`** (correlation id for logs and Lua repair).
- **`LuaCsAiEnvelopeProcessor`** (Mods) + **`AiGameCommandRouter`**: Lua is taken from the envelope (fenced `lua` block or JSON **ExecuteLua**) and run in **`LuaCsSecureEnvironment`** under the mod runtime's bindings.
- Limits: `LuaCsExecutionGuard` applies wall-clock, step and allocation limits so infinite Lua loops cannot hang forever; the per-resume coroutine budget is the game's to set (`LuaCsCoroutineBudgetSettings` on `CoreAiModsLifetimeScope`).
- Success / failure publish **`LuaExecutionSucceeded`** / **`LuaExecutionFailed`**. On failure with **Programmer**, the orchestrator is invoked again with **`lua_error`** / **`fix_this_lua`** in the user payload (up to **4** repair generations).
- **EditMode:** `AiLuaPayloadParserEditModeTests`; **PlayMode (live provider):** `ProgrammerLuaModsLivePlayModeTests`.
- Sample game: **`CoreAiLuaHotkey`** on the object with **`ExampleRogueliteEntry`** — **F9** queues a Programmer task.

**This file’s version:** aligned with the core (April 2026): TraceId, timeout, `GameLogFeature.Llm`, arena sample (Creator waves).
