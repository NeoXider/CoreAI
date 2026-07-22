using System;
using System.Collections.Generic;
using System.Text;

namespace CoreAI.LuaAssets
{
    /// <summary>
    /// Renders tokenized Lua/Luau source as Unity rich text (<c>&lt;color=#RRGGBB&gt;</c> tags), the same
    /// tag syntax understood by both legacy IMGUI rich text and TextMeshPro. Pure C# so it can back an
    /// editor inspector today and a runtime in-game console later without duplicating this logic.
    /// </summary>
    public static class LuaRichTextFormatter
    {
        /// <summary>
        /// Builds the highlighted rich text. <paramref name="colorLookup"/> maps a token kind to a hex
        /// color (with or without a leading '#'); returning null/empty renders that kind unstyled.
        /// </summary>
        public static string Format(string source, IReadOnlyList<LuaToken> tokens, Func<LuaTokenKind, string> colorLookup)
        {
            if (string.IsNullOrEmpty(source) || tokens == null || tokens.Count == 0)
            {
                return Escape(source);
            }

            StringBuilder sb = new(source.Length + 64);
            for (int i = 0; i < tokens.Count; i++)
            {
                LuaToken token = tokens[i];
                string escaped = Escape(token.GetText(source));

                if (token.Kind == LuaTokenKind.Whitespace)
                {
                    sb.Append(escaped);
                    continue;
                }

                string color = colorLookup?.Invoke(token.Kind);
                if (string.IsNullOrEmpty(color))
                {
                    sb.Append(escaped);
                    continue;
                }

                sb.Append("<color=#").Append(color.TrimStart('#')).Append('>').Append(escaped).Append("</color>");
            }

            return sb.ToString();
        }

        /// <summary>
        /// Neutralizes '&lt;' so arbitrary code content (including a literal <c>&lt;color=red&gt;</c>
        /// string inside a Lua string/comment) can never be parsed as a rich-text tag, while remaining
        /// visually identical: Unity rich text has no HTML-entity decoding, so replacing '&lt;' with
        /// "&amp;lt;" would show the literal escape sequence on screen instead of decoding it. Inserting a
        /// zero-width space immediately after '&lt;' keeps the visible glyph but breaks the character run
        /// so it can never match a known tag name (b/i/size/color/...). '&gt;' and '&amp;' need no
        /// treatment: Unity's tag matcher only triggers on '&lt;', so a lone '&gt;' or '&amp;' is already
        /// harmless.
        /// </summary>
        public static string Escape(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text ?? string.Empty;
            }

            if (text.IndexOf('<') < 0)
            {
                return text;
            }

            StringBuilder sb = new(text.Length + 8);
            foreach (char c in text)
            {
                sb.Append(c);
                if (c == '<')
                {
                    sb.Append('\u200B');
                }
            }

            return sb.ToString();
        }
    }
}
