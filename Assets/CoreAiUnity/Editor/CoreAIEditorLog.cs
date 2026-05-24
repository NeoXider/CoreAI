using UnityEngine;

namespace CoreAI.Editor
{
    /// <summary>
    /// Writes CoreAI editor diagnostics to the Unity console.
    /// </summary>
    internal static class CoreAIEditorLog
    {
        private const string Prefix = "[CoreAI] ";

        internal static void Log(string message)
        {
            Debug.Log(Prefix + message);
        }

        internal static void LogWarning(string message)
        {
            Debug.LogWarning(Prefix + message);
        }

        internal static void LogError(string message)
        {
            Debug.LogError(Prefix + message);
        }
    }
}
