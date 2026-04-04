# MEAI Function Calling

## Overview

CoreAI использует **Microsoft.Extensions.AI (MEAI)** для стандартизированного function calling. Это позволяет AI агентам вызывать функции (tools) через единый .NET API, работающий с любым LLM provider.

## Architecture

```
AiOrchestrator
    ↓
MeaiChatClientAdapter (IChatClient over ILlmClient)
    ↓
MemoryTool.CreateAIFunction() → AIFunctionFactory.Create()
    ↓
ChatOptions.Tools = { memoryFunction }
    ↓
MEAI Pipeline (automatic tool invocation)
    ↓
LLM Response → MEAI executes tools → Clean response
```

## Memory Tool

### Registration

```csharp
var memoryTool = new MemoryTool(_memoryStore, roleId);
var memoryFunction = memoryTool.CreateAIFunction();

var chatOptions = new ChatOptions {
    Tools = { memoryFunction }
};
```

### Usage by AI Model

The model outputs:
```json
{"tool": "memory", "action": "write", "content": "player prefers stealth"}
```

MEAI automatically:
1. Detects the tool call in the response
2. Executes `MemoryTool.ExecuteAsync()`
3. Returns the result to the model
4. Provides clean content to the orchestrator

### Actions

| Action | Description |
|--------|-------------|
| `write` | Replace all memory |
| `append` | Add to existing memory |
| `clear` | Remove all memory |

## Files

| File | Description |
|------|-------------|
| `MemoryTool.cs` | MEAI AIFunction via AIFunctionFactory.Create() |
| `MeaiChatClientAdapter.cs` | IChatClient adapter over ILlmClient |
| `AiOrchestrator.cs` | MEAI pipeline integration |
| `AgentMemoryState.cs` | Memory state container |
| `IAgentMemoryStore.cs` | Memory store interface |
| `NullAgentMemoryStore.cs` | Null implementation for testing |

## Dependencies

Microsoft.Extensions.AI is installed via NuGet:
- `Microsoft.Extensions.AI` v10.4.1
- `Microsoft.Extensions.AI.Abstractions` v10.4.1

See `Assets/packages.config`.

## System Prompts

Agents receive tool instructions in system prompts:

```
MEMORY TOOL: You have a 'memory' tool available. 
Use ONLY this JSON format:
{"tool": "memory", "action": "write", "content": "YOUR_INFO_HERE"}
```

See `BuiltInAgentSystemPromptTexts.cs` for all role prompts.

## Fallback

If MEAI is unavailable, the orchestrator falls back to direct `ILlmClient` usage:

```csharp
#if !COREAI_NO_MEAI
    // MEAI pipeline
#else
    // Direct ILlmClient
#endif
```

## Adding New Tools

1. Create a tool class with a method for the action
2. Register via `AIFunctionFactory.Create()`:
```csharp
AIFunctionFactory.Create(
    executeMethod,
    "tool_name",
    "Tool description for the model");
```
3. Add to `ChatOptions.Tools`
4. Update system prompts with tool instructions

## Testing

Run MEAI integration tests:
- `MemoryToolTests.cs` - Tool execution tests
- `MeaiChatClientAdapterTests.cs` - Adapter tests
- `AiOrchestratorMeaiTests.cs` - Full pipeline tests

## Migration from Legacy Format

The old `[TOOL:memory]...[/TOOL]` format has been removed. 
All tool calls now use MEAI function calling with JSON format.

See [CHANGELOG.md](../../CHANGELOG.md) for version history.
