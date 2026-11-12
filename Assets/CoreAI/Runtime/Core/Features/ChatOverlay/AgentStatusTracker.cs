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
        Retrying = 4,

        /// <summary>The model writes a tool call: its arguments stream and nothing shows yet.</summary>
        ComposingTool = 5
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
        private long _streamedCharacters;
        private int _composingCharacters;
        private string _shownOutputTokens = "";

        /// <summary>Creates a tracker that formats with <paramref name="options"/>.</summary>
        public AgentStatusTracker(ChatOverlayOptions options = null)
        {
            _options = options ?? new ChatOverlayOptions();
        }

        /// <summary>Current activity.</summary>
        public AgentStatusKind Kind { get; private set; }

        /// <summary>Tool the status is about (running, retrying or being written); empty otherwise.</summary>
        public string ToolName => Kind == AgentStatusKind.RunningTool || Kind == AgentStatusKind.Retrying ||
                                  Kind == AgentStatusKind.ComposingTool
            ? _subject
            : "";

        /// <summary>
        /// Argument characters of the tool call being written, as last reported; 0 in every other state.
        /// </summary>
        public int ComposingCharacters => Kind == AgentStatusKind.ComposingTool ? _composingCharacters : 0;

        /// <summary>
        /// Output tokens of the running turn so far: an estimate from the characters that arrived (answer,
        /// reasoning and tool-call arguments), 0 before any did. Kept after the turn ends until the next begins.
        /// </summary>
        public int OutputTokens { get; private set; }

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

        /// <summary>
        /// Optional text shown while NO turn runs: work that goes on in the background of the conversation
        /// (sub-agents still working after the chat agent answered). Empty hides the status line when idle.
        /// It is not touched by a turn beginning or ending; whoever set it clears it.
        /// </summary>
        public string BackgroundDetail { get; private set; } = "";

        /// <summary>Sets <see cref="BackgroundDetail"/>; empty clears it.</summary>
        public void SetBackgroundDetail(string detail)
        {
            string trimmed = detail?.Trim() ?? "";
            if (string.Equals(trimmed, BackgroundDetail, StringComparison.Ordinal))
            {
                return;
            }

            BackgroundDetail = trimmed;
            Version++;
        }

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
            _streamedCharacters = 0;
            _composingCharacters = 0;
            _shownOutputTokens = "";
            OutputTokens = 0;
            Version++;
        }

        /// <summary>
        /// The model is writing a call of <paramref name="toolName"/>; <paramref name="characters"/> is the
        /// size of the arguments so far in the current model response (it starts again with the next response).
        /// </summary>
        public void ToolComposing(string toolName, int characters)
        {
            if (!IsActive)
            {
                return;
            }

            int size = Math.Max(0, characters);
            if (size < _composingCharacters)
            {
                // WHY: a smaller size means the next response began; what the previous one wrote stays counted.
                _streamedCharacters += _composingCharacters;
            }

            _composingCharacters = size;
            RefreshOutputTokens();
            if (_runningTools.Count == 0 && Kind != AgentStatusKind.Retrying)
            {
                Set(AgentStatusKind.ComposingTool, Normalize(toolName), 0);
            }
        }

        /// <summary>
        /// Counts <paramref name="characters"/> of answer or reasoning text that arrived in the running turn
        /// towards <see cref="OutputTokens"/>.
        /// </summary>
        public void AddStreamedCharacters(int characters)
        {
            if (!IsActive || characters <= 0)
            {
                return;
            }

            _streamedCharacters += characters;
            RefreshOutputTokens();
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

        /// <summary>
        /// The status line text at <paramref name="now"/>; when idle, <see cref="BackgroundDetail"/> (empty
        /// unless background work was reported).
        /// </summary>
        public string Format(double now)
        {
            if (!IsActive)
            {
                return BackgroundDetail;
            }

            string status;
            switch (Kind)
            {
                case AgentStatusKind.RunningTool when Detail.Length > 0:
                case AgentStatusKind.ComposingTool when Detail.Length > 0:
                case AgentStatusKind.Thinking when Detail.Length > 0:
                    status = Detail;
                    break;
                case AgentStatusKind.ComposingTool:
                    status = ComposingToolText(_subject);
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

            string timed = _options.ElapsedFormat
                .Replace("{status}", status)
                .Replace("{elapsed}", ElapsedSeconds(now).ToString());
            if (OutputTokens <= 0 || string.IsNullOrEmpty(_options.OutputTokensFormat))
            {
                return timed;
            }

            return _options.OutputTokensFormat
                .Replace("{status}", timed)
                .Replace("{tokens}", FormatTokens(OutputTokens));
        }

        /// <summary>
        /// A token count as the status shows it: to the nearest ten below a thousand ("350"), in thousands with
        /// one decimal above ("1.2k"). The count is an estimate, so more digits would only flicker.
        /// </summary>
        public static string FormatTokens(int tokens)
        {
            if (tokens <= 0)
            {
                return "0";
            }

            if (tokens < 1000)
            {
                int rounded = Math.Max(10, (int)Math.Round(tokens / 10d, MidpointRounding.AwayFromZero) * 10);
                if (rounded < 1000)
                {
                    return rounded.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
            }

            return (tokens / 1000d).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "k";
        }

        private void RefreshOutputTokens()
        {
            double perToken = _options.CharactersPerOutputToken > 0d ? _options.CharactersPerOutputToken : 4d;
            OutputTokens = (int)Math.Min(int.MaxValue,
                Math.Ceiling((_streamedCharacters + _composingCharacters) / perToken));
            // WHY the shown text decides: the count moves with every chunk, and a version per chunk would make
            // every listener redraw for a number that reads the same.
            string shown = FormatTokens(OutputTokens);
            if (!string.Equals(shown, _shownOutputTokens, StringComparison.Ordinal))
            {
                _shownOutputTokens = shown;
                Version++;
            }
        }

        private string ComposingToolText(string tool)
        {
            IDictionary<string, string> custom = _options.ComposingToolTexts;
            if (custom != null && custom.TryGetValue(tool, out string text) && !string.IsNullOrEmpty(text))
            {
                return text;
            }

            return (_options.ComposingToolFormat ?? "{tool}").Replace("{tool}", tool);
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
