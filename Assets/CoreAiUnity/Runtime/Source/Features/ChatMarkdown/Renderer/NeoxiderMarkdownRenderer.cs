using System;
using UnityEngine;
using UnityEngine.Scripting;
using UnityEngine.UIElements;

namespace CoreAI.ChatMarkdown
{
    /// <summary>
    /// <see cref="ICoreAiMarkdownRenderer"/> backed by <c>com.neoxider.markdownrenderer</c> (Markdig + UI Toolkit):
    /// headings, emphasis, inline code, quotes, lists, links, rules and fenced code with Python/C# highlighting.
    /// Registered automatically while the package is installed; a host re-registers a configured instance to change
    /// colours, or registers null to switch Markdown off.
    /// </summary>
    /// <remarks>
    /// The package's default style sheet is never used: it is loaded through <c>AssetDatabase</c> (Editor only), so it
    /// would make the Editor and a player look different. <see cref="StyleSheet"/> is CoreAI's own chat sheet from
    /// <c>Resources</c>, which ships in players.
    /// </remarks>
    public sealed class NeoxiderMarkdownRenderer : ICoreAiMarkdownRenderer
    {
        /// <summary><c>Resources</c> path of the default chat style sheet.</summary>
        public const string StyleSheetResourcePath = "CoreAI/UI/CoreAiChatMarkdown";

        private StyleSheet _styleSheet;
        private bool _styleSheetResolved;

        /// <summary>
        /// Colour of inline code. It is written into the rich text, so USS cannot change it; the default suits the
        /// dark chat theme.
        /// </summary>
        public Color InlineCodeColor { get; set; } = new Color32(0xE5, 0xC0, 0x7B, 0xFF);

        /// <summary>Inline code takes the text colour from USS instead of <see cref="InlineCodeColor"/>.</summary>
        public bool InlineCodeInheritsTextColor { get; set; }

        /// <summary>Links take the text colour from USS instead of the package's blue.</summary>
        public bool LinkInheritsTextColor { get; set; }

        /// <summary>
        /// Style sheet added to every view; defaults to <see cref="StyleSheetResourcePath"/>. Set another sheet to
        /// restyle every view at once, or null to use only your own USS.
        /// </summary>
        public StyleSheet StyleSheet
        {
            get
            {
                if (!_styleSheetResolved)
                {
                    _styleSheetResolved = true;
                    _styleSheet = Resources.Load<StyleSheet>(StyleSheetResourcePath);
                }

                return _styleSheet;
            }
            set
            {
                _styleSheet = value;
                _styleSheetResolved = true;
            }
        }

        /// <inheritdoc />
        public VisualElement Render(string markdown, Action<string> linkActivated)
        {
            global::UIMarkdownRenderer.UIMarkdownRenderer renderer = new(
                (target, _) => linkActivated?.Invoke(target),
                false,
                includeDefaultStylesheet: false)
            {
                InlineCodeColor = InlineCodeColor,
                InlineCodeInheritsTextColor = InlineCodeInheritsTextColor,
                LinkInheritsTextColor = LinkInheritsTextColor
            };
            renderer.SetMarkdown(markdown ?? "");
            return renderer.RootElement;
        }
    }

    /// <summary>
    /// Registers <see cref="NeoxiderMarkdownRenderer"/> when a player or a play session starts while the package is
    /// installed. Edit-mode UI and EditMode tests keep an empty registry unless they register a backend themselves, so
    /// installing the package never changes what an EditMode test sees.
    /// </summary>
    public static class NeoxiderMarkdownRendererRegistration
    {
        /// <summary>Puts a default <see cref="NeoxiderMarkdownRenderer"/> into the registry.</summary>
        [Preserve]
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        public static void Register()
        {
            CoreAiMarkdownRendererRegistry.Register(new NeoxiderMarkdownRenderer());
        }
    }
}
