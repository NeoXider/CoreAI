using System.Collections.Generic;

namespace CoreAI.Infrastructure.Luau
{
    internal enum LuauTokenKind
    {
        Eof,
        Name,
        Number,
        String,
        InterpString,
        Punct
    }

    /// <summary>
    /// One lexical token of Luau source. <see cref="Start"/>/<see cref="End"/> are absolute character
    /// offsets into the original source; rewrites are recorded against these spans so unedited text is
    /// emitted verbatim and line numbers survive.
    /// </summary>
    internal sealed class LuauToken
    {
        public LuauTokenKind Kind;
        public string Text;
        public int Start;
        public int End;
        public int Line;
        public int Column;

        /// <summary>Lua 5.2 spelling for Luau-only number literals (binary, digit separators); null when already valid.</summary>
        public string NumberRewrite;

        /// <summary>
        /// Lua 5.2 spelling for a quoted string carrying Luau-only escapes (<c>\u{XXXX}</c>, <c>\z</c>);
        /// includes the surrounding quotes and is null when the literal is already valid 5.2.
        /// </summary>
        public string StringRewrite;

        /// <summary>Segments of a backtick interpolated string; null for every other kind.</summary>
        public List<LuauInterpPart> InterpParts;
    }

    /// <summary>
    /// One segment of an interpolated string: either literal text or a brace expression. Expression
    /// segments carry their own pre-lexed token list (Eof-terminated) for recursive rewriting.
    /// </summary>
    internal sealed class LuauInterpPart
    {
        public bool IsExpr;
        public int Start;
        public int End;
        public List<LuauToken> Tokens;
    }
}
