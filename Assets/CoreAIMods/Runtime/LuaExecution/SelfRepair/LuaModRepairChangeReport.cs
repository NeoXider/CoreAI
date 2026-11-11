using System;
using System.Globalization;
using System.Text;
using CoreAI.Mods.WorldPackages;

namespace CoreAI.Ai.SelfRepair
{
    /// <summary>What a repair did to a mod's source, read cheaply from the failing and the repaired text.</summary>
    public readonly struct LuaModRepairSourceComparison
    {
        /// <summary>Creates a comparison.</summary>
        public LuaModRepairSourceComparison(int failingLines, int repairedLines, int failingHandlers, int repairedHandlers)
        {
            FailingLines = failingLines;
            RepairedLines = repairedLines;
            FailingHandlers = failingHandlers;
            RepairedHandlers = repairedHandlers;
        }

        /// <summary>Lines of the failing source that are neither blank nor a comment.</summary>
        public int FailingLines { get; }

        /// <summary>Lines of the repaired source that are neither blank nor a comment.</summary>
        public int RepairedLines { get; }

        /// <summary>Event hookups in the failing source (<c>:Connect(</c>, <c>:Once(</c>, <c>hooks_on(</c> and the like).</summary>
        public int FailingHandlers { get; }

        /// <summary>Event hookups in the repaired source.</summary>
        public int RepairedHandlers { get; }

        /// <summary>True when the repair removed event hookups the failing source had.</summary>
        public bool HandlersRemoved => RepairedHandlers < FailingHandlers;

        /// <summary>
        /// True when the repaired source keeps less than half of the failing one's code lines (only for a failing
        /// source of at least <see cref="LuaModRepairChangeReport.MinimumLinesForShrinkSignal"/> lines).
        /// </summary>
        public bool DrasticallyShorter =>
            FailingLines >= LuaModRepairChangeReport.MinimumLinesForShrinkSignal &&
            RepairedLines * 2 < FailingLines;

        /// <summary>True when the repair may have deleted the feature instead of fixing it.</summary>
        public bool IsSuspicious => HandlersRemoved || DrasticallyShorter;
    }

    /// <summary>
    /// Builds the player's line for a repair that held: what the model says it changed (its own short summary, taken
    /// from its reply), the way back, and a warning instead of "fixed" when the repair looks like it removed the
    /// feature. "Fixed" only proves that the errors stopped; a model can stop them by deleting the code that failed, so
    /// the line names two cheap signals: event hookups (<c>:Connect(</c>, <c>:ConnectParallel(</c>, <c>:Once(</c>,
    /// <c>hooks_on(</c>, <c>hooks_every(</c>) that are gone, and a repaired source with less than half the failing one's
    /// code lines. Engine-free, no allocation beyond the strings it returns.
    /// </summary>
    public static class LuaModRepairChangeReport
    {
        /// <summary>Characters of the model's summary a line keeps.</summary>
        public const int MaximumSummaryCharacters = 160;

        /// <summary>Sentences of the model's summary a line keeps.</summary>
        public const int MaximumSummarySentences = 2;

        /// <summary>The failing source must have this many code lines before "less than half" is a signal.</summary>
        public const int MinimumLinesForShrinkSignal = 8;

        /// <summary>The marker the repair request asks the model to start its summary with.</summary>
        public const string SummaryMarker = "Changed:";

        private static readonly string[] HandlerPatterns =
        {
            ":Connect(", ":ConnectParallel(", ":Once(", "hooks_on(", "hooks_every("
        };

