using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>Version record for a Lua script managed by CoreAI.</summary>
    public sealed class LuaScriptVersionRecord
    {
        public LuaScriptVersionRecord(
            string scriptKey,
            string originalLua,
            string currentLua,
            IReadOnlyList<LuaScriptRevision> history)
        {
            ScriptKey = scriptKey ?? "";
            OriginalLua = originalLua ?? "";
            CurrentLua = currentLua ?? "";
            History = history ?? new LuaScriptRevision[0];
        }

        public string ScriptKey { get; }
        public string OriginalLua { get; }
        public string CurrentLua { get; }
        public IReadOnlyList<LuaScriptRevision> History { get; }
    }
}