# 🛠️ MEAI Tool Calling — Architecture

**Microsoft.Extensions.AI (MEAI)** is a unified pipeline for tool calling across all backends. The **OpenAI-compatible HTTP** `IChatClient` (`MeaiOpenAiChatClient`) lives in the portable **`com.nexoider.coreai`** assembly; Unity wires **`MeaiLlmClient`**, decorators, and **`MessagePipeToolCallEventPublisher`** in **`com.nexoider.coreaiunity`**. See [`README.md`](README.md) for the full portable-doc map.

> 💡 **v2.0+:** Tools can be organized into **SkillSets** — named groups with per-skill prompt instructions. See [AGENT_BUILDER.md — Skills](AGENT_BUILDER.md#skills-v20). The MEAI pipeline handles skill tools identically — `DelegateLlmTool` and `AIFunctionFactory` work the same way regardless of whether the tool was registered directly or through a `SkillSet`.

---

## 📌 The MEAI version is fixed at 9.10.2

CoreAI is written against **Microsoft.Extensions.AI 9.10.2** and builds against it here, in its own repository. That is not a stale pin waiting to be bumped — it is the version the Unity consumer can actually load:

- Unity ships its **own `System.Text.Json`** (assembly version `8.0.0.0`) in the editor's BCL extensions and substitutes it for whatever copy the project restored.
- Every MEAI **10.x** assembly is built against `System.Text.Json 10.0.0.0`. Under Unity it therefore fails to load — at the first call into MEAI, not at compile time.
- 9.10.2 is the newest release on the `8.0.0.0` line.

**The rule:** what CoreAI compiles against equals what the consumer can load. Keeping them equal is what turns "the consumer cannot upgrade" into a build failure here — on `dotnet build tools/portable/CoreAI.Core.csproj` and in the license-free `portable-core` CI job — instead of a surprise during integration. Three places state the version: `Assets/packages.config`, the vendored `Assets/Packages/Microsoft.Extensions.AI*` folders, and `tools/portable/CoreAI.Core.csproj`. `MeaiVersionFloorEditModeTests` fails as soon as any of them disagrees.

Four consequences worth knowing before porting code from a MEAI 10.x sample:

- The user-approval content type is **`FunctionApprovalRequestContent`** (10.x merged it with the MCP variant under the name `ToolApprovalRequestContent`), and it — like `ApprovalRequiredAIFunction` — is marked `[Experimental("MEAI001")]`. `SmartToolCallingChatClient` suppresses `MEAI001` around exactly those uses, never file-wide.
- `FunctionCallContent.InformationalOnly` does not exist. Calls the service already resolved are recognised by their paired `FunctionResultContent` instead.
- **`FunctionInvoker` results are wrapped, not passed through.** Whatever the delegate returns becomes the *value inside* the `FunctionResultContent` MEAI builds. Returning a ready-made `FunctionResultContent` (10.x unwraps it) would show the model the string `Microsoft.Extensions.AI.FunctionResultContent`. `SmartToolCallingChatClient` returns the payload and lets MEAI own the pairing.
- **`AdditionalTools` is read once per request**, before the first provider call, so a tool registered after the model names it is never consulted — MEAI answers `Requested function "…" not found.` itself and CoreAI policy never sees the failure. Unknown names are therefore resolved inside `NativePolicyClient`, where the name is first known, so they still count towards `maxConsecutiveErrors`.

Raising the version is allowed only once the engine can load the newer assemblies: change the pins together, then update the game.

---

## 📐 Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                      ILlmClient                              │
├────────────────────────┬────────────────────────────────────┤
│ MeaiLlmUnityClient     │    OpenAiChatLlmClient              │
│   (local GGUF)         │    (HTTP API)                       │
├────────────────────────┼────────────────────────────────────┤
│ LlmUnityMeaiChatClient │    MeaiOpenAiChatClient             │
│   (MEAI.IChatClient)   │    (MEAI.IChatClient)               │
├────────────────────────┴────────────────────────────────────┤
│                MeaiLlmClient                                 │
│  ┌───────────────────────────────────────────────────────┐  │
│  │     MEAI.FunctionInvokingChatClient                    │  │
│  │  1. Model → tool_calls                                │  │
│  │  2. Resolve AIFunction by name                        │  │
│  │  3. Execute AIFunction.InvokeAsync()                  │  │
│  │  4. Result → model → final answer                     │  │
│  └───────────────────────────────────────────────────────┘  │
├─────────────────────────────────────────────────────────────┤
│           AIFunction[] (MemoryTool, LuaTool, etc.)          │
└─────────────────────────────────────────────────────────────┘
```

**The same MEAI pipeline for both backends.**

---

## 🔧 How it works

### 1. ILlmTool — declarative description

```csharp
public interface ILlmTool
{
    string Name { get; }           // "memory", "execute_lua", "get_inventory"
    string Description { get; }    // What the tool does
    string ParametersSchema { get; } // JSON schema for parameters
}
```

`ILlmTool` is metadata only for system prompts and routing.

### 2. AIFunction — executor

```csharp
public class MemoryTool
{
    public AIFunction CreateAIFunction() => AIFunctionFactory.Create(
        async (string action, string? content, CancellationToken ct) => ExecuteAsync(action, content, ct),
        "memory",
        "Store, append, or clear persistent memory.");
}
```

`AIFunction` wraps a .NET method for MEAI.

The public .NET parameter names are part of the native tool contract. Keep them
identical to the JSON schema property names exposed through `ILlmTool.ParametersSchema`
(`ingredients` in the schema must be `ExecuteAsync(object ingredients, ...)`, not
`ingredientsObj`). If they diverge, MEAI can reject a valid model tool call before
the tool implementation sees it.

### 3. Mapping ILlmTool → AIFunction

In `MeaiLlmClient.BuildAIFunctions()`:

```csharp
switch (tool)
{
    case MemoryLlmTool:  → new MemoryTool(store, roleId).CreateAIFunction()
    case LuaLlmTool:     → luaTool.CreateAIFunction()
    case InventoryLlmTool: → invTool.CreateAIFunction()
    case GameConfigLlmTool: → gcTool.CreateAIFunction()
}
```

### 3.1 IL2CPP / WebGL — typed binding

`MeaiLlmClient` obtains functions via `IAIFunctionLlmTool.CreateAIFunction()` or
`IAIFunctionsLlmTool.CreateAIFunctions()`. Name-based method lookup via reflection is not used.
For a custom tool, implement the matching public interface; a single method with
a matching name is not enough. Declarative built-in memory tools bind separately
to the current role's store. An optional missing binding leaves a warning;
`RequireAny` / `RequireSpecific` with a missing required function fail with `InvalidRequest`
before contacting the provider.

`ForcedToolMode=None` forbids local execution: tool factories are not invoked, and incoming
approval plus unsolicited `FunctionCallContent` do not start the body or an extra model turn.
JSON in plain text is preserved even in the Text channel and with Native-with-Text compatibility enabled.
`ToolExecutionPolicy` itself also rejects such a call, returning an unsuccessful result with the original `CallId`.

### 3.2 WebGL — publishing an awaiting tool body

MEAI awaits the delegate's task inside its binary with `ConfigureAwait(false)`. A Unity WebGL player
has no thread pool, and `SynchronizationContext.Current` there is `UnitySynchronizationContext`, so
when the tool task completes asynchronously on the player loop that continuation is queued to a pool
that never runs: the result is never delivered, the turn shows "Processing…" forever, no second
request is sent, no exception, no timeout. A tool that completes synchronously never hits this.

Rules for every tool whose body can suspend (a confirmation card, a world autosave, an HTTP call):

1. No `ConfigureAwait(false)` anywhere on the tool path — awaits resume on the host context.
   `WebGlUnsafeAsyncPrimitivesEditModeTests` fails on a new occurrence in WebGL-reachable code.
2. Hand the task to MEAI through `MeaiToolTaskBridge.Publish(...)`: it completes the surfaced task
   with the context cleared, so MEAI's continuation runs inline on the completing call stack.

```csharp
public Task<string> ExecuteAsync([Description("...")] string code, CancellationToken ct = default) =>
    MeaiToolTaskBridge.Publish(ExecuteBodyAsync(code, ct));
```

`LuaTool` (`execute_lua`) and `LuaModsLlmTool` (`manage_mods`) do this; a host async delegate given
to `DelegateLlmTool` / `AgentBuilder.WithTool` must do the same. Reproduced and re-verified in the
browser on 2026-09-02 (`dev-docs/G11_RUN_RECORD_2026-09-02.md`).

### 4. MEAI pipeline

```
1. Orchestrator → CompleteAsync(request.Tools)
2. MeaiLlmClient.BuildAIFunctions(tools) → AIFunction[]
3. FunctionInvokingChatClient(innerClient, tools)
4. Model → tool_calls: {"name": "memory", "arguments": {...}}
5. MEAI → finds AIFunction by name → InvokeAsync()
6. Result → model → final answer
7. MeaiLlmClient → LlmCompletionResult
```

### 5. Orchestrator tool contract prompt

When a role has registered tools, `AiOrchestrator` appends a compact `## Tool Contract`
block to the system prompt before calling the LLM. This block lists the available tools,
their descriptions, parameter schemas, and rules for tool-required tasks:

- If the task asks to use a tool, call the matching native tool through MEAI.
- Pass required values as structured tool arguments; do not mention them only in prose.
- Do not claim that a registered tool is unavailable.
- After a tool succeeds, summarize the real tool result briefly.

This prompt contract does not replace provider-native tool choice. It gives small/local
models the same explicit behavioral guidance that production integrations expect, while
`ForcedToolMode` / `RequiredToolName` still control provider-level tool selection when needed.

---

## 📦 Files

### Core (CoreAI)

| File | Purpose |
|------|-----------|
| `ILlmTool.cs` | `ILlmTool` interface + `LlmToolBase` |
| `MemoryTool.cs` | AIFunction for memory (write/append/clear) |
| `LuaTool.cs` | AIFunction for Lua execution |
| `InventoryTool.cs` | AIFunction for inventory |
| `GameConfigTool.cs` | AIFunction for config |
| `WorldTool.cs` | AIFunction for world control |
| `MemoryLlmTool.cs` | `ILlmTool` → `MemoryTool` adapter |
| `LuaLlmTool.cs` | `ILlmTool` → `LuaTool` adapter |
| `InventoryLlmTool.cs` | `ILlmTool` → `InventoryTool` adapter |
| `GameConfigLlmTool.cs` | `ILlmTool` → `GameConfigTool` adapter |
| `WorldLlmTool.cs` | `ILlmTool` → `WorldTool` adapter |

### Unity layer (CoreAiUnity)

| File | Purpose |
|------|------------|
| `MeaiLlmClient.cs` | Shared `ILlmClient` adapter: tool binding, regular response and streaming |
| `LlmEndpointClientFactory.cs` | HTTP/LLMUnity activation after readiness and `Auto`/`Native`/`Text` channel selection |
| `OpenAiChatLlmClient.cs` | Synchronous HTTP composition with a mandatory explicit channel |
| `CoreAISettingsAsset.cs` | Unity host settings |

`MeaiOpenAiChatClient.cs` lives in **CoreAI.Core**, not in the Unity layer. It implements
`Microsoft.Extensions.AI.IChatClient` on top of `IOpenAiHttpTransport`; the core can be used outside Unity.
LLMUnity starts a local llama.cpp endpoint, which the factory reaches with the same HTTP client.

## Creating a client and invoking tools

For runtime selection, use the factory. `descriptor` sets the address, model and `ToolChannel`;
`Auto` performs one probe after readiness, while explicit `Native`/`Text` skip capability probing.

```csharp
LlmEndpointClientFactory factory = new(coreSettings, logger, memoryStore);
LlmEndpointClientActivation activation = await factory.ActivateAsync(
    descriptor, sessionApiKey, cancellationToken);
ILlmClient client = activation.Client;
```

If the capability is already known from host configuration or probing, synchronous composition requires
passing it explicitly. It does not probe the server itself:

```csharp
OpenAiChatLlmClient client = new(httpSettings, coreSettings, logger,
    supportsNativeToolCalling: decision.Native, memoryStore: memoryStore);

LlmCompletionResult result = await client.CompleteAsync(new LlmCompletionRequest
{
    AgentRoleId = "Creator",
    SystemPrompt = "...",
    UserPayload = "Craft an Iron Sword",
    Tools = policy.GetToolsForRole("Creator")
}, cancellationToken);
```

CoreAI binds `ILlmTool` to `AIFunction` via typed interfaces. In a regular non-streaming
response on .NET/desktop, the native loop is driven by MEAI `FunctionInvokingChatClient` (MEAI 9.10.2), while the shared
`ToolExecutionPolicy` applies host rules: timeouts, retries, mutation serialization, and `EndsTurn`.
MEAI also keeps its native approval roundtrip.

The streaming path preserves early execution of completed calls: after the first
`FunctionCallContent`, MEAI buffers the continuation until the end of the stream, so the shared
CoreAI policy with its own streaming adapter is used here. A narrow non-streaming fallback remains for WebGL;
replacing it with the MEAI loop requires separate in-browser verification of continuation behavior.
These are composition limits, not different formats or separate tool implementations.

Text parsing is allowed only on a `Text` endpoint or with an explicit
`AllowTextShapedToolCallsOnNativeEndpoint = true` in the request. In this mode, only declared names are recognized and removed
from the visible response. On `Native`, plain JSON stays as illustrative text,
even if an individual tool failed to bind. An optional unavailable tool yields
diagnostics; `RequireAny` without executable tools and `RequireSpecific` without the named binding
fail with `InvalidRequest` before contacting the model. In `Text`, local `AIFunction` instances are kept,
but the outgoing HTTP contains no `tools`, `tool_choice`, `parallel_tool_calls`, including `ExtraBodyJson`.

---

## 🎯 Forced Tool Mode (v0.25.0+)

Sometimes the model “forgets” to call a tool even when it clearly should (e.g. the user asked for a list quiz and the LLM replies in text that it ran the test). From v0.25.0, `AiTaskRequest` and `LlmCompletionRequest` include `ForcedToolMode` (enum `LlmToolChoiceMode`) for **deterministic** tool-choice behavior per request — default is `Auto` (same as before).

### API

```csharp
public enum LlmToolChoiceMode
{
    Auto = 0,            // default — model decides
    RequireAny = 1,      // provider must call AT LEAST ONE tool
    RequireSpecific = 2, // provider must call the tool named RequiredToolName
    None = 3             // provider must answer with text only; tool calls disallowed
}
```

Setting it:

```csharp
// Force any tool call for this request:
await orch.RunTaskAsync(new AiTaskRequest
{
    RoleId = "Teacher",
    Hint = "give me a quiz on lists",
    ForcedToolMode = LlmToolChoiceMode.RequireAny
});

// Force a specific tool:
await orch.RunTaskAsync(new AiTaskRequest
{
    RoleId = "Teacher",
    Hint = "spawn quiz",
    ForcedToolMode = LlmToolChoiceMode.RequireSpecific,
    RequiredToolName = "spawn_quiz"
});
```

### Mapping to Microsoft.Extensions.AI

`MeaiLlmClient.ApplyForcedToolMode` maps values 1:1 to `ChatOptions.ToolMode`:

| `LlmToolChoiceMode` | MEAI `ChatToolMode` | Provider semantics |
|---|---|---|
| `Auto` | `null` | OpenAI: `tool_choice: "auto"` (default) |
| `RequireAny` | `ChatToolMode.RequireAny` | OpenAI: `tool_choice: "required"` |
| `RequireSpecific` | `ChatToolMode.RequireSpecific(name)` | OpenAI: `tool_choice: {type: "function", function: {name: ...}}` |
| `None` | `ChatToolMode.None` | OpenAI: `tool_choice: "none"` |

For `RequireSpecific`, the name is checked against registered `AIFunction[]` for the role — if the tool is missing, a warning is logged and forced mode is downgraded to `RequireAny` (the model must still call something — better to fail loudly than silently get a non-tool answer).

### Streaming + ForcedToolMode (v0.25.0)

In `MeaiLlmClient.CompleteStreamingAsync`, forced mode applies **only on the first iteration** of the tool loop. After we feed the model the tool result, options are cloned with `ChatToolMode.Auto` via `CloneOptionsWithAutoToolMode` — otherwise the model would stay locked in an infinite tool-call loop (each round would be forced again).

This matches how multi-step tool chains work in Claude Code / Cursor: the first tool call can be forced (if the app layer decides), later steps are up to the model.

### When to use

- **`Auto` (default).** 95% of cases. In `ToolsAndChat`, the model usually picks tools well on its own.
- **`RequireAny`.** When you know deterministically that a tool is needed but not which one. E.g. an intent classifier detected “wants to test knowledge” — some interactive evaluation tool must run, not plain text.
- **`RequireSpecific`.** Narrow integrations: rerun fixes, Lua repair, forcing a specific workflow. Use sparingly — forcing tool choice too often hurts dialogue naturalness.
- **`None`.** Tools are registered for the role, but for this turn they must not run (e.g. post-tool reflection / summarization).

### Tests

See `Assets/CoreAiUnity/Tests/EditMode/ForcedToolModeEditModeTests.cs`.

---

## 📚 References

- [Microsoft.Extensions.AI Docs](https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai)
- [FunctionInvokingChatClient](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.ai.functioninvokingchatclient)
- [AIFunctionFactory](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.ai.aifunctionfactory)
- [MEAI_TOKENS_FACT_VS_ESTIMATE.md](MEAI_TOKENS_FACT_VS_ESTIMATE.md) — provider `usage` vs pre-request estimates; `stream_options` / SSE usage; orchestrator vs HTTP timeouts; streaming timeout vs cancel; `MessagePipeToolCallEventPublisher` (Unity) for tool lifecycle diagnostics
- [LLM_ROUTING.md](LLM_ROUTING.md) — portable routing modes and policy contracts
- [README.md](README.md) — index of all files in this `Docs` folder
- [ChatToolMode](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.ai.chattoolmode)
