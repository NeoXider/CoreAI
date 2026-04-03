using CoreAI.Ai;

namespace CoreAI.Messaging
{
    /// <summary>
    /// Типизированная команда применения решения ИИ к игре (шина Unity — MessagePipe).
    /// </summary>
    public sealed class ApplyAiGameCommand
    {
        /// <summary>Тип команды; см. <see cref="AiGameCommandTypeIds"/>.</summary>
        public string CommandTypeId { get; set; } = "";

        /// <summary>Тело команды: JSON, Lua, текст — по соглашению для данного <see cref="CommandTypeId"/>.</summary>
        public string JsonPayload { get; set; } = "";

        /// <summary>Роль, с которой оркестратор вызывал LLM (для маршрутизации Lua и т.д.).</summary>
        public string SourceRoleId { get; set; } = "";

        /// <summary>Исходный hint задачи (для цикла исправления Programmer).</summary>
        public string SourceTaskHint { get; set; } = "";

        /// <summary>Совпадает с <see cref="AiTaskRequest.SourceTag"/> у вызвавшей задачи (пусто — не задано).</summary>
        public string SourceTag { get; set; } = "";

        /// <summary>0 — первый ответ модели; увеличивается при каждом ремонте Lua.</summary>
        public int LuaRepairGeneration { get; set; }

        /// <summary>Совпадает с TraceId в <see cref="LlmCompletionRequest"/> одного вызова оркестратора.</summary>
        public string TraceId { get; set; } = "";

        /// <summary>Совпадает с <see cref="AiTaskRequest.LuaScriptVersionKey"/> (пусто — не задано).</summary>
        public string LuaScriptVersionKey { get; set; } = "";

        /// <summary>Совпадает с <see cref="AiTaskRequest.DataOverlayVersionKeysCsv"/>.</summary>
        public string DataOverlayVersionKeysCsv { get; set; } = "";
    }

    /// <summary>
    /// Абстракция публикации команд из портативного ядра (реализация в Source через MessagePipe).
    /// </summary>
    public interface IAiGameCommandSink
    {
        /// <summary>Отправить команду подписчикам шины (например MessagePipe).</summary>
        void Publish(ApplyAiGameCommand command);
    }
}
