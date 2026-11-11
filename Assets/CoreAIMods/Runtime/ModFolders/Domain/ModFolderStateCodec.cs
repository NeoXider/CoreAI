using System;
using System.Collections.Generic;
using System.Text;

namespace CoreAI.Mods.Folders
{
    /// <summary>
    /// The persistence format of the two pieces of folder state that no mod carries: the persisted folders
    /// of a mod store (created or kept folders, empty ones included) and a viewer's fold state. Both are
    /// UTF-8 text, one entry per line, LF-separated, sorted, so the files diff cleanly and need no JSON
    /// library in this assembly:
    /// <code>
    /// coreai-mod-folders 1
    /// folder Games/Arcade
    /// folder Tools
    /// </code>
    /// <code>
    /// coreai-mod-folds 1
    /// default expanded
    /// toggled Games/Arcade
    /// </code>
    /// A path never holds a line break (control characters are refused by <see cref="ModFolderPath.Parse"/>),
    /// so a line is always one whole entry. A reader skips (and counts) lines it does not understand and
    /// folder lines that fail the strict path rules, and refuses a different header or version with
    /// <see cref="ModFolderErrorCode.UnsupportedFormat"/>. Empty text reads as an empty document.
    /// </summary>
    public static class ModFolderStateCodec
    {
        /// <summary>The first line of a persisted-folders document.</summary>
        public const string FoldersHeader = "coreai-mod-folders 1";

        /// <summary>The first line of a fold-state document.</summary>
        public const string FoldStateHeader = "coreai-mod-folds 1";

        private const string FolderPrefix = "folder ";
        private const string DefaultPrefix = "default ";
        private const string ToggledPrefix = "toggled ";

        /// <summary>
        /// Writes persisted folders: read leniently, the top level dropped, duplicates (case-insensitive)
        /// written once in the casing first given, sorted.
        /// </summary>
        public static string EncodeFolders(IEnumerable<string> folders)
        {
            List<string> unique = UniqueSortedPaths(folders);
            StringBuilder text = new(FoldersHeader.Length + 1 + unique.Count * 24);
            text.Append(FoldersHeader).Append('\n');
            foreach (string path in unique)
            {
                text.Append(FolderPrefix).Append(path).Append('\n');
            }

            return text.ToString();
        }

        /// <summary>Reads a persisted-folders document.</summary>
        public static ModFolderDocument DecodeFolders(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return ModFolderDocument.Read(Array.Empty<string>(), 0);
            }

            List<string> lines = SplitLines(text);
            int first = FirstContentLine(lines);
            if (!string.Equals(lines[first], FoldersHeader, StringComparison.Ordinal))
            {
                return ModFolderDocument.Refused("The folder document starts with '" + Clip(lines[first])
                                                 + "', not '" + FoldersHeader + "'.");
            }

            List<string> folders = new();
            HashSet<string> seen = new(ModFolderPath.Comparer);
            int skipped = 0;
            for (int i = first + 1; i < lines.Count; i++)
            {
                string line = lines[i];
                if (line.Length == 0)
                {
                    continue;
                }

                if (!line.StartsWith(FolderPrefix, StringComparison.Ordinal))
                {
                    skipped++;
                    continue;
                }

                ModFolderPathParse parse = ModFolderPath.Parse(line.Substring(FolderPrefix.Length));
                if (!parse.Succeeded || parse.Path.IsRoot || !seen.Add(parse.Path.Value))
                {
                    skipped++;
                    continue;
                }

                folders.Add(parse.Path.Value);
            }

            folders.Sort(ModFolderTree.ComparePaths);
            return ModFolderDocument.Read(folders, skipped);
        }

        /// <summary>Writes a fold state: its default and the folders toggled away from it, sorted.</summary>
        public static string EncodeFoldState(ModFolderFoldState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            IReadOnlyList<string> toggled = state.GetToggledPaths();
            StringBuilder text = new(FoldStateHeader.Length + 32 + toggled.Count * 24);
            text.Append(FoldStateHeader).Append('\n');
            text.Append(DefaultPrefix).Append(state.ExpandedByDefault ? "expanded" : "collapsed").Append('\n');
            foreach (string path in toggled)
            {
                text.Append(ToggledPrefix).Append(path).Append('\n');
            }

            return text.ToString();
        }

