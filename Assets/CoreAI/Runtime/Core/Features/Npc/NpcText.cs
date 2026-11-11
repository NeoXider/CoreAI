using System;
using System.Collections.Generic;
using System.Text;

namespace CoreAI.Ai.Npc
{
    /// <summary>Text rules of the NPC block: name matching in chat lines and cleaning a model reply into a spoken line.</summary>
    public static class NpcText
    {
        /// <summary>
        /// Index of the first whole-word, case-insensitive mention of <paramref name="name"/> in
        /// <paramref name="text"/>, or -1. A mention is whole when the characters around it are not letters or
        /// digits, so "Bob" matches "bob, hi" and "@Bob" but not "Bobby".
        /// </summary>
        public static int IndexOfName(string text, string name)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(name))
            {
                return -1;
            }

            string needle = name.Trim();
            int start = 0;
            while (start <= text.Length - needle.Length)
            {
                int found = text.IndexOf(needle, start, StringComparison.OrdinalIgnoreCase);
                if (found < 0)
                {
                    return -1;
                }

                int end = found + needle.Length;
                bool startsWord = found == 0 || !char.IsLetterOrDigit(text[found - 1]);
                bool endsWord = end >= text.Length || !char.IsLetterOrDigit(text[end]);
                if (startsWord && endsWord)
                {
                    return found;
                }

                start = found + 1;
            }

            return -1;
        }

        /// <summary>The earliest whole-word mention of any of <paramref name="names"/>, or -1.</summary>
        public static int IndexOfAnyName(string text, IReadOnlyList<string> names)
        {
            int best = -1;
            if (names == null)
            {
                return best;
            }

            foreach (string name in names)
            {
                int index = IndexOfName(text, name);
                if (index >= 0 && (best < 0 || index < best))
                {
                    best = index;
                }
            }

            return best;
        }

        /// <summary>Cuts <paramref name="text"/> to <paramref name="maxCharacters"/> (0 or less keeps it), never splitting a surrogate pair.</summary>
        public static string Clamp(string text, int maxCharacters)
        {
            string value = text ?? "";
            if (maxCharacters <= 0 || value.Length <= maxCharacters)
            {
                return value;
            }

            int cut = maxCharacters;
            if (cut > 0 && char.IsHighSurrogate(value[cut - 1]))
            {
                cut--;
            }

            return value.Substring(0, cut);
        }

        /// <summary>
        /// Turns a raw model reply into one spoken line: drops a leading "<paramref name="npcName"/>:" the model
        /// sometimes echoes, outer quotes and stage directions in asterisks, folds line breaks and runs of spaces,
        /// and cuts at <paramref name="maxCharacters"/> on a word boundary with an ellipsis.
        /// </summary>
        public static string CleanReply(string raw, string npcName, int maxCharacters)
        {
            string text = (raw ?? "").Trim();
            text = StripSpeakerPrefix(text, npcName);
            text = StripStageDirections(text);
            text = CollapseWhitespace(text).Trim();
            text = StripOuterQuotes(text).Trim();
            if (maxCharacters > 0 && text.Length > maxCharacters)
            {
                int cut = text.LastIndexOf(' ', Math.Max(0, maxCharacters - 1));
                if (cut < maxCharacters / 2)
                {
                    cut = maxCharacters - 1;
                }

                text = Clamp(text, cut).TrimEnd() + "…";
            }

            return text;
        }

        private static string StripSpeakerPrefix(string text, string npcName)
        {
            string name = (npcName ?? "").Trim();
            if (name.Length == 0 || text.Length <= name.Length)
            {
                return text;
            }

            if (text.StartsWith(name, StringComparison.OrdinalIgnoreCase))
            {
                int index = name.Length;
                while (index < text.Length && text[index] == ' ')
                {
                    index++;
                }

                if (index < text.Length && text[index] == ':')
                {
                    return text.Substring(index + 1).TrimStart();
                }
            }

            return text;
        }

        private static string StripStageDirections(string text)
        {
            if (text.IndexOf('*') < 0)
            {
                return text;
            }

            StringBuilder sb = new(text.Length);
            int index = 0;
            while (index < text.Length)
            {
                char c = text[index];
                if (c == '*')
                {
                    int close = text.IndexOf('*', index + 1);
                    if (close > index)
                    {
                        index = close + 1;
                        continue;
                    }
                }

                sb.Append(c);
                index++;
            }

            string stripped = sb.ToString();
            return stripped.Trim().Length == 0 ? text.Replace("*", "") : stripped;
        }

        private static string CollapseWhitespace(string text)
        {
            StringBuilder sb = new(text.Length);
            bool lastWasSpace = false;
            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c))
                {
                    if (!lastWasSpace)
                    {
                        sb.Append(' ');
                    }

                    lastWasSpace = true;
                    continue;
                }

                sb.Append(c);
                lastWasSpace = false;
            }

            return sb.ToString();
        }

        private static string StripOuterQuotes(string text)
        {
            if (text.Length >= 2)
            {
                char first = text[0];
                char last = text[text.Length - 1];
                if ((first == '"' && last == '"') || (first == '“' && last == '”') ||
                    (first == '\'' && last == '\''))
                {
                    return text.Substring(1, text.Length - 2);
                }
            }

            return text;
        }
    }
}
