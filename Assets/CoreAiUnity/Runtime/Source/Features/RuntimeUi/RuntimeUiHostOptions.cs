using UnityEngine.UIElements;

namespace CoreAI.RuntimeUi
{
    /// <summary>
    /// Configuration of the runtime UI host. The defaults give a fresh world working screens with no
    /// setup: the host creates its own panel on first use, with the theme stylesheet shipped in this
    /// package's Resources.
    /// </summary>
    public sealed class RuntimeUiHostOptions
    {
        /// <summary>Resources path of the theme stylesheet the auto-created panel uses.</summary>
        public const string DefaultThemeResourcePath = "CoreAiRuntimeUiTheme";

        /// <summary>False registers a host that refuses every call with <c>UI_DISABLED</c>.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Panel settings for the auto-created panel; null creates one at runtime.</summary>
        public PanelSettings PanelSettings { get; set; }

        /// <summary>Theme stylesheet for a runtime-created panel; null loads <see cref="DefaultThemeResourcePath"/>.</summary>
        public ThemeStyleSheet ThemeStyleSheet { get; set; }

        /// <summary>Sorting order of the auto-created panel against other panels.</summary>
        public float PanelSortingOrder { get; set; } = 50f;

        /// <summary>False keeps the host detached until <see cref="IRuntimeUiHost.AttachTo"/> is called.</summary>
        public bool AutoCreateSurface { get; set; } = true;

        /// <summary>Keeps the auto-created panel alive across scene loads (play mode only).</summary>
        public bool PersistAcrossScenes { get; set; } = true;

        /// <summary>Most screens the host holds.</summary>
        public int MaxScreens { get; set; } = 256;

        /// <summary>Most authored elements one screen holds (markup and API-created together).</summary>
        public int MaxElementsPerScreen { get; set; } = 4096;

        /// <summary>Parser limits; null uses <see cref="RuntimeUiParseLimits.Default"/>.</summary>
        public RuntimeUiParseLimits ParseLimits { get; set; }

        /// <summary>The theme; null creates <see cref="RuntimeUiTheme"/> with the default tokens and classes.</summary>
        public RuntimeUiTheme Theme { get; set; }

        /// <summary>Image lookup; null creates a <see cref="RuntimeUiAssetRegistry"/>.</summary>
        public IRuntimeUiAssetResolver Assets { get; set; }

        /// <summary>Template lookup; null creates an empty <see cref="UxmlTemplateRegistry"/>.</summary>
        public IUxmlTemplateResolver Templates { get; set; }
    }
}
