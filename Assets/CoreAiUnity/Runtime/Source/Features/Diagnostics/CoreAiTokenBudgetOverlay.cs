using CoreAI.Ai;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM && COREAI_HAS_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace CoreAI.Diagnostics
{
    /// <summary>
    /// IMGUI overlay with runtime token-budget diagnostics: last-request and session token counts,
    /// estimated session cost (when prices are configured on <see cref="CoreAISettingsAsset"/>),
    /// and a rolling-window request-load indicator against the chat-service rate limiter.
    /// Toggle via the inspector flag or the hotkey (default F10, <see cref="KeyCode.None"/> disables it).
    /// Works in Editor Play Mode and players; outside Play Mode it renders a "no service" panel.
    /// For a game-styled UI on your own Canvas, use <see cref="CoreAiTokenBudgetUiView"/> instead.
    /// </summary>
    [ExecuteAlways]
    public sealed class CoreAiTokenBudgetOverlay : MonoBehaviour
    {
        [Header("Display Settings")] [Tooltip("Show or hide the overlay window.")] [SerializeField]
        private bool _showOverlay = true;

        [Tooltip("Hotkey that toggles the overlay at runtime. Set to None to disable the hotkey.")] [SerializeField]
        private KeyCode _toggleKey = KeyCode.F10;

        [Tooltip("Rolling window length in seconds for the request-load indicator.")] [SerializeField] [Min(1f)]
        private float _rollingWindowSeconds = 60f;

        private TokenBudgetRuntimeSource _source;

        private Rect _windowRect = new(10, 300, 380, 260);
        private GUIStyle _headerStyle;
        private GUIStyle _valueStyle;
        private GUIStyle _alertStyle;
        private bool _stylesInitialized;

        /// <summary>Aggregator behind the overlay; exposed for host code and tests.</summary>
        public TokenBudgetCalculator Calculator => GetOrCreateSource().Calculator;

        /// <summary>Show or hide the overlay window from code.</summary>
        public bool ShowOverlay
        {
            get => _showOverlay;
            set => _showOverlay = value;
        }

        /// <summary>Toggle hotkey; <see cref="KeyCode.None"/> disables it.</summary>
        public KeyCode ToggleKey
        {
            get => _toggleKey;
            set => _toggleKey = value;
        }

        private void Awake()
        {
            GetOrCreateSource();
        }

        private void OnDestroy()
        {
            _source?.Dispose();
            _source = null;
        }

        private TokenBudgetRuntimeSource GetOrCreateSource()
        {
            return _source ??= new TokenBudgetRuntimeSource(_rollingWindowSeconds);
        }

        private void Update()
        {
            if (!Application.isPlaying)
            {
                return;
            }

            if (IsToggleKeyPressedThisFrame())
            {
                _showOverlay = !_showOverlay;
            }

            GetOrCreateSource().TickResolve();
        }

        private bool IsToggleKeyPressedThisFrame()
        {
            if (_toggleKey == KeyCode.None)
            {
                return false;
            }
#if ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetKeyDown(_toggleKey))
            {
                return true;
            }
#endif
#if ENABLE_INPUT_SYSTEM && COREAI_HAS_INPUT_SYSTEM
            Keyboard kb = Keyboard.current;
            if (kb != null)
            {
                Key key = ToInputSystemKey(_toggleKey);
                if (key != Key.None && kb[key].wasPressedThisFrame)
                {
                    return true;
                }
            }
#endif
            return false;
        }

#if ENABLE_INPUT_SYSTEM && COREAI_HAS_INPUT_SYSTEM
        /// <summary>
        /// Maps a legacy <see cref="KeyCode"/> value to the Input System key enum.
        /// </summary>
        private static Key ToInputSystemKey(KeyCode keyCode)
        {
            switch (keyCode)
            {
                case KeyCode.F1: return Key.F1;
                case KeyCode.F2: return Key.F2;
                case KeyCode.F3: return Key.F3;
                case KeyCode.F4: return Key.F4;
                case KeyCode.F5: return Key.F5;
                case KeyCode.F6: return Key.F6;
                case KeyCode.F7: return Key.F7;
                case KeyCode.F8: return Key.F8;
                case KeyCode.F9: return Key.F9;
                case KeyCode.F10: return Key.F10;
                case KeyCode.F11: return Key.F11;
                case KeyCode.F12: return Key.F12;
                case KeyCode.BackQuote: return Key.Backquote;
                case KeyCode.Tab: return Key.Tab;
                case KeyCode.Escape: return Key.Escape;
                case KeyCode.Return: return Key.Enter;
                case KeyCode.Space: return Key.Space;
                default: return Key.None;
            }
        }
#endif

        private void InitStyles()
        {
            if (_stylesInitialized)
            {
                return;
            }

            _headerStyle = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold,
                fontSize = 14,
                normal = { textColor = new Color(0.3f, 0.8f, 1f) }
            };
            _valueStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                normal = { textColor = Color.white }
            };
            _alertStyle = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold,
                fontSize = 12,
                normal = { textColor = new Color(1f, 0.3f, 0.3f) }
            };

            _stylesInitialized = true;
        }

        private void OnGUI()
        {
            if (!_showOverlay)
            {
                return;
            }

            InitStyles();

            _windowRect = GUI.Window(98766, _windowRect, DrawWindow, "CoreAI - Token Budget");
        }

        private void DrawWindow(int id)
        {
            if (GUI.Button(new Rect(_windowRect.width - 58f, 2f, 52f, 18f), "Hide"))
            {
                _showOverlay = false;
            }

            TokenBudgetRuntimeSource source = GetOrCreateSource();

            if (!Application.isPlaying || !source.IsResolved)
            {
                GUILayout.Label("no service", _alertStyle);
                GUILayout.Label(
                    Application.isPlaying
                        ? "  Waiting for CoreAILifetimeScope..."
                        : "  Enter Play Mode with a CoreAILifetimeScope in the scene.",
                    _valueStyle);
                GUI.DragWindow(new Rect(0, 0, _windowRect.width, 22f));
                return;
            }

            TokenBudgetCalculator calc = source.Calculator;
            double now = source.NowSeconds;

            GUILayout.Label("Tokens", _headerStyle);
            GUILayout.Label(Indent(TokenBudgetTextFormatter.FormatTokens(calc)), _valueStyle);

            GUILayout.Label("Cost", _headerStyle);
            double inPrice = source.Settings?.InputTokenPricePer1KUsd ?? 0f;
            double outPrice = source.Settings?.OutputTokenPricePer1KUsd ?? 0f;
            GUILayout.Label(Indent(TokenBudgetTextFormatter.FormatCost(calc, inPrice, outPrice)), _valueStyle);

            GUILayout.Label("Request Load", _headerStyle);
            RateLimiterMetrics rate = source.ChatService?.GetRateLimiterMetrics() ?? default;
            string loadText = TokenBudgetTextFormatter.FormatLoad(calc, rate, now, out bool nearLimit);
            GUILayout.Label(Indent(loadText), nearLimit ? _alertStyle : _valueStyle);

            GUILayout.Label($"\n[{_toggleKey}] toggle  |  drag to move", _valueStyle);
            GUI.DragWindow(new Rect(0, 0, _windowRect.width, 22f));
        }

        /// <summary>Prefixes every line with two spaces to match the section headers.</summary>
        private static string Indent(string text)
        {
            return string.IsNullOrEmpty(text) ? text : "  " + text.Replace("\n", "\n  ");
        }
    }
}