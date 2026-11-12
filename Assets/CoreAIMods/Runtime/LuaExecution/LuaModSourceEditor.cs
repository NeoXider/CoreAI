using System;
using System.Collections.Generic;
using System.Text;

namespace CoreAI.Ai
{
    /// <summary>What <see cref="LuaModSourceEditor.Replace"/> did with a mod's source.</summary>
    internal enum LuaModSourceEditStatus
    {
        /// <summary><c>old_text</c> was replaced; <see cref="LuaModSourceEdit.Source"/> is the new source.</summary>
        Applied = 0,

        /// <summary><c>old_text</c> occurs nowhere in the source, not even with CRLF line endings read as LF.</summary>
        NotFound = 1,

        /// <summary><c>old_text</c> occurs more than once and replacing every occurrence was not asked for.</summary>
        Ambiguous = 2,

        /// <summary><c>old_text</c> and <c>new_text</c> are the same text once line endings are ignored.</summary>
        NoChange = 3
    }

    /// <summary>The outcome of one <see cref="LuaModSourceEditor.Replace"/>.</summary>
    internal sealed class LuaModSourceEdit
    {
        public LuaModSourceEdit(LuaModSourceEditStatus status, string source, int occurrences,
            IReadOnlyList<int> lines, int firstLine, int lastLine, int totalLines, bool readCrlfAsLf)
        {
            Status = status;
            Source = source;
            Occurrences = occurrences;
            Lines = lines;
            FirstLine = firstLine;
            LastLine = lastLine;
            TotalLines = totalLines;
            ReadCrlfAsLf = readCrlfAsLf;
        }

        /// <summary>What happened.</summary>
        public LuaModSourceEditStatus Status { get; }

        /// <summary>The edited source when <see cref="Status"/> is <see cref="LuaModSourceEditStatus.Applied"/>; otherwise the source unchanged.</summary>
        public string Source { get; }

        /// <summary>How many times <c>old_text</c> was found (0 when not found).</summary>
        public int Occurrences { get; }

        /// <summary>
        /// 1-based line numbers for the model to look at: where each occurrence starts (Ambiguous), or the lines that
        /// hold the first line of <c>old_text</c> when the whole of it was not found (NotFound). At most
        /// <see cref="LuaModSourceEditor.MaxListedLines"/>.
        /// </summary>
        public IReadOnlyList<int> Lines { get; }

        /// <summary>Applied: the line of the edited source where the first replacement starts.</summary>
        public int FirstLine { get; }

        /// <summary>Applied: the line of the edited source where the last replacement ends.</summary>
        public int LastLine { get; }

        /// <summary>Applied: how many lines the edited source has; otherwise how many the source has.</summary>
        public int TotalLines { get; }

        /// <summary>True when <c>old_text</c> was found only after reading CRLF line endings as LF on both sides.</summary>
        public bool ReadCrlfAsLf { get; }
    }

    /// <summary>
    /// The text half of <c>manage_mods edit</c> and <c>get_source</c> paging: line numbering and exact replacement in a
    /// mod's source, with no runtime involved. A line is the text up to and including its <c>\n</c>, so the lines of a
    /// source concatenate back to it exactly.
    /// </summary>
    /// <remarks>
    /// <c>old_text</c> must occur in the source exactly. When it does not, it is looked up once more with every CRLF
    /// read as LF on both sides: a source saved on Windows can hold CRLF while a model writes LF (or the reverse), and
    /// a correct edit must not be refused for that alone. <c>new_text</c> is always written in the line endings the
    /// source mostly uses, so an edit does not mix them. No other whitespace is ignored: indentation and spaces must
    /// match, because Lua strings and long comments can depend on them.
    /// </remarks>
    internal static class LuaModSourceEditor
    {
        /// <summary>Most line numbers a refusal lists.</summary>
        internal const int MaxListedLines = 10;

        /// <summary>The start index of every line; empty for an empty source. A final <c>\n</c> starts no new line.</summary>
        public static IReadOnlyList<int> LineStarts(string source)
        {
            string text = source ?? "";
            List<int> starts = new();
            if (text.Length == 0)
            {
                return starts;
            }

            starts.Add(0);
            for (int index = 0; index < text.Length; index++)
            {
                if (text[index] == '\n' && index + 1 < text.Length)
                {
                    starts.Add(index + 1);
                }
            }

            return starts;
        }

        /// <summary>True when the two texts are the same once CRLF is read as LF.</summary>
        public static bool IsSameText(string left, string right)
        {
            return string.Equals(ToLf(left), ToLf(right), StringComparison.Ordinal);
        }

