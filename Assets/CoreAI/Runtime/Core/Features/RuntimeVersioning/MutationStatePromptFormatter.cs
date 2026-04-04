using System.Collections.Generic;
using System.Text;

namespace CoreAI.Ai
{
    /// <summary>Единый блок mutation state для Programmer: Lua + data overlays.</summary>
    public static class MutationStatePromptFormatter
    {
        private const int MaxChars = 5000;

        public static string Format(
            string luaKey,
            LuaScriptVersionRecord luaSnapshot,
            IReadOnlyList<string> dataKeys,
            IReadOnlyList<DataOverlayVersionRecord> dataSnapshots)
        {
            StringBuilder sb = new(512);
            sb.Append("## Mutation_state\n");

            if (!string.IsNullOrWhiteSpace(luaKey))
            {
                sb.Append("lua_key: ").Append(luaKey.Trim()).Append('\n');
                if (luaSnapshot == null)
                {
                    sb.Append("lua_revision_count: 0\n");
                }
                else
                {
                    sb.Append("lua_revision_count: ").Append(luaSnapshot.History.Count).Append('\n');
                    sb.Append("lua_original_baseline:\n```lua\n").Append(Clamp(luaSnapshot.OriginalLua))
                        .Append("\n```\n");
                    sb.Append("lua_current:\n```lua\n").Append(Clamp(luaSnapshot.CurrentLua)).Append("\n```\n");
                }
            }

            if (dataKeys != null && dataKeys.Count > 0)
            {
                for (int i = 0; i < dataKeys.Count; i++)
                {
                    string key = dataKeys[i] ?? "";
                    DataOverlayVersionRecord snap = dataSnapshots != null && i < dataSnapshots.Count
                        ? dataSnapshots[i]
                        : null;
                    sb.Append("data_key: ").Append(key).Append('\n');
                    if (snap == null)
                    {
                        sb.Append("data_revision_count: 0\n");
                        continue;
                    }

                    sb.Append("data_revision_count: ").Append(snap.History.Count).Append('\n');
                    sb.Append("data_original_baseline:\n```json\n").Append(Clamp(snap.OriginalPayload))
                        .Append("\n```\n");
                    sb.Append("data_current:\n```json\n").Append(Clamp(snap.CurrentPayload)).Append("\n```\n");
                }
            }

            return sb.ToString();
        }

        private static string Clamp(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return "";
            }

            return s.Length <= MaxChars ? s : s.Substring(0, MaxChars) + "\n…";
        }
    }
}