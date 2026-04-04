using UnityEngine;

namespace CoreAI.Infrastructure.Logging
{
    /// <summary>Тесты и заглушки: без вывода.</summary>
    public sealed class NullGameLogger : IGameLogger
    {
        /// <inheritdoc />
        public void LogDebug(GameLogFeature feature, string message, Object context = null)
        {
        }

        /// <inheritdoc />
        public void LogInfo(GameLogFeature feature, string message, Object context = null)
        {
        }

        /// <inheritdoc />
        public void LogWarning(GameLogFeature feature, string message, Object context = null)
        {
        }

        /// <inheritdoc />
        public void LogError(GameLogFeature feature, string message, Object context = null)
        {
        }
    }
}