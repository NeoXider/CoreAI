namespace CoreAI.Logging
{
    /// <summary>
    /// Универсальный интерфейс логирования для CoreAI (без зависимостей от Unity).
    /// Реализация предоставляется из внешнего слоя (Unity, консоль и т.д.).
    ///
    /// <para>Каждый метод принимает опциональный <paramref name="tag"/> —
    /// строковый идентификатор подсистемы. Стандартные теги собраны в <see cref="LogTag"/>.
    /// Unity-реализация отображает tag на <c>GameLogFeature</c> для фильтрации.</para>
    ///
    /// <para>Доступ: через DI (конструктор) ИЛИ статически через <see cref="Log.Instance"/>.
    /// При инициализации DI-контейнера <see cref="Log.Instance"/> устанавливается автоматически.</para>
    /// </summary>
    public interface ILog
    {
        /// <summary>Отладочное сообщение (наиболее подробное).</summary>
        void Debug(string message, string tag = null);

        /// <summary>Информационное сообщение.</summary>
        void Info(string message, string tag = null);

        /// <summary>Предупреждение.</summary>
        void Warn(string message, string tag = null);

        /// <summary>Ошибка.</summary>
        void Error(string message, string tag = null);
    }

    /// <summary>
    /// No-op реализация — используется до инициализации или в тестах без логгера.
    /// </summary>
    public sealed class NullLog : ILog
    {
        public static readonly NullLog Instance = new();

        public void Debug(string message, string tag = null)
        {
        }

        public void Info(string message, string tag = null)
        {
        }

        public void Warn(string message, string tag = null)
        {
        }

        public void Error(string message, string tag = null)
        {
        }
    }


    /// <summary>
    /// Глобальный статический доступ к логгеру.
    /// Устанавливается автоматически при создании DI-контейнера.
    /// Для ручного использования: <c>Log.Instance.Info("msg", LogTag.Llm)</c>.
    /// </summary>
    public static class Log
    {
        private static ILog _instance = NullLog.Instance;

        /// <summary>Текущий логгер. По умолчанию — no-op (NullLog).</summary>
        public static ILog Instance
        {
            get => _instance;
            set => _instance = value ?? NullLog.Instance;
        }
    }

    /// <summary>
    /// Стандартные теги подсистем для фильтрации логов.
    /// Unity-реализация маппит эти теги на <c>GameLogFeature</c> flags.
    /// Можно создавать собственные теги — они будут маппиться на <c>GameLogFeature.Core</c>.
    /// </summary>
    public static class LogTag
    {
        /// <summary>Общие сообщения ядра.</summary>
        public const string Core = "Core";

        /// <summary>VContainer, lifetime scope, bootstrap.</summary>
        public const string Composition = "Composition";

        /// <summary>Шина команд, Lua-пайплайн, подписки MessagePipe.</summary>
        public const string MessagePipe = "MessagePipe";

        /// <summary>Запросы/ответы LLM, tool calling.</summary>
        public const string Llm = "Llm";

        /// <summary>Метрики оркестратора.</summary>
        public const string Metrics = "Metrics";

        /// <summary>Lua sandbox, execution, repair.</summary>
        public const string Lua = "Lua";

        /// <summary>World commands (spawn, move, destroy).</summary>
        public const string World = "World";

        /// <summary>Память агентов (MemoryTool, ChatHistory).</summary>
        public const string Memory = "Memory";

        /// <summary>Конфигурация (GameConfig, Settings).</summary>
        public const string Config = "Config";
    }
}