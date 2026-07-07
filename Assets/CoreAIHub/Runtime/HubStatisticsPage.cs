using System;
using System.Collections.Generic;
using System.Globalization;
using CoreAI.Ai;
using UnityEngine.UIElements;

namespace CoreAI.Hub.UI
{
    /// <summary>
    /// Built-in Hub page that shows live orchestration metrics (completions, latency, retries, per-role
    /// breakdown) from <see cref="InMemoryAiOrchestrationMetrics"/> plus the token budget from
    /// <see cref="ICoreAISettings"/>. Read-only; values refresh on a light 1 s scheduler while the tab is visible.
    /// </summary>
    public sealed class HubStatisticsPage : IHubPage
    {
        /// <summary>Default registry id for the built-in Statistics page.</summary>
        public const string DefaultPageId = "coreai.hub.statistics";

        private const long RefreshIntervalMs = 1000;

        private readonly InMemoryAiOrchestrationMetrics _metrics;
        private readonly ICoreAISettings _settings;

        private Label _completions;
        private Label _successFail;
        private Label _avgLatency;
        private Label _retries;
        private Label _published;
        private Label _lastSuccess;
        private VisualElement _rolesContainer;

        /// <summary>Creates the Statistics page from optional live sources (null-tolerant).</summary>
        public HubStatisticsPage(
            InMemoryAiOrchestrationMetrics metrics = null,
            ICoreAISettings settings = null,
            string pageId = DefaultPageId,
            string displayName = "Statistics",
            int order = 200)
        {
            _metrics = metrics;
            _settings = settings;
            PageId = string.IsNullOrWhiteSpace(pageId) ? DefaultPageId : pageId;
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? "Statistics" : displayName;
            Order = order;
        }

        /// <inheritdoc />
        public string PageId { get; }

        /// <inheritdoc />
        public string DisplayName { get; }

        /// <inheritdoc />
        public int Order { get; }

        /// <inheritdoc />
        public Func<object> CreatePageContent => BuildContent;

        /// <inheritdoc />
        public void OnActivated()
        {
        }

        /// <inheritdoc />
        public void OnDeactivated()
        {
        }

        /// <inheritdoc />
        public void OnDestroyed()
        {
        }

        private object BuildContent()
        {
            ScrollView scroll = HubPageWidgets.CreatePage(DisplayName, out VisualElement body);

            if (_settings != null)
            {
                body.Add(HubPageWidgets.MakeSection("Token budget"));
                body.Add(HubPageWidgets.MakeRow("Context window",
                    _settings.ContextWindowTokens.ToString("N0", CultureInfo.InvariantCulture) + " tok"));
                body.Add(HubPageWidgets.MakeRow("Max output tokens",
                    _settings.MaxTokens > 0
                        ? _settings.MaxTokens.ToString("N0", CultureInfo.InvariantCulture) + " tok"
                        : "provider default"));
            }

            if (_metrics == null)
            {
                body.Add(HubPageWidgets.MakeNote(
                    "No orchestration metrics source is wired. Pass an InMemoryAiOrchestrationMetrics to " +
                    "HubBuiltInPages.RegisterAll to display live completion, latency, and per-role stats."));
                return scroll;
            }

            body.Add(HubPageWidgets.MakeSection("Orchestration"));
            body.Add(HubPageWidgets.MakeRow("Completions", "0", out _completions));
            body.Add(HubPageWidgets.MakeRow("Success / fail", "0 / 0", out _successFail));
            body.Add(HubPageWidgets.MakeRow("Avg latency", "0 ms", out _avgLatency));
            body.Add(HubPageWidgets.MakeRow("Structured retries", "0", out _retries));
            body.Add(HubPageWidgets.MakeRow("Commands published", "0", out _published));
            body.Add(HubPageWidgets.MakeRow("Last success", "-", out _lastSuccess));

            body.Add(HubPageWidgets.MakeSection("Per-role"));
            _rolesContainer = new VisualElement { name = "coreai-hub-stats-roles" };
            body.Add(_rolesContainer);

            body.Add(HubPageWidgets.MakeNote("Read-only. Refreshes about once a second while visible."));

            Refresh();
            // schedule only ticks while the element is attached to a panel (i.e. the tab is shown).
            body.schedule.Execute(Refresh).Every(RefreshIntervalMs);

            return scroll;
        }

        private void Refresh()
        {
            if (_metrics == null || _completions == null)
            {
                return;
            }

            _completions.text = _metrics.TotalCompletions.ToString(CultureInfo.InvariantCulture);
            _successFail.text = string.Format(
                CultureInfo.InvariantCulture,
                "{0} / {1}",
                _metrics.SuccessfulCompletions,
                _metrics.FailedCompletions);
            _avgLatency.text = _metrics.AverageLatencyMs.ToString("0", CultureInfo.InvariantCulture) + " ms";
            _retries.text = _metrics.StructuredRetries.ToString(CultureInfo.InvariantCulture);
            _published.text = _metrics.CommandsPublished.ToString(CultureInfo.InvariantCulture);
            _lastSuccess.text = _metrics.TotalCompletions == 0
                ? "-"
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
            Dictionary<string, InMemoryAiOrchestrationMetrics.RoleMetrics> roles = _metrics.GetAllRoleMetrics();
            if (roles.Count == 0)
            {
                _rolesContainer.Add(HubPageWidgets.MakeRow("(no roles yet)", ""));
                return;
            }

            foreach (KeyValuePair<string, InMemoryAiOrchestrationMetrics.RoleMetrics> kvp in roles)
            {
                InMemoryAiOrchestrationMetrics.RoleMetrics rm = kvp.Value;
                string value = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}/{1} OK, {2} ms avg",
                    rm.Successes,
                    rm.Completions,
                    rm.AverageLatencyMs.ToString("0", CultureInfo.InvariantCulture));
                _rolesContainer.Add(HubPageWidgets.MakeRow(kvp.Key, value));
            }
        }
    }
}