        /// <summary>
        /// Reads a fold state. Toggled paths are read leniently, because a viewer may have closed a legacy
        /// folder whose name the strict rules refuse. Without a <c>default</c> line the default is
        /// <paramref name="expandedByDefault"/>.
        /// </summary>
        public static ModFolderFoldStateDocument DecodeFoldState(string text, bool expandedByDefault = true)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return ModFolderFoldStateDocument.Read(new ModFolderFoldState(expandedByDefault), 0);
            }

            List<string> lines = SplitLines(text);
            int first = FirstContentLine(lines);
            if (!string.Equals(lines[first], FoldStateHeader, StringComparison.Ordinal))
            {
                return ModFolderFoldStateDocument.Refused("The fold-state document starts with '" + Clip(lines[first])
                                                          + "', not '" + FoldStateHeader + "'.", expandedByDefault);
            }

            bool expanded = expandedByDefault;
            int skipped = 0;
            List<string> toggled = new();
            for (int i = first + 1; i < lines.Count; i++)
            {
                string line = lines[i];
                if (line.Length == 0)
                {
                    continue;
                }

                if (line.StartsWith(DefaultPrefix, StringComparison.Ordinal))
                {
                    string value = line.Substring(DefaultPrefix.Length);
                    if (value == "expanded")
                    {
                        expanded = true;
                    }
                    else if (value == "collapsed")
                    {
                        expanded = false;
                    }
                    else
                    {
                        skipped++;
                    }

                    continue;
                }

                if (!line.StartsWith(ToggledPrefix, StringComparison.Ordinal))
                {
                    skipped++;
                    continue;
                }

                ModFolderPath path = ModFolderPath.FromStored(line.Substring(ToggledPrefix.Length));
                if (path.IsRoot)
                {
                    skipped++;
                    continue;
                }

                toggled.Add(path.Value);
            }

            ModFolderFoldState state = new(expanded);
            foreach (string path in toggled)
            {
                state.SetExpanded(ModFolderPath.FromNormalized(path), !expanded);
            }

            return ModFolderFoldStateDocument.Read(state, skipped);
        }

        private static List<string> UniqueSortedPaths(IEnumerable<string> folders)
        {
            List<string> unique = new();
            if (folders == null)
            {
                return unique;
            }

            HashSet<string> seen = new(ModFolderPath.Comparer);
            foreach (string folder in folders)
            {
                ModFolderPath path = ModFolderPath.FromStored(folder);
                if (!path.IsRoot && seen.Add(path.Value))
                {
                    unique.Add(path.Value);
                }
            }

            unique.Sort(ModFolderTree.ComparePaths);
            return unique;
        }

        private static List<string> SplitLines(string text)
        {
            List<string> lines = new();
            int start = 0;
            for (int i = 0; i <= text.Length; i++)
            {
                if (i == text.Length || text[i] == '\n')
                {
                    int end = i;
                    if (end > start && text[end - 1] == '\r')
                    {
                        end--;
                    }

                    lines.Add(text.Substring(start, end - start));
                    start = i + 1;
                }
            }

            return lines;
        }

        private static int FirstContentLine(List<string> lines)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i];
                if (line.Length > 0 && line[0] == '﻿')
                {
                    lines[i] = line = line.Substring(1);
                }

                if (line.Trim().Length > 0)
                {
                    lines[i] = line.Trim();
                    return i;
                }
            }

            return 0;
        }

        private static string Clip(string text)
        {
            return text.Length <= 40 ? text : text.Substring(0, 40) + "...";
        }
    }

    /// <summary>A read persisted-folders document.</summary>
    public sealed class ModFolderDocument
    {
        private ModFolderDocument(IReadOnlyList<string> folders, int skippedLines, ModFolderErrorCode error,
            string message)
        {
            Folders = folders;
            SkippedLines = skippedLines;
            Error = error;
            Message = message ?? "";
        }

        /// <summary>The persisted folders, normalized and sorted; empty when refused.</summary>
        public IReadOnlyList<string> Folders { get; }

        /// <summary>Lines the reader did not understand or whose path failed the rules.</summary>
        public int SkippedLines { get; }

        /// <summary><see cref="ModFolderErrorCode.UnsupportedFormat"/> when the header was not recognised.</summary>
        public ModFolderErrorCode Error { get; }

        /// <summary>Why the document was refused; empty when read.</summary>
        public string Message { get; }

        /// <summary>True when the document was read.</summary>
        public bool Succeeded => Error == ModFolderErrorCode.None;

        internal static ModFolderDocument Read(IReadOnlyList<string> folders, int skipped)
        {
            return new ModFolderDocument(folders, skipped, ModFolderErrorCode.None, "");
        }

        internal static ModFolderDocument Refused(string message)
        {
            return new ModFolderDocument(Array.Empty<string>(), 0, ModFolderErrorCode.UnsupportedFormat, message);
        }
    }

    /// <summary>A read fold-state document.</summary>
    public sealed class ModFolderFoldStateDocument
    {
        private ModFolderFoldStateDocument(ModFolderFoldState state, int skippedLines, ModFolderErrorCode error,
            string message)
        {
            State = state;
            SkippedLines = skippedLines;
            Error = error;
            Message = message ?? "";
        }

        /// <summary>The fold state read; a fresh default state when refused.</summary>
        public ModFolderFoldState State { get; }

        /// <summary>Lines the reader did not understand.</summary>
        public int SkippedLines { get; }

        /// <summary><see cref="ModFolderErrorCode.UnsupportedFormat"/> when the header was not recognised.</summary>
        public ModFolderErrorCode Error { get; }

        /// <summary>Why the document was refused; empty when read.</summary>
        public string Message { get; }

        /// <summary>True when the document was read.</summary>
        public bool Succeeded => Error == ModFolderErrorCode.None;

        internal static ModFolderFoldStateDocument Read(ModFolderFoldState state, int skipped)
        {
            return new ModFolderFoldStateDocument(state, skipped, ModFolderErrorCode.None, "");
        }

        internal static ModFolderFoldStateDocument Refused(string message, bool expandedByDefault)
        {
            return new ModFolderFoldStateDocument(new ModFolderFoldState(expandedByDefault), 0,
                ModFolderErrorCode.UnsupportedFormat, message);
        }
    }
}
