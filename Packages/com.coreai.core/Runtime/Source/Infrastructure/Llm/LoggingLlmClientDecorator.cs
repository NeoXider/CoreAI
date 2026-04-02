using System;
using System.Diagnostics;
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
            var c = client;
            while (c is LoggingLlmClientDecorator d)
                c = d.Inner;
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
                routing.PreflightAnnotate(request);

            var trace = string.IsNullOrWhiteSpace(request.TraceId) ? "—" : request.TraceId.Trim();
            var role = string.IsNullOrWhiteSpace(request.AgentRoleId) ? "(роль не задана)" : request.AgentRoleId.Trim();
            var system = request.SystemPrompt ?? "";
            var user = request.UserPayload ?? "";
            var backendLine = string.IsNullOrWhiteSpace(request.RoutingProfileId)
                ? _backendLabel
                : $"{_backendLabel}→{request.RoutingProfileId.Trim()}";

            _logger.LogInfo(GameLogFeature.Llm,
                $"LLM ▶ traceId={trace} role={role} backend={backendLine}\n" +
                $"  system ({system.Length} симв.): {Preview(system, SystemPreviewChars)}\n" +
                $"  user ({user.Length} симв.): {Preview(user, UserPreviewChars)}");

            var sw = Stopwatch.StartNew();
            LlmCompletionResult result;
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                if (_requestTimeoutSeconds > 0f)
                    linked.CancelAfter(TimeSpan.FromSeconds(_requestTimeoutSeconds));

                try
                {
                    result = await _inner.CompleteAsync(request, linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    sw.Stop();
                    var msg = $"LLM request timeout ({_requestTimeoutSeconds}s)";
                    _logger.LogWarning(GameLogFeature.Llm,
                        $"LLM ⏱ traceId={trace} role={role} backend={backendLine} wallMs={sw.Elapsed.TotalMilliseconds:F0} | {msg}");
                    return new LlmCompletionResult { Ok = false, Error = msg };
                }
            }

            sw.Stop();
            var wallMs = sw.Elapsed.TotalMilliseconds;

            if (result == null)
            {
                _logger.LogWarning(GameLogFeature.Llm,
                    $"LLM ✖ traceId={trace} role={role} backend={backendLine} wallMs={wallMs:F0} | результат null");
                return new LlmCompletionResult { Ok = false, Error = "null result" };
            }

            if (!result.Ok && _requestTimeoutSeconds > 0f && !cancellationToken.IsCancellationRequested &&
                string.Equals(result.Error, "Cancelled", StringComparison.Ordinal))
            {
                var msg = $"LLM request timeout ({_requestTimeoutSeconds}s)";
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

            var content = result.Content ?? "";
            var tokLine = FormatTokenLine(result, wallMs, content.Length);
            _logger.LogInfo(GameLogFeature.Llm,
                $"LLM ◀ traceId={trace} role={role} backend={backendLine} wallMs={wallMs:F0} | {tokLine}\n" +
                $"  content ({content.Length} симв.): {Preview(content, ResponsePreviewChars)}");

            return result;
        }

        private static string FormatTokenLine(LlmCompletionResult result, double wallMs, int outChars)
        {
            if (result.CompletionTokens.HasValue && wallMs > 1)
            {
                var tps = result.CompletionTokens.Value / (wallMs / 1000.0);
                return $"tokens in/out/total={Fmt(result.PromptTokens)}/{Fmt(result.CompletionTokens)}/{Fmt(result.TotalTokens)} | out≈{tps:F1} tok/s (по completion)";
            }

            if (result.TotalTokens.HasValue)
                return $"tokens in/out/total={Fmt(result.PromptTokens)}/{Fmt(result.CompletionTokens)}/{Fmt(result.TotalTokens)} | tok/s н/д";

            return $"tokens н/д (LLMUnity не отдаёт usage в Chat) | outChars={outChars} | оценка скорости н/д";
        }

        private static string Fmt(int? n) => n.HasValue ? n.Value.ToString() : "—";

        private static string Preview(string text, int maxChars)
        {
            if (string.IsNullOrEmpty(text))
                return "(пусто)";

            var t = text.Trim();
            if (t.Length <= maxChars)
                return t;

            return t.Substring(0, maxChars) + $"... [+{t.Length - maxChars} симв.]";
        }
    }
}
