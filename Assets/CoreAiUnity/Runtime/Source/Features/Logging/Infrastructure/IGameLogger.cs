using UnityEngine;

namespace CoreAI.Infrastructure.Logging
{
    /// <summary>
    /// Logging abstraction for Unity-facing CoreAI diagnostics.
    /// </summary>
    public interface IGameLogger
    {
        /// <summary>Writes a debug message for the selected feature.</summary>
        void LogDebug(GameLogFeature feature, string message, Object context = null);

        /// <summary>Writes an informational message for the selected feature.</summary>
        void LogInfo(GameLogFeature feature, string message, Object context = null);

        /// <summary>Writes a warning message for the selected feature.</summary>
        void LogWarning(GameLogFeature feature, string message, Object context = null);

        /// <summary>Writes an error message for the selected feature.</summary>
        void LogError(GameLogFeature feature, string message, Object context = null);
    }
}
