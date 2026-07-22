using System;
using CoreAI.Diagnostics;
using CoreAI.Hub;
using UnityEngine.UIElements;

namespace CoreAI.Demos
{
    /// <summary>
    /// UI Toolkit Hub page that replaces the floating IMGUI <c>CoreAiTokenBudgetOverlay</c> (F10). It owns a
    /// <see cref="TokenBudgetRuntimeSource"/> and renders the same Tokens / Cost / Request Load sections the
    /// overlay drew, formatted through the shared <see cref="TokenBudgetTextFormatter"/>, refreshed on the
    /// panel's own scheduler while the tab is visible.
    /// </summary>
    public sealed class TokenBudgetHubPage : HubPageBase
    {
        /// <summary>Default registry id for the Token Budget page.</summary>
        public const string DefaultPageId = "coreai.demo.fullaccess.tokenbudget";

        private const double RollingWindowSeconds = 60d;

        private TokenBudgetRuntimeSource _source;
        private Label _statusLabel;
        private Label _tokensLabel;
        private Label _costLabel;
        private Label _loadLabel;
        private IVisualElementScheduledItem _tick;

        public TokenBudgetHubPage(
            string pageId = DefaultPageId,
            string displayName = "Token Budget",
            int order = 90)
            : base(
                string.IsNullOrWhiteSpace(pageId) ? DefaultPageId : pageId,
                string.IsNullOrWhiteSpace(displayName) ? "Token Budget" : displayName,
                order)
        {
        }

        /// <inheritdoc />
        public override Func<object> CreatePageContent => Build;

        /// <inheritdoc />
        public override void OnActivated()
        {
            _tick?.Resume();
            Refresh();
        }

        /// <inheritdoc />
        public override void OnDeactivated()
        {
            _tick?.Pause();
        }

        /// <inheritdoc />
        public override void OnDestroyed()
        {
            _tick?.Pause();
            _tick = null;
            _source?.Dispose();
            _source = null;
        }

        private object Build()
        {
            ScrollView scroll = DemoHubWidgets.CreatePage("Token Budget", out VisualElement body);

            body.Add(DemoHubWidgets.MakeBody(
                "Live LLM token usage, estimated cost and request load over the last 60 seconds."));

            _statusLabel = DemoHubWidgets.MakeBody("");
            body.Add(_statusLabel);

            body.Add(DemoHubWidgets.MakeSection("Tokens"));
            _tokensLabel = DemoHubWidgets.MakeBody("-");
            body.Add(_tokensLabel);

            body.Add(DemoHubWidgets.MakeSection("Cost"));
            _costLabel = DemoHubWidgets.MakeBody("-");
            body.Add(_costLabel);

            body.Add(DemoHubWidgets.MakeSection("Request Load"));
            _loadLabel = DemoHubWidgets.MakeBody("-");
            body.Add(_loadLabel);

            _tick = scroll.schedule.Execute(Refresh).Every(250);
            Refresh();
            return scroll;
        }

        private void Refresh()
        {
            if (_tokensLabel == null)
            {
                return;
            }

            _source ??= new TokenBudgetRuntimeSource(RollingWindowSeconds);
            _source.TickResolve();

            if (!_source.IsResolved)
            {
                _statusLabel.text = "Waiting for a CoreAILifetimeScope in Play Mode…";
                _tokensLabel.text = _costLabel.text = _loadLabel.text = "-";
                return;
            }

            _statusLabel.text = "";

            TokenBudgetCalculator calc = _source.Calculator;
            double nowSeconds = _source.NowSeconds;

            _tokensLabel.text = TokenBudgetTextFormatter.FormatTokens(calc);

            double inPrice = _source.Settings?.InputTokenPricePer1KUsd ?? 0d;
            double outPrice = _source.Settings?.OutputTokenPricePer1KUsd ?? 0d;
            _costLabel.text = TokenBudgetTextFormatter.FormatCost(calc, inPrice, outPrice);

            RateLimiterMetrics rate = _source.ChatService?.GetRateLimiterMetrics() ?? default;
            _loadLabel.text = TokenBudgetTextFormatter.FormatLoad(calc, rate, nowSeconds, out _);
        }
    }
}
