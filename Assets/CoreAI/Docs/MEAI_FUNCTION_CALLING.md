# MEAI Function Calling Guide

## Overview

CoreAI uses **Microsoft.Extensions.AI (MEAI)** for standardized function calling across all LLM providers. This enables AI agents to call tools (like memory) through a unified .NET API.

## How It Works

```
AiOrchestrator
  ↓ Creates MemoryTool with AIFunctionFactory.Create()
  ↓ Registers tool with MeaiChatClientAdapter
  ↓ Sends request with ChatOptions.Tools
  ↓ LLM responds with tool call JSON
  ↓ MEAI automatically executes the tool
  ↓ Clean response returned to orchestrator
```

## Memory Tool

### AI Model Usage

Models output JSON:
```json
{"tool": "memory", "action": "write", "content": "player prefers stealth"}
```

### Actions

| Action | Description | Example |
|--------|-------------|---------|
| `write` | Replace all memory | `{"tool": "memory", "action": "write", "content": "remember: apples"}` |
| `append` | Add to existing | `{"tool": "memory", "action": "append", "content": "also: oranges"}` |
| `clear` | Remove all | `{"tool": "memory", "action": "clear"}` |

## Implementation

### MemoryTool.cs

```csharp
public AIFunction CreateAIFunction()
{
    return AIFunctionFactory.Create(
        ExecuteAsync,
        "memory",
        "Store, append, or clear persistent memory");
}

public async Task<MemoryResult> ExecuteAsync(
    string action,
    string content = null,
    CancellationToken ct = default)
{
    // Handle write/append/clear actions
}
```

### AiOrchestrator Integration

```csharp
var memoryTool = new MemoryTool(_memoryStore, roleId);
var memoryFunction = memoryTool.CreateAIFunction();

_meaiClient.RegisterTool(memoryFunction);

var chatOptions = new ChatOptions {
    Tools = { memoryFunction }
};

var response = await _meaiClient.GetResponseAsync(
    chatHistory, chatOptions);
```

## System Prompts

All agents receive updated system prompts:

**Creator:**
```
MEMORY TOOL: You have a 'memory' tool available.
Use ONLY this JSON format:
{"tool": "memory", "action": "write", "content": "YOUR_INFO_HERE"}
```

**Programmer:**
```
For memory operations, use ONLY JSON format:
{"tool": "memory", "action": "write", "content": "YOUR_DATA"}
```

## Adding New Tools

1. Create tool class with method:
```csharp
public class MyTool
{
    public async Task<MyResult> ExecuteAsync(string param) { ... }
}
```

2. Register via AIFunctionFactory:
```csharp
AIFunctionFactory.Create(
    myTool.ExecuteAsync,
    "my_tool",
    "Description for the AI model");
```

3. Add to orchestrator:
```csharp
_meaiClient.RegisterTool(myTool.CreateAIFunction());
```

4. Update system prompts with tool instructions

## Dependencies

Microsoft.Extensions.AI installed via NuGet:
- `Microsoft.Extensions.AI` v10.4.1
- `Microsoft.Extensions.AI.Abstractions` v10.4.1

See `Assets/packages.config`.

## Files

| File | Description |
|------|-------------|
| `MemoryTool.cs` | MEAI AIFunction via AIFunctionFactory |
| `MeaiChatClientAdapter.cs` | IChatClient over ILlmClient |
| `AiOrchestrator.cs` | MEAI pipeline integration |
| `IAgentMemoryStore.cs` | Memory storage interface |
| `NullAgentMemoryStore.cs` | Null implementation for testing |

## Testing

Run MEAI integration tests:
- `MemoryToolMeaiEditModeTests.cs` - Tool execution tests
- All existing tests updated for JSON format

## Migration

The old `[TOOL:memory]...[/TOOL]` format is **removed**. All tool calls now use MEAI function calling with JSON format.

**Benefits:**
- ✅ Standard .NET API (Microsoft.Extensions.AI)
- ✅ Provider-agnostic (works with any LLM)
- ✅ Automatic tool invocation via MEAI middleware
- ✅ Better metrics and tracking
- ✅ Easy to add new tools

---

**Version**: 0.3.0  
**Date**: 2026-04-04  
**Status**: Production-ready
