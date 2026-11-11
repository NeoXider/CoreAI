using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CoreAI.ChatOverlay
{
    /// <summary>
    /// Engine-free rules for chat answers written in Markdown, shared by every surface that shows them: the
    /// in-world overlay feed (<see cref="ToPlainText"/>), the optional Markdown renderer of the chat panel
    /// (<see cref="PrepareForRender"/>, streamed as stable blocks plus a live tail, see
    /// <see cref="FindStablePrefixLength"/>), and any other UI that shows model text.
    /// </summary>
    /// <remarks>
    /// Model text is untrusted and often incomplete while it streams. Every rule here is conservative: a construct it
    /// does not recognise stays as text, and no rule removes the words of an answer, only markers.
    /// </remarks>
    public static class ChatMarkdownText
    {
        /// <summary>Inserted after a <c>&lt;</c> so neither Markdown nor UI Toolkit rich text reads a tag there.</summary>
        public const char TagBreaker = '​';

        /// <summary>Shortest pause between two re-renders of a streaming answer.</summary>
        public const double MinStreamingRefreshSeconds = 0.08;

        /// <summary>Longest pause between two re-renders of a streaming answer.</summary>
        public const double MaxStreamingRefreshSeconds = 0.25;

        private const string Bullet = "• ";

        /// <summary>
        /// The words of a Markdown answer for a surface that shows plain text: emphasis, code and link markers,
        /// heading and quote prefixes, fence and rule lines and table delimiter rows are removed, bullets become
        /// <c>•</c>, code keeps its text, link text stays and the target goes. A marker still open at the end (an answer
        /// mid-stream) is dropped too. Text without Markdown is returned as is.
        /// </summary>
        public static string ToPlainText(string markdown)
        {
            if (string.IsNullOrEmpty(markdown))
            {
                return markdown ?? "";
            }

            if (!ContainsMarkdownSyntax(markdown))
            {
                return markdown;
            }

            List<string> lines = SplitLines(markdown);
            StringBuilder builder = new(markdown.Length);
            bool inFence = false;
            char fenceChar = '\0';
            int fenceLength = 0;
            int blankRun = 0;
            bool wroteLine = false;
            foreach (string line in lines)
            {
                string output;
                if (inFence)
                {
                    if (IsClosingFence(line, fenceChar, fenceLength))
                    {
                        inFence = false;
                        continue;
                    }

                    output = line;
                }
                else if (TryOpenFence(line, out fenceChar, out fenceLength, out _))
                {
                    inFence = true;
                    continue;
                }
                else if (IsThematicBreak(line) || IsSetextUnderline(line) || IsTableDelimiterRow(line))
                {
                    continue;
                }
                else
                {
                    output = PlainLine(line);
                }

                if (IsBlank(output))
                {
                    blankRun++;
                    if (blankRun > 1)
                    {
                        continue;
                    }
                }
                else
                {
                    blankRun = 0;
                }

                if (wroteLine)
                {
                    builder.Append('\n');
                }

                builder.Append(output);
                wroteLine = true;
            }

            return builder.ToString();
        }

        /// <summary>
        /// Markdown that is safe to hand to a UI renderer.
        /// <list type="bullet">
        /// <item>Images become links (unless <paramref name="allowMedia"/>), so model text never makes the client fetch
        /// a URL by itself.</item>
        /// <item>Every <c>&lt;</c> that starts a tag (HTML or UI Toolkit rich text) gets <see cref="TagBreaker"/>, so
        /// raw HTML is shown instead of silently dropped and <c>&lt;size=…&gt;</c> cannot restyle the UI;
        /// <c>&lt;br&gt;</c> becomes a space; autolinks become ordinary links and the common entities are decoded,
        /// because renderers without those inline kinds drop them.</item>
        /// <item>A front-matter fence on the first line is defused, a fenced code block nested in a list item is
        /// lifted to the top level, and an unterminated fence is closed.</item>
        /// <item>When <paramref name="streaming"/>, markers that are open only because the answer is still arriving
        /// are hidden on the last line (see <see cref="ChatMarkdownInline.HideDangling"/>), and a lone block marker
        /// being typed (<c>#</c>, <c>-</c>, <c>---</c>, <c>1.</c>) waits for the next chunk.</item>
        /// </list>
        /// Code (fenced or inline) is never changed.
        /// </summary>
        public static string PrepareForRender(string markdown, bool streaming, bool allowMedia = false)
        {
            if (string.IsNullOrEmpty(markdown))
            {
                return "";
            }

            List<string> lines = SplitLines(markdown);
            List<string> output = new(lines.Count + 2);
            bool inFence = false;
            char fenceChar = '\0';
            int fenceLength = 0;
            int fenceIndent = 0;
            int fenceOpenedAt = -1;
            int fenceClosedAt = -1;
            for (int index = 0; index < lines.Count; index++)
            {
                string line = lines[index];
                if (inFence)
                {
                    bool closes = IsClosingFence(line, fenceChar, fenceLength);
                    output.Add(Dedent(line, fenceIndent));
                    if (closes)
                    {
                        inFence = false;
                        fenceClosedAt = output.Count - 1;
                        if (fenceIndent > 0 && index + 1 < lines.Count && !IsBlank(lines[index + 1]))
                        {
                            output.Add("");
                        }
                    }

                    continue;
                }

                if (TryOpenFence(line, out fenceChar, out fenceLength, out fenceIndent))
                {
                    inFence = true;
                    // WHY: the renderer drops a code block nested in a list item, so an indented fence is lifted to
                    // the top level; the blank line ends the list before it.
                    if (fenceIndent > 0 && output.Count > 0 && !IsBlank(output[output.Count - 1]))
                    {
                        output.Add("");
                    }

                    fenceOpenedAt = output.Count;
                    output.Add(Dedent(line, fenceIndent));
                    continue;
                }

                output.Add(IsBlank(line) ? line : NeutralizeLine(line, allowMedia));
            }

            if (streaming && output.Count > 0)
            {
                int last = output.Count - 1;
                if (!inFence)
                {
                    if (last != fenceClosedAt)
                    {
                        output[last] = HideDanglingLine(output[last]);
                    }
                }
                else if (last != fenceOpenedAt)
                {
                    output[last] = DropPartialFence(output[last], fenceChar);
                }
            }

            if (inFence)
            {
                output.Add(new string(fenceChar, fenceLength));
            }

            string result = string.Join("\n", output);
            return StartsWithFrontMatterFence(result) ? "\n" + result : result;
        }

        /// <summary>
        /// Length of the prefix of a streaming answer that is made of complete top-level blocks and will not change as
        /// more text arrives: it ends where the last block that follows a blank line starts, on a line that is itself
        /// complete. No boundary is placed inside an open code fence, before an indented line or before a list item (a
        /// list is never split), so the prefix renders the same on its own as inside the whole answer, and it only grows
        /// as the answer grows. 0 means the whole text is still open.
        /// </summary>
        public static int FindStablePrefixLength(string markdown)
        {
            if (string.IsNullOrEmpty(markdown))
            {
                return 0;
            }

            int length = markdown.Length;
            int position = 0;
            int boundary = 0;
            bool inFence = false;
            bool previousBlank = false;
            char fenceChar = '\0';
            int fenceLength = 0;
            while (position < length)
            {
                int lineEnd = markdown.IndexOf('\n', position);
                bool complete = lineEnd >= 0;
                if (!complete)
                {
                    lineEnd = length;
                }

                string line = markdown.Substring(position, lineEnd - position).TrimEnd('\r');
                if (inFence)
                {
                    if (complete && IsClosingFence(line, fenceChar, fenceLength))
                    {
                        inFence = false;
                    }

                    previousBlank = false;
                }
                else if (IsBlank(line))
                {
                    previousBlank = true;
                }
                else
                {
                    // WHY complete lines only: until its newline arrives a line can still turn into a list item
                    // ("1" -> "1."), and a boundary that moves back would re-render the whole bubble.
                    if (previousBlank && complete && IsBoundaryLine(line))
                    {
                        boundary = position;
                    }

                    if (TryOpenFence(line, out char openedChar, out int openedLength, out _))
                    {
                        inFence = true;
                        fenceChar = openedChar;
                        fenceLength = openedLength;
                    }

                    previousBlank = false;
                }

                position = complete ? lineEnd + 1 : length;
            }

            return boundary;
        }

        /// <summary>
        /// How long a streaming answer may wait between two re-renders: short for a short open tail, longer as the
        /// tail that has to be re-rendered grows (bounded by <see cref="MinStreamingRefreshSeconds"/> and
        /// <see cref="MaxStreamingRefreshSeconds"/>).
        /// </summary>
        public static double StreamingRefreshSeconds(int pendingCharacters)
        {
            double seconds = MinStreamingRefreshSeconds + Math.Max(0, pendingCharacters) / 20000d;
            return Math.Min(MaxStreamingRefreshSeconds, seconds);
        }

        /// <summary>
        /// Splits prepared Markdown into text segments and GitHub-style pipe tables (a header row, a delimiter row such
        /// as <c>|---|:--:|</c> with the same number of cells, then rows until a blank line). Tables inside code fences
        /// are not tables.
        /// </summary>
        public static IReadOnlyList<ChatMarkdownSegment> SplitTables(string markdown)
        {
            List<ChatMarkdownSegment> segments = new();
            if (string.IsNullOrEmpty(markdown))
            {
                return segments;
            }

            if (markdown.IndexOf('|') < 0)
            {
                segments.Add(ChatMarkdownSegment.ForMarkdown(markdown));
                return segments;
            }

            List<string> lines = SplitLines(markdown);
            List<string> pending = new();
            bool inFence = false;
            char fenceChar = '\0';
            int fenceLength = 0;
            for (int index = 0; index < lines.Count; index++)
            {
                string line = lines[index];
                if (inFence)
                {
                    pending.Add(line);
                    inFence = !IsClosingFence(line, fenceChar, fenceLength);
                    continue;
                }

                if (TryOpenFence(line, out fenceChar, out fenceLength, out _))
                {
                    inFence = true;
                    pending.Add(line);
                    continue;
                }

                if (index + 1 < lines.Count &&
                    TryReadTableHeader(line, lines[index + 1], out List<string> header,
                        out List<ChatMarkdownTableAlignment> alignments))
                {
                    FlushMarkdown(pending, segments);
                    List<IReadOnlyList<string>> rows = new();
                    int row = index + 2;
                    while (row < lines.Count && !IsBlank(lines[row]) && lines[row].IndexOf('|') >= 0 &&
                           !TryOpenFence(lines[row], out _, out _, out _))
                    {
                        rows.Add(FitCells(SplitTableCells(lines[row]), header.Count));
                        row++;
                    }

                    segments.Add(ChatMarkdownSegment.ForTable(header, alignments, rows));
                    index = row - 1;
                    continue;
                }

                pending.Add(line);
            }

            FlushMarkdown(pending, segments);
            return segments;
        }

        private static bool ContainsMarkdownSyntax(string text)
        {
            foreach (char value in text)
            {
                switch (value)
                {
                    case '*':
                    case '_':
                    case '`':
                    case '~':
                    case '#':
                    case '>':
                    case '[':
                    case '<':
                    case '\\':
                    case '|':
                    case '-':
                    case '+':
                    case '=':
                        return true;
                }
            }

            return false;
        }

        private static List<string> SplitLines(string text)
        {
            string normalized = text.IndexOf('\r') >= 0 ? text.Replace("\r\n", "\n").Replace('\r', '\n') : text;
            return new List<string>(normalized.Split('\n'));
        }

        private static bool IsBlank(string line)
        {
            foreach (char value in line)
            {
                if (!char.IsWhiteSpace(value))
                {
                    return false;
                }
            }

            return true;
        }

        private static int LeadingWhitespace(string line)
        {
            int index = 0;
            while (index < line.Length && (line[index] == ' ' || line[index] == '\t'))
            {
                index++;
            }

            return index;
        }

        private static string Dedent(string line, int indent)
        {
            int remove = Math.Min(indent, LeadingWhitespace(line));
            return remove > 0 ? line.Substring(remove) : line;
        }

        /// <summary>
        /// A code fence opener: three or more backticks or tildes (backtick info strings hold no backtick). Any
        /// indentation is accepted, because answers put fences inside list items.
        /// </summary>
        private static bool TryOpenFence(string line, out char fenceChar, out int fenceLength, out int indent)
        {
            fenceChar = '\0';
            fenceLength = 0;
            indent = LeadingWhitespace(line);
            int index = indent;
            if (index >= line.Length || (line[index] != '`' && line[index] != '~'))
            {
                return false;
            }

            char marker = line[index];
            int run = ChatMarkdownInline.RunLength(line, index, marker);
            if (run < 3 || (marker == '`' && line.IndexOf('`', index + run) >= 0))
            {
                return false;
            }

            fenceChar = marker;
            fenceLength = run;
            return true;
        }

        private static bool IsClosingFence(string line, char fenceChar, int fenceLength)
        {
            int index = LeadingWhitespace(line);
            int run = index < line.Length ? ChatMarkdownInline.RunLength(line, index, fenceChar) : 0;
            if (run < fenceLength)
            {
                return false;
            }

            for (int rest = index + run; rest < line.Length; rest++)
            {
                if (!char.IsWhiteSpace(line[rest]))
                {
                    return false;
                }
            }

            return true;
        }

        private static string DropPartialFence(string line, char fenceChar)
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                return line;
            }

            foreach (char value in trimmed)
            {
                if (value != fenceChar)
                {
                    return line;
                }
            }

            return "";
        }

        private static bool IsThematicBreak(string line)
        {
            char marker = '\0';
            int count = 0;
            foreach (char value in line)
            {
                if (value == ' ' || value == '\t')
                {
                    continue;
                }

                if (value != '-' && value != '*' && value != '_')
                {
                    return false;
                }

                if (marker != '\0' && value != marker)
                {
                    return false;
                }

                marker = value;
                count++;
            }

            return count >= 3;
        }

        private static bool IsSetextUnderline(string line)
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                return false;
            }

            foreach (char value in trimmed)
            {
                if (value != '=')
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsTableDelimiterRow(string line)
        {
            if (line.IndexOf('|') < 0 || line.IndexOf('-') < 0)
            {
                return false;
            }

            List<string> cells = SplitTableCells(line);
            if (cells.Count == 0)
            {
                return false;
            }

            foreach (string cell in cells)
            {
                if (!IsDelimiterCell(cell))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsDelimiterCell(string cell)
        {
            string value = cell.Trim();
            int start = value.StartsWith(":", StringComparison.Ordinal) ? 1 : 0;
            int end = value.EndsWith(":", StringComparison.Ordinal) && value.Length > start ? value.Length - 1 : value.Length;
            if (end - start < 1)
            {
                return false;
            }

            for (int index = start; index < end; index++)
            {
                if (value[index] != '-')
                {
                    return false;
                }
            }

            return true;
        }

        private static ChatMarkdownTableAlignment AlignmentOf(string cell)
        {
            string value = cell.Trim();
            bool left = value.StartsWith(":", StringComparison.Ordinal);
            bool right = value.EndsWith(":", StringComparison.Ordinal);
            if (left && right)
            {
                return ChatMarkdownTableAlignment.Center;
            }

            if (right)
            {
                return ChatMarkdownTableAlignment.Right;
            }

            return left ? ChatMarkdownTableAlignment.Left : ChatMarkdownTableAlignment.None;
        }

        private static bool TryReadTableHeader(string line, string delimiterLine, out List<string> header,
            out List<ChatMarkdownTableAlignment> alignments)
        {
            header = null;
            alignments = null;
            if (line.IndexOf('|') < 0 || !IsTableDelimiterRow(delimiterLine) ||
                BlockPrefixLength(line) != LeadingWhitespace(line))
            {
                return false;
            }

            List<string> cells = SplitTableCells(line);
            List<string> delimiters = SplitTableCells(delimiterLine);
            if (cells.Count == 0 || cells.Count != delimiters.Count)
            {
                return false;
            }

            header = cells;
            alignments = new List<ChatMarkdownTableAlignment>(delimiters.Count);
            foreach (string delimiter in delimiters)
            {
                alignments.Add(AlignmentOf(delimiter));
            }

            return true;
        }

        /// <summary>Cells of one table row: outer pipes removed, split on unescaped pipes outside code spans, trimmed.</summary>
        private static List<string> SplitTableCells(string line)
        {
            string row = line.Trim();
            if (row.StartsWith("|", StringComparison.Ordinal))
            {
                row = row.Substring(1);
            }

            if (row.EndsWith("|", StringComparison.Ordinal) && !row.EndsWith("\\|", StringComparison.Ordinal))
            {
                row = row.Substring(0, row.Length - 1);
            }

            List<string> cells = new();
            StringBuilder cell = new();
            for (int index = 0; index < row.Length; index++)
            {
                char value = row[index];
                if (value == '\\' && index + 1 < row.Length)
                {
                    cell.Append(value).Append(row[index + 1]);
                    index++;
                    continue;
                }

                if (value == '`')
                {
                    int run = ChatMarkdownInline.RunLength(row, index, '`');
                    int close = ChatMarkdownInline.FindBacktickRun(row, index + run, run);
                    int end = close >= 0 ? close + run : index + run;
                    cell.Append(row, index, end - index);
                    index = end - 1;
                    continue;
                }

                if (value == '|')
                {
                    cells.Add(cell.ToString().Trim());
                    cell.Clear();
                    continue;
                }

                cell.Append(value);
            }

            cells.Add(cell.ToString().Trim());
            return cells;
        }

        private static IReadOnlyList<string> FitCells(List<string> cells, int count)
        {
            while (cells.Count < count)
            {
                cells.Add("");
            }

            if (cells.Count > count)
            {
                cells.RemoveRange(count, cells.Count - count);
            }

            return cells;
        }

        private static void FlushMarkdown(List<string> pending, List<ChatMarkdownSegment> segments)
        {
            if (pending.Count == 0)
            {
                return;
            }

            bool hasText = false;
            foreach (string line in pending)
            {
                hasText |= !IsBlank(line);
            }

            if (hasText)
            {
                segments.Add(ChatMarkdownSegment.ForMarkdown(string.Join("\n", pending)));
            }

            pending.Clear();
        }

        /// <summary>
        /// Length of a line's block prefix: indentation, quote markers, then one list marker or heading marker.
        /// </summary>
        private static int BlockPrefixLength(string line)
        {
            int index = LeadingWhitespace(line);
            while (index < line.Length && line[index] == '>')
            {
                index++;
                while (index < line.Length && line[index] == ' ')
                {
                    index++;
                }
            }

            if (index >= line.Length)
            {
                return index;
            }

            char marker = line[index];
            if ((marker == '-' || marker == '*' || marker == '+') && index + 1 < line.Length &&
                (line[index + 1] == ' ' || line[index + 1] == '\t'))
            {
                return index + 2;
            }

            int ordered = OrderedMarkerLength(line, index);
            if (ordered > 0 && index + ordered < line.Length && line[index + ordered] == ' ')
            {
                return index + ordered + 1;
            }

            if (marker == '#')
            {
                int run = ChatMarkdownInline.RunLength(line, index, '#');
                if (run <= 6 && index + run < line.Length && line[index + run] == ' ')
                {
                    return index + run + 1;
                }
            }

            return index;
        }

        /// <summary>Length of <c>12.</c> or <c>3)</c> at <paramref name="start"/>; 0 when there is none.</summary>
        private static int OrderedMarkerLength(string line, int start)
        {
            int index = start;
            while (index < line.Length && index - start < 9 && line[index] >= '0' && line[index] <= '9')
            {
                index++;
            }

            if (index == start || index >= line.Length || (line[index] != '.' && line[index] != ')'))
            {
                return 0;
            }

            return index - start + 1;
        }

        private static bool IsListItemStart(string line)
        {
            int index = LeadingWhitespace(line);
            if (index >= line.Length)
            {
                return false;
            }

            char marker = line[index];
            if (marker == '-' || marker == '*' || marker == '+')
            {
                return index + 1 >= line.Length || line[index + 1] == ' ' || line[index + 1] == '\t';
            }

            int ordered = OrderedMarkerLength(line, index);
            return ordered > 0 && (index + ordered >= line.Length || line[index + ordered] == ' ');
        }

        private static bool IsBoundaryLine(string line)
        {
            return line.Length > 0 && !char.IsWhiteSpace(line[0]) && !IsListItemStart(line);
        }

        private static string PlainLine(string line)
        {
            int indent = LeadingWhitespace(line);
            int index = indent;
            while (index < line.Length && line[index] == '>')
            {
                index++;
                while (index < line.Length && line[index] == ' ')
                {
                    index++;
                }
            }

            string bullet = "";
            if (index < line.Length)
            {
                char marker = line[index];
                if ((marker == '-' || marker == '*' || marker == '+') && index + 1 < line.Length &&
                    (line[index + 1] == ' ' || line[index + 1] == '\t'))
                {
                    bullet = Bullet;
                    index += 2;
                }
                else if (marker == '#')
                {
                    int run = ChatMarkdownInline.RunLength(line, index, '#');
                    if (run <= 6 && (index + run == line.Length || line[index + run] == ' '))
                    {
                        index = Math.Min(line.Length, index + run + 1);
                        return line.Substring(0, indent) + ChatMarkdownInline.ToPlain(TrimClosingHashes(line.Substring(index)));
                    }
                }
            }

            string content = line.Substring(index);
            if (content.TrimStart().StartsWith("|", StringComparison.Ordinal))
            {
                List<string> cells = SplitTableCells(content);
                for (int cell = 0; cell < cells.Count; cell++)
                {
                    cells[cell] = ChatMarkdownInline.ToPlain(cells[cell]);
                }

                return line.Substring(0, indent) + bullet + string.Join(" | ", cells);
            }

            return line.Substring(0, indent) + bullet + ChatMarkdownInline.ToPlain(content);
        }

        private static string TrimClosingHashes(string heading)
        {
            string trimmed = heading.TrimEnd();
            int end = trimmed.Length;
            while (end > 0 && trimmed[end - 1] == '#')
            {
                end--;
            }

            if (end == trimmed.Length)
            {
                return heading;
            }

            return end == 0 || trimmed[end - 1] == ' ' ? trimmed.Substring(0, end).TrimEnd() : heading;
        }

        private static string HideDanglingLine(string line)
        {
            if (IsBlank(line))
            {
                return line;
            }

            if (IsBareBlockMarker(line.Trim()))
            {
                return "";
            }

            int prefix = BlockPrefixLength(line);
            return line.Substring(0, prefix) + ChatMarkdownInline.HideDangling(line.Substring(prefix));
        }

        /// <summary>
        /// A line that is only the start of a block still being typed: hashes, a bullet, an ordered marker, or a run of
        /// rule/underline/emphasis characters. Rendered now it would flash as an empty heading, an empty item, or turn
        /// the paragraph above into a heading for one frame.
        /// </summary>
        private static bool IsBareBlockMarker(string trimmed)
        {
            if (trimmed.Length == 0)
            {
                return false;
            }

            if (OrderedMarkerLength(trimmed, 0) == trimmed.Length)
            {
                return true;
            }

            char first = trimmed[0];
            if (first != '#' && first != '-' && first != '=' && first != '*' && first != '_' && first != '+' &&
                first != '~')
            {
                return false;
            }

            foreach (char value in trimmed)
            {
                if (value != first && value != ' ')
                {
                    return false;
                }
            }

            return first != '#' || trimmed.Length <= 6;
        }

        private static bool StartsWithFrontMatterFence(string text)
        {
            if (!text.StartsWith("---", StringComparison.Ordinal))
            {
                return false;
            }

            int index = 3;
            while (index < text.Length && (text[index] == ' ' || text[index] == '\t'))
            {
                index++;
            }

            return index == text.Length || text[index] == '\n';
        }

        /// <summary>
        /// Neutralises one line outside code: tag starts get <see cref="TagBreaker"/>, <c>&lt;br&gt;</c> becomes a
        /// space, autolinks become links, entities are decoded and, unless <paramref name="allowMedia"/>, the
        /// <c>!</c> of an image is dropped so it renders as a link.
        /// </summary>
        private static string NeutralizeLine(string line, bool allowMedia)
        {
            StringBuilder builder = null;
            int length = line.Length;
            for (int index = 0; index < length; index++)
            {
                char value = line[index];
                if (value == '`')
                {
                    int run = ChatMarkdownInline.RunLength(line, index, '`');
                    int close = ChatMarkdownInline.FindBacktickRun(line, index + run, run);
                    int end = close >= 0 ? close + run : index + run;
                    builder?.Append(line, index, end - index);
                    index = end - 1;
                    continue;
                }

                if (value == '\\' && index + 1 < length && line[index + 1] != '<')
                {
                    builder?.Append(line, index, 2);
                    index++;
                    continue;
                }

                string replacement = null;
                int consumed = 1;
                if (value == '<')
                {
                    int autolinkEnd = ChatMarkdownInline.MatchAutolink(line, index);
                    if (autolinkEnd > index)
                    {
                        replacement = AutolinkAsLink(line.Substring(index + 1, autolinkEnd - index - 2));
                        consumed = autolinkEnd - index;
                    }
                    else if (TryMatchBreakTag(line, index, out int breakEnd))
                    {
                        replacement = " ";
                        consumed = breakEnd - index;
                    }
                    else if (index + 1 < length && IsTagStart(line[index + 1]))
                    {
                        replacement = "<" + TagBreaker;
                    }
                }
                else if (value == '&')
                {
                    if (TryDecodeEntity(line, index, out string decoded, out int entityEnd))
                    {
                        replacement = decoded;
                        consumed = entityEnd - index;
                    }
                }
                else if (value == '!' && !allowMedia && index + 1 < length && line[index + 1] == '[')
                {
                    replacement = "";
                }

                if (replacement == null)
                {
                    builder?.Append(value);
                    continue;
                }

                if (builder == null)
                {
                    builder = new StringBuilder(length + 16);
                    builder.Append(line, 0, index);
                }

                builder.Append(replacement);
                index += consumed - 1;
            }

            return builder?.ToString() ?? line;
        }

        private static bool IsTagStart(char value)
        {
            return (value >= 'a' && value <= 'z') || (value >= 'A' && value <= 'Z') || value == '/' ||
                   value == '!' || value == '?' || value == '#';
        }

        private static bool TryMatchBreakTag(string line, int start, out int end)
        {
            end = start;
            int index = start + 1;
            if (index + 1 >= line.Length || char.ToLowerInvariant(line[index]) != 'b' ||
                char.ToLowerInvariant(line[index + 1]) != 'r')
            {
                return false;
            }

            index += 2;
            while (index < line.Length && line[index] == ' ')
            {
                index++;
            }

            if (index < line.Length && line[index] == '/')
            {
                index++;
            }

            if (index >= line.Length || line[index] != '>')
            {
                return false;
            }

            end = index + 1;
            return true;
        }

        private static string AutolinkAsLink(string target)
        {
            bool email = target.IndexOf(':') < 0 && target.IndexOf('@') > 0;
            string destination = email ? "mailto:" + target : target;
            StringBuilder label = new(target.Length + 8);
            foreach (char value in target)
            {
                if (value == '\\' || value == '*' || value == '_' || value == '[' || value == ']' || value == '`' ||
                    value == '<' || value == '>' || value == '~')
                {
                    label.Append('\\');
                }

                label.Append(value);
            }

            return "[" + label + "](<" + destination + ">)";
        }

        private static readonly Dictionary<string, char> NamedEntities = new(StringComparer.Ordinal)
        {
            { "amp", '&' },
            { "lt", '<' },
            { "gt", '>' },
            { "quot", '"' },
            { "apos", '\'' },
            { "nbsp", ' ' }
        };

        /// <summary>
        /// Decodes <c>&amp;amp;</c>-style named entities from a short list and numeric ones. The result is written as
        /// Markdown that shows the character literally (<c>\&amp;</c>, <c>\&gt;</c>, a broken tag start for <c>&lt;</c>).
        /// </summary>
        private static bool TryDecodeEntity(string line, int start, out string decoded, out int end)
        {
            decoded = null;
            end = start;
            int semicolon = line.IndexOf(';', start + 1);
            if (semicolon < 0 || semicolon - start > 10)
            {
                return false;
            }

            string name = line.Substring(start + 1, semicolon - start - 1);
            int codePoint;
            if (NamedEntities.TryGetValue(name, out char named))
            {
                codePoint = named;
            }
            else if (name.Length > 1 && name[0] == '#')
            {
                bool hex = name[1] == 'x' || name[1] == 'X';
                string digits = hex ? name.Substring(2) : name.Substring(1);
                bool parsed = hex
                    ? int.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out codePoint)
                    : int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out codePoint);
                if (!parsed || codePoint <= 0 || codePoint > 0x10FFFF || (codePoint >= 0xD800 && codePoint <= 0xDFFF))
                {
                    return false;
                }
            }
            else
            {
                return false;
            }

            if (codePoint == '<')
            {
                decoded = "<" + TagBreaker;
            }
            else if (codePoint < 128 && ChatMarkdownInline.IsAsciiPunctuation((char)codePoint))
            {
                decoded = "\\" + (char)codePoint;
            }
            else if (codePoint < 32)
            {
                return false;
            }
            else
            {
                decoded = char.ConvertFromUtf32(codePoint);
            }

            end = semicolon + 1;
            return true;
        }
    }
}
