namespace CoreAI.Infrastructure.Logging
{
    /// <summary>
    /// Уровень сообщения для фильтрации (минимальный порог задаётся в <see cref="IGameLogSettings"/>).
    /// </summary>
    public enum GameLogLevel
    {
        /// <summary>Самый подробный уровень.</summary>
        Debug = 0,

        /// <summary>Обычные информационные сообщения.</summary>
        Info = 1,

        /// <summary>Предупреждения.</summary>
        Warning = 2,

        /// <summary>Ошибки.</summary>
        Error = 3
    }
}
