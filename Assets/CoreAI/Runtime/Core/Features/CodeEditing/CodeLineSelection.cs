using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;


namespace CoreAI.CodeEditing
{

    public static class CodeLineSelection
    {

        public const int MaxLines = 30;

        private static readonly char[] TokenSeparator = { ' ' };

        public static bool TryParse(string spec, int lineCount, out IReadOnlyList<int> lines, out string error)
        {
            lines = Array.Empty<int>();
            error = null;
            if (string.IsNullOrWhiteSpace(spec))
            {
                error = Invalid();
                return false;
            }

            string[] tokens = Normalize(spec).Split(TokenSeparator, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
            {
                error = Invalid();
                return false;
            }

            List<(int Start, int End)> ranges = new(tokens.Length);
            foreach (string token in tokens)
            {
                if (!TryParseRange(token, out int start, out int end))
                {
                    error = Invalid();
                    return false;
                }

                ranges.Add((start, end));
            }

            foreach ((int start, int end) in ranges)
            {
                if (start < 1 || start > lineCount)
                {
                    error = OutOfRange(start, lineCount);
                    return false;
                }

                if (end > lineCount)
                {
                    error = OutOfRange(end, lineCount);
                    return false;
                }
            }

            HashSet<int> unique = new();
            foreach ((int start, int end) in ranges)
            {
                if (end - start + 1 > MaxLines)
                {
                    error = TooMany();
                    return false;
                }

                for (int line = start; line <= end; line++)
                {
                    unique.Add(line);
                }

                if (unique.Count > MaxLines)
                {
                    error = TooMany();
                    return false;
                }
            }

            List<int> sorted = new(unique);
            sorted.Sort();
            lines = sorted;
            return true;
        }

        public static List<(int Start, int End)> ToRanges(IReadOnlyList<int> lines)
        {
            List<(int Start, int End)> ranges = new();
            if (lines == null || lines.Count == 0)
            {
                return ranges;
            }

            List<int> sorted = new(lines);
            sorted.Sort();
            int start = sorted[0];
            int previous = start;
            for (int i = 1; i < sorted.Count; i++)
            {
                int line = sorted[i];
                if (line == previous)
                {
                    continue;
                }

                if (line != previous + 1)
                {
                    ranges.Add((start, previous));
                    start = line;
                }

                previous = line;
            }

            ranges.Add((start, previous));
            return ranges;
        }

        public static List<int> ShiftFrom(IReadOnlyList<int> lines, int fromLine, int delta)
        {
            List<int> shifted = new(lines?.Count ?? 0);
            if (lines != null)
            {
                foreach (int line in lines)
                {
                    shifted.Add(line >= fromLine ? line + delta : line);
                }
            }

            return shifted;
        }

        public static string Describe(IReadOnlyList<int> lines)
        {
            StringBuilder text = new();
            foreach ((int start, int end) in ToRanges(lines))
            {
                if (text.Length > 0)
                {
                    text.Append(", ");
                }

                text.Append(start);
                if (end != start)
                {
                    text.Append('-').Append(end);
                }
            }

            return text.ToString();
        }

        private static string Normalize(string spec)
        {
            StringBuilder text = new(spec.Length);
            bool pendingSpace = false;
            foreach (char c in spec)
            {
                if (char.IsWhiteSpace(c))
                {
                    pendingSpace = text.Length > 0;
                    continue;
                }

                if (IsHardSeparator(c))
                {
                    AppendBreak(text);
                    pendingSpace = false;
                    continue;
                }

                if (IsDash(c))
                {
                    text.Append('-');
                    pendingSpace = false;
                    continue;
                }

                if (pendingSpace && text[text.Length - 1] != '-')
                {
                    AppendBreak(text);
                }

                pendingSpace = false;
                text.Append(c);
            }

            return text.ToString();
        }

        private static void AppendBreak(StringBuilder text)
        {
            if (text.Length > 0 && text[text.Length - 1] != ' ')
            {
                text.Append(' ');
            }
        }

        private static bool IsHardSeparator(char c) => c is ',' or ';' or '[' or ']' or '"';

        private static bool IsDash(char c) => c == '-' || (c >= '‐' && c <= '―') || c == '−';

        private static bool TryParseRange(string token, out int start, out int end)
        {
            start = 0;
            end = 0;
            string[] parts = token.Split('-');
            if (parts.Length > 2 || !TryParseLine(parts[0], out start))
            {
                return false;
            }

            end = start;
            if (parts.Length == 2 && (!TryParseLine(parts[1], out end) || end < start))
            {
                return false;
            }

            return true;
        }

        private static bool TryParseLine(string text, out int value) =>
            int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);

        private static string Invalid() =>
            "invalid_lines: expected a line number, a range like 10-15, or a list like 1,2,7-9";

        private static string OutOfRange(int line, int lineCount) =>
            $"line_out_of_range: line={line} line_count={lineCount}";

        private static string TooMany() => $"too_many_lines: max={MaxLines}";
    }
}
