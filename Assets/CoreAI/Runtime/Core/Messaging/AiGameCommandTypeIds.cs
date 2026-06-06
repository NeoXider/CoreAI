namespace CoreAI.Messaging
{
    /// <summary>Defines stable command type identifiers used by AI game command routing.</summary>
    public static class AiGameCommandTypeIds
    {
        /// <summary>Envelope.</summary>
        public const string Envelope = "AiEnvelope";

        /// <summary>Lua execution succeeded.</summary>
        public const string LuaExecutionSucceeded = "LuaExecutionSucceeded";

        /// <summary>Lua execution failed.</summary>
        public const string LuaExecutionFailed = "LuaExecutionFailed";

        /// <summary>
        /// Command type id for Unity world commands.
        /// </summary>
        public const string WorldCommand = "WorldCommand";

        /// <summary>Data overlay applied.</summary>
        public const string DataOverlayApplied = "DataOverlayApplied";
    }
}