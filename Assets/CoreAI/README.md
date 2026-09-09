# CoreAI Core (`com.neoxider.coreai`)

An engine-free C# library that turns a chat model into an agent your application controls: roles and
prompts, C# tools the model may call, multi-document skills, scoped memory, provider routing, and a
streaming turn loop that executes tool calls while the model is still generating.

No `UnityEngine`. The assembly definition declares `noEngineReferences: true`, and the same sources
build as a plain `netstandard2.1` DLL through [`tools/portable`](../../tools/portable/README.md).

---

## Who this is for

| You are building | What you take | Notes |
|---|---|---|
| A **Unity game** — NPCs, an AI teacher, agents that change a running world | this package **+** [`com.neoxider.coreaiunity`](../CoreAiUnity/README.md) | The Unity layer adds DI, chat UI, persistence, LLMUnity, WebGL transports. |
| A **plain .NET application** — CLI, desktop, worker | this package **only**, as a DLL | See [`tools/portable`](../../tools/portable/README.md) and the [console sample](../../examples/dotnet/README.md). |
| A **server-side agent** | this package **only** | You own hosting, credentials, storage and concurrency; the library owns the turn. |

If your host is not Unity, nothing here pulls Unity in. If your host *is* Unity, this package is still
the part that holds the logic — the Unity layer is adapters around it.

---

## Quick start

**A plain .NET host, no Unity.** Reference the portable project and run one turn with a tool and a
two-document skill. Every type and signature below is the one used by the compiled
[console sample](../../examples/dotnet/Program.cs).

```xml
<ItemGroup>
  <ProjectReference Include="../CoreAI/tools/portable/CoreAI.Core.csproj" />
</ItemGroup>
```

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreAI;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using CoreAI.Logging;
using Microsoft.Extensions.AI;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

Dictionary<string, int> stock = new(StringComparer.OrdinalIgnoreCase) { ["iron"] = 12, ["wood"] = 30 };

DelegateLlmTool getStock = new("get_stock", "Read stock by item id: iron or wood.",
    (string item) => stock.TryGetValue(item, out int count) ? $"{item}: {count}" : "Unknown item.");

SkillSet inventory = SkillSet.FromTextParts("Inventory", "Read the application's supply stock.",
    new KeyValuePair<string, string>[]
    {
        new("SKILL.md", "Read references/items.md for item ids, then call get_stock."),
        new("references/items.md", "iron = pig iron bars; wood = timber planks.")
    }, getStock);

IReadOnlyList<SkillSet> skills = new[] { inventory };
ILlmTool[] tools = { ReadSkillLlmTool.Create(skills), CallSkillToolLlmTool.Create(skills) };

ChatOptions options = new()
{
    Tools = tools.Select(tool => (AITool)((IAIFunctionLlmTool)tool).CreateAIFunction()).ToList()
};
OpenAiHttpOptions http = new()
{
    ApiBaseUrl = Environment.GetEnvironmentVariable("COREAI_ENDPOINT"),
    ApiKey = Environment.GetEnvironmentVariable("COREAI_API_KEY") ?? "",
    Model = Environment.GetEnvironmentVariable("COREAI_MODEL"),
    RequestTimeoutSeconds = 60
};
CoreAISettingsOptions settings = new() { MaxToolCallRoundtrips = 8, DefaultToolTimeoutMs = 5000 };

using SmartToolCallingChatClient client = new(
    new MeaiOpenAiChatClient(http), NullLog.Instance, settings, false, tools, "Quartermaster");

ChatMessage[] messages =
{
    new(ChatRole.System, "You are a quartermaster. Read the relevant skill before calling a tool.\n"
        + SkillSet.BuildCatalog(skills)),
    new(ChatRole.User, "How much iron is in stock?")
};

