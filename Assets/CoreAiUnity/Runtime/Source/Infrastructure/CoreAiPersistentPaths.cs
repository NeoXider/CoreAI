namespace CoreAI.Infrastructure
{
    /// <summary>
    /// Folder segments under <see cref="UnityEngine.Application.persistentDataPath"/> used by CoreAI file-backed stores.
    /// </summary>
    public static class CoreAiPersistentPaths
    {
        public const string RootFolderName = "CoreAI";
        public const string ConversationSummaries = "ConversationSummaries";
        public const string AgentMemory = "AgentMemory";
        public const string LuaScriptVersions = "LuaScriptVersions";
        public const string DataOverlayVersions = "DataOverlayVersions";
    }
}
