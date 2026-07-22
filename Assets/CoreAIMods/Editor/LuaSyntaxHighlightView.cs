using System.Collections.Generic;
using CoreAI.LuaAssets;
using UnityEditor;
using UnityEngine;

namespace CoreAI.Editor
{
    /// <summary>
    /// Shared "highlighted code view" drawer used by both <see cref="LuaTextAssetEditor"/> and
    /// <see cref="LuaScriptViewerWindow"/>: caps very large sources, tokenizes, formats to rich text, and
    /// draws it in a scrollable read-only area.
    /// </summary>
    /// <remarks>
    /// WHY: Unity's IMGUI text fields (<c>EditorGUILayout.SelectableLabel</c>/<c>TextArea</c>) never
    /// render rich text — they show tag characters literally, because caret placement needs a 1:1
    /// mapping between rendered glyphs and source characters. A rich-text <see cref="GUIStyle"/> Label is
    /// the only IMGUI control that actually colors the tags, so this view trades native text-drag
    /// selection for a "Copy" button (see <see cref="LuaScriptViewerWindow"/>) — the standard workaround
    /// used by most Unity code-preview tooling.
    /// </remarks>
    internal static class LuaSyntaxHighlightView
    {
        public const float DefaultFontSize = 12f;
        public const float MinFontSize = 8f;
        public const float MaxFontSize = 24f;

        private static GUIStyle _codeStyle;
        private static float _codeStyleFontSize = -1f;
        private static Font _monoFont;
        private static bool _monoFontResolved;

        public static Vector2 Draw(string source, Vector2 scroll, float fontSize, float viewHeight)
        {
            LuaSourceCap.Result capped = LuaSourceCap.Cap(source);
            if (capped.WasTruncated)
            {
                EditorGUILayout.HelpBox(
                    $"Showing the first {capped.Text.Length:N0} of {capped.OriginalLength:N0} characters. " +
                    "Open the file in an external editor to view it in full.",
                    MessageType.Info);
            }

            IReadOnlyList<LuaToken> tokens = LuaTokenizer.Tokenize(capped.Text);
            string richText = LuaRichTextFormatter.Format(capped.Text, tokens, LuaSyntaxPalette.GetColor);

            GUIStyle style = GetCodeStyle(fontSize);
            scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.Height(viewHeight));
            GUILayout.Label(richText, style, GUILayout.ExpandWidth(true));
            EditorGUILayout.EndScrollView();

            return scroll;
        }

        private static GUIStyle GetCodeStyle(float fontSize)
        {
            if (_codeStyle == null)
            {
                _codeStyle = new GUIStyle(EditorStyles.label)
                {
                    richText = true,
                    wordWrap = false,
                    font = GetMonoFont(),
                    alignment = TextAnchor.UpperLeft
                };
                _codeStyle.padding = new RectOffset(4, 4, 2, 2);
            }

            if (!Mathf.Approximately(_codeStyleFontSize, fontSize))
            {
                _codeStyle.fontSize = Mathf.RoundToInt(fontSize);
                _codeStyleFontSize = fontSize;
            }

            return _codeStyle;
        }

        private static Font GetMonoFont()
        {
            if (_monoFontResolved)
            {
                return _monoFont;
            }

            _monoFontResolved = true;
            // WHY: Unity ships no guaranteed cross-platform monospace editor font; ask the OS for a
            // common one and fall back to the default UI font if none of these are installed.
            _monoFont = Font.CreateDynamicFontFromOSFont(
                new[] { "Consolas", "Courier New", "Menlo", "DejaVu Sans Mono", "monospace" },
                (int)DefaultFontSize);
            return _monoFont;
        }
    }
}
