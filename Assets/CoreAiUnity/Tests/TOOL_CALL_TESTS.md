# 🧪 Tool Calling Test Suite

Complete test suite for all MEAI tool calls: Memory and Execute Lua.

## Tests

### EditMode Tests (Fast, No Unity)

| Test | What it tests | File |
|------|---------------|------|
| `MemoryTool_CreateAIFunction_ReturnsNonNull` | AIFunction creation for memory | `MeaiToolCallsEditModeTests.cs` |
| `MemoryTool_ExecuteAsync_Write_SavesMemory` | Memory write | `MeaiToolCallsEditModeTests.cs` |
| `MemoryTool_ExecuteAsync_Append_AppendsToExisting` | Append to memory | `MeaiToolCallsEditModeTests.cs` |
| `MemoryTool_ExecuteAsync_Clear_RemovesMemory` | Clear memory | `MeaiToolCallsEditModeTests.cs` |
| `LuaTool_CreateAIFunction_ReturnsNonNull` | AIFunction creation for Lua | `MeaiToolCallsEditModeTests.cs` |
| `LuaTool_ExecuteAsync_EmptyCode_ReturnsError` | Empty-code validation | `MeaiToolCallsEditModeTests.cs` |
| `LuaTool_ExecuteAsync_ValidCode_CallsExecutor` | Lua code execution | `MeaiToolCallsEditModeTests.cs` |
| `TryParseToolCallFromText_MemoryTool_ParsesCorrectly` | memory tool call parsing | `MeaiToolCallsEditModeTests.cs` |
| `TryParseToolCallFromText_LuaTool_ParsesCorrectly` | execute_lua tool call parsing | `MeaiToolCallsEditModeTests.cs` |
| `TryParseToolCallFromText_NoToolCall_ReturnsFalse` | No tool call | `MeaiToolCallsEditModeTests.cs` |
| `CompleteStreamingAsync_ToolJsonInStream_ExecutesToolAndReturnsFinalText` | Streaming tool cycle: tool JSON -> execute -> continued text | `MeaiLlmClientEditModeTests.cs` |
| `CompleteStreamingAsync_ToolJsonWithVisiblePrefix_KeepsPrefixAndHidesJson` | Prefix + final text; with live SSE, the prefix chunk may temporarily contain JSON before tool extraction | `MeaiLlmClientEditModeTests.cs` |
| `CompleteStreamingAsync_TooManyToolIterations_ReturnsTerminalError` | Protection against an infinite streaming tool loop | `MeaiLlmClientEditModeTests.cs` |

### EditMode Tests (v0.24.0 - Parser Hardening)

| Test | What it tests | File |
|------|---------------|------|
| `SingleToolCall_ExtractedCorrectly` | Single tool call with prefix text | `MeaiLlmClientEditModeTests.cs` |
| `MultipleToolCalls_AllExtracted` | Multiple tool calls in one text | `MeaiLlmClientEditModeTests.cs` |
| `JsonInCodeBlock_NotExtracted` | JSON in a code block is ignored (false positive protection) | `MeaiLlmClientEditModeTests.cs` |
| `MalformedJson_GracefullySkipped` | Incomplete JSON does not cause an error | `MeaiLlmClientEditModeTests.cs` |
| `JsonWithoutNameAndArguments_NotExtracted` | Plain JSON without name+arguments is not a tool call | `MeaiLlmClientEditModeTests.cs` |
| `EmptyText_ReturnsFalse` | Empty string/null -> false | `MeaiLlmClientEditModeTests.cs` |
| `NestedBracesInArguments_HandledCorrectly` | Nested JSON objects in arguments | `MeaiLlmClientEditModeTests.cs` |
| `StripCodeBlocks_PreservesPositions` | Code-block stripping preserves positions | `MeaiLlmClientEditModeTests.cs` |
| `IsValidToolCallJson_RequiresBothKeys` | Validation requires name+arguments | `MeaiLlmClientEditModeTests.cs` |
| `FindToolCallJsonSpans_MultipleSpans` | Finds multiple JSON spans | `MeaiLlmClientEditModeTests.cs` |
| `ToolCallWithStringContainingBraces_HandledCorrectly` | String containing {} in arguments | `MeaiLlmClientEditModeTests.cs` |