        /// <summary>
        /// The model's summary of its change from its reply: the text after <see cref="SummaryMarker"/> when a line has
        /// it, otherwise the reply's first sentences; code blocks, reasoning blocks and markdown removed, whitespace
        /// collapsed, at most <see cref="MaximumSummarySentences"/> sentences and
        /// <see cref="MaximumSummaryCharacters"/> characters. Empty when the reply says nothing usable.
        /// </summary>
        public static string ExtractSummary(string answer)
        {
            if (string.IsNullOrWhiteSpace(answer))
            {
                return "";
            }

            string text = RemoveBlocks(answer, "<think>", "</think>");
            text = RemoveBlocks(text, "```", "```");
            string marked = AfterMarker(text);
            string plain = Plain(marked.Length > 0 ? marked : text);
            plain = FirstSentences(plain, MaximumSummarySentences);
            if (plain.Length > MaximumSummaryCharacters)
            {
                int cut = plain.LastIndexOf(' ', MaximumSummaryCharacters - 3);
                plain = plain.Substring(0, cut > MaximumSummaryCharacters / 2 ? cut : MaximumSummaryCharacters - 3)
                    .TrimEnd(' ', ',', ';', ':') + "...";
            }

            if (plain.Length < 3)
            {
                return "";
            }

            return char.IsLower(plain[0]) ? char.ToUpperInvariant(plain[0]) + plain.Substring(1) : plain;
        }

        /// <summary>Compares the failing and the repaired source.</summary>
        public static LuaModRepairSourceComparison Compare(string failingSource, string repairedSource)
        {
            return new LuaModRepairSourceComparison(CodeLines(failingSource), CodeLines(repairedSource),
                Handlers(failingSource), Handlers(repairedSource));
        }

        /// <summary>
        /// The line for a repair that held. <paramref name="undoWay"/> is how <c>/undo</c> takes it back (see
        /// <see cref="UndoWay"/>); empty leaves the undo out.
        /// </summary>
        public static string BuildFixedLine(string modId, string summary, LuaModRepairSourceComparison comparison,
            string undoWay)
        {
            string id = (modId ?? "").Trim();
            string said = string.IsNullOrWhiteSpace(summary)
                ? "the AI did not say what it changed (" + Lines(comparison.FailingLines) + " -> " +
                  Lines(comparison.RepairedLines) + ")."
                : "the AI says: " + Sentence(summary.Trim());
            StringBuilder line = new();
            if (comparison.IsSuspicious)
            {
                line.Append("'").Append(id).Append("' runs without errors now, but the fix ");
                if (comparison.HandlersRemoved)
                {
                    line.Append("removed ")
                        .Append((comparison.FailingHandlers - comparison.RepairedHandlers).ToString(CultureInfo.InvariantCulture))
                        .Append(" of its ").Append(comparison.FailingHandlers.ToString(CultureInfo.InvariantCulture))
                        .Append(comparison.FailingHandlers == 1 ? " event handler" : " event handlers");
                    if (comparison.DrasticallyShorter)
                    {
                        line.Append(" and ");
                    }
                }

                if (comparison.DrasticallyShorter)
                {
                    line.Append("kept ").Append(comparison.RepairedLines.ToString(CultureInfo.InvariantCulture))
                        .Append(" of ").Append(comparison.FailingLines.ToString(CultureInfo.InvariantCulture))
                        .Append(" lines of code");
                }

                line.Append("; ").Append(said).Append(" Check that it still does what you asked.");
            }
            else
            {
                line.Append("'").Append(id).Append("' is fixed; ").Append(said);
            }

            if (!string.IsNullOrEmpty(undoWay))
            {
                line.Append(' ').Append(undoWay);
            }

            return line.ToString();
        }

        /// <summary>How <c>/undo</c> takes a repair back, by where the undo history put it.</summary>
        public static string UndoWay(RbxWorldRepairRecording recording)
        {
            switch (recording)
            {
                case RbxWorldRepairRecording.JoinedRepairedChange:
                    return "/undo takes back your request together with this fix.";
                case RbxWorldRepairRecording.OwnEntry:
                    return "/undo takes back this fix.";
                default:
                    return "/undo takes back the AI's change.";
            }
        }

        private static string Lines(int count)
        {
            return count.ToString(CultureInfo.InvariantCulture) + (count == 1 ? " line" : " lines");
        }

