using System;
using System.Collections.Generic;
using System.Text;
using CoreAI.Ai;
using CoreAI.Logging;
using CoreAI.Messaging;

namespace CoreAI.ChatOverlay
{
    /// <summary>
    /// Engine-free core of the in-world chat: the input state, the feed, slash-command routing, the agent turn
    /// and the agent HUD. It owns no UI; it drives an <see cref="IChatOverlayView"/> and sends through an
    /// <see cref="IChatOverlayTurnSender"/>. Everything runs on the thread that constructed it (the Unity main
    /// thread); tool events and messages from other threads are queued and applied on <see cref="Tick"/>.
    /// A plain line the player types is offered to the pre-send hooks (<see cref="AddPreSendHook"/>) first; only a
    /// line no hook takes reaches the agent.
    /// </summary>
    public sealed class ChatOverlayController : IChatOverlayInputState, IChatOverlayMessages, IChatTurnControl,
        IChatOverlayPreSendHooks, IChatOverlayPlayerLineRouter, IDisposable
    {
        private readonly ChatOverlayOptions _options;
        private readonly IChatOverlayView _view;
        private readonly IChatOverlayTurnSender _sender;
        private readonly IChatAgentSelector _agents;
        private readonly IChatOverlayClock _clock;
        private readonly IAgentToolEventSource _toolEvents;
        private readonly ILog _log;
        private readonly ChatCommandDispatcher _dispatcher;
        private readonly int _ownerThreadId;
        private readonly object _queueGate = new();
        private readonly List<PendingToolEvent> _pendingToolEvents = new();
        private readonly List<PendingToolEvent> _drainedToolEvents = new();
        private readonly List<PendingMessage> _pendingMessages = new();
        private readonly List<PendingMessage> _drainedMessages = new();
        private readonly HashSet<ChatFeedLine> _dirtyLines = new();
        private readonly List<ChatFeedLine> _dirtyScratch = new();
        private readonly Dictionary<long, bool> _shownVisible = new();
        private readonly List<PreSendHookRegistration> _preSendHooks = new();
        private bool _inputOpen;
        private bool _closeReleasePending;
        private bool _publishedOpen;
        private int _pushedStatusVersion = -1;
        private int _pushedStatusSecond = -1;
        private string _pushedStatusText = "";
        private TurnState _turn;
        private bool _disposed;

        /// <summary>Creates the controller and subscribes to the view and the tool events.</summary>
        public ChatOverlayController(
            ChatOverlayOptions options,
            IChatOverlayView view,
            IChatOverlayTurnSender sender,
            IChatCommandRegistry commands,
            IChatAgentSelector agents,
            IChatOverlayClock clock,
            IAgentToolEventSource toolEvents = null,
            ILog log = null)
        {
            _options = options ?? new ChatOverlayOptions();
            _view = view ?? throw new ArgumentNullException(nameof(view));
            _sender = sender ?? throw new ArgumentNullException(nameof(sender));
            Commands = commands ?? throw new ArgumentNullException(nameof(commands));
            _agents = agents ?? throw new ArgumentNullException(nameof(agents));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _toolEvents = toolEvents;
            _log = log ?? NullLog.Instance;
            _ownerThreadId = Environment.CurrentManagedThreadId;
            _dispatcher = new ChatCommandDispatcher(commands, _options, this, _log);
            Feed = new ChatFeed(_options.MaxFeedLines, _options.MaxLineCharacters);
            Status = new AgentStatusTracker(_options);

            Feed.LineAdded += HandleLineAdded;
            Feed.LineChanged += HandleLineChanged;
            Feed.LineRemoved += HandleLineRemoved;
            _view.Submitted += Submit;
            _view.CloseRequested += CloseInput;
            if (_toolEvents != null)
            {
                _toolEvents.ToolCallStarted += HandleToolStarted;
                _toolEvents.ToolCallCompleted += HandleToolCompleted;
                _toolEvents.ToolCallFailed += HandleToolFailed;
            }
        }

        /// <inheritdoc />
        public event Action<bool> InputOpenChanged;

        /// <summary>The lines of the chat.</summary>
        public ChatFeed Feed { get; }

        /// <summary>The agent HUD state.</summary>
        public AgentStatusTracker Status { get; }

        /// <summary>The commands this chat understands.</summary>
        public IChatCommandRegistry Commands { get; }

        /// <summary>The options this chat was built with.</summary>
        public ChatOverlayOptions Options => _options;

        /// <summary>The role that answers the next message.</summary>
        public string ActiveRoleId => _agents.ActiveRoleId;

        /// <inheritdoc />
        public bool IsInputOpen => _inputOpen || _closeReleasePending;

        /// <summary>True only while the input is shown; unlike <see cref="IsInputOpen"/> it drops at once on close.</summary>
        public bool IsInputVisible => _inputOpen;

        /// <inheritdoc />
        public bool IsTurnActive => _turn != null;

        /// <summary>Trace id of the running turn; empty when idle.</summary>
        public string ActiveTraceId => _turn?.TraceId ?? "";

        /// <summary>Opens the input with <paramref name="prefill"/> in it; reopening replaces the text.</summary>
        public void OpenInput(string prefill = "")
        {
            if (_disposed)
            {
                return;
            }

            _inputOpen = true;
            _closeReleasePending = false;
            _view.SetInputOpen(true, prefill ?? "");
            RefreshVisibility(_clock.NowSeconds);
            PublishInputState();
        }

        /// <summary>Opens the input with the command prefix already typed.</summary>
        public void OpenCommandInput()
        {
            OpenInput(_options.CommandPrefix.ToString());
        }

        /// <summary>Closes the input; game keys stay blocked until the next <see cref="Tick"/>.</summary>
        public void CloseInput()
        {
            if (_disposed || !_inputOpen)
            {
                return;
            }

            _inputOpen = false;
            _closeReleasePending = true;
            _view.SetInputOpen(false, "");
            RefreshVisibility(_clock.NowSeconds);
        }

        /// <summary>
        /// Handles one submitted line as the Enter key does: commands run locally, plain text goes to the agent.
        /// An empty line only closes the input.
        /// </summary>
        public void Submit(string text)
        {
            if (_disposed)
            {
                return;
            }

            string trimmed = (text ?? "").Trim();
            if (trimmed.Length == 0 || _options.CloseInputOnSubmit)
            {
                CloseInput();
            }

            if (trimmed.Length == 0)
            {
                return;
            }

            if (_options.MaxInputLength > 0 && trimmed.Length > _options.MaxInputLength)
            {
                trimmed = trimmed.Substring(0, _options.MaxInputLength);
            }

            ChatDispatchResult result = _dispatcher.Dispatch(trimmed);
            double now = _clock.NowSeconds;
            switch (result.Kind)
            {
                case ChatDispatchKind.Handled:
                    if (result.Reply.Length > 0)
                    {
                        Feed.Add(ChatFeedLineKind.Command, "", result.Reply, now);
                    }

                    break;
                case ChatDispatchKind.Failed:
                case ChatDispatchKind.UnknownCommand:
                    Feed.Add(ChatFeedLineKind.Error, "", result.Reply, now);
                    break;
                case ChatDispatchKind.SendToModel:
                    if (result.Reply.Length > 0)
                    {
                        Feed.Add(ChatFeedLineKind.Command, "", result.Reply, now);
                    }

                    bool playerLineShown = false;
                    if (result.CommandName.Length == 0 && _preSendHooks.Count > 0)
                    {
                        // WHY the line is shown before the hooks run: an NPC that answers at once (a fallback
                        // line without a model) must speak after the line it answers, not before it.
                        Feed.Add(ChatFeedLineKind.Player, _options.PlayerSpeakerName, result.ModelText, now);
                        playerLineShown = true;
                        if (OfferToPreSendHooks(new ChatOverlayPlayerLine(result.ModelText,
                                _options.PlayerSpeakerName, now)))
                        {
                            break;
                        }
                    }

                    SendToModel(result.ModelText, result.RoleId, playerLineShown);
                    break;
            }
        }

        /// <inheritdoc />
        public IDisposable AddPreSendHook(IChatOverlayPreSendHook hook)
        {
            if (hook == null)
            {
                throw new ArgumentNullException(nameof(hook));
            }

            PreSendHookRegistration registration = new(this, hook);
            if (!_disposed)
            {
                _preSendHooks.Add(registration);
            }

            return registration;
        }

        /// <inheritdoc />
        public bool OfferPlayerLine(string text)
        {
            string trimmed = (text ?? "").Trim();
            if (_disposed || trimmed.Length == 0 || _preSendHooks.Count == 0)
            {
                return false;
            }

            if (_options.MaxInputLength > 0 && trimmed.Length > _options.MaxInputLength)
            {
                trimmed = trimmed.Substring(0, _options.MaxInputLength);
            }

            return OfferToPreSendHooks(new ChatOverlayPlayerLine(trimmed, _options.PlayerSpeakerName,
                _clock.NowSeconds));
        }

        /// <inheritdoc />
        public bool StopActiveTurn()
        {
            TurnState turn = _turn;
            if (turn == null)
            {
                return false;
            }

            turn.Stop();
            return true;
        }

        /// <summary>
        /// Starts a turn the host asks for instead of the player (<see cref="ChatOverlayHostTurnRequest"/>): it takes the
        /// chat's turn slot, so the status line follows it and <c>/stop</c> stops it. False, with no callback, when a turn
        /// already runs, no AI service is connected, the request has no text or the chat is disposed; the host tries
        /// again later.
        /// </summary>
        public bool TryStartHostTurn(ChatOverlayHostTurnRequest request)
        {
            if (_disposed || request == null || string.IsNullOrWhiteSpace(request.Text) || _turn != null ||
                !_sender.IsAvailable)
            {
                return false;
            }

            double now = _clock.NowSeconds;
            if (!string.IsNullOrWhiteSpace(request.Announcement))
            {
                Feed.Add(ChatFeedLineKind.System, "", request.Announcement.Trim(), now);
            }

            string roleId = string.IsNullOrWhiteSpace(request.RoleId) ? _agents.ActiveRoleId : request.RoleId.Trim();
            string traceId = string.IsNullOrWhiteSpace(request.TraceId)
                ? Guid.NewGuid().ToString("N")
                : request.TraceId.Trim();
            TurnState turn = new(this, roleId, traceId);
            _turn = turn;
            Status.BeginTurn(now);
            PushStatus(now);
            HostTurnObserver observer = new(this, turn, request);
            ChatOverlayTurnRequest send = new()
            {
                Text = request.Text,
                RoleId = roleId,
                TraceId = traceId,
                StreamingOverride = _options.EnableStreaming ? (bool?)null : false,
                HistoryMode = request.HistoryMode
            };

            try
            {
                turn.AttachHandle(_sender.Send(send, observer));
            }
            catch (Exception ex)
            {
                _log.Error($"[ChatOverlay] The turn sender threw on a host turn: {ex}", LogTag.Llm);
                observer.OnFailed(new ChatOverlayTurnFailure(ChatOverlayTurnFailureKind.Error,
                    LlmErrorPresentation.ToUserMessage(ex), LlmErrorCode.ProviderError, ex));
            }

            return true;
        }

        /// <inheritdoc />
        public void PostSystemMessage(string text)
        {
            PostMessage("", text, ChatFeedLineKind.System);
        }

        /// <inheritdoc />
        public void PostMessage(string speaker, string text, ChatFeedLineKind kind = ChatFeedLineKind.System)
        {
            if (_disposed || string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            if (Environment.CurrentManagedThreadId != _ownerThreadId)
            {
                lock (_queueGate)
                {
                    _pendingMessages.Add(new PendingMessage(speaker, text, kind));
                }

                return;
            }

            Feed.Add(kind, speaker, text, _clock.NowSeconds);
        }

        /// <summary>
        /// Applies queued tool events and messages, renders changed lines once per tick, fades lines, refreshes
        /// the status line and releases the keyboard after a close. Call once per frame.
        /// </summary>
        public void Tick()
        {
            if (_disposed)
            {
                return;
            }

            double now = _clock.NowSeconds;
            DrainMessages(now);
            DrainToolEvents();
            FlushDirtyLines();
            RefreshVisibility(now);
            PushStatus(now);
            if (_closeReleasePending)
            {
                _closeReleasePending = false;
                PublishInputState();
            }
        }

        /// <summary>Cancels the running turn without a feed line and detaches from the view and events.</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            TurnState turn = _turn;
            _turn = null;
            turn?.Abandon();
            _disposed = true;
            Feed.LineAdded -= HandleLineAdded;
            Feed.LineChanged -= HandleLineChanged;
            Feed.LineRemoved -= HandleLineRemoved;
            _view.Submitted -= Submit;
            _view.CloseRequested -= CloseInput;
            if (_toolEvents != null)
            {
                _toolEvents.ToolCallStarted -= HandleToolStarted;
                _toolEvents.ToolCallCompleted -= HandleToolCompleted;
                _toolEvents.ToolCallFailed -= HandleToolFailed;
            }

            _inputOpen = false;
            _closeReleasePending = false;
            _preSendHooks.Clear();
            PublishInputState();
        }

        private bool OfferToPreSendHooks(ChatOverlayPlayerLine line)
        {
            PreSendHookRegistration[] hooks = _preSendHooks.ToArray();
            foreach (PreSendHookRegistration registration in hooks)
            {
                if (!registration.Active)
                {
                    continue;
                }

                try
                {
                    if (registration.Hook.TryTake(line))
                    {
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    // WHY skipped rather than rethrown: a broken hook must never swallow the player's line; the
                    // next hook, or the agent, still gets it.
                    _log.Error($"[ChatOverlay] Pre-send hook {registration.Hook.GetType().Name} threw: {ex}",
                        LogTag.Core);
                }
            }

            return false;
        }

        private void SendToModel(string text, string roleOverride, bool playerLineShown = false)
        {
            double now = _clock.NowSeconds;
            if (!_sender.IsAvailable)
            {
                if (!playerLineShown)
                {
                    Feed.Add(ChatFeedLineKind.Player, _options.PlayerSpeakerName, text, now);
                }

                Feed.Add(ChatFeedLineKind.Error, "", _options.ServiceUnavailableText, now);
                return;
            }

            if (_turn != null)
            {
                Feed.Add(ChatFeedLineKind.Error, "", _options.WithPrefix(_options.BusyText), now);
                return;
            }

            if (!playerLineShown)
            {
                Feed.Add(ChatFeedLineKind.Player, _options.PlayerSpeakerName, text, now);
            }
            string roleId = string.IsNullOrWhiteSpace(roleOverride) ? _agents.ActiveRoleId : roleOverride.Trim();
            TurnState turn = new(this, roleId, Guid.NewGuid().ToString("N"));
            _turn = turn;
            Status.BeginTurn(now);
            PushStatus(now);

            ChatOverlayTurnRequest request = new()
            {
                Text = text,
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
                _log.Error($"[ChatOverlay] The turn sender threw: {ex}", LogTag.Llm);
                turn.OnFailed(new ChatOverlayTurnFailure(ChatOverlayTurnFailureKind.Error,
                    LlmErrorPresentation.ToUserMessage(ex), LlmErrorCode.ProviderError, ex));
            }
        }

        private void EndTurn(TurnState turn, ChatOverlayTurnFailure? failure)
        {
            if (!ReferenceEquals(_turn, turn))
            {
                return;
            }

            _turn = null;
            double now = _clock.NowSeconds;
            if (failure.HasValue)
            {
                ChatOverlayTurnFailure value = failure.Value;
                switch (value.Kind)
                {
                    case ChatOverlayTurnFailureKind.Cancelled:
                        if (!string.IsNullOrEmpty(_options.StoppedText))
                        {
                            Feed.Add(ChatFeedLineKind.System, "", _options.StoppedText, now);
                        }

                        break;
                    default:
                        Feed.Add(ChatFeedLineKind.Error, "", (_options.ErrorPrefix ?? "") + value.Message, now);
                        break;
                }
            }

            Status.EndTurn();
            PushStatus(now);
        }

        private void HandleLineAdded(ChatFeedLine line)
        {
            _view.AddLine(line);
            _shownVisible[line.Id] = true;
        }

        private void HandleLineChanged(ChatFeedLine line)
        {
            _dirtyLines.Add(line);
        }

        private void HandleLineRemoved(ChatFeedLine line)
        {
            _dirtyLines.Remove(line);
            _shownVisible.Remove(line.Id);
            _view.RemoveLine(line);
        }

        private void FlushDirtyLines()
        {
            if (_dirtyLines.Count == 0)
            {
                return;
            }

            _dirtyScratch.Clear();
            _dirtyScratch.AddRange(_dirtyLines);
            _dirtyLines.Clear();
            foreach (ChatFeedLine line in _dirtyScratch)
            {
                if (!line.IsRemoved)
                {
                    _view.UpdateLine(line);
                }
            }

            _dirtyScratch.Clear();
        }

        private void RefreshVisibility(double now)
        {
            IReadOnlyList<ChatFeedLine> lines = Feed.Lines;
            for (int i = 0; i < lines.Count; i++)
            {
                ChatFeedLine line = lines[i];
                bool visible = ChatFeedVisibility.IsVisible(line, now, _inputOpen, _options.FadeDelaySeconds);
                if (!_shownVisible.TryGetValue(line.Id, out bool shown) || shown != visible)
                {
                    _shownVisible[line.Id] = visible;
                    _view.SetLineVisible(line, visible);
                }
            }
        }

        private void PushStatus(double now)
        {
            int second = Status.ElapsedSeconds(now);
            if (Status.Version == _pushedStatusVersion && second == _pushedStatusSecond)
            {
                return;
            }

            _pushedStatusVersion = Status.Version;
            _pushedStatusSecond = second;
            string text = Status.Format(now);
            if (string.Equals(text, _pushedStatusText, StringComparison.Ordinal))
            {
                return;
            }

            _pushedStatusText = text;
            _view.SetStatus(Status.Kind, text);
        }

        private void PublishInputState()
        {
            bool open = IsInputOpen;
            if (open == _publishedOpen)
            {
                return;
            }

            _publishedOpen = open;
            try
            {
                InputOpenChanged?.Invoke(open);
            }
            catch (Exception ex)
            {
                _log.Error($"[ChatOverlay] InputOpenChanged handler threw: {ex}", LogTag.Core);
            }
        }

        private void HandleToolStarted(LlmToolCallStarted evt)
        {
            QueueToolEvent(new PendingToolEvent(ToolPhase.Started, evt.TraceId, evt.ToolName));
        }

        private void HandleToolCompleted(LlmToolCallCompleted evt)
        {
            QueueToolEvent(new PendingToolEvent(ToolPhase.Completed, evt.TraceId, evt.ToolName));
        }

        private void HandleToolFailed(LlmToolCallFailed evt)
        {
            QueueToolEvent(new PendingToolEvent(ToolPhase.Failed, evt.TraceId, evt.ToolName));
        }

        private void QueueToolEvent(PendingToolEvent evt)
        {
            lock (_queueGate)
            {
                _pendingToolEvents.Add(evt);
            }
        }

        private void DrainToolEvents()
        {
            lock (_queueGate)
            {
                if (_pendingToolEvents.Count == 0)
                {
                    return;
                }

                _drainedToolEvents.AddRange(_pendingToolEvents);
                _pendingToolEvents.Clear();
            }

            foreach (PendingToolEvent evt in _drainedToolEvents)
            {
                TurnState turn = _turn;
                if (turn == null || !turn.Owns(evt.TraceId))
                {
                    continue;
                }

                switch (evt.Phase)
                {
                    case ToolPhase.Started:
                        Status.ToolStarted(evt.ToolName);
                        break;
                    case ToolPhase.Completed:
                        Status.ToolCompleted(evt.ToolName);
                        break;
                    default:
                        Status.ToolFailed(evt.ToolName);
                        break;
                }
            }

            _drainedToolEvents.Clear();
        }

        private void DrainMessages(double now)
        {
            lock (_queueGate)
            {
                if (_pendingMessages.Count == 0)
                {
                    return;
                }

                _drainedMessages.AddRange(_pendingMessages);
                _pendingMessages.Clear();
            }

            foreach (PendingMessage message in _drainedMessages)
            {
                Feed.Add(message.Kind, message.Speaker, message.Text, now);
            }

            _drainedMessages.Clear();
        }

        private string AssistantSpeaker(string roleId)
        {
            return (_options.AssistantSpeakerName ?? "").Replace("{role}", roleId ?? "");
        }

        private static bool IsFailureChunk(LlmStreamChunk chunk)
        {
            return !string.IsNullOrEmpty(chunk.Error) || chunk.ErrorCode != LlmErrorCode.None;
        }

        private enum ToolPhase
        {
            Started,
            Completed,
            Failed
        }

        private readonly struct PendingToolEvent
        {
            public PendingToolEvent(ToolPhase phase, string traceId, string toolName)
            {
                Phase = phase;
                TraceId = traceId ?? "";
                ToolName = toolName ?? "";
            }

            public ToolPhase Phase { get; }

            public string TraceId { get; }

            public string ToolName { get; }
        }

        private sealed class PreSendHookRegistration : IDisposable
        {
            private ChatOverlayController _owner;

            public PreSendHookRegistration(ChatOverlayController owner, IChatOverlayPreSendHook hook)
            {
                _owner = owner;
                Hook = hook;
            }

            public IChatOverlayPreSendHook Hook { get; }

            public bool Active => _owner != null;

            public void Dispose()
            {
                ChatOverlayController owner = _owner;
                _owner = null;
                owner?._preSendHooks.Remove(this);
            }
        }

        private readonly struct PendingMessage
        {
            public PendingMessage(string speaker, string text, ChatFeedLineKind kind)
            {
                Speaker = speaker ?? "";
                Text = text ?? "";
                Kind = kind;
            }

            public string Speaker { get; }

            public string Text { get; }

            public ChatFeedLineKind Kind { get; }
        }

        /// <summary>Ends a host turn without a feed line; the host reports the outcome itself.</summary>
        private void EndHostTurn(TurnState turn)
        {
            if (!ReferenceEquals(_turn, turn))
            {
                return;
            }

            turn.Abandon();
            EndTurn(turn, null);
        }

        /// <summary>
        /// Feeds a host turn's progress into its turn state. Unless the host asked for the answer, text becomes a
        /// progress hint, so the status line moves and the feed stays quiet. Reports the end exactly once.
        /// </summary>
        private sealed class HostTurnObserver : IChatOverlayTurnObserver
        {
            private static readonly LlmStreamChunk ProgressOnly = new() { GenerationInProgress = true };

            private readonly ChatOverlayController _owner;
            private readonly TurnState _turn;
            private readonly ChatOverlayHostTurnRequest _request;
            private readonly ThinkBlockStreamFilter _think = new();
            private readonly StringBuilder _answer = new();
            private bool _ended;

            public HostTurnObserver(ChatOverlayController owner, TurnState turn, ChatOverlayHostTurnRequest request)
            {
                _owner = owner;
                _turn = turn;
                _request = request;
            }

            public void OnChunk(LlmStreamChunk chunk)
            {
                if (_ended || chunk == null)
                {
                    return;
                }

                if (IsFailureChunk(chunk))
                {
                    Finish(ChatOverlayTurnFailure.FromErrorCode(
                        chunk.ErrorCode == LlmErrorCode.None ? LlmErrorCode.ProviderError : chunk.ErrorCode,
                        chunk.RetryAfterSeconds));
                    return;
                }

                if (!string.IsNullOrEmpty(chunk.Text))
                {
                    KeepAnswer(_think.ProcessChunk(chunk.Text));
                }

                _turn.OnChunk(_request.ShowAnswer || string.IsNullOrEmpty(chunk.Text) ? chunk : ProgressOnly);
            }

            private void KeepAnswer(string visible)
            {
                int room = ChatOverlayHostTurnResult.MaximumAnswerCharacters - _answer.Length;
                if (string.IsNullOrEmpty(visible) || room <= 0)
                {
                    return;
                }

                _answer.Append(visible.Length <= room ? visible : visible.Substring(0, room));
            }

            public void OnCompleted()
            {
                if (_ended)
                {
                    return;
                }

                bool answered = false;
                if (_request.ShowAnswer && ReferenceEquals(_owner._turn, _turn))
                {
                    _turn.OnCompleted();
                    answered = true;
                }

                Finish(null, answered);
            }

            public void OnFailed(ChatOverlayTurnFailure failure)
            {
                Finish(failure);
            }

            private void Finish(ChatOverlayTurnFailure? failure, bool answered = false)
            {
                if (_ended)
                {
                    return;
                }

                _ended = true;
                bool stillCurrent = ReferenceEquals(_owner._turn, _turn);
                ChatOverlayHostTurnResult result;
                if (!stillCurrent && !answered)
                {
                    result = new ChatOverlayHostTurnResult(ChatOverlayHostTurnEnd.Cancelled,
                        _owner._options.StoppedText, LlmErrorCode.Cancelled);
                }
                else if (failure.HasValue)
                {
                    ChatOverlayTurnFailure value = failure.Value;
                    ChatOverlayHostTurnEnd end = value.Kind switch
                    {
                        ChatOverlayTurnFailureKind.Cancelled => ChatOverlayHostTurnEnd.Cancelled,
                        ChatOverlayTurnFailureKind.Timeout => ChatOverlayHostTurnEnd.TimedOut,
                        _ => ChatOverlayHostTurnEnd.Failed
                    };
                    result = new ChatOverlayHostTurnResult(end, value.Message, value.ErrorCode);
                }
                else
                {
                    KeepAnswer(_think.Flush());
                    result = new ChatOverlayHostTurnResult(ChatOverlayHostTurnEnd.Completed, "", LlmErrorCode.None,
                        _answer.ToString());
                }

                _owner.EndHostTurn(_turn);
                try
                {
                    _request.Ended?.Invoke(result);
                }
                catch (Exception ex)
                {
                    _owner._log.Error($"[ChatOverlay] A host turn's end callback threw: {ex}", LogTag.Core);
                }
            }
        }

        /// <summary>One turn's observer; callbacks for a turn that is no longer current are ignored.</summary>
        private sealed class TurnState : IChatOverlayTurnObserver
        {
            private readonly ChatOverlayController _owner;
            private readonly ThinkBlockStreamFilter _think = new();
            private IChatOverlayTurn _handle;
            private ChatFeedLine _line;
            private bool _anyVisibleText;
            private bool _ended;
            private string _heldToolSummary;
            private bool _heldToolSummaryFailed;

            public TurnState(ChatOverlayController owner, string roleId, string traceId)
            {
                _owner = owner;
                RoleId = roleId;
                TraceId = traceId;
            }

            public string RoleId { get; }

            public string TraceId { get; }

            private bool IsCurrent => !_ended && ReferenceEquals(_owner._turn, this);

            public bool Owns(string traceId)
            {
                return string.Equals((traceId ?? "").Trim(), TraceId, StringComparison.Ordinal);
            }

            public void AttachHandle(IChatOverlayTurn handle)
            {
                _handle = handle;
                if (_ended)
                {
                    handle?.Cancel();
                }
            }

            public void OnChunk(LlmStreamChunk chunk)
            {
                if (!IsCurrent || chunk == null)
                {
                    return;
                }

                if (IsFailureChunk(chunk))
                {
                    AppendVisible(_think.Flush(), false);
                    Finish(ChatOverlayTurnFailure.FromErrorCode(
                        chunk.ErrorCode == LlmErrorCode.None ? LlmErrorCode.ProviderError : chunk.ErrorCode,
                        chunk.RetryAfterSeconds));
                    _handle?.Cancel();
                    return;
                }

                _owner.Status.AddStreamedCharacters((chunk.Text?.Length ?? 0) + (chunk.ReasoningText?.Length ?? 0));
                if (chunk.BufferedStreamingNoToolBinding)
                {
                    if (chunk.BufferedStreamingUseToolProgressHint)
                    {
                        _owner.Status.MarkThinking();
                    }

                    return;
                }

                if (!string.IsNullOrEmpty(chunk.Text))
                {
                    if (TryHoldToolSummary(chunk.Text))
                    {
                        return;
                    }

                    AppendVisible(_think.ProcessChunk(chunk.Text), chunk.StartsNewMessage);
                }
                else if (!string.IsNullOrEmpty(chunk.ReasoningText) || chunk.GenerationInProgress)
                {
                    if (chunk.ComposingToolCharacters > 0)
                    {
                        _owner.Status.ToolComposing(chunk.ComposingToolName, chunk.ComposingToolCharacters);
                    }
                    else
                    {
                        _owner.Status.MarkThinking();
                    }
                }
            }

            public void OnCompleted()
            {
                if (!IsCurrent)
                {
                    return;
                }

                AppendVisible(_think.Flush(), false);
                ShowHeldToolSummary();
                if (!_anyVisibleText)
                {
                    _owner.Feed.Add(ChatFeedLineKind.System, "", _owner._options.NoResponseText,
                        _owner._clock.NowSeconds);
                }

                Finish(null);
            }

            // WHY held to the end of the turn: the orchestrator writes this sentence before it reports how the
            // turn ended. When the turn then fails, the failure line alone tells the player what happened.
            private bool TryHoldToolSummary(string text)
            {
                if (_anyVisibleText || _line != null || !AiOrchestrator.IsToolOnlySummary(text, out bool failed))
                {
                    return false;
                }

                _heldToolSummary = text;
                _heldToolSummaryFailed = failed;
                _owner._log.Info($"[ChatOverlay] A turn of tool calls alone, kept out of the feed: {text}",
                    LogTag.Llm);
                _owner.Status.MarkThinking();
                return true;
            }

            private void ShowHeldToolSummary()
            {
                if (_heldToolSummary == null)
                {
                    return;
                }

                string sentence = _heldToolSummaryFailed
                    ? _owner._options.ToolOnlyFailedText
                    : _owner._options.ToolOnlyDoneText;
                AppendVisible(string.IsNullOrEmpty(sentence) ? _heldToolSummary : sentence, false);
                _heldToolSummary = null;
            }

            public void OnFailed(ChatOverlayTurnFailure failure)
            {
                if (!IsCurrent)
                {
                    return;
                }

                AppendVisible(_think.Flush(), false);
                Finish(failure);
            }

            public void Stop()
            {
                if (!IsCurrent)
                {
                    return;
                }

                Finish(new ChatOverlayTurnFailure(ChatOverlayTurnFailureKind.Cancelled,
                    _owner._options.StoppedText, LlmErrorCode.Cancelled));
                _handle?.Cancel();
                try
                {
                    _owner._sender.StopAgent(RoleId);
                }
                catch (Exception ex)
                {
                    _owner._log.Warn($"[ChatOverlay] StopAgent({RoleId}) threw: {ex.Message}", LogTag.Llm);
                }
            }

            public void Abandon()
            {
                _ended = true;
                CompleteLine();
                _handle?.Cancel();
            }

            private void AppendVisible(string visible, bool startsNewMessage)
            {
                if (string.IsNullOrEmpty(visible))
                {
                    return;
                }

                double now = _owner._clock.NowSeconds;
                if (startsNewMessage && _line != null)
                {
                    CompleteLine();
                }

                if (_line == null || _line.IsRemoved)
                {
                    if (!_anyVisibleText)
                    {
                        visible = visible.TrimStart();
                        if (visible.Length == 0)
                        {
                            return;
                        }
                    }

                    _line = _owner.Feed.BeginStreaming(ChatFeedLineKind.Assistant,
                        _owner.AssistantSpeaker(RoleId), now);
                }

                _owner.Feed.Append(_line, visible, now);
                _anyVisibleText = true;
                _owner.Status.MarkAnswering();
            }

            private void CompleteLine()
            {
                if (_line != null)
                {
                    _owner.Feed.Complete(_line, _owner._clock.NowSeconds);
                    _line = null;
                }
            }

            private void Finish(ChatOverlayTurnFailure? failure)
            {
                CompleteLine();
                _ended = true;
                _owner.EndTurn(this, failure);
            }
        }
    }
}
