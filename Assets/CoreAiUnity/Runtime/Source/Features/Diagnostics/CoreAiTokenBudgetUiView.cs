using CoreAI.Ai;
using UnityEngine;
using UnityEngine.Events;

namespace CoreAI.Diagnostics
{
    /// <summary>
    /// UGUI-friendly token-budget view for a game's own Canvas: instead of drawing anything itself,
    /// it periodically pushes formatted text through <see cref="UnityEvent{T0}"/> outputs that you wire
    /// to your UI in the inspector (e.g. <c>TMP_Text.text</c> or <c>Text.text</c> dynamic-string slots).
    /// No hotkey, no IMGUI — show/hide it with your own UI logic by enabling/disabling the component
    /// or its GameObject. Same data as <see cref="CoreAiTokenBudgetOverlay"/>.
    /// </summary>
    public sealed class CoreAiTokenBudgetUiView : MonoBehaviour
    {
        [Header("Update")]
        [Tooltip("How often the text outputs are refreshed, in seconds.")]
        [SerializeField]
        [Min(0.05f)]
        private float _updateInterval = 0.25f;

        [Tooltip("Rolling window length in seconds for the request-load indicator.")]
        [SerializeField]
        [Min(1f)]
        private float _rollingWindowSeconds = 60f;

        [Header("Text Outputs (bind to your UI)")]
        [Tooltip("Token lines: last request / session totals / request counts.")]
        [SerializeField]
        private UnityEvent<string> _onTokensTextChanged = new();

        [Tooltip("Cost lines, or a hint when prices are not configured in CoreAISettings.")]
        [SerializeField]
        private UnityEvent<string> _onCostTextChanged = new();

        [Tooltip("Rate-limiter and rolling-window load lines.")]
        [SerializeField]
        private UnityEvent<string> _onLoadTextChanged = new();

        [Header("State Outputs")]
        [Tooltip("Fires with true when the chat rate-limiter window saturates, false when it recovers.")]
        [SerializeField]
        private UnityEvent<bool> _onNearLimitChanged = new();

        [Tooltip("Fires with true once CoreAI services are found, false while still waiting for the scope.")]
        [SerializeField]
        private UnityEvent<bool> _onServiceAvailableChanged = new();

        private TokenBudgetRuntimeSource _source;
        private float _nextRefreshTime;
        private bool _lastNearLimit;
        private bool _lastServiceAvailable;
        private bool _hasPushedOnce;

        /// <summary>Aggregator behind this view; exposed for host code and tests.</summary>
        public TokenBudgetCalculator Calculator => _source?.Calculator;

        /// <summary>Shared data source; exposed so code-driven UIs can read raw values directly.</summary>
        public TokenBudgetRuntimeSource Source => _source;

        /// <summary>Token lines output; available for code-side subscription.</summary>
        public UnityEvent<string> OnTokensTextChanged => _onTokensTextChanged;

        /// <summary>Cost lines output; available for code-side subscription.</summary>
        public UnityEvent<string> OnCostTextChanged => _onCostTextChanged;

        /// <summary>Load lines output; available for code-side subscription.</summary>
        public UnityEvent<string> OnLoadTextChanged => _onLoadTextChanged;

        /// <summary>Near-limit flag output; available for code-side subscription.</summary>
        public UnityEvent<bool> OnNearLimitChanged => _onNearLimitChanged;

        /// <summary>Service-availability output; available for code-side subscription.</summary>
        public UnityEvent<bool> OnServiceAvailableChanged => _onServiceAvailableChanged;

        private void Awake()
        {
            _source = new TokenBudgetRuntimeSource(_rollingWindowSeconds);
        }

        private void OnEnable()
        {
            // Push immediately on (re)enable so freshly shown panels never display stale text.
            _nextRefreshTime = 0f;
        }

        private void OnDestroy()
        {
            _source?.Dispose();
            _source = null;
        }

        private void Update()
        {
            if (!Application.isPlaying || _source == null)
            {
                return;
            }

            _source.TickResolve();

            if (Time.unscaledTime < _nextRefreshTime)
            {
                return;
            }

            _nextRefreshTime = Time.unscaledTime + _updateInterval;
            Refresh();
        }

        /// <summary>Recomputes all outputs and fires the bound events. Safe to call manually.</summary>
        public void Refresh()
        {
            if (_source == null)
            {
                return;
            }

            TokenBudgetCalculator calc = _source.Calculator;
            double now = _source.NowSeconds;

            _onTokensTextChanged.Invoke(TokenBudgetTextFormatter.FormatTokens(calc));

            double inPrice = _source.Settings?.InputTokenPricePer1KUsd ?? 0f;
            double outPrice = _source.Settings?.OutputTokenPricePer1KUsd ?? 0f;
            _onCostTextChanged.Invoke(TokenBudgetTextFormatter.FormatCost(calc, inPrice, outPrice));

            RateLimiterMetrics rate = _source.ChatService?.GetRateLimiterMetrics() ?? default;
            string loadText = TokenBudgetTextFormatter.FormatLoad(calc, rate, now, out bool nearLimit);
            _onLoadTextChanged.Invoke(loadText);

            if (!_hasPushedOnce || nearLimit != _lastNearLimit)
            {
                _lastNearLimit = nearLimit;
                _onNearLimitChanged.Invoke(nearLimit);
            }

            bool serviceAvailable = _source.IsResolved;
            if (!_hasPushedOnce || serviceAvailable != _lastServiceAvailable)
            {
                _lastServiceAvailable = serviceAvailable;
                _onServiceAvailableChanged.Invoke(serviceAvailable);
            }

            _hasPushedOnce = true;
        }
    }
}
