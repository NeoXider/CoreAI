using UnityEngine;

namespace CoreAI.Editor
{
    /// <summary>
    /// Единственная точка вывода в Unity Console для Editor-скриптов CoreAiUnity (аналог UnityGameLogSink в рантайме).
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