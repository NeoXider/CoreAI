# Tokens Per Second Fix Plan

> **Implemented status (2026-07-01).** The core intent of this plan has landed. Honest relabeling is done:
> the report/formatter no longer calls the metric "decode" or "LM Studio comparable" — it renders
> `tok/s provider-call (prefill+decode)` alongside the effective end-to-end rate
> (`BenchmarkReportFormatter.cs`), and the `GenerationMs` / `GenerationTokensPerSecond` XML docs now spell
> out "prefill + decode, NOT decode-only" (`BenchmarkReport.cs`, `GameCreationBenchmarkHarness.cs`). The
> LM-Studio-comparable decode-only measurement exists as a live TTFT-based probe
> (`Assets/CoreAiUnity/Tests/PlayMode/LlmVerification/TokensPerSecondPlayModeTests.cs`). What was **not**
> adopted verbatim: the field/JSON renames to `ProviderCallMs` / `providerCallCompletionTokensPerSecond` /
> `effectiveCompletionTokensPerSecond` (the existing `Generation*` names were kept, with clarified docs),
> and the `ChooseCompletionTokens` source-marker refactor. The design below is retained as reference for
> any future field-migration work.

## Scope

This is a read-only analysis of the current benchmark token/throughput reporting. The only intended output is this plan. No C# files were changed.

Files inspected:

- `Assets/CoreAiUnity/Tests/PlayMode/LlmVerification/Benchmarks/GameCreationBenchmarkHarness.cs`
- `Assets/CoreAI/Runtime/Core/Features/Benchmarking/BenchmarkReport.cs`
- `Assets/CoreAI/Runtime/Core/Features/Benchmarking/BenchmarkReportFormatter.cs`
- `Assets/CoreAI/Runtime/Core/Features/Benchmarking/ScenarioResult.cs`
- `Assets/CoreAI/Runtime/Core/Features/Orchestration/ILlmClient.cs`
- `Assets/CoreAiUnity/Tests/PlayMode/LlmVerification/Benchmarks/GameCreationBenchmarkPlayModeTests.cs`
- `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/RoutingLlmClient.cs`

## Current Findings

1. `GenerationMs` is not decode-only.

   In `GameCreationBenchmarkHarness.SessionCapturingLlmClient`, `GenerationMs` is measured around the whole `ILlmClient.CompleteAsync` call:

   - start: immediately before `_inner.CompleteAsync(request, cancellationToken)`
   - stop: immediately after the task returns

   For streaming, it wraps the whole stream enumeration:

   - start: before `await foreach`
   - stop: after the stream completes

   That means the current metric is "provider call wall time" for each model turn. It includes request dispatch, provider queue time if any, prompt processing/prefill, and decode. It correctly excludes Unity tool execution, grading, screenshot capture, and outer orchestration gaps, but it does not isolate decode.

2. Existing comments and labels are too strong.

   The current comments in both `GameCreationBenchmarkHarness.cs` and `BenchmarkReport.cs` describe completion-tokens divided by `GenerationMs` as "decode" and "comparable to LM Studio". That is inaccurate for non-streaming calls and likely inaccurate for the current benchmark because `GameCreationBenchmarkPlayModeTests.cs` says streaming is forced off for determinism.

3. CoreAI can measure TTFT only on the streaming path at the current abstraction.

   `ILlmClient.CompleteAsync` returns only a final `LlmCompletionResult` with optional usage. It does not expose:

   - provider-side prompt/prefill duration;
   - first token timestamp;
   - provider-side decode duration;
   - per-token timestamps.

   `ILlmClient.CompleteStreamingAsync` returns incremental `LlmStreamChunk` objects. A decorator can record time from request start to the first meaningful streamed output chunk as TTFT. Then it can compute an approximate post-TTFT duration:

   - `ProviderCallMs = call end - call start`
   - `TimeToFirstTokenMs = first output chunk timestamp - call start`
   - `PostTtftMs = call end - first output chunk timestamp`

   This post-TTFT duration is the closest CoreAI can measure without provider-side telemetry. It is still not perfectly equal to LM Studio's decode tok/s because the first chunk may contain more than one token, tool JSON may be buffered/hidden by CoreAI streaming policy, and terminal usage can arrive after decode completes. Still, it is much closer than total-call timing.

