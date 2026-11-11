using System;
using System.Collections.Generic;
using System.Text;

namespace CoreAI.ChatOverlay
{
    /// <summary>Kinds of inline pieces <see cref="ChatMarkdownInline"/> recognises inside one line.</summary>
    internal enum ChatMarkdownInlineKind
    {
        Text = 0,
        Escape = 1,
        CodeSpan = 2,
        DanglingTicks = 3,
        Delimiter = 4,
        Link = 5,
        Image = 6,
        DanglingLink = 7,
        Autolink = 8
    }

    /// <summary>One inline piece of a line: a slice of the source plus what it means.</summary>
    internal sealed class ChatMarkdownInlineToken
    {
        public ChatMarkdownInlineKind Kind { get; set; }

        public int Start { get; set; }

        public int Length { get; set; }

        /// <summary>Code span content, link text, image alt, autolink target or the escaped character.</summary>
        public string Inner { get; set; } = "";

        public char Delimiter { get; set; }

        public bool CanOpen { get; set; }

        public bool CanClose { get; set; }

        /// <summary>Delimiter characters left without a partner after pairing.</summary>
        public int Unmatched { get; set; }

        /// <summary>An opener that never met its closer.</summary>
        public bool Dangling { get; set; }
    }

    /// <summary>
    /// A small, line-local reading of CommonMark inlines: code spans, escapes, links, images, autolinks and
    /// emphasis delimiters paired with the flanking rules. It is not a Markdown parser; it only answers the two
    /// questions chat surfaces ask: "what are the words without the markers" and "which markers are still open
    /// because the answer is still streaming".
    /// </summary>
    internal static class ChatMarkdownInline
    {
        /// <summary>Splits <paramref name="text"/> into inline tokens and pairs its emphasis delimiters.</summary>
        public static List<ChatMarkdownInlineToken> Tokenize(string text)
        {
            List<ChatMarkdownInlineToken> tokens = new();
            if (string.IsNullOrEmpty(text))
            {
                return tokens;
            }

            int length = text.Length;
            int index = 0;
            int textStart = 0;
            while (index < length)
            {
                char current = text[index];
                if (current == '\\' && index + 1 < length && IsAsciiPunctuation(text[index + 1]))
                {
                    AddText(tokens, textStart, index);
                    tokens.Add(new ChatMarkdownInlineToken
                    {
                        Kind = ChatMarkdownInlineKind.Escape, Start = index, Length = 2,
                        Inner = text[index + 1].ToString()
                    });
                    index += 2;
                    textStart = index;
                    continue;
                }

                if (current == '`')
                {
                    int run = RunLength(text, index, '`');
                    int close = FindBacktickRun(text, index + run, run);
                    AddText(tokens, textStart, index);
                    if (close >= 0)
                    {
                        tokens.Add(new ChatMarkdownInlineToken
                        {
                            Kind = ChatMarkdownInlineKind.CodeSpan, Start = index, Length = close + run - index,
                            Inner = StripCodePadding(text.Substring(index + run, close - index - run))
                        });
                        index = close + run;
                    }
                    else
                    {
                        tokens.Add(new ChatMarkdownInlineToken
                        {
                            Kind = ChatMarkdownInlineKind.DanglingTicks, Start = index, Length = run
                        });
                        index += run;
                    }

                    textStart = index;
                    continue;
                }

                if ((current == '[' || (current == '!' && index + 1 < length && text[index + 1] == '[')) &&
                    TryParseLink(text, current == '!' ? index + 1 : index, out int linkEnd, out string label,
                        out bool dangling))
                {
                    AddText(tokens, textStart, index);
                    ChatMarkdownInlineKind kind = dangling
                        ? ChatMarkdownInlineKind.DanglingLink
                        : current == '!' ? ChatMarkdownInlineKind.Image : ChatMarkdownInlineKind.Link;
                    tokens.Add(new ChatMarkdownInlineToken
                    {
                        Kind = kind, Start = index, Length = linkEnd - index, Inner = label
                    });
                    index = linkEnd;
                    textStart = index;
                    continue;
                }

                if (current == '<')
                {
                    int autolinkEnd = MatchAutolink(text, index);
                    if (autolinkEnd > index)
                    {
                        AddText(tokens, textStart, index);
                        tokens.Add(new ChatMarkdownInlineToken
                        {
                            Kind = ChatMarkdownInlineKind.Autolink, Start = index, Length = autolinkEnd - index,
                            Inner = text.Substring(index + 1, autolinkEnd - index - 2)
                        });
                        index = autolinkEnd;
                        textStart = index;
                        continue;
                    }
                }

                if (current == '*' || current == '_' || current == '~')
                {
                    int run = RunLength(text, index, current);
                    if (current != '~' || run >= 2)
                    {
                        AddText(tokens, textStart, index);
                        tokens.Add(CreateDelimiter(text, index, run, current));
                        index += run;
                        textStart = index;
                        continue;
                    }

                    index += run;
                    continue;
                }

                index++;
            }

            AddText(tokens, textStart, length);
            PairDelimiters(tokens);
            return tokens;
        }

