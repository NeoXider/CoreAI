using System;
using System.Collections.Generic;
using System.Text;

namespace CoreAI.CodeEditing
{

    public readonly struct CodeGutterPalette
    {
        public CodeGutterPalette(string markerHex, string errorHex, string caretHex, string hintHex = null)
        {
            MarkerHex = markerHex;
            ErrorHex = errorHex;
            CaretHex = caretHex;
            HintHex = hintHex;
        }

        public string MarkerHex { get; }

        public string ErrorHex { get; }

        public string CaretHex { get; }

        public string HintHex { get; }
    }

    public readonly struct CodeEnterPlan
    {
        public CodeEnterPlan(string indent, string tail, int trimStart, int trimLength)
        {
            Indent = indent ?? "";
            Tail = tail ?? "";
            TrimStart = trimStart;
            TrimLength = trimLength;
        }

        public string Indent { get; }

        public string Tail { get; }

        public int TrimStart { get; }

        public int TrimLength { get; }

        public bool IsEmpty => Indent.Length == 0 && Tail.Length == 0 && TrimLength == 0;
    }

    public static class CodeEditorTextUtility
    {
        public const string IndentUnit = "    ";

        private const char NonBreakingSpace = (char)160;

        public static string NormalizeEditorText(string text) => NormalizeEditorText(text, 0, out _);

        public static string NormalizeEditorText(string text, int caretIndex, out int newCaretIndex)
        {
            text ??= "";
            caretIndex = Clamp(caretIndex, 0, text.Length);

            StringBuilder sb = new(text.Length);
            int mappedCaret = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (i == caretIndex)
                {
                    mappedCaret = sb.Length;
                }

                char c = text[i];
                if (c == '\r')
                {

                    if (i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        continue;
                    }

                    sb.Append('\n');
                    continue;
                }

                if (c == '\t')
                {
                    sb.Append(c);
                    continue;
                }

                sb.Append(c);
            }

            if (caretIndex == text.Length)
            {
                mappedCaret = sb.Length;
            }

            newCaretIndex = mappedCaret;
            return sb.ToString();
        }

        public static bool NeedsEditorNormalization(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\r')
                {
                    return true;
                }
            }

