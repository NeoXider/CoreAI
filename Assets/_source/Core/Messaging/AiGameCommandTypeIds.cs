namespace CoreAI.Messaging
{
    /// <summary>Строковые идентификаторы <see cref="ApplyAiGameCommand.CommandTypeId"/>.</summary>
    public static class AiGameCommandTypeIds
    {
        public const string Envelope = "AiEnvelope";
        public const string LuaExecutionSucceeded = "LuaExecutionSucceeded";
        public const string LuaExecutionFailed = "LuaExecutionFailed";
    }
}
