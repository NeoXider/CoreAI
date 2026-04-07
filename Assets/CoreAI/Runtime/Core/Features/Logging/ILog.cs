namespace CoreAI.Logging
{
    /// <summary>
    /// Универсальный интерфейс логирования для CoreAI.Core (без зависимостей от Unity).
    /// Реализация предоставляется из внешнего слоя (Unity, консоль и т.д.).
    /// </summary>
    public interface ILog
    {
        /// <summary>Логировать информационное сообщение.</summary>
        void Info(string message);

        /// <summary>Логировать предупреждение.</summary>
        void Warn(string message);

        /// <summary>Логировать ошибку.</summary>
        void Error(string message);
    }

    /// <summary>
    /// No-op реализация — используется по умолчанию когда логгер не установлен.
    /// </summary>
    public sealed class NullLog : ILog
    {
        public static readonly NullLog Instance = new();
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message) { }
    }

    /// <summary>
    /// Глобальный доступ к логгеру. Устанавливается при инициализации системы.
    /// </summary>
    public static class Log
    {
        private static ILog _instance = NullLog.Instance;

        /// <summary>Текущий логгер. По умолчанию — no-op.</summary>
        public static ILog Instance
        {
            get => _instance;
            set => _instance = value ?? NullLog.Instance;
        }
    }
}
