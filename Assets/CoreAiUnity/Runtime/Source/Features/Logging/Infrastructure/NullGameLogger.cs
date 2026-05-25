using UnityEngine;

namespace CoreAI.Infrastructure.Logging
{
    /// <summary>Game logger used when Unity-facing logging is intentionally disabled.</summary>
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
