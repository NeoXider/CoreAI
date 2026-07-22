using System;
using System.Collections.Generic;
using System.Globalization;

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
        readonly string _s;
        int _p;
        int _line;
        int _lineStart;

        public LuauLexer(string source)
        {
            _s = source ?? "";
            _p = 0;
            _line = 1;
            _lineStart = 0;
        }

        public List<LuauToken> LexAll()
        {
            var tokens = new List<LuauToken>();
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

        LuauToken Next()
        {
            SkipTrivia();
            int start = _p;
            int line = _line;
            int col = _p - _lineStart + 1;
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

                return Make(LuauTokenKind.Name, _s.Substring(start, _p - start), start, _p, line, col);
            }

            if (char.IsDigit(c) || (c == '.' && _p + 1 < _s.Length && char.IsDigit(_s[_p + 1])))
            {
                return LexNumber(start, line, col);
            }

            if (c == '"' || c == '\'')
            {
                LexQuotedString(c);
                return Make(LuauTokenKind.String, _s.Substring(start, _p - start), start, _p, line, col);
            }

            if (c == '[' && _p + 1 < _s.Length && (_s[_p + 1] == '[' || _s[_p + 1] == '='))
            {
                int level = LongBracketLevel(_p);
                if (level >= 0)
                {
                    SkipLongBracket(level, "long string");
                    return Make(LuauTokenKind.String, _s.Substring(start, _p - start), start, _p, line, col);
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

        static LuauToken Make(LuauTokenKind kind, string text, int start, int end, int line, int col)
        {
            return new LuauToken { Kind = kind, Text = text, Start = start, End = end, Line = line, Column = col };
        }

        void SkipTrivia()
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
        int LongBracketLevel(int pos)
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

        void SkipLongBracket(int level, string what)
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

        void LexQuotedString(char quote)
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
                if (c == '\n')
                {
                    throw new LuauDownlevelException("unfinished string", line, col);
                }

                if (c == '\\')
                {
                    _p++;
                    if (_p >= _s.Length)
                    {
                        throw new LuauDownlevelException("unfinished string", line, col);
                    }

                    if (_s[_p] == '\n')
                    {
                        _line++;
                        _lineStart = _p + 1;
                    }

                    _p++;
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

        LuauToken LexNumber(int start, int line, int col)
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

                    while (_p < _s.Length && char.IsDigit(_s[_p]))
                    {
                        _p++;
                    }
                }
            }

            string text = _s.Substring(start, _p - start);
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

        LuauToken LexInterpString(int start, int line, int col)
        {
            var parts = new List<LuauInterpPart>();
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
                    _p++;
                    if (_p >= _s.Length)
                    {
                        throw new LuauDownlevelException("unfinished interpolated string", line, col);
                    }

                    _p++;
                }
                else if (c == '`')
                {
                    parts.Add(new LuauInterpPart { IsExpr = false, Start = textStart, End = _p });
                    _p++;
                    var tok = Make(LuauTokenKind.InterpString, _s.Substring(start, _p - start), start, _p, line, col);
                    tok.InterpParts = parts;
                    return tok;
                }
                else if (c == '{')
                {
                    parts.Add(new LuauInterpPart { IsExpr = false, Start = textStart, End = _p });
                    _p++;
                    int exprStart = _p;
                    var exprTokens = new List<LuauToken>();
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
                                parts.Add(new LuauInterpPart { IsExpr = true, Start = exprStart, End = t.Start, Tokens = exprTokens });
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

        string MatchPunct()
        {
            char c = _s[_p];
            char c1 = _p + 1 < _s.Length ? _s[_p + 1] : '\0';
            char c2 = _p + 2 < _s.Length ? _s[_p + 2] : '\0';
            switch (c)
            {
                case '.':
                    if (c1 == '.' && c2 == '.') return "...";
                    if (c1 == '.' && c2 == '=') return "..=";
                    if (c1 == '.') return "..";
                    return ".";
                case '/':
                    if (c1 == '/' && c2 == '=') return "//=";
                    if (c1 == '/') return "//";
                    if (c1 == '=') return "/=";
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
    }
}
