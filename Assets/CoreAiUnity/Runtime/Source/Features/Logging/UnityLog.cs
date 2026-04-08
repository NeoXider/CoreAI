using CoreAI.Infrastructure.Logging;
using CoreAI.Logging;

namespace CoreAI.Unity.Logging
{
    /// <summary>
    /// Unity-реализация <see cref="ILog"/> — делегирует к <see cref="IGameLogger"/>.
    /// Маппит строковые <see cref="LogTag"/> на <see cref="GameLogFeature"/> для фильтрации.
    /// Устанавливается как <see cref="Log.Instance"/> при инициализации DI-контейнера.
    /// </summary>
    public sealed class UnityLog : ILog
    {
        private readonly IGameLogger _logger;

        public UnityLog(IGameLogger logger)
        {
            _logger = logger;
        }

        public void Debug(string message, string tag = null)
            => _logger.LogDebug(MapTag(tag), message);

        public void Info(string message, string tag = null)
            => _logger.LogInfo(MapTag(tag), message);

        public void Warn(string message, string tag = null)
            => _logger.LogWarning(MapTag(tag), message);

        public void Error(string message, string tag = null)
            => _logger.LogError(MapTag(tag), message);

        /// <summary>
        /// Маппинг строковых тегов <see cref="LogTag"/> → <see cref="GameLogFeature"/>.
        /// Неизвестные теги → <see cref="GameLogFeature.Core"/>.
        /// </summary>
        private static GameLogFeature MapTag(string tag)
        {
            if (string.IsNullOrEmpty(tag))
                return GameLogFeature.Core;

            return tag switch
            {
                LogTag.Core        => GameLogFeature.Core,
                LogTag.Composition => GameLogFeature.Composition,
                LogTag.MessagePipe => GameLogFeature.MessagePipe,
                LogTag.Llm         => GameLogFeature.Llm,
                LogTag.Metrics     => GameLogFeature.Metrics,
                LogTag.Lua         => GameLogFeature.MessagePipe, // Lua идёт через MessagePipe pipeline
                LogTag.World       => GameLogFeature.Core,
                LogTag.Memory      => GameLogFeature.Llm,         // Memory — часть LLM tool calling
                LogTag.Config      => GameLogFeature.Core,
                _                  => GameLogFeature.Core
            };
        }
    }
}
