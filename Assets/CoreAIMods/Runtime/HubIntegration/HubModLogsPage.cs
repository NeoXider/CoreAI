using System;
using System.Collections.Generic;
using CoreAI.Hub;
using UnityEngine.UIElements;

namespace CoreAI.Ai.Hub
{
    /// <summary>
    /// UI Toolkit Hub page listing recent mod <c>report()</c>/<c>print()</c> output merged with recent
    /// Tick-time handler errors into a single chronological log, newest last. A mod-id filter narrows
    /// the view (substring match, same style as <see cref="HubModsPage"/>'s search box) and "Errors"/
    /// "Reports" toggles hide either source. Live-refreshes on <see cref="IHubModService.LogsChanged"/>
    /// (fired when the underlying runtime records a handler error or a report), marshalled onto the
    /// panel's scheduler like <c>CoreAiHubWindow.OnRegistryChanged</c>. Content is built lazily in
    /// <see cref="CreatePageContent"/>.
    /// </summary>
    public sealed class HubModLogsPage : IHubPage
    {
        /// <summary>Default registry id for the Mod Logs page.</summary>
        public const string DefaultPageId = "coreai.hub.modlogs";

        /// <summary>Maximum number of merged log lines rendered at once.</summary>
        private const int MaxDisplayedLines = 200;

        private readonly IHubModService _service;

        private VisualElement _root;
        private ScrollView _logScroll;
        private TextField _filterField;
        private Toggle _errorsToggle;
        private Toggle _reportsToggle;
        private string _filter = "";
        private bool _subscribed;

        /// <param name="service">VM-agnostic mod CRUD/query surface (built by <see cref="HubModsPages"/>).</param>
        /// <param name="order">Sort priority for the Hub tab (defaults to 350).</param>
        public HubModLogsPage(IHubModService service, int order = 350)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            Order = order;
        }

        /// <inheritdoc />
        public string PageId => DefaultPageId;

        /// <inheritdoc />
        public string DisplayName => "Logs";

        /// <inheritdoc />
        public int Order { get; }

        /// <inheritdoc />
        public Func<object> CreatePageContent => Build;

        /// <inheritdoc />
        public void OnActivated()
        {
            Subscribe();
            Refresh();
        }

        /// <inheritdoc />
        public void OnDeactivated()
        {
            Unsubscribe();
        }

        /// <inheritdoc />
        public void OnDestroyed()
        {
            Unsubscribe();
        }

        private object Build()
        {
            _root = new VisualElement { name = "coreai-hub-modlogs" };
            _root.style.flexGrow = 1f;
            _root.style.flexDirection = FlexDirection.Column;

            _root.Add(HubModWidgets.MakeTitle("Mod Logs"));

            if (!_service.IsSupported)
            {
                _root.Add(HubModWidgets.MakeNote(
                    "The Lua sandbox is not supported on this platform, so there are no mod logs here."));
            }

            VisualElement toolbar = new();
            toolbar.style.flexDirection = FlexDirection.Row;
            toolbar.style.flexWrap = Wrap.Wrap;
            toolbar.style.alignItems = Align.Center;
            toolbar.style.marginBottom = 6f;

            _filterField = new TextField { name = "coreai-hub-modlogs-filter" };
            _filterField.value = _filter;
            _filterField.style.flexGrow = 1f;
            _filterField.style.minWidth = 140f;
            _filterField.style.marginRight = 6f;
            _filterField.tooltip = "Filter by mod id";
            _filterField.RegisterValueChangedCallback(evt =>
            {
                _filter = evt.newValue ?? "";
                Refresh();
            });
            toolbar.Add(_filterField);

            _errorsToggle = new Toggle("Errors") { value = true };
            _errorsToggle.style.marginRight = 10f;
            _errorsToggle.RegisterValueChangedCallback(_ => Refresh());
            HubModWidgets.StyleToggleLabel(_errorsToggle);
            toolbar.Add(_errorsToggle);

            _reportsToggle = new Toggle("Reports") { value = true };
            _reportsToggle.style.marginRight = 10f;
            _reportsToggle.RegisterValueChangedCallback(_ => Refresh());
            HubModWidgets.StyleToggleLabel(_reportsToggle);
            toolbar.Add(_reportsToggle);

            toolbar.Add(HubModWidgets.MakeButton("Clear", ClearLogs));

            _root.Add(toolbar);

            _logScroll = new ScrollView(ScrollViewMode.Vertical) { name = "coreai-hub-modlogs-list" };
            _logScroll.style.flexGrow = 1f;
            _root.Add(_logScroll);

            Subscribe();
            Refresh();
            return _root;
        }

        private void ClearLogs()
        {
            try
            {
                _service.ClearErrors();
                _service.ClearReports();
            }
            catch (Exception)
            {
            }

            Refresh();
        }

