namespace CoreAI.Messaging
{
    /// <summary>Строковые идентификаторы <see cref="ApplyAiGameCommand.CommandTypeId"/>.</summary>
    public static class AiGameCommandTypeIds
    {
        /// <summary>Текстовый конверт от LLM (JSON/markdown) для разбора и Lua.</summary>
        public const string Envelope = "AiEnvelope";

        /// <summary>Песочница успешно выполнила извлечённый Lua.</summary>
        public const string LuaExecutionSucceeded = "LuaExecutionSucceeded";

        /// <summary>Ошибка выполнения Lua; может запустить цикл ремонта Programmer.</summary>
        public const string LuaExecutionFailed = "LuaExecutionFailed";
    }
}
