using System.Collections.Generic;
using UnityEngine;
using UnityEngine.TextCore.Text;
using UnityEngine.UIElements;

namespace CoreAI.PanelFonts
{
    /// <summary>
    /// The font CoreAI's UI Toolkit panels fall back to for the letters their own font lacks: Liberation Sans from
    /// this package's Resources (Latin, Cyrillic, Greek). A panel given these text settings draws such text in every
    /// player, a WebGL page included.
    /// </summary>
    /// <remarks>
    /// WHY: a panel without text settings draws with Unity's built-in font and borrows missing letters from the
    /// system's fonts; a browser page has none to lend, so Russian text came out blank there.
    /// </remarks>
    public static class CoreAiPanelFonts
    {
        /// <summary>Resources path of the bundled font.</summary>
        public const string FontResourcePath = "CoreAI/Fonts/LiberationSans";

        private static Font _font;
        private static FontAsset _fontAsset;
        private static PanelTextSettings _textSettings;

        /// <summary>The bundled font; null when the package's Resources are stripped from the build.</summary>
        public static Font Font
        {
            get
            {
                if (_font == null)
                {
                    _font = Resources.Load<Font>(FontResourcePath);
                }

                return _font;
            }
        }

        /// <summary>The bundled font as a dynamic font asset, created once; null without the font.</summary>
        public static FontAsset FontAsset
        {
            get
            {
                if (_fontAsset == null)
                {
                    Font font = Font;
                    if (font == null)
                    {
                        return null;
                    }

                    _fontAsset = FontAsset.CreateFontAsset(font);
                    if (_fontAsset != null)
                    {
                        _fontAsset.name = "CoreAI Panel Font";
                        _fontAsset.hideFlags = HideFlags.HideAndDontSave;
                    }
                }

                return _fontAsset;
            }
        }

        /// <summary>
        /// Text settings whose default font and whose fallback for missing letters is the bundled font; shared by
        /// every panel that takes them. Null without the font.
        /// </summary>
        public static PanelTextSettings TextSettings
        {
            get
            {
                if (_textSettings == null)
                {
                    FontAsset fontAsset = FontAsset;
                    if (fontAsset == null)
                    {
                        return null;
                    }

                    _textSettings = ScriptableObject.CreateInstance<PanelTextSettings>();
                    _textSettings.name = "CoreAI Panel Text Settings";
                    _textSettings.hideFlags = HideFlags.HideAndDontSave;
                    _textSettings.defaultFontAsset = fontAsset;
                    _textSettings.fallbackFontAssets = new List<FontAsset> { fontAsset };
                }

                return _textSettings;
            }
        }

        /// <summary>
        /// Gives <paramref name="panel"/> the shared text settings. A panel that already has text settings keeps
        /// them: the game chose its own fonts. Returns true when the panel was changed.
        /// </summary>
        public static bool ApplyTo(PanelSettings panel)
        {
            if (panel == null || panel.textSettings != null)
            {
                return false;
            }

            PanelTextSettings settings = TextSettings;
            if (settings == null)
            {
                return false;
            }

            panel.textSettings = settings;
            return true;
        }
    }
}
