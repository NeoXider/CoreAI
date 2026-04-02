namespace CoreAI.Messaging
{
    /// <summary>
    /// Типизированная команда применения решения ИИ к игре (шина Unity — MessagePipe).
    /// </summary>
    public sealed class ApplyAiGameCommand
    {
        public string CommandTypeId { get; set; } = "";

        public string JsonPayload { get; set; } = "";

        /// <summary>Роль, с которой оркестратор вызывал LLM (для маршрутизации Lua и т.д.).</summary>
        public string SourceRoleId { get; set; } = "";

        /// <summary>Исходный hint задачи (для цикла исправления Programmer).</summary>
        public string SourceTaskHint { get; set; } = "";

        /// <summary>0 — первый ответ модели; увеличивается при каждом ремонте Lua.</summary>
        public int LuaRepairGeneration { get; set; }
    }

    /// <summary>
    /// Абстракция публикации команд из портативного ядра (реализация в Source через MessagePipe).
    /// </summary>
    public interface IAiGameCommandSink
    {
        void Publish(ApplyAiGameCommand command);
    }
}
