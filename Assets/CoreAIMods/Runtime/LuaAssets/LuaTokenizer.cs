using System;
using System.Collections.Generic;

namespace CoreAI.LuaAssets
{
    /// <summary>
    /// Hand-written single-pass lexer for Lua 5.x / Luau source. Pure C# with no engine or editor
    /// dependency so the same tokenizer backs the editor inspector/window today and can be reused by a
    /// future in-game console. Never throws on malformed input (unterminated strings/comments simply run
    /// to end-of-source) so it is safe to call on partially-written, AI-generated, or truncated mod code.
    /// </summary>
    public static class LuaTokenizer
    {
        private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
        {
            "and", "break", "do", "else", "elseif", "end", "false", "for", "function", "goto", "if",
            "in", "local", "nil", "not", "or", "repeat", "return", "then", "true", "until", "while",
            // WHY: "type"/"export" are Luau soft keywords (only reserved at statement start); this
            // best-effort lexer highlights them unconditionally, matching how most Luau editors do it.
            "continue", "type", "export"
        };

        // WHY: Roblox/Luau engine globals get a distinct highlight from ordinary identifiers so mod
        // authors can spot host-API access at a glance.
        private static readonly HashSet<string> Globals = new(StringComparer.Ordinal)
        {
            "game", "workspace", "script", "task", "shared", "_G", "Enum", "Instance"
        };

        // WHY: order is longest-prefix-first so e.g. "..." is matched before "..", and "..=" before "..".
        private static readonly string[] MultiCharOperators =
        {
            "...", "..=", "//=",
            "==", "~=", "<=", ">=", "::", "+=", "-=", "*=", "/=", "%=", "^=", "//", ".."
        };

        public static List<LuaToken> Tokenize(string source)
        {
            List<LuaToken> tokens = new();
            if (string.IsNullOrEmpty(source))
            {
                return tokens;
            }

            int length = source.Length;
            int pos = 0;
            while (pos < length)
            {
                char c = source[pos];

                if (IsWhitespace(c))
                {
                    int start = pos;
                    while (pos < length && IsWhitespace(source[pos]))
                    {
                        pos++;
                    }

                    tokens.Add(new LuaToken(LuaTokenKind.Whitespace, start, pos - start));
                    continue;
                }

                if (c == '-' && pos + 1 < length && source[pos + 1] == '-')
                {
                    int start = pos;
                    pos += 2;
                    if (TryMatchLongBracketOpen(source, pos, out int level))
                    {
                        pos = ScanLongBracket(source, pos, level);
                    }
                    else
                    {
                        while (pos < length && source[pos] != '\n')
                        {
                            pos++;
                        }
                    }

                    tokens.Add(new LuaToken(LuaTokenKind.Comment, start, pos - start));
                    continue;
                }

                if (c == '"' || c == '\'')
                {
                    int start = pos;
                    pos = ScanShortString(source, pos, c);
                    tokens.Add(new LuaToken(LuaTokenKind.String, start, pos - start));
                    continue;
                }

                if (c == '`')
                {
                    int start = pos;
                    pos = ScanInterpolatedString(source, pos);
                    tokens.Add(new LuaToken(LuaTokenKind.InterpolatedString, start, pos - start));
                    continue;
                }

                if (c == '[' && TryMatchLongBracketOpen(source, pos, out int longStringLevel))
                {
                    int start = pos;
                    pos = ScanLongBracket(source, pos, longStringLevel);
                    tokens.Add(new LuaToken(LuaTokenKind.LongString, start, pos - start));
                    continue;
                }

                if (char.IsDigit(c) || (c == '.' && pos + 1 < length && char.IsDigit(source[pos + 1])))
                {
                    int start = pos;
                    pos = ScanNumber(source, pos);
                    tokens.Add(new LuaToken(LuaTokenKind.Number, start, pos - start));
                    continue;
                }

                if (IsIdentStart(c))
                {
                    int start = pos;
                    pos = ScanIdentifier(source, pos);
                    LuaTokenKind kind = ClassifyWord(source, start, pos);
                    tokens.Add(new LuaToken(kind, start, pos - start));
                    continue;
                }

                if (c == ':')
                {
                    int start = pos;
                    pos++;
                    bool isTypeAnnotation = IsTypeAnnotationColon(tokens, source, pos);
                    tokens.Add(new LuaToken(
                        isTypeAnnotation ? LuaTokenKind.TypeAnnotation : LuaTokenKind.Operator, start, 1));
                    continue;
                }

                {
                    int start = pos;
                    pos = ScanOperatorOrPunctuation(source, pos);
                    tokens.Add(new LuaToken(LuaTokenKind.Operator, start, pos - start));
                }
            }

            return tokens;
        }

