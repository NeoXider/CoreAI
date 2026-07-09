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
        public const string LuaMods = "LuaMods";
        public const string ModPackages = "Mods";
        public const string LuaScriptVersions = "LuaScriptVersions";
        public const string DataOverlayVersions = "DataOverlayVersions";

        /// <summary>Folder for agent-authored skills persisted by <c>FileSkillStore</c>.</summary>
        public const string Skills = "Skills";

        /// <summary>Folder for world-object snapshots persisted by <c>WorldStateManager</c>.</summary>
        public const string WorldState = "WorldState";
    }
}