        private static string Sentence(string text)
        {
            if (text.Length == 0)
            {
                return text;
            }

            char last = text[text.Length - 1];
            return last == '.' || last == '!' || last == '?' ? text : text + ".";
        }

        private static int CodeLines(string source)
        {
            if (string.IsNullOrEmpty(source))
            {
                return 0;
            }

            int count = 0;
            bool inBlockComment = false;
            string[] lines = source.Replace("\r\n", "\n").Split('\n');
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (inBlockComment)
                {
                    if (line.IndexOf("]]", StringComparison.Ordinal) >= 0)
                    {
                        inBlockComment = false;
                    }

                    continue;
                }

                if (line.StartsWith("--[[", StringComparison.Ordinal) || line.StartsWith("--[=[", StringComparison.Ordinal))
                {
                    inBlockComment = line.IndexOf("]]", 4, StringComparison.Ordinal) < 0;
                    continue;
                }

                if (line.Length > 0 && !line.StartsWith("--", StringComparison.Ordinal))
                {
                    count++;
                }
            }

            return count;
        }

        private static int Handlers(string source)
        {
            if (string.IsNullOrEmpty(source))
            {
                return 0;
            }

            int count = 0;
            foreach (string pattern in HandlerPatterns)
            {
                int index = 0;
                while ((index = source.IndexOf(pattern, index, StringComparison.Ordinal)) >= 0)
                {
                    count++;
                    index += pattern.Length;
                }
            }

            return count;
        }

        private static string RemoveBlocks(string text, string open, string close)
        {
            StringBuilder kept = new(text.Length);
            int index = 0;
            while (index < text.Length)
            {
                int start = text.IndexOf(open, index, StringComparison.OrdinalIgnoreCase);
                if (start < 0)
                {
                    kept.Append(text, index, text.Length - index);
                    break;
                }

                kept.Append(text, index, start - index).Append(' ');
                int end = text.IndexOf(close, start + open.Length, StringComparison.OrdinalIgnoreCase);
                index = end < 0 ? text.Length : end + close.Length;
            }

            return kept.ToString();
        }

        private static string AfterMarker(string text)
        {
            int at = text.IndexOf(SummaryMarker, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                return "";
            }

            int end = text.IndexOf('\n', at);
            string rest = end < 0 ? text.Substring(at + SummaryMarker.Length) : text.Substring(at + SummaryMarker.Length, end - at - SummaryMarker.Length);
            if (Plain(rest).Length < 3 && end >= 0)
            {
                // WHY: "Changed:" alone on its line, with the summary on the lines below it.
                rest = text.Substring(end + 1);
            }

            return rest;
        }

        /// <summary>
        /// Plain text: each line loses its markdown heading, quote and bullet marks, emphasis and inline-code marks go,
        /// and all whitespace collapses into single spaces.
        /// </summary>
        private static string Plain(string text)
        {
            StringBuilder plain = new(text.Length);
            bool space = false;
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            foreach (string raw in lines)
            {
                string line = raw.TrimStart(' ', '\t', '#', '>', '-', '*', '+');
                foreach (char c in line)
                {
                    if (c == '*' || c == '`')
                    {
                        continue;
                    }

                    if (char.IsWhiteSpace(c) || char.IsControl(c))
                    {
                        space = plain.Length > 0;
                        continue;
                    }

                    if (space)
                    {
                        plain.Append(' ');
                        space = false;
                    }

                    plain.Append(c);
                }

                space = plain.Length > 0;
            }

            return plain.ToString().Trim();
        }

        private static string FirstSentences(string text, int sentences)
        {
            int found = 0;
            for (int index = 0; index < text.Length; index++)
            {
                char c = text[index];
                if ((c == '.' || c == '!' || c == '?') && (index + 1 == text.Length || text[index + 1] == ' '))
                {
                    found++;
                    if (found == sentences)
                    {
                        return text.Substring(0, index + 1);
                    }
                }
            }

            return text;
        }
    }
}
