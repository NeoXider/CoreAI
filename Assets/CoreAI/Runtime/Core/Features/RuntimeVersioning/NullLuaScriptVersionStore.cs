using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>Заглушка: версионирование выключено (по умолчанию в портативном инсталлере до override в Unity).</summary>
    public sealed class NullLuaScriptVersionStore : ILuaScriptVersionStore
    {
        public bool TryGetSnapshot(string scriptKey, out LuaScriptVersionRecord snapshot)
        {
            snapshot = null;
            return false;
        }

        public void RecordSuccessfulExecution(string scriptKey, string executedLuaSource)
        {
        }

        public void SeedOriginal(string scriptKey, string originalLuaSource, bool overwriteExistingOriginal = false)
        {
        }

        public void ResetToOriginal(string scriptKey)
        {
        }

        public void ResetToRevision(string scriptKey, int revisionIndex)
        {
        }

        public void ResetAllToOriginal()
        {
        }

        public IReadOnlyList<string> GetKnownKeys()
        {
            return System.Array.Empty<string>();
        }

        public string BuildProgrammerPromptSection(string scriptKey)
        {
            return "";
        }
    }
}