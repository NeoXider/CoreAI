using System.Collections.Generic;
using System.Runtime.CompilerServices;
using CoreAI.LuaAssets;

// WHY: CoreAI.Mods.Hub had no InternalsVisibleTo yet; the highlighter stays internal (it is a Hub UI
// detail, not public API) while remaining directly assertable from the EditMode test assembly.
[assembly: InternalsVisibleTo("CoreAI.Mods.Tests")]

namespace CoreAI.Ai.Hub
{
    /// <summary>
    /// Turns Lua mod source into UI Toolkit rich text for the Hub mod editor's highlight overlay:
    /// tokenizes via <see cref="LuaTokenizer"/> and wraps keywords, strings, comments and numbers in
    /// <c>&lt;color=#RRGGBB&gt;</c> tags tuned for the dark Hub theme. Any literal '&lt;' in the source
    /// is neutralized by <see cref="LuaRichTextFormatter.Escape"/> (a zero-width space is inserted after
    /// it) so user code can never inject a real rich-text tag. Pure and deterministic — a given source
    /// always yields the same markup — so EditMode tests assert on substrings of the output.
    /// </summary>
    internal static class LuaSyntaxHighlighter
    {
        // WHY: single-purpose dark palette — the Hub UI has no light skin, unlike the editor-side
        // LuaSyntaxPalette which switches on EditorGUIUtility.isProSkin and lives behind UnityEditor.
        private const string KeywordColor = "61AFEF";
        private const string StringColor = "98C379";
        private const string CommentColor = "8A929E";
        private const string NumberColor = "D19A66";
        private const string GlobalColor = "56B6C2";
        private const string FunctionCallColor = "E5C07B";

        /// <summary>Rich-text markup for <paramref name="source"/>; "" for null/empty input.</summary>
        public static string Highlight(string source)
        {
            if (string.IsNullOrEmpty(source))
            {
                return string.Empty;
            }

            List<LuaToken> tokens = LuaTokenizer.Tokenize(source);
            return LuaRichTextFormatter.Format(source, tokens, GetColor);
        }

        private static string GetColor(LuaTokenKind kind)
        {
            switch (kind)
            {
                case LuaTokenKind.Keyword: return KeywordColor;
                case LuaTokenKind.String:
                case LuaTokenKind.LongString:
                case LuaTokenKind.InterpolatedString: return StringColor;
                case LuaTokenKind.Comment: return CommentColor;
                case LuaTokenKind.Number: return NumberColor;
                case LuaTokenKind.Global: return GlobalColor;
                case LuaTokenKind.FunctionCall: return FunctionCallColor;
                // WHY: identifiers/operators keep the field's own text color — an all-colored wall of
                // text is harder to read than selective highlighting.
                default: return null;
            }
        }
    }
}