        /// <summary>The words of <paramref name="text"/> without inline Markdown markers.</summary>
        public static string ToPlain(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text ?? "";
            }

            List<ChatMarkdownInlineToken> tokens = Tokenize(text);
            StringBuilder builder = new(text.Length);
            foreach (ChatMarkdownInlineToken token in tokens)
            {
                switch (token.Kind)
                {
                    case ChatMarkdownInlineKind.Text:
                        builder.Append(text, token.Start, token.Length);
                        break;
                    case ChatMarkdownInlineKind.Escape:
                    case ChatMarkdownInlineKind.CodeSpan:
                    case ChatMarkdownInlineKind.Autolink:
                        builder.Append(token.Inner);
                        break;
                    case ChatMarkdownInlineKind.Link:
                    case ChatMarkdownInlineKind.Image:
                    case ChatMarkdownInlineKind.DanglingLink:
                        builder.Append(ToPlain(token.Inner));
                        break;
                    case ChatMarkdownInlineKind.Delimiter:
                        if (!token.Dangling && token.Unmatched > 0)
                        {
                            builder.Append(token.Delimiter, token.Unmatched);
                        }

                        break;
                }
            }

            return builder.ToString();
        }

        /// <summary>
        /// <paramref name="text"/> with the markers that are open only because the answer is still arriving taken
        /// out: an opener without its closer, a code span without its closing backticks, a link whose target is
        /// still being written, and a delimiter that is the last thing typed. Everything else stays verbatim.
        /// </summary>
        public static string HideDangling(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text ?? "";
            }

            List<ChatMarkdownInlineToken> tokens = Tokenize(text);
            StringBuilder builder = new(text.Length);
            foreach (ChatMarkdownInlineToken token in tokens)
            {
                switch (token.Kind)
                {
                    case ChatMarkdownInlineKind.DanglingTicks:
                        break;
                    case ChatMarkdownInlineKind.DanglingLink:
                        builder.Append(token.Inner);
                        break;
                    case ChatMarkdownInlineKind.Delimiter:
                        int keep = token.Length - (token.Dangling ? token.Unmatched : 0);
                        bool lastTyped = token.Start + token.Length == text.Length &&
                                         (token.Start == 0 || char.IsWhiteSpace(text[token.Start - 1]));
                        if (!lastTyped && keep > 0)
                        {
                            builder.Append(token.Delimiter, keep);
                        }

                        break;
                    default:
                        builder.Append(text, token.Start, token.Length);
                        break;
                }
            }