        private void Refresh()
        {
            if (_logScroll == null)
            {
                return;
            }

            _logScroll.Clear();

            List<LogLine> lines = new();

            if (_errorsToggle == null || _errorsToggle.value)
            {
                CollectErrors(lines);
            }

            if (_reportsToggle == null || _reportsToggle.value)
            {
                CollectReports(lines);
            }

            string filter = string.IsNullOrWhiteSpace(_filter) ? null : _filter.Trim();
            if (filter != null)
            {
                lines.RemoveAll(line => !Contains(line.ModId, filter));
            }

            lines.Sort((a, b) => a.AtUtc.CompareTo(b.AtUtc));

            if (lines.Count > MaxDisplayedLines)
            {
                lines.RemoveRange(0, lines.Count - MaxDisplayedLines);
            }

            if (lines.Count == 0)
            {
                _logScroll.Add(HubModWidgets.MakeNote("No log entries yet."));
                return;
            }

            VisualElement lastLine = null;
            foreach (LogLine line in lines)
            {
                lastLine = BuildLine(line);
                _logScroll.Add(lastLine);
            }

            // WHY: Keep the newest entry in view after a refresh (there's no "user scrolled up" tracking yet,
            // so this always snaps to the bottom — acceptable since logs are read-mostly-live here).
            // Deferred one tick: ScrollTo needs the just-added element's layout resolved first.
            if (lastLine != null)
            {
                _logScroll.schedule.Execute(() =>
                {
                    // WHY: the periodic Refresh may have cleared and rebuilt the list before this deferred
                    // tick ran, so the captured line can already be detached — ScrollTo would then throw
                    // "not a child of the ScrollView content-container". Only scroll while it is still parented.
                    if (lastLine.parent == _logScroll.contentContainer)
                    {
                        _logScroll.ScrollTo(lastLine);
                    }
                });
            }
        }

        private void CollectErrors(List<LogLine> lines)
        {
            IReadOnlyList<LuaModHandlerError> errors;
            try
            {
                errors = _service.RecentErrorEntries();
            }
            catch (Exception ex)
            {
                _logScroll.Add(HubModWidgets.MakeNote($"Failed to list errors: {ex.Message}"));
                return;
            }

            foreach (LuaModHandlerError error in errors)
            {
                lines.Add(new LogLine(error.AtUtc, error.ModId, error.Error, true));
            }
        }

        private void CollectReports(List<LogLine> lines)
        {
            IReadOnlyList<LuaModReport> reports;
            try
            {
                reports = _service.RecentReports();
            }
            catch (Exception ex)
            {
                _logScroll.Add(HubModWidgets.MakeNote($"Failed to list reports: {ex.Message}"));
                return;
            }

            foreach (LuaModReport report in reports)
            {
                lines.Add(new LogLine(report.AtUtc, report.ModId, report.Message, false));
            }
        }

        private static VisualElement BuildLine(LogLine line)
        {
            // WHY: Two labels (muted prefix + coloured body) instead of one rich-text string: report/error
            // text comes from mod-authored content, so it must render as plain text rather than being
            // parsed for markup tags.
            VisualElement row = new() { name = "coreai-hub-modlog-line" };
            row.style.flexDirection = FlexDirection.Row;
            row.style.flexWrap = Wrap.Wrap;
            row.style.marginBottom = 2f;

            Label prefix = new($"[{line.AtUtc.ToLocalTime():HH:mm:ss}] [{line.ModId}] ");
            prefix.style.whiteSpace = WhiteSpace.Normal;
            prefix.style.fontSize = 12f;
            prefix.style.color = HubModWidgets.Muted;
            row.Add(prefix);

            Label message = new(line.Message);
            message.style.whiteSpace = WhiteSpace.Normal;
            message.style.fontSize = 12f;
            message.style.color = line.IsError ? HubModWidgets.Danger : HubModWidgets.Text;
            message.style.flexGrow = 1f;
            message.style.flexShrink = 1f;
            message.style.minWidth = 0f;
            row.Add(message);

            return row;
        }

        private static bool Contains(string haystack, string needle)
        {
            return !string.IsNullOrEmpty(haystack) &&
                   haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void OnLogsChanged()
        {
            if (_root == null)
            {
                return;
            }

            // WHY: LogsChanged may arrive off the UI thread (Tick-time handler errors / mod reports);
            // marshal the refresh onto the panel's scheduler so VisualElement mutations always run on
            // the main thread.
            _root.schedule.Execute(Refresh);
        }

        private void Subscribe()
        {
            if (_subscribed)
            {
                return;
            }

            _service.LogsChanged += OnLogsChanged;
            _subscribed = true;
        }

        private void Unsubscribe()
        {
            if (!_subscribed)
            {
                return;
            }

            _service.LogsChanged -= OnLogsChanged;
            _subscribed = false;
        }

        private readonly struct LogLine
        {
            public LogLine(DateTime atUtc, string modId, string message, bool isError)
            {
                AtUtc = atUtc;
                ModId = modId ?? "";
                Message = message ?? "";
                IsError = isError;
            }

            public DateTime AtUtc { get; }
            public string ModId { get; }
            public string Message { get; }
            public bool IsError { get; }
        }
    }
}
