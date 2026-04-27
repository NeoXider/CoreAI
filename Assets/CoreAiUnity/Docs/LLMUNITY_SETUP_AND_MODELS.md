# LLMUnity: editor verification, models in builds, OpenAI-compatible API

**Goal:** Quickly confirm **LLMUnity + CoreAI** work; which **GGUF** files to include in builds; how to switch to **OpenAI-compatible HTTP** (cloud, LM Studio, vLLM, etc.).

**From scratch:** [QUICK_START.md](QUICK_START.md). **Demo scene in the Inspector:** [../../_exampleGame/Docs/UNITY_SETUP.md](../../_exampleGame/Docs/UNITY_SETUP.md).

Related docs: [DEVELOPER_GUIDE.md](DEVELOPER_GUIDE.md) (core data flow), [AI_AGENT_ROLES.md](AI_AGENT_ROLES.md) (roles and model sizes), `CoreAILifetimeScope` (LLM backend selection).

### Official LLMUnity documentation (Undream AI)

- Overview and API: [undream.ai/LLMUnity](https://undream.ai/LLMUnity)  
- Repository (README, Quick start, **LLM model management**): [github.com/undreamai/LLMUnity](https://github.com/undreamai/LLMUnity)  

**Quick start (short):** GameObject → **LLM** component → **Download model** or **Load model** (.gguf) → separate (or same) object → **LLMAgent** → in the Inspector, **LLM** reference to the server → in code `await llmAgent.Chat("...")`.  
Before the first request in builds with **Download on Start**, the docs recommend `await LLM.WaitUntilModelSetup();` — **MeaiLlmUnityClient** in CoreAI waits for global model setup and the **LLM** server to be ready before **Chat**.

**Model Manager (LLM Inspector):** the model list is copied into the build; the **Build** checkbox excludes a specific model from the build; the **radio** selection writes the path into **`LLM.model`** (save it **in the scene**). If several models have files on disk and `model` is empty, CoreAI may **auto-pick** one (see `LlmUnityModelBootstrap`: priority to entries with **Build** checked).

**CoreAI on top of LLMUnity:** when `LLM.model` is empty, the early guard **`LlmUnityAutoDisableIfNoModel`** disables LLMUnity so the console is not spammed with “No model file provided!”; DI then uses **`StubLlmClient`**.

---

## 1. What should be on the scene (local LLMUnity)

1. GameObject with **`LLM`** (server/inference): a **Qwen3.5 2B** (or other) GGUF model selected; optionally **Num GPU Layers** &gt; 0 on GPU.
2. Child (or linked) object with **`LLMAgent`**: Inspector references this **`LLM`**, **Remote** off for a purely local setup.
3. **`CoreAILifetimeScope`** on the composition root: **Open Ai Http Llm Settings** empty **or** the asset has **Use Open Ai Compatible Http** disabled — then `ILlmClient` = **MeaiLlmUnityClient** → your `LLMAgent`.

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

**Important:** calls run on Unity’s **main thread** (same as the LLMUnity adapter). Do not store keys in a public repository.

---

## 5. Default system prompts

Chain: manifest (if set) → `Resources/AgentPrompts/System` → **built-in** strings in `BuiltInDefaultAgentSystemPromptProvider` / `BuiltInAgentSystemPromptTexts` (already registered in `RegisterAgentPrompts`).

Roles: **Creator, Analyzer, Programmer, AINpc, CoreMechanicAI, PlayerChat** — see `AgentRolesAndPromptsTests`.

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
- **`LuaAiEnvelopeProcessor`** (Core) + **`AiGameCommandRouter`**: Lua is taken from the envelope (fenced `lua` block or JSON **ExecuteLua**) and run in **`SecureLuaEnvironment`** with **`report`**, **`add`** (see `LoggingLuaRuntimeBindings`).
- Limits: `LuaExecutionGuard` applies a best-effort wall-clock and “step” limit (via `InstructionLimitDebugger`) so infinite Lua loops cannot hang forever.
- Success / failure publish **`LuaExecutionSucceeded`** / **`LuaExecutionFailed`**. On failure with **Programmer**, the orchestrator is invoked again with **`lua_error`** / **`fix_this_lua`** in the user payload (up to **4** repair generations).
- **EditMode:** `LuaAiEnvelopeProcessorEditModeTests`, `AiLuaPayloadParserEditModeTests`, `ProgrammerLuaPipelineEditModeTests`.
- Sample game: **`CoreAiLuaHotkey`** on the object with **`ExampleRogueliteEntry`** — **F9** queues a Programmer task.

**This file’s version:** aligned with the core (April 2026): TraceId, timeout, `GameLogFeature.Llm`, arena sample (Creator waves).
