using CoreAI.PanelFonts;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TextCore.Text;
using UnityEngine.UIElements;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// The font CoreAI's panels fall back to. A WebGL page has no system fonts, so what a panel can draw there is
    /// exactly what this font holds: the Russian alphabet and the punctuation a model writes must be in it.
    /// </summary>
    public sealed class CoreAiPanelFontsEditModeTests
    {
        [Test]
        public void Font_IsBundled_AndHoldsTheRussianAlphabet()
        {
            FontAsset fontAsset = CoreAiPanelFonts.FontAsset;

            Assert.IsNotNull(CoreAiPanelFonts.Font, "the font is in the package's Resources");
            Assert.IsNotNull(fontAsset);
            for (char letter = 'А'; letter <= 'я'; letter++)
            {
                Assert.IsTrue(fontAsset.HasCharacter(letter, false, true), "U+" + ((int)letter).ToString("X4"));
            }

            Assert.IsTrue(fontAsset.HasCharacter('Ё', false, true), "capital yo");
            Assert.IsTrue(fontAsset.HasCharacter('ё', false, true), "small yo");
            Assert.IsTrue(fontAsset.HasCharacter('—', false, true), "the long dash");
            Assert.IsTrue(fontAsset.HasCharacter('«', false, true), "an opening angle quote");
        }

        [Test]
        public void TextSettings_FallBackToTheBundledFont()
        {
            PanelTextSettings settings = CoreAiPanelFonts.TextSettings;

            Assert.IsNotNull(settings);
            Assert.AreSame(CoreAiPanelFonts.FontAsset, settings.defaultFontAsset);
            CollectionAssert.Contains(settings.fallbackFontAssets, CoreAiPanelFonts.FontAsset);
            Assert.AreSame(settings, CoreAiPanelFonts.TextSettings, "one instance is shared by every panel");
        }

        [Test]
        public void ApplyTo_GivesAPanelTheTextSettings_AndKeepsAGamesOwn()
        {
            PanelSettings panel = ScriptableObject.CreateInstance<PanelSettings>();
            PanelSettings own = ScriptableObject.CreateInstance<PanelSettings>();
            PanelTextSettings gamesOwn = ScriptableObject.CreateInstance<PanelTextSettings>();
            try
            {
                own.textSettings = gamesOwn;

                Assert.IsTrue(CoreAiPanelFonts.ApplyTo(panel));
                Assert.AreSame(CoreAiPanelFonts.TextSettings, panel.textSettings);
                Assert.IsFalse(CoreAiPanelFonts.ApplyTo(panel), "a second call changes nothing");
                Assert.IsFalse(CoreAiPanelFonts.ApplyTo(own));
                Assert.AreSame(gamesOwn, own.textSettings);
                Assert.IsFalse(CoreAiPanelFonts.ApplyTo(null));
            }
            finally
            {
                Object.DestroyImmediate(panel);
                Object.DestroyImmediate(own);
                Object.DestroyImmediate(gamesOwn);
            }
        }
    }
}
