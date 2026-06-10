using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Logging;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Wraps <see cref="ILlmClient"/>: trace IDs, call timing,
    /// tokens (if the backend fills <see cref="LlmCompletionResult"/>),
    /// prompt/response previews, and HTTP retry with exponential backoff.
    /// </summary>
    public sealed class LoggingLlmClientDecorator : ILlmClient
    {
        private const int SystemPreviewChars = 1200;
        private const int UserPreviewChars = 1600;
        private const int ResponsePreviewChars = 2400;

        private const int MaxRetryCapSeconds = 30;

        private readonly ILlmClient _inner;
        private readonly ILog _logger;
        private readonly string _backendLabel;
        private readonly float _requestTimeoutSeconds;
        private readonly int _maxHttpRetryAttempts;

        /// <param name="requestTimeoutSeconds">The request timeout seconds value.</param>
        /// <param name="maxHttpRetryAttempts">The max http retry attempts value.</param>
        public LoggingLlmClientDecorator(ILlmClient inner, ILog logger,
            float requestTimeoutSeconds = 0f, int maxHttpRetryAttempts = 0)
        {
            _inner = inner;
            _logger = logger ?? NullLog.Instance;
            _requestTimeoutSeconds = requestTimeoutSeconds < 0f ? 0f : requestTimeoutSeconds;
            _maxHttpRetryAttempts = maxHttpRetryAttempts < 0 ? 0 : maxHttpRetryAttempts;
            _backendLabel = inner?.GetType().Name ?? "?";
        }

        /// <summary>Inner.</summary>
        public ILlmClient Inner => _inner;

        /// <summary>Peels all <see cref="LoggingLlmClientDecorator"/> from the top of the chain.</summary>
        public static ILlmClient Unwrap(ILlmClient client)
        {
            ILlmClient c = client;
            while (c is LoggingLlmClientDecorator d)
            {
                c = d.Inner;
            }

            return c;
        }

        /// <inheritdoc />
        public async Task<LlmCompletionResult> CompleteAsync(
            LlmCompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                _logger.Warn($"LLM | backend={_backendLabel} | request=null", LogTag.Llm);
                return new LlmCompletionResult { Ok = false, Error = "LlmCompletionRequest is null" };
            }

            if (_inner is ILlmPreflightAnnotator routing)
            {
                routing.PreflightAnnotate(request);
            }

            string trace = string.IsNullOrWhiteSpace(request.TraceId) ? "-" : request.TraceId.Trim();
            string role = string.IsNullOrWhiteSpace(request.AgentRoleId)
                ? "(role not set)"
                : request.AgentRoleId.Trim();
            string system = request.SystemPrompt ?? "";
            string user = request.UserPayload ?? "";
            string backendLine = string.IsNullOrWhiteSpace(request.RoutingProfileId)
                ? _backendLabel
                : $"{_backendLabel}->{request.RoutingProfileId.Trim()}";

            _logger.Info(
                $"LLM ▶ traceId={trace} role={role} backend={backendLine}\n" +
                $"  system ({system.Length} chars): {Preview(system, SystemPreviewChars)}\n" +
                $"  user ({user.Length} chars): {Preview(user, UserPreviewChars)}\n" +
                $"  {FormatPromptBudgetLine(system, user, request.Tools)}", LogTag.Llm);

            Stopwatch sw = Stopwatch.StartNew();
            LlmCompletionResult result = null;
            // Timeout is now enforced by the Unity-aware caller (CoreAiChatService)
            // This decorator only handles logging and HTTP 429/5xx retries.
            try
            {
                // WebGL player: keep continuation on Unity SynchronizationContext. See note in
                // browser stack hangs the await chain after HTTP completes, leaving chat UI stuck.
#if UNITY_WEBGL && !UNITY_EDITOR
                result = await _inner.CompleteAsync(request, cancellationToken);
#else
                result = await _inner.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
#endif
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // honour caller cancellation (timeout or user stop)
            }
            catch (LlmClientException httpEx) when (
                IsRetryableHttpError(httpEx, out int httpWait) &&
                _maxHttpRetryAttempts > 0)
            {
                bool exhausted = true;
                for (int attempt = 0; attempt < _maxHttpRetryAttempts; attempt++)
                {
                    int waitSec = httpWait > 0 ? Math.Min(httpWait, MaxRetryCapSeconds) : ComputeBackoff(attempt);
                    _logger.Warn(
                        $"LLM ↺ traceId={trace} role={role} | {httpEx.ErrorCode} - retry {attempt + 1}/{_maxHttpRetryAttempts} after {waitSec}s",
                        LogTag.Llm);
#if UNITY_WEBGL && !UNITY_EDITOR
                    await Task.Delay(TimeSpan.FromSeconds(waitSec), cancellationToken);
                    try
                    {
                        result = await _inner.CompleteAsync(request, cancellationToken);
                        exhausted = false;
                        break;
                    }
#else
                    await Task.Delay(TimeSpan.FromSeconds(waitSec), cancellationToken).ConfigureAwait(false);
                    try
                    {
                        result = await _inner.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
                        exhausted = false;
                        break;
                    }
#endif
                    catch (LlmClientException retryEx) when (IsRetryableHttpError(retryEx, out httpWait))
                    {
                        // will retry again if attempts remain
                    }
                }

                if (exhausted)
                {
                    sw.Stop();
                    string msg = $"{httpEx.ErrorCode} after {_maxHttpRetryAttempts} retries: {httpEx.Message}";
                    _logger.Warn(
                        $"LLM ✖ traceId={trace} role={role} backend={backendLine} | {msg}", LogTag.Llm);
                    return new LlmCompletionResult { Ok = false, Error = msg };
                }
            }

            // apply the same retry policy as for LlmClientException above.
            if (result != null &&
                !result.Ok &&
                IsRetryableFailureResult(result, out int httpWaitFromResult) &&
                _maxHttpRetryAttempts > 0)
            {
                int httpWait = httpWaitFromResult;
                bool exhausted = true;
                for (int attempt = 0; attempt < _maxHttpRetryAttempts; attempt++)
                {
                    int waitSec = httpWait > 0 ? Math.Min(httpWait, MaxRetryCapSeconds) : ComputeBackoff(attempt);
                    _logger.Warn(
                        $"LLM ↺ traceId={trace} role={role} | {result.ErrorCode} - retry {attempt + 1}/{_maxHttpRetryAttempts} after {waitSec}s (failed completion)",
                        LogTag.Llm);
#if UNITY_WEBGL && !UNITY_EDITOR
                    await Task.Delay(TimeSpan.FromSeconds(waitSec), cancellationToken);
                    try
                    {
                        result = await _inner.CompleteAsync(request, cancellationToken);
                        if (result != null && result.Ok)
                        {
                            exhausted = false;
                            break;
                        }

                        if (!IsRetryableFailureResult(result, out httpWait))
                        {
                            exhausted = false;
                            break;
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (LlmClientException retryEx) when (IsRetryableHttpError(retryEx, out httpWait))
                    {
                        // continue loop
                    }
                    catch (Exception)
                    {
                        exhausted = false;
                        break;
                    }
#else
                    await Task.Delay(TimeSpan.FromSeconds(waitSec), cancellationToken).ConfigureAwait(false);
                    try
                    {
                        result = await _inner.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
                        if (result != null && result.Ok)
                        {
                            exhausted = false;
                            break;
                        }

                        if (!IsRetryableFailureResult(result, out httpWait))
                        {
                            exhausted = false;
                            break;
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (LlmClientException retryEx) when (IsRetryableHttpError(retryEx, out httpWait))
                    {
                    }
                    catch (Exception)
                    {
                        exhausted = false;
                        break;
                    }
#endif
                }

                if (exhausted && result != null && !result.Ok)
                {
                    sw.Stop();
                    string msg = $"{result.ErrorCode} after {_maxHttpRetryAttempts} retries: {result.Error}";
                    _logger.Warn(
                        $"LLM ✖ traceId={trace} role={role} backend={backendLine} | {msg}", LogTag.Llm);
                    return new LlmCompletionResult
                    {
                        Ok = false,
                        Error = msg,
                        ErrorCode = result.ErrorCode,
                        HttpStatus = result.HttpStatus,
                        RetryAfterSeconds = result.RetryAfterSeconds,
                        ProviderErrorBody = result.ProviderErrorBody
                    };
                }
            }

            sw.Stop();
            double wallMs = sw.Elapsed.TotalMilliseconds;

            if (result == null)
            {
                _logger.Warn(
                    $"LLM ✖ traceId={trace} role={role} backend={backendLine} wallMs={wallMs:F0} | result is null",
                    LogTag.Llm);
                return new LlmCompletionResult { Ok = false, Error = "null result" };
            }

            if (!result.Ok)
            {
                _logger.Warn(
                    $"LLM ✖ traceId={trace} role={role} backend={backendLine} wallMs={wallMs:F0} | {result.Error ?? "(no text)"}",
                    LogTag.Llm);
                return result;
            }

            string content = result.Content ?? "";
            string tokLine = FormatTokenLine(result, wallMs, content.Length, system, user, request.Tools);
            string toolsLine = FormatExecutedTools(result.ExecutedToolCalls);
            _logger.Info(
                $"LLM ◀ traceId={trace} role={role} backend={backendLine} wallMs={wallMs:F0} | {tokLine}{toolsLine}\n" +
                $"  content ({content.Length} chars): {Preview(content, ResponsePreviewChars)}", LogTag.Llm);

            return result;
        }

        // .NET Standard 2.0 has no Random.Shared; System.Random is not thread-safe,
        // so guard the shared instance with a lock.
        private static readonly Random BackoffRandom = new();
        private static readonly object BackoffRandomLock = new();

        /// <summary>
        /// Deterministic exponential base delay (seconds) for a zero-based retry attempt:
        /// <c>min(2 * 2^attempt, MaxRetryCapSeconds)</c>.
        /// </summary>
        internal static int ComputeBackoffBase(int attempt)
        {
            return (int)Math.Min(2 * Math.Pow(2, attempt), MaxRetryCapSeconds);
        }

        /// <summary>
        /// "Full jitter" retry backoff: uniform random delay in <c>[0, base]</c> where base is the
        /// exponential value from <see cref="ComputeBackoffBase"/>. Randomizing the whole window
        /// de-synchronizes clients that all hit a 429/5xx at the same moment (thundering herd).
        /// Pass <c>null</c> for <paramref name="random"/> to get the deterministic base delay.
        /// </summary>
        internal static int ComputeBackoffDelay(int attempt, Random random)
        {
            int baseDelay = ComputeBackoffBase(attempt);
            if (random == null)
            {
                return baseDelay;
            }

            return random.Next(0, baseDelay + 1);
        }

        /// <summary>Computes the jittered retry backoff delay for a zero-based retry attempt.</summary>
        internal static int ComputeBackoff(int attempt)
        {
            lock (BackoffRandomLock)
            {
                return ComputeBackoffDelay(attempt, BackoffRandom);
            }
        }

        /// <summary>
        /// Returns true if the exception is a retryable HTTP error (429 or 5xx)
        /// and we have retry budget left.
        /// </summary>
        private static bool IsRetryableHttpError(Exception ex, out int retryAfterSeconds)
        {
            retryAfterSeconds = 0;
            if (ex is LlmClientException llmEx &&
                (llmEx.ErrorCode == LlmErrorCode.RateLimited ||
                 llmEx.ErrorCode == LlmErrorCode.BackendUnavailable))
            {
                retryAfterSeconds = llmEx.RetryAfterSeconds ?? 0;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Same policy as <see cref="IsRetryableHttpError"/> for adapters that surface HTTP 429/5xx as
        /// <see cref="LlmCompletionResult"/> (e.g. MeaiLlmClient wraps exceptions into <c>Ok=false</c>).
        /// </summary>
        private static bool IsRetryableFailureResult(LlmCompletionResult r, out int retryAfterSeconds)
        {
            retryAfterSeconds = 0;
            if (r == null || r.Ok)
            {
                return false;
            }

            if (r.ErrorCode != LlmErrorCode.RateLimited && r.ErrorCode != LlmErrorCode.BackendUnavailable)
            {
                return false;
            }

            retryAfterSeconds = r.RetryAfterSeconds ?? 0;
            return true;
        }

        /// <summary>
        /// Decorated streaming: forwards chunks to the caller as-is (so UI sees tokens
        /// as they arrive) while accumulating a preview for the final log line.
        /// Timeout from <c>_requestTimeoutSeconds</c> applies to the whole stream.
        /// </summary>
        /// <remarks>
        /// Without this override, <see cref="ILlmClient.CompleteStreamingAsync"/>
        /// would fall back to <see cref="CompleteAsync"/> and emit the entire response as
        /// a single buffered chunk, hiding real streaming behavior from the UI.
        /// </remarks>
        public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
            LlmCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                _logger.Warn($"LLM stream | backend={_backendLabel} | request=null", LogTag.Llm);
                yield return new LlmStreamChunk { IsDone = true, Error = "LlmCompletionRequest is null" };
                yield break;
            }

            if (_inner is ILlmPreflightAnnotator routing)
            {
                routing.PreflightAnnotate(request);
            }

            string trace = string.IsNullOrWhiteSpace(request.TraceId) ? "-" : request.TraceId.Trim();
            string role = string.IsNullOrWhiteSpace(request.AgentRoleId)
                ? "(role not set)"
                : request.AgentRoleId.Trim();
            string backendLine = string.IsNullOrWhiteSpace(request.RoutingProfileId)
                ? _backendLabel
                : $"{_backendLabel}->{request.RoutingProfileId.Trim()}";
            string streamSystem = request.SystemPrompt ?? "";
            string streamUser = request.UserPayload ?? "";
            IReadOnlyList<ILlmTool> streamTools = request.Tools;

            _logger.Info(
                $"LLM ▶ (stream) traceId={trace} role={role} backend={backendLine}\n" +
                $"  system ({streamSystem.Length} chars): {Preview(request.SystemPrompt, SystemPreviewChars)}\n" +
                $"  user ({streamUser.Length} chars): {Preview(request.UserPayload, UserPreviewChars)}\n" +
                $"  {FormatPromptBudgetLine(streamSystem, streamUser, streamTools)}", LogTag.Llm);

            Stopwatch sw = Stopwatch.StartNew();
            StringBuilder accumulated = new();
            int chunkCount = 0;
            int? promptTokens = null;
            int? completionTokens = null;
            int? totalTokens = null;
            string terminalError = null;
            IReadOnlyList<LlmToolCallTrace> executedTools = Array.Empty<LlmToolCallTrace>();

            // Timeout is enforced by the Unity-aware caller (CoreAiChatService)

            IAsyncEnumerator<LlmStreamChunk> enumerator = null;
            string initError = null;
            try
            {
                enumerator = _inner.CompleteStreamingAsync(request, cancellationToken)
                    .GetAsyncEnumerator(cancellationToken);
            }
            catch (Exception ex)
            {
                sw.Stop();
                initError = ex.Message;
                _logger.Warn(
                    $"LLM ✖ (stream) traceId={trace} role={role} backend={backendLine} wallMs={sw.Elapsed.TotalMilliseconds:F0} | init failed: {ex.Message}",
                    LogTag.Llm);
            }

            if (initError != null)
            {
                yield return new LlmStreamChunk { IsDone = true, Error = initError };
                yield break;
            }

            try
            {
                while (true)
                {
                    bool hasNext;
                    LlmStreamChunk current = null;
                    string exceptionMessage = null;
                    bool wasCancelled = false;

                    try
                    {
                        // No ConfigureAwait(false): WebGL has no working ThreadPool, and the
                        // continuation must come back through UnitySynchronizationContext.
                        hasNext = await enumerator.MoveNextAsync();
                        current = hasNext ? enumerator.Current : null;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        terminalError = "cancelled";
                        wasCancelled = true;
                        hasNext = false;
                    }
                    catch (Exception ex)
                    {
                        exceptionMessage = ex.Message;
                        hasNext = false;
                    }

                    if (wasCancelled)
                    {
                        yield return new LlmStreamChunk { IsDone = true, Error = terminalError };
                        yield break;
                    }

                    if (exceptionMessage != null)
                    {
                        terminalError = exceptionMessage;
                        yield return new LlmStreamChunk { IsDone = true, Error = exceptionMessage };
                        yield break;
                    }

                    if (!hasNext)
                    {
                        break;
                    }

                    if (current != null && !string.IsNullOrEmpty(current.Text))
                    {
                        accumulated.Append(current.Text);
                        chunkCount++;
                    }

                    if (current != null)
                    {
                        if (current.PromptTokens.HasValue)
                        {
                            promptTokens = current.PromptTokens;
                        }

                        if (current.CompletionTokens.HasValue)
                        {
                            completionTokens = current.CompletionTokens;
                        }

                        if (current.TotalTokens.HasValue)
                        {
                            totalTokens = current.TotalTokens;
                        }

                        if (!string.IsNullOrEmpty(current.Error))
                        {
                            terminalError = current.Error;
                        }

                        if (current.ExecutedToolCalls != null && current.ExecutedToolCalls.Count > 0)
                        {
                            executedTools = current.ExecutedToolCalls;
                        }
                    }

                    yield return current;
                }
            }
            finally
            {
                sw.Stop();
                if (enumerator != null)
                {
                    try
                    {
                        await enumerator.DisposeAsync();
                    }
                    catch
                    {
                        /* swallow */
                    }
                }

                double wallMs = sw.Elapsed.TotalMilliseconds;
                string content = accumulated.ToString();
                LlmCompletionResult synthetic = new()
                {
                    Ok = string.IsNullOrEmpty(terminalError),
                    Content = content,
                    PromptTokens = promptTokens,
                    CompletionTokens = completionTokens,
                    TotalTokens = totalTokens,
                    Error = terminalError ?? "",
                    ExecutedToolCalls = executedTools
                };
                string tokLine = FormatTokenLine(synthetic, wallMs, content.Length, streamSystem, streamUser,
                    streamTools);
                string toolsLine = FormatExecutedTools(executedTools);

                if (!string.IsNullOrEmpty(terminalError))
                {
                    _logger.Warn(
                        $"LLM ✖ (stream) traceId={trace} role={role} backend={backendLine} wallMs={wallMs:F0} chunks={chunkCount} | {terminalError}{toolsLine}",
                        LogTag.Llm);
                }
                else
                {
                    _logger.Info(
                        $"LLM ◀ (stream) traceId={trace} role={role} backend={backendLine} wallMs={wallMs:F0} chunks={chunkCount} | {tokLine}{toolsLine}\n" +
                        $"  content ({content.Length} chars): {Preview(content, ResponsePreviewChars)}", LogTag.Llm);
                }
            }
        }

        /// <summary>
        /// Formats executed tool traces for the compact LLM completion log line.
        /// Returns an empty string when no tool was invoked, so plain text turns stay one-line.
        /// Format: <c> | tools=[name(ok,12ms),name(fail,4ms,native)]</c>.
        /// </summary>
        internal static string FormatExecutedTools(IReadOnlyList<LlmToolCallTrace> traces)
        {
            if (traces == null || traces.Count == 0)
            {
                return "";
            }

            StringBuilder sb = new();
            sb.Append(" | tools=[");
            for (int i = 0; i < traces.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                LlmToolCallTrace t = traces[i];
                string status = t.Success ? "ok" : "fail";
                sb.Append(t.Name);
                sb.Append('(');
                sb.Append(status);
                sb.Append(',');
                sb.Append(t.DurationMs.ToString("F0"));
                sb.Append("ms");
                if (!string.IsNullOrEmpty(t.Source) && t.Source != "native")
                {
                    sb.Append(',');
                    sb.Append(t.Source);
                }

                sb.Append(')');
            }

            sb.Append(']');
            return sb.ToString();
        }

        private static string FormatTokenLine(
            LlmCompletionResult result,
            double wallMs,
            int outChars,
            string systemPrompt,
            string userPayload,
            IReadOnlyList<ILlmTool> tools)
        {
            string budgetSuffix = " | " + FormatPromptBudgetLine(systemPrompt ?? "", userPayload ?? "", tools);
            string outWordsPart = outChars > 0
                ? $" | outWords≈{CountWords(result.Content ?? "")}"
                : "";

            if (result.CompletionTokens.HasValue && wallMs > 1)
            {
                double tps = result.CompletionTokens.Value / (wallMs / 1000.0);
                return
                    $"tokens in/out/total={Fmt(result.PromptTokens)}/{Fmt(result.CompletionTokens)}/{Fmt(result.TotalTokens)} | out≈{tps:F1} tok/s (completion){outWordsPart}{budgetSuffix}";
            }

            if (result.TotalTokens.HasValue)
            {
                return
                    $"tokens in/out/total={Fmt(result.PromptTokens)}/{Fmt(result.CompletionTokens)}/{Fmt(result.TotalTokens)} | tok/s n/a{outWordsPart}{budgetSuffix}";
            }

            return
                $"tokens n/a (backend did not return usage for this response - common for streaming/local clients and some Chat API responses) | outChars={outChars} | speed estimate n/a{outWordsPart}{budgetSuffix}";
        }

        /// <summary>
        /// Memory section delimiter in system prompt, as in <see cref="CoreAI.Ai.AiOrchestrator"/> (BuildRequest).
        /// </summary>
        internal const string OrchestratorMemorySectionDelimiter = "\n\n## Memory\n";

        /// <summary>
        /// Splits <paramref name="systemPrompt"/> into clean system text and memory body after <see cref="OrchestratorMemorySectionDelimiter"/>.
        /// </summary>
        internal static void SplitSystemCoreAndMemory(string systemPrompt, out string corePrompt, out string memoryBody)
        {
            corePrompt = systemPrompt ?? "";
            memoryBody = "";
            if (string.IsNullOrEmpty(systemPrompt))
            {
                return;
            }

            int idx = systemPrompt.IndexOf(OrchestratorMemorySectionDelimiter, StringComparison.Ordinal);
            if (idx < 0)
            {
                return;
            }

            corePrompt = systemPrompt.Substring(0, idx).TrimEnd();
            memoryBody = systemPrompt.Substring(idx + OrchestratorMemorySectionDelimiter.Length).Trim();
        }

        /// <summary>
        /// Rough prompt size estimate for logging and budgeting when the API doesn't return usage.
        /// </summary>
        internal static string FormatPromptBudgetLine(
            string systemPrompt,
            string userPayload,
            IReadOnlyList<ILlmTool> tools = null)
        {
            SplitSystemCoreAndMemory(systemPrompt ?? "", out string core, out string mem);
            int sysChars = systemPrompt?.Length ?? 0;
            int coreChars = core.Length;
            int memChars = mem.Length;
            int toolsChars = EstimateToolsCatalogChars(tools);
            int toolCount = tools?.Count ?? 0;

            int chatChars = userPayload?.Length ?? 0;
            int coreTok = EstimateTokensRough(core);
            int memTok = EstimateTokensRough(mem);
            int toolsTok = EstimateTokensRoughFromCharCount(toolsChars);
            int chatTok = EstimateTokensRough(userPayload);
            int coreWords = CountWords(core);
            int memWords = CountWords(mem);
            int toolsWords = CountWords(BuildToolsCatalogBlobForWordCount(tools));
            int chatWords = CountWords(userPayload);

            int sysTokFromParts = coreTok + memTok;
            int sysTokWhole = EstimateTokensRough(systemPrompt);
            return
                $"promptBudget systemSplit chars total={sysChars} core={coreChars} memory={memChars} toolsDef≈{toolsChars}({toolCount} tools) " +
                $"| system estTok≈{sysTokWhole} (core≈{coreTok} mem≈{memTok} toolsDef≈{toolsTok}; partsSum≈{sysTokFromParts + toolsTok}) " +
                $"| system words≈{coreWords + memWords + toolsWords} (core≈{coreWords} mem≈{memWords} tools≈{toolsWords}) " +
                $"| chat chars={chatChars} estTok≈{chatTok} words≈{chatWords} " +
                $"[estTok=⌈chars/4⌉; toolsDef≈tool definition size]";
        }

        /// <summary>
        /// Estimate characters that LLMUnity adds to system when tools are present.
        /// </summary>
        internal static int EstimateToolsCatalogChars(IReadOnlyList<ILlmTool> tools)
        {
            if (tools == null || tools.Count == 0)
            {
                return 0;
            }

            int n = LlmUnityToolsRulesPreambleCharCount;
            foreach (ILlmTool t in tools)
            {
                if (t == null)
                {
                    continue;
                }

                string name = t.Name ?? "";
                string desc = t.Description ?? "";
                string schema = t.ParametersSchema ?? "";
                n += "- name: ".Length + name.Length + "\n  description: ".Length + desc.Length + "\n".Length;
                n += "  parameters schema: ".Length + schema.Length + "\n".Length;
            }

            return n;
        }

        private static int LlmUnityToolsRulesPreambleCharCount =>
            "\n\nCRITICAL SYSTEM RULES FOR TOOLS:\n".Length +
            "1. You have access to the following tools. You MUST use one if it matches the user request.\n".Length +
            "2. To use a tool, output ONLY valid JSON matching this format: ```json\n{\"name\": \"tool_name\", \"arguments\": {\"arg\": \"val\"}}\n```\n"
                .Length +
            "3. DO NOT output conversational text if you call a tool. ONLY output the JSON block.\n\nAVAILABLE TOOLS:\n"
                .Length;

        private static string BuildToolsCatalogBlobForWordCount(IReadOnlyList<ILlmTool> tools)
        {
            if (tools == null || tools.Count == 0)
            {
                return "";
            }

            StringBuilder sb = new();
            foreach (ILlmTool t in tools)
            {
                if (t == null)
                {
                    continue;
                }

                sb.Append(t.Name);
                sb.Append(' ');
                sb.Append(t.Description);
                sb.Append(' ');
                sb.Append(t.ParametersSchema);
                sb.Append(' ');
            }

            return sb.ToString();
        }

        private static int EstimateTokensRoughFromCharCount(int charCount)
        {
            if (charCount <= 0)
            {
                return 0;
            }

            return (charCount + 3) / 4;
        }

        internal static int EstimateTokensRough(string text)
        {
            return EstimateTokensRoughFromCharCount(string.IsNullOrEmpty(text) ? 0 : text.Length);
        }

        internal static int CountWords(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return 0;
            }

            int count = 0;
            bool inWord = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                bool ws = char.IsWhiteSpace(c);
                if (!ws && !inWord)
                {
                    inWord = true;
                    count++;
                }
                else if (ws)
                {
                    inWord = false;
                }
            }

            return count;
        }

        private static string Fmt(int? n)
        {
            return n.HasValue ? n.Value.ToString() : "-";
        }

        private static string Preview(string text, int maxChars)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "(empty)";
            }

            string t = text.Trim();
            if (t.Length <= maxChars)
            {
                return t;
            }

            return t.Substring(0, maxChars) + $"... [+{t.Length - maxChars} chars]";
        }
    }
}