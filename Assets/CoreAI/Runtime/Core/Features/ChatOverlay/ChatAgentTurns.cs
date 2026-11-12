using System;
using System.Collections.Generic;
using System.Diagnostics;
using CoreAI.Ai;
using CoreAI.Logging;
using CoreAI.Messaging;

namespace CoreAI.ChatOverlay
{
    /// <summary>
    /// One snapshot of what the player's agent is doing, as a chat draws its status line. Fields that do not apply
    /// to <see cref="State"/> are empty or 0.
    /// </summary>
    public readonly struct ChatAgentStatus
    {
        /// <summary>Creates a snapshot; null strings become empty.</summary>
        public ChatAgentStatus(AgentStatusKind state, string tool, int characters, int tokens, int attempt,
            int maxAttempts, int step, string detail)
        {
            State = state;
            Tool = tool ?? "";
            Characters = characters;
            Tokens = tokens;
            Attempt = attempt;
            MaxAttempts = maxAttempts;
            Step = step;
            Detail = detail ?? "";
        }

        /// <summary>The activity: thinking, composing a tool call, running a tool, answering, retrying, idle.</summary>
        public AgentStatusKind State { get; }

        /// <summary>The tool while composing, running or retrying one.</summary>
        public string Tool { get; }

        /// <summary>While composing a tool call: the characters of its arguments written so far.</summary>
        public int Characters { get; }

        /// <summary>Output tokens of the turn so far (an estimate); 0 when unknown.</summary>
        public int Tokens { get; }

        /// <summary>The repeated attempt of <see cref="Tool"/> (1 is the first retry); 0 on a first attempt.</summary>
        public int Attempt { get; }

        /// <summary>How many attempts the pipeline allows; 0 when unknown.</summary>
        public int MaxAttempts { get; }

        /// <summary>1-based count of the tool calls the turn started; 0 before the first.</summary>
        public int Step { get; }

        /// <summary>The orchestrator's plan line ("builder: walls 2/5"); empty when none.</summary>
        public string Detail { get; }

        /// <summary>
        /// The one mapping point from the HUD tracker to the snapshot a chat reads: a state the tracker gains reaches
        /// every script chat through this method.
        /// </summary>
        public static ChatAgentStatus From(AgentStatusTracker tracker, int step)
        {
            if (tracker == null)
            {
                return default;
            }

            return new ChatAgentStatus(tracker.Kind, tracker.ToolName, tracker.ComposingCharacters, tracker.OutputTokens,
                tracker.RetryAttempt, 0, step, tracker.Detail);
        }

        /// <summary>True when every field equals <paramref name="other"/>'s.</summary>
        public bool SameAs(ChatAgentStatus other)
        {
            return State == other.State && Characters == other.Characters && Tokens == other.Tokens
                   && Attempt == other.Attempt && MaxAttempts == other.MaxAttempts && Step == other.Step
                   && string.Equals(Tool, other.Tool, StringComparison.Ordinal)
                   && string.Equals(Detail, other.Detail, StringComparison.Ordinal);
        }
    }

    /// <summary>How a turn of the player's agent ended.</summary>
    public enum ChatAgentTurnOutcome
    {
        /// <summary>The answer is complete.</summary>
        Completed = 0,

        /// <summary>A provider, transport or pipeline failure.</summary>
        Failed = 1,

        /// <summary>The turn made no progress within its deadline.</summary>
        TimedOut = 2,

        /// <summary>Somebody stopped the turn.</summary>
        Cancelled = 3
    }

    /// <summary>Whether a completed turn was tool calls alone, the model saying nothing.</summary>
    public enum ChatAgentToolOnly
    {
        /// <summary>The model said something (or nothing at all, without tools).</summary>
        None = 0,

        /// <summary>Every tool call succeeded.</summary>
        Done = 1,

        /// <summary>At least one tool call failed.</summary>
        Failed = 2
    }

    /// <summary>The end of one turn.</summary>
    public sealed class ChatAgentTurnResult
    {
        /// <summary>Creates the result; null strings become empty.</summary>
        public ChatAgentTurnResult(ChatAgentTurnOutcome outcome, string message, LlmErrorCode errorCode,
            ChatAgentToolOnly toolOnly, string toolSummary, bool anyText)
        {
            Outcome = outcome;
            Message = message ?? "";
            ErrorCode = errorCode;
            ToolOnly = toolOnly;
            ToolSummary = toolSummary ?? "";
            AnyText = anyText;
        }

