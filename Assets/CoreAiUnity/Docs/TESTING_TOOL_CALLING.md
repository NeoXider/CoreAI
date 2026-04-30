# Testing the unified tool-calling pipeline

Quick reference: which tests to run after touching `MeaiLlmClient`, `SmartToolCallingChatClient`, `ToolExecutionPolicy`, or `LlmToolCallTextExtractor`.

---

## TL;DR — what each layer covers

| Layer | What it asserts | Tests |
|------|------|------|
| **Pure parser** | brace counting, code-block exclusion, multi-match | `TryExtractToolCallsFromTextTests` (`MeaiLlmClientEditModeTests.cs`) |
| **Portable extractor** | `LlmToolCallTextExtractor.TryExtract` / `StripForDisplay` | `ToolCallExtractionParityEditModeTests.PortableExtractor_*` |
| **Non-streaming** | text-shape JSON executes, gets stripped, traces populated | `ToolCallExtractionParityEditModeTests.NonStreaming_*` |
| **Streaming** | text-shape JSON executes when bound; stripped when unbound; traces on `IsDone` | `ToolCallExtractionParityEditModeTests.Streaming_*` + `MeaiLlmClientEditModeTests.CompleteStreamingAsync_*` |
| **Per-call log** | `[ToolCall] traceId=… role=… tool=… status=OK dur=… args=… result=…` | `ToolCallExtractionParityEditModeTests.NonStreaming_PerCallLogLine_*` |
| **Summary log** | `tools=[name(ok,12ms),…]` tail on `LLM ◀` line | `ToolCallExtractionParityEditModeTests.FormatExecutedTools_*` |
| **End-to-end (player frame)** | full pipeline on `MeaiLlmClient` + memory store + log assertions | `ToolCallStreamingParityPlayModeTests.*` |
| **Real LLM (opt-in)** | live HTTP / LLMUnity tool execution | `MerchantWithToolCallingPlayModeTests`, `AllToolCallsPlayModeTests` |

---

## Run from Unity

**EditMode** — Window → General → Test Runner → EditMode tab → run any of:

- `ToolCallExtractionParityEditModeTests` ← **the canonical regression suite for the 1.3.0 fix**
- `MeaiLlmClientEditModeTests` (unchanged, validates the parser)
- `SmartToolCallingChatClientEditModeTests` (unchanged, validates retry/duplicate behaviour)
- `ToolExecutionPolicyEditModeTests`

**PlayMode** — Test Runner → PlayMode tab:

- `ToolCallStreamingParityPlayModeTests` ← stream + non-stream parity in a player frame
- `MerchantWithToolCallingPlayModeTests` (real LLM, set `COREAI_PLAYMODE_LLM_BACKEND`)
- `AllToolCallsPlayModeTests` (real LLM, broader coverage)

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

## What each test pins down (in plain English)

### `Streaming_TextShapedToolCall_ExecutesAndStripsFromChunks` (PlayMode)
The production bug. A model emits `Hi! {"name":"memory","arguments":{...}}`. After the fix:
1. `MemoryTool.ExecuteAsync` actually runs — the in-memory store has the new content.
2. The user-visible chunks contain `Hi!` and `Saved.` but **never** the JSON.
3. The terminal `IsDone` chunk reports `ExecutedToolCalls = [memory(ok,…)]`.
4. A `[ToolCall] traceId=play-stream-1 role=Teacher tool=memory status=OK …` line lands in the log stream.

### `NonStreaming_TextShapedToolCall_ExecutesAndStripsFromContent` (PlayMode)
Same, for the non-streaming path. Pre-fix, `SmartToolCallingChatClient` only checked native `FunctionCallContent` — text-shape JSON was invisible. Post-fix the loop runs identical extraction → execution → strip cycle as the streaming path.

### `Streaming_RequestedButNotBound_StripsJsonAndEmitsClean` (EditMode)
Reproduces the original symptom: `MemoryLlmTool` is in `request.Tools`, but `BuildAIFunctions` dropped it because `_memoryStore == null`. Pre-fix the streaming gate `aiTools.Count > 0` skipped extraction → JSON leaked into chat. Post-fix the strip still runs, a `source=missing` synthetic trace is recorded, and a warning is logged.

### `NonStreaming_PerCallLogLine_IsEmittedWhenLogToolCallsEnabled` (EditMode)
Pins the `[ToolCall]` log shape (`traceId=… role=… tool=… status=OK/FAIL dur=…ms args=… result=…`). This is the per-call diagnostic the user wanted — independent of the verbose `LogMeaiToolCallingSteps` switch.

### `FormatExecutedTools_RendersStableLine` (EditMode)
Pins the summary tail appended to `LLM ◀`: `tools=[memory(ok,12ms),missing_tool(fail,0ms,missing),memory(fail,0ms,duplicate)]`.

### Multi-call edge cases (1.3.1)

| Test | What it pins |
|------|------|
| `NonStreaming_ChainOfTwoToolsThenText_ExecutesBothAndStripsAll` | `tool_a → tool_b → text` chain across 3 iterations; both run once; final reply has no leaked JSON; trace list is `[tool_a(ok), tool_b(ok)]` in order. |
| `NonStreaming_TwoParallelToolCalls_BothExecuteInSameIteration` | Two native `FunctionCallContent` in **one** response → both execute via `ExecuteBatchAsync` in a single LLM iteration; both traces marked `source=native`. |
| `NonStreaming_NativeToolCallWithTextPrefix_NativeWins_TextNotLeaked` | Response has `TextContent` (with pseudo-JSON) + real `FunctionCallContent`. Native path takes priority — no phantom tool call is invented from the text. |
| `Streaming_FailureThenSuccess_ResetsConsecutiveErrorsAndContinues` | Tool fails (iter 1), succeeds (iter 2), text reply (iter 3). The success resets the consecutive-error counter, so the third turn doesn't trip the max-errors guard. Final chunk carries `[fail, success]` traces. |

---

## Manual smoke check (no test runner)

1. Set `CoreAISettingsAsset.LogToolCalls = true` and `LogToolCallArguments = true`.
2. Run a chat turn that triggers a tool — even with a local Ollama / LM Studio model.
3. Look for these three log lines in order:

   ```
   LLM ▶ (stream) traceId=…
   [ToolCall] traceId=… role=… tool=memory status=OK dur=12ms args={"action":"append",…}
   LLM ◀ (stream) traceId=… … | tools=[memory(ok,12ms)]
     content (…): <free of "name":"memory"…>
   ```

4. Confirm the chat panel does not display the JSON. Confirm `ApplyAiGameCommand.JsonPayload` (router log) does not contain `"name":"memory"`.

If any of these assertions fail at runtime, the corresponding EditMode/PlayMode test would also fail — they're the same invariants.
