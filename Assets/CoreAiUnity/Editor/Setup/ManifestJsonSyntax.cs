namespace CoreAI.Editor
{
    /// <summary>
    /// Strict JSON syntax check (RFC 8259) for edits of <c>Packages/manifest.json</c>, written without Newtonsoft:
    /// the setup assembly must compile in a project where Newtonsoft is missing, since that is one of the faults
    /// its menus repair. Only syntax is checked; the root must be an object, as in every package manifest.
    /// </summary>
    internal static class ManifestJsonSyntax
    {
        private const int MaxDepth = 256;

        /// <summary>True when <paramref name="text"/> is one JSON object and nothing else but whitespace.</summary>
        internal static bool IsObject(string text)
        {
            if (text == null)
            {
                return false;
            }

            int index = 0;
            SkipWhitespace(text, ref index);
            if (index >= text.Length || text[index] != '{' || !TryValue(text, ref index, 0))
            {
                return false;
            }

            SkipWhitespace(text, ref index);
            return index == text.Length;
        }

        private static bool TryValue(string text, ref int index, int depth)
        {
            if (index >= text.Length || depth > MaxDepth)
            {
                return false;
            }

            switch (text[index])
            {
                case '{':
                    return TryObject(text, ref index, depth + 1);
                case '[':
                    return TryArray(text, ref index, depth + 1);
                case '"':
                    return TryString(text, ref index);
                case 't':
                    return TryLiteral(text, ref index, "true");
                case 'f':
                    return TryLiteral(text, ref index, "false");
                case 'n':
                    return TryLiteral(text, ref index, "null");
                default:
                    return TryNumber(text, ref index);
            }
        }

        private static bool TryObject(string text, ref int index, int depth)
        {
            index++;
            SkipWhitespace(text, ref index);
            if (index < text.Length && text[index] == '}')
            {
                index++;
                return true;
            }

            while (true)
            {
                SkipWhitespace(text, ref index);
                if (index >= text.Length || text[index] != '"' || !TryString(text, ref index))
                {
                    return false;
                }

                SkipWhitespace(text, ref index);
                if (index >= text.Length || text[index] != ':')
                {
                    return false;
                }

                index++;
                SkipWhitespace(text, ref index);
                if (!TryValue(text, ref index, depth))
                {
                    return false;
                }

                SkipWhitespace(text, ref index);
                if (index >= text.Length)
                {
                    return false;
                }

                char separator = text[index++];
                if (separator == '}')
                {
                    return true;
                }

                if (separator != ',')
                {
                    return false;
                }
            }
        }

        private static bool TryArray(string text, ref int index, int depth)
        {
            index++;
            SkipWhitespace(text, ref index);
            if (index < text.Length && text[index] == ']')
            {
                index++;
                return true;
            }

            while (true)
            {
                SkipWhitespace(text, ref index);
                if (!TryValue(text, ref index, depth))
                {
                    return false;
                }

                SkipWhitespace(text, ref index);
                if (index >= text.Length)
                {
                    return false;
                }

                char separator = text[index++];
                if (separator == ']')
                {
                    return true;
                }

                if (separator != ',')
                {
                    return false;
                }
            }
        }

        private static bool TryString(string text, ref int index)
        {
            index++;
            while (index < text.Length)
            {
                char c = text[index++];
                if (c == '"')
                {
                    return true;
                }

                if (c < ' ')
                {
                    return false;
                }

                if (c != '\\')
                {
                    continue;
                }

                if (index >= text.Length)
                {
                    return false;
                }

                char escape = text[index++];
                if (escape == 'u')
                {
                    for (int digit = 0; digit < 4; digit++)
                    {
                        if (index >= text.Length || !IsHexDigit(text[index]))
                        {
                            return false;
                        }

                        index++;
                    }
                }
                else if ("\"\\/bfnrt".IndexOf(escape) < 0)
                {
                    return false;
                }
            }

            return false;
        }

        private static bool TryNumber(string text, ref int index)
        {
            if (index < text.Length && text[index] == '-')
            {
                index++;
            }

            if (index >= text.Length)
            {
                return false;
            }

            if (text[index] == '0')
            {
                index++;
            }
            else if (text[index] >= '1' && text[index] <= '9')
            {
                SkipDigits(text, ref index);
            }
            else
            {
                return false;
            }

            if (index < text.Length && text[index] == '.')
            {
                index++;
                if (!StartsWithDigit(text, index))
                {
                    return false;
                }

                SkipDigits(text, ref index);
            }

            if (index < text.Length && (text[index] == 'e' || text[index] == 'E'))
            {
                index++;
                if (index < text.Length && (text[index] == '+' || text[index] == '-'))
                {
                    index++;
                }

                if (!StartsWithDigit(text, index))
                {
                    return false;
                }

                SkipDigits(text, ref index);
            }

            return true;
        }

        private static bool TryLiteral(string text, ref int index, string literal)
        {
            if (index + literal.Length > text.Length ||
                string.CompareOrdinal(text, index, literal, 0, literal.Length) != 0)
            {
                return false;
            }

            index += literal.Length;
            return true;
        }

        private static void SkipWhitespace(string text, ref int index)
        {
            while (index < text.Length &&
                   (text[index] == ' ' || text[index] == '\t' || text[index] == '\n' || text[index] == '\r'))
            {
                index++;
            }
        }

        private static void SkipDigits(string text, ref int index)
        {
            while (StartsWithDigit(text, index))
            {
                index++;
            }
        }

        private static bool StartsWithDigit(string text, int index)
        {
            return index < text.Length && text[index] >= '0' && text[index] <= '9';
        }

        private static bool IsHexDigit(char c)
        {
            return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
        }
    }
}