4. True LM Studio-comparable decode-only timing requires provider telemetry or a controlled streaming benchmark.

   To match LM Studio, CoreAI needs one of these:

   - provider-side timings from LM Studio/OpenAI-compatible responses if exposed by the local server;
   - a streaming benchmark mode that records TTFT and reports completion tokens divided by post-TTFT stream time as "estimated decode tok/s";
   - a separate one-shot raw HTTP/SSE probe against the same backend that measures stream TTFT and final completion outside the agentic tool loop.

   The current non-streaming agentic benchmark can only report total-call throughput honestly.

## Required Terminology Fix

Rename and relabel the current metric before adding any new timing mode. The current number is useful, but it must not be called decode-only.

Recommended field semantics:

- `ProviderCallMs`: sum of wall-clock time inside LLM provider calls, including prefill and decode, excluding tools and orchestration gaps.
- `ProviderCallCompletionTokensPerSecond`: `TotalCompletionTokens / ProviderCallMs`.
- `EffectiveCompletionTokensPerSecond`: `TotalCompletionTokens / TotalLatencyMs`, across the full agentic scenario, including tools and orchestration.
- `DecodeCompletionTokensPerSecond`: only present when a decode/post-TTFT duration is actually measured.
- `DecodeTimingKnown`: true only when streaming/provider telemetry produced a decode/post-TTFT denominator.
- `TimeToFirstTokenMs`: measured only on streaming/provider-telemetry paths.
- `DecodeMs` or `PostTtftMs`: measured only on streaming/provider-telemetry paths.

Recommended UI/report labels:

- Replace "`tok/s decode`" with "`tok/s provider-call`" for the current metric.
- Keep "`effective ... across the agentic session`" for the end-to-end metric.
- If a streaming decode estimate is later added, show it separately as "`tok/s decode estimate`" or "`tok/s post-TTFT`".
- Do not say "LM Studio-comparable" unless the denominator excludes prompt processing/prefill.

Recommended JSON field migration:

- Keep old fields temporarily for compatibility:
  - `totalGenerationMs`
  - `generationTokensPerSecond`
- Add explicit replacement fields:
  - `totalProviderCallMs`
  - `providerCallCompletionTokensPerSecond`
  - `effectiveCompletionTokensPerSecond`
  - optional `totalTimeToFirstTokenMs`
  - optional `totalDecodeMs`
  - optional `decodeCompletionTokensPerSecond`
  - `decodeTimingKnown`
- Mark old fields in comments/docs as deprecated aliases for provider-call timing, not decode timing.

## Token Count Guard Issue

Current code around the token-counting block does this when provider usage exists:

```csharp
promptTokens = capture.ProviderPromptTokens;
completionTokens = Math.Max(capture.ProviderCompletionTokens, estCompletion);
```

This can inflate reported token counts.

Why:

- Provider completion tokens and the local BPE estimate may use different tokenizers.
- `CompletionTextForEstimate()` includes assistant text plus tool names and `tool.Detail`.
- `tool.Detail` is described as "Tool result or failure detail" in `LlmToolCallTrace`, not only model-emitted tool-call arguments.
- For native tool calls, provider completion usage may count serialized function-call output differently than the local text estimate.
- If `estCompletion` includes tool results or CoreAI-expanded diagnostic text, `Math.Max(provider, estimate)` can overstate completion tokens and therefore inflate throughput if the denominator is unchanged.

The guard was trying to avoid under-reporting when local streaming backends report too few completion tokens on tool-call turns. That concern is valid, but using the max as canonical makes the source ambiguous and can silently mix tokenizers.

Safer replacement:

1. Store both values:
   - `ProviderCompletionTokens`
   - `EstimatedCompletionTokens`
   - `CompletionTokensForScoring`
   - `CompletionTokenSource`

2. Use provider counts as canonical only when they look complete.

   Suggested rule:

   - If no provider usage exists, use estimate and mark source `Estimated`.
   - If provider usage exists and `estCompletion <= 0`, use provider and mark source `Provider`.
   - If provider completion is zero or suspiciously tiny while the captured model output/tool-call text is non-empty, use estimate and mark source `EstimatedProviderUnderreported`.
   - If provider completion is present and not suspicious, use provider and keep the estimate only for diagnostics.

