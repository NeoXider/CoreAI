# CoreAI Unity (`com.neoxider.coreaiunity`)

The Unity host for [CoreAI](../CoreAI/README.md): DI wiring, a drop-in UI Toolkit chat panel,
streaming HTTP/SSE and LLMUnity clients, WebGL transports, persistence, and Editor setup menus.

The agent logic itself lives in the engine-free [`com.neoxider.coreai`](../CoreAI/README.md). This
package is the adapter layer that puts it on a scene.

| Package | Depends on | Version |
|---|---|---|
| `com.neoxider.coreaiunity` | `com.neoxider.coreai` (same version, lockstep) | see [`package.json`](package.json) |

---

## Who this is for

- **You are shipping a Unity game** and want NPCs, an in-game teacher, or agents that change a running
  world — this package plus the core is the whole base install.
- **You want the chat UI without writing UI** — one Editor menu item produces a working scene.
- **You are on WebGL** — this package owns the browser-safe transports and the async guards that make
  that work; the core alone does not.

If your host is **not** Unity, you do not need this package at all. See
[`tools/portable`](../../tools/portable/README.md).

---

## Quick start

**1. Install** (Unity Package Manager → *Add package from Git URL*, core first):

```text
https://github.com/NeoXider/CoreAI.git?path=Assets/CoreAI
https://github.com/NeoXider/CoreAI.git?path=Assets/CoreAiUnity
```

Then `CoreAI → Setup → Install Git Dependencies` (VContainer / MessagePipe / UniTask) and install
`Microsoft.Extensions.AI` via NuGetForUnity. Step-by-step: [INSTALL.md](../../INSTALL.md).

**2. Make a scene:**

```text
CoreAI → Setup → Create Chat Demo Scene
```

That creates `CoreAILifetimeScope`, a `CoreAiChatPanel`, panel settings and a `CoreAiChatConfig_Demo`
asset. Set your backend in `CoreAI → Settings` and press **Play**.

**3. Call the model from any script** — no DI boilerplate:

```csharp
using CoreAI;

string reply = await CoreAi.AskAsync("Hello!");                     // Task<string?>

await foreach (string chunk in CoreAi.StreamAsync("Tell a story", "SmartChat"))
{
    label.text += chunk;
}
```

**4. Build an agent that calls your code:**

```csharp
using CoreAI.Ai;

Dictionary<string, int> stock = new() { ["fire sword"] = 0, ["iron sword"] = 3 };

AgentConfig blacksmith = new AgentBuilder("Blacksmith")
    .WithSystemPrompt("You are a blacksmith. Sell weapons and remember purchases.")
    .WithTool(new DelegateLlmTool("stock_of", "How many of an item are in stock.",
        (string item) => stock.TryGetValue(item, out int count) ? count.ToString() : "0"))
    .WithMemory()
    .WithChatHistory()
    .WithMode(AgentMode.ToolsAndChat)
    .Build();

string answer = await CoreAi.AskAsync("Got any fire swords?", "Blacksmith");
```

`Build()` registers the role with the live `AgentMemoryPolicy` when one exists (that is, once
`CoreAILifetimeScope` has built). Use `BuildDetached()` when you want the `AgentConfig` without that
side effect, and `AgentConfig.ApplyToPolicy(policy)` to register it later against a policy you hold.

Reference: [AGENT_BUILDER](../CoreAI/Docs/AGENT_BUILDER.md) · [COREAI_SINGLETON_API](Docs/COREAI_SINGLETON_API.md) ·
[QUICK_START](Docs/QUICK_START.md)

---

## What this package adds on top of the core