### EditMode Tests (v0.24.0 - ToolExecutionPolicy)

| Test | What it tests | File |
|------|---------------|------|
| `CheckDuplicate_FirstCall_ReturnsNull` | First call is not blocked | `ToolExecutionPolicyEditModeTests.cs` |
| `CheckDuplicate_SameSignatureTwice_BlocksSecond` | Repeated identical call is blocked | `ToolExecutionPolicyEditModeTests.cs` |
| `CheckDuplicate_DifferentArgs_Allowed` | Different arguments are not a duplicate | `ToolExecutionPolicyEditModeTests.cs` |
| `CheckDuplicate_AllowDuplicatesGlobal_NeverBlocks` | Global AllowDuplicates=true | `ToolExecutionPolicyEditModeTests.cs` |
| `CheckDuplicate_PerToolAllowDuplicates_Respected` | Per-tool AllowDuplicates flag | `ToolExecutionPolicyEditModeTests.cs` |
| `RecordSuccess_ResetsCounter` | Success resets the error counter | `ToolExecutionPolicyEditModeTests.cs` |
| `RecordFailure_IncrementsCounter` | Failure increments the counter | `ToolExecutionPolicyEditModeTests.cs` |
| `IsMaxErrorsReached_AtThreshold_ReturnsTrue` | Error threshold works | `ToolExecutionPolicyEditModeTests.cs` |
| `Reset_ClearsEverything` | Reset clears both duplicates and the counter | `ToolExecutionPolicyEditModeTests.cs` |
| `ExecuteSingle_ToolFound_ReturnsResult` | Found tool returns a result | `ToolExecutionPolicyEditModeTests.cs` |
| `ExecuteSingle_ToolNotFound_ReturnsFailed` | Missing tool -> failed | `ToolExecutionPolicyEditModeTests.cs` |
| `ExecuteBatch_AllSucceed_ResetsErrorCounter` | Batch success resets errors | `ToolExecutionPolicyEditModeTests.cs` |
| `ExecuteBatch_DuplicateBlocked_ReturnsFailed` | Batch duplicate is blocked | `ToolExecutionPolicyEditModeTests.cs` |
| `BuildMaxErrorsResponse_ContainsErrorText` | Max-errors response contains error text | `ToolExecutionPolicyEditModeTests.cs` |

### EditMode Tests (Composition Reliability)

| Test | What it tests | File |
|------|---------------|------|
| `Start_FirstEntryPoint_InitializesCoreAiFacade` | First startup initializes the CoreAI facade | `CoreAIGameEntryPointEditModeTests.cs` |
| `Start_SecondEntryPoint_IsSkippedAndDoesNotOverrideFacade` | Repeated startup does not reinitialize CoreAI or overwrite dependencies | `CoreAIGameEntryPointEditModeTests.cs` |

### PlayMode Tests (with Real LLM)

| Test | What it tests | Backend | File |
|------|---------------|--------|------|
| `AllToolCalls_MemoryTool_WriteAppendClear` | Write/Append/Clear memory | LLMUnity or HTTP | `AllToolCallsPlayModeTests.cs` |
| `AllToolCalls_ExecuteLuaTool_Programmer` | Execute Lua from Programmer | LLMUnity or HTTP | `AllToolCallsPlayModeTests.cs` |
| `CraftingMemoryLlmUnity_ThreeCrafts_AllUnique` | Crafting combat test with memory | LLMUnity | `CraftingMemoryViaLlmUnityPlayModeTests.cs` |

## Running Tests

### EditMode (Fast)

```
Unity Test Runner -> EditMode -> Run All
```

### PlayMode with LLMUnity (Local Model)

```bash
# Set environment variables
export COREAI_PLAYMODE_LLM_BACKEND=llmunity

# Run in Unity
Unity Test Runner -> PlayMode -> AllToolCallsPlayModeTests
```

### PlayMode with HTTP API (LM Studio)

