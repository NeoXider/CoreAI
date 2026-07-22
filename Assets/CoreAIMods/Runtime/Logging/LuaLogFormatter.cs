using System.Collections.Generic;
using System.Text;

namespace CoreAI.Ai.Logging
{
    /// <summary>
    /// Renders <see cref="LuaLogEntry"/> lists into compact, LLM-friendly plain text for the
    /// AI-facing surface (<c>GetModLogsLlmTool</c>): one line per entry, oldest-first, hard-capped to
    /// a character budget. Identical consecutive messages are coalesced into one line with a
    /// <c>×N</c> suffix. When the budget is exceeded the NEWEST entries are kept (the AI cares about
    /// recent events) and a <c>...(+N more)</c> marker on the first line names how many raw entries
    /// were dropped.
    /// </summary>
    public static class LuaLogFormatter
    {
        /// <summary>Prefix of the first-line marker emitted when entries had to be dropped to fit <c>maxChars</c>.</summary>
        public const string TruncationMarkerPrefix = "...(+";

        /// <summary>
        /// Formats <paramref name="entries"/> as one compact line each
        /// (<c>[seq] LEVEL modId script:line - message</c>), oldest-first, coalescing identical
        /// consecutive messages into a single <c>×N</c> line. When not everything fits into
        /// <paramref name="maxChars"/>, the newest entries are kept and a <c>...(+N more)</c> marker
        /// is placed at the top. The returned string never exceeds <paramref name="maxChars"/>.
        /// </summary>
        public static string ToPromptText(IEnumerable<LuaLogEntry> entries, int maxChars)
        {
            if (entries == null || maxChars <= 0)
            {
                return "";
            }

            List<LuaLogEntry> list = entries as List<LuaLogEntry> ?? new List<LuaLogEntry>(entries);
            List<(string Line, int RawCount)> groups = Coalesce(list);
            if (groups.Count == 0)
            {
                return "";
            }

            // WHY: rawBefore[i] = raw entries covered by groups [0..i-1]; dropping everything before
            // group i drops exactly rawBefore[i] entries, which sizes the truncation marker.
            int[] rawBefore = new int[groups.Count + 1];
            for (int i = 0; i < groups.Count; i++)
            {
                rawBefore[i + 1] = rawBefore[i] + groups[i].RawCount;
            }

            // WHY: fill newest-first — walk groups from the end and keep adding while both the lines
            // and the marker that the remaining dropped count would require still fit the budget.
            int keptFrom = groups.Count;
            int usedChars = 0;
            for (int i = groups.Count - 1; i >= 0; i--)
            {
                int candidate = usedChars + (usedChars > 0 ? 1 : 0) + groups[i].Line.Length;
                int markerCost = rawBefore[i] > 0 ? FormatMarker(rawBefore[i]).Length + 1 : 0;
                if (candidate + markerCost > maxChars)
                {
                    break;
                }

                usedChars = candidate;
                keptFrom = i;
            }

            int dropped = rawBefore[keptFrom];
            StringBuilder sb = new();
            if (dropped > 0)
            {
                string marker = FormatMarker(dropped);
                if (keptFrom == groups.Count && marker.Length > maxChars)
                {
                    return "";
                }

                sb.Append(marker);
            }

            for (int i = keptFrom; i < groups.Count; i++)
            {
                if (sb.Length > 0)
                {
                    sb.Append('\n');
                }

                sb.Append(groups[i].Line);
            }

            return sb.ToString();
        }

        /// <summary>
        /// Merges identical consecutive entries (same mod, level and message) into one rendered line
        /// with a <c>×N</c> suffix, using the newest entry of the run for sequence/location data.
        /// </summary>
        private static List<(string Line, int RawCount)> Coalesce(List<LuaLogEntry> list)
        {
            List<(string Line, int RawCount)> groups = new();
            int i = 0;
            while (i < list.Count)
            {
                int runEnd = i;
                while (runEnd + 1 < list.Count && SameMessage(list[runEnd], list[runEnd + 1]))
                {
                    runEnd++;
                }

                int count = runEnd - i + 1;
                string line = FormatLine(list[runEnd]);
                if (count > 1)
                {
                    line = $"{line} ×{count}";
                }

                groups.Add((line, count));
                i = runEnd + 1;
            }

            return groups;
        }

        private static bool SameMessage(LuaLogEntry a, LuaLogEntry b)
        {
            return a.Level == b.Level && a.ModId == b.ModId && a.Message == b.Message;
        }

        private static string FormatMarker(int droppedCount)
        {
            return $"{TruncationMarkerPrefix}{droppedCount} more)";
        }

        private static string FormatLine(LuaLogEntry entry)
        {
            string levelTag = entry.Level.ToString().ToUpperInvariant();
            string modId = string.IsNullOrEmpty(entry.ModId) ? "?" : entry.ModId;
            string location = string.IsNullOrEmpty(entry.ScriptName)
                ? ""
                : entry.Line.HasValue
                    ? $" {entry.ScriptName}:{entry.Line.Value}"
                    : $" {entry.ScriptName}";

            return $"[{entry.Sequence}] {levelTag} {modId}{location} - {entry.Message}";
        }
    }
}