        private static LuaTokenKind ClassifyWord(string source, int start, int end)
        {
            int length = end - start;

            if (MatchesAny(source, start, length, Keywords))
            {
                return LuaTokenKind.Keyword;
            }

            if (MatchesAny(source, start, length, Globals))
            {
                return LuaTokenKind.Global;
            }

            int p = end;
            while (p < source.Length && (source[p] == ' ' || source[p] == '\t'))
            {
                p++;
            }

            return p < source.Length && source[p] == '(' ? LuaTokenKind.FunctionCall : LuaTokenKind.Identifier;
        }

        private static bool MatchesAny(string source, int start, int length, HashSet<string> set)
        {
            // WHY: avoid an intermediate Substring allocation per identifier on the hot path; HashSet
            // lookup needs a string, so this only allocates when the length matches a candidate.
            foreach (string candidate in set)
            {
                if (candidate.Length == length && string.CompareOrdinal(source, start, candidate, 0, length) == 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Best-effort classification of a ':' as a Luau type annotation (<c>local x: number</c>,
        /// <c>function foo(): string</c>) versus a method-call colon (<c>obj:method()</c>). Not a full
        /// parser: relies on two cheap heuristics — the colon following a ')' is treated as a return-type
        /// annotation, and a colon followed by "identifier(" is treated as a method call.
        /// </summary>
        private static bool IsTypeAnnotationColon(List<LuaToken> tokens, string source, int posAfterColon)
        {
            for (int i = tokens.Count - 1; i >= 0; i--)
            {
                LuaTokenKind kind = tokens[i].Kind;
                if (kind == LuaTokenKind.Whitespace || kind == LuaTokenKind.Comment)
                {
                    continue;
                }

                if (kind == LuaTokenKind.Operator && tokens[i].Length == 1 && source[tokens[i].Start] == ')')
                {
                    return true;
                }

                break;
            }

            int p = posAfterColon;
            while (p < source.Length && (source[p] == ' ' || source[p] == '\t'))
            {
                p++;
            }

            int identStart = p;
            while (p < source.Length && IsIdentPart(source[p]))
            {
                p++;
            }

            if (p > identStart)
            {
                int q = p;
                while (q < source.Length && (source[q] == ' ' || source[q] == '\t'))
                {
                    q++;
                }

                if (q < source.Length && source[q] == '(')
                {
                    return false;
                }
            }

            return true;
        }

        private static int ScanShortString(string source, int pos, char quote)
        {
            int length = source.Length;
            pos++;
            while (pos < length)
            {
                char c = source[pos];
                if (c == '\\' && pos + 1 < length)
                {
                    pos += 2;
                    continue;
                }

                if (c == quote)
                {
                    pos++;
                    break;
                }

                if (c == '\n')
                {
                    break;
                }

                pos++;
            }

            return pos;
        }

        private static int ScanInterpolatedString(string source, int pos)
        {
            int length = source.Length;
            pos++;
            int braceDepth = 0;
            while (pos < length)
            {
                char c = source[pos];
                if (c == '\\' && pos + 1 < length)
                {
                    pos += 2;
                    continue;
                }

                if (c == '{')
                {
                    braceDepth++;
                    pos++;
                    continue;
                }

                if (c == '}' && braceDepth > 0)
                {
                    braceDepth--;
                    pos++;
                    continue;
                }

                if (c == '`' && braceDepth == 0)
                {
                    pos++;
                    break;
                }

                pos++;
            }

            return pos;
        }

        private static bool TryMatchLongBracketOpen(string source, int pos, out int level)
        {
            level = 0;
            int length = source.Length;
            if (pos >= length || source[pos] != '[')
            {
                return false;
            }

            int p = pos + 1;
            int eq = 0;
            while (p < length && source[p] == '=')
            {
                eq++;
                p++;
            }

            if (p < length && source[p] == '[')
            {
                level = eq;
                return true;
            }

            return false;
        }

        private static int ScanLongBracket(string source, int pos, int level)
        {
            int length = source.Length;
            pos += 2 + level; // skip "[" + '='*level + "["

            string closer = "]" + new string('=', level) + "]";
            int idx = source.IndexOf(closer, Math.Min(pos, length), StringComparison.Ordinal);
            return idx < 0 ? length : idx + closer.Length;
        }

        private static int ScanNumber(string source, int pos)
        {
            int length = source.Length;
            if (source[pos] == '0' && pos + 1 < length && (source[pos + 1] == 'x' || source[pos + 1] == 'X'))
            {
                pos += 2;
                while (pos < length && (IsHexDigit(source[pos]) || source[pos] == '_'))
                {
                    pos++;
                }

                if (pos < length && source[pos] == '.')
                {
                    pos++;
                    while (pos < length && (IsHexDigit(source[pos]) || source[pos] == '_'))
                    {
                        pos++;
                    }
                }

                if (pos < length && (source[pos] == 'p' || source[pos] == 'P'))
                {
                    pos++;
                    if (pos < length && (source[pos] == '+' || source[pos] == '-'))
                    {
                        pos++;
                    }

                    while (pos < length && char.IsDigit(source[pos]))
                    {
                        pos++;
                    }
                }

                return pos;
            }

            while (pos < length && (char.IsDigit(source[pos]) || source[pos] == '_'))
            {
                pos++;
            }

            if (pos < length && source[pos] == '.')
            {
                pos++;
                while (pos < length && (char.IsDigit(source[pos]) || source[pos] == '_'))
                {
                    pos++;
                }
            }

            if (pos < length && (source[pos] == 'e' || source[pos] == 'E'))
            {
                int save = pos;
                pos++;
                if (pos < length && (source[pos] == '+' || source[pos] == '-'))
                {
                    pos++;
                }

                if (pos < length && char.IsDigit(source[pos]))
                {
                    while (pos < length && char.IsDigit(source[pos]))
                    {
                        pos++;
                    }
                }
                else
                {
                    pos = save;
                }
            }

            return pos;
        }

        private static int ScanIdentifier(string source, int pos)
        {
            int length = source.Length;
            while (pos < length && IsIdentPart(source[pos]))
            {
                pos++;
            }

            return pos;
        }

        private static int ScanOperatorOrPunctuation(string source, int pos)
        {
            for (int i = 0; i < MultiCharOperators.Length; i++)
            {
                string op = MultiCharOperators[i];
                if (Matches(source, pos, op))
                {
                    return pos + op.Length;
                }
            }

            return pos + 1;
        }

        private static bool Matches(string source, int pos, string token)
        {
            if (pos + token.Length > source.Length)
            {
                return false;
            }

            for (int i = 0; i < token.Length; i++)
            {
                if (source[pos + i] != token[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsWhitespace(char c)
        {
            return c == ' ' || c == '\t' || c == '\r' || c == '\n';
        }

        private static bool IsIdentStart(char c)
        {
            return char.IsLetter(c) || c == '_';
        }

        private static bool IsIdentPart(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_';
        }

        private static bool IsHexDigit(char c)
        {
            return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
        }
    }
}