```bash
# Set environment variables
export COREAI_PLAYMODE_LLM_BACKEND=http
export COREAI_OPENAI_TEST_BASE=http://localhost:1234/v1
export COREAI_OPENAI_TEST_MODEL=qwen3.5-2b

# Run in Unity
Unity Test Runner -> PlayMode -> AllToolCallsPlayModeTests
```

### Auto Mode (Default)

```bash
# Without variables, automatically chooses LLMUnity or HTTP
Unity Test Runner -> PlayMode -> AllToolCallsPlayModeTests
```

## Unified Tool Call Format

All tool calls use **one format**:

```json
{"name": "tool_name", "arguments": {"param1": "value1", "param2": "value2"}}
```

### Memory Tool

```json
{"name": "memory", "arguments": {"action": "write", "content": "Craft#1: Iron Sword"}}
{"name": "memory", "arguments": {"action": "append", "content": "Craft#2: Steel Shield"}}
{"name": "memory", "arguments": {"action": "clear"}}
```

### Execute Lua Tool

```json
{"name": "execute_lua", "arguments": {"code": "create_item('Sword', 'weapon', 75)\nreport('crafted Sword')"}}
```

## Test Architecture

```
┌─────────────────────────────────────────────────────────┐
│                Tool Call Tests                          │
├──────────────────┬──────────────────┬───────────────────┤
│   EditMode       │   EditMode       │   PlayMode        │
│   (unit)         │   (policy/parser)│   (LLM)           │
├──────────────────┼──────────────────┼───────────────────┤
│ MemoryTool       │ ToolExecPolicy   │ Memory Write      │
│ LuaTool          │  └ Duplicates    │ Memory Append     │
│ JSON Parsing     │  └ Error Countr  │ Memory Clear      │
│ Streaming cycle  │  └ Batch exec    │ Execute Lua       │
│ Code block guard │ TryExtractToolC  │ Crafting Memory   │
│ Multi-tool       │  └ Multi-tool    │ Workflow          │
│                  │  └ Code blocks   │                   │
├──────────────────┼──────────────────┼───────────────────┤
│ Fast (1-2s)      │ Fast (1-2s)      │ Slow (1-5m)       │
│ No Unity LLM     │ No Unity LLM     │ LLMUnity/GGUF     │
│                  │                  │ or HTTP API       │
└──────────────────┴──────────────────┴───────────────────┘
```

## Backend Switching

`PlayModeProductionLikeLlmFactory` automatically chooses the backend:

1. **Auto** (default): tries LLMUnity -> HTTP
2. **LLMUnity**: local GGUF model only
3. **HTTP**: OpenAI-compatible API only (LM Studio)

Switch through `COREAI_PLAYMODE_LLM_BACKEND`:
- `auto` or empty -> Auto
- `llmunity`, `local`, `gguf` -> LLMUnity
- `http`, `openai`, `openai_http` -> HTTP API

## Expected Results

### Memory Tool Test
- ✅ Write: memory is saved to IAgentMemoryStore
- ✅ Append: new memory is appended to existing memory
- ✅ Clear: memory is fully removed

### Execute Lua Tool Test
- ✅ Programmer calls execute_lua tool
- ✅ Lua code executes
- ✅ Command is published to MessagePipe

### Crafting Memory Test
- ✅ Craft 1: memory is saved
- ✅ Craft 2: the model sees memory from Craft 1
- ✅ All crafts are unique
- ✅ Craft 4 (repeat) repeats Craft 2 (determinism)

## Troubleshooting

### Configuring Programmer Retry Limit

Default: **3 attempts** after a Lua error. You can change it:

```csharp
// Before system initialization:
CoreAISettings.MaxLuaRepairRetries = 5; // Increase to 5
```

### LLMUnity Does Not Load
- Check that the GGUF model exists
- Increase the timeout in `EnsureLlmUnityModelReady`
- Check Unity logs for LLM errors

### HTTP API Does Not Respond
- Make sure LM Studio is running
- Check `COREAI_OPENAI_TEST_BASE` (must end with `/v1`)
- Check that the model is loaded in LM Studio

### Tool Calls Are Not Recognized
- Check the JSON format: `{"name": "...", "arguments": {...}}`
- Check that tools are passed in `LlmCompletionRequest.Tools`
- Enable logging for `GameLogFeature.Llm`