3. Define "suspiciously tiny" conservatively.

   A simple rule:

   - `providerCompletionTokens <= 0 && estCompletion > 0`
   - or `providerCompletionTokens < Math.Min(16, estCompletion / 4)` when model output/tool-call traces are non-empty

   Avoid `Math.Max` for normal drift. If provider says 950 and estimate says 1020, keep provider and expose estimate drift. If provider says 3 and estimate says 900, use the estimate with a clear source marker.

4. Do not use tool result text as generated text.

   Audit `CompletionTextForEstimate()`. If `LlmToolCallTrace.Detail` can contain tool results, split the trace into model-authored tool arguments versus tool execution result, or estimate only from assistant content plus model-authored function-call name/arguments.

## Minimal Code Diffs To Apply Later

Do not do all of this as a rewrite. Apply in small steps.

### Step 1: Honest naming without behavior change

Files:

- `Assets/CoreAiUnity/Tests/PlayMode/LlmVerification/Benchmarks/GameCreationBenchmarkHarness.cs`
- `Assets/CoreAI/Runtime/Core/Features/Benchmarking/ScenarioResult.cs`
- `Assets/CoreAI/Runtime/Core/Features/Benchmarking/BenchmarkReport.cs`
- `Assets/CoreAI/Runtime/Core/Features/Benchmarking/BenchmarkReportFormatter.cs`

Minimal changes:

1. Rename comments first:
   - `GenerationMs` comment becomes "wall-clock spent inside provider LLM calls, including prefill and decode, excluding tool execution and grading."
   - remove "decode-only" and "LM Studio comparable" from current comments.

2. Add alias properties in `BenchmarkReport`:
   - `TotalProviderCallMs => TotalGenerationMs`
   - `ProviderCallCompletionTokensPerSecond => GenerationTokensPerSecond`
   - `EffectiveCompletionTokensPerSecond => EffectiveTokensPerSecond`

   Keep `GenerationTokensPerSecond` temporarily as a compatibility alias, but update its XML comment to say it is provider-call throughput, not decode throughput.

3. In Markdown:
   - line currently rendering `{GenerationTokensPerSecond} tok/s decode` should render `{ProviderCallCompletionTokensPerSecond} tok/s provider-call`.
   - keep effective text, but label as "`effective ... full agentic session`".
   - add one note near the scorecard: "Provider-call tok/s includes prompt prefill; LM Studio's tok/s may be decode-only."

4. In SVG:
   - footer currently renders `{GenerationTokensPerSecond} tok/s`; change to `{ProviderCallCompletionTokensPerSecond} tok/s provider-call`.

5. In JSON:
   - add `totalProviderCallMs`
   - add `providerCallCompletionTokensPerSecond`
   - add `effectiveCompletionTokensPerSecond`
   - keep `totalGenerationMs` and `generationTokensPerSecond` for one release as deprecated aliases.

Risk:

- Very low. It changes labels and adds aliases, but does not change scoring or benchmark numbers.

### Step 2: Make token-source reporting safe

Files:

- `ScenarioResult.cs`
- `GameCreationBenchmarkHarness.cs`
- `BenchmarkReportFormatter.cs`

Minimal changes:

1. Add fields to `ScenarioResult`:
   - `public int EstimatedCompletionTokens { get; set; }`
   - `public int? ProviderCompletionTokens { get; set; }` already exists
   - `public string CompletionTokenSource { get; set; } = "unknown";`
   - optionally `public bool CompletionTokenProviderUnderreported { get; set; }`

2. Replace `Math.Max(capture.ProviderCompletionTokens, estCompletion)` with a small helper:

```csharp
private static int ChooseCompletionTokens(
    int providerCompletionTokens,
    int estimatedCompletionTokens,
    bool hasModelGeneratedText,
    out string source,
    out bool providerUnderreported)
{
    providerUnderreported = false;

    if (providerCompletionTokens <= 0 && estimatedCompletionTokens > 0 && hasModelGeneratedText)
    {
        source = "estimated_provider_underreported";
        providerUnderreported = true;
        return estimatedCompletionTokens;
    }

    if (estimatedCompletionTokens >= 64
        && providerCompletionTokens > 0
        && providerCompletionTokens < estimatedCompletionTokens / 4
        && hasModelGeneratedText)
    {
        source = "estimated_provider_underreported";
        providerUnderreported = true;
        return estimatedCompletionTokens;
    }

    source = "provider";
    return providerCompletionTokens;
}
```

