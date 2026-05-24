using UnityEngine;

namespace CoreAI.Infrastructure.Logging
{
    /// <summary>
    /// Logging abstraction for Unity-facing CoreAI diagnostics.
    /// </summary>
    public interface IGameLogger
    {
        /// <summary>Executes log debug.</summary>
        void LogDebug(GameLogFeature feature, string message, Object context = null);

        /// <summary>Executes log info.</summary>
        void LogInfo(GameLogFeature feature, string message, Object context = null);

        /// <summary>Executes log warning.</summary>
        void LogWarning(GameLogFeature feature, string message, Object context = null);

        /// <summary>Executes log error.</summary>
        void LogError(GameLogFeature feature, string message, Object context = null);
    }
}
