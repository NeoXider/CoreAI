# Testing the unified tool-calling pipeline

Which tests cover tool calling, and which to run after touching `MeaiLlmClient`,
`SmartToolCallingChatClient`, `ToolExecutionPolicy`, `LlmToolCallTextExtractor`, `MemoryTool` or the
`execute_lua` tool. The rules every test follows (no coaching, prove tool traces, timeouts, assembly
layout) are in [Tests/README.md](../Tests/README.md). Tool-call JSON shapes and tool contracts are in
[TOOL_CALL_SPEC.md](TOOL_CALL_SPEC.md).

---

## What each layer covers

| Layer | What it asserts | Tests |
|------|------|------|
| **Pure parser** | brace counting, code-block exclusion, multi-match, cited examples are not calls | `TryExtractToolCallsFromTextTests` (in `MeaiLlmClientEditModeTests.cs`) |
| **Hybrid streaming helpers** | prose before and after a tool call keeps streaming (`GetHybridSafeSegments` / `GetHybridUnemittedSuffix`) | `HybridSafeSegmentsTests` (in `MeaiLlmClientEditModeTests.cs`) |
| **Portable extractor** | `LlmToolCallTextExtractor.TryExtract` / `StripForDisplay` | `ToolCallExtractionParityEditModeTests.PortableExtractor_*` |
| **Non-streaming** | text-shape JSON executes, gets stripped, traces populated | `ToolCallExtractionParityEditModeTests.NonStreaming_*` |
| **Streaming** | text-shape JSON executes when bound and is stripped when unbound; traces on `IsDone`; tool loop cap | `ToolCallExtractionParityEditModeTests.Streaming_*` + `MeaiLlmClientEditModeTests.CompleteStreamingAsync_*` |
| **Per-call log** | `[ToolCall] traceId=… role=… tool=… status=OK dur=… args=… result=…` | `ToolCallExtractionParityEditModeTests.NonStreaming_PerCallLogLine_*` |
| **Summary log** | `tools=[name(ok,12ms),…]` tail on the `LLM <` line | `ToolCallExtractionParityEditModeTests.FormatExecutedTools_*` |
| **Execution policy** | cross-turn echo no-op, per-tool / global `AllowDuplicates`, consecutive-error counter, batch execution | `ToolExecutionPolicyEditModeTests` |
| **Tool loop** | retry and duplicate behaviour of the MEAI tool loop | `SmartToolCallingChatClientEditModeTests` |
| **Memory and Lua tools** | `MemoryTool` actions and versions; the `execute_lua` AIFunction | `MeaiToolCallsEditModeTests`; the Lua runtime behind `execute_lua`: `LuaToolEditModeTests`, `LuaCsModRuntimeEditModeTests` (`CoreAI.Mods.Tests`) |
| **MessagePipe events** | LLM broker events, streaming/non-streaming parity, child-scope subscriptions | `MessagePipeEventPublishingEditModeTests` |
| **Fast PlayMode (stub inner client)** | `MeaiLlmClient` tool pipeline in a player frame, no loaded model | `ToolCallStreamingParityPlayModeTests` (`CoreAI.Tests.PlayMode.FastNoLlm`) |
| **Real LLM (opt-in)** | live HTTP / LLMUnity tool execution | see [Live PlayMode tests](#live-playmode-tests) |

Unless noted, EditMode tests are in `Assets/CoreAiUnity/Tests/EditMode/` (assembly `CoreAI.Tests`).

---

## Key EditMode cases

### `MemoryTool` and `execute_lua` AIFunctions (`MeaiToolCallsEditModeTests.cs`)

| Test | What it tests |
|------|---------------|
| `MemoryTool_CreateAIFunction_ReturnsNonNull` | AIFunction creation for memory |
| `MemoryTool_ExecuteAsync_Write_SavesMemory` | Memory write |
| `MemoryTool_ExecuteAsync_Append_AppendsToExisting` | Append to memory |
| `MemoryTool_ExecuteAsync_Read_ReturnsCurrentMemoryWithoutSaving` | Read does not save |
| `MemoryTool_ExecuteAsync_Clear_RemovesMemory` | Clear memory |
| `MemoryTool_ExecuteAsync_StrReplace_ReplacesFirstExactMatch`, `MemoryTool_ExecuteAsync_Insert_AddsContentAfterAnchorLine`, `MemoryTool_ExecuteAsync_Delete_RemovesFirstExactBlock`, `MemoryTool_ExecuteAsync_Rename_RenamesFirstLeadingKeyLabel` | Granular memory edits |
| `MemoryTool_ExecuteAsync_Versioning_RevertRestoresPriorSnapshot` | Memory versions and revert |
| `LuaTool_CreateAIFunction_ReturnsNonNull` | AIFunction creation for Lua |
| `LuaTool_ExecuteAsync_EmptyCode_ReturnsError` | Empty-code validation |
| `LuaTool_ExecuteAsync_ValidCode_CallsExecutor` | The executor receives the code |

### Streaming tool cycle (`MeaiLlmClientEditModeTests.cs`)

| Test | What it tests |
|------|---------------|
| `CompleteStreamingAsync_ToolJsonInStream_ExecutesToolAndReturnsFinalText` | Tool JSON in the stream → execute → continued text |
| `CompleteStreamingAsync_ToolJsonWithVisiblePrefix_KeepsPrefixAndHidesJson` | Prefix and final text survive; with live SSE the prefix chunk may briefly contain JSON before extraction |
| `CompleteStreamingAsync_TooManyToolIterations_ReturnsTerminalError` | An endless streaming tool loop ends with a terminal error |

### Text-shape parser (`TryExtractToolCallsFromTextTests` in `MeaiLlmClientEditModeTests.cs`)

| Test | What it tests |
|------|---------------|
| `SingleToolCall_ExtractedCorrectly` | Single tool call with prefix text |
| `MultipleToolCalls_AllExtracted` | Multiple tool calls in one text |
| `JsonInCodeBlock_NotExtracted` | JSON in a code block is ignored |
| `MalformedJson_GracefullySkipped` | Incomplete JSON does not cause an error |
| `JsonWithoutNameAndArguments_NotExtracted` | JSON without `name` + `arguments` is not a tool call |
| `EmptyText_ReturnsFalse` | Empty string / null → false |
| `NestedBracesInArguments_HandledCorrectly` | Nested JSON objects in arguments |
| `StripCodeBlocks_PreservesPositions` | Code-block stripping preserves positions |
| `IsValidToolCallJson_RequiresBothKeys` | Validation requires `name` + `arguments` |
| `FindToolCallJsonSpans_MultipleSpans` | Finds multiple JSON spans |
| `ToolCallWithStringContainingBraces_HandledCorrectly` | String containing `{}` in arguments |

### `ToolExecutionPolicy` (`ToolExecutionPolicyEditModeTests.cs`)

| Test | What it tests |
|------|---------------|
| `Echo_FirstCall_Executes` | First call is not blocked |
| `Echo_SameSignatureNextTurn_IsNoOpNotExecuted` | A call repeated in a later turn is a no-op |
| `Echo_DifferentArgs_BothExecute` | Different arguments are not a duplicate |
| `Echo_AllowDuplicatesGlobal_NeverSuppresses` | Global `AllowDuplicates = true` |
| `Echo_PerToolAllowDuplicates_Respected` | Per-tool `AllowDuplicates` flag |
| `RecordSuccess_ResetsCounter` | Success resets the error counter |
| `RecordFailure_IncrementsCounter` | Failure increments the counter |
| `IsMaxErrorsReached_AtThreshold_ReturnsTrue` | Error threshold works |
| `Reset_ClearsEverything` | Reset clears both duplicates and the counter |
| `ExecuteSingle_ToolFound_ReturnsResult` | Found tool returns a result |
| `ExecuteSingle_ToolNotFound_ReturnsFailed` | Missing tool → failed |
| `ExecuteBatch_AllSucceed_ResetsErrorCounter` | Batch success resets errors |
| `ExecuteBatch_Echo_ReturnsStructuredNoOp_NotAFailure` | A cross-turn echo in a batch returns a structured no-op, not a failure |
| `ExecuteBatch_IntraBatchIdenticalCalls_CrossTurnEchoStillBlocked` | Identical calls in one batch all run; the echo in a later turn is blocked |

---

## Live PlayMode tests

Live tests skip (`Assert.Ignore`) when no backend is configured. The backend comes from
`PlayModeProductionLikeLlmFactory` (settings asset backend type first); configuration, env vars and the
local config file are described in [RUNNING_LIVE_TESTS.md](RUNNING_LIVE_TESTS.md) and
[Tests/README.md § C4](../Tests/README.md#c4-backends-and-running). Unless noted, these fixtures are
compiled only with `COREAI_LLM` and not on WebGL.

| Test | What it asserts | Notes |
|------|-----------------|-------|
| `AllToolCallsPlayModeTests.AllToolCalls_MemoryTool_WriteAppendClear` | Memory write, append and clear through the `memory` tool reach `IAgentMemoryStore` | Compiled only with the LLMUnity package (`COREAI_HAS_LLMUNITY`) and not on WebGL |
| `MerchantWithToolCallingPlayModeTests.ChatAgent_CallsInventoryTool_ThenRespondsWithItems` (`ChatWithToolCallingPlayModeTests.cs`) | Merchant calls `get_inventory`, then answers with the items | |
| `MultiToolChainPlayModeTests.Creator_OneHint_WorldSpawnThenMemoryWriteThenPlainReply` | One Creator turn spawns through `world_command` and writes a memory marker | |
| `StreamingToolCallingPlayModeTests` | Streaming with tools completes, early cancellation stops cleanly, no state leaks into a following non-streaming call | |
| `ToolNameRepairPlayModeTests` | Wrong-cased tool names are repaired and executed; an unknown name stays visible and is not executed | |
| `ProgrammerLuaModsLivePlayModeTests` | Programmer builds the Tetris mod and repairs a broken mod through `execute_lua` / `manage_mods` | `[Explicit]` |
| `CraftingMemoryViaOpenAiPlayModeTests.CraftingMemoryOpenAi_ThreeCrafts_AllUnique` (Scenarios) | Every craft completes an `execute_lua` call; three crafted names are unique | No compile guard. Determinism variants (`CraftingMemoryOpenAi_TwoCrafts_SecondIsDifferent`, `CraftingMemoryOpenAi_RepeatIngredients_SecondMatchesFirst`) are `[Explicit]` |
| `CraftingMemoryViaLlmUnityPlayModeTests.CraftingMemoryLlmUnity_ThreeCrafts_AllUnique` (Scenarios) | Same craft chain on the local model | `[Explicit]`; ignored unless the resolved backend is LLMUnity |
| `MultiAgentCraftingWorkflowPlayModeTests.MultiAgent_CreatorThenMechanicThenProgrammer_CompleteWorkflow` (Scenarios) | Creator → CoreMechanicAI → Programmer → CoreMechanicAI repeat; isolated role memory | The Programmer step forces `execute_lua` (`ForcedToolMode = LlmToolChoiceMode.RequireSpecific`, `RequiredToolName = "execute_lua"`) |

Some local llama.cpp / LM Studio servers reject a forced-specific `tool_choice` with HTTP 400 while
accepting `"auto"` and `"required"`; against such a backend the forced `execute_lua` step fails for that
reason, not because of the tool pipeline.

The live `execute_lua` check that used to live in `AllToolCallsPlayModeTests` was removed with the legacy
Lua runtime; the Lua-CSharp `execute_lua` path is covered deterministically by `LuaToolEditModeTests` and
`LuaCsModRuntimeEditModeTests`.

---

## Run from Unity

**EditMode:** Window → General → Test Runner → EditMode, then run any of:

- `ToolCallExtractionParityEditModeTests` (text-shape extraction, stripping, traces and logs on both paths)
- `MeaiLlmClientEditModeTests` (parser, hybrid helpers, streaming tool cycle)
- `SmartToolCallingChatClientEditModeTests`
- `ToolExecutionPolicyEditModeTests`
- `MeaiToolCallsEditModeTests`
- `MessagePipeEventPublishingEditModeTests`

**PlayMode:** Test Runner → PlayMode:

- `ToolCallStreamingParityPlayModeTests` (stream and non-stream parity in a player frame, no model)
- the live tests above, filtered by assembly (`CoreAI.Tests.PlayMode.LlmVerification`,
  `CoreAI.Tests.PlayMode.Scenarios`)

---

## Run from CLI

```
Unity.exe -batchmode -nographics -projectPath . \
  -runTests -testPlatform EditMode \
  -testFilter "CoreAI.Tests.EditMode.ToolCallExtractionParityEditModeTests" \
  -testResults TestRun_EditMode.xml -logFile EditMode.log -quit
```

```
Unity.exe -batchmode -nographics -projectPath . \
  -runTests -testPlatform PlayMode \
  -testFilter "CoreAI.Tests.PlayMode.ToolCallStreamingParityPlayModeTests" \
  -testResults TestRun_PlayMode.xml -logFile PlayMode.log -quit
```

---

## What the parity tests pin down

### `Streaming_TextShapedToolCall_ExecutesAndStripsFromChunks` (PlayMode)

A model emits `Hi! {"name":"memory","arguments":{...}}` on a stream. The test asserts:

1. `MemoryTool.ExecuteAsync` runs: the in-memory store holds the new content.
2. The concatenated streamed `Text` contains `Hi!` and `Saved.`. With **live streaming** and **bound**
   AIFunctions, raw tool-shaped JSON may appear in `Text` before `TryExtractToolCallsFromText` runs (as
   with OpenRouter-style deltas), so the test does not require the aggregate stream to omit JSON.
3. The terminal `IsDone` chunk reports `ExecutedToolCalls = [memory(ok,…)]`.
4. A `[ToolCall] traceId=play-stream-1 role=Teacher tool=memory status=OK …` line is logged.

### `NonStreaming_TextShapedToolCall_ExecutesAndStripsFromContent` (PlayMode)

The same for the non-streaming path: `SmartToolCallingChatClient` runs the same
extraction → execution → strip cycle on text-shape JSON as the streaming path, not only on native
`FunctionCallContent`.

### `Streaming_RequestedButNotBound_StripsJsonAndEmitsClean` (EditMode)

`MemoryLlmTool` is in `request.Tools`, but `BuildAIFunctions` drops it because the client has no memory
store. The JSON is still stripped from the visible text (the `Saved!` prefix survives), and the final
chunk records a synthetic `source=missing` trace for the unbound tool.

### `NonStreaming_PerCallLogLine_IsEmittedWhenLogToolCallsEnabled` (EditMode)

Pins the `[ToolCall]` log shape (`traceId=… role=… tool=… status=OK/FAIL dur=…ms args=… result=…`),
independent of the verbose `LogMeaiToolCallingSteps` switch.

### `FormatExecutedTools_RendersStableLine` (EditMode)

Pins the summary tail appended to `LLM <`:
`tools=[memory(ok,12ms),missing_tool(fail,0ms,missing),memory(fail,0ms,duplicate)]`.

### Multi-call edge cases (EditMode)

| Test | What it pins |
|------|------|
| `NonStreaming_ChainOfTwoToolsThenText_ExecutesBothAndStripsAll` | `tool_a → tool_b → text` chain across 3 iterations; both run once; the final reply has no leaked JSON; the trace list is `[tool_a(ok), tool_b(ok)]` in order. |
| `NonStreaming_TwoParallelToolCalls_BothExecuteInSameIteration` | Two native `FunctionCallContent` in **one** response → both execute via `ExecuteBatchAsync` in a single LLM iteration; both traces marked `source=native`. |
| `NonStreaming_NativeToolCallWithTextPrefix_NativeWins_TextNotLeaked` | Response has `TextContent` (with pseudo-JSON) + a real `FunctionCallContent`. The native path takes priority; no phantom tool call is invented from the text. |
| `Streaming_FailureThenSuccess_ResetsConsecutiveErrorsAndContinues` | Tool fails (iteration 1), succeeds (iteration 2), text reply (iteration 3). The success resets the consecutive-error counter, so the third turn does not trip the max-errors guard. The final chunk carries `[fail, success]` traces. |

---

## Manual smoke check (no test runner)

1. On the `CoreAISettings` asset enable **Advanced Settings → Debug → Log Tool Calls** and **Log Arguments**
   (`LogToolCalls`, `LogToolCallArguments`).
2. Run a chat turn that triggers a tool, even with a local Ollama / LM Studio model.
3. Look for these log lines in order:

   ```
   LLM > (stream) traceId=…
   [ToolCall] traceId=… role=… tool=memory status=OK dur=12ms args={"action":"append",…}
   LLM < (stream) traceId=… … | tools=[memory(ok,12ms)]
   ```

   The `LLM <` content preview may still contain tool-shaped JSON when the gateway batched text and JSON
   in one delta; the chat UI may show it briefly during live streaming before the tool pass strips it on
   the next iteration.

4. Confirm that `ApplyAiGameCommand.JsonPayload` (router log) does not contain raw tool JSON in the
   **final** assistant envelope after the tool loop (the same invariant as the orchestrator publish).

If one of these fails at runtime, the corresponding EditMode/PlayMode test fails too; they check the same
invariants.

---

## Gotcha: two logging interfaces

Tool-call diagnostics use two logging interfaces:

- **`ILog`** (portable, `Log.Instance`): used by `ToolExecutionPolicy` for the `[ToolCall]` per-call lines.
- **`IGameLogger`** (Unity): used by `MeaiLlmClient` for `MeaiLlmClient: ...` infrastructure lines.

In production both write to the same console through `UnityLog`. In a **PlayMode test**, a `SpyLogger`
that implements only `IGameLogger` never sees the `[ToolCall]` lines; they go to `NullLog`.

**Fix:** make the spy implement both `IGameLogger` and `ILog`, set `Log.Instance = spy` before invoking
the pipeline, and reset `Log.Instance = NullLog.Instance` in `[TearDown]` (see
`ToolCallStreamingParityPlayModeTests`).

---

## When a live tool test fails

- **Tool failure limit.** A failed `execute_lua` call returns the Lua error to the model, which may retry
  in the same turn. The loop stops after **Tool Call Retries** consecutive all-failed tool batches (default
  **3**; `CoreAISettings` asset → Advanced Settings → General → Retry limits, field `maxToolCallRetries`).
  **Lua Repair Retries** (`MaxLuaRepairRetries`) applies only to the opt-in `LuaCsAiEnvelopeProcessor`
  repair path.
- **LLMUnity does not load.** Check that the GGUF model exists and the path in the settings asset is
  right; `EnsureLlmUnityModelReady` waits up to 120 s for the model.
- **HTTP API does not respond.** Make sure LM Studio (or the provider) is running with the model loaded
  and that `COREAI_TEST_BASE_URL` ends with `/v1`.
- **Tool calls are not recognized.** Check that the tools are in `LlmCompletionRequest.Tools` and enable
  the `Llm` category (`GameLogFeature.Llm`) in the Game Log Settings asset.

More symptoms and fixes: [TROUBLESHOOTING.md](TROUBLESHOOTING.md#-problem-tool-call-does-not-work).