        /// <summary>How the turn ended.</summary>
        public ChatAgentTurnOutcome Outcome { get; }

        /// <summary>The player-facing sentence of a failure (<see cref="LlmErrorPresentation"/>); empty otherwise.</summary>
        public string Message { get; }

        /// <summary>The typed failure category; <see cref="LlmErrorCode.None"/> on success.</summary>
        public LlmErrorCode ErrorCode { get; }

        /// <summary>Set when the turn was tool calls alone; the chat words that line itself.</summary>
        public ChatAgentToolOnly ToolOnly { get; }

        /// <summary>The orchestrator's sentence about a tool-only turn (tool names and errors), for logs.</summary>
        public string ToolSummary { get; }

        /// <summary>True when the turn showed any answer text.</summary>
        public bool AnyText { get; }
    }

    /// <summary>
    /// One running turn of the player's agent, reported as typed events on the chat's thread: visible answer text
    /// piece by piece (think blocks removed, a tool-only summary held back), every status change, and the end
    /// exactly once.
    /// </summary>
    public sealed class ChatAgentTurn : IChatOverlayTurnObserver
    {
        private readonly ChatAgentTurns _owner;
        private readonly ThinkBlockStreamFilter _think = new();
        private IChatOverlayTurn _handle;
        private bool _anyText;
        private bool _ended;
        private string _heldToolSummary;
        private bool _heldToolSummaryFailed;

        internal ChatAgentTurn(ChatAgentTurns owner, string roleId, string traceId)
        {
            _owner = owner;
            RoleId = roleId;
            TraceId = traceId;
        }

        /// <summary>Visible answer text: the new piece, and true when it starts a new message of the agent.</summary>
        public event Action<string, bool> TextAdded;

        /// <summary>Raised after every status change of this turn.</summary>
        public event Action<ChatAgentStatus> StatusChanged;

        /// <summary>Raised once when the turn ended.</summary>
        public event Action<ChatAgentTurnResult> Ended;

        /// <summary>The role that answers.</summary>
        public string RoleId { get; }

        /// <summary>The correlation id of the turn's tool events.</summary>
        public string TraceId { get; }

        /// <summary>True until the turn ended.</summary>
        public bool IsActive => !_ended;

        /// <summary>The status now.</summary>
        public ChatAgentStatus Status { get; private set; }

        /// <summary>Tool calls this turn started.</summary>
        internal int Step { get; set; }

        /// <summary>The result once ended; null before.</summary>
        public ChatAgentTurnResult Result { get; private set; }

        /// <summary>Stops the turn: the request is cancelled and the agent is asked to stop its work.</summary>
        public void Cancel()
        {
            if (_ended)
            {
                return;
            }

            Finish(new ChatAgentTurnResult(ChatAgentTurnOutcome.Cancelled, "", LlmErrorCode.Cancelled,
                ChatAgentToolOnly.None, "", _anyText));
            _handle?.Cancel();
            _owner.StopAgent(RoleId);
        }

        internal void AttachHandle(IChatOverlayTurn handle)
        {
            _handle = handle;
            if (_ended)
            {
                handle?.Cancel();
            }
        }

        internal void PublishStatus(ChatAgentStatus status)
        {
            if (_ended || status.SameAs(Status))
            {
                return;
            }

            Status = status;
            _owner.Raise(StatusChanged, status, "StatusChanged");
        }

