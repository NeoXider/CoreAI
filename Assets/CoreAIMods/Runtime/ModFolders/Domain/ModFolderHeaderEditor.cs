using System;
using System.Collections.Generic;
using System.Text;

namespace CoreAI.Mods.Folders
{
    /// <summary>
    /// Reads and rewrites the <c>category:</c> entry of a mod's <c>@coreai</c> header, the one place a mod's
    /// folder is authored. Every manifest writer derives <c>Category</c> from the header, so a folder move
    /// that changed only the manifest would be undone by the next save; moving a mod therefore rewrites
    /// this one entry and nothing else.
    /// <para>
    /// The scanner follows <c>LuaModHeader.Parse</c> exactly: a block header is <c>--[[@coreai</c> at index 0
    /// up to the first <c>]]</c>; otherwise the leading <c>-- @coreai key: value</c> comment lines form a
    /// line header. Keys compare case-insensitively, a value runs to the end of its line and is trimmed, and
    /// the last <c>category</c> entry wins. A block opener without <c>]]</c> is no header to the parser and is
    /// refused here (<see cref="ModFolderErrorCode.HeaderUnclosed"/>), because any insertion would land
    /// inside an unterminated comment.
    /// </para>
    /// </summary>
    public static class ModFolderHeaderEditor
    {
        /// <summary>The header key that holds the folder.</summary>
        public const string CategoryKey = "category";

        private const string BlockMarker = "--[[@coreai";
        private const string LineMarker = "@coreai";

        /// <summary>
        /// The raw <c>category</c> value <c>LuaModHeader.Parse</c> would return for <paramref name="source"/>
        /// (empty without one).
        /// </summary>
        public static string ReadCategory(string source)
        {
            HeaderScan scan = Scan(source);
            if (scan.Entries.Count == 0)
            {
                return "";
            }

            CategoryEntry last = scan.Entries[scan.Entries.Count - 1];
            return source.Substring(last.ValueStart, last.ValueEnd - last.ValueStart);
        }

        /// <summary>
        /// Returns <paramref name="source"/> with its folder set to <paramref name="folderPath"/>, which must
        /// pass <see cref="ModFolderPath.Parse"/> (it is written normalized). Every existing <c>category</c>
        /// entry gets the new value in place, so no line moves. Without one, a <c>category:</c> line is added
        /// after the block opener, or a <c>-- @coreai category:</c> line before the first header line, or, for
        /// a source with no header, as a new first line. Setting the top level on a source without a category
        /// changes nothing.
        /// </summary>
        public static ModFolderHeaderEdit SetCategory(string source, string folderPath)
        {
            ModFolderPathParse parse = ModFolderPath.Parse(folderPath);
            if (!parse.Succeeded)
            {
                return ModFolderHeaderEdit.Failure(parse.Error, parse.Message);
            }

            string text = source ?? "";
            string value = parse.Path.Value;
            HeaderScan scan = Scan(text);
            if (scan.Kind == HeaderKind.UnclosedBlock)
            {
                return ModFolderHeaderEdit.Failure(ModFolderErrorCode.HeaderUnclosed,
                    "The mod source starts a --[[@coreai header that is never closed with ]]; close it before "
                    + "moving the mod.");
            }

            if (scan.Entries.Count > 0)
            {
                StringBuilder edited = new(text.Length + value.Length);
                int copied = 0;
                foreach (CategoryEntry entry in scan.Entries)
                {
                    if (value.Length == 0)
                    {
                        edited.Append(text, copied, entry.ColonIndex + 1 - copied);
                    }
                    else if (entry.ValueStart == entry.ValueEnd)
                    {
                        edited.Append(text, copied, entry.ValueStart - copied);
                        if (entry.ValueStart == entry.ColonIndex + 1)
                        {
                            edited.Append(' ');
                        }

                        edited.Append(value);
                    }
                    else
                    {
                        edited.Append(text, copied, entry.ValueStart - copied);
                        edited.Append(value);
                    }

                    copied = entry.ValueEnd;
                }

                edited.Append(text, copied, text.Length - copied);
                return ModFolderHeaderEdit.Success(edited.ToString(), value);
            }

            if (value.Length == 0)
            {
                return ModFolderHeaderEdit.Success(text, value);
            }

            string newline = text.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";
            if (scan.Kind == HeaderKind.Block)
            {
                int afterMarker = BlockMarker.Length;
                if (StartsWithAt(text, afterMarker, "\r\n"))
                {
                    return ModFolderHeaderEdit.Success(
                        text.Insert(afterMarker + 2, CategoryKey + ": " + value + "\r\n"), value);
                }

                if (StartsWithAt(text, afterMarker, "\n"))
                {
                    return ModFolderHeaderEdit.Success(
                        text.Insert(afterMarker + 1, CategoryKey + ": " + value + "\n"), value);
                }

                return ModFolderHeaderEdit.Success(
                    text.Insert(afterMarker, newline + CategoryKey + ": " + value + newline), value);
            }

            int insertAt = scan.Kind == HeaderKind.Line ? scan.FirstLineStart : 0;
            return ModFolderHeaderEdit.Success(
                text.Insert(insertAt, "-- " + LineMarker + " " + CategoryKey + ": " + value + newline), value);
        }

