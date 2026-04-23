using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Logging;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Оборачивает <see cref="ILlmClient"/>: сквозной <see cref="LlmCompletionRequest.TraceId"/>, время вызова,
    /// токены (если бэкенд заполнил <see cref="LlmCompletionResult"/>), превью промптов и ответа.
    /// </summary>
    public sealed class LoggingLlmClientDecorator : ILlmClient
    {
        private const int SystemPreviewChars = 1200;
        private const int UserPreviewChars = 1600;
        private const int ResponsePreviewChars = 2400;

        private readonly ILlmClient _inner;
        private readonly IGameLogger _logger;
        private readonly string _backendLabel;
        private readonly float _requestTimeoutSeconds;

        /// <param name="requestTimeoutSeconds">0 — без лимита; иначе отмена <see cref="CompleteAsync"/> по истечении секунд (совместно с внешним token).</param>
        public LoggingLlmClientDecorator(ILlmClient inner, IGameLogger logger, float requestTimeoutSeconds = 0f)
        {
            _inner = inner;
            _logger = logger;
            _requestTimeoutSeconds = requestTimeoutSeconds < 0f ? 0f : requestTimeoutSeconds;
            _backendLabel = inner?.GetType().Name ?? "?";
        }

        /// <summary>Нижележащий клиент (без декораторов — самый внешний из цепочки).</summary>
        public ILlmClient Inner => _inner;

        /// <summary>Снимает все <see cref="LoggingLlmClientDecorator"/> с вершины цепочки.</summary>
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
                _logger.LogWarning(GameLogFeature.Llm, $"LLM | backend={_backendLabel} | request=null");
                return new LlmCompletionResult { Ok = false, Error = "LlmCompletionRequest is null" };
            }

            if (_inner is RoutingLlmClient routing)
            {
                routing.PreflightAnnotate(request);
            }

            string trace = string.IsNullOrWhiteSpace(request.TraceId) ? "—" : request.TraceId.Trim();
            string role = string.IsNullOrWhiteSpace(request.AgentRoleId)
                ? "(роль не задана)"
                : request.AgentRoleId.Trim();
            string system = request.SystemPrompt ?? "";
            string user = request.UserPayload ?? "";
            string backendLine = string.IsNullOrWhiteSpace(request.RoutingProfileId)
                ? _backendLabel
                : $"{_backendLabel}→{request.RoutingProfileId.Trim()}";

            _logger.LogInfo(GameLogFeature.Llm,
                $"LLM ▶ traceId={trace} role={role} backend={backendLine}\n" +
                $"  system ({system.Length} симв.): {Preview(system, SystemPreviewChars)}\n" +
                $"  user ({user.Length} симв.): {Preview(user, UserPreviewChars)}");

            Stopwatch sw = Stopwatch.StartNew();
            LlmCompletionResult result;
            using (CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                if (_requestTimeoutSeconds > 0f)
                {
                    linked.CancelAfter(TimeSpan.FromSeconds(_requestTimeoutSeconds));
                }

                try
                {
                    result = await _inner.CompleteAsync(request, linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    sw.Stop();
                    string msg = $"LLM request timeout ({_requestTimeoutSeconds}s)";
                    _logger.LogWarning(GameLogFeature.Llm,
                        $"LLM ⏱ traceId={trace} role={role} backend={backendLine} wallMs={sw.Elapsed.TotalMilliseconds:F0} | {msg}");
                    return new LlmCompletionResult { Ok = false, Error = msg };
                }
            }

            sw.Stop();
            double wallMs = sw.Elapsed.TotalMilliseconds;

            if (result == null)
            {
                _logger.LogWarning(GameLogFeature.Llm,
                    $"LLM ✖ traceId={trace} role={role} backend={backendLine} wallMs={wallMs:F0} | результат null");
                return new LlmCompletionResult { Ok = false, Error = "null result" };
            }

            if (!result.Ok && _requestTimeoutSeconds > 0f && !cancellationToken.IsCancellationRequested &&
                string.Equals(result.Error, "Cancelled", StringComparison.Ordinal))
            {
                string msg = $"LLM request timeout ({_requestTimeoutSeconds}s)";
                _logger.LogWarning(GameLogFeature.Llm,
                    $"LLM ⏱ traceId={trace} role={role} backend={backendLine} wallMs={wallMs:F0} | {msg}");
                return new LlmCompletionResult { Ok = false, Error = msg };
            }

            if (!result.Ok)
            {
                _logger.LogWarning(GameLogFeature.Llm,
                    $"LLM ✖ traceId={trace} role={role} backend={backendLine} wallMs={wallMs:F0} | {result.Error ?? "(без текста)"}");
                return result;
            }

            string content = result.Content ?? "";
            string tokLine = FormatTokenLine(result, wallMs, content.Length);
            _logger.LogInfo(GameLogFeature.Llm,
                $"LLM ◀ traceId={trace} role={role} backend={backendLine} wallMs={wallMs:F0} | {tokLine}\n" +
                $"  content ({content.Length} симв.): {Preview(content, ResponsePreviewChars)}");

            return result;
        }

        /// <summary>
        /// Декорированный стриминг: пробрасывает чанки наружу как есть (чтобы UI
        /// видел токены по мере поступления), но параллельно накапливает превью
        /// ответа для финального лога. Таймаут из <c>_requestTimeoutSeconds</c>
        /// применяется ко всему стриму.
        /// </summary>
        /// <remarks>
        /// Без этого override'а default-реализация <see cref="ILlmClient.CompleteStreamingAsync"/>
        /// делала fallback к <see cref="CompleteAsync"/> и выдавала весь ответ одним
        /// чанком в конце генерации — стриминг в UI не был виден.
        /// </remarks>
        public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
            LlmCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                _logger.LogWarning(GameLogFeature.Llm, $"LLM stream | backend={_backendLabel} | request=null");
                yield return new LlmStreamChunk { IsDone = true, Error = "LlmCompletionRequest is null" };
                yield break;
            }

            if (_inner is RoutingLlmClient routing)
            {
                routing.PreflightAnnotate(request);
            }

            string trace = string.IsNullOrWhiteSpace(request.TraceId) ? "—" : request.TraceId.Trim();
            string role = string.IsNullOrWhiteSpace(request.AgentRoleId)
                ? "(роль не задана)"
                : request.AgentRoleId.Trim();
            string backendLine = string.IsNullOrWhiteSpace(request.RoutingProfileId)
                ? _backendLabel
                : $"{_backendLabel}→{request.RoutingProfileId.Trim()}";

            _logger.LogInfo(GameLogFeature.Llm,
                $"LLM ▶ (stream) traceId={trace} role={role} backend={backendLine}\n" +
                $"  system ({(request.SystemPrompt ?? "").Length} симв.): {Preview(request.SystemPrompt, SystemPreviewChars)}\n" +
                $"  user ({(request.UserPayload ?? "").Length} симв.): {Preview(request.UserPayload, UserPreviewChars)}");

            Stopwatch sw = Stopwatch.StartNew();
            StringBuilder accumulated = new();
            int chunkCount = 0;
            int? promptTokens = null;
            int? completionTokens = null;
            int? totalTokens = null;
            string terminalError = null;

            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (_requestTimeoutSeconds > 0f)
            {
                linked.CancelAfter(TimeSpan.FromSeconds(_requestTimeoutSeconds));
            }

            IAsyncEnumerator<LlmStreamChunk> enumerator = null;
            string initError = null;
            try
            {
                enumerator = _inner.CompleteStreamingAsync(request, linked.Token).GetAsyncEnumerator(linked.Token);
            }
            catch (Exception ex)
            {
                sw.Stop();
                initError = ex.Message;
                _logger.LogWarning(GameLogFeature.Llm,
                    $"LLM ✖ (stream) traceId={trace} role={role} backend={backendLine} wallMs={sw.Elapsed.TotalMilliseconds:F0} | init failed: {ex.Message}");
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
                    bool failedWithTimeout = false;
                    bool failedWithException = false;
                    string exceptionMessage = null;

                    try
                    {
                        hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                        current = hasNext ? enumerator.Current : null;
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        failedWithTimeout = true;
                        hasNext = false;
                    }
                    catch (Exception ex)
                    {
                        failedWithException = true;
                        exceptionMessage = ex.Message;
                        hasNext = false;
                    }

                    if (failedWithTimeout)
                    {
                        terminalError = $"LLM stream timeout ({_requestTimeoutSeconds}s)";
                        yield return new LlmStreamChunk { IsDone = true, Error = terminalError };
                        yield break;
                    }

                    if (failedWithException)
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
                        if (current.PromptTokens.HasValue) promptTokens = current.PromptTokens;
                        if (current.CompletionTokens.HasValue) completionTokens = current.CompletionTokens;
                        if (current.TotalTokens.HasValue) totalTokens = current.TotalTokens;
                        if (!string.IsNullOrEmpty(current.Error)) terminalError = current.Error;
                    }

                    yield return current;
                }
            }
            finally
            {
                sw.Stop();
                if (enumerator != null)
                {
                    try { await enumerator.DisposeAsync().ConfigureAwait(false); } catch { /* swallow */ }
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
                    Error = terminalError ?? ""
                };
                string tokLine = FormatTokenLine(synthetic, wallMs, content.Length);

                if (!string.IsNullOrEmpty(terminalError))
                {
                    _logger.LogWarning(GameLogFeature.Llm,
                        $"LLM ✖ (stream) traceId={trace} role={role} backend={backendLine} wallMs={wallMs:F0} chunks={chunkCount} | {terminalError}");
                }
                else
                {
                    _logger.LogInfo(GameLogFeature.Llm,
                        $"LLM ◀ (stream) traceId={trace} role={role} backend={backendLine} wallMs={wallMs:F0} chunks={chunkCount} | {tokLine}\n" +
                        $"  content ({content.Length} симв.): {Preview(content, ResponsePreviewChars)}");
                }
            }
        }

        private static string FormatTokenLine(LlmCompletionResult result, double wallMs, int outChars)
        {
            if (result.CompletionTokens.HasValue && wallMs > 1)
            {
                double tps = result.CompletionTokens.Value / (wallMs / 1000.0);
                return
                    $"tokens in/out/total={Fmt(result.PromptTokens)}/{Fmt(result.CompletionTokens)}/{Fmt(result.TotalTokens)} | out≈{tps:F1} tok/s (по completion)";
            }

            if (result.TotalTokens.HasValue)
            {
                return
                    $"tokens in/out/total={Fmt(result.PromptTokens)}/{Fmt(result.CompletionTokens)}/{Fmt(result.TotalTokens)} | tok/s н/д";
            }

            return $"tokens н/д (LLMUnity не отдаёт usage в Chat) | outChars={outChars} | оценка скорости н/д";
        }

        private static string Fmt(int? n)
        {
            return n.HasValue ? n.Value.ToString() : "—";
        }

        private static string Preview(string text, int maxChars)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "(пусто)";
            }

            string t = text.Trim();
            if (t.Length <= maxChars)
            {
                return t;
            }

            return t.Substring(0, maxChars) + $"... [+{t.Length - maxChars} симв.]";
        }
    }
}