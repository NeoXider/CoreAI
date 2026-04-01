namespace CoreAI.Infrastructure.Logging
{
    /// <summary>
    /// Уровень сообщения для фильтрации (минимальный порог задаётся в <see cref="IGameLogSettings"/>).
    /// </summary>
    public enum GameLogLevel
    {
        Debug = 0,
        Info = 1,
        Warning = 2,
        Error = 3
    }
}
