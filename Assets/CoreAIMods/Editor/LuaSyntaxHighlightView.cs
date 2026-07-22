using System;
using System.Collections.Generic;
using CoreAI.LuaAssets;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.Editor
{
    /// <summary>
    /// Shared UI Toolkit "highlighted code view" used by both <see cref="LuaTextAssetEditor"/> and
    /// <see cref="LuaScriptViewerWindow"/>: caps very large sources, tokenizes, formats to rich text, and
    /// shows it in a scrollable, selectable read-only <see cref="Label"/> (<c>enableRichText</c>), so the
    /// <c>&lt;color&gt;</c> tags render as colors and Ctrl/Cmd+C copies the selection.
    /// </summary>
    /// <remarks>
    /// WHY: tokenizing + formatting is pure but non-trivial — a full re-lex of up to 64 KiB. The rich
    /// text is recomputed only when the source string or the editor skin changes (see
    /// <see cref="SetSource"/> / the skin poll), never on every repaint, so scrolling and inspector
    /// redraws cost nothing beyond the Label's own layout. Font size only restyles the Label and never
    /// invalidates the formatted text.
    /// </remarks>
    internal sealed class LuaSyntaxHighlightView
    {
        public const float DefaultFontSize = 12f;
        public const float MinFontSize = 8f;
        public const float MaxFontSize = 24f;

        private const long SkinPollIntervalMs = 500L;

        private static Font _monoFont;
        private static bool _monoFontResolved;

        private readonly VisualElement _root;
        private readonly Label _truncationNotice;
        private readonly ScrollView _scroll;
        private readonly Label _codeLabel;

        private string _cachedSource;
        private bool _cachedProSkin;
        private bool _hasContent;

        public LuaSyntaxHighlightView(float fontSize = DefaultFontSize)
        {
            _root = new VisualElement();
            _root.style.flexGrow = 1f;

            _truncationNotice = new Label { enableRichText = false };
            _truncationNotice.style.display = DisplayStyle.None;
            _truncationNotice.style.whiteSpace = WhiteSpace.Normal;
            _truncationNotice.style.paddingLeft = 6f;
            _truncationNotice.style.paddingRight = 6f;
            _truncationNotice.style.paddingTop = 4f;
            _truncationNotice.style.paddingBottom = 4f;
            _root.Add(_truncationNotice);

            _scroll = new ScrollView(ScrollViewMode.VerticalAndHorizontal);
            _scroll.style.flexGrow = 1f;
            _root.Add(_scroll);

            _codeLabel = new Label { enableRichText = true };
            _codeLabel.selection.isSelectable = true;
            _codeLabel.focusable = true; // WHY: required so Ctrl/Cmd+C copies the active selection
            _codeLabel.style.whiteSpace = WhiteSpace.NoWrap;
            _codeLabel.style.paddingLeft = 4f;
            _codeLabel.style.paddingRight = 4f;
            _codeLabel.style.paddingTop = 2f;
            _codeLabel.style.paddingBottom = 2f;

            Font mono = GetMonoFont();
            if (mono != null)
            {
                _codeLabel.style.unityFont = new StyleFont(mono);
            }

            SetFontSize(fontSize);
            _scroll.Add(_codeLabel);

            // WHY: the editor skin can flip (Preferences) while the view is open; a cheap periodic
            // isProSkin compare re-formats only on the actual change, never a per-frame re-lex.
            _root.schedule.Execute(RefreshIfSkinChanged).Every(SkinPollIntervalMs);
        }

        /// <summary>Root element to add into an inspector or window tree.</summary>
        public VisualElement Root => _root;

        /// <summary>Restyles the code Label's font size without touching the formatted rich text.</summary>
        public void SetFontSize(float fontSize)
        {
            _codeLabel.style.fontSize = Mathf.RoundToInt(Mathf.Clamp(fontSize, MinFontSize, MaxFontSize));
        }

        /// <summary>
        /// Sets the source shown. Re-tokenizes/formats only when the source or the editor skin differs
        /// from the last call, so repeated calls with the same source are free.
        /// </summary>
        public void SetSource(string source)
        {
            bool proSkin = EditorGUIUtility.isProSkin;
            if (_hasContent &&
                string.Equals(_cachedSource, source, StringComparison.Ordinal) &&
                _cachedProSkin == proSkin)
            {
                return;
            }

            _cachedSource = source;
            _cachedProSkin = proSkin;
            _hasContent = true;
            Rebuild(source);
        }

        private void RefreshIfSkinChanged()
        {
            if (!_hasContent || _cachedProSkin == EditorGUIUtility.isProSkin)
            {
                return;
            }

            _cachedProSkin = EditorGUIUtility.isProSkin;
            Rebuild(_cachedSource);
        }

        private void Rebuild(string source)
        {
            LuaSourceCap.Result capped = LuaSourceCap.Cap(source);
            if (capped.WasTruncated)
            {
                _truncationNotice.text =
                    $"Showing the first {capped.Text.Length:N0} of {capped.OriginalLength:N0} characters. " +
                    "Open the file in an external editor to view it in full.";
                _truncationNotice.style.display = DisplayStyle.Flex;
            }
            else
            {
                _truncationNotice.style.display = DisplayStyle.None;
            }

            IReadOnlyList<LuaToken> tokens = LuaTokenizer.Tokenize(capped.Text);
            _codeLabel.text = LuaRichTextFormatter.Format(capped.Text, tokens, LuaSyntaxPalette.GetColor);
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