        /// <summary>
        /// Replaces <paramref name="oldText"/> with <paramref name="newText"/> in <paramref name="source"/>: the only
        /// occurrence, or every non-overlapping occurrence when <paramref name="replaceAll"/> is true.
        /// </summary>
        /// <exception cref="ArgumentException"><paramref name="oldText"/> is null or empty.</exception>
        public static LuaModSourceEdit Replace(string source, string oldText, string newText, bool replaceAll)
        {
            if (string.IsNullOrEmpty(oldText))
            {
                throw new ArgumentException("old_text is empty.", nameof(oldText));
            }

            string text = source ?? "";
            string replacementText = newText ?? "";
            if (IsSameText(oldText, replacementText))
            {
                return new LuaModSourceEdit(LuaModSourceEditStatus.NoChange, text, 0, Array.Empty<int>(), 0, 0,
                    LineStarts(text).Count, false);
            }

            List<int> starts = new();
            List<int> ends = new();
            FindExact(text, oldText, starts, ends);
            bool readCrlfAsLf = false;
            if (starts.Count == 0 && (oldText.IndexOf('\r') >= 0 || text.IndexOf('\r') >= 0))
            {
                FindReadingCrlfAsLf(text, oldText, starts, ends);
                readCrlfAsLf = starts.Count > 0;
            }

            if (starts.Count == 0)
            {
                return new LuaModSourceEdit(LuaModSourceEditStatus.NotFound, text, 0, LinesHolding(text, oldText), 0,
                    0, LineStarts(text).Count, false);
            }

            if (starts.Count > 1 && !replaceAll)
            {
                return new LuaModSourceEdit(LuaModSourceEditStatus.Ambiguous, text, starts.Count,
                    LinesAt(text, starts), 0, 0, LineStarts(text).Count, readCrlfAsLf);
            }

            string replacement = InLineEndingsOf(text, replacementText);
            StringBuilder built = new(text.Length + Math.Max(0, replacement.Length - (ends[0] - starts[0])) * starts.Count);
            int copied = 0;
            int firstStart = 0;
            int lastStart = 0;
            for (int index = 0; index < starts.Count; index++)
            {
                built.Append(text, copied, starts[index] - copied);
                if (index == 0)
                {
                    firstStart = built.Length;
                }

                lastStart = built.Length;
                built.Append(replacement);
                copied = ends[index];
            }

            built.Append(text, copied, text.Length - copied);
            string edited = built.ToString();
            int totalLines = LineStarts(edited).Count;
            int firstLine = Math.Min(LineAt(edited, firstStart), Math.Max(1, totalLines));
            int lastLine = replacement.Length == 0
                ? Math.Min(LineAt(edited, lastStart), Math.Max(1, totalLines))
                : LineAt(edited, lastStart + replacement.Length - 1);
            return new LuaModSourceEdit(LuaModSourceEditStatus.Applied, edited, starts.Count, Array.Empty<int>(),
                firstLine, Math.Max(firstLine, lastLine), totalLines, readCrlfAsLf);
        }

        private static string ToLf(string text)
        {
            return (text ?? "").Replace("\r\n", "\n");
        }

        private static void FindExact(string text, string find, List<int> starts, List<int> ends)
        {
            int from = 0;
            while (from <= text.Length - find.Length)
            {
                int at = text.IndexOf(find, from, StringComparison.Ordinal);
                if (at < 0)
                {
                    return;
                }

                starts.Add(at);
                ends.Add(at + find.Length);
                from = at + find.Length;
            }
        }

        /// <summary>
        /// Finds <paramref name="find"/> in an LF view of <paramref name="text"/> and maps every match back to the
        /// range it covers in the original, so a matched CRLF is replaced whole.
        /// </summary>
        private static void FindReadingCrlfAsLf(string text, string find, List<int> starts, List<int> ends)
        {
            StringBuilder view = new(text.Length);
            int[] original = new int[text.Length + 1];
            int index = 0;
            while (index < text.Length)
            {
                original[view.Length] = index;
                if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
                {
                    view.Append('\n');
                    index += 2;
                }
                else
                {
                    view.Append(text[index]);
                    index++;
                }
            }

            original[view.Length] = text.Length;
            string lfText = view.ToString();
            string lfFind = ToLf(find);
            if (lfFind.Length == 0)
            {
                return;
            }

            List<int> viewStarts = new();
            List<int> viewEnds = new();
            FindExact(lfText, lfFind, viewStarts, viewEnds);
            for (int match = 0; match < viewStarts.Count; match++)
            {
                starts.Add(original[viewStarts[match]]);
                ends.Add(original[viewEnds[match]]);
            }
        }

        /// <summary><paramref name="text"/> in CRLF when the source mostly uses CRLF, otherwise in LF.</summary>
        private static string InLineEndingsOf(string source, string text)
        {
            string lf = ToLf(text);
            int newlines = 0;
            int crlf = 0;
            for (int index = 0; index < source.Length; index++)
            {
                if (source[index] == '\n')
                {
                    newlines++;
                    if (index > 0 && source[index - 1] == '\r')
                    {
                        crlf++;
                    }
                }
            }

            return crlf * 2 > newlines ? lf.Replace("\n", "\r\n") : lf;
        }

        /// <summary>1-based line of the character at <paramref name="index"/>.</summary>
        private static int LineAt(string text, int index)
        {
            int line = 1;
            int end = Math.Min(index, text.Length);
            for (int position = 0; position < end; position++)
            {
                if (text[position] == '\n')
                {
                    line++;
                }
            }

            return line;
        }

        private static IReadOnlyList<int> LinesAt(string text, List<int> starts)
        {
            List<int> lines = new(Math.Min(starts.Count, MaxListedLines));
            int line = 1;
            int position = 0;
            for (int match = 0; match < starts.Count && lines.Count < MaxListedLines; match++)
            {
                for (; position < starts[match]; position++)
                {
                    if (text[position] == '\n')
                    {
                        line++;
                    }
                }

                lines.Add(line);
            }

            return lines;
        }

        /// <summary>The lines that contain the first non-blank line of <paramref name="find"/>, trimmed.</summary>
        private static IReadOnlyList<int> LinesHolding(string text, string find)
        {
            string probe = "";
            foreach (string line in ToLf(find).Split('\n'))
            {
                if (line.Trim().Length > 0)
                {
                    probe = line.Trim();
                    break;
                }
            }

            List<int> lines = new();
            if (probe.Length == 0)
            {
                return lines;
            }

            string[] sourceLines = ToLf(text).Split('\n');
            for (int index = 0; index < sourceLines.Length && lines.Count < MaxListedLines; index++)
            {
                if (sourceLines[index].IndexOf(probe, StringComparison.Ordinal) >= 0)
                {
                    lines.Add(index + 1);
                }
            }

            return lines;
        }
    }
}