3. For no provider usage:

```csharp
completionTokens = estCompletion;
completionTokenSource = "estimated";
```

4. Use the same source marker in Markdown token display:
   - provider: `1234`
   - estimated: `~1234`
   - estimated because provider underreported: `~1234 (provider reported 3)`

5. Add JSON fields:
   - `estimatedCompletionTokens`
   - `completionTokenSource`
   - `completionTokenProviderUnderreported`

Risk:

- Low to medium. Scoring can change for runs where the previous `Math.Max` inflated completion tokens. That is desired, but historical comparisons need a note.

### Step 3: Optional TTFT/post-TTFT timing path

Files:

- `GameCreationBenchmarkHarness.cs`
- `ScenarioResult.cs`
- `BenchmarkReport.cs`
- `BenchmarkReportFormatter.cs`
- possibly `GameCreationBenchmarkPlayModeTests.cs` if the benchmark gets an opt-in streaming timing mode

Minimal changes:

1. Extend `SessionCapturingLlmClient` with:
   - `ProviderCallMs` or keep `GenerationMs` as legacy
   - `TimeToFirstTokenMs`
   - `PostTtftMs`
   - `DecodeTimingKnown`

2. In `CompleteStreamingAsync`, record:

```csharp
long t0 = Stopwatch.GetTimestamp();
long first = 0;
await foreach (LlmStreamChunk chunk in _inner.CompleteStreamingAsync(request, cancellationToken).ConfigureAwait(false))
{
    if (first == 0 && IsMeaningfulFirstOutput(chunk))
    {
        first = Stopwatch.GetTimestamp();
    }
    ...
}
long end = Stopwatch.GetTimestamp();
ProviderCallMs += ElapsedMs(t0, end);
if (first != 0)
{
    TimeToFirstTokenMs += ElapsedMs(t0, first);
    PostTtftMs += ElapsedMs(first, end);
    DecodeTimingKnown = true;
}
```

3. Define `IsMeaningfulFirstOutput(chunk)` carefully:
   - `!string.IsNullOrEmpty(chunk.Text)` is useful for prose.
   - For tool-only turns where text is buffered/hidden, first visible text may never arrive. This means decode timing is unknown unless the lower-level stream exposes raw deltas before CoreAI strips/buffers tool JSON.
   - Do not treat terminal usage-only chunks as first token.

4. Add report aggregate:

```csharp
public double DecodeCompletionTokensPerSecond =>
    TotalDecodeMs > 0 ? TotalCompletionTokens / (TotalDecodeMs / 1000.0) : 0;
```

5. Only show the decode estimate when `DecodeTimingKnown` is true for enough measured turns. Otherwise show "decode timing unavailable (non-streaming/provider telemetry absent)".

Risk:

- Medium. Streaming changes can affect deterministic benchmark behavior, tool-call buffering, and token accounting. Keep this opt-in and do not change default scoring.

## Direct Answers To The Requested Questions

### 1. Can CoreAI measure TTFT / decode-only duration to match LM Studio, or only total-call time?

Currently:

- Non-streaming benchmark path: only total provider-call time is measurable. This includes prefill and decode.
- Streaming path: CoreAI can measure TTFT from call start to first meaningful streamed chunk, and can estimate post-TTFT duration. That is closer to decode-only but not guaranteed to exactly match LM Studio.
- Exact LM Studio-comparable decode-only timing requires provider-side telemetry or a raw streaming probe where first-token and final-token events are measured without CoreAI tool buffering hiding the first generated tokens.

### 2. Exact label/field changes for honesty

Use these names:

- Current `GenerationMs`: label as `ProviderCallMs` in reports/docs.
- Current `GenerationTokensPerSecond`: label as `ProviderCallCompletionTokensPerSecond`.
- Current `EffectiveTokensPerSecond`: label as `EffectiveCompletionTokensPerSecond`.
- Future streaming/provider telemetry metric: label as `DecodeCompletionTokensPerSecond` or `PostTtftCompletionTokensPerSecond`.

User-visible labels:

- "`tok/s provider-call (includes prefill)`"
- "`effective tok/s across full agentic session`"
- "`decode estimate tok/s (post-TTFT, streaming only)`"

Avoid:

- "`tok/s decode`" for the current metric.
- "`LM Studio comparable`" for any metric whose denominator includes prefill.