            return false;
        }

        public static int GuideBarCount(int indentLevel) => indentLevel > 0 ? indentLevel : 0;

        public static string InsertTab(string text, int caretIndex, out int newCaretIndex) =>
            InsertTab(text, caretIndex, caretIndex, out newCaretIndex);

        public static string InsertTab(string text, int selStart, int selEnd, out int newCaretIndex)
        {
            text ??= "";
            int a = Clamp(Math.Min(selStart, selEnd), 0, text.Length);
            int b = Clamp(Math.Max(selStart, selEnd), 0, text.Length);
            int column = a - LineStartIndex(text, a);
            string spaces = new(' ', IndentUnit.Length - column % IndentUnit.Length);
            newCaretIndex = a + spaces.Length;
            return text.Remove(a, b - a).Insert(a, spaces);
        }

        public static string IndentLines(string text, int selStart, int selEnd, out int newStart, out int newEnd)
        {
            string updated = IndentSelection(text, selStart, selEnd, out newStart, out newEnd);
            if (selStart == selEnd)
            {
                int caret = Clamp(selStart, 0, (text ?? "").Length) + IndentUnit.Length;
                newStart = caret;
                newEnd = caret;
            }

            return updated;
        }

        public static bool TryBackspaceUnindent(string text, int caretIndex, out string updatedText,
            out int newCaretIndex)
        {
            text ??= "";
            caretIndex = Clamp(caretIndex, 0, text.Length);
            updatedText = text;
            newCaretIndex = caretIndex;

            int lineStart = LineStartIndex(text, caretIndex);
            if (caretIndex == lineStart)
            {
                return false;
            }

            for (int i = lineStart; i < caretIndex; i++)
            {
                if (text[i] != ' ')
                {
                    return false;
                }
            }

            int spacesBeforeCaret = caretIndex - lineStart;
            int previousIndentLength = (spacesBeforeCaret - 1) / IndentUnit.Length * IndentUnit.Length;
            int removedLength = spacesBeforeCaret - previousIndentLength;
            updatedText = text.Remove(lineStart + previousIndentLength, removedLength);
            newCaretIndex = lineStart + previousIndentLength;
            return true;
        }

        public static string IndentSelection(string text, int selStart, int selEnd,
            out int newStart, out int newEnd)
        {
            text ??= "";
            int a = Clamp(Math.Min(selStart, selEnd), 0, text.Length);
            int b = Clamp(Math.Max(selStart, selEnd), 0, text.Length);
            List<int> lineStarts = CollectLineStarts(text, a, b);

            bool skipBlank = lineStarts.Count > 1;
            StringBuilder sb = new(text);
            int indented = 0;
            bool firstIndented = false;
            for (int k = lineStarts.Count - 1; k >= 0; k--)
            {
                if (skipBlank && IsBlankLine(text, lineStarts[k]))
                {
                    continue;
                }

                sb.Insert(lineStarts[k], IndentUnit);
                indented++;
                firstIndented |= k == 0;
            }

            newStart = firstIndented && lineStarts.Count > 0 && a > lineStarts[0] ? a + IndentUnit.Length : a;
            newEnd = b + IndentUnit.Length * indented;
            return sb.ToString();
        }

        private static bool IsBlankLine(string text, int lineStart)
        {
            for (int i = lineStart; i < text.Length && text[i] != '\n'; i++)
            {
                if (text[i] != ' ')
                {
                    return false;
                }
            }

            return true;
        }

        public static string UnindentSelection(string text, int selStart, int selEnd,
            out int newStart, out int newEnd)
        {
            text ??= "";
            int a = Clamp(Math.Min(selStart, selEnd), 0, text.Length);
            int b = Clamp(Math.Max(selStart, selEnd), 0, text.Length);
            List<int> lineStarts = CollectLineStarts(text, a, b);

            StringBuilder sb = new(text);
            int totalRemoved = 0;
            int removedOnFirst = 0;
            for (int k = lineStarts.Count - 1; k >= 0; k--)
            {
                int ls = lineStarts[k];
                int spaces = 0;
                while (spaces < IndentUnit.Length && ls + spaces < sb.Length && sb[ls + spaces] == ' ')
                {
                    spaces++;
                }

                if (spaces > 0)
                {
                    sb.Remove(ls, spaces);
                    totalRemoved += spaces;
                    if (k == 0)
                    {
                        removedOnFirst = spaces;
                    }
                }
            }

            int firstLineStart = lineStarts.Count > 0 ? lineStarts[0] : a;
            newStart = Math.Max(firstLineStart, a - removedOnFirst);
            newEnd = Math.Max(newStart, b - totalRemoved);
            return sb.ToString();
        }

        private static List<int> CollectLineStarts(string text, int a, int b)
        {
            List<int> list = new();
            int firstLineStart = LineStartIndex(text, a);
            int lastRef = b > a ? b - 1 : b;
            int i = firstLineStart;
            while (true)
            {
                list.Add(i);
                int nextNl = text.IndexOf('\n', i);
                if (nextNl < 0 || nextNl >= lastRef)
                {
                    break;
                }

                i = nextNl + 1;
            }

            return list;
        }

        private const string CommentToken = "#";

        public static string ToggleLineComment(string text, int selStart, int selEnd,
            out int newStart, out int newEnd, string commentToken = "#")
        {
            text ??= "";
            int a = Clamp(Math.Min(selStart, selEnd), 0, text.Length);
            int b = Clamp(Math.Max(selStart, selEnd), 0, text.Length);
            string[] lines = text.Split('\n');
            int firstLine = LineIndexFromCaret(text, a);
            int lastLine = LineIndexFromCaret(text, b > a ? b - 1 : b);

            bool anyContent = false;
            bool allCommented = true;
            int column = int.MaxValue;
            for (int i = firstLine; i <= lastLine; i++)
            {
                int lead = LeadingSpaces(lines[i]);
                if (lead >= lines[i].Length)
                {
                    continue;
                }

                anyContent = true;
                column = Math.Min(column, lead);
                if (string.CompareOrdinal(lines[i], lead, commentToken, 0, commentToken.Length) != 0)
                {
                    allCommented = false;
                }
            }

            if (!anyContent)
            {
                newStart = a;
                newEnd = b;
                return text;
            }

            int[] columnShift = new int[lines.Length];
            int[] shiftFrom = new int[lines.Length];
            for (int i = firstLine; i <= lastLine; i++)
            {
                int lead = LeadingSpaces(lines[i]);
                if (lead >= lines[i].Length)
                {
                    continue;
                }

                if (allCommented)
                {
                    int removed = lead + commentToken.Length < lines[i].Length && lines[i][lead + commentToken.Length] == ' ' ? commentToken.Length + 1 : commentToken.Length;
                    lines[i] = lines[i].Remove(lead, removed);
                    columnShift[i] = -removed;
                    shiftFrom[i] = lead;
                }
                else
                {
                    lines[i] = lines[i].Insert(column, commentToken + " ");
                    columnShift[i] = commentToken.Length + 1;
                    shiftFrom[i] = column;
                }
            }

            newStart = ShiftCaret(text, lines, a, columnShift, shiftFrom);
            newEnd = ShiftCaret(text, lines, b, columnShift, shiftFrom);
            return string.Join("\n", lines);
        }

        public static string MoveLines(string text, int selStart, int selEnd, bool up,
            out int newStart, out int newEnd)
        {
            text ??= "";
            int a = Clamp(Math.Min(selStart, selEnd), 0, text.Length);
            int b = Clamp(Math.Max(selStart, selEnd), 0, text.Length);
            newStart = a;
            newEnd = b;

            List<string> lines = new(text.Split('\n'));
            int firstLine = LineIndexFromCaret(text, a);
            int lastLine = LineIndexFromCaret(text, b > a ? b - 1 : b);

            if (up ? firstLine == 0 : lastLine == lines.Count - 1)
            {
                return text;
            }

            int delta;
            if (up)
            {
                string moved = lines[firstLine - 1];
                lines.RemoveAt(firstLine - 1);
                lines.Insert(lastLine, moved);
                delta = -(moved.Length + 1);
            }
            else
            {
                string moved = lines[lastLine + 1];
                lines.RemoveAt(lastLine + 1);
                lines.Insert(firstLine, moved);
                delta = moved.Length + 1;
            }

            newStart = a + delta;
            newEnd = b + delta;
            return string.Join("\n", lines);
        }

        public static string DuplicateLines(string text, int selStart, int selEnd, bool up,
            out int newStart, out int newEnd)
        {
            text ??= "";
            int a = Clamp(Math.Min(selStart, selEnd), 0, text.Length);
            int b = Clamp(Math.Max(selStart, selEnd), 0, text.Length);
            List<string> lines = new(text.Split('\n'));
            int firstLine = LineIndexFromCaret(text, a);
            int lastLine = LineIndexFromCaret(text, b > a ? b - 1 : b);

            List<string> block = lines.GetRange(firstLine, lastLine - firstLine + 1);
            lines.InsertRange(up ? firstLine : lastLine + 1, block);

            int blockLength = 0;
            for (int i = 0; i < block.Count; i++)
            {
                blockLength += block[i].Length + 1;
            }

            newStart = up ? a : a + blockLength;
            newEnd = up ? b : b + blockLength;
            return string.Join("\n", lines);
        }

        public static string DeleteLines(string text, int selStart, int selEnd, out int newCaretIndex)
        {
            text ??= "";
            int a = Clamp(Math.Min(selStart, selEnd), 0, text.Length);
            int b = Clamp(Math.Max(selStart, selEnd), 0, text.Length);
            List<string> lines = new(text.Split('\n'));
            int firstLine = LineIndexFromCaret(text, a);
            int lastLine = LineIndexFromCaret(text, b > a ? b - 1 : b);

            lines.RemoveRange(firstLine, lastLine - firstLine + 1);
            if (lines.Count == 0)
            {
                lines.Add("");
            }

            string result = string.Join("\n", lines);
            int line = Math.Min(firstLine, lines.Count - 1);
            newCaretIndex = Clamp(OffsetOfLine(lines, line), 0, result.Length);
            return result;
        }

        public static void SelectLines(string text, int selStart, int selEnd,
            out int newStart, out int newEnd)
        {
            text ??= "";
            int a = Clamp(Math.Min(selStart, selEnd), 0, text.Length);
            int b = Clamp(Math.Max(selStart, selEnd), 0, text.Length);
            List<string> lines = new(text.Split('\n'));
            int firstLine = LineIndexFromCaret(text, a);
            int lastLine = LineIndexFromCaret(text, b > a ? b - 1 : b);

            int start = OffsetOfLine(lines, firstLine);
            int end = OffsetOfLine(lines, lastLine) + lines[lastLine].Length;
            if (lastLine < lines.Count - 1)
            {
                end++;
            }

            if (start == a && end == b && lastLine < lines.Count - 1)
            {
                lastLine++;
                end = OffsetOfLine(lines, lastLine) + lines[lastLine].Length;
                if (lastLine < lines.Count - 1)
                {
                    end++;
                }
            }

            newStart = start;
            newEnd = end;
        }

        public static int SmartHomeCaret(string text, int caretIndex)
        {
            text ??= "";
            caretIndex = Clamp(caretIndex, 0, text.Length);
            int lineStart = LineStartIndex(text, caretIndex);
            int lineEnd = text.IndexOf('\n', lineStart);
            lineEnd = lineEnd < 0 ? text.Length : lineEnd;

            int firstGlyph = lineStart;
            while (firstGlyph < lineEnd && text[firstGlyph] == ' ')
            {
                firstGlyph++;
            }

            return caretIndex == firstGlyph ? lineStart : firstGlyph;
        }

        public static bool TryDeleteWordBefore(string text, int caretIndex, out string updatedText,
            out int newCaretIndex)
        {
            text ??= "";
            caretIndex = Clamp(caretIndex, 0, text.Length);
            updatedText = text;
            newCaretIndex = caretIndex;
            if (caretIndex == 0)
            {
                return false;
            }

            int i = caretIndex;
            while (i > 0 && text[i - 1] == ' ')
            {
                i--;
            }

            if (i > 0 && text[i - 1] == '\n')
            {
                i--;
            }
            else
            {
                bool word = IsWordChar(text[Math.Max(0, i - 1)]);
                while (i > 0 && text[i - 1] != '\n' && text[i - 1] != ' ' && IsWordChar(text[i - 1]) == word)
                {
                    i--;
                }
            }

            if (i == caretIndex)
            {
                return false;
            }

            updatedText = text.Remove(i, caretIndex - i);
            newCaretIndex = i;
            return true;
        }

        public static bool TryDeleteWordAfter(string text, int caretIndex, out string updatedText,
            out int newCaretIndex)
        {
            text ??= "";
            caretIndex = Clamp(caretIndex, 0, text.Length);
            updatedText = text;
            newCaretIndex = caretIndex;
            if (caretIndex >= text.Length)
            {
                return false;
            }

            int i = caretIndex;
            if (text[i] == '\n')
            {
                i++;
            }
            else
            {
                bool word = IsWordChar(text[i]);
                while (i < text.Length && text[i] != '\n' && text[i] != ' ' && IsWordChar(text[i]) == word)
                {
                    i++;
                }

                while (i < text.Length && text[i] == ' ')
                {
                    i++;
                }
            }

            if (i == caretIndex)
            {
                return false;
            }

            updatedText = text.Remove(caretIndex, i - caretIndex);
            return true;
        }

        private const string PairOpen = "([{\"'";

        private const string PairClose = ")]}\"'";

        private const string AutoCloseBefore = ";:.,=}])>";

        public enum AutoCloseEdit
        {

            None,

            TypeOver,

            Wrapped,

            Inserted,

            InsertedTriple
        }

        public static bool TryAutoClosePair(char typed, string text, int selStart, int selEnd,
            out string updatedText, out int newStart, out int newEnd) =>
            AutoClosePair(typed, text, selStart, selEnd, true, out updatedText, out newStart, out newEnd)
            != AutoCloseEdit.None;

        public static AutoCloseEdit AutoClosePair(char typed, string text, int selStart, int selEnd,
            bool allowTypeOver, out string updatedText, out int newStart, out int newEnd) =>
            AutoClosePairCore(typed, text, selStart, selEnd, allowTypeOver, null,
                out updatedText, out newStart, out newEnd);

        /// <summary>Uses a host language's classification of the insertion point without rescanning it as Python.</summary>
        public static AutoCloseEdit AutoClosePair(char typed, string text, int selStart, int selEnd,
            bool allowTypeOver, bool isCodeAtInsertion, out string updatedText, out int newStart, out int newEnd) =>
            AutoClosePairCore(typed, text, selStart, selEnd, allowTypeOver, isCodeAtInsertion,
                out updatedText, out newStart, out newEnd);

        private static AutoCloseEdit AutoClosePairCore(char typed, string text, int selStart, int selEnd,
            bool allowTypeOver, bool? isCodeAtInsertion, out string updatedText, out int newStart, out int newEnd)
        {
            text ??= "";
            int a = Clamp(Math.Min(selStart, selEnd), 0, text.Length);
            int b = Clamp(Math.Max(selStart, selEnd), 0, text.Length);
            updatedText = text;
            newStart = a;
            newEnd = b;

            int closeIndex = PairClose.IndexOf(typed);
            if (allowTypeOver && a == b && closeIndex >= 0 && a < text.Length && text[a] == typed)
            {
                newStart = a + 1;
                newEnd = a + 1;
                return AutoCloseEdit.TypeOver;
            }

            int openIndex = PairOpen.IndexOf(typed);
            if (openIndex < 0)
            {
                return AutoCloseEdit.None;
            }

            char close = PairClose[openIndex];
            if (b > a)
            {
                updatedText = text.Insert(b, close.ToString()).Insert(a, typed.ToString());
                newStart = a + 1;
                newEnd = b + 1;
                return AutoCloseEdit.Wrapped;
            }

            if (!(isCodeAtInsertion ?? IsCodeAt(text, a)) || (a < text.Length && !IsAutoCloseBefore(text[a])))
            {
                return AutoCloseEdit.None;
            }

            bool quote = typed == close;
            if (quote && OpensTripleQuote(text, a, typed))
            {
                updatedText = text.Insert(a, new string(typed, 4));
                newStart = a + 1;
                newEnd = a + 1;
                return AutoCloseEdit.InsertedTriple;
            }

            if (quote && a > 0 && IsWordChar(text[a - 1]) && !EndsWithStringPrefix(text, a))
            {
                return AutoCloseEdit.None;
            }

            updatedText = text.Insert(a, new string(new[] { typed, close }));
            newStart = a + 1;
            newEnd = a + 1;
            return AutoCloseEdit.Inserted;
        }

        private static bool IsAutoCloseBefore(char next) =>
            char.IsWhiteSpace(next) || AutoCloseBefore.IndexOf(next) >= 0;

        private static bool OpensTripleQuote(string text, int caret, char quote) =>
            caret >= 2
            && text[caret - 1] == quote
            && text[caret - 2] == quote
            && (caret < 3 || text[caret - 3] != quote)
            && IsCodeAt(text, caret - 2);

        private static bool EndsWithStringPrefix(string text, int caret)
        {
            int start = caret;
            while (start > 0 && IsWordChar(text[start - 1]))
            {
                start--;
            }

            string word = text.Substring(start, caret - start).ToLowerInvariant();
            return word is "f" or "r" or "b" or "u" or "rb" or "br" or "fr" or "rf";
        }

        public sealed class AutoCloseTracker
        {
            private readonly List<(int Open, int Close, char Closer)> _pairs = new();

            private string _text;

            private readonly List<string> _recentTexts = new();

            private const int RecentTextLimit = 8;

            public int Count => _pairs.Count;

            private void Remember(string text)
            {
                _text = text;
                _recentTexts.Add(text);
                if (_recentTexts.Count > RecentTextLimit)
                {
                    _recentTexts.RemoveAt(0);
                }
            }

            public void Track(int open, int close, char closer, string text = null)
            {
                if (text != null)
                {
                    OnTextChanged(_text ?? text, text);
                }

                _pairs.Add((open, close, closer));
            }

            public void Clear()
            {
                _pairs.Clear();
                _recentTexts.Clear();
                _text = null;
            }

            public bool IsAutoClosed(string text, int caret)
            {
                Prune(caret);
                for (int i = 0; i < _pairs.Count; i++)
                {
                    (int _, int close, char closer) = _pairs[i];
                    if (close == caret && text != null && caret < text.Length && text[caret] == closer)
                    {
                        return true;
                    }
                }

                return false;
            }

            public void Prune(int caret)
            {
                for (int i = _pairs.Count - 1; i >= 0; i--)
                {
                    if (caret <= _pairs[i].Open || caret > _pairs[i].Close)
                    {
                        _pairs.RemoveAt(i);
                    }
                }
            }

            public void OnTextChanged(string previous, string next) =>
                OnTextChangedCore(previous, next, -1);

            /// <summary>Uses an exact single-character insertion position when identical characters make the text diff ambiguous.</summary>
            public void OnTextChanged(string previous, string next, int insertionIndex) =>
                OnTextChangedCore(previous, next, insertionIndex);

            private void OnTextChangedCore(string previous, string next, int insertionIndex)
            {
                next ??= "";

                if (_text != null
                    && !string.Equals(previous, _text, StringComparison.Ordinal)
                    && _recentTexts.Contains(next))
                {
                    return;
                }

                previous = _text ?? previous ?? "";
                Remember(next);
                if (_pairs.Count == 0 || string.Equals(previous, next, StringComparison.Ordinal))
                {
                    return;
                }
                int prefix = 0;
                int max = Math.Min(previous.Length, next.Length);
                while (prefix < max && previous[prefix] == next[prefix])
                {
                    prefix++;
                }

                int suffix = 0;
                while (suffix < max - prefix
                       && previous[previous.Length - 1 - suffix] == next[next.Length - 1 - suffix])
                {
                    suffix++;
                }

                int changedEnd = previous.Length - suffix;
                if (IsSingleCharacterInsertionAt(previous, next, insertionIndex))
                {
                    prefix = insertionIndex;
                    changedEnd = insertionIndex;
                }
                int delta = next.Length - previous.Length;
                for (int i = _pairs.Count - 1; i >= 0; i--)
                {
                    int open = Shift(_pairs[i].Open, prefix, changedEnd, delta);
                    int close = Shift(_pairs[i].Close, prefix, changedEnd, delta);
                    char closer = _pairs[i].Closer;
                    if (open < 0 || close < 0 || open >= close || close >= next.Length || next[close] != closer)
                    {
                        _pairs.RemoveAt(i);
                        continue;
                    }

                    _pairs[i] = (open, close, closer);
                }
            }

            private static bool IsSingleCharacterInsertionAt(string previous, string next, int index) =>
                index >= 0 && index <= previous.Length && next.Length == previous.Length + 1
                && string.CompareOrdinal(previous, 0, next, 0, index) == 0
                && string.CompareOrdinal(previous, index, next, index + 1, previous.Length - index) == 0;

            private static int Shift(int index, int prefix, int changedEnd, int delta)
            {
                if (index < prefix)
                {
                    return index;
                }

                return index >= changedEnd ? index + delta : -1;
            }
        }

        public static bool TryBackspacePair(string text, int caretIndex, out string updatedText,
            out int newCaretIndex)
        {
            text ??= "";
            caretIndex = Clamp(caretIndex, 0, text.Length);
            updatedText = text;
            newCaretIndex = caretIndex;
            if (caretIndex == 0 || caretIndex >= text.Length)
            {
                return false;
            }

            int openIndex = PairOpen.IndexOf(text[caretIndex - 1]);
            if (openIndex < 0 || text[caretIndex] != PairClose[openIndex])
            {
                return false;
            }

            updatedText = text.Remove(caretIndex - 1, 2);
            newCaretIndex = caretIndex - 1;
            return true;
        }

        public static int CaretIndexForLine(string text, int line)
        {
            text ??= "";

            long target = Math.Max(0L, (long)line - 1L);
            int start = 0;
            for (long row = 0; row < target; row++)
            {
                int newline = text.IndexOf('\n', start);
                if (newline < 0)
                {
                    break;
                }

                start = newline + 1;
            }

            return start;
        }

        public static int CaretIndexForError(string text, int line, int? column)
        {
            text ??= "";
            int lineStart = CaretIndexForLine(text, line);
            int lineEnd = text.IndexOf('\n', lineStart);
            lineEnd = lineEnd < 0 ? text.Length : lineEnd;
            int codeStart = lineStart + LeadingSpaces(text.Substring(lineStart, lineEnd - lineStart));
            long target = codeStart + (long)Math.Max(0, column ?? 0);
            return (int)Clamp64(target, lineStart, lineEnd);
        }

        public const int ErrorMessageMaxLength = 90;

        public static string ShortErrorMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return null;
            }

            string single = message.Trim().Replace('\n', ' ').Replace('\r', ' ');
            return single.Length <= ErrorMessageMaxLength
                ? single
                : single.Substring(0, ErrorMessageMaxLength - 3).TrimEnd() + "...";
        }

        private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        private static int LeadingSpaces(string line)
        {
            int count = 0;
            while (count < line.Length && line[count] == ' ')
            {
                count++;
            }

            return count;
        }

        private static int LineIndexFromCaret(string text, int caretIndex)
        {
            int line = 0;
            for (int i = 0; i < caretIndex && i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    line++;
                }
            }

            return line;
        }

        public static int LineStartIndex(string text, int caretIndex)
        {
            text ??= "";
            caretIndex = Clamp(caretIndex, 0, text.Length);
            return caretIndex <= 0 ? 0 : text.LastIndexOf('\n', caretIndex - 1) + 1;
        }

        private static int OffsetOfLine(IReadOnlyList<string> lines, int line)
        {
            int offset = 0;
            for (int i = 0; i < line && i < lines.Count; i++)
            {
                offset += lines[i].Length + 1;
            }

            return offset;
        }

        private static int ShiftCaret(string originalText, IReadOnlyList<string> newLines, int caret,
            IReadOnlyList<int> columnShift, IReadOnlyList<int> shiftFrom)
        {
            int line = LineIndexFromCaret(originalText, caret);
            int column = caret - LineStartIndex(originalText, caret);
            int shift = columnShift[line];
            if (shift != 0 && column >= shiftFrom[line])
            {

                column = Math.Max(shiftFrom[line], column + shift);
            }

            int lineLength = newLines[line].Length;
            return OffsetOfLine(newLines, line) + Math.Min(column, lineLength);
        }

        public static string InsertNewLineWithIndent(string text, int caretIndex, out int newCaretIndex)
        {
            text ??= "";
            caretIndex = Clamp(caretIndex, 0, text.Length);
            CodeEnterPlan plan = PlanPythonEnter(text, caretIndex, caretIndex);
            return ApplyEnterPlan(text.Insert(caretIndex, "\n"), caretIndex + 1, plan, out newCaretIndex);
        }

        public static string ComputeNewLineIndent(string text, int caretIndex) =>
            PlanPythonEnter(text, caretIndex, caretIndex).Indent;

        public static CodeEnterPlan PlanPythonEnter(string text, int selStart, int selEnd)
        {
            text ??= "";
            int a = Clamp(Math.Min(selStart, selEnd), 0, text.Length);
            int b = Clamp(Math.Max(selStart, selEnd), 0, text.Length);
            int lineStart = LineStartIndex(text, a);
            int lineEnd = text.IndexOf('\n', b);
            lineEnd = lineEnd < 0 ? text.Length : lineEnd;
            string before = text.Substring(lineStart, a - lineStart);
            bool afterBlank = IsBlank(text, b, lineEnd);
            string lead = before.Substring(0, LeadingIndentLength(before));

            if (before.Length > 0 && lead.Length == before.Length && afterBlank)
            {
                return new CodeEnterPlan(lead, "", lineStart, before.Length);
            }

            if (a > 0 && b < text.Length && IsCodeAt(text, a))
            {
                int open = OpenBrackets.IndexOf(text[a - 1]);
                if (open >= 0 && text[b] == CloseBrackets[open])
                {
                    return new CodeEnterPlan(lead + IndentUnit, "\n" + lead, 0, 0);
                }
            }

            string code = CodePartOfLine(before, out bool endsInString);
            if (!endsInString && code.Length > 0)
            {
                char last = code[code.Length - 1];
                if (last == ':' || OpenBrackets.IndexOf(last) >= 0)
                {
                    return new CodeEnterPlan(lead + IndentUnit, "", 0, 0);
                }

                if (afterBlank && lead.Length > 0 && EndsBlock(code))
                {
                    int dedented = (lead.Length - 1) / IndentUnit.Length * IndentUnit.Length;
                    return new CodeEnterPlan(lead.Substring(0, dedented), "", 0, 0);
                }
            }

            return new CodeEnterPlan(lead, "", 0, 0);
        }

        public static string ApplyEnterPlan(string text, int caretAfterNewLine, CodeEnterPlan plan,
            out int newCaretIndex)
        {
            text ??= "";
            int caret = Clamp(caretAfterNewLine, 0, text.Length);
            string updated = text.Insert(caret, plan.Indent + plan.Tail);
            newCaretIndex = caret + plan.Indent.Length;
            if (plan.TrimLength > 0
                && plan.TrimStart >= 0
                && plan.TrimStart + plan.TrimLength <= caret - 1
                && IsBlank(updated, plan.TrimStart, plan.TrimStart + plan.TrimLength))
            {
                updated = updated.Remove(plan.TrimStart, plan.TrimLength);
                newCaretIndex -= plan.TrimLength;
            }

            return updated;
        }

        private static bool EndsBlock(string code)
        {
            string statement = code.TrimStart();
            if (statement is "pass" or "break" or "continue" or "return" or "raise")
            {
                return true;
            }

            return StartsWithKeyword(statement, "return") || StartsWithKeyword(statement, "raise");
        }

        private static bool StartsWithKeyword(string statement, string keyword) =>
            statement.Length > keyword.Length
            && statement.StartsWith(keyword, StringComparison.Ordinal)
            && !IsWordChar(statement[keyword.Length]);

        private static string CodePartOfLine(string line, out bool endsInString)
        {
            endsInString = false;
            char quote = '\0';
            int quoteLength = 0;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (quote != '\0')
                {
                    if (c == '\\')
                    {
                        i++;
                        continue;
                    }

                    if (c == quote && (quoteLength == 1 || HasQuoteRun(line, i, quote)))
                    {
                        i += quoteLength - 1;
                        quote = '\0';
                    }

                    continue;
                }

                if (c == '#')
                {
                    return line.Substring(0, i).TrimEnd();
                }

                if (c is '\'' or '"')
                {
                    quote = c;
                    quoteLength = HasQuoteRun(line, i, c) ? 3 : 1;
                    i += quoteLength - 1;
                }
            }

            endsInString = quote != '\0';
            return line.TrimEnd();
        }

        private static int LeadingIndentLength(string line)
        {
            int count = 0;
            while (count < line.Length && line[count] is ' ' or '\t')
            {
                count++;
            }

            return count;
        }

        private static bool IsBlank(string text, int from, int to)
        {
            for (int i = from; i < to; i++)
            {
                if (text[i] != ' ' && text[i] != '\t')
                {
                    return false;
                }
            }

            return true;
        }

        public static bool TryAlignBlockContinuation(string text, int selStart, int selEnd,
            out string updatedText, out int newCaretIndex)
        {
            text ??= "";
            updatedText = text;
            newCaretIndex = Clamp(selEnd, 0, text.Length);
            if (selStart != selEnd)
            {
                return false;
            }

            int caret = Clamp(selStart, 0, text.Length);
            int lineStart = LineStartIndex(text, caret);
            string before = text.Substring(lineStart, caret - lineStart);
            int indent = LeadingSpaces(before);
            string statement = before.Trim();
            string keyword = LeadingWord(statement);
            string[] openers = keyword switch
            {
                "else" when statement == "else" => new[] { "if", "elif", "for", "while", "try", "except" },
                "finally" when statement == "finally" => new[] { "try", "except", "else" },
                "elif" when statement.Length > 4 => new[] { "if", "elif" },
                "except" => new[] { "try", "except" },
                _ => null
            };
            if (openers == null || !IsCodeAt(text, caret))
            {
                return false;
            }

            int target = FindOpenerIndent(text.Split('\n'), LineStartsInsideString(text),
                LineIndexFromCaret(text, caret), indent, openers);
            if (target < 0 || target == indent)
            {
                return false;
            }

            string aligned = new string(' ', target) + before.Substring(indent) + ":";
            updatedText = text.Substring(0, lineStart) + aligned + text.Substring(caret);
            newCaretIndex = lineStart + aligned.Length;
            return true;
        }

        private static int FindOpenerIndent(string[] lines, bool[] insideString, int row, int indent, string[] openers)
        {
            int bound = indent;
            for (int r = row - 1; r >= 0 && bound >= 0; r--)
            {
                string line = lines[r];
                string trimmed = line.Trim();

                if (trimmed.Length == 0 || trimmed[0] == '#' || insideString[r])
                {
                    continue;
                }

                int own = LeadingSpaces(line);
                if (own > bound)
                {
                    continue;
                }

                if (Array.IndexOf(openers, LeadingWord(trimmed)) >= 0)
                {
                    return own;
                }

                bound = own - 1;
            }

            return -1;
        }

        private static bool[] LineStartsInsideString(string text)
        {
            bool[] code = MarkCodePositions(text);
            List<bool> inside = new() { false };
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    inside.Add(!code[i]);
                }
            }

            return inside.ToArray();
        }

        private static string LeadingWord(string statement)
        {
            int end = 0;
            while (end < statement.Length && IsWordChar(statement[end]))
            {
                end++;
            }

            return statement.Substring(0, end);
        }

        public static int LineFromCaret(string text, int caretIndex)
        {
            text ??= "";
            caretIndex = Clamp(caretIndex, 0, text.Length);

            int line = 1;
            for (int i = 0; i < caretIndex; i++)
            {
                if (text[i] == '\n')
                {
                    line++;
                }
            }

            return line;
        }

        public static int CountLines(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 1;
            }

            int lines = 1;
            foreach (char c in text)
            {
                if (c == '\n')
                {
                    lines++;
                }
            }

            return lines;
        }

        public static string BuildGutterText(
            int totalLines,
            int? markerLine,
            int? errorLine = null,
            int? caretLine = null,
            CodeGutterPalette palette = default)
        {
            StringBuilder sb = new();
            for (int i = 1; i <= Math.Max(1, totalLines); i++)
            {
                if (i > 1)
                {
                    sb.Append('\n');
                }

                sb.Append(BuildGutterLine(i, markerLine, errorLine, caretLine, palette));
            }

            return sb.ToString();
        }

        public static string BuildGutterLine(
            int number,
            int? markerLine,
            int? errorLine = null,
            int? caretLine = null,
            CodeGutterPalette palette = default,
            bool hinted = false)
        {
            StringBuilder sb = new();
            if (errorLine.HasValue && errorLine.Value == number)
            {
                AppendGutterNumber(sb, number, palette.ErrorHex, true);
            }
            else if (markerLine.HasValue && markerLine.Value == number)
            {
                AppendGutterNumber(sb, number, palette.MarkerHex, true);
            }
            else if (hinted)
            {
                AppendGutterNumber(sb, number, palette.HintHex, true);
            }
            else if (caretLine.HasValue && caretLine.Value == number)
            {

                AppendGutterNumber(sb, number, palette.CaretHex, false);
            }
            else
            {
                sb.Append(number);
            }

            return sb.ToString();
        }

        private static void AppendGutterNumber(StringBuilder sb, int number, string colourHex, bool bold)
        {
            bool coloured = !string.IsNullOrEmpty(colourHex);
            if (coloured)
            {
                sb.Append("<color=").Append(colourHex).Append('>');
            }

            if (bold)
            {
                sb.Append("<b>");
            }

            sb.Append(number);

            if (bold)
            {
                sb.Append("</b>");
            }

            if (coloured)
            {
                sb.Append("</color>");
            }
        }

        public static int CaretIndexAtPoint(string text, float x, float y, float lineHeight, Func<string, float> prefixWidth)
        {
            text ??= "";
            if (lineHeight <= 0f || prefixWidth == null || float.IsNaN(x) || float.IsNaN(y))
            {
                return text.Length;
            }

            int row = y <= 0f ? 0 : (int)Math.Floor(y / lineHeight);
            if (row >= CountLines(text))
            {
                return text.Length;
            }

            int start = 0;
            for (int i = 0; i < row; i++)
            {
                start = text.IndexOf('\n', start) + 1;
            }

            int end = text.IndexOf('\n', start);
            int length = (end < 0 ? text.Length : end) - start;
            if (x <= 0f || length == 0)
            {
                return start;
            }

            int low = 0;
            int high = length;
            while (low < high)
            {
                int middle = (low + high) / 2;
                if (prefixWidth(text.Substring(start, middle)) < x)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }

            if (low == 0)
            {
                return start;
            }

            float right = prefixWidth(text.Substring(start, low));
            if (right < x)
            {
                return start + length;
            }

            float left = prefixWidth(text.Substring(start, low - 1));
            return start + (x - left <= right - x ? low - 1 : low);
        }

        public static void ChangedLineRange(
            IReadOnlyList<string> previous,
            IReadOnlyList<string> next,
            out int prefix,
            out int oldMiddle,
            out int newMiddle)
        {
            int oldCount = previous?.Count ?? 0;
            int newCount = next?.Count ?? 0;
            prefix = 0;
            while (prefix < oldCount && prefix < newCount
                   && string.Equals(previous[prefix], next[prefix], StringComparison.Ordinal))
            {
                prefix++;
            }

            int suffix = 0;
            while (suffix < oldCount - prefix && suffix < newCount - prefix
                   && string.Equals(previous[oldCount - 1 - suffix], next[newCount - 1 - suffix],
                       StringComparison.Ordinal))
            {
                suffix++;
            }

            oldMiddle = oldCount - prefix - suffix;
            newMiddle = newCount - prefix - suffix;
        }

        public static int? TracebackLineFromOutput(string outputLine) =>
            ParseTracebackLine(outputLine);

        private static int? ParseTracebackLine(string outputLine)
        {
            if (string.IsNullOrEmpty(outputLine)) return null;
            int length = Math.Min(outputLine.Length, CodeEditorLimits.MaximumOutputCharacters);
            for (int index = 0; index < length; index++)
            {
                if (outputLine[index] != ':') continue;
                int start = index + 1;
                int end = start;
                while (end < length && end - start < 10 && outputLine[end] >= '0' && outputLine[end] <= '9') end++;
                if (end > start && end < length && outputLine[end] == ':' &&
                    int.TryParse(outputLine.Substring(start, end - start), out int line) && line > 0)
                    return line;
            }
            return null;
        }

        private const string OpenBrackets = "([{";

        private const string CloseBrackets = ")]}";

        public sealed class CodeMaskCache
        {
            private string _text;
            private bool[] _mask;

            public int MarkCount { get; private set; }

            internal bool[] Mask(string text)
            {
                if (_mask != null
                    && (ReferenceEquals(text, _text) || string.Equals(text, _text, StringComparison.Ordinal)))
                {
                    return _mask;
                }

                _text = text;
                _mask = MarkCodePositions(text);
                MarkCount++;
                return _mask;
            }
        }

        public static int MatchingBracketIndex(string text, int bracketIndex, CodeMaskCache cache = null)
        {
            if (string.IsNullOrEmpty(text) || bracketIndex < 0 || bracketIndex >= text.Length)
            {
                return -1;
            }

            char target = text[bracketIndex];
            int openKind = OpenBrackets.IndexOf(target);
            int closeKind = CloseBrackets.IndexOf(target);
            if (openKind < 0 && closeKind < 0)
            {

                return -1;
            }

            bool[] code = cache != null ? cache.Mask(text) : MarkCodePositions(text);
            if (!code[bracketIndex])
            {
                return -1;
            }

            int step = openKind >= 0 ? 1 : -1;
            char opposite = openKind >= 0 ? CloseBrackets[openKind] : OpenBrackets[closeKind];
            int depth = 0;
            for (int i = bracketIndex; i >= 0 && i < text.Length; i += step)
            {
                if (!code[i])
                {
                    continue;
                }

                if (text[i] == target)
                {
                    depth++;
                }
                else if (text[i] == opposite)
                {
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }
                }
            }

            return -1;
        }

        public static bool TryFindBracketPair(
            string text,
            int caretIndex,
            out int first,
            out int second,
            CodeMaskCache cache = null)
        {
            first = -1;
            second = -1;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            caretIndex = Clamp(caretIndex, 0, text.Length);
            for (int candidate = caretIndex - 1; candidate <= caretIndex; candidate++)
            {
                if (candidate < 0 || candidate >= text.Length)
                {
                    continue;
                }

                int match = MatchingBracketIndex(text, candidate, cache);
                if (match >= 0)
                {
                    first = Math.Min(candidate, match);
                    second = Math.Max(candidate, match);
                    return true;
                }
            }

            return false;
        }

        internal static bool[] MarkCodePositions(string text)
        {
            text ??= "";
            bool[] code = new bool[text.Length];
            ScanCode(text, code, -1);
            return code;
        }

        public static bool IsCodeAt(string text, int caretIndex)
        {
            text ??= "";
            return ScanCode(text, null, Clamp(caretIndex, 0, text.Length));
        }

        private static bool ScanCode(string text, bool[] code, int stopAt)
        {
            int i = 0;
            while (i < text.Length)
            {
                if (stopAt >= 0 && i >= stopAt)
                {
                    return true;
                }

                char c = text[i];

                if (c == '#')
                {
                    int commentStart = i;
                    while (i < text.Length && text[i] != '\n')
                    {
                        i++;
                    }

                    if (stopAt > commentStart && stopAt <= i)
                    {
                        return false;
                    }

                    continue;
                }

                if (c is '\'' or '"')
                {
                    int stringStart = i;
                    bool closed = false;
                    int quoteLength = HasQuoteRun(text, i, c) ? 3 : 1;
                    i += quoteLength;
                    while (i < text.Length)
                    {
                        if (text[i] == '\\')
                        {
                            i += 2;
                            continue;
                        }

                        if (text[i] == c && (quoteLength == 1 || HasQuoteRun(text, i, c)))
                        {
                            i += quoteLength;
                            closed = true;
                            break;
                        }

                        if (quoteLength == 1 && text[i] == '\n')
                        {
                            break;
                        }

                        i++;
                    }

                    i = Math.Min(i, text.Length);
                    if (stopAt > stringStart && (closed ? stopAt < i : stopAt <= i))
                    {
                        return false;
                    }

                    continue;
                }

                if (code != null)
                {
                    code[i] = true;
                }

                i++;
            }

            return true;
        }

        private static bool HasQuoteRun(string text, int index, char quote) =>
            index + 2 < text.Length && text[index + 1] == quote && text[index + 2] == quote;

        private static int Clamp(int value, int min, int max) =>
            value < min ? min : value > max ? max : value;

        private static long Clamp64(long value, long min, long max) =>
            value < min ? min : value > max ? max : value;
    }
}
