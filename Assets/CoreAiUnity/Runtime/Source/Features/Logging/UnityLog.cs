using CoreAI.Infrastructure.Logging;

namespace CoreAI.Unity.Logging
{
    /// <summary>
    /// Реализация ILog для Unity — делегирует к IGameLogger.
    /// Используется для логирования tool call и других событий из CoreAI.Core.
    /// </summary>
    public sealed class UnityLog : CoreAI.Logging.ILog
    {
        private readonly IGameLogger _logger;

        public UnityLog(IGameLogger logger)
        {
            _logger = logger;
        }

        public void Info(string message) => _logger.LogInfo(GameLogFeature.Llm, $"[CoreAI] {message}");
        public void Warn(string message) => _logger.LogWarning(GameLogFeature.Llm, $"[CoreAI] {message}");
        public void Error(string message) => _logger.LogError(GameLogFeature.Llm, $"[CoreAI] {message}");
    }
}
