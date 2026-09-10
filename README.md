<p align="center">
  <img src="Images/header_concept_2.png" alt="CoreAI Banner" width="100%">
</p>

# <img src="Docs/Images/coreai_icon.png" alt="CoreAI Icon" width="40" height="40" align="absmiddle"> CoreAI

**A C# agent runtime you embed in your own application.** Roles and prompts, C# tools the model may
call, multi-document skills, scoped memory, provider routing, and a streaming turn loop that executes
tool calls while the model is still generating.

Games are the first-class target — NPCs that call your inventory code, an AI teacher, agents that
change a running world. The core is engine-free, so the same runtime also builds as a plain
`netstandard2.1` DLL for a CLI, a service, or any .NET host.

[![CI](https://github.com/NeoXider/CoreAI/actions/workflows/ci.yml/badge.svg)](https://github.com/NeoXider/CoreAI/actions/workflows/ci.yml)
[![Unity](https://img.shields.io/badge/Unity-6000.0%2B-black)](https://unity.com/releases/editor)
[![Runs on](https://img.shields.io/badge/runs%20on-local%204B%20GGUF%20or%20any%20OpenAI--compatible%20API-blue)](#recommended-models)
[![License](https://img.shields.io/badge/license-PolyForm%20Noncommercial%201.0-blue)](LICENSE)

<sub>
<a href="#who-this-is-for">Who it's for</a>&nbsp;&nbsp;•&nbsp;
<a href="#quick-start">Quick start</a>&nbsp;&nbsp;•&nbsp;
<a href="#what-you-get">What you get</a>&nbsp;&nbsp;•&nbsp;
<a href="#packages-and-what-is-optional">Packages</a>&nbsp;&nbsp;•&nbsp;
<a href="#embedding-as-a-dll">DLL</a>&nbsp;&nbsp;•&nbsp;
<a href="#what-is-actually-different-here">What's different</a>&nbsp;&nbsp;•&nbsp;
<a href="#limits-and-non-goals">Limits</a>&nbsp;&nbsp;•&nbsp;
<a href="#game-creation-benchmark">Benchmark</a>&nbsp;&nbsp;•&nbsp;
<a href="#documentation">Docs</a>&nbsp;&nbsp;•&nbsp;
<a href="Docs/ROADMAP.md">Roadmap</a>
</sub>

---

## Who this is for

| You are building | Install | First result |
|---|---|---|
| **A Unity game** — NPC dialogue, an in-game teacher, agents that edit the world | `com.neoxider.coreai` + `com.neoxider.coreaiunity` | `CoreAI → Setup → Create Chat Demo Scene` → Play → type |
| **A plain .NET app** — CLI, desktop, worker, service | reference [`tools/portable/CoreAI.Core.csproj`](tools/portable/README.md) | `dotnet run --project examples/dotnet -- "…"` |
| **A server-side agent** | the same DLL | you own hosting, credentials, storage, concurrency |

You already know what an LLM API returns. What CoreAI supplies is the layer around it: which tools this
role may call, what happens when the model gets a tool name wrong, what a 60-turn conversation costs,
where a specific player's memory lives, and what a caller learns when the turn fails. That layer is the
product; your game logic stays yours.

---

## Quick start

### Unity (about five minutes)

1. **Install** via Package Manager → *Add package from Git URL* — core first:
   ```text
   https://github.com/NeoXider/CoreAI.git?path=Assets/CoreAI
   https://github.com/NeoXider/CoreAI.git?path=Assets/CoreAiUnity
   ```
2. **Dependencies:** `Microsoft.Extensions.AI` through [NuGetForUnity](https://github.com/GlitchEnzo/NuGetForUnity),
   then `CoreAI → Setup → Install Git Dependencies` (VContainer / MessagePipe / UniTask). Full
   walkthrough, including the manual-DLL path: **[INSTALL.md](INSTALL.md)**.
3. **Scene:** `CoreAI → Setup → Create Chat Demo Scene`.
4. **Backend:** `CoreAI → Settings` — a local GGUF via LLMUnity, or any OpenAI-compatible endpoint
   (LM Studio, Ollama, vLLM, a hosted provider, or your own proxy).
5. **Press Play and type.**

Then, from any script:

```csharp
using CoreAI;
using CoreAI.Ai;

string reply = await CoreAi.AskAsync("Hello!");

await foreach (string chunk in CoreAi.StreamAsync("Tell a story", "SmartChat"))
{
    label.text += chunk;
}

Dictionary<string, int> stock = new() { ["fire sword"] = 0, ["iron sword"] = 3 };

AgentConfig blacksmith = new AgentBuilder("Blacksmith")
    .WithSystemPrompt("You are a blacksmith. Sell weapons and remember purchases.")
    .WithTool(new DelegateLlmTool("stock_of", "How many of an item are in stock.",
        (string item) => stock.TryGetValue(item, out int count) ? count.ToString() : "0"))
    .WithMemory()
    .WithMode(AgentMode.ToolsAndChat)
    .Build();          // registers the role with the live policy once the scope is built

string answer = await CoreAi.AskAsync("Got any fire swords?", "Blacksmith");
```

More: [Unity package README](Assets/CoreAiUnity/README.md) · [QUICK_START](Assets/CoreAiUnity/Docs/QUICK_START.md) ·
[COREAI_SINGLETON_API](Assets/CoreAiUnity/Docs/COREAI_SINGLETON_API.md)

### Plain .NET (no Unity)

```powershell
dotnet build tools/portable/CoreAI.Core.csproj -c Release
dotnet run --project examples/dotnet/CoreAI.ConsoleSample.csproj -- --help
```

A compiling end-to-end example — one C# tool, a two-document skill, a real HTTP client — is
[`examples/dotnet/Program.cs`](examples/dotnet/Program.cs); the same code is reproduced and explained in
the [core package README](Assets/CoreAI/README.md#quick-start).

---

## What you get

**Agents.** `AgentBuilder` → `AgentConfig` → `AgentMemoryPolicy`. Per-role system prompt, tools, mode
(`ToolsAndChat` / `ToolsOnly` / `ChatOnly`), temperature, output budget, roundtrip cap, streaming
override, LLM profile.

**Tools the model calls.** Implement `ILlmTool`, or wrap a delegate:
`new DelegateLlmTool("get_weather", "Current weather.", (string city) => …)`. `ToolExecutionPolicy`
adds per-tool timeouts, duplicate detection, retry-with-feedback, tool-name repair
(`MEMORY` → `memory`), a result-size cap, and bounded parallelism. Built-ins: `memory`, `game_config`,
`game_state`, `get_inventory`, `wait`, `read_skill`, `call_skill_tool`, `manage_skills` (core);
`world_command`, `component_command`, `scene_tool`, `camera` (Unity); `execute_lua`, `manage_mods`
(Mods).

**Skills.** A skill is a named group of tools plus instructions that may span several documents. The
model sees a one-line catalog entry per skill and two meta-tools, then pulls the entry document with
`read_skill`, and one reference section — or all of them — on a second call.

**Memory that stays separated.** `AgentMemoryScope(tenantId, userId, sessionId, topicId)` plus the role
id (and optionally an actor id) forms one canonical, opaque key shared by long-term memory, flat chat,
structured transcript and rolling summary.

**Long conversations that stay affordable.** Token-budgeted history, `## Conversation Summary` rollup,
optional LLM-assisted compaction, and bounded rebuild retries when a provider reports
`ContextLengthExceeded`.

**Provider routing.** `LocalModel` (LLMUnity GGUF), `ClientOwnedApi`, `ClientLimited`,
`ServerManagedApi` (your backend owns the keys), `Offline`. Multiple endpoints can stay registered at
once, with per-role assignment and automatic fallback.

**Streaming that survives real providers.** SSE chunking, `<think>` tags split across chunks,
fragmented tool-call arguments, mid-stream transport failure after a tool has already run.

**Production guardrails.** Request timeout, HTTP 429/5xx retry with `Retry-After`, pre-commit stream
retry, circuit breaker, runaway-output cap, Lua generation rate limit, an append-only SHA-256-chained
audit log, and a token-budget overlay.

---

## Packages and what is optional

Seven UPM packages, versions kept in lockstep (CI job `package-graph` fails the build if they drift).
**Only the first two are required.**

| Package | What's inside | Depends on |
|---|---|---|
| **[com.neoxider.coreai](Assets/CoreAI)** | Engine-free core: agents, tools, skills, memory, orchestration, OpenAI-compatible client | — |
| **[com.neoxider.coreaiunity](Assets/CoreAiUnity)** | Unity host: DI, chat UI, streaming clients, WebGL transports, persistence, Editor menus | `coreai` |
| **[com.neoxider.coreaimods](Assets/CoreAIMods)** | Lua sandbox (Lua-CSharp, bundled), `execute_lua` / `manage_mods`, mod runtime, Roblox-style world API | `coreai` + `coreaiunity` |
| **[com.neoxider.coreaihub](Assets/CoreAIHub)** | Tabbed UI Toolkit Hub window | `coreai` + `coreaiunity` |
| **[com.neoxider.coreaimcp](Assets/CoreAIMcp)** | In-game [MCP server](Assets/CoreAIMcp/README.md) — an external agent drives the running game | `coreaiunity` + `coreaimods` |
| **[com.neoxider.coreaimirror](Assets/CoreAIMirror)** | Mirror networking transport for online worlds | `coreai` + `coreaimods` + Mirror |
| **[com.neoxider.coreaibenchmark](Assets/CoreAIBenchmark)** | Dev/test-only game-creation benchmark harness | `coreai` + `coreaiunity` + `coreaimods` |

What happens when an optional package is **absent** — this is the point of the split:

- **Mods** — nothing in the base references it; the project compiles unchanged, minus Lua. MCP,
  Benchmark and Mirror depend on it and go with it.
- **Hub** — `HubPageRegistry` still lives in the core, so page registration survives and you render
  pages yourself. The Mods↔Hub bridge assembly compiles out through
  `defineConstraints: ["COREAI_HAS_HUB"]` — **silently**, so verify the page is really gone before
  filing a bug.
- **MCP** — a leaf. Nothing references it.
- **Mirror** — its assemblies carry `defineConstraints: ["MIRROR"]`, so a project without Mirror never
  builds them at all.
- **Benchmark** — only the PlayMode scenarios disappear; the report types ship inside the core.

Two project-level scripting symbols gate provider and Lua code, **both opt-in and absent by default**:
`COREAI_LLM` (provider-backed HTTP/MEAI/LLMUnity execution) and `COREAI_LUA`. Toggle them from
`CoreAI → Setup → Modules`; `tools/check_positive_module_opt_in.py` enforces the positive-opt-in
contract, and CI builds all four legs (`core` / `llm` / `lua` / `full`).

**Install profiles:** Base = `coreai` + `coreaiunity`; +Mods; +Hub; Full = Base + Mods + Hub
(+ Benchmark for model evaluation, + MCP for external agents, + Mirror for online worlds).
Step by step: **[INSTALL.md](INSTALL.md)**.

---

## Embedding as a DLL

The core has **no Unity dependency at all** — its assembly definition declares
`noEngineReferences: true` with an empty `references` list, and no source file under
`Assets/CoreAI/Runtime/Core` references `UnityEngine` or `UnityEditor`.

```powershell
dotnet build tools/portable/CoreAI.Core.csproj -c Release
# → tools/portable/bin/Release/netstandard2.1/CoreAI.Core.dll
```

The project compiles `Assets/CoreAI/Runtime/Core/**/*.cs` with no exclusions and references no Unity
assembly, so a stray `using UnityEngine;` in the core breaks it — which is why it is the **first** CI
job. Wiring, dependency pinning, and what your app must supply:
**[tools/portable/README.md](tools/portable/README.md)**.

In Unity, install the UPM packages instead. Adding the DLL on top of the sources produces a second
`CoreAI.Core` assembly.

---

## What is actually different here

Every line below names the type and the test that backs it. Anything that could not be pointed at is
not on this list.

**Tool calls run while the model is still generating.** On the native tool-call channel,
`MeaiLlmClient.CompleteStreamingAsync` hands each `FunctionCallContent` to
`ToolExecutionPolicy.ExecuteStreamedAsync` as its deltas arrive — the turn does not have to assemble
first, and the client is written to survive a transport failure that happens *after* a tool has
already executed.
→ `MeaiStreamingToolCallEditModeTests.CompleteStreamingAsync_NativeToolCallMidStream_ExecutesBeforeStreamEnds`,
`…_StreamThrowsAfterExecutedToolCall_YieldsTerminalErrorChunkWithTraces`.
*Caveat:* text-shaped tool calls — a small local model emitting JSON in prose — are extracted and run
at the end of each roundtrip instead.

**Streaming is the default execution path, not a chat-only feature.** Headless task execution
(`AiOrchestrator.RunTaskAsync`) runs through the same streamed tool loop and collapses the stream back
into an `LlmCompletionResult`. → [ARCHITECTURE](Assets/CoreAiUnity/Docs/ARCHITECTURE.md) §Streaming Is
The Default Execution Path.

**Tool calls execute in parallel, with mutations serialized.** Bounded by
`ICoreAISettings.MaxParallelToolCalls` (default 4; `1` = strictly sequential); mutating built-ins are
serialized against each other and result order is preserved.
→ `MeaiStreamingToolCallEditModeTests.CompleteStreamingAsync_TwoNativeToolCallsWithParallelLimit_OverlapAndResultsInCallOrder`,
`ToolExecutionPolicyEditModeTests`.

**Skills span several documents and are disclosed in stages.** `SkillSet.FromFiles` /
`FromTextParts` keep every document addressable as a `SkillSection`; `read_skill` returns the entry
document plus an index, and a second call fetches one section or `all`. A Unity `SkillSetAsset` accepts
a primary `TextAsset` plus references.
→ `SkillSectionDisclosureEditModeTests` (15 cases, including
`ReadSkill_StagedAnswer_IsSmallerThanTheWholeSkill`), `SkillSetEditModeTests`,
`SkillSetAssetInstructionsEditModeTests`.

**Memory has real isolation boundaries, and identifiers never reach disk in the clear.** Tenant / user
/ session / topic + role (+ optional actor) collapse into one canonical key used by memory, chat,
transcript and summary alike; keys are opaque full SHA-256, and a case-only difference in a user id
produces a different key rather than a Windows filename collision.
→ `ScopedAgentMemoryStoreDecoratorEditModeTests` (15 cases, including
`ScopedKeys_LowercaseGuid_IsNotPersistedInPlaintext`, `FilePersistence_CaseOnlyUsers_CreateDistinctOpaqueFiles`),
`AgentMemoryActorScopeKeyEditModeTests`.

**The MCP catalog changes on a live server without a restart.** `McpToolRegistry.AddOrReplace` /
`Remove` / `Replace` publish atomically, bump a revision, and push
`notifications/tools/list_changed` to connected sessions. A tool swapped between admission and
execution cannot receive the previous call's arguments — the plan pins the binding and a copy of the
arguments.
→ `CoreAiMcpServerResidencyPlayModeTests.LiveCatalog_AddRemoveReplace_OverOneRunningHttpServerAndSession`
(one real running server, real loopback HTTP, one session throughout),
`McpToolRegistryEditModeTests.AdmittedInvocation_AfterReplacement_UsesOnlyCapturedBodyAndArguments`.

**The engine-free claim is a CI gate, not a statement.**
`dotnet test tools/portable/Tests/CoreAI.Portable.Tests.csproj -c Release` runs the existing EditMode
fixtures against the real `netstandard2.1` DLL with no Unity present — **1,317 cases, 0 failures,
21 s** on 2026-09-09 — and it is the first job on every push. Re-run it yourself; it is a command, not
a badge.

**Primitives that are dead in a browser cannot creep back in.** `Task.Run`, `Task.Delay`,
`CancelAfter`, `RunContinuationsAsynchronously` and `ConfigureAwait(false)` are rejected in
WebGL-reachable runtime code by a source-scanning guard; delays and timeouts go through
`ILlmAsyncMarshaler` → UniTask player-loop timing instead.
→ `WebGlUnsafeAsyncPrimitivesEditModeTests`, `CoreAiWebGlAsyncGuardEditModeTests`, and the single
`CAIU001` Roslyn analyzer gated by the CI job `analyzer`. The guard carries a frozen allowlist of
inherited exceptions and its own test (`Allowlist_HasNoStaleEntries`) keeps that list honest — it
prevents *new* violations in clean files rather than certifying the whole tree. Read the matching entry
under [Limits](#limits-and-non-goals) before you round this up to "never blocks the frame".

**A failed turn tells the caller it failed.** `CoreAiChatPanel.SubmitMessageFromExternalResultAsync`
returns a `CoreAiChatExternalSubmitResult` that separates admission from completion and carries
`Ok`, `ErrorCode`, HTTP/retry hints, token usage and executed-tool receipts. Partial streamed text can
be visible while `Ok` is false — a non-empty string is never treated as proof of success.
→ [Unity README](Assets/CoreAiUnity/README.md) §typed completion receipt.

---

## Limits and non-goals

- **"Never blocks the frame" is not a claim we make.** The guards above prevent browser-dead primitives
  and keep continuations on the main thread. They do **not** ban `.Result`, `.Wait()` or
  `GetAwaiter().GetResult()`, no test measures frame time, `FileAgentMemoryStore` still takes
  synchronous `SemaphoreSlim` waits on its sync API, and `CAIU001` ships as a *warning* with recorded
  debt in [KNOWN_ISSUES](Assets/CoreAiUnity/Docs/KNOWN_ISSUES.md). Use `await`; do not block on CoreAI
  tasks from the main thread.
- **No MCP client.** `com.neoxider.coreaimcp` is a server: an external agent drives the game. There is
  no outbound connector, so an in-game agent does not consume external MCP servers — if you want that
  bridge, you write it. The server is also HTTP-loopback only, and unavailable in a WebGL player.
- **No provider abstraction beyond OpenAI-compatible HTTP.** Anything else needs your own `IChatClient`
  or a proxy in front.
- **`SmartToolCallingChatClient` does not run the tool loop in streaming mode** — it says so in its own
  log line. The execute-as-you-stream path is the Unity `MeaiLlmClient`.
- **Local models on WebGL are out.** Browser builds use `ServerManagedApi` in production, or
  `ClientOwnedApi` only where key exposure is acceptable. Cross-origin endpoints need CORS.
- **`netstandard2.1` is not .NET Framework**, and compiling is not proof for NativeAOT, trimming or
  IL2CPP — each needs its own check of type preservation and tool binding.
- **Model quality is yours to verify.** Deterministic suites use stubs; live suites are stochastic and
  need a configured backend. Re-run the scenarios your game depends on against your own model.
- **We do not claim to beat other agent harnesses.** No comparative benchmark of developer experience
  exists here. The [game-creation benchmark](#game-creation-benchmark) scores *models*, not harnesses.

---

## Game-Creation Benchmark

CoreAI ships a benchmark that scores how well an LLM builds and changes a game by driving real
`execute_lua` and `world_command` tools — 0–100 across eight scenario groups (**suite v1.7**, benchmark
v2 prompts, G1–G8), against any OpenAI-compatible endpoint.

<img src="Docs/Images/benchmark_v2_frontier.png" alt="CoreAI Game-Creation Benchmark v2 — top-tier frontier-model comparison ranked by suite score" width="900">

| # | Model | Suite | Pass-rate |
|---:|---|---:|---:|
| 1 | `gpt-5.6-sol` | **96.6** | 85.7% |
| 2 | `gpt-5.6-terra` | **93.0** | 86.2% |
| 3 | `gpt-5.3-spark` | **92.9** | 79.3% |
| 4 | `gpt-5.5` | **90.3** | 82.8% |
| 5 | `gpt-5.6-luna` | **88.1** | 79.3% |
| 6 | `claude-sonnet-5` | **86.2** | 75.9% |
| 7 | `claude-opus-4.8` | **83.2** | 79.3% |
| 8 | `claude-fable-5` | **81.4** | 75.9% |

One run each (2026-07-11) via the `cli-agents` bridge — indicative, not a ranked submission.

> ⚠️ **Claude scores are understated.** Those three ran through a non-native, unstable API with a high
> tool-failure rate (opus ~24%, sonnet ~23%, fable ~36% vs 1–13% for the Codex models): well-formed
> tool calls that still failed to land. Read them as a lower bound.

Free models through the opencode native API (`opencode serve`, real token streaming):
`hy3-free` **86.9** / 72.4% (full G1–G8); `deepseek-v4-flash-free` 87.2 / 75.0% (partial — the free
tier's rate limit ended the run mid-suite). Rate-limited and meant for use inside opencode, so treat
them as "can a free model build a game?" checks.

Full tables, methodology, group legend and per-model castle prefabs:
**[benchmark guide](Assets/CoreAIBenchmark/README.md)** · **[leaderboard](Docs/BENCHMARK_LEADERBOARD.md)**.

---

## Recommended models

| Model | Size | Tool calling | When |
|---|---|---|---|
| **Qwen3.5-4B** | 4B | Reliable | Recommended local GGUF |
| **Qwen3.5-35B (MoE)** via API | 35B/3A | Reliable | ~4B speed with 35B quality |
| **Gemma 4 26B** (LM Studio) | 26B | Reliable | Strong over HTTP |
| Qwen3.5-2B | 2B | Workable | Occasional multi-step mistakes |
| Qwen3.5-0.8B | 0.8B | Basic | Struggles with multi-step chains |

Recorded live PlayMode runs pass on Qwen3.5-4B for memory, custom agents, world commands, Lua,
multi-agent workflows, chat history and NPC dialogue. Results vary by build, quantization and provider
— reproduce the cases your game depends on. Setup: [LLMUNITY_SETUP_AND_MODELS](Assets/CoreAiUnity/Docs/LLMUNITY_SETUP_AND_MODELS.md).

---

## Tests

```text
Unity → Window → General → Test Runner
  ├── EditMode  — large deterministic suite, no real LLM: prompts, streaming, tools, skills,
  │               memory scopes, Lua sandbox, rate limits, WebGL async guards, architecture fitness
  └── PlayMode  — needs a configured HTTP or local GGUF backend
       ├── FastNoLlm/       — stubs: skill pipeline, catalog injection, meta-tool registration
       └── LlmVerification/ — real model: tool discovery, resilience, benchmark
```

Without Unity at all:

```powershell
dotnet test tools/portable/Tests/CoreAI.Portable.Tests.csproj -c Release
```

**1,317 cases, 0 failures** on 2026-09-09. The full Unity EditMode suite is much larger (~4,200
executed cases in the full profile) but **profile-dependent** — `COREAI_LLM`, `COREAI_LUA` and Hub
change which assemblies compile at all. Compare the *number of executed cases* between runs, not just
the colour: a suite that failed to compile also reports green.

Test-writing rules: [ARCHITECTURE §Test Integrity Rule](Assets/CoreAiUnity/Docs/ARCHITECTURE.md) and
[Tests/README.md](Assets/CoreAiUnity/Tests/README.md).

---

## Documentation

All documentation is English. Entry points: **[Docs/README.md](Docs/README.md)** (repository) and
**[DOCS_INDEX.md](Assets/CoreAiUnity/Docs/DOCS_INDEX.md)** (Unity package map).

| Goal | Document |
|---|---|
| Install, per profile | [INSTALL.md](INSTALL.md) |
| First scene, first agent | [QUICK_START](Assets/CoreAiUnity/Docs/QUICK_START.md) · [QUICK_START_FULL](Assets/CoreAiUnity/Docs/QUICK_START_FULL.md) |
| One-liners from any script | [COREAI_SINGLETON_API](Assets/CoreAiUnity/Docs/COREAI_SINGLETON_API.md) |
| Build agents, tools, skills | [AGENT_BUILDER](Assets/CoreAI/Docs/AGENT_BUILDER.md) · [TOOL_CALLING_BEST_PRACTICES](Assets/CoreAI/Docs/TOOL_CALLING_BEST_PRACTICES.md) |
| Chat UI and streaming | [README_CHAT](Assets/CoreAiUnity/Runtime/Source/Features/Chat/README_CHAT.md) · [STREAMING_ARCHITECTURE](Assets/CoreAiUnity/Docs/STREAMING_ARCHITECTURE.md) |
| Memory, context budget, compaction | [MemorySystem](Assets/CoreAiUnity/Docs/MemorySystem.md) · [ARCHITECTURE](Assets/CoreAiUnity/Docs/ARCHITECTURE.md) |
| Endpoints, profiles, per-role routing | [LLM_ROUTING](Assets/CoreAI/Docs/LLM_ROUTING.md) · [RUNTIME_BACKEND_SWITCHING](Assets/CoreAiUnity/Docs/RUNTIME_BACKEND_SWITCHING.md) |
| Lua modding and its sandbox | [LUA_GAME_API](Assets/CoreAI/Docs/LUA_GAME_API.md) · [LUA_SANDBOX_SECURITY](Assets/CoreAI/Docs/LUA_SANDBOX_SECURITY.md) · [RBX_API](Assets/CoreAI/Docs/RBX_API.md) |
| Drive a running game from Claude Code | [CoreAI MCP](Assets/CoreAIMcp/README.md) |
| Embed as a DLL | [tools/portable](tools/portable/README.md) · [.NET sample](examples/dotnet/README.md) |
| Codebase map, PR checklist | [DEVELOPER_GUIDE](Assets/CoreAiUnity/Docs/DEVELOPER_GUIDE.md) · [DGF_SPEC](Assets/CoreAiUnity/Docs/DGF_SPEC.md) |
| When it breaks | [TROUBLESHOOTING](Assets/CoreAiUnity/Docs/TROUBLESHOOTING.md) · [KNOWN_ISSUES](Assets/CoreAiUnity/Docs/KNOWN_ISSUES.md) |
| Where this is going | [ROADMAP](Docs/ROADMAP.md) |

**Reset CoreAI file persistence (Editor):** `CoreAI → Delete All Persistent Saves...` (exit Play Mode
first) deletes `persistentDataPath/CoreAI` — agent memory, persisted chat, summaries, Lua version
files. Assets under `Assets/` are untouched.

---

## Changelog and versions

Release notes live only in the two changelogs — [core](Assets/CoreAI/CHANGELOG.md) (core + mods) and
[Unity host](Assets/CoreAiUnity/CHANGELOG.md). The authoritative version of each package is the
`version` field in its `package.json`; all seven move together via `python tools/bump_version.py <version>`.

---

## Author and community

**Author:** [Neoxider](https://github.com/NeoXider) · **Ecosystem:**
[NeoxiderTools](https://github.com/NeoXider/NeoxiderTools) · **License:**
[PolyForm Noncommercial 1.0](LICENSE)

**Contact:** neoxider@gmail.com · [GitHub Issues](https://github.com/NeoXider/CoreAI/issues)

CoreAI is free for non-commercial use and developed in spare time. If it saves you hours:
star the repo, [sponsor on GitHub](https://github.com/sponsors/NeoXider), or write to
neoxider@gmail.com for a commercial licence, priority support, or custom integration.
