using System;
using System.Collections.Generic;

namespace CoreAI.ChatOverlay
{
    /// <summary>What the agent is doing, as the status line shows it.</summary>
    public enum AgentStatusKind
    {
        /// <summary>No turn is running; the status line is hidden.</summary>
        Idle = 0,

        /// <summary>The model works and no tool runs.</summary>
        Thinking = 1,

        /// <summary>The answer streams into the feed.</summary>
        Answering = 2,

        /// <summary>A tool runs.</summary>
        RunningTool = 3,

        /// <summary>A tool or request failed and the agent is about to try again.</summary>
        Retrying = 4
    }

    /// <summary>
    /// Engine-free state of the agent HUD: one status per turn, fed by tool-call events and stream progress,
    /// cleared when the turn ends (done, error or cancel). <see cref="Version"/> changes on every transition so a
    /// view re-renders only when something changed.
    /// </summary>
    public sealed class AgentStatusTracker
    {
        private readonly ChatOverlayOptions _options;
        private readonly List<string> _runningTools = new();
        private readonly Dictionary<string, int> _failures = new(StringComparer.OrdinalIgnoreCase);
        private string _subject = "";

        /// <summary>Creates a tracker that formats with <paramref name="options"/>.</summary>
        public AgentStatusTracker(ChatOverlayOptions options = null)
        {
            _options = options ?? new ChatOverlayOptions();
        }

        /// <summary>Current activity.</summary>
        public AgentStatusKind Kind { get; private set; }

        /// <summary>Tool the status is about (running or retrying); empty otherwise.</summary>
        public string ToolName => Kind == AgentStatusKind.RunningTool || Kind == AgentStatusKind.Retrying ? _subject : "";

        /// <summary>Repeated attempt number of <see cref="ToolName"/>; 0 for the first attempt.</summary>
        public int RetryAttempt { get; private set; }

        /// <summary>True while a turn runs.</summary>
        public bool IsActive => Kind != AgentStatusKind.Idle;

        /// <summary>Chat clock time the running turn began.</summary>
        public double TurnStartedAt { get; private set; }

        /// <summary>Increases on every change of the status.</summary>
        public int Version { get; private set; }

        /// <summary>
        /// Optional text another component sets for the running turn (orchestrator mode writes the plan line,
        /// "builder: walls 2/5"). While set it replaces the thinking and running-tool texts; answering and
        /// retrying keep their own. Cleared when a turn begins or ends.
        /// </summary>
        public string Detail { get; private set; } = "";

        /// <summary>Sets <see cref="Detail"/> while a turn runs; empty clears it.</summary>
        public void SetDetail(string detail)
        {
            string trimmed = IsActive ? detail?.Trim() ?? "" : "";
            if (string.Equals(trimmed, Detail, StringComparison.Ordinal))
            {
                return;
            }

            Detail = trimmed;
            Version++;
        }

        /// <summary>Starts a turn: the status becomes "thinking" and the elapsed time counts from <paramref name="now"/>.</summary>
        public void BeginTurn(double now)
        {
            _runningTools.Clear();
            _failures.Clear();
            TurnStartedAt = now;
            Kind = AgentStatusKind.Thinking;
            _subject = "";
            RetryAttempt = 0;
            Detail = "";
            Version++;
        }

        /// <summary>The model works without visible output (reasoning, a progress hint).</summary>
        public void MarkThinking()
        {
            if (IsActive && _runningTools.Count == 0 && Kind != AgentStatusKind.Retrying)
            {
                Set(AgentStatusKind.Thinking, "", 0);
            }
        }

        /// <summary>Visible answer text arrived.</summary>
        public void MarkAnswering()
        {
            if (IsActive && _runningTools.Count == 0)
            {
                Set(AgentStatusKind.Answering, "", 0);
            }
        }

        /// <summary>A tool of this turn started.</summary>
        public void ToolStarted(string toolName)
        {
            if (!IsActive)
            {
                return;
            }

            string tool = Normalize(toolName);
            _runningTools.Add(tool);
            _failures.TryGetValue(tool, out int failures);
            Set(AgentStatusKind.RunningTool, tool, failures);
        }

        /// <summary>A tool of this turn finished successfully.</summary>
        public void ToolCompleted(string toolName)
        {
            if (!IsActive)
            {
                return;
            }

            string tool = Normalize(toolName);
            RemoveRunning(tool);
            _failures.Remove(tool);
            ShowRemainingOrThinking();
        }

