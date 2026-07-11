using System;
using System.Collections.Generic;
using System.Globalization;
using CoreAI.Ai;
using UnityEngine.UIElements;

namespace CoreAI.Hub.UI
{
    /// <summary>
    /// Built-in Hub page that shows live orchestration metrics, derived health signals, current backend,
    /// and per-role breakdown from <see cref="InMemoryAiOrchestrationMetrics"/>.
    /// </summary>
    public sealed class HubStatisticsPage : HubPageBase
    {
        /// <summary>Default registry id for the built-in Statistics page.</summary>
        public const string DefaultPageId = "coreai.hub.statistics";

        private const long RefreshIntervalMs = 1000;

        private readonly InMemoryAiOrchestrationMetrics _metrics;
        private readonly ICoreAISettings _settings;

        private Label _backend;
        private Label _liveState;
        private Label _completions;
        private Label _successFail;
        private Label _successRate;
        private Label _avgLatency;
        private Label _retryPressure;
        private Label _commandsPerCompletion;
        private Label _lastSuccess;
        private Label _capacity;
        private VisualElement _rolesContainer;

        /// <summary>Creates the Statistics page from optional live sources (null-tolerant).</summary>
        public HubStatisticsPage(
            InMemoryAiOrchestrationMetrics metrics = null,
            ICoreAISettings settings = null,
            string pageId = DefaultPageId,
            string displayName = "Statistics",
            int order = 200)
            : base(
                string.IsNullOrWhiteSpace(pageId) ? DefaultPageId : pageId,
                string.IsNullOrWhiteSpace(displayName) ? "Statistics" : displayName,
                order)
        {
            _metrics = metrics;
            _settings = settings;
        }

        /// <inheritdoc />
        public override Func<object> CreatePageContent => BuildContent;

        /// <inheritdoc />
        public override void OnActivated()
        {
            Refresh();
        }

        private object BuildContent()
        {
            ScrollView scroll = HubPageWidgets.CreatePage(DisplayName, out VisualElement body);
            scroll.AddToClassList("coreai-hub-page");
            body.AddToClassList("coreai-hub-page-body");

            body.Add(HubPageWidgets.MakeSection("Backend snapshot"));
            body.Add(HubPageWidgets.MakeRow("Current backend", "-", out _backend));
            body.Add(HubPageWidgets.MakeRow("Live scope", "-", out _liveState));

            body.Add(HubPageWidgets.MakeSection("Throughput and reliability"));
            body.Add(HubPageWidgets.MakeRow("Completions", "0", out _completions));
            body.Add(HubPageWidgets.MakeRow("Success / fail", "0 / 0", out _successFail));
            body.Add(HubPageWidgets.MakeRow("Success rate", "-", out _successRate));
            body.Add(HubPageWidgets.MakeRow("Avg latency", "-", out _avgLatency));
            body.Add(HubPageWidgets.MakeRow("Structured retry pressure", "-", out _retryPressure));
            body.Add(HubPageWidgets.MakeRow("Commands per completion", "-", out _commandsPerCompletion));
            body.Add(HubPageWidgets.MakeRow("Last success", "-", out _lastSuccess));

            if (_settings != null)
            {
                body.Add(HubPageWidgets.MakeSection("Configured capacity"));
                body.Add(HubPageWidgets.MakeRow("Token budget", "-", out _capacity));
            }

            VisualElement actions = new();
            actions.AddToClassList("coreai-hub-actions");
            actions.style.flexDirection = FlexDirection.Row;
            actions.style.flexWrap = Wrap.Wrap;
            actions.Add(MakeButton("Refresh", Refresh));
            if (_metrics != null)
            {
                actions.Add(MakeButton("Reset counters", ResetMetrics));
            }

            body.Add(actions);

            body.Add(HubPageWidgets.MakeSection("Per-role activity"));
            _rolesContainer = new VisualElement { name = "coreai-hub-stats-roles" };
            body.Add(_rolesContainer);

            if (_metrics == null)
            {
                body.Add(HubPageWidgets.MakeNote(
                    "No orchestration metrics source is wired. The backend snapshot still updates, but " +
                    "completion, latency, retry, and role counters need InMemoryAiOrchestrationMetrics."));
            }

