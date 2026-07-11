using System.Text;

namespace CoreAI.Ai
{
    /// <summary>Formats Lua script version data for programmer prompts.</summary>
    public static class LuaScriptVersionPromptFormatter
    {
        private const int MaxLuaChars = 6000;

        /// <summary>Formats a Lua script version snapshot for inclusion in an LLM prompt.</summary>
        public static string Format(string scriptKey, LuaScriptVersionRecord snapshot)
        {
            if (string.IsNullOrWhiteSpace(scriptKey))
            {
                return "";
            }

            StringBuilder sb = new(256);
            sb.Append("## Lua_script_versioning\n");
            sb.Append("script_key: ").Append(scriptKey.Trim()).Append('\n');
            if (snapshot == null)
            {
                sb.Append(
                    "No saved revisions yet for this key. After the first successful sandbox run, the executed Lua becomes the original baseline.\n");
                sb.Append(
                    "If the game seeded a baseline via SeedOriginal, it will appear on the next Programmer call once the store has been updated.\n");
                return sb.ToString();
            }

            sb.Append("revision_count: ").Append(snapshot.History.Count).Append('\n');
            sb.Append("original_lua_baseline (first accepted / seeded; use as revert target):\n```lua\n");
            sb.Append(Clamp(snapshot.OriginalLua)).Append("\n```\n");
            sb.Append("current_saved_lua (last successful execution):\n```lua\n");
            sb.Append(Clamp(snapshot.CurrentLua)).Append("\n```\n");
            if (!string.Equals(snapshot.OriginalLua, snapshot.CurrentLua))
            {
                sb.Append(
                    "The baseline and current differ. Prefer fixing forward; users can reset to baseline outside the model.\n");
            }

            return sb.ToString();
        }

        private static string Clamp(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return "";
            }

            if (s.Length <= MaxLuaChars)
            {
                return s;
            }

            return s.Substring(0, MaxLuaChars) + "\n...";
        }
    }
}
