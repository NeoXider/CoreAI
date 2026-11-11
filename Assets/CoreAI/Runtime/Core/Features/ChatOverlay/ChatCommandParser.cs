using System;
using System.Collections.Generic;
using System.Text;

namespace CoreAI.ChatOverlay
{
    /// <summary>What a submitted chat line is.</summary>
    public enum ChatInputKind
    {
        /// <summary>Nothing but whitespace.</summary>
        Empty = 0,

        /// <summary>Plain text for the model.</summary>
        Text = 1,

        /// <summary>A slash command, handled locally before anything reaches the model.</summary>
        Command = 2
    }

    /// <summary>
    /// Engine-free parsing of one submitted chat line. A line that starts with the command prefix is a command;
    /// a doubled prefix (<c>//text</c>) escapes it and sends <c>/text</c> to the model as plain text.
    /// </summary>
    public static class ChatCommandParser
    {
        /// <summary>The default command prefix.</summary>
        public const char DefaultPrefix = '/';

        /// <summary>
        /// Classifies <paramref name="text"/>. For <see cref="ChatInputKind.Command"/> the parsed command is in
        /// <paramref name="invocation"/>; for <see cref="ChatInputKind.Text"/> the text to send is in
        /// <paramref name="modelText"/> (trimmed, with an escaping prefix removed).
        /// </summary>
        public static ChatInputKind Classify(string text, char prefix, out ChatCommandInvocation invocation,
            out string modelText)
        {
            invocation = default;
            modelText = "";
            if (string.IsNullOrWhiteSpace(text))
            {
                return ChatInputKind.Empty;
            }

            string trimmed = text.Trim();
            if (trimmed[0] != prefix)
            {
                modelText = trimmed;
                return ChatInputKind.Text;
            }

            if (trimmed.Length > 1 && trimmed[1] == prefix)
            {
                modelText = trimmed.Substring(1);
                return ChatInputKind.Text;
            }

            invocation = ParseInvocation(trimmed);
            return ChatInputKind.Command;
        }

        /// <summary>True when <paramref name="text"/> would be handled as a command.</summary>
        public static bool IsCommand(string text, char prefix = DefaultPrefix)
        {
            return Classify(text, prefix, out _, out _) == ChatInputKind.Command;
        }

        /// <summary>
        /// Parses a trimmed line whose first character is the prefix: the name runs to the first whitespace and
        /// the rest is the argument text.
        /// </summary>
        public static ChatCommandInvocation ParseInvocation(string trimmedCommandLine)
        {
            string line = trimmedCommandLine ?? "";
            if (line.Length == 0)
            {
                return new ChatCommandInvocation("", "", Array.Empty<string>(), "");
            }

            int nameEnd = 1;
            while (nameEnd < line.Length && !char.IsWhiteSpace(line[nameEnd]))
            {
                nameEnd++;
            }

            string name = line.Substring(1, nameEnd - 1);
            string argumentText = nameEnd < line.Length ? line.Substring(nameEnd).Trim() : "";
            return new ChatCommandInvocation(name, argumentText, Tokenize(argumentText), line);
        }

        /// <summary>
        /// Splits <paramref name="argumentText"/> on whitespace. A double-quoted run is one argument without its
        /// quotes; an unterminated quote runs to the end of the text.
        /// </summary>
        public static IReadOnlyList<string> Tokenize(string argumentText)
        {
            if (string.IsNullOrWhiteSpace(argumentText))
            {
                return Array.Empty<string>();
            }

            List<string> tokens = new();
            StringBuilder current = new();
            bool inQuotes = false;
            bool hasToken = false;
            foreach (char c in argumentText)
            {
                if (c == '"')
                {
                    inQuotes = !inQuotes;
                    hasToken = true;
                    continue;
                }

                if (!inQuotes && char.IsWhiteSpace(c))
                {
                    if (hasToken)
                    {
                        tokens.Add(current.ToString());
                        current.Clear();
                        hasToken = false;
                    }

                    continue;
                }

                current.Append(c);
                hasToken = true;
            }

            if (hasToken)
            {
                tokens.Add(current.ToString());
            }

            return tokens;
        }
    }
}