        /// <summary>
        /// True when <paramref name="after"/> is exactly what <see cref="SetCategory"/> makes of
        /// <paramref name="before"/> for the category <paramref name="after"/> declares: the code is the same
        /// and only the folder entry differs. A runtime can use it to swap the live text of a loaded mod
        /// after a move without running the mod again.
        /// </summary>
        public static bool DiffersOnlyInCategory(string before, string after)
        {
            if (before == null || after == null)
            {
                return false;
            }

            if (string.Equals(before, after, StringComparison.Ordinal))
            {
                return true;
            }

            ModFolderHeaderEdit edit = SetCategory(before, ReadCategory(after));
            return edit.Succeeded && string.Equals(edit.Source, after, StringComparison.Ordinal);
        }

        private static HeaderScan Scan(string source)
        {
            HeaderScan scan = new();
            if (string.IsNullOrEmpty(source))
            {
                return scan;
            }

            if (source.StartsWith(BlockMarker, StringComparison.Ordinal))
            {
                int end = source.IndexOf("]]", BlockMarker.Length, StringComparison.Ordinal);
                if (end < 0)
                {
                    scan.Kind = HeaderKind.UnclosedBlock;
                    return scan;
                }

                scan.Kind = HeaderKind.Block;
                ScanBlock(source, BlockMarker.Length, end, scan.Entries);
                return scan;
            }

            ScanLines(source, scan);
            return scan;
        }

        private static void ScanBlock(string source, int start, int end, List<CategoryEntry> entries)
        {
            int position = start;
            while (position < end)
            {
                int lineStart = position;
                int lineEnd = source.IndexOf('\n', lineStart);
                if (lineEnd < 0 || lineEnd > end)
                {
                    lineEnd = end;
                    position = end;
                }
                else
                {
                    position = lineEnd + 1;
                }

                if (lineEnd > lineStart && source[lineEnd - 1] == '\r')
                {
                    lineEnd--;
                }

                ScanEntry(source, lineStart, lineEnd, entries);
            }
        }

        private static void ScanLines(string source, HeaderScan scan)
        {
            int position = 0;
            bool parsed = false;
            while (position < source.Length)
            {
                int lineStart = position;
                int lineEnd = source.IndexOf('\n', lineStart);
                if (lineEnd < 0)
                {
                    lineEnd = source.Length;
                    position = source.Length;
                }
                else
                {
                    position = lineEnd + 1;
                }

                int contentEnd = lineEnd;
                if (contentEnd > lineStart && source[contentEnd - 1] == '\r')
                {
                    contentEnd--;
                }

                int contentStart = SkipWhitespace(source, lineStart, contentEnd);
                if (contentStart == contentEnd)
                {
                    if (parsed)
                    {
                        break;
                    }

                    continue;
                }

                if (!StartsWithAt(source, contentStart, contentEnd, "--"))
                {
                    break;
                }

                int markerStart = SkipWhitespace(source, contentStart + 2, contentEnd);
                if (!StartsWithAt(source, markerStart, contentEnd, LineMarker))
                {
                    break;
                }

                if (!parsed)
                {
                    scan.FirstLineStart = lineStart;
                }

                int entryStart = SkipWhitespace(source, markerStart + LineMarker.Length, contentEnd);
                ScanEntry(source, entryStart, contentEnd, scan.Entries);
                parsed = true;
            }

            if (parsed)
            {
                scan.Kind = HeaderKind.Line;
            }
        }

