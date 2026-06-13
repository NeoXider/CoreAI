# рџ› пёЏ MEAI Tool Calling вЂ” Architecture

**Microsoft.Extensions.AI (MEAI)** is a unified pipeline for tool calling across all backends. The **OpenAI-compatible HTTP** `IChatClient` (`MeaiOpenAiChatClient`) lives in the portable **`com.nexoider.coreai`** assembly; Unity wires **`MeaiLlmClient`**, decorators, and **`MessagePipeToolCallEventPublisher`** in **`com.nexoider.coreaiunity`**. See [`README.md`](README.md) for the full portable-doc map.

> рџ’Ў **v2.0+:** Tools can be organized into **SkillSets** вЂ” named groups with per-skill prompt instructions. See [AGENT_BUILDER.md вЂ” Skills](AGENT_BUILDER.md#skills-v20). The MEAI pipeline handles skill tools identically вЂ” `DelegateLlmTool` and `AIFunctionFactory` work the same way regardless of whether the tool was registered directly or through a `SkillSet`.

---

## рџ“ђ Architecture

```
в”Њв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”ђ
в”‚                      ILlmClient                              в”‚
в”њв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”¬в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”¤
в”‚ MeaiLlmUnityClient     в”‚    OpenAiChatLlmClient              в”‚
в”‚   (local GGUF)         в”‚    (HTTP API)                       в”‚
в”њв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”јв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”¤
в”‚ LlmUnityMeaiChatClient в”‚    MeaiOpenAiChatClient             в”‚
в”‚   (MEAI.IChatClient)   в”‚    (MEAI.IChatClient)               в”‚
в”њв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”ґв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”¤
в”‚                MeaiLlmClient                                 в”‚
в”‚  в”Њв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”ђ  в”‚
в”‚  в”‚     MEAI.FunctionInvokingChatClient                    в”‚  в”‚
в”‚  в”‚  1. Model в†’ tool_calls                                в”‚  в”‚
в”‚  в”‚  2. Resolve AIFunction by name                        в”‚  в”‚
в”‚  в”‚  3. Execute AIFunction.InvokeAsync()                  в”‚  в”‚
в”‚  в”‚  4. Result в†’ model в†’ final answer                     в”‚  в”‚
в”‚  в””в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”  в”‚
в”њв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”¤
в”‚           AIFunction[] (MemoryTool, LuaTool, etc.)          в”‚
в””в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”
```

**The same MEAI pipeline for both backends.**

---

## рџ”§ How it works

### 1. ILlmTool вЂ” declarative description

```csharp
public interface ILlmTool
{
    string Name { get; }           // "memory", "execute_lua", "get_inventory"
    string Description { get; }    // What the tool does
    string ParametersSchema { get; } // JSON schema for parameters
}
```

`ILlmTool` is metadata only for system prompts and routing.

### 2. AIFunction вЂ” executor

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

### 3. Mapping ILlmTool в†’ AIFunction

In `MeaiLlmClient.BuildAIFunctions()`:

```csharp
switch (tool)
{
    case MemoryLlmTool:  в†’ new MemoryTool(store, roleId).CreateAIFunction()
    case LuaLlmTool:     в†’ luaTool.CreateAIFunction()
    case InventoryLlmTool: в†’ invTool.CreateAIFunction()
    case GameConfigLlmTool: в†’ gcTool.CreateAIFunction()
}
```

### 3.1 IL2CPP / WebGL вЂ” `CreateAIFunction` visibility

`MeaiLlmClient` resolves `CreateAIFunction()` via reflection on each `ILlmTool` instance. IL2CPP can strip methods from **game** assemblies that are not preserved. For custom tools shipped outside **`CoreAI.Core`** / **`CoreAI.Source`**, mark `CreateAIFunction` with **`[UnityEngine.Scripting.Preserve]`** or add a project **`link.xml`** entry for your tool types. The CoreAI package **`link.xml`** preserves CoreAI assemblies only.

### 4. MEAI pipeline

```
1. Orchestrator в†’ CompleteAsync(request.Tools)
2. MeaiLlmClient.BuildAIFunctions(tools) в†’ AIFunction[]
3. FunctionInvokingChatClient(innerClient, tools)
4. Model в†’ tool_calls: {"name": "memory", "arguments": {...}}
5. MEAI в†’ finds AIFunction by name в†’ InvokeAsync()
6. Result в†’ model в†’ final answer
7. MeaiLlmClient в†’ LlmCompletionResult
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

## рџ“¦ Files

### Core (CoreAI)

| File | Purpose |
|------|-----------|
| `ILlmTool.cs` | `ILlmTool` interface + `LlmToolBase` |
| `MemoryTool.cs` | AIFunction for memory (write/append/clear) |
| `LuaTool.cs` | AIFunction for Lua execution |
| `InventoryTool.cs` | AIFunction for inventory |
| `GameConfigTool.cs` | AIFunction for config |
| `WorldTool.cs` | AIFunction for world control |
| `MemoryLlmTool.cs` | `ILlmTool` в†’ `MemoryTool` adapter |
| `LuaLlmTool.cs` | `ILlmTool` в†’ `LuaTool` adapter |
| `InventoryLlmTool.cs` | `ILlmTool` в†’ `InventoryTool` adapter |
| `GameConfigLlmTool.cs` | `ILlmTool` в†’ `GameConfigTool` adapter |
| `WorldLlmTool.cs` | `ILlmTool` в†’ `WorldTool` adapter |

### Unity layer (CoreAiUnity)

| File | Purpose |
|------|-----------|
| `MeaiLlmClient.cs` | **Unified MEAI client** for all backends |
| `MeaiLlmUnityClient.cs` | Factory: LLMAgent в†’ LlmUnityMeaiChatClient в†’ MeaiLlmClient |
| `OpenAiChatLlmClient.cs` | Factory: HTTP в†’ MeaiOpenAiChatClient в†’ MeaiLlmClient |
| `LlmUnityMeaiChatClient.cs` | `MEAI.IChatClient` for LLMAgent |
| `MeaiOpenAiChatClient.cs` | `MEAI.IChatClient` for HTTP API |
| `CoreAISettingsAsset.cs` | Unified settings (API, LLMUnity, retry, timeout) |

---

## рџљЂ Usage

### Creating a client

```csharp
// HTTP API
var client = new OpenAiChatLlmClient(settings, logger, memoryStore);

// LLMUnity
var client = new MeaiLlmUnityClient(unityAgent, logger, memoryStore);

// Both use MeaiLlmClient в†’ FunctionInvokingChatClient
```

### Tool calling

```csharp
// Orchestrator passes tools in the request
var result = await client.CompleteAsync(new LlmCompletionRequest
{
    AgentRoleId = "Creator",
    SystemPrompt = "...",
    UserPayload = "Craft an Iron Sword",
    Tools = policy.GetToolsForRole("Creator")  // ILlmTool[]
});

// MEAI automatically:
// 1. Converts ILlmTool в†’ AIFunction
// 2. Sends tools to the model
// 3. Model returns tool_calls
// 4. FunctionInvokingChatClient runs AIFunction
// 5. Result в†’ model в†’ final answer
```

---

## рџЋЇ Benefits of MEAI

| Before MEAI | After MEAI |
|---------|-----------|
| Manual parsing of tool calls from text | вњ… Automatic pipeline |
| Different code for LLMUnity and HTTP | вњ… Single `MeaiLlmClient` |
| Manual retry | вњ… MEAI handles the loop |
| Fallback hacks | вњ… Standard Microsoft approach |

---

## рџЋЇ Forced Tool Mode (v0.25.0+)

Sometimes the model вЂњforgetsвЂќ to call a tool even when it clearly should (e.g. the user asked for a list quiz and the LLM replies in text that it ran the test). From v0.25.0, `AiTaskRequest` and `LlmCompletionRequest` include `ForcedToolMode` (enum `LlmToolChoiceMode`) for **deterministic** tool-choice behavior per request вЂ” default is `Auto` (same as before).

### API

```csharp
public enum LlmToolChoiceMode
{
    Auto = 0,            // default вЂ” model decides
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

For `RequireSpecific`, the name is checked against registered `AIFunction[]` for the role вЂ” if the tool is missing, a warning is logged and forced mode is downgraded to `RequireAny` (the model must still call something вЂ” better to fail loudly than silently get a non-tool answer).

### Streaming + ForcedToolMode (v0.25.0)

In `MeaiLlmClient.CompleteStreamingAsync`, forced mode applies **only on the first iteration** of the tool loop. After we feed the model the tool result, options are cloned with `ChatToolMode.Auto` via `CloneOptionsWithAutoToolMode` вЂ” otherwise the model would stay locked in an infinite tool-call loop (each round would be forced again).

This matches how multi-step tool chains work in Claude Code / Cursor: the first tool call can be forced (if the app layer decides), later steps are up to the model.

### When to use

- **`Auto` (default).** 95% of cases. In `ToolsAndChat`, the model usually picks tools well on its own.
- **`RequireAny`.** When you know deterministically that a tool is needed but not which one. E.g. an intent classifier detected вЂњwants to test knowledgeвЂќ вЂ” some interactive evaluation tool must run, not plain text.
- **`RequireSpecific`.** Narrow integrations: rerun fixes, Lua repair, forcing a specific workflow. Use sparingly вЂ” forcing tool choice too often hurts dialogue naturalness.
- **`None`.** Tools are registered for the role, but for this turn they must not run (e.g. post-tool reflection / summarization).

### Tests

See `Assets/CoreAiUnity/Tests/EditMode/ForcedToolModeEditModeTests.cs`.

---

## рџ“љ References

- [Microsoft.Extensions.AI Docs](https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai)
- [FunctionInvokingChatClient](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.ai.functioninvokingchatclient)
- [AIFunctionFactory](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.ai.aifunctionfactory)
- [MEAI_TOKENS_FACT_VS_ESTIMATE.md](MEAI_TOKENS_FACT_VS_ESTIMATE.md) вЂ” provider `usage` vs pre-request estimates; `stream_options` / SSE usage; orchestrator vs HTTP timeouts; streaming timeout vs cancel; `MessagePipeToolCallEventPublisher` (Unity) for tool lifecycle diagnostics
- [LLM_ROUTING.md](LLM_ROUTING.md) вЂ” portable routing modes and policy contracts
- [README.md](README.md) вЂ” index of all files in this `Docs` folder
- [ChatToolMode](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.ai.chattoolmode)
