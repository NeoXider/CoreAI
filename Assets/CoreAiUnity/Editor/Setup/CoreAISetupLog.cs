using UnityEngine;

namespace CoreAI.Editor
{
    /// <summary>
    /// Console output of the setup menus. A copy of <c>CoreAIEditorLog</c>'s prefix rule, kept in this assembly
    /// because the setup menus must compile before any other CoreAI assembly does.
    /// </summary>
    internal static class CoreAISetupLog
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
    }
}
