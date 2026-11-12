using System;
using System.Collections.Generic;
using System.Text;

namespace CoreAI.Diagnostics
{
    /// <summary>
    /// Renders engine log entries as compact text for a model or a debug client: one line per entry
    /// (<c>[seq] LEVEL message</c>), oldest first, identical consecutive entries folded into one line with
    /// <c>(xN)</c>. Over the character budget the NEWEST lines are kept and the first line says how many
    /// entries were left out.
    /// </summary>
    public static class RuntimeLogFormatter
    {
        /// <summary>Prefix of the first line when entries did not fit the budget.</summary>
        public const string OmittedMarkerPrefix = "...(+";

        /// <summary>Formats <paramref name="entries"/> within <paramref name="maxChars"/> characters.</summary>
        /// <param name="entries">Entries, oldest first.</param>
        /// <param name="maxChars">Hard cap on the returned text.</param>
        /// <param name="stackTraceLines">Stack trace lines kept per entry that has one; 0 drops them.</param>
        public static string Format(IReadOnlyList<RuntimeLogEntry> entries, int maxChars, int stackTraceLines = 0)
        {
            if (entries == null || entries.Count == 0 || maxChars <= 0)
            {
                return "";
            }

            List<(string Text, int Raw)> groups = new();
            for (int index = 0; index < entries.Count; index++)
            {
                RuntimeLogEntry first = entries[index];
                int end = index;
                while (end + 1 < entries.Count && entries[end + 1].Level == first.Level &&
                       string.Equals(entries[end + 1].Message, first.Message, StringComparison.Ordinal))
                {
                    end++;
                }

                groups.Add((Line(first, entries[end], end - index + 1, stackTraceLines), end - index + 1));
                index = end;
            }

            int[] rawBefore = new int[groups.Count + 1];
            for (int i = 0; i < groups.Count; i++) rawBefore[i + 1] = rawBefore[i] + groups[i].Raw;

            int kept = groups.Count;
            int used = 0;
            for (int i = groups.Count - 1; i >= 0; i--)
            {
                int cost = used + (used > 0 ? 1 : 0) + groups[i].Text.Length;
                int markerCost = rawBefore[i] > 0 ? Marker(rawBefore[i]).Length + 1 : 0;
                if (cost + markerCost > maxChars)
                {
                    break;
                }

                used = cost;
                kept = i;
            }

            int omitted = rawBefore[kept];

            if (kept == groups.Count)
            {
                // WHY: not even the newest line fits; show its head rather than nothing.
                string newest = groups[groups.Count - 1].Text;
                int before = rawBefore[groups.Count - 1];
                string prefix = before > 0 ? Marker(before) + "\n" : "";
                int room = maxChars - prefix.Length;
                return room <= 0 ? "" : prefix + newest.Substring(0, Math.Min(newest.Length, room));
            }

            StringBuilder text = new();
            if (omitted > 0)
            {
                text.Append(Marker(omitted));
            }

            for (int i = kept; i < groups.Count; i++)
            {
                if (text.Length > 0) text.Append('\n');
                text.Append(groups[i].Text);
            }

            return text.ToString();
        }

        private static string Marker(int omitted) => OmittedMarkerPrefix + omitted + " more)";

        private static string Line(RuntimeLogEntry first, RuntimeLogEntry last, int repeats, int stackTraceLines)
        {
            StringBuilder line = new();
            line.Append('[').Append(first.Sequence);
            if (repeats > 1) line.Append('-').Append(last.Sequence);
            line.Append("] ").Append(RuntimeLogLevels.Tag(first.Level)).Append(' ').Append(first.Message.Trim());
            if (repeats > 1) line.Append(" (x").Append(repeats).Append(')');
            if (stackTraceLines > 0 && last.StackTrace.Length > 0)
            {
                string[] frames = last.StackTrace.Split('\n');
                int written = 0;
                foreach (string frame in frames)
                {
                    string trimmed = frame.Trim();
                    if (trimmed.Length == 0) continue;
                    line.Append("\n    ").Append(trimmed);
                    if (++written >= stackTraceLines) break;
                }
            }

            return line.ToString();
        }
    }
}
