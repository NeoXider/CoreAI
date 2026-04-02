using UnityEngine;

namespace CoreAI.Infrastructure.Logging
{
    /// <summary>
    /// Логирование с привязкой к <see cref="GameLogFeature"/> — включение/выключение категорий в asset настроек.
    /// </summary>
    public interface IGameLogger
    {
        /// <summary>Сообщение уровня Debug (если разрешено настройками).</summary>
        void LogDebug(GameLogFeature feature, string message, Object context = null);

        /// <summary>Информационное сообщение.</summary>
        void LogInfo(GameLogFeature feature, string message, Object context = null);

        /// <summary>Предупреждение.</summary>
        void LogWarning(GameLogFeature feature, string message, Object context = null);

        /// <summary>Ошибка.</summary>
        void LogError(GameLogFeature feature, string message, Object context = null);
    }
}