            Refresh();
            body.schedule.Execute(Refresh).Every(RefreshIntervalMs);

            return scroll;
        }

        private void ResetMetrics()
        {
            _metrics?.Reset();
            Refresh();
        }

        private void Refresh()
        {
            CoreAiBackendStatus status = CoreAiBackend.Status;
            if (_backend != null)
            {
                _backend.text = status.ToString();
            }

            if (_liveState != null)
            {
                _liveState.text = status.IsLive ? "Live, hot-swappable" : "No live CoreAI scope";
            }

            if (_settings != null && _capacity != null)
            {
                _capacity.text = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:N0} context / {1}",
                    _settings.ContextWindowTokens,
                    _settings.MaxTokens > 0
                        ? _settings.MaxTokens.ToString("N0", CultureInfo.InvariantCulture) + " max output"
                        : "provider output default");
            }

            if (_metrics == null || _completions == null)
            {
                RefreshRoles();
                return;
            }

            int total = _metrics.TotalCompletions;
            int ok = _metrics.SuccessfulCompletions;
            int failed = _metrics.FailedCompletions;

            _completions.text = total.ToString(CultureInfo.InvariantCulture);
            _successFail.text = string.Format(CultureInfo.InvariantCulture, "{0} / {1}", ok, failed);
            _successRate.text = total == 0 ? "no completions yet" : Percent(ok, total);
            _avgLatency.text = total == 0
                ? "-"
                : _metrics.AverageLatencyMs.ToString("0", CultureInfo.InvariantCulture) + " ms";
            _retryPressure.text = total == 0
                ? "no completions yet"
                : string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} retries ({1})",
                    _metrics.StructuredRetries,
                    Percent(_metrics.StructuredRetries, total));
            _commandsPerCompletion.text = total == 0
                ? "-"
                : ((double)_metrics.CommandsPublished / total).ToString("0.##", CultureInfo.InvariantCulture);
            _lastSuccess.text = ok == 0
                ? "never"
                : _metrics.SecondsSinceLastSuccess.ToString("0", CultureInfo.InvariantCulture) + " s ago";

            RefreshRoles();
        }

        private void RefreshRoles()
        {
            if (_rolesContainer == null)
            {
                return;
            }

            _rolesContainer.Clear();
            if (_metrics == null)
            {
                _rolesContainer.Add(HubPageWidgets.MakeRow("(metrics not wired)", ""));
                return;
            }

            Dictionary<string, InMemoryAiOrchestrationMetrics.RoleMetrics> roles = _metrics.GetAllRoleMetrics();
            if (roles.Count == 0)
            {
                _rolesContainer.Add(HubPageWidgets.MakeRow("(no role activity yet)", ""));
                return;
            }

            foreach (KeyValuePair<string, InMemoryAiOrchestrationMetrics.RoleMetrics> kvp in roles)
            {
                InMemoryAiOrchestrationMetrics.RoleMetrics rm = kvp.Value;
                string value = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}/{1} OK ({2}), {3} fail, {4} ms avg, {5} retries, {6} cmds",
                    rm.Successes,
                    rm.Completions,
                    Percent(rm.Successes, rm.Completions),
                    rm.Failures,
                    rm.AverageLatencyMs.ToString("0", CultureInfo.InvariantCulture),
                    rm.StructuredRetries,
                    rm.CommandsPublished);
                _rolesContainer.Add(
                    HubPageWidgets.MakeRow(string.IsNullOrEmpty(kvp.Key) ? "(default)" : kvp.Key, value));
            }
        }

        private static string Percent(int numerator, int denominator)
        {
            if (denominator <= 0)
            {
                return "-";
            }

            return ((double)numerator / denominator * 100d).ToString("0.#", CultureInfo.InvariantCulture) + "%";
        }

        private static Button MakeButton(string text, Action clicked)
        {
            Button button = new(clicked) { text = text };
            button.AddToClassList("coreai-hub-action-button");
            return button;
        }
    }
}