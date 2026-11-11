using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CoreAI.Mods.Folders
{
    /// <summary>
    /// A mod's folder: the <c>category</c> of its <c>@coreai</c> header and manifest read as a
    /// <c>/</c>-separated path (<c>Games/Arcade</c>). The empty path is the top level. A one-level category
    /// (<c>Samples</c>) is a valid one-segment path, so mods written before folders existed load unchanged.
    /// <para>
    /// Two readings exist. <see cref="Parse"/> is strict and is used for every path a player, a tool or an
    /// operation writes: it fails with a named <see cref="ModFolderErrorCode"/>. <see cref="FromStored"/> is
    /// lenient and never fails: it reads whatever a stored manifest holds (trimmed, empty segments dropped),
    /// so a legacy category with characters the strict rules refuse still shows as a folder and can be
    /// renamed.
    /// </para>
    /// <para>
    /// Folder identity is case-insensitive (<see cref="Comparer"/>): <c>Games</c> and <c>games</c> are one
    /// folder, shown with the casing it was first written in.
    /// </para>
    /// </summary>
    public readonly struct ModFolderPath : IEquatable<ModFolderPath>
    {
        /// <summary>The segment separator. A backslash is read as a separator too and written as this.</summary>
        public const char Separator = '/';

        /// <summary>
        /// The longest segment, in UTF-16 characters. Below the 100-character limit of a Roblox
        /// <c>Instance.Name</c>, so every segment maps onto a <c>Folder</c> name without truncation.
        /// </summary>
        public const int MaxSegmentLength = 64;

        /// <summary>The most segments a path may have.</summary>
        public const int MaxDepth = 8;

        /// <summary>The longest whole path, in UTF-16 characters, separators included.</summary>
        public const int MaxLength = 255;

        /// <summary>
        /// The characters a segment may hold besides letters, combining marks and decimal digits of any
        /// script: space and <c>_ - . ( ) &amp; + ! ' , #</c>.
        /// </summary>
        public const string AllowedPunctuation = " _-.()&+!',#";

        /// <summary>The top level: the folder of every mod without a category.</summary>
        public static readonly ModFolderPath Root = default;

        /// <summary>The comparer of folder identity: ordinal, case-insensitive.</summary>
        public static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

        private readonly string _value;

        private ModFolderPath(string value)
        {
            _value = value;
        }

        /// <summary>The normalized path text; empty for the top level.</summary>
        public string Value => _value ?? "";

        /// <summary>True for the top level.</summary>
        public bool IsRoot => string.IsNullOrEmpty(_value);

        /// <summary>The number of segments; 0 for the top level.</summary>
        public int Depth
        {
            get
            {
                if (IsRoot)
                {
                    return 0;
                }

                int depth = 1;
                string value = _value;
                for (int i = 0; i < value.Length; i++)
                {
                    if (value[i] == Separator)
                    {
                        depth++;
                    }
                }

                return depth;
            }
        }

        /// <summary>The last segment; empty for the top level.</summary>
        public string Name
        {
            get
            {
                if (IsRoot)
                {
                    return "";
                }

                int slash = _value.LastIndexOf(Separator);
                return slash < 0 ? _value : _value.Substring(slash + 1);
            }
        }

        /// <summary>The folder that holds this one; the top level for a one-segment path and for the top level.</summary>
        public ModFolderPath Parent
        {
            get
            {
                if (IsRoot)
                {
                    return Root;
                }

                int slash = _value.LastIndexOf(Separator);
                return slash < 0 ? Root : new ModFolderPath(_value.Substring(0, slash));
            }
        }

        /// <summary>The segments in order; empty for the top level.</summary>
        public string[] GetSegments()
        {
            return IsRoot ? Array.Empty<string>() : _value.Split(Separator);
        }

        /// <summary>
        /// True when this path is <paramref name="ancestor"/> or lies inside it, compared segment by segment
        /// and case-insensitively: <c>Games/Arcade</c> is inside <c>Games</c>, <c>GamesOld</c> is not.
        /// </summary>
        public bool IsSameOrDescendantOf(ModFolderPath ancestor)
        {
            if (ancestor.IsRoot)
            {
                return true;
            }

            string value = Value;
            string prefix = ancestor._value;
            if (value.Length < prefix.Length ||
                string.Compare(value, 0, prefix, 0, prefix.Length, StringComparison.OrdinalIgnoreCase) != 0)
            {
                return false;
            }

            return value.Length == prefix.Length || value[prefix.Length] == Separator;
        }

        /// <summary>True when this path lies strictly inside <paramref name="ancestor"/>.</summary>
        public bool IsDescendantOf(ModFolderPath ancestor)
        {
            return IsSameOrDescendantOf(ancestor) && !Equals(ancestor);
        }

        /// <summary>
        /// This path moved from under <paramref name="from"/> to under <paramref name="to"/>:
        /// <c>Games/Arcade/Retro</c> rebased from <c>Games/Arcade</c> onto <c>Old/Arcade</c> is
        /// <c>Old/Arcade/Retro</c>. Throws when this path is not inside <paramref name="from"/>.
        /// </summary>
        public ModFolderPath RebaseOnto(ModFolderPath from, ModFolderPath to)
        {
            if (!IsSameOrDescendantOf(from))
            {
                throw new ArgumentException("'" + Value + "' is not inside '" + from.Value + "'.", nameof(from));
            }

            string suffix = from.IsRoot
                ? Value
                : Value.Length == from.Value.Length
                    ? ""
                    : Value.Substring(from.Value.Length + 1);
            if (suffix.Length == 0)
            {
                return to;
            }

            return to.IsRoot ? new ModFolderPath(suffix) : new ModFolderPath(to.Value + Separator + suffix);
        }

        /// <summary>
        /// The path of a folder named <paramref name="name"/> inside this one. The name must already be one
        /// normalized segment (from <see cref="ParseName"/> or an existing folder); nothing is checked here.
        /// </summary>
        public ModFolderPath Child(string name)
        {
            return IsRoot ? new ModFolderPath(name) : new ModFolderPath(Value + Separator + name);
        }

        /// <summary>
        /// Strictly reads a path someone wants to write. Whitespace around segments and leading or trailing
        /// separators are dropped, runs of spaces become one space, a backslash reads as a separator; an
        /// empty or blank text is the top level. Every rule violation fails with its own
        /// <see cref="ModFolderErrorCode"/> and a message naming the segment.
        /// </summary>
        public static ModFolderPathParse Parse(string text)
        {
            if (text == null || IsBlank(text))
            {
                return ModFolderPathParse.Success(Root);
            }

            List<string> rawSegments = SplitSegments(text);
            int first = 0;
            int last = rawSegments.Count - 1;
            while (first <= last && IsBlank(rawSegments[first]))
            {
                first++;
            }

            while (last >= first && IsBlank(rawSegments[last]))
            {
                last--;
            }

            if (first > last)
            {
                return ModFolderPathParse.Success(Root);
            }

            int depth = last - first + 1;
            StringBuilder path = new(text.Length);
            for (int index = first; index <= last; index++)
            {
                int position = index - first + 1;
                string raw = rawSegments[index];
                if (IsBlank(raw))
                {
                    return ModFolderPathParse.Failure(ModFolderErrorCode.EmptySegment,
                        "Folder path '" + text + "' has an empty segment at position " + position
                        + "; remove the doubled separator.");
                }

                if (!TryNormalizeSegment(raw, out string segment, out int badIndex))
                {
                    return ModFolderPathParse.Failure(ModFolderErrorCode.InvalidCharacter,
                        "Folder path '" + text + "': segment " + position + " ('" + raw.Trim() + "') holds "
                        + DescribeCharacter(raw, badIndex) + ", which is not allowed. A folder name may use letters, "
                        + "digits, spaces and " + DescribePunctuation() + ".");
                }

                if (segment.Length > MaxSegmentLength)
                {
                    return ModFolderPathParse.Failure(ModFolderErrorCode.SegmentTooLong,
                        "Folder path '" + text + "': segment " + position + " is " + segment.Length
                        + " characters long; the limit is " + MaxSegmentLength + ".");
                }

                if (segment == "." || segment == "..")
                {
                    return ModFolderPathParse.Failure(ModFolderErrorCode.ReservedName,
                        "Folder path '" + text + "': segment " + position + " is '" + segment
                        + "', which reads as navigation, not as a folder name.");
                }

                if (path.Length > 0)
                {
                    path.Append(Separator);
                }

                path.Append(segment);
            }

            if (depth > MaxDepth)
            {
                return ModFolderPathParse.Failure(ModFolderErrorCode.TooDeep,
                    "Folder path '" + text + "' is " + depth + " folders deep; the limit is " + MaxDepth + ".");
            }

            if (path.Length > MaxLength)
            {
                return ModFolderPathParse.Failure(ModFolderErrorCode.PathTooLong,
                    "Folder path '" + text + "' is " + path.Length + " characters long; the limit is " + MaxLength
                    + ".");
            }

            return ModFolderPathParse.Success(new ModFolderPath(path.ToString()));
        }

        /// <summary>
        /// Strictly reads one folder name (a rename target): the rules of <see cref="Parse"/> for a single
        /// segment. A separator fails with <see cref="ModFolderErrorCode.InvalidName"/>, a blank name with
        /// <see cref="ModFolderErrorCode.EmptySegment"/>.
        /// </summary>
        public static ModFolderPathParse ParseName(string name)
        {
            if (name == null || IsBlank(name))
            {
                return ModFolderPathParse.Failure(ModFolderErrorCode.EmptySegment, "A folder name cannot be empty.");
            }

            if (name.IndexOf(Separator) >= 0 || name.IndexOf('\\') >= 0)
            {
                return ModFolderPathParse.Failure(ModFolderErrorCode.InvalidName,
                    "Folder name '" + name + "' holds a separator; a name is one folder. Use a move to change "
                    + "where a folder lives.");
            }

            return Parse(name);
        }

        /// <summary>
        /// Leniently reads a stored category and never fails: segments are split on <c>/</c> (or a
        /// backslash), trimmed, runs of whitespace become one space and empty segments are dropped, but
        /// characters, lengths and depth are kept as stored. A canonical text is returned as the same
        /// string, without allocating.
        /// </summary>
        public static ModFolderPath FromStored(string category)
        {
            if (string.IsNullOrEmpty(category))
            {
                return Root;
            }

            if (IsCanonical(category))
            {
                return new ModFolderPath(category);
            }

            List<string> rawSegments = SplitSegments(category);
            StringBuilder path = new(category.Length);
            for (int index = 0; index < rawSegments.Count; index++)
            {
                string segment = CollapseWhitespace(rawSegments[index]);
                if (segment.Length == 0)
                {
                    continue;
                }

                if (path.Length > 0)
                {
                    path.Append(Separator);
                }

                path.Append(segment);
            }

            return path.Length == 0 ? Root : new ModFolderPath(path.ToString());
        }

        /// <summary>
        /// Checks only the depth and the length of an already normalized path (characters are not checked):
        /// what a move must keep true for every folder it carries, legacy names included.
        /// </summary>
        public static ModFolderPathParse CheckLimits(ModFolderPath path)
        {
            int depth = path.Depth;
            if (depth > MaxDepth)
            {
                return ModFolderPathParse.Failure(ModFolderErrorCode.TooDeep,
                    "Folder path '" + path.Value + "' would be " + depth + " folders deep; the limit is " + MaxDepth
                    + ".");
            }

            if (path.Value.Length > MaxLength)
            {
                return ModFolderPathParse.Failure(ModFolderErrorCode.PathTooLong,
                    "Folder path '" + path.Value + "' would be " + path.Value.Length
                    + " characters long; the limit is " + MaxLength + ".");
            }

            return ModFolderPathParse.Success(path);
        }

        /// <summary>True when <paramref name="c"/> may appear in a segment under the strict rules (BMP only).</summary>
        public static bool IsAllowedCharacter(char c)
        {
            if (char.IsSurrogate(c))
            {
                return false;
            }

            return IsAllowedCategory(CharUnicodeInfo.GetUnicodeCategory(c)) || AllowedPunctuation.IndexOf(c) >= 0;
        }

        /// <inheritdoc />
        public bool Equals(ModFolderPath other)
        {
            return string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);
        }

        /// <inheritdoc />
        public override bool Equals(object obj)
        {
            return obj is ModFolderPath other && Equals(other);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            return Comparer.GetHashCode(Value);
        }

        /// <summary>The normalized path text.</summary>
        public override string ToString()
        {
            return Value;
        }

        /// <summary>Case-insensitive identity.</summary>
        public static bool operator ==(ModFolderPath left, ModFolderPath right)
        {
            return left.Equals(right);
        }

        /// <summary>Case-insensitive identity.</summary>
        public static bool operator !=(ModFolderPath left, ModFolderPath right)
        {
            return !left.Equals(right);
        }

        /// <summary>Wraps text that is already normalized (a path this type produced) without re-reading it.</summary>
        internal static ModFolderPath FromNormalized(string normalized)
        {
            return string.IsNullOrEmpty(normalized) ? Root : new ModFolderPath(normalized);
        }

        private static bool IsAllowedCategory(UnicodeCategory category)
        {
            switch (category)
            {
                case UnicodeCategory.UppercaseLetter:
                case UnicodeCategory.LowercaseLetter:
                case UnicodeCategory.TitlecaseLetter:
                case UnicodeCategory.ModifierLetter:
                case UnicodeCategory.OtherLetter:
                case UnicodeCategory.NonSpacingMark:
                case UnicodeCategory.SpacingCombiningMark:
                case UnicodeCategory.DecimalDigitNumber:
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsCanonical(string text)
        {
            int length = text.Length;
            for (int i = 0; i < length; i++)
            {
                char c = text[i];
                if (c == '\\')
                {
                    return false;
                }

                if (c == Separator)
                {
                    if (i == 0 || i == length - 1 || text[i - 1] == Separator || text[i - 1] == ' ' ||
                        text[i + 1] == ' ')
                    {
                        return false;
                    }

                    continue;
                }

                if (char.IsWhiteSpace(c))
                {
                    if (c != ' ' || i == 0 || i == length - 1 || text[i - 1] == ' ')
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static List<string> SplitSegments(string text)
        {
            List<string> segments = new();
            int start = 0;
            for (int i = 0; i <= text.Length; i++)
            {
                if (i == text.Length || text[i] == Separator || text[i] == '\\')
                {
                    segments.Add(text.Substring(start, i - start));
                    start = i + 1;
                }
            }

            return segments;
        }

        private static bool IsBlank(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                if (!char.IsWhiteSpace(text[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private static string CollapseWhitespace(string segment)
        {
            StringBuilder builder = new(segment.Length);
            bool pendingSpace = false;
            for (int i = 0; i < segment.Length; i++)
            {
                char c = segment[i];
                if (char.IsWhiteSpace(c))
                {
                    pendingSpace = builder.Length > 0;
                    continue;
                }

                if (pendingSpace)
                {
                    builder.Append(' ');
                    pendingSpace = false;
                }

                builder.Append(c);
            }

            return builder.ToString();
        }

        /// <summary>
        /// Trims a raw segment, turns inner runs of space separators into one space and checks every
        /// character. An inner control character (a tab, a newline) is refused rather than read as a space:
        /// a header value is one line.
        /// </summary>
        private static bool TryNormalizeSegment(string raw, out string segment, out int badIndex)
        {
            int start = 0;
            int end = raw.Length;
            while (start < end && char.IsWhiteSpace(raw[start]))
            {
                start++;
            }

            while (end > start && char.IsWhiteSpace(raw[end - 1]))
            {
                end--;
            }

            StringBuilder builder = new(end - start);
            bool pendingSpace = false;
            for (int i = start; i < end; i++)
            {
                char c = raw[i];
                if (char.IsHighSurrogate(c) && i + 1 < end && char.IsLowSurrogate(raw[i + 1]))
                {
                    if (!IsAllowedCategory(CharUnicodeInfo.GetUnicodeCategory(raw, i)))
                    {
                        segment = null;
                        badIndex = i;
                        return false;
                    }

                    AppendPendingSpace(builder, ref pendingSpace);
                    builder.Append(c).Append(raw[i + 1]);
                    i++;
                    continue;
                }

                if (!char.IsControl(c) && CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator)
                {
                    pendingSpace = builder.Length > 0;
                    continue;
                }

                if (!IsAllowedCharacter(c))
                {
                    segment = null;
                    badIndex = i;
                    return false;
                }

                AppendPendingSpace(builder, ref pendingSpace);
                builder.Append(c);
            }

            segment = builder.ToString();
            badIndex = -1;
            return true;
        }

        private static void AppendPendingSpace(StringBuilder builder, ref bool pendingSpace)
        {
            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }
        }

        private static string DescribeCharacter(string text, int index)
        {
            if (index < 0 || index >= text.Length)
            {
                return "a character";
            }

            int codePoint = char.IsHighSurrogate(text[index]) && index + 1 < text.Length &&
                            char.IsLowSurrogate(text[index + 1])
                ? char.ConvertToUtf32(text[index], text[index + 1])
                : text[index];
            string hex = "U+" + codePoint.ToString("X4", CultureInfo.InvariantCulture);
            return char.IsControl(text[index]) || char.IsSurrogate(text[index])
                ? hex
                : "'" + char.ConvertFromUtf32(codePoint) + "' (" + hex + ")";
        }

        private static string DescribePunctuation()
        {
            StringBuilder builder = new();
            for (int i = 1; i < AllowedPunctuation.Length; i++)
            {
                if (builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(AllowedPunctuation[i]);
            }

            return builder.ToString();
        }
    }

    /// <summary>The outcome of a strict <see cref="ModFolderPath"/> read.</summary>
    public readonly struct ModFolderPathParse
    {
        private ModFolderPathParse(ModFolderPath path, ModFolderErrorCode error, string message)
        {
            Path = path;
            Error = error;
            Message = message ?? "";
        }

        /// <summary>The normalized path; the top level on failure.</summary>
        public ModFolderPath Path { get; }

        /// <summary>The violated rule; <see cref="ModFolderErrorCode.None"/> on success.</summary>
        public ModFolderErrorCode Error { get; }

        /// <summary>A sentence naming the rule and the offending segment; empty on success.</summary>
        public string Message { get; }

        /// <summary>True when the text is a valid path.</summary>
        public bool Succeeded => Error == ModFolderErrorCode.None;

        internal static ModFolderPathParse Success(ModFolderPath path)
        {
            return new ModFolderPathParse(path, ModFolderErrorCode.None, "");
        }

        internal static ModFolderPathParse Failure(ModFolderErrorCode error, string message)
        {
            return new ModFolderPathParse(ModFolderPath.Root, error, message);
        }
    }
}
