# Release v0.3.0 - MEAI Function Calling

## Summary

**Major Release**: Migrated to **Microsoft.Extensions.AI (MEAI)** for standardized function calling across all LLM providers.

## What Changed

### Code
- ✅ **MemoryTool.cs** - NEW: MEAI AIFunction via AIFunctionFactory.Create()
- ✅ **MeaiChatClientAdapter.cs** - NEW: IChatClient adapter over ILlmClient
- ✅ **AiOrchestrator.cs** - UPDATED: MEAI pipeline + fallback
- ✅ **BuiltInAgentSystemPromptTexts.cs** - UPDATED: MEAI format prompts
- ❌ **AgentToolCallParser.cs** - REMOVED: Replaced by MEAI

### Packages
- ✅ **com.nexoider.coreai**: 0.2.0 → **0.3.0**
- ✅ **com.nexoider.coreaiunity**: 0.2.0 → **0.3.0**

### Tests
- ✅ MemoryToolMeaiEditModeTests.cs - 8 NEW MEAI integration tests
- ✅ All tests updated for MEAI/JSON format
- ✅ Legacy AgentToolCallParser tests removed

### Documentation
- ✅ MEAI_FUNCTION_CALLING.md - NEW: Complete MEAI guide
- ✅ README_MEAI.md - NEW: Quick reference
- ✅ AI_AGENT_ROLES.md - UPDATED: MEAI integration notes
- ✅ README.md - UPDATED: Package description
- ✅ Both CHANGELOG.md - UPDATED: v0.3.0 sections

## How It Works

### Before (Legacy JSON Parsing)
```csharp
// Manual parsing
AgentToolCallParser.TryExtract(content, out cleaned, out toolCalls);
foreach (var tool in toolCalls) {
    if (tool.ToolName == "memory") {
        // Manual memory handling
    }
}
```

### After (MEAI Function Calling)
```csharp
// MEAI automatic tool invocation
var memoryTool = new MemoryTool(_memoryStore, roleId);
var memoryFunction = memoryTool.CreateAIFunction(); // AIFunctionFactory

var chatOptions = new ChatOptions {
    Tools = { memoryFunction }
};

// MEAI automatically detects and executes tool calls
var response = await chatClient.GetResponseAsync(
    chatHistory, chatOptions);
```

## AI Model Usage

Models output standard JSON:
```json
{"tool": "memory", "action": "write", "content": "remember: apples"}
```

MEAI automatically:
1. Detects the tool call in response
2. Executes MemoryTool.ExecuteAsync()
3. Returns result to model
4. Provides clean response content

## Breaking Changes

- ❌ `[TOOL:memory]` format **removed**
- ❌ `AgentToolCallParser` **removed**
- ✅ Only MEAI function calling supported

## Dependencies

Microsoft.Extensions.AI via NuGet:
- `Microsoft.Extensions.AI` v10.4.1
- `Microsoft.Extensions.AI.Abstractions` v10.4.1

Already installed in `Assets/packages.config`.

## Benefits

1. **Standard .NET API**: Official Microsoft library
2. **Provider-Agnostic**: Works with any LLM (OpenAI, Qwen, Llama, etc.)
3. **Automatic Tool Invocation**: MEAI middleware handles execution
4. **Better Metrics**: Built-in tool usage tracking
5. **Easy Extensibility**: Add new tools via AIFunctionFactory
6. **Fallback Support**: Falls back to legacy if MEAI unavailable

## Testing

Run MEAI integration tests:
```bash
# Unity Test Runner
EditMode: MemoryToolMeaiEditModeTests (8 tests)
All existing tests updated for MEAI/JSON format
```

## Migration Guide

### For AI Models

Update system prompts:
```
MEMORY TOOL: Use JSON format:
{"tool": "memory", "action": "write", "content": "YOUR_DATA"}
```

### For Developers

No code changes needed for basic usage. 
To add new tools:

```csharp
// 1. Create tool class
public class MyTool {
    public async Task<MyResult> ExecuteAsync(string param) { ... }
}

// 2. Register via AIFunctionFactory
AIFunctionFactory.Create(
    myTool.ExecuteAsync,
    "my_tool",
    "Description for AI model");

// 3. Add to orchestrator
_meaiClient.RegisterTool(myTool.CreateAIFunction());
```

## Files Changed

### CoreAI Package (com.nexoider.coreai)
- ✨ `Runtime/Core/Features/AgentMemory/MemoryTool.cs`
- ✨ `Runtime/Core/Features/Orchestration/MeaiChatClientAdapter.cs`
- ✅ `Runtime/Core/Features/Orchestration/AiOrchestrator.cs`
- ✅ `Runtime/Core/Features/AgentPrompts/BuiltInAgentSystemPromptTexts.cs`
- ❌ `Runtime/Core/Features/AgentMemory/AgentToolCallParser.cs` (removed)
- ✨ `Docs/MEAI_FUNCTION_CALLING.md`
- ✨ `Runtime/Core/Features/AgentMemory/README_MEAI.md`
- ✅ `package.json` → v0.3.0
- ✅ `CHANGELOG.md` → v0.3.0 section
- ✅ `README.md` → Updated

### CoreAI Unity Package (com.nexoider.coreaiunity)
- ✨ `Tests/EditMode/MemoryToolMeaiEditModeTests.cs`
- ❌ `Tests/EditMode/AgentToolCallParserTests.cs` (removed)
- ❌ `Tests/EditMode/AgentToolCallParserJsonEditModeTests.cs` (removed)
- ✅ `Tests/PlayModeTest/AgentMemoryWithRealModelPlayModeTests.cs`
- ✅ `package.json` → v0.3.0
- ✅ `CHANGELOG.md` → v0.3.0 section
- ✅ `Docs/AI_AGENT_ROLES.md` → Updated

## Timeline

- **v0.1.x**: Legacy `[TOOL:memory]` format
- **v0.2.0**: JSON-only format (transitional)
- **v0.3.0**: MEAI function calling (current)

## Release Date

2026-04-04

## Notes

This is a **feature release** (0.2.0 → 0.3.0) adding Microsoft.Extensions.AI integration while maintaining backward compatibility through fallback mechanism.

All users are encouraged to upgrade to v0.3.0 for better tool calling support and easier integration with any LLM provider.
