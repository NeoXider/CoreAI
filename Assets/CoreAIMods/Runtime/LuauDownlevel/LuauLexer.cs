using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CoreAI.Infrastructure.Luau
{
    /// <summary>
    /// Internal control-flow exception for lexer/parser failures. Always caught inside
    /// <see cref="LuauDownleveler.Process(string)"/> and converted into an Error diagnostic —
    /// it never escapes the public API.
    /// </summary>
    internal sealed class LuauDownlevelException : Exception
    {
        public LuauDownlevelException(string message, int line, int column) : base(message)
        {
            Line = line;
            Column = column;
        }

        public int Line { get; }
        public int Column { get; }
    }

    /// <summary>
    /// Luau-aware tokenizer: quoted strings with escapes, long strings/comments <c>[[..]]</c> /
    /// <c>--[=[..]=]</c>, backtick interpolated strings (with recursively lexed brace expressions),
    /// Luau number literals (hex, binary, digit separators), and the Luau operator set including
    /// compound assignments, <c>//</c>, <c>::</c> and <c>-&gt;</c>.
    /// </summary>
    internal sealed class LuauLexer
    {
        private readonly string _s;
        private readonly DownlevelBudget _budget;
        private int _p;
        private int _line;
        private int _lineStart;
        private int _interpolationDepth;

        public LuauLexer(string source, DownlevelBudget budget)
        {
            _s = source ?? "";
            _budget = budget;
            _p = 0;
            _line = 1;
            _lineStart = 0;
        }

        public List<LuauToken> LexAll()
        {
            List<LuauToken> tokens = new();
            if (_s.Length > 1 && _s[0] == '#' && _s[1] == '!')
            {
                while (_p < _s.Length && _s[_p] != '\n')
                {
                    _p++;
                }
            }

            while (true)
            {
                LuauToken t = Next();
                tokens.Add(t);
                if (t.Kind == LuauTokenKind.Eof)
                {
                    return tokens;
                }
            }
        }

        private LuauToken Next()
        {
            SkipTrivia();
            int start = _p;
            int line = _line;
            int col = _p - _lineStart + 1;
            _budget.Spend(1, line, col);
            if (_p >= _s.Length)
            {
                return Make(LuauTokenKind.Eof, "<eof>", start, start, line, col);
            }

            char c = _s[_p];
            if (c == '_' || char.IsLetter(c))
            {
                while (_p < _s.Length && (_s[_p] == '_' || char.IsLetterOrDigit(_s[_p])))
                {
                    _p++;
                }

                return Make(LuauTokenKind.Name, ReadText(start, _p, line, col), start, _p, line, col);
            }

            if (char.IsDigit(c) || (c == '.' && _p + 1 < _s.Length && char.IsDigit(_s[_p + 1])))
            {
                return LexNumber(start, line, col);
            }

            if (c == '"' || c == '\'')
            {
                LexQuotedString(c);
                string text = ReadText(start, _p, line, col);
                LuauToken tok = Make(LuauTokenKind.String, text, start, _p, line, col);
                tok.StringRewrite = ConvertLuauStringEscapes(text, _budget);
                return tok;
            }

            if (c == '[' && _p + 1 < _s.Length && (_s[_p + 1] == '[' || _s[_p + 1] == '='))
            {
                int level = LongBracketLevel(_p);
                if (level >= 0)
                {
                    SkipLongBracket(level, "long string");
                    return Make(LuauTokenKind.String, ReadText(start, _p, line, col), start, _p, line, col);
                }
            }

            if (c == '`')
            {
                return LexInterpString(start, line, col);
            }

            string punct = MatchPunct();
            if (punct != null)
            {
                _p += punct.Length;
                return Make(LuauTokenKind.Punct, punct, start, _p, line, col);
            }

            throw new LuauDownlevelException($"unexpected character '{c}'", line, col);
        }

        private static LuauToken Make(LuauTokenKind kind, string text, int start, int end, int line, int col)
        {
            return new LuauToken { Kind = kind, Text = text, Start = start, End = end, Line = line, Column = col };
        }

        private string ReadText(int start, int end, int line, int column)
        {
            _budget.Spend(end - start, line, column);
            _budget.CheckTextLength(end - start, line, column);
            return _s.Substring(start, end - start);
        }

        private void SkipTrivia()
        {
            while (_p < _s.Length)
            {
                char c = _s[_p];
                if (c == '\n')
                {
                    _p++;
                    _line++;
                    _lineStart = _p;
                }
                else if (c == ' ' || c == '\t' || c == '\r' || c == '\f' || c == '\v')
                {
                    _p++;
                }
                else if (c == '-' && _p + 1 < _s.Length && _s[_p + 1] == '-')
                {
                    _p += 2;
                    int level = _p < _s.Length ? LongBracketLevel(_p) : -1;
                    if (level >= 0)
                    {
                        SkipLongBracket(level, "long comment");
                    }
                    else
                    {
                        while (_p < _s.Length && _s[_p] != '\n')
                        {
                            _p++;
                        }
                    }
                }
                else
                {
                    return;
                }
            }
        }

        /// <summary>Returns the '=' count of a long-bracket opener at <paramref name="pos"/>, or -1 when not one.</summary>
        private int LongBracketLevel(int pos)
        {
            if (pos >= _s.Length || _s[pos] != '[')
            {
                return -1;
            }

            int i = pos + 1;
            int level = 0;
            while (i < _s.Length && _s[i] == '=')
            {
                level++;
                i++;
            }

            return i < _s.Length && _s[i] == '[' ? level : -1;
        }

        private void SkipLongBracket(int level, string what)
        {
            int line = _line;
            int col = _p - _lineStart + 1;
            _p += level + 2;
            while (true)
            {
                if (_p >= _s.Length)
                {
                    throw new LuauDownlevelException($"unfinished {what}", line, col);
                }

                char c = _s[_p];
                if (c == '\n')
                {
                    _p++;
                    _line++;
                    _lineStart = _p;
                }
                else if (c == ']')
                {
                    int i = _p + 1;
                    int seen = 0;
                    while (i < _s.Length && _s[i] == '=')
                    {
                        seen++;
                        i++;
                    }

                    if (seen == level && i < _s.Length && _s[i] == ']')
                    {
                        _p = i + 1;
                        return;
                    }

                    _p++;
                }
                else
                {
                    _p++;
                }
            }
        }

        private void LexQuotedString(char quote)
        {
            int line = _line;
            int col = _p - _lineStart + 1;
            _p++;
            while (true)
            {
                if (_p >= _s.Length)
                {
                    throw new LuauDownlevelException("unfinished string", line, col);
                }

                char c = _s[_p];
                if (c == '\n' || c == '\r')
                {
                    throw new LuauDownlevelException("unfinished string", line, col);
                }

                if (c == '\\')
                {
                    SkipStringEscape(line, col);
                }
                else if (c == quote)
                {
                    _p++;
                    return;
                }
                else
                {
                    _p++;
                }
            }
        }

        private void SkipStringEscape(int stringLine, int stringColumn)
        {
            int start = _p;
            int line = _line;
            int column = _p - _lineStart + 1;
            _p++;
            if (_p >= _s.Length)
            {
                throw new LuauDownlevelException("unfinished string", stringLine, stringColumn);
            }

            if (_s[_p] == 'u')
            {
                ReadUnicodeEscape(_s, start, _s.Length, out int end, line, column);
                _p = end;
            }
            else if (_s[_p] == 'z')
            {
                _p++;
                while (_p < _s.Length && IsLuaWhitespace(_s[_p]))
                {
                    ConsumeEscapedCharacter();
                }
            }
            else
            {
                ConsumeEscapedCharacter();
            }
        }

        private void ConsumeEscapedCharacter()
        {
            char character = _s[_p++];
            if (character == '\r' && _p < _s.Length && _s[_p] == '\n')
            {
                _p++;
            }

            if (character == '\r' || character == '\n')
            {
                _line++;
                _lineStart = _p;
            }
        }

        private LuauToken LexNumber(int start, int line, int col)
        {
            bool sawUnderscore = false;
            bool isBinary = false;
            if (_s[_p] == '0' && _p + 1 < _s.Length && (_s[_p + 1] == 'x' || _s[_p + 1] == 'X'))
            {
                _p += 2;
                while (_p < _s.Length && (Uri.IsHexDigit(_s[_p]) || _s[_p] == '_'))
                {
                    sawUnderscore |= _s[_p] == '_';
                    _p++;
                }

                if (_p < _s.Length && _s[_p] == '.')
                {
                    _p++;
                    while (_p < _s.Length && (Uri.IsHexDigit(_s[_p]) || _s[_p] == '_'))
                    {
                        sawUnderscore |= _s[_p] == '_';
                        _p++;
                    }
                }

                if (_p < _s.Length && (_s[_p] == 'p' || _s[_p] == 'P'))
                {
                    _p++;
                    if (_p < _s.Length && (_s[_p] == '+' || _s[_p] == '-'))
                    {
                        _p++;
                    }

                    while (_p < _s.Length && char.IsDigit(_s[_p]))
                    {
                        _p++;
                    }
                }
            }
            else if (_s[_p] == '0' && _p + 1 < _s.Length && (_s[_p + 1] == 'b' || _s[_p + 1] == 'B'))
            {
                isBinary = true;
                _p += 2;
                while (_p < _s.Length && (_s[_p] == '0' || _s[_p] == '1' || _s[_p] == '_'))
                {
                    _p++;
                }
            }
            else
            {
                while (_p < _s.Length && (char.IsDigit(_s[_p]) || _s[_p] == '_'))
                {
                    sawUnderscore |= _s[_p] == '_';
                    _p++;
                }

                // WHY: a '.' followed by another '.' is the concat operator, not a decimal point.
                if (_p < _s.Length && _s[_p] == '.' && !(_p + 1 < _s.Length && _s[_p + 1] == '.'))
                {
                    _p++;
                    while (_p < _s.Length && (char.IsDigit(_s[_p]) || _s[_p] == '_'))
                    {
                        sawUnderscore |= _s[_p] == '_';
                        _p++;
                    }
                }

                if (_p < _s.Length && (_s[_p] == 'e' || _s[_p] == 'E'))
                {
                    _p++;
                    if (_p < _s.Length && (_s[_p] == '+' || _s[_p] == '-'))
                    {
                        _p++;
                    }

                    // WHY: Luau permits digit separators in the exponent too ('1e1_0'); consume and
                    // strip them like the mantissa's so the rewritten literal stays valid Lua 5.2.
                    while (_p < _s.Length && (char.IsDigit(_s[_p]) || _s[_p] == '_'))
                    {
                        sawUnderscore |= _s[_p] == '_';
                        _p++;
                    }
                }
            }

            string text = ReadText(start, _p, line, col);
            LuauToken tok = Make(LuauTokenKind.Number, text, start, _p, line, col);
            if (isBinary)
            {
                string bits = text.Substring(2).Replace("_", "");
                if (bits.Length == 0 || bits.Length > 64)
                {
                    throw new LuauDownlevelException($"malformed binary literal '{text}'", line, col);
                }

                ulong value = 0;
                for (int i = 0; i < bits.Length; i++)
                {
                    value = (value << 1) | (bits[i] == '1' ? 1UL : 0UL);
                }

                tok.NumberRewrite = value.ToString(CultureInfo.InvariantCulture);
            }
            else if (sawUnderscore)
            {
                tok.NumberRewrite = text.Replace("_", "");
            }

            return tok;
        }

        // WHY: an interpolated string lexes the expressions inside its braces through Next, which
        // re-enters here for a string nested inside them; unbounded, deeply nested backtick strings
        // overflowed the .NET stack of any caller, plain Lua included (this lexer runs on every chunk).
        private LuauToken LexInterpString(int start, int line, int col)
        {
            if (_interpolationDepth >= LuauDownleveler.MaxNestingDepth)
            {
                throw new LuauDownlevelException(LuauDownleveler.NestingDepthExceededMessage, line, col);
            }

            _interpolationDepth++;
            try
            {
                return LexInterpStringCore(start, line, col);
            }
            finally
            {
                _interpolationDepth--;
            }
        }

        private LuauToken LexInterpStringCore(int start, int line, int col)
        {
            List<LuauInterpPart> parts = new();
            _p++;
            int textStart = _p;
            while (true)
            {
                if (_p >= _s.Length)
                {
                    throw new LuauDownlevelException("unfinished interpolated string", line, col);
                }

                char c = _s[_p];
                if (c == '\n' || c == '\r')
                {
                    throw new LuauDownlevelException("interpolated strings cannot span multiple lines", line, col);
                }

                if (c == '\\')
                {
                    SkipStringEscape(line, col);
                }
                else if (c == '`')
                {
                    parts.Add(new LuauInterpPart { IsExpr = false, Start = textStart, End = _p });
                    _p++;
                    LuauToken tok = Make(LuauTokenKind.InterpString, ReadText(start, _p, line, col), start, _p, line,
                        col);
                    tok.InterpParts = parts;
                    return tok;
                }
                else if (c == '{')
                {
                    parts.Add(new LuauInterpPart { IsExpr = false, Start = textStart, End = _p });
                    _p++;
                    int exprStart = _p;
                    List<LuauToken> exprTokens = new();
                    int depth = 0;
                    while (true)
                    {
                        LuauToken t = Next();
                        if (t.Kind == LuauTokenKind.Eof)
                        {
                            throw new LuauDownlevelException("unfinished interpolated string expression", line, col);
                        }

                        if (t.Kind == LuauTokenKind.Punct && t.Text == "{")
                        {
                            depth++;
                        }
                        else if (t.Kind == LuauTokenKind.Punct && t.Text == "}")
                        {
                            if (depth == 0)
                            {
                                exprTokens.Add(Make(LuauTokenKind.Eof, "<eof>", t.Start, t.Start, t.Line, t.Column));
                                parts.Add(new LuauInterpPart
                                    { IsExpr = true, Start = exprStart, End = t.Start, Tokens = exprTokens });
                                textStart = t.End;
                                break;
                            }

                            depth--;
                        }

                        exprTokens.Add(t);
                    }
                }
                else
                {
                    _p++;
                }
            }
        }

        private string MatchPunct()
        {
            char c = _s[_p];
            char c1 = _p + 1 < _s.Length ? _s[_p + 1] : '\0';
            char c2 = _p + 2 < _s.Length ? _s[_p + 2] : '\0';
            switch (c)
            {
                case '.':
                    if (c1 == '.' && c2 == '.')
                    {
                        return "...";
                    }

                    if (c1 == '.' && c2 == '=')
                    {
                        return "..=";
                    }

                    if (c1 == '.')
                    {
                        return "..";
                    }

                    return ".";
                case '/':
                    if (c1 == '/' && c2 == '=')
                    {
                        return "//=";
                    }

                    if (c1 == '/')
                    {
                        return "//";
                    }

                    if (c1 == '=')
                    {
                        return "/=";
                    }

                    return "/";
                case '=': return c1 == '=' ? "==" : "=";
                case '~': return c1 == '=' ? "~=" : null;
                case '<': return c1 == '=' ? "<=" : "<";
                case '>': return c1 == '=' ? ">=" : ">";
                case '-': return c1 == '=' ? "-=" : c1 == '>' ? "->" : "-";
                case '+': return c1 == '=' ? "+=" : "+";
                case '*': return c1 == '=' ? "*=" : "*";
                case '%': return c1 == '=' ? "%=" : "%";
                case '^': return c1 == '=' ? "^=" : "^";
                case ':': return c1 == ':' ? "::" : ":";
                case '#': return "#";
                case '(': return "(";
                case ')': return ")";
                case '{': return "{";
                case '}': return "}";
                case '[': return "[";
                case ']': return "]";
                case ';': return ";";
                case ',': return ",";
                case '?': return "?";
                case '|': return "|";
                case '&': return "&";
                case '@': return "@";
                default: return null;
            }
        }

        /// <summary>
        /// Rewrites the Luau-only escapes a quoted string may carry into Lua 5.2 spellings:
        /// <c>\u{XXXX}</c> becomes the code point's UTF-8 bytes as zero-padded <c>\ddd</c> decimal
        /// escapes and <c>\z</c> (plus the whitespace it swallows) is removed. Returns the full
        /// rewritten literal (quotes included) or null when nothing had to change. Unicode escapes
        /// are validated during lexing before this conversion.
        /// </summary>
        internal static string ConvertLuauStringEscapes(string text, DownlevelBudget budget)
        {
            budget.Spend(text.Length);
            if (text.IndexOf('\\') < 0)
            {
                return null;
            }

            StringBuilder sb = null;
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (c != '\\' || i + 1 >= text.Length)
                {
                    if (sb != null)
                    {
                        budget.CheckTextLength(sb.Length + 1L);
                    }

                    sb?.Append(c);
                    i++;
                    continue;
                }

                char n = text[i + 1];
                if (n == 'z')
                {
                    // WHY: Luau '\z' drops the escape and every following whitespace char.
                    sb ??= new StringBuilder(text.Length).Append(text, 0, i);
                    i += 2;
                    while (i < text.Length && IsLuaWhitespace(text[i]))
                    {
                        i++;
                    }

                    continue;
                }

                if (n == 'u' && i + 2 < text.Length && text[i + 2] == '{')
                {
                    int cp = ReadUnicodeEscape(text, i, text.Length, out int end, 1, 1);
                    int bytes = cp <= 0x7F ? 1 : cp <= 0x7FF ? 2 : cp <= 0xFFFF ? 3 : 4;
                    budget.CheckTextLength((sb?.Length ?? i) + 4L * bytes);
                    sb ??= new StringBuilder(text.Length).Append(text, 0, i);
                    AppendUtf8DecimalEscapes(sb, cp);
                    i = end;
                    continue;
                }

                if (sb != null)
                {
                    budget.CheckTextLength(sb.Length + 2L);
                    sb.Append(c).Append(n);
                }
                i += 2;
            }

            if (sb != null)
            {
                budget.Spend(sb.Length);
            }

            return sb?.ToString();
        }

        internal static int ReadUnicodeEscape(string text, int start, int limit, out int end, int line, int column)
        {
            int index = start + 2;
            if (index >= limit || text[index] != '{')
            {
                throw new LuauDownlevelException("malformed Unicode escape", line, column);
            }

            index++;
            int codePoint = 0;
            int digits = 0;
            while (index < limit && Uri.IsHexDigit(text[index]))
            {
                int digit = HexValue(text[index]);
                if (digits >= 16 || codePoint > (0x10FFFF - digit) / 16)
                {
                    throw new LuauDownlevelException("Unicode escape exceeds the supported code-point range", line,
                        column);
                }

                codePoint = codePoint * 16 + digit;
                digits++;
                index++;
            }

            if (digits == 0 || index >= limit || text[index] != '}')
            {
                throw new LuauDownlevelException("malformed Unicode escape", line, column);
            }

            end = index + 1;
            return codePoint;
        }

        internal static bool IsLuaWhitespace(char c)
        {
            return c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f' || c == '\v';
        }

        private static int HexValue(char c)
        {
            if (c >= '0' && c <= '9')
            {
                return c - '0';
            }

            return (c >= 'a' ? c - 'a' : c - 'A') + 10;
        }

        private static void AppendUtf8DecimalEscapes(StringBuilder sb, int cp)
        {
            if (cp <= 0x7F)
            {
                AppendByte(sb, cp);
            }
            else if (cp <= 0x7FF)
            {
                AppendByte(sb, 0xC0 | (cp >> 6));
                AppendByte(sb, 0x80 | (cp & 0x3F));
            }
            else if (cp <= 0xFFFF)
            {
                AppendByte(sb, 0xE0 | (cp >> 12));
                AppendByte(sb, 0x80 | ((cp >> 6) & 0x3F));
                AppendByte(sb, 0x80 | (cp & 0x3F));
            }
            else
            {
                AppendByte(sb, 0xF0 | (cp >> 18));
                AppendByte(sb, 0x80 | ((cp >> 12) & 0x3F));
                AppendByte(sb, 0x80 | ((cp >> 6) & 0x3F));
                AppendByte(sb, 0x80 | (cp & 0x3F));
            }
        }

        private static void AppendByte(StringBuilder sb, int b)
        {
            sb.Append('\\').Append(b.ToString("D3", CultureInfo.InvariantCulture));
        }
    }
}