        private static void ScanEntry(string source, int start, int end, List<CategoryEntry> entries)
        {
            start = SkipWhitespace(source, start, end);
            end = TrimEnd(source, start, end);
            if (start >= end || StartsWithAt(source, start, end, LineMarker))
            {
                return;
            }

            int colon = source.IndexOf(':', start, end - start);
            if (colon < 0)
            {
                return;
            }

            int keyEnd = TrimEnd(source, start, colon);
            if (keyEnd - start != CategoryKey.Length ||
                string.Compare(source, start, CategoryKey, 0, CategoryKey.Length, StringComparison.OrdinalIgnoreCase) != 0)
            {
                return;
            }

            int valueStart = SkipWhitespace(source, colon + 1, end);
            int valueEnd = TrimEnd(source, valueStart, end);
            entries.Add(new CategoryEntry(colon, valueStart, valueEnd));
        }

        private static int SkipWhitespace(string source, int start, int end)
        {
            while (start < end && char.IsWhiteSpace(source[start]))
            {
                start++;
            }

            return start;
        }

        private static int TrimEnd(string source, int start, int end)
        {
            while (end > start && char.IsWhiteSpace(source[end - 1]))
            {
                end--;
            }

            return end;
        }

        private static bool StartsWithAt(string source, int start, string token)
        {
            return StartsWithAt(source, start, source.Length, token);
        }

        private static bool StartsWithAt(string source, int start, int end, string token)
        {
            if (start < 0 || start + token.Length > end)
            {
                return false;
            }

            return string.CompareOrdinal(source, start, token, 0, token.Length) == 0;
        }

        private enum HeaderKind
        {
            None,
            Block,
            Line,
            UnclosedBlock
        }

        private readonly struct CategoryEntry
        {
            public CategoryEntry(int colonIndex, int valueStart, int valueEnd)
            {
                ColonIndex = colonIndex;
                ValueStart = valueStart;
                ValueEnd = valueEnd;
            }

            public int ColonIndex { get; }

            public int ValueStart { get; }

            public int ValueEnd { get; }
        }

        private sealed class HeaderScan
        {
            public HeaderKind Kind = HeaderKind.None;
            public int FirstLineStart;
            public readonly List<CategoryEntry> Entries = new();
        }
    }

    /// <summary>The outcome of <see cref="ModFolderHeaderEditor.SetCategory"/>.</summary>
    public readonly struct ModFolderHeaderEdit
    {
        private ModFolderHeaderEdit(string source, string category, ModFolderErrorCode error, string message)
        {
            Source = source;
            Category = category ?? "";
            Error = error;
            Message = message ?? "";
        }

        /// <summary>The edited source; null on failure.</summary>
        public string Source { get; }

        /// <summary>The normalized folder written into the header.</summary>
        public string Category { get; }

        /// <summary>Why the edit failed; <see cref="ModFolderErrorCode.None"/> on success.</summary>
        public ModFolderErrorCode Error { get; }

        /// <summary>A sentence naming the failure; empty on success.</summary>
        public string Message { get; }

        /// <summary>True when <see cref="Source"/> holds the edited text.</summary>
        public bool Succeeded => Error == ModFolderErrorCode.None;

        internal static ModFolderHeaderEdit Success(string source, string category)
        {
            return new ModFolderHeaderEdit(source, category, ModFolderErrorCode.None, "");
        }

        internal static ModFolderHeaderEdit Failure(ModFolderErrorCode error, string message)
        {
            return new ModFolderHeaderEdit(null, "", error, message);
        }
    }
}
