using System;
using System.Collections.Generic;
using System.Text;

namespace CoreAI.CodeEditing
{

    public sealed class PythonSyntaxHighlighter : ISyntaxHighlighter
    {

        private readonly CodeSyntaxPalette Palette;

        public PythonSyntaxHighlighter() : this(CodeSyntaxPalette.Load("python"))
        {
        }

        public PythonSyntaxHighlighter(CodeSyntaxPalette palette)
        {
            Palette = palette ?? CodeSyntaxPalette.Load("python");
        }

        private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
        {
            "and",
            "as",
            "assert",
            "async",
            "await",
            "break",
            "class",
            "continue",
            "def",
            "del",
            "elif",
            "else",
            "except",
            "finally",
            "for",
            "from",
            "global",
            "if",
            "import",
            "in",
            "is",
            "lambda",
            "nonlocal",
            "not",
            "or",
            "pass",
            "raise",
            "return",
            "try",
            "while",
            "with",
            "yield"
        };

        private static readonly HashSet<string> Constants = new(StringComparer.Ordinal) { "True", "False", "None" };

        private static readonly HashSet<string> Builtins = new(StringComparer.Ordinal)
        {
            "abs",
            "all",
            "any",
            "bin",
            "bool",
            "bytes",
            "chr",
            "dict",
            "dir",
            "divmod",
            "enumerate",
            "filter",
            "float",
            "format",
            "frozenset",
            "hex",
            "id",
            "input",
            "int",
            "isinstance",
            "iter",
            "len",
            "list",
            "map",
            "max",
            "min",
            "next",
            "object",
            "oct",
            "open",
            "ord",
            "pow",
            "print",
            "range",
            "repr",
            "reversed",
            "round",
            "set",
            "slice",
            "sorted",
            "str",
            "sum",
            "super",
            "tuple",
            "type",
            "zip"
        };

        public string[] Highlight(string[] dedentedLines)
        {
            string[] result = new string[dedentedLines.Length];

            string activeTriple = null;
            for (int i = 0; i < dedentedLines.Length; i++)
            {
                result[i] = HighlightLine(dedentedLines[i], ref activeTriple);
            }

            return result;
        }

        private string HighlightLine(string code, ref string activeTriple)
        {
            StringBuilder builder = new();
            int i = 0;

            if (activeTriple != null)
            {
                int close = code.IndexOf(activeTriple, StringComparison.Ordinal);
                if (close < 0)
                {
                    SyntaxHighlightText.AppendColored(builder, code, Palette.String);
                    return builder.ToString();
                }

                int end = close + activeTriple.Length;
                SyntaxHighlightText.AppendColored(builder, code[..end], Palette.String);
                activeTriple = null;
                i = end;
            }

            string prevToken = string.Empty;

            for (; i < code.Length;)
            {
                if (code[i] == '#')
                {
                    SyntaxHighlightText.AppendColored(builder, code[i..], Palette.Comment);
                    break;
                }

                if (IsStringStart(code, i))
                {
                    int start = i;
                    int quoteIndex = GetStringQuoteIndex(code, i);
                    bool tripleQuoted = IsTripleQuoted(code, quoteIndex);
                    char quote = code[quoteIndex];
                    i = quoteIndex + (tripleQuoted ? 3 : 1);

                    bool closed = false;
                    while (i < code.Length)
                    {
                        if (!tripleQuoted && code[i] == '\\' && i + 1 < code.Length)
                        {
                            i += 2;
                            continue;
                        }

                        if (tripleQuoted)
                        {
                            if (i + 2 < code.Length && code[i] == quote && code[i + 1] == quote && code[i + 2] == quote)
                            {
                                i += 3;
                                closed = true;
                                break;
                            }
                        }
                        else if (code[i] == quote)
                        {
                            i++;
                            closed = true;
                            break;
                        }

                        i++;
                    }

                    SyntaxHighlightText.AppendColored(builder, code[start..i], Palette.String);

                    if (tripleQuoted && !closed)
                    {
                        activeTriple = new string(quote, 3);
                    }

                    continue;
                }

                if (char.IsDigit(code[i]))
                {
                    int start = i++;
                    while (i < code.Length && (char.IsLetterOrDigit(code[i]) || code[i] == '.' || code[i] == '_'))
                    {
                        i++;
                    }

                    SyntaxHighlightText.AppendColored(builder, code[start..i], Palette.Number);
                    continue;
                }

                if (char.IsLetter(code[i]) || code[i] == '_')
                {
                    int start = i++;
                    while (i < code.Length && (char.IsLetterOrDigit(code[i]) || code[i] == '_'))
                    {
                        i++;
                    }

                    string token = code[start..i];

                    if (Keywords.Contains(token) || Constants.Contains(token))
                    {
                        SyntaxHighlightText.AppendColored(builder, token, Palette.Keyword);
                        prevToken = token;
                        continue;
                    }

                    if (token == "self" || token == "cls")
                    {
                        SyntaxHighlightText.AppendColored(builder, token, Palette.Variable);
                        prevToken = token;
                        continue;
                    }

                    if (prevToken == "class")
                    {
                        SyntaxHighlightText.AppendColored(builder, token, Palette.Type);
                        prevToken = token;
                        continue;
                    }

                    if (prevToken == "def")
                    {
                        SyntaxHighlightText.AppendColored(builder, token, Palette.Function);
                        prevToken = token;
                        continue;
                    }

                    int lookahead = i;
                    while (lookahead < code.Length && char.IsWhiteSpace(code[lookahead]) && code[lookahead] != '\n')
                    {
                        lookahead++;
                    }

                    bool isCall = lookahead < code.Length && code[lookahead] == '(';

                    if (Builtins.Contains(token) || isCall)
                    {
                        SyntaxHighlightText.AppendColored(builder, token, Palette.Function);
                        prevToken = token;
                        continue;
                    }

                    builder.Append(SyntaxHighlightText.Escape(token));
                    prevToken = token;
                    continue;
                }

                if (code[i] is '(' or ')' or '[' or ']' or '{' or '}')
                {
                    SyntaxHighlightText.AppendColored(builder, code[i].ToString(), Palette.Bracket);
                    i++;
                    continue;
                }

                builder.Append(SyntaxHighlightText.Escape(code[i].ToString()));
                i++;
            }

            return builder.ToString();
        }

        private static bool IsStringStart(string code, int index) => GetStringQuoteIndex(code, index) >= 0;

        private static int GetStringQuoteIndex(string code, int index)
        {
            if (index >= code.Length)
            {
                return -1;
            }

            char current = code[index];
            if (current == '"' || current == '\'')
            {
                return index;
            }

            if (!IsStringPrefix(current) || index + 1 >= code.Length)
            {
                return -1;
            }

            char next = code[index + 1];
            if (next == '"' || next == '\'')
            {
                return index + 1;
            }

            if (IsStringPrefix(next) && index + 2 < code.Length &&
                (code[index + 2] == '"' || code[index + 2] == '\''))
            {
                return index + 2;
            }

            return -1;
        }

        private static bool IsStringPrefix(char symbol)
        {
            return symbol == 'r' || symbol == 'R' ||
                   symbol == 'f' || symbol == 'F' ||
                   symbol == 'b' || symbol == 'B' ||
                   symbol == 'u' || symbol == 'U';
        }

        private static bool IsTripleQuoted(string code, int index)
        {
            char quote = code[index];
            return index + 2 < code.Length && code[index + 1] == quote && code[index + 2] == quote;
        }
    }
}