| Area | What you get |
|---|---|
| **Composition** | `CoreAILifetimeScope` (VContainer), `CoreServicesInstaller`, MessagePipe brokers, `link.xml` for IL2CPP |
| **Static facade** | `CoreAi.AskAsync` / `StreamAsync` / `StreamChunksAsync` / `OrchestrateAsync`, tool-call events, `AddSkillForRole` |
| **Chat UI** | `CoreAiChatPanel` (UI Toolkit), `CoreAiChatService`, typing indicator, cancel, error presentation split between player and log |
| **Providers** | `MeaiLlmClient` (streaming tool loop), `LlmUnityMeaiChatClient` (on-device GGUF), routing/timeout/retry decorators |
| **WebGL** | `FetchSseOpenAiTransport` + `CoreAiSseFetch.jslib` for real incremental SSE, `UnityWebRequestOpenAiTransport` fallback, `UnityMainThreadLlmAsyncMarshaler` |
| **Persistence** | `FileAgentMemoryStore`, `FileConversationSummaryStore`, `FileSkillStore`, `persistentDataPath`; on WebGL the engine persists (`config.autoSyncPersistentDataPath = true`) and `CoreAiWebGlPersistence` only reports whether it is armed |
| **Unity-only tools** | `world_command` (`WorldLlmTool`), `component_command` (`ComponentLlmTool`), `scene_tool` (`SceneLlmTool`), `camera` (`CameraLlmTool`) |
| **Editor** | `CoreAI → Setup → …` scene/asset wizards, Settings window, Agent Session Inspector, production-settings validation |

Tools that are **not** in this package: `memory`, `game_config`, `game_state`, `get_inventory`,
`read_skill`, `call_skill_tool`, `manage_skills`, `wait` — those are in the engine-free core.
`execute_lua` / `manage_mods` are in `com.neoxider.coreaimods`.

---

## Optional modules

Base install is core + this package. Everything else is independent and additive:

| Package | Adds | If absent |
|---|---|---|
| `com.neoxider.coreaimods` | Lua sandbox, `execute_lua`, `manage_mods`, mod runtime, RbxApi | Nothing in the base references it; the project compiles unchanged |
| `com.neoxider.coreaihub` | Tabbed Hub window (Chat / Settings / Statistics / Mods) | `HubPageRegistry` still exists in the core; the Mods↔Hub bridge assembly compiles out via `defineConstraints: ["COREAI_HAS_HUB"]` — silently, so check the Hub page is really gone before filing a bug |
| `com.neoxider.coreaimcp` | [In-game MCP server](../CoreAIMcp/README.md) | Leaf package — nothing depends on it |
| `com.neoxider.coreaimirror` | Mirror networking transport | Assemblies carry `defineConstraints: ["MIRROR"]`; without Mirror installed they never build |
| `com.neoxider.coreaibenchmark` | PlayMode game-creation benchmark (G1–G8) | Only the benchmark scenarios disappear; the report types ship inside the core |
| `ai.undream.llm` (LLMUnity) | On-device GGUF inference | `COREAI_HAS_LLMUNITY` stays undefined; the HTTP paths are unaffected |

Two project-level scripting symbols, **both opt-in and absent by default**: `COREAI_LLM` (provider-backed
HTTP/MEAI/LLMUnity execution) and `COREAI_LUA`. Toggle them from `CoreAI → Setup → Modules`. CI builds
all four combinations (`core` / `llm` / `lua` / `full`).

---

## Properties worth knowing about (each verifiable)

**Tool calls execute while the model is still generating.** `MeaiLlmClient.CompleteStreamingAsync`
hands each native `FunctionCallContent` to `ToolExecutionPolicy.ExecuteStreamedAsync` as its deltas
arrive; the turn does not have to finish first. Proof:
`MeaiStreamingToolCallEditModeTests.CompleteStreamingAsync_NativeToolCallMidStream_ExecutesBeforeStreamEnds`.
Text-shaped tool calls from small local models are extracted at the end of each roundtrip instead.

**Streaming is the default execution path, not a chat-only feature.** With
`ICoreAISettings.EnableStreaming` on, `AiOrchestrator.RunTaskAsync` also runs through
`CompleteStreamingAsync` and collapses the stream back into an `LlmCompletionResult`, so headless task
execution uses the same tool loop as the chat panel. See [ARCHITECTURE](Docs/ARCHITECTURE.md).