        /// <summary>A tool of this turn failed; the status says the agent will try again.</summary>
        public void ToolFailed(string toolName)
        {
            if (!IsActive)
            {
                return;
            }

            string tool = Normalize(toolName);
            RemoveRunning(tool);
            _failures.TryGetValue(tool, out int failures);
            failures++;
            _failures[tool] = failures;
            if (_runningTools.Count > 0)
            {
                ShowRemainingOrThinking();
                return;
            }

            Set(AgentStatusKind.Retrying, tool, failures);
        }

        /// <summary>
        /// A retry reported by something other than a tool event (a request retry, a host hook):
        /// <paramref name="subject"/> is shown with the retrying format.
        /// </summary>
        public void ReportRetry(string subject, int attempt)
        {
            if (IsActive)
            {
                Set(AgentStatusKind.Retrying, Normalize(subject), Math.Max(1, attempt));
            }
        }

        /// <summary>Ends the turn (done, error or cancel); the status line clears.</summary>
        public void EndTurn()
        {
            _runningTools.Clear();
            _failures.Clear();
            Detail = "";
            Set(AgentStatusKind.Idle, "", 0);
        }

        /// <summary>Whole seconds since the turn began; -1 when idle.</summary>
        public int ElapsedSeconds(double now)
        {
            return IsActive ? (int)Math.Max(0d, Math.Floor(now - TurnStartedAt)) : -1;
        }

        /// <summary>The status line text at <paramref name="now"/>; empty when idle.</summary>
        public string Format(double now)
        {
            if (!IsActive)
            {
                return "";
            }

            string status;
            switch (Kind)
            {
                case AgentStatusKind.RunningTool when Detail.Length > 0:
                case AgentStatusKind.Thinking when Detail.Length > 0:
                    status = Detail;
                    break;
                case AgentStatusKind.RunningTool:
                    status = RunningToolText(_subject);
                    if (RetryAttempt > 0)
                    {
                        status = (_options.RetryAttemptFormat ?? "{status}")
                            .Replace("{status}", status)
                            .Replace("{attempt}", RetryAttempt.ToString());
                    }

                    break;
                case AgentStatusKind.Retrying:
                    status = (_options.RetryingToolFormat ?? "").Replace("{tool}", _subject);
                    break;
                case AgentStatusKind.Answering:
                    status = _options.AnsweringText ?? "";
                    break;
                default:
                    status = _options.ThinkingText ?? "";
                    break;
            }

            double elapsed = now - TurnStartedAt;
            if (elapsed < _options.ShowElapsedAfterSeconds || string.IsNullOrEmpty(_options.ElapsedFormat))
            {
                return status;
            }

            return _options.ElapsedFormat
                .Replace("{status}", status)
                .Replace("{elapsed}", ElapsedSeconds(now).ToString());
        }

        private string RunningToolText(string tool)
        {
            IDictionary<string, string> custom = _options.ToolStatusTexts;
            if (custom != null && custom.TryGetValue(tool, out string text) && !string.IsNullOrEmpty(text))
            {
                return text;
            }

            return (_options.RunningToolFormat ?? "{tool}").Replace("{tool}", tool);
        }

        private void ShowRemainingOrThinking()
        {
            if (_runningTools.Count > 0)
            {
                string tool = _runningTools[_runningTools.Count - 1];
                _failures.TryGetValue(tool, out int failures);
                Set(AgentStatusKind.RunningTool, tool, failures);
                return;
            }

            Set(AgentStatusKind.Thinking, "", 0);
        }

        private void RemoveRunning(string tool)
        {
            for (int i = _runningTools.Count - 1; i >= 0; i--)
            {
                if (string.Equals(_runningTools[i], tool, StringComparison.OrdinalIgnoreCase))
                {
                    _runningTools.RemoveAt(i);
                    return;
                }
            }
        }

        private void Set(AgentStatusKind kind, string subject, int attempt)
        {
            if (Kind == kind && RetryAttempt == attempt && string.Equals(_subject, subject, StringComparison.Ordinal))
            {
                return;
            }

            Kind = kind;
            _subject = subject ?? "";
            RetryAttempt = attempt;
            Version++;
        }

        private static string Normalize(string toolName)
        {
            return string.IsNullOrWhiteSpace(toolName) ? "tool" : toolName.Trim();
        }
    }
}
