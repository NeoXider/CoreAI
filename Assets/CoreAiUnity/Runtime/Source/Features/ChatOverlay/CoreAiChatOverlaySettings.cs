using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.ChatOverlay
{
    /// <summary>
    /// Scope configuration of the in-world chat overlay: keys, fade, layout, panel and the engine-free
    /// <see cref="ChatOverlayOptions"/> (texts, speaker names, HUD formats). Edit it on a lifetime scope or in code
    /// before the scope builds. A host game that has its own chat sets <see cref="Enabled"/> to false.
    /// </summary>
    [Serializable]
    public sealed class CoreAiChatOverlaySettings
    {
        /// <summary>Resources path of the theme stylesheet tried when neither a panel nor a theme is assigned.</summary>
        public const string DefaultThemeResourcePath = "CoreAiRuntimeUiTheme";

        [Tooltip("False registers nothing: no overlay, no command registry.")]
        [SerializeField]
        private bool enabled = true;

        [Tooltip("Keys that open the input empty.")]
        [SerializeField]
        private KeyCode[] openChatKeys = { KeyCode.T, KeyCode.Return, KeyCode.KeypadEnter };

        [Tooltip("Keys that open the input with the command prefix typed.")]
        [SerializeField]
        private KeyCode[] openCommandKeys = { KeyCode.Slash, KeyCode.KeypadDivide };

        [Tooltip("Key that closes the input (and only the input).")]
        [SerializeField]
        private KeyCode closeKey = KeyCode.Escape;

        [Tooltip("Seconds a line stays visible after it last changed while the input is closed; 0 never fades.")]
        [SerializeField]
        private float fadeDelaySeconds = 10f;

        [Tooltip("Length of the fade animation in seconds.")]
        [SerializeField]
        private float fadeDurationSeconds = 0.6f;

        [Tooltip("Most lines the feed keeps.")]
        [SerializeField]
        private int maxFeedLines = 100;

        [Tooltip("Agent role that answers; empty uses the chat default role.")]
        [SerializeField]
        private string roleId = "";

        [Tooltip("False always waits for the whole answer; true streams when the role and settings allow it.")]
        [SerializeField]
        private bool enableStreaming = true;

        [Tooltip("Give the answering agent the runtime camera tool when the model can see images.")]
        [SerializeField]
        private bool enableCameraTool = true;

        [Tooltip("Register /help, /agent, /model and /stop.")]
        [SerializeField]
        private bool registerBuiltInCommands = true;

        [Tooltip("Speaker name of the local player.")]
        [SerializeField]
        private string playerName = "You";

        [Tooltip("Speaker name of the agent; {role} is replaced with the role id.")]
        [SerializeField]
        private string assistantName = "AI";

        [Tooltip("Panel the overlay renders into. Empty creates one at runtime.")]
        [SerializeField]
        private PanelSettings panelSettings;

        [Tooltip("Theme of a runtime-created panel. Empty tries Resources/" + DefaultThemeResourcePath + ".")]
        [SerializeField]
        private ThemeStyleSheet themeStyleSheet;

        [Tooltip("Optional stylesheet added to the overlay root (classes coreai-chat-overlay*).")]
        [SerializeField]
        private StyleSheet styleSheet;

        [Tooltip("Apply the built-in look in code. Turn off to style everything from the stylesheet.")]
        [SerializeField]
        private bool useDefaultStyle = true;

        [Tooltip("Sorting order of the overlay document.")]
        [SerializeField]
        private int sortingOrder = 90;

        [Tooltip("Width of the chat column in panel pixels.")]
        [SerializeField]
        private float width = 620f;

        [Tooltip("Height of the feed in panel pixels; older lines scroll above it.")]
        [SerializeField]
        private float feedMaxHeight = 300f;

        [Tooltip("Font size in panel pixels.")]
        [SerializeField]
        private float fontSize = 17f;

        [Tooltip("Distance from the left and bottom screen edges in panel pixels.")]
        [SerializeField]
        private float margin = 16f;

        [Tooltip("Keep the overlay alive across scene loads (play mode only).")]
        [SerializeField]
        private bool dontDestroyOnLoad = true;

        [NonSerialized]
        private ChatOverlayOptions _options;

        /// <summary>False registers nothing.</summary>
        public bool Enabled
        {
            get => enabled;
            set => enabled = value;
        }

        /// <summary>Keys that open the input empty.</summary>
        public KeyCode[] OpenChatKeys
        {
            get => openChatKeys ?? Array.Empty<KeyCode>();
            set => openChatKeys = value;
        }

        /// <summary>Keys that open the input with the command prefix typed.</summary>
        public KeyCode[] OpenCommandKeys
        {
            get => openCommandKeys ?? Array.Empty<KeyCode>();
            set => openCommandKeys = value;
        }

        /// <summary>Key that closes the input.</summary>
        public KeyCode CloseKey
        {
            get => closeKey;
            set => closeKey = value;
        }

        /// <summary>Seconds before a line fades while the input is closed; 0 never fades.</summary>
        public float FadeDelaySeconds
        {
            get => fadeDelaySeconds;
            set => fadeDelaySeconds = value;
        }

        /// <summary>Length of the fade animation.</summary>
        public float FadeDurationSeconds
        {
            get => fadeDurationSeconds;
            set => fadeDurationSeconds = value;
        }

        /// <summary>Most lines the feed keeps.</summary>
        public int MaxFeedLines
        {
            get => maxFeedLines;
            set => maxFeedLines = value;
        }

        /// <summary>Agent role that answers; empty uses the chat default role.</summary>
        public string RoleId
        {
            get => roleId ?? "";
            set => roleId = value;
        }

        /// <summary>False always waits for the whole answer.</summary>
        public bool EnableStreaming
        {
            get => enableStreaming;
            set => enableStreaming = value;
        }

        /// <summary>Give the answering agent the runtime camera tool.</summary>
        public bool EnableCameraTool
        {
            get => enableCameraTool;
            set => enableCameraTool = value;
        }

        /// <summary>Register /help, /agent, /model and /stop.</summary>
        public bool RegisterBuiltInCommands
        {
            get => registerBuiltInCommands;
            set => registerBuiltInCommands = value;
        }

        /// <summary>Speaker name of the local player.</summary>
        public string PlayerName
        {
            get => playerName ?? "";
            set => playerName = value;
        }

        /// <summary>Speaker name of the agent.</summary>
        public string AssistantName
        {
            get => assistantName ?? "";
            set => assistantName = value;
        }

        /// <summary>Panel to render into; null creates one at runtime.</summary>
        public PanelSettings PanelSettings
        {
            get => panelSettings;
            set => panelSettings = value;
        }

        /// <summary>Theme of a runtime-created panel.</summary>
        public ThemeStyleSheet ThemeStyleSheet
        {
            get => themeStyleSheet;
            set => themeStyleSheet = value;
        }

        /// <summary>Optional stylesheet added to the overlay root.</summary>
        public StyleSheet StyleSheet
        {
            get => styleSheet;
            set => styleSheet = value;
        }

        /// <summary>Apply the built-in look in code.</summary>
        public bool UseDefaultStyle
        {
            get => useDefaultStyle;
            set => useDefaultStyle = value;
        }

        /// <summary>Sorting order of the overlay document.</summary>
        public int SortingOrder
        {
            get => sortingOrder;
            set => sortingOrder = value;
        }

        /// <summary>Width of the chat column.</summary>
        public float Width
        {
            get => width;
            set => width = value;
        }

        /// <summary>Height of the feed.</summary>
        public float FeedMaxHeight
        {
            get => feedMaxHeight;
            set => feedMaxHeight = value;
        }

        /// <summary>Font size.</summary>
        public float FontSize
        {
            get => fontSize;
            set => fontSize = value;
        }

        /// <summary>Distance from the left and bottom screen edges.</summary>
        public float Margin
        {
            get => margin;
            set => margin = value;
        }

        /// <summary>Keep the overlay alive across scene loads.</summary>
        public bool DontDestroyOnLoad
        {
            get => dontDestroyOnLoad;
            set => dontDestroyOnLoad = value;
        }

        /// <summary>
        /// When set before the scope builds, the overlay attaches into this element of an existing panel instead
        /// of creating its own document.
        /// </summary>
        public VisualElement HostElement { get; set; }

        /// <summary>
        /// Texts, formats and behaviour the inspector does not show. The serialized fields above are applied over
        /// it by <see cref="ResolveOptions"/>.
        /// </summary>
        public ChatOverlayOptions Options
        {
            get => _options ??= new ChatOverlayOptions();
            set => _options = value;
        }

        /// <summary>
        /// A copy of <see cref="Options"/> with the serialized fields applied; an empty role or speaker name keeps
        /// the value set on <see cref="Options"/>.
        /// </summary>
        public ChatOverlayOptions ResolveOptions()
        {
            ChatOverlayOptions resolved = Options.Clone();
            if (!string.IsNullOrWhiteSpace(roleId))
            {
                resolved.RoleId = roleId.Trim();
            }

            if (!string.IsNullOrEmpty(playerName))
            {
                resolved.PlayerSpeakerName = playerName;
            }

            if (!string.IsNullOrEmpty(assistantName))
            {
                resolved.AssistantSpeakerName = assistantName;
            }

            resolved.EnableStreaming = enableStreaming;
            resolved.FadeDelaySeconds = fadeDelaySeconds;
            resolved.MaxFeedLines = Math.Max(1, maxFeedLines);
            return resolved;
        }
    }
}
