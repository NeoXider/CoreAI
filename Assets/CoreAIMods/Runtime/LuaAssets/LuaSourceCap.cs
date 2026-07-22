namespace CoreAI.LuaAssets
{
    /// <summary>
    /// Truncates very large Lua/Luau source before tokenizing/rendering so an oversized mod file cannot
    /// stall the editor inspector (or a future in-game console) with megabytes of rich text.
    /// </summary>
    public static class LuaSourceCap
    {
        /// <summary>Default cap: 64 KiB of UTF-16 characters, comfortably beyond any hand-written mod.</summary>
        public const int DefaultMaxChars = 64 * 1024;

        public readonly struct Result
        {
            public readonly string Text;
            public readonly bool WasTruncated;
            public readonly int OriginalLength;

            public Result(string text, bool wasTruncated, int originalLength)
            {
                Text = text;
                WasTruncated = wasTruncated;
                OriginalLength = originalLength;
            }
        }

        public static Result Cap(string source, int maxChars = DefaultMaxChars)
        {
            if (string.IsNullOrEmpty(source))
            {
                return new Result(source ?? string.Empty, false, source?.Length ?? 0);
            }

            if (maxChars <= 0 || source.Length <= maxChars)
            {
                return new Result(source, false, source.Length);
            }

            // WHY: cut on a line boundary when one exists reasonably close to the limit, so a token
            // (string/long-comment/long-string) is not sliced mid-scan, which would otherwise leave an
            // unterminated token running to the end of the truncated text.
            int cut = source.LastIndexOf('\n', maxChars - 1);
            if (cut < maxChars / 2)
            {
                cut = maxChars;
            }

            return new Result(source.Substring(0, cut), true, source.Length);
        }
    }
}
