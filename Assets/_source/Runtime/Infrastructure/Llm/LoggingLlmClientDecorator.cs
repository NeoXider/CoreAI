using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Logging;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Оборачивает <see cref="ILlmClient"/> и пишет в <see cref="IGameLogger"/> роль агента,
    /// тип бэкенда, превью системного/пользовательского промпта и ответа модели (или ошибку).
    /// </summary>
    public sealed class LoggingLlmClientDecorator : ILlmClient
    {
        private const int SystemPreviewChars = 1200;
        private const int UserPreviewChars = 1600;
        private const int ResponsePreviewChars = 2400;

        private readonly ILlmClient _inner;
        private readonly IGameLogger _logger;
        private readonly string _backendLabel;

        public LoggingLlmClientDecorator(ILlmClient inner, IGameLogger logger)
        {
            _inner = inner;
            _logger = logger;
            _backendLabel = inner?.GetType().Name ?? "?";
        }

        public async Task<LlmCompletionResult> CompleteAsync(
            LlmCompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                _logger.LogWarning(GameLogFeature.Llm, $"Запрос LLM | backend={_backendLabel} | request=null");
                return new LlmCompletionResult { Ok = false, Error = "LlmCompletionRequest is null" };
            }

            var role = string.IsNullOrWhiteSpace(request.AgentRoleId) ? "(роль не задана)" : request.AgentRoleId.Trim();
            var system = request.SystemPrompt ?? "";
            var user = request.UserPayload ?? "";

            _logger.LogInfo(GameLogFeature.Llm,
                $"Запрос LLM | роль={role} | backend={_backendLabel}\n" +
                $"  system ({system.Length} симв.): {Preview(system, SystemPreviewChars)}\n" +
                $"  user ({user.Length} симв.): {Preview(user, UserPreviewChars)}");

            var result = await _inner.CompleteAsync(request, cancellationToken).ConfigureAwait(false);

            if (result == null)
            {
                _logger.LogWarning(GameLogFeature.Llm, $"Ответ LLM | роль={role} | backend={_backendLabel} | результат null");
                return new LlmCompletionResult { Ok = false, Error = "null result" };
            }

            if (!result.Ok)
            {
                _logger.LogWarning(GameLogFeature.Llm,
                    $"Ошибка LLM | роль={role} | backend={_backendLabel} | {result.Error ?? "(без текста)"}");
                return result;
            }

            var content = result.Content ?? "";
            _logger.LogInfo(GameLogFeature.Llm,
                $"Ответ LLM | роль={role} | backend={_backendLabel}\n" +
                $"  content ({content.Length} симв.): {Preview(content, ResponsePreviewChars)}");

            return result;
        }

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
