namespace CoreAI.Infrastructure.Logging
{
    /// <summary>
    /// Когда компонент ещё не может взять <see cref="IGameLogger"/> из VContainer (ранний Awake и т.д.):
    /// тот же путь, что и в DI: <see cref="FilteringGameLogger"/> → <see cref="UnityGameLogSink"/> (единственное место прямого Unity Debug в рантайме).
    /// </summary>
    public static class GameLoggerUnscopedFallback
    {
        private static IGameLogger _instance;

        /// <summary>Синглтон с <see cref="DefaultGameLogSettings"/> (все категории).</summary>
        public static IGameLogger Instance =>
            _instance ??= new FilteringGameLogger(new UnityGameLogSink(), new DefaultGameLogSettings());
    }
}