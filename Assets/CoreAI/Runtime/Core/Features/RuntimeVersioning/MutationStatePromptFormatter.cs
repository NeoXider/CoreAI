using System.Collections.Generic;
using System.Text;

namespace CoreAI.Ai
{
    /// <summary>Formats mutation state for inclusion in AI prompts.</summary>
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
                    sb.Append("lua_original_baseline:\n```lua\n").Append(Clamp(luaSnapshot.OriginalLua, luaKey, "lua_original_baseline"))
                        .Append("\n```\n");
                    sb.Append("lua_current:\n```lua\n").Append(Clamp(luaSnapshot.CurrentLua, luaKey, "lua_current")).Append("\n```\n");
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
                    sb.Append("data_original_baseline:\n```json\n").Append(Clamp(snap.OriginalPayload, key, "data_original_baseline"))
                        .Append("\n```\n");
                    sb.Append("data_current:\n```json\n").Append(Clamp(snap.CurrentPayload, key, "data_current")).Append("\n```\n");
                }
            }

            return sb.ToString();
        }

        private static string Clamp(string s, string key, string field)
        {
            return VersionPromptClip.Clamp(s, MaxChars, nameof(MutationStatePromptFormatter), key, field);
        }
    }

    /// <summary>
    /// The shared clip for stored Lua / data snapshots in versioning prompts: the kept prefix, then
    /// <c>…[+N chars]</c> on its own line inside the code fence. The snapshot is re-sent every turn unchanged, so
    /// the cut is logged once per formatter, key, field and length.
    /// </summary>
    internal static class VersionPromptClip
    {
        internal static string Clamp(string s, int maxChars, string formatter, string key, string field)
        {
            if (string.IsNullOrEmpty(s))
            {
                return "";
            }

            string clipped = TruncationMarker.ClipBlock(s, maxChars, out int dropped);
            if (dropped > 0)
            {
                TruncationMarker.LogOnce(null, $"{formatter}|{key}|{field}|{s.Length}",
                    $"[{formatter}] '{key}' {field} clipped for the prompt: {s.Length} chars total -> " +
                    $"{s.Length - dropped} shown, {dropped} dropped (limit {maxChars}). Logged once per distinct snapshot.");
            }

            return clipped;
        }
    }
}
