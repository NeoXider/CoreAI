using CoreAI.Ai;
using CoreAI.Infrastructure.Logging;

namespace CoreAI.Infrastructure.Ai
{
    /// <summary>Metrics decorator that writes orchestration events to the game logger.</summary>
    public sealed class LoggingAiOrchestrationMetrics : IAiOrchestrationMetrics
    {
        private readonly IGameLogger _logger;
        private readonly IGameLogSettings _settings;

        /// <summary>Initializes a new instance of LoggingAiOrchestrationMetrics.</summary>
        public LoggingAiOrchestrationMetrics(IGameLogger logger, IGameLogSettings settings)
        {
            _logger = logger;
            _settings = settings;
        }

        /// <inheritdoc />
        public void RecordLlmCompletion(
            string actorId,
            string roleId,
            string traceId,
            AiLlmCompletionOutcome outcome,
            double wallMs)
        {
            if (_settings == null || !_settings.ShouldLog(GameLogFeature.Metrics, GameLogLevel.Info))
            {
                return;
            }

            string a = string.IsNullOrWhiteSpace(actorId) ? "-" : actorId.Trim();
            string r = string.IsNullOrWhiteSpace(roleId) ? "-" : roleId.Trim();
            string t = string.IsNullOrWhiteSpace(traceId) ? "-" : traceId.Trim();
            string outcomeName = outcome switch
            {
                AiLlmCompletionOutcome.Succeeded => "success",
                AiLlmCompletionOutcome.ProviderFailure => "provider_failure",
                AiLlmCompletionOutcome.Replaced => "replacement",
                AiLlmCompletionOutcome.DeadlineCancellation => "deadline_cancellation",
                _ => "cancelled"
            };
            _logger.LogInfo(GameLogFeature.Metrics,
                $"[ai-metrics] llm actor={a} role={r} traceId={t} outcome={outcomeName} wallMs={wallMs:F0}");
        }

        /// <inheritdoc />
        public void RecordStructuredRetry(string actorId, string roleId, string traceId, string reason)
        {
            if (_settings == null || !_settings.ShouldLog(GameLogFeature.Metrics, GameLogLevel.Info))
            {
                return;
            }

            string a = string.IsNullOrWhiteSpace(actorId) ? "-" : actorId.Trim();
            string r = string.IsNullOrWhiteSpace(roleId) ? "-" : roleId.Trim();
            string t = string.IsNullOrWhiteSpace(traceId) ? "-" : traceId.Trim();
            string msg = string.IsNullOrWhiteSpace(reason) ? "-" : reason.Trim();
            _logger.LogInfo(GameLogFeature.Metrics,
                $"[ai-metrics] structured_retry actor={a} role={r} traceId={t} reason={msg}");
        }

        /// <inheritdoc />
        public void RecordCommandPublished(string actorId, string roleId, string traceId)
        {
            if (_settings == null || !_settings.ShouldLog(GameLogFeature.Metrics, GameLogLevel.Info))
            {
                return;
            }

            string a = string.IsNullOrWhiteSpace(actorId) ? "-" : actorId.Trim();
            string r = string.IsNullOrWhiteSpace(roleId) ? "-" : roleId.Trim();
            string t = string.IsNullOrWhiteSpace(traceId) ? "-" : traceId.Trim();
            _logger.LogInfo(GameLogFeature.Metrics,
                $"[ai-metrics] publish actor={a} role={r} traceId={t}");
        }
    }
}
