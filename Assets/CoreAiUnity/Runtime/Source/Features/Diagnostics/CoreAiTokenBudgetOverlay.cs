using System;
using System.Text;
using CoreAI.Ai;
using CoreAI.Composition;
using CoreAI.Messaging;
using MessagePipe;
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
    /// Toggle via the inspector flag or the hotkey (default F10). Works in Editor Play Mode and players;
    /// outside Play Mode it renders a "no service" panel.
    /// </summary>
    [ExecuteAlways]
    public sealed class CoreAiTokenBudgetOverlay : MonoBehaviour
    {
        [Header("Display Settings")] [Tooltip("Show or hide the overlay window.")] [SerializeField]
        private bool _showOverlay = true;

        [Tooltip("Hotkey that toggles the overlay at runtime.")] [SerializeField]
        private KeyCode _toggleKey = KeyCode.F10;

        [Tooltip("Rolling window length in seconds for the request-load indicator.")] [SerializeField] [Min(1f)]
        private float _rollingWindowSeconds = 60f;

        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

        private TokenBudgetCalculator _calculator;
        private CoreAILifetimeScope _scope;
        private IInGameLlmChatService _chatService;
        private ICoreAISettings _settings;
        private IDisposable _usageSubscription;
        private bool _resolved;
        private float _nextResolveAttempt;

        private Rect _windowRect = new(10, 300, 380, 260);
        private GUIStyle _headerStyle;
        private GUIStyle _valueStyle;
        private GUIStyle _alertStyle;
        private bool _stylesInitialized;

        /// <summary>Aggregator behind the overlay; exposed for host code and tests.</summary>
        public TokenBudgetCalculator Calculator => _calculator;

        private void Awake()
        {
            _calculator = new TokenBudgetCalculator(_rollingWindowSeconds);
        }

        private void OnDestroy()
        {
            _usageSubscription?.Dispose();
            _usageSubscription = null;
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

            if (!_resolved && Time.realtimeSinceStartup >= _nextResolveAttempt)
            {
                _nextResolveAttempt = Time.realtimeSinceStartup + 1f;
                TryResolveServices();
            }
        }

        /// <summary>
        /// Resolves CoreAI services from the scene <see cref="CoreAILifetimeScope"/>; safe to retry.
        /// </summary>
        private void TryResolveServices()
        {
            if (_scope == null)
            {
                _scope = FindAnyObjectByType<CoreAILifetimeScope>(FindObjectsInactive.Include);
            }

            if (_scope == null || _scope.Container == null)
            {
                return;
            }

            try
            {
                _chatService = (IInGameLlmChatService)_scope.Container.Resolve(typeof(IInGameLlmChatService));
            }
            catch (Exception)
            {
                _chatService = null;
            }

            try
            {
                _settings = (ICoreAISettings)_scope.Container.Resolve(typeof(ICoreAISettings));
            }
            catch (Exception)
            {
                _settings = null;
            }

            if (_usageSubscription == null)
            {
                try
                {
                    ISubscriber<LlmUsageReported> usage =
                        (ISubscriber<LlmUsageReported>)_scope.Container.Resolve(typeof(ISubscriber<LlmUsageReported>));
                    _usageSubscription = usage.Subscribe(OnUsageReported);
                }
                catch (Exception)
                {
                    _usageSubscription = null;
                }
            }

            _resolved = _chatService != null || _settings != null || _usageSubscription != null;
        }

        /// <summary>
        /// Records a usage event into the calculator. May be invoked off the main thread,
        /// so it only touches the thread-safe calculator and the stopwatch clock.
        /// </summary>
        private void OnUsageReported(LlmUsageReported usage)
        {
            _calculator?.RecordUsage(
                usage.PromptTokens,
                usage.CompletionTokens,
                usage.TotalTokens,
                _clock.Elapsed.TotalSeconds);
        }

        private bool IsToggleKeyPressedThisFrame()
        {
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
            if (_calculator == null)
            {
                _calculator = new TokenBudgetCalculator(_rollingWindowSeconds);
            }

            if (!Application.isPlaying || (!_resolved && _usageSubscription == null))
            {
                GUILayout.Label("no service", _alertStyle);
                GUILayout.Label(
                    Application.isPlaying
                        ? "  Waiting for CoreAILifetimeScope..."
                        : "  Enter Play Mode with a CoreAILifetimeScope in the scene.",
                    _valueStyle);
                GUI.DragWindow();
                return;
            }

            double now = _clock.Elapsed.TotalSeconds;

            GUILayout.Label("Tokens", _headerStyle);
            StringBuilder sb = new();
            sb.AppendLine(
                $"  Last request: {FmtTok(_calculator.LastPromptTokens)} in / {FmtTok(_calculator.LastCompletionTokens)} out / {FmtTok(_calculator.LastTotalTokens)} total");
            sb.AppendLine(
                $"  Session: {_calculator.TotalPromptTokens} in / {_calculator.TotalCompletionTokens} out / {_calculator.TotalTokens} total");
            sb.AppendLine(
                $"  Requests: {_calculator.TotalRequests} (with usage: {_calculator.RequestsWithUsage}) | avg {_calculator.AverageTokensPerRequest:F0} tok/req");
            GUILayout.Label(sb.ToString().TrimEnd(), _valueStyle);

            GUILayout.Label("Cost", _headerStyle);
            double inPrice = _settings?.InputTokenPricePer1KUsd ?? 0f;
            double outPrice = _settings?.OutputTokenPricePer1KUsd ?? 0f;
            if (TokenBudgetCalculator.HasPricing(inPrice, outPrice))
            {
                double sessionCost = _calculator.EstimateSessionCostUsd(inPrice, outPrice);
                double lastCost = TokenBudgetCalculator.ComputeCostUsd(
                    Math.Max(_calculator.LastPromptTokens, 0),
                    Math.Max(_calculator.LastCompletionTokens, 0),
                    inPrice, outPrice);
                GUILayout.Label(
                    $"  Session: ${sessionCost:F4} | last request: ${lastCost:F4}\n" +
                    $"  (in ${inPrice:F4}/1K, out ${outPrice:F4}/1K)", _valueStyle);
            }
            else
            {
                GUILayout.Label("  Prices not set (CoreAISettings > Debug > Token budget overlay)", _valueStyle);
            }

            GUILayout.Label("Request Load", _headerStyle);
            int requestsInWindow = _calculator.GetRequestsInWindow(now);
            long tokensInWindow = _calculator.GetTokensInWindow(now);
            RateLimiterMetrics rate = _chatService?.GetRateLimiterMetrics() ?? default;
            if (rate.MaxRequestsPerWindow > 0)
            {
                bool nearLimit = rate.AcceptedInWindow >= rate.MaxRequestsPerWindow;
                GUILayout.Label(
                    $"  Chat limiter: {rate.AcceptedInWindow}/{rate.MaxRequestsPerWindow} per {rate.WindowSeconds}s {Bar(rate.AcceptedInWindow, rate.MaxRequestsPerWindow)}\n" +
                    $"  Rejected total: {rate.TotalRejected}",
                    nearLimit ? _alertStyle : _valueStyle);
            }
            else
            {
                GUILayout.Label("  Chat limiter: n/a (no IInGameLlmChatService / limit off)", _valueStyle);
            }

            GUILayout.Label(
                $"  All LLM usage: {requestsInWindow} req / {tokensInWindow} tok in last {(int)_calculator.WindowSeconds}s",
                _valueStyle);

            GUILayout.Label($"\n[{_toggleKey}] toggle  |  drag to move", _valueStyle);
            GUI.DragWindow();
        }

        /// <summary>Renders a 10-segment text load bar, e.g. <c>[###.......]</c>.</summary>
        private static string Bar(int value, int max)
        {
            if (max <= 0)
            {
                return "";
            }

            int filled = Mathf.Clamp(Mathf.RoundToInt(value / (float)max * 10f), 0, 10);
            return "[" + new string('#', filled) + new string('.', 10 - filled) + "]";
        }

        private static string FmtTok(int value)
        {
            return value < 0 ? "-" : value.ToString();
        }
    }
}