**Tool calls run in parallel, bounded, with mutations serialized.**
`ICoreAISettings.MaxParallelToolCalls` (default 4; `1` = sequential). Mutating built-ins (`memory`,
`manage_mods`, `manage_skills`, `world_command`, `component_command`, `execute_lua`,
`call_skill_tool`) are serialized against each other and result order is preserved. Proof:
`CompleteStreamingAsync_TwoNativeToolCallsWithParallelLimit_OverlapAndResultsInCallOrder`.

**Primitives that are dead in the browser cannot creep back in.** `Task.Run`, `Task.Delay`,
`CancelAfter`, `RunContinuationsAsynchronously` and `ConfigureAwait(false)` are rejected in
WebGL-reachable runtime code by a source-scanning guard, and delays/timeouts go through
`ILlmAsyncMarshaler` → `UniTask` player-loop timing instead. Proof:
`WebGlUnsafeAsyncPrimitivesEditModeTests` (including `Allowlist_HasNoStaleEntries`),
`CoreAiWebGlAsyncGuardEditModeTests`, plus the single `CAIU001` Roslyn analyzer shipped in
[`RoslynAnalyzers/`](RoslynAnalyzers) and gated by the CI job `analyzer`. The guard holds a frozen
allowlist of inherited exceptions, so it stops *new* violations in clean files rather than certifying
the whole tree. What this does **not** claim: see Limits below.

**A typed completion receipt, not a string you have to guess about.**
`CoreAiChatPanel.SubmitMessageFromExternalResultAsync` returns `CoreAiChatExternalSubmitResult`, which
separates *admitted* from *completed*: `Admitted == false` carries a `Rejection` and implies no model
turn; `Admitted == true` owns its `TurnGeneration` even if the panel is later disabled; `Completion`
exposes `Ok`, `ErrorCode`, HTTP/retry hints, token usage and executed tool receipts. Partial streamed
text can be visible while `Ok` is false — a non-empty string is never proof of success. Headless
equivalents: `IAiTaskResultService.RunTaskResultAsync`, `CoreAiChatService.SendMessageResultAsync`
(check `SupportsTaskResults` first; decorators throw `NotSupportedException` rather than fake a
receipt).

**Memory is isolated per tenant / user / session / topic.** Assign an
`AgentMemoryScopeProviderBehaviour` on `CoreAILifetimeScope`, or call
`SetAgentMemoryScopeProvider(...)` before the container is built. Keys are opaque full SHA-256, so no
user identifier reaches a filename or an error log. Both setters throw after build on purpose. Proof:
`ScopedAgentMemoryStoreDecoratorEditModeTests`, `AgentMemoryActorScopeKeyEditModeTests`,
`CoreAILifetimeScopeConversationStoreEditModeTests`. Details: [ARCHITECTURE](Docs/ARCHITECTURE.md)
§Runtime Context And Memory Scope.

---

## Limits — read before you plan around them

- **"Never blocks the frame" is not a guarantee this package makes.** The async guards above prevent
  browser-dead primitives and keep continuations on the main thread; they do **not** ban `.Result`,
  `.Wait()` or `GetAwaiter().GetResult()`, and no test measures frame time. `FileAgentMemoryStore`
  still takes synchronous `SemaphoreSlim` waits on its sync API. `CAIU001` ships as a *warning* with
  recorded debt in [KNOWN_ISSUES](Docs/KNOWN_ISSUES.md). Use `await`; do not block on CoreAI tasks
  from the main thread.
- **`LocalModel` (LLMUnity) does not work on WebGL.** Browser builds use `ServerManagedApi` for
  production, or `ClientOwnedApi` only where key exposure is acceptable.
- **A WebGL build stops unless the web template arms browser storage.**
  `config.autoSyncPersistentDataPath = true` in `createUnityInstance()` is the only channel that
  carries `Application.persistentDataPath` into IndexedDB, and Unity's stock templates ship that line
  commented out. Add the line to your own template, or run **`CoreAI → Setup → Install WebGL
  Template`** — it copies the template shipped inside this package into `Assets/WebGLTemplates/CoreAI`
  and selects it. A player that deliberately ships without CoreAI's persistent storage opts out with
  the scripting define symbol **`COREAI_WEBGL_NO_PERSISTENCE`** on the Web platform; the refusal is
  logged on every build.
- **Cross-origin endpoints need CORS**, and a non-empty `API Key` on a `CoreAISettings` asset under
  `Resources/` aborts the build on purpose — inject keys at runtime.
- **Model quality is your problem, not the framework's.** Deterministic EditMode suites use stubs;
  live PlayMode suites need a configured backend and are stochastic. Re-run the scenarios your game
  depends on against your own model before shipping.
- **Authority rejection and invalid structured output currently share `LlmErrorCode.InvalidRequest`.**
  Present a general failure status rather than branching on diagnostic wording.
- **Do not automatically replay an admitted turn.** It may already have produced text or executed
  tools.

---

## Tests

```text
Unity → Window → General → Test Runner
  ├── EditMode  — large deterministic suite, no real LLM (streaming, tools, skills, memory scopes,
  │               Lua, rate limits, WebGL async guards, architecture fitness)
  └── PlayMode  — needs a backend (HTTP env vars or a local GGUF)
       ├── FastNoLlm/       — stubs: skill pipeline, catalog injection, meta-tool registration
       └── LlmVerification/ — real model: tool discovery, resilience, benchmark
```

The engine-free subset also runs without Unity at all —
`dotnet test tools/portable/Tests/CoreAI.Portable.Tests.csproj -c Release` executed **1,317 cases with
0 failures** on 2026-09-09, and runs on every push as CI job `portable-core`. EditMode counts are
profile-dependent (`COREAI_LLM` / `COREAI_LUA` / Hub change which assemblies compile), so compare the
number of executed cases between runs, not just the colour.

Requirements for writing tests here: [Tests/README.md](Tests/README.md) and
[ARCHITECTURE](Docs/ARCHITECTURE.md) §Test Integrity Rule.

---

## Documentation

| Level | Documents |
|---|---|
| Start | [QUICK_START](Docs/QUICK_START.md) · [QUICK_START_FULL](Docs/QUICK_START_FULL.md) · [COREAI_SINGLETON_API](Docs/COREAI_SINGLETON_API.md) · [COREAI_SETTINGS](Docs/COREAI_SETTINGS.md) · [EXAMPLES](Docs/EXAMPLES.md) |
| Chat & streaming | [README_CHAT](Runtime/Source/Features/Chat/README_CHAT.md) · [STREAMING_ARCHITECTURE](Docs/STREAMING_ARCHITECTURE.md) · [HTTP_TRANSPORT_SPEC](Docs/HTTP_TRANSPORT_SPEC.md) |
| Agents, tools, memory | [AGENT_BUILDER](../CoreAI/Docs/AGENT_BUILDER.md) · [TOOL_CALL_SPEC](Docs/TOOL_CALL_SPEC.md) · [MemorySystem](Docs/MemorySystem.md) · [AI_AGENT_ROLES](Docs/AI_AGENT_ROLES.md) |
| Architecture | [ARCHITECTURE](Docs/ARCHITECTURE.md) · [DEVELOPER_GUIDE](Docs/DEVELOPER_GUIDE.md) · [DGF_SPEC](Docs/DGF_SPEC.md) · [MULTIPLAYER_AI](Docs/MULTIPLAYER_AI.md) |
| When it breaks | [TROUBLESHOOTING](Docs/TROUBLESHOOTING.md) · [KNOWN_ISSUES](Docs/KNOWN_ISSUES.md) · [WEBGL_BUILD_TROUBLESHOOTING](Docs/WEBGL_BUILD_TROUBLESHOOTING.md) |

Full map: [DOCS_INDEX](Docs/DOCS_INDEX.md). Release notes: [CHANGELOG.md](CHANGELOG.md) — versions live
in `package.json`, not in this file.

---

Author: [Neoxider](https://github.com/NeoXider) · License:
[PolyForm Noncommercial 1.0](../../LICENSE) · Commercial licensing: neoxider@gmail.com
