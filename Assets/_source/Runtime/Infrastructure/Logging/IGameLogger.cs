using UnityEngine;

namespace CoreAI.Infrastructure.Logging
{
    /// <summary>
    /// Логирование с привязкой к <see cref="GameLogFeature"/> — включение/выключение категорий в asset настроек.
    /// </summary>
    public interface IGameLogger
    {
        void LogDebug(GameLogFeature feature, string message, Object context = null);

        void LogInfo(GameLogFeature feature, string message, Object context = null);

        void LogWarning(GameLogFeature feature, string message, Object context = null);

        void LogError(GameLogFeature feature, string message, Object context = null);
    }
}