            return builder.ToString();
        }

        /// <summary>
        /// End index (exclusive) of a CommonMark autolink (<c>&lt;scheme:target&gt;</c> or
        /// <c>&lt;name@host&gt;</c>) starting at <paramref name="start"/>, or -1.
        /// </summary>
        public static int MatchAutolink(string text, int start)
        {
            int length = text.Length;
            if (start >= length || text[start] != '<')
            {
                return -1;
            }

            int index = start + 1;
            if (index < length && IsAsciiLetter(text[index]))
            {
                int schemeStart = index;
                index++;
                while (index < length && (IsAsciiLetterOrDigit(text[index]) || text[index] == '+' ||
                                          text[index] == '.' || text[index] == '-'))
                {
                    index++;
                }

                int schemeLength = index - schemeStart;
                if (schemeLength >= 2 && schemeLength <= 32 && index < length && text[index] == ':')
                {
                    index++;
                    while (index < length && text[index] != '>' && text[index] != '<' &&
                           !char.IsWhiteSpace(text[index]))
                    {
                        index++;
                    }

                    if (index < length && text[index] == '>')
                    {
                        return index + 1;
                    }
                }
            }

            index = start + 1;
            int at = -1;
            while (index < length && text[index] != '>' && text[index] != '<' && !char.IsWhiteSpace(text[index]))
            {
                char current = text[index];
                if (current == '@')
                {
                    at = index;
                }
                else if (!IsAsciiLetterOrDigit(current) && current != '.' && current != '-' && current != '_' &&
                         current != '+')
                {
                    return -1;
                }

                index++;
            }

            bool email = at > start + 1 && index < length && text[index] == '>' && index - at > 1;
            return email ? index + 1 : -1;
        }

        /// <summary>Number of consecutive <paramref name="marker"/> characters from <paramref name="start"/>.</summary>
        public static int RunLength(string text, int start, char marker)
        {
            int index = start;
            while (index < text.Length && text[index] == marker)
            {
                index++;
            }

            return index - start;
        }

        /// <summary>Start of the next run of exactly <paramref name="run"/> backticks at or after <paramref name="from"/>, or -1.</summary>
        public static int FindBacktickRun(string text, int from, int run)
        {
            int index = from < text.Length ? text.IndexOf('`', from) : -1;
            while (index >= 0)
            {
                int found = RunLength(text, index, '`');
                if (found == run)
                {
                    return index;
                }

                int next = index + found;
                index = next < text.Length ? text.IndexOf('`', next) : -1;
            }

            return -1;
        }

        public static bool IsAsciiPunctuation(char value)
        {
            return (value >= '!' && value <= '/') || (value >= ':' && value <= '@') ||
                   (value >= '[' && value <= '`') || (value >= '{' && value <= '~');
        }

        private static bool IsAsciiLetter(char value)
        {
            return (value >= 'a' && value <= 'z') || (value >= 'A' && value <= 'Z');
        }

        private static bool IsAsciiLetterOrDigit(char value)
        {
            return IsAsciiLetter(value) || (value >= '0' && value <= '9');
        }

        private static bool IsPunctuationOrSymbol(char value)
        {
            return char.IsPunctuation(value) || char.IsSymbol(value);
        }

        private static void AddText(List<ChatMarkdownInlineToken> tokens, int start, int end)
        {
            if (end > start)
            {
                tokens.Add(new ChatMarkdownInlineToken
                {
                    Kind = ChatMarkdownInlineKind.Text, Start = start, Length = end - start
                });
            }
        }

        private static string StripCodePadding(string content)
        {
            if (content.Length >= 2 && content[0] == ' ' && content[content.Length - 1] == ' ' &&
                content.Trim().Length > 0)
            {
                return content.Substring(1, content.Length - 2);
            }

            return content;
        }

        /// <summary>
        /// Reads <c>[label](target)</c>, <c>[label][reference]</c>, or a <c>[label](target</c> whose target is still
        /// being typed (no closing parenthesis and no whitespace after the opening one) as a link.
        /// </summary>
        private static bool TryParseLink(string text, int open, out int end, out string label, out bool dangling)
        {
            end = open;
            label = "";
            dangling = false;
            int length = text.Length;
            int depth = 0;
            int close = -1;
            for (int index = open; index < length; index++)
            {
                char current = text[index];
                if (current == '\\')
                {
                    index++;
                    continue;
                }

                if (current == '[')
                {
                    depth++;
                }
                else if (current == ']')
                {
                    depth--;
                    if (depth == 0)
                    {
                        close = index;
                        break;
                    }
                }
            }

            if (close < 0)
            {
                return false;
            }

            label = text.Substring(open + 1, close - open - 1);
            int after = close + 1;
            if (after < length && text[after] == '(')
            {
                int parenthesisDepth = 0;
                for (int index = after; index < length; index++)
                {
                    char current = text[index];
                    if (current == '\\')
                    {
                        index++;
                        continue;
                    }

                    if (current == '(')
                    {
                        parenthesisDepth++;
                    }
                    else if (current == ')')
                    {
                        parenthesisDepth--;
                        if (parenthesisDepth == 0)
                        {
                            end = index + 1;
                            return true;
                        }
                    }
                }

                for (int index = after + 1; index < length; index++)
                {
                    if (char.IsWhiteSpace(text[index]))
                    {
                        return false;
                    }
                }

                end = length;
                dangling = true;
                return true;
            }

            if (after < length && text[after] == '[')
            {
                int referenceClose = text.IndexOf(']', after + 1);
                if (referenceClose < 0)
                {
                    return false;
                }

                end = referenceClose + 1;
                return true;
            }

            return false;
        }

        private static ChatMarkdownInlineToken CreateDelimiter(string text, int start, int run, char marker)
        {
            char previous = start > 0 ? text[start - 1] : ' ';
            char next = start + run < text.Length ? text[start + run] : ' ';
            bool previousSpace = char.IsWhiteSpace(previous);
            bool nextSpace = char.IsWhiteSpace(next);
            bool previousPunctuation = IsPunctuationOrSymbol(previous);
            bool nextPunctuation = IsPunctuationOrSymbol(next);
            bool leftFlanking = !nextSpace && (!nextPunctuation || previousSpace || previousPunctuation);
            bool rightFlanking = !previousSpace && (!previousPunctuation || nextSpace || nextPunctuation);
            bool canOpen;
            bool canClose;
            if (marker == '_')
            {
                canOpen = leftFlanking && (!rightFlanking || previousPunctuation);
                canClose = rightFlanking && (!leftFlanking || nextPunctuation);
            }
            else
            {
                canOpen = leftFlanking;
                canClose = rightFlanking;
            }

            return new ChatMarkdownInlineToken
            {
                Kind = ChatMarkdownInlineKind.Delimiter, Start = start, Length = run, Delimiter = marker,
                CanOpen = canOpen, CanClose = canClose, Unmatched = run
            };
        }

        private static void PairDelimiters(List<ChatMarkdownInlineToken> tokens)
        {
            List<ChatMarkdownInlineToken> openers = new();
            foreach (ChatMarkdownInlineToken token in tokens)
            {
                if (token.Kind != ChatMarkdownInlineKind.Delimiter)
                {
                    continue;
                }

                while (token.CanClose && token.Unmatched > 0)
                {
                    int match = openers.Count - 1;
                    while (match >= 0 && openers[match].Delimiter != token.Delimiter)
                    {
                        match--;
                    }

                    if (match < 0)
                    {
                        break;
                    }

                    ChatMarkdownInlineToken opener = openers[match];
                    int take = token.Delimiter == '~' || (opener.Unmatched >= 2 && token.Unmatched >= 2) ? 2 : 1;
                    take = Math.Min(take, Math.Min(opener.Unmatched, token.Unmatched));
                    opener.Unmatched -= take;
                    token.Unmatched -= take;
                    openers.RemoveRange(match + 1, openers.Count - match - 1);
                    if (opener.Unmatched == 0)
                    {
                        openers.RemoveAt(match);
                    }
                }

                if (token.CanOpen && token.Unmatched > 0)
                {
                    openers.Add(token);
                }
            }

            foreach (ChatMarkdownInlineToken opener in openers)
            {
                opener.Dangling = true;
            }
        }
    }
}