        /// <inheritdoc />
        void IChatOverlayTurnObserver.OnChunk(LlmStreamChunk chunk)
        {
            if (_ended || chunk == null)
            {
                return;
            }

            if (!string.IsNullOrEmpty(chunk.Error) || chunk.ErrorCode != LlmErrorCode.None)
            {
                AppendVisible(_think.Flush(), false);
                Finish(FailureResult(ChatOverlayTurnFailure.FromErrorCode(
                    chunk.ErrorCode == LlmErrorCode.None ? LlmErrorCode.ProviderError : chunk.ErrorCode,
                    chunk.RetryAfterSeconds)));
                _handle?.Cancel();
                return;
            }

            AgentStatusTracker tracker = _owner.Tracker;
            tracker.AddStreamedCharacters((chunk.Text?.Length ?? 0) + (chunk.ReasoningText?.Length ?? 0));
            if (chunk.BufferedStreamingNoToolBinding)
            {
                if (chunk.BufferedStreamingUseToolProgressHint)
                {
                    tracker.MarkThinking();
                }

                _owner.PublishStatus(this);
                return;
            }

            if (!string.IsNullOrEmpty(chunk.Text))
            {
                if (!TryHoldToolSummary(chunk.Text))
                {
                    AppendVisible(_think.ProcessChunk(chunk.Text), chunk.StartsNewMessage);
                }
            }
            else if (!string.IsNullOrEmpty(chunk.ReasoningText) || chunk.GenerationInProgress)
            {
                if (chunk.ComposingToolCharacters > 0)
                {
                    tracker.ToolComposing(chunk.ComposingToolName, chunk.ComposingToolCharacters);
                }
                else
                {
                    tracker.MarkThinking();
                }
            }

            _owner.PublishStatus(this);
        }

        /// <inheritdoc />
        void IChatOverlayTurnObserver.OnCompleted()
        {
            if (_ended)
            {
                return;
            }

            AppendVisible(_think.Flush(), false);
            ChatAgentToolOnly toolOnly = _heldToolSummary == null
                ? ChatAgentToolOnly.None
                : _heldToolSummaryFailed ? ChatAgentToolOnly.Failed : ChatAgentToolOnly.Done;
            Finish(new ChatAgentTurnResult(ChatAgentTurnOutcome.Completed, "", LlmErrorCode.None, toolOnly,
                _heldToolSummary, _anyText));
        }

        /// <inheritdoc />
        void IChatOverlayTurnObserver.OnFailed(ChatOverlayTurnFailure failure)
        {
            if (_ended)
            {
                return;
            }

            AppendVisible(_think.Flush(), false);
            Finish(FailureResult(failure));
        }

        internal void Abandon()
        {
            _ended = true;
            _handle?.Cancel();
        }

        // WHY held to the end of the turn: the orchestrator writes this sentence before it reports how the turn ended;
        // when the turn then fails, the failure alone tells the player what happened (the overlay's rule).
        private bool TryHoldToolSummary(string text)
        {
            if (_anyText || !AiOrchestrator.IsToolOnlySummary(text, out bool failed))
            {
                return false;
            }

            _heldToolSummary = text;
            _heldToolSummaryFailed = failed;
            _owner.Log.Info($"[ChatAgentTurns] A turn of tool calls alone: {text}", LogTag.Llm);
            _owner.Tracker.MarkThinking();
            return true;
        }

        private void AppendVisible(string visible, bool startsNewMessage)
        {
            if (string.IsNullOrEmpty(visible))
            {
                return;
            }

            if (!_anyText)
            {
                visible = visible.TrimStart();
                if (visible.Length == 0)
                {
                    return;
                }

                startsNewMessage = false;
            }

            _anyText = true;
            _owner.Tracker.MarkAnswering();
            _owner.Raise(TextAdded, visible, startsNewMessage, "TextAdded");
        }

        private ChatAgentTurnResult FailureResult(ChatOverlayTurnFailure failure)
        {
            ChatAgentTurnOutcome outcome = failure.Kind switch
            {
                ChatOverlayTurnFailureKind.Cancelled => ChatAgentTurnOutcome.Cancelled,
                ChatOverlayTurnFailureKind.Timeout => ChatAgentTurnOutcome.TimedOut,
                _ => ChatAgentTurnOutcome.Failed
            };
            return new ChatAgentTurnResult(outcome, failure.Message, failure.ErrorCode, ChatAgentToolOnly.None, "",
                _anyText);
        }

        private void Finish(ChatAgentTurnResult result)
        {
            if (_ended)
            {
                return;
            }

            _ended = true;
            Result = result;
            _owner.OnTurnEnded(this);
            _owner.Raise(Ended, result, "Ended");
        }
    }

