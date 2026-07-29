using CoreAI.Logging;
using CoreAI.Unity.Logging;

#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;

namespace CoreAI.Infrastructure.Logging
{
    /// <summary>
    /// Installs a process-wide fallback game logger when no scoped logger has been registered.
    /// </summary>
#if UNITY_EDITOR
    [InitializeOnLoad]
#endif
    public static class GameLoggerUnscopedFallback
    {
        static GameLoggerUnscopedFallback()
        {
            Initialize();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Initialize()
        {
            IGameLogger _ = Instance;
        }

        private static IGameLogger _instance;

        /// <summary>Shared fallback game logger instance.</summary>
        public static IGameLogger Instance
        {
            get
            {
                if (_instance != null)
                {
                    return _instance;
                }

                // WHY: The same live filter the container registers, so runtime GameLogFilter changes
                // reach the many call sites that log without a resolved scope.
                _instance = new FilteringGameLogger(new UnityGameLogSink(), GameLogFilter.Settings);

                if (Log.Instance is NullLog)
                {
                    Log.Instance = new UnityLog(_instance);
                }

                return _instance;
            }
        }
    }
}