ChatResponse response = await client.GetResponseAsync(messages, options, CancellationToken.None);
Console.WriteLine(response.Messages.Last(message => message.Role == ChatRole.Assistant).Text);
Console.WriteLine($"Tool calls executed: {client.LastExecutedToolCalls.Count}");
```

Point `COREAI_ENDPOINT` at any OpenAI-compatible API with native tool calling (LM Studio, Ollama,
vLLM, a hosted provider). Nothing selects a model or a provider for you.

Run the finished version:

```powershell
dotnet run --project examples/dotnet/CoreAI.ConsoleSample.csproj -- "How much iron is in stock?"
```

In Unity you normally do not assemble the pipeline by hand — see the
[Unity quick start](../CoreAiUnity/README.md#quick-start).

---

## What is in this package, and what is not

Always present in this assembly:

- **Agents** — `AgentBuilder` (fluent, ~26 options) → `AgentConfig` → `AgentMemoryPolicy`.
- **Tools** — `ILlmTool`, `DelegateLlmTool`, `MemoryTool`, `WaitLlmTool`, `ToolExecutionPolicy`
  (timeouts, duplicate detection, retry-with-feedback, tool-name repair, bounded parallelism).
- **Skills** — `SkillSet`, `SkillSection`, `read_skill`, `call_skill_tool`, `manage_skills`.
- **Memory and context** — `AgentMemoryScope`, the `Scoped*StoreDecorator` family,
  `DeterministicConversationContextManager`, `LlmAssistedConversationContextManager`,
  `IContextBudgetPolicy`, `IConversationSummaryStore`.
- **Orchestration** — `IAiOrchestrationService`, `AiOrchestrator`, `QueuedAiOrchestrator`.
- **LLM plumbing** — `MeaiOpenAiChatClient` (OpenAI-compatible HTTP + SSE),
  `SmartToolCallingChatClient`, routing/timeout/retry/circuit-breaker decorators, `ILlmEndpointRegistry`.
- **Audit contracts** — `IAuditLog`, `AuditEntry`, `AuditHash`.

Deliberately **not** here: Unity UI and DI, file-backed Unity stores, LLMUnity, the Lua sandbox, the
MCP server, the Mirror transport. Those ship as separate packages so the core stays engine-free.

| Optional package | Add it for | Without it |
|---|---|---|
| `com.neoxider.coreaiunity` | Unity host: DI, chat UI, persistence, WebGL transports | Core still works in any .NET host |
| `com.neoxider.coreaimods` | Lua sandbox, `execute_lua` / `manage_mods`, mod runtime | No Lua; nothing in the base references it |
| `com.neoxider.coreaihub` | Built-in Hub window | `HubPageRegistry` still lives in this package; you render pages yourself |
| `com.neoxider.coreaimcp` | In-game MCP server | No MCP endpoint; leaf package, nothing depends on it |
| `com.neoxider.coreaimirror` | Mirror networking transport | Assemblies carry `defineConstraints: ["MIRROR"]` and never build |
| `com.neoxider.coreaibenchmark` | PlayMode game-creation benchmark | Only the benchmark scenarios disappear |

Two project-level scripting symbols gate provider and Lua code paths, both **opt-in and absent by
default**: `COREAI_LLM` (provider-backed HTTP/MEAI/LLMUnity) and `COREAI_LUA`. The positive-opt-in
contract is enforced by `tools/check_positive_module_opt_in.py`, and CI builds a four-leg matrix
(`core` / `llm` / `lua` / `full`).

---

## Building it as a DLL

```powershell
dotnet build tools/portable/CoreAI.Core.csproj -c Release
```

Output: `tools/portable/bin/Release/netstandard2.1/CoreAI.Core.dll`. The project compiles
`Assets/CoreAI/Runtime/Core/**/*.cs` with no exclusions and no Unity reference — a stray
`using UnityEngine;` in the core breaks this build, which is the first job in CI.

Full instructions, dependency pinning and the "what your app must supply" list:
[`tools/portable/README.md`](../../tools/portable/README.md).

---

## Properties worth knowing about (each verifiable)

**Tool calls execute while the model is still generating.** On the native tool-call channel,
`MeaiLlmClient.CompleteStreamingAsync` hands each `FunctionCallContent` to
`ToolExecutionPolicy.ExecuteStreamedAsync` as its deltas arrive, rather than waiting for the turn to
assemble. Proof: `MeaiStreamingToolCallEditModeTests.CompleteStreamingAsync_NativeToolCallMidStream_ExecutesBeforeStreamEnds`.
Caveat: *text-shaped* tool calls (a small local model emitting JSON in prose) are extracted and run at
the end of each roundtrip, not mid-stream.

**Tool calls run in parallel, with mutations serialized.** Bounded by
`ICoreAISettings.MaxParallelToolCalls` (default 4; `1` = strictly sequential); mutating built-ins are
serialized against each other and result order is preserved. Proof:
`MeaiStreamingToolCallEditModeTests.CompleteStreamingAsync_TwoNativeToolCallsWithParallelLimit_OverlapAndResultsInCallOrder`,
`ToolExecutionPolicyEditModeTests`.

**Skills are multi-document, and disclosed in stages.** `SkillSet.FromTextParts` /
`SkillSet.FromFiles` keep each document addressable as a `SkillSection`; `read_skill` returns the entry
document plus an index of the rest, and a second call fetches one section or `all`. The model's schema
cost stays at two meta-tools plus a one-line catalog entry per skill regardless of how many tools a
skill holds. Proof: `SkillSectionDisclosureEditModeTests` (15 cases, including
`ReadSkill_StagedAnswer_IsSmallerThanTheWholeSkill`), `SkillSetEditModeTests`.

**Memory has real isolation boundaries.** `AgentMemoryScope(tenantId, userId, sessionId, topicId)`
combines with the role id — and optionally an actor id — into one canonical key used by memory, flat
chat, transcript and summary alike. Keys are opaque full SHA-256, so no user identifier lands in a
filename or an error log. Proof: `ScopedAgentMemoryStoreDecoratorEditModeTests` (including
`ScopedKeys_LowercaseGuid_IsNotPersistedInPlaintext`), `AgentMemoryActorScopeKeyEditModeTests`.

**The engine-free claim is a gate, not a wish.** `dotnet test tools/portable/Tests/CoreAI.Portable.Tests.csproj -c Release`
runs the existing EditMode fixtures against the real `netstandard2.1` DLL with no Unity present —
**1,317 cases, 0 failures** on 2026-09-09 — and runs on every push as the CI job `portable-core`.

---

## Limits — read before you plan around them

- **The core has no storage and no scheduler.** `FileConversationSummaryStore` and the Unity file
  stores live outside it. In a plain .NET host you supply persistence, or memory dies with the process.
- **`SmartToolCallingChatClient` does not run the tool loop in streaming mode.** Its
  `GetStreamingResponseAsync` passes tool calls through unexecuted and logs that it did. The
  execute-as-you-stream path is `MeaiLlmClient.CompleteStreamingAsync` (Unity layer). Use the
  non-streaming `GetResponseAsync` for a portable tool loop, as the console sample does.
- **`netstandard2.1` is not .NET Framework.** Classic .NET Framework does not implement it. NativeAOT,
  trimming and IL2CPP each need their own check of type preservation and tool binding — compiling is
  not proof.
- **No provider abstraction beyond OpenAI-compatible HTTP.** `MeaiOpenAiChatClient` speaks the
  OpenAI chat-completions shape. Other provider APIs need your own `IChatClient` or a proxy.
- **Nothing measures frame time.** Async hygiene for Unity/WebGL is enforced by the Unity layer's
  guards, not by this package.

---

## Where to go next

| Topic | Document |
|---|---|
| Guide index for this package | [Docs/README.md](Docs/README.md) |
| Building agents, tools, modes, recipes | [Docs/AGENT_BUILDER.md](Docs/AGENT_BUILDER.md) |
| How `ILlmTool` becomes an `AIFunction` | [Docs/MEAI_TOOL_CALLING.md](Docs/MEAI_TOOL_CALLING.md) |
| Endpoints, profiles, per-role routing | [Docs/LLM_ROUTING.md](Docs/LLM_ROUTING.md) |
| Writing tools that survive small models | [Docs/TOOL_CALLING_BEST_PRACTICES.md](Docs/TOOL_CALLING_BEST_PRACTICES.md) |
| Unity host | [../CoreAiUnity/README.md](../CoreAiUnity/README.md) |
| DLL embedding | [../../tools/portable/README.md](../../tools/portable/README.md) |
| Release notes | [CHANGELOG.md](CHANGELOG.md) |

License: [PolyForm Noncommercial 1.0](../../LICENSE). Commercial licensing: neoxider@gmail.com.
