using System;
using System.Collections.Generic;

namespace CoreAI.ChatOverlay
{
    /// <summary>Column alignment of a Markdown pipe table, from its delimiter row.</summary>
    public enum ChatMarkdownTableAlignment
    {
        /// <summary>No colon in the delimiter cell.</summary>
        None = 0,

        /// <summary><c>:---</c></summary>
        Left = 1,

        /// <summary><c>:---:</c></summary>
        Center = 2,

        /// <summary><c>---:</c></summary>
        Right = 3
    }

    /// <summary>
    /// One piece of a chat answer as returned by <see cref="ChatMarkdownText.SplitTables"/>: either Markdown for a
    /// renderer, or a pipe table already split into cells (cell text is still inline Markdown).
    /// </summary>
    public sealed class ChatMarkdownSegment
    {
        private static readonly IReadOnlyList<string> NoCells = Array.Empty<string>();
        private static readonly IReadOnlyList<ChatMarkdownTableAlignment> NoAlignments =
            Array.Empty<ChatMarkdownTableAlignment>();
        private static readonly IReadOnlyList<IReadOnlyList<string>> NoRows = Array.Empty<IReadOnlyList<string>>();

        private ChatMarkdownSegment(string markdown, IReadOnlyList<string> header,
            IReadOnlyList<ChatMarkdownTableAlignment> alignments, IReadOnlyList<IReadOnlyList<string>> rows,
            bool isTable)
        {
            Markdown = markdown ?? "";
            Header = header ?? NoCells;
            Alignments = alignments ?? NoAlignments;
            Rows = rows ?? NoRows;
            IsTable = isTable;
        }

        /// <summary>True for a pipe table; false for Markdown text.</summary>
        public bool IsTable { get; }

        /// <summary>The Markdown of a text segment; empty for a table.</summary>
        public string Markdown { get; }

        /// <summary>Header cells of a table; empty for a text segment.</summary>
        public IReadOnlyList<string> Header { get; }

        /// <summary>One alignment per header cell.</summary>
        public IReadOnlyList<ChatMarkdownTableAlignment> Alignments { get; }

        /// <summary>Body rows, each with exactly as many cells as the header.</summary>
        public IReadOnlyList<IReadOnlyList<string>> Rows { get; }

        internal static ChatMarkdownSegment ForMarkdown(string markdown)
        {
            return new ChatMarkdownSegment(markdown, null, null, null, false);
        }

        internal static ChatMarkdownSegment ForTable(IReadOnlyList<string> header,
            IReadOnlyList<ChatMarkdownTableAlignment> alignments, IReadOnlyList<IReadOnlyList<string>> rows)
        {
            return new ChatMarkdownSegment("", header, alignments, rows, true);
        }
    }
}
