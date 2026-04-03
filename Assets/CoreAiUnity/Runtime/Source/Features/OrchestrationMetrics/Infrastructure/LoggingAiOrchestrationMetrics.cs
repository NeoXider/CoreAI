using CoreAI.Ai;
using CoreAI.Infrastructure.Logging;

namespace CoreAI.Infrastructure.Ai
{
    /// <summary>Пишет события <see cref="IAiOrchestrationMetrics"/> в лог при включённом <see cref="GameLogFeature.Metrics"/>.</summary>
    public sealed class LoggingAiOrchestrationMetrics : IAiOrchestrationMetrics
    {
        private readonly IGameLogger _logger;
        private readonly IGameLogSettings _settings;

        /// <summary>Создать метрики с фильтрацией по настройкам лога.</summary>
        public LoggingAiOrchestrationMetrics(IGameLogger logger, IGameLogSettings settings)
        {
            _logger = logger;
            _settings = settings;
        }

        /// <inheritdoc />
        public void RecordLlmCompletion(string roleId, string traceId, bool ok, double wallMs)
        {
            if (_settings == null || !_settings.ShouldLog(GameLogFeature.Metrics, GameLogLevel.Info))
                return;
            var r = string.IsNullOrWhiteSpace(roleId) ? "—" : roleId.Trim();
            var t = string.IsNullOrWhiteSpace(traceId) ? "—" : traceId.Trim();
            _logger.LogInfo(GameLogFeature.Metrics,
                $"[ai-metrics] llm role={r} traceId={t} ok={ok} wallMs={wallMs:F0}");
        }

        /// <inheritdoc />
        public void RecordStructuredRetry(string roleId, string traceId, string reason)
        {
            if (_settings == null || !_settings.ShouldLog(GameLogFeature.Metrics, GameLogLevel.Info))
                return;
            var r = string.IsNullOrWhiteSpace(roleId) ? "—" : roleId.Trim();
            var t = string.IsNullOrWhiteSpace(traceId) ? "—" : traceId.Trim();
            var msg = string.IsNullOrWhiteSpace(reason) ? "—" : reason.Trim();
            _logger.LogInfo(GameLogFeature.Metrics,
                $"[ai-metrics] structured_retry role={r} traceId={t} reason={msg}");
        }

        /// <inheritdoc />
        public void RecordCommandPublished(string roleId, string traceId)
        {
            if (_settings == null || !_settings.ShouldLog(GameLogFeature.Metrics, GameLogLevel.Info))
                return;
            var r = string.IsNullOrWhiteSpace(roleId) ? "—" : roleId.Trim();
            var t = string.IsNullOrWhiteSpace(traceId) ? "—" : traceId.Trim();
            _logger.LogInfo(GameLogFeature.Metrics,
                $"[ai-metrics] publish role={r} traceId={t}");
        }
    }
}
