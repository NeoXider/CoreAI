using CoreAI.Logging;
using CoreAI.Unity.Logging;

namespace CoreAI.Infrastructure.Logging
{
    /// <summary>
    /// Когда компонент ещё не может взять <see cref="ILog"/> из VContainer (ранний Awake и т.д.):
    /// тот же путь, что и в DI: <see cref="FilteringGameLogger"/> → <see cref="UnityGameLogSink"/>.
    /// Автоматически устанавливает <see cref="Log.Instance"/> при первом обращении.
    /// </summary>
    public static class GameLoggerUnscopedFallback
    {
        private static IGameLogger _instance;

        /// <summary>Синглтон с <see cref="DefaultGameLogSettings"/> (все категории).</summary>
        public static IGameLogger Instance
        {
            get
            {
                if (_instance != null)
                {
                    return _instance;
                }

                _instance = new FilteringGameLogger(new UnityGameLogSink(), new DefaultGameLogSettings());

                // Если ILog ещё не установлен DI — предоставляем fallback
                if (Log.Instance is NullLog)
                {
                    Log.Instance = new UnityLog(_instance);
                }

                return _instance;
            }
        }
    }
}