    /// <summary>
    /// The player's own agent for a chat surface other than the overlay (a script's chat): one turn at a time,
    /// sent through the same <see cref="IChatOverlayTurnSender"/> the overlay uses (one orchestrator, one per-role
    /// history), with the overlay's role and model selectors, and the agent's status as a stream of changes. Tool
    /// events may arrive on any thread; they are applied on <see cref="Tick"/>, which the surface calls once per
    /// frame. Everything else runs on the thread that owns the chat.
    /// </summary>
    public sealed class ChatAgentTurns : IDisposable
    {
        private readonly IChatOverlayTurnSender _sender;
        private readonly IChatAgentSelector _agents;
        private readonly IAgentToolEventSource _toolEvents;
        private readonly ChatOverlayOptions _options;
        private readonly Func<double> _clock;
        private readonly object _gate = new();
        private readonly List<(int Phase, string TraceId, string Tool)> _pending = new();
        private readonly List<(int Phase, string TraceId, string Tool)> _drained = new();
        private ChatAgentTurn _current;
        private bool _disposed;

        /// <summary>Creates the service; <paramref name="models"/>, <paramref name="toolEvents"/> and the rest may be null.</summary>
        public ChatAgentTurns(IChatOverlayTurnSender sender, IChatAgentSelector agents, IChatModelSelector models = null,
            IAgentToolEventSource toolEvents = null, ChatOverlayOptions options = null, ILog log = null,
            Func<double> clock = null)
        {
            _sender = sender ?? throw new ArgumentNullException(nameof(sender));
            _agents = agents ?? throw new ArgumentNullException(nameof(agents));
            Models = models;
            _toolEvents = toolEvents;
            _options = options ?? new ChatOverlayOptions();
            Log = log ?? NullLog.Instance;
            Stopwatch watch = Stopwatch.StartNew();
            _clock = clock ?? (() => watch.Elapsed.TotalSeconds);
            Tracker = new AgentStatusTracker(_options);
            if (_toolEvents != null)
            {
                _toolEvents.ToolCallStarted += OnToolStarted;
                _toolEvents.ToolCallCompleted += OnToolCompleted;
                _toolEvents.ToolCallFailed += OnToolFailed;
            }
        }

        /// <summary>Raised when a turn begins.</summary>
        public event Action<ChatAgentTurn> TurnStarted;

        /// <summary>False when no AI service is connected.</summary>
        public bool IsAvailable => !_disposed && _sender.IsAvailable;

        /// <summary>True while a turn runs.</summary>
        public bool IsBusy => _current != null;

        /// <summary>The running turn, or null.</summary>
        public ChatAgentTurn Current => _current;

        /// <summary>The role that answers the next turn.</summary>
        public string ActiveRoleId => _agents.ActiveRoleId ?? "";

        /// <summary>The roles a chat may switch to.</summary>
        public IReadOnlyList<string> AvailableRoles => _agents.AvailableRoles ?? Array.Empty<string>();

        /// <summary>The model-profile seam of the active role; null when the host has none.</summary>
        public IChatModelSelector Models { get; }

        internal AgentStatusTracker Tracker { get; }

        internal ILog Log { get; }

        /// <summary>Switches the role that answers; false (and the current role) for an unknown one.</summary>
        public bool TrySelectRole(string roleId, out string selectedRoleId)
        {
            return _agents.TrySelect(roleId, out selectedRoleId);
        }

        /// <summary>
        /// Starts a turn of <paramref name="roleOverride"/> (empty: the active role) with <paramref name="text"/>.
        /// Throws <see cref="InvalidOperationException"/> while a turn runs or when no AI is connected, and
        /// <see cref="ArgumentException"/> for an empty line. A model failure arrives as the turn's end, never as a throw.
        /// </summary>
        public ChatAgentTurn Start(string text, string roleOverride = "")
        {
            string line = (text ?? "").Trim();
            if (line.Length == 0)
            {
                throw new ArgumentException("A turn needs a non-empty line.", nameof(text));
            }

            if (_disposed || !_sender.IsAvailable)
            {
                throw new InvalidOperationException(_options.ServiceUnavailableText);
            }

            if (_current != null)
            {
                throw new InvalidOperationException(_options.WithPrefix(_options.BusyText));
            }

            if (_options.MaxInputLength > 0 && line.Length > _options.MaxInputLength)
            {
                line = line.Substring(0, _options.MaxInputLength);
            }

            string roleId = string.IsNullOrWhiteSpace(roleOverride) ? ActiveRoleId : roleOverride.Trim();
            ChatAgentTurn turn = new(this, roleId, Guid.NewGuid().ToString("N"));
            _current = turn;
            Tracker.BeginTurn(_clock());
            PublishStatus(turn);
            Raise(TurnStarted, turn, "TurnStarted");
            ChatOverlayTurnRequest request = new()
            {
                Text = line,
                RoleId = roleId,
                TraceId = turn.TraceId,
                StreamingOverride = _options.EnableStreaming ? (bool?)null : false
            };

            try
            {
                turn.AttachHandle(_sender.Send(request, turn));
            }
            catch (Exception ex)
            {
                Log.Error($"[ChatAgentTurns] The turn sender threw: {ex}", LogTag.Llm);
                ((IChatOverlayTurnObserver)turn).OnFailed(new ChatOverlayTurnFailure(ChatOverlayTurnFailureKind.Error,
                    LlmErrorPresentation.ToUserMessage(ex), LlmErrorCode.ProviderError, ex));
            }

            return turn;
        }

        /// <summary>Stops the running turn; false when none runs.</summary>
        public bool StopCurrent()
        {
            ChatAgentTurn turn = _current;
            if (turn == null)
            {
                return false;
            }

            turn.Cancel();
            return true;
        }

        /// <summary>Applies queued tool events to the running turn's status; call once per frame on the chat's thread.</summary>
        public void Tick()
        {
            lock (_gate)
            {
                if (_pending.Count == 0)
                {
                    return;
                }

                _drained.AddRange(_pending);
                _pending.Clear();
            }

            ChatAgentTurn turn = _current;
            foreach ((int phase, string traceId, string tool) in _drained)
            {
                if (turn == null || !string.Equals((traceId ?? "").Trim(), turn.TraceId, StringComparison.Ordinal))
                {
                    continue;
                }

                switch (phase)
                {
                    case 0:
                        turn.Step++;
                        Tracker.ToolStarted(tool);
                        break;
                    case 1:
                        Tracker.ToolCompleted(tool);
                        break;
                    default:
                        Tracker.ToolFailed(tool);
                        break;
                }

                PublishStatus(turn);
            }

            _drained.Clear();
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            ChatAgentTurn turn = _current;
            _current = null;
            turn?.Abandon();
            Tracker.EndTurn();
            if (_toolEvents != null)
            {
                _toolEvents.ToolCallStarted -= OnToolStarted;
                _toolEvents.ToolCallCompleted -= OnToolCompleted;
                _toolEvents.ToolCallFailed -= OnToolFailed;
            }
        }

        internal void PublishStatus(ChatAgentTurn turn)
        {
            if (ReferenceEquals(turn, _current))
            {
                turn.PublishStatus(ChatAgentStatus.From(Tracker, turn.Step));
            }
        }

        internal void OnTurnEnded(ChatAgentTurn turn)
        {
            if (ReferenceEquals(turn, _current))
            {
                _current = null;
                Tracker.EndTurn();
            }
        }

        internal void StopAgent(string roleId)
        {
            try
            {
                _sender.StopAgent(roleId);
            }
            catch (Exception ex)
            {
                Log.Warn($"[ChatAgentTurns] StopAgent({roleId}) threw: {ex.Message}", LogTag.Llm);
            }
        }

        internal void Raise<T>(Action<T> handler, T value, string name)
        {
            try
            {
                handler?.Invoke(value);
            }
            catch (Exception ex)
            {
                Log.Error($"[ChatAgentTurns] A {name} handler threw: {ex}", LogTag.Core);
            }
        }

        internal void Raise<T1, T2>(Action<T1, T2> handler, T1 first, T2 second, string name)
        {
            try
            {
                handler?.Invoke(first, second);
            }
            catch (Exception ex)
            {
                Log.Error($"[ChatAgentTurns] A {name} handler threw: {ex}", LogTag.Core);
            }
        }

        private void OnToolStarted(LlmToolCallStarted evt)
        {
            Queue(0, evt.TraceId, evt.ToolName);
        }

        private void OnToolCompleted(LlmToolCallCompleted evt)
        {
            Queue(1, evt.TraceId, evt.ToolName);
        }

        private void OnToolFailed(LlmToolCallFailed evt)
        {
            Queue(2, evt.TraceId, evt.ToolName);
        }

        private void Queue(int phase, string traceId, string tool)
        {
            lock (_gate)
            {
                _pending.Add((phase, traceId, tool));
            }
        }
    }
}