### 3. Can the `max(provider, estimate)` guard inflate counts and how to make it safe?

Yes. It can inflate counts whenever the local estimate is larger than provider usage because of tokenizer drift, tool-call serialization differences, or inclusion of tool trace detail that was not model-generated completion text.

Make it safe by replacing unconditional `Math.Max` with a source-aware guard:

- trust provider counts for normal drift;
- use estimate only when provider completion usage is absent, zero, or implausibly tiny relative to non-empty captured model output;
- store both provider and estimate values;
- render the token source explicitly in Markdown and JSON.

### 4. Minimal code diffs

Apply in this order:

1. Rename comments and formatter labels from "decode" to "provider-call".
2. Add explicit report alias properties and JSON fields with honest names.
3. Replace `Math.Max` with `ChooseCompletionTokens(...)`.
4. Add token-source fields to `ScenarioResult` and formatter output.
5. Optionally add streaming TTFT/post-TTFT fields behind an opt-in benchmark mode.

Do not change benchmark scoring in Step 1. Token scoring may change only in Step 2 when the unsafe max guard is replaced.

### 5. Regression tests to add

Add EditMode tests under the existing `Assets/CoreAiUnity/Tests/EditMode` assembly, which already references `CoreAI.Benchmarking`.

Recommended tests:

1. `BenchmarkReportFormatterEditModeTests.Markdown_LabelsCurrentThroughputAsProviderCallNotDecode`
   - Build a `BenchmarkReport` with one `ScenarioResult`.
   - Assert Markdown contains `provider-call`.
   - Assert Markdown does not contain `tok/s decode`.
   - Assert Markdown mentions prefill or non-LM-Studio comparability.

2. `BenchmarkReportFormatterEditModeTests.Json_EmitsExplicitProviderCallAndEffectiveFields`
   - Build a report with `CompletionTokens = 100`, `GenerationMs = 10000`, `LatencyMs = 20000`.
   - Assert JSON contains:
     - `"totalProviderCallMs":10000`
     - `"providerCallCompletionTokensPerSecond":10`
     - `"effectiveCompletionTokensPerSecond":5`
   - Keep old JSON fields present if compatibility is retained.

3. `BenchmarkReportEditModeTests.ProviderCallThroughput_UsesCompletionTokensOverProviderCallMs`
   - Same numeric fixture.
   - Assert provider-call throughput is 10.
   - Assert effective throughput is 5.

4. `GameCreationBenchmarkTokenSelectionEditModeTests.ProviderUsageWinsForNormalEstimateDrift`
   - provider completion 950, estimate 1020, generated output non-empty.
   - Assert chosen completion tokens are 950.
   - Assert source is `provider`.

5. `GameCreationBenchmarkTokenSelectionEditModeTests.EstimateWinsWhenProviderUnderreportsToolTurn`
   - provider completion 3, estimate 900, generated output/tool-call trace non-empty.
   - Assert chosen completion tokens are 900.
   - Assert source is `estimated_provider_underreported`.

6. `GameCreationBenchmarkTokenSelectionEditModeTests.NoProviderUsesEstimateAndMarksApproximate`
   - no provider usage, estimate 700.
   - Assert chosen completion tokens are 700.
   - Assert source is `estimated`.

7. Optional streaming test if Step 3 is implemented:
   - fake `ILlmClient` streams after a controlled delay, then emits text chunks, then a terminal usage chunk.
   - assert `TimeToFirstTokenMs > 0`.
   - assert `PostTtftMs > 0`.
   - assert decode timing is unavailable when only a terminal usage chunk is emitted.

Verification after implementation:

- Run targeted EditMode tests for the new formatter/token selection tests.
- Run the benchmark artifact writer once with a stubbed or single-scenario path if available.
- For real LLM benchmark runs, compare:
  - provider-call tok/s: expected lower on huge prompts;
  - effective tok/s: lower than provider-call;
  - decode estimate: present only when streaming/provider telemetry is enabled.

## Recommended Final Fix Order

1. Ship honest labels first. This immediately stops the misleading LM Studio comparison without changing benchmark data.
2. Replace the token-count `Math.Max` guard with source-aware selection and explicit source reporting.
3. Add optional streaming TTFT/post-TTFT instrumentation only after the report is honest, and keep it opt-in until tool-call streaming behavior is verified.

