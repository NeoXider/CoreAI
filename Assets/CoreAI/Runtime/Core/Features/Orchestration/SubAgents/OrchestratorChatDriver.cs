using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Authority;
using CoreAI.Logging;

namespace CoreAI.Ai.SubAgents
{
    /// <summary>What a chat turn became when it went through <see cref="OrchestratorChatDriver"/>.</summary>
    public enum LeadTurnDisposition
    {
        /// <summary>The mode does not apply to this turn; it runs as an ordinary single-agent turn.</summary>
        PassThrough = 0,

        /// <summary>The turn is the lead turn of a plan (Plan style).</summary>
        Lead = 1,

        /// <summary>
        /// The turn is the chat agent's own turn with the sub-agent controls (Team style): a player message, or a
        /// wake turn a sub-agent's completion asked for (<see cref="LeadTurn.IsWake"/>).
        /// </summary>
        Team = 2
    }

    /// <summary>
    /// One chat turn in orchestrator mode: the lead request to run, its plan, and a token that the plan's Stop
    /// cancels. Hand it back to <see cref="OrchestratorChatDriver.EndTurn"/> when the lead turn has ended.
    /// </summary>
    public sealed class LeadTurn : IDisposable
    {
        private CancellationTokenSource _linked;

        internal LeadTurn(
            LeadTurnDisposition disposition,
            AiTaskRequest request,
            OrchestratorSession session,
            OrchestrationPlan plan,
            CancellationTokenSource linked,
            CancellationToken token)
        {
            Disposition = disposition;
            Request = request;
            Session = session;
            Plan = plan;
            _linked = linked;
            Token = token;
        }

        /// <summary>What the turn became.</summary>
        public LeadTurnDisposition Disposition { get; }

        /// <summary>The request to run: the lead request, or the caller's own for a pass-through turn.</summary>
        public AiTaskRequest Request { get; }

        /// <summary>The conversation's session; <c>null</c> for a pass-through turn.</summary>
        public OrchestratorSession Session { get; }

        /// <summary>The plan this lead turn runs; <c>null</c> for a pass-through or Team-style turn.</summary>
        public OrchestrationPlan Plan { get; }

        /// <summary>True for a Team-style wake turn (started by a sub-agent's completion, not by the player).</summary>
        public bool IsWake { get; internal set; }

        /// <summary>The trace the turn runs under (Team style); empty otherwise.</summary>
        public string TraceId { get; internal set; } = "";

        /// <summary>The token to run <see cref="Request"/> with: the caller's, linked to the plan's scope.</summary>
        public CancellationToken Token { get; }

        internal bool Ended { get; set; }

        /// <summary>Releases the linked token source.</summary>
        public void Dispose()
        {
            Interlocked.Exchange(ref _linked, null)?.Dispose();
        }
    }

    /// <summary>Thrown when orchestrator mode refuses a player instruction (the queue is full).</summary>
    public sealed class OrchestratorInstructionRefusedException : InvalidOperationException
    {
        /// <summary>Creates the exception with the refusal text the player reads.</summary>
        public OrchestratorInstructionRefusedException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// The chat side of orchestrator mode, shared by every chat surface (the chat panel and the in-world overlay
    /// both send through the chat service, which hands each turn to this driver).
    /// <para>
    /// Team style (<see cref="OrchestratorModeOptions.Style"/>, the default): a chat turn stays the chat agent's own
    /// turn with the four sub-agent controls added; each player message opens a fresh budget window and gets the
    /// waiting completion notices folded in; nothing waits behind sub-agents. When a notice waits and no lead turn
    /// runs, <see cref="WakeDue"/> is raised and the surface takes a wake turn (<see cref="TryTakeWake"/>) and runs
    /// it through its own chat pipeline; a player turn that arrives during a wake waits for that one turn.
    /// </para>
    /// <para>
    /// Plan style: a chat turn becomes the lead turn of a plan; an instruction that arrives while its conversation
    /// runs a plan waits in that conversation's queue and runs, in its own caller's flow, when the plan before it
    /// ends.
    /// </para>
    /// <para>
    /// Stop ends everything of the agent in both styles: the plan or the sub-agents, the queue, every waiting turn.
    /// </para>
    /// <para>
    /// Conversations are keyed like <see cref="OrchestratorSessionRegistry.ResolveConversationKey"/>: the request's
    /// <see cref="AiTaskRequest.ConversationId"/>, else the actor's session plus the role. Nothing here blocks or
    /// starts a thread: a waiting turn awaits a task source completed outside every lock, so it runs in WebGL.
    /// </para>
    /// </summary>
    public sealed class OrchestratorChatDriver : IDisposable
    {
        private readonly object _lock = new();
        private readonly OrchestratorSessionRegistry _sessions;
        private readonly ILog _log;
        private readonly Dictionary<string, SessionOwner> _owners = new(StringComparer.Ordinal);
        private readonly List<Waiter> _waiters = new();
        private readonly Dictionary<string, PendingWake> _pendingWakes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TeamTurnEntry> _teamTraces = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _leadRoles = new(StringComparer.Ordinal);
        private readonly HashSet<OrchestratorSession> _observed = new();
        private bool _active;
        private bool _disposed;

        /// <summary>Creates the driver over <paramref name="sessions"/>; active when the mode is enabled.</summary>
        public OrchestratorChatDriver(OrchestratorSessionRegistry sessions, ILog log = null)
        {
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
            _log = log ?? NullLog.Instance;
            _active = sessions.Options.Enabled;
        }

        /// <summary>The sessions this driver plans in.</summary>
        public OrchestratorSessionRegistry Sessions => _sessions;

        /// <summary>The style chat turns are run in (<see cref="OrchestratorModeOptions.Style"/>).</summary>
        public OrchestrationStyle Style => _sessions.Options.Style;

        /// <summary>
        /// Raised with a conversation key when a sub-agent's completion waits and no lead turn of that conversation
        /// runs (Team style): call <see cref="TryTakeWake"/> and run the wake. Raised on the thread that changed the
        /// state; a surface marshals to its own thread first.
        /// </summary>
        public event Action<string> WakeDue;

        /// <summary>
        /// The player's switch. Off: every chat turn runs as an ordinary single-agent turn (a running plan is not
        /// touched). On: chat turns become lead turns. Starts at <see cref="OrchestratorModeOptions.Enabled"/>.
        /// </summary>
        public bool IsActive
        {
            get
            {
                lock (_lock)
                {
                    return _active && !_disposed;
                }
            }
            set
            {
                lock (_lock)
                {
                    _active = value;
                }
            }
        }

        /// <summary>
        /// True when <paramref name="request"/> becomes a lead turn: the mode is active, the turn comes from a chat
        /// surface (<see cref="AiTaskRequest.SourceTag"/> <c>Chat</c>), it is not a sub-agent turn and it has text.
        /// </summary>
        public bool Applies(AiTaskRequest request)
        {
            return request != null &&
                   IsActive &&
                   !SubAgentRequestFactory.IsSubAgentSourceTag(request.SourceTag) &&
                   string.Equals(request.SourceTag?.Trim(), ChatSourceTag, StringComparison.OrdinalIgnoreCase) &&
                   !string.IsNullOrWhiteSpace(request.Hint);
        }

        /// <summary>Source tag of the turns the driver takes over (the chat panel's and the overlay's).</summary>
        public const string ChatSourceTag = "Chat";

        /// <summary>
        /// Starts the turn: a pass-through turn returns at once; otherwise the instruction starts a plan or waits
        /// for its place in the queue (cancelled by <paramref name="cancellationToken"/> or by Stop). Throws
        /// <see cref="OrchestratorInstructionRefusedException"/> when the queue is full.
        /// </summary>
        public async Task<LeadTurn> BeginTurnAsync(AiTaskRequest request, CancellationToken cancellationToken)
        {
            if (!Applies(request))
            {
                ReturnPendingWake(request?.TraceId);
                return new LeadTurn(LeadTurnDisposition.PassThrough, request, null, null, null, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            AiTaskRequest host = request.Clone();
            if (string.IsNullOrWhiteSpace(host.TraceId))
            {
                host.TraceId = Guid.NewGuid().ToString("N");
            }

            host.TraceId = host.TraceId.Trim();
            string role = string.IsNullOrWhiteSpace(host.RoleId) ? BuiltInAgentRoleIds.Creator : host.RoleId.Trim();
            string key = OrchestratorSessionRegistry.ResolveConversationKey(host.ConversationId, host.ActorContext, role);
            OrchestratorSession session = _sessions.GetOrCreate(key);
            SubAgentParentContext parent = new(role, host.TraceId, host.CancellationScope, host.ActorContext,
                host.Priority);

            if (Style == OrchestrationStyle.Team)
            {
                return await BeginTeamTurnAsync(host, key, role, session, parent, cancellationToken);
            }

            OrchestrationPlan plan;
            Waiter waiter = null;
            lock (_lock)
            {
                _owners[key] = new SessionOwner(session, host.ActorContext, role);
                // WHY: a plan that just ended starts the next waiting turn outside this lock; an instruction that
                // arrives in between must not start ahead of the turns that were already waiting.
                InstructionSubmission submission = session.ActivePlan == null && HasWaitersLocked(session)
                    ? QueueBehindWaiters(session, host.Hint)
                    : session.Submit(host.Hint, parent);
                switch (submission.Disposition)
                {
                    case InstructionDisposition.Started:
                        plan = submission.Plan;
                        break;
                    case InstructionDisposition.Queued:
                        plan = null;
                        waiter = new Waiter(session, submission.Queued.Id, parent, host);
                        _waiters.Add(waiter);
                        break;
                    default:
                        throw new OrchestratorInstructionRefusedException(submission.Refusal);
                }
            }

            if (waiter != null)
            {
                using (cancellationToken.Register(() => CancelWaiter(waiter)))
                {
                    plan = await waiter.Started.Task;
                }
            }

            AiTaskRequest lead = session.CreateLeadRequest(host, plan);
            CancellationTokenSource linked =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, plan.Scope.Token);
            return new LeadTurn(LeadTurnDisposition.Lead, lead, session, plan, linked, linked.Token);
        }

        /// <summary>
        /// Ends a lead turn with <paramref name="outcome"/>: completes its plan (stopping any run the lead left
        /// behind) and starts the next waiting instruction of the same conversation. A pass-through turn only
        /// releases its resources. Calling it twice does nothing the second time.
        /// </summary>
        public void EndTurn(LeadTurn turn, OrchestrationPlanState outcome)
        {
            if (turn == null)
            {
                return;
            }

            if (turn.Disposition == LeadTurnDisposition.Team)
            {
                EndTeamTurn(turn);
                return;
            }

            Waiter next = null;
            OrchestrationPlan nextPlan = null;
            if (turn.Disposition == LeadTurnDisposition.Lead && !turn.Ended)
            {
                turn.Ended = true;
                // WHY outside the lock: completing a plan cancels its scope, and cancellation callbacks run the
                // unwinding sub-agent runs inline; none of that may run while this driver holds its lock.
                if (ReferenceEquals(turn.Session.ActivePlan, turn.Plan))
                {
                    turn.Session.CompletePlan(outcome);
                }

                lock (_lock)
                {
                    next = TryStartNextLocked(turn.Session, out nextPlan);
                }
            }

            turn.Dispose();
            next?.Started.TrySetResult(nextPlan);
        }

        /// <summary>
        /// Runs one buffered chat turn: <paramref name="runLead"/> runs the request it is given (the lead request,
        /// or the caller's own for a pass-through turn) with the token it is given.
        /// </summary>
        /// <param name="request">The chat turn.</param>
        /// <param name="runLead">Runs a request with a token.</param>
        /// <param name="cancellationToken">The caller's token.</param>
        /// <param name="isSuccess">Whether a returned result counts as a finished plan; null treats every result as one.</param>
        public async Task<T> RunAsync<T>(
            AiTaskRequest request,
            Func<AiTaskRequest, CancellationToken, Task<T>> runLead,
            CancellationToken cancellationToken,
            Func<T, bool> isSuccess = null)
        {
            if (runLead == null)
            {
                throw new ArgumentNullException(nameof(runLead));
            }

            LeadTurn turn = await BeginTurnAsync(request, cancellationToken);
            if (turn.Disposition == LeadTurnDisposition.PassThrough)
            {
                turn.Dispose();
                return await runLead(request, cancellationToken);
            }

            OrchestrationPlanState outcome = OrchestrationPlanState.Failed;
            try
            {
                T result;
                try
                {
                    result = await runLead(turn.Request, turn.Token);
                }
                catch (Exception error) when (turn.Token.IsCancellationRequested)
                {
                    outcome = OrchestrationPlanState.Cancelled;
                    if (error is OperationCanceledException)
                    {
                        throw;
                    }

                    throw new OperationCanceledException(LlmCancellation.CancelledErrorText, error, turn.Token);
                }

                // WHY: how a stopped lead run reports the stop depends on where its continuations run (a cancelled
                // result, an error, or a reply the model finished meanwhile); Stop wins over all of them, so the
                // caller always sees a stopped turn.
                if (turn.Token.IsCancellationRequested)
                {
                    outcome = OrchestrationPlanState.Cancelled;
                    throw new OperationCanceledException(LlmCancellation.CancelledErrorText, turn.Token);
                }

                outcome = isSuccess == null || isSuccess(result)
                    ? OrchestrationPlanState.Completed
                    : OrchestrationPlanState.Failed;
                return result;
            }
            finally
            {
                EndTurn(turn, outcome);
            }
        }

        /// <summary>The streaming form of <see cref="RunAsync{T}"/>; an error chunk ends the plan as failed.</summary>
        public async IAsyncEnumerable<LlmStreamChunk> RunStreamingAsync(
            AiTaskRequest request,
            Func<AiTaskRequest, CancellationToken, IAsyncEnumerable<LlmStreamChunk>> runLead,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (runLead == null)
            {
                throw new ArgumentNullException(nameof(runLead));
            }

            LeadTurn turn = await BeginTurnAsync(request, cancellationToken);
            if (turn.Disposition == LeadTurnDisposition.PassThrough)
            {
                turn.Dispose();
                await foreach (LlmStreamChunk chunk in runLead(request, cancellationToken))
                {
                    yield return chunk;
                }

                yield break;
            }

            bool finished = false;
            bool failed = false;
            try
            {
                await foreach (LlmStreamChunk chunk in runLead(turn.Request, turn.Token))
                {
                    failed |= chunk != null && (chunk.ErrorCode != LlmErrorCode.None || !string.IsNullOrEmpty(chunk.Error));
                    yield return chunk;
                }

                // WHY: a stream that ends normally after Stop is still a stopped turn (see RunAsync).
                turn.Token.ThrowIfCancellationRequested();
                finished = true;
            }
            finally
            {
                OrchestrationPlanState outcome = finished
                    ? failed ? OrchestrationPlanState.Failed : OrchestrationPlanState.Completed
                    : turn.Token.IsCancellationRequested
                        ? OrchestrationPlanState.Cancelled
                        : OrchestrationPlanState.Failed;
                EndTurn(turn, outcome);
            }
        }

        /// <summary>
        /// Stop for <paramref name="roleId"/> (and <paramref name="actor"/>, when given): ends the running plan of
        /// every conversation that actor runs on that role, clears its queue and cancels every turn waiting in it.
        /// Returns whether anything was stopped.
        /// </summary>
        public bool Stop(string roleId, ActorContext? actor = null)
        {
            string role = string.IsNullOrWhiteSpace(roleId) ? BuiltInAgentRoleIds.Creator : roleId.Trim();
            List<OrchestratorSession> sessions = new();
            lock (_lock)
            {
                foreach (SessionOwner owner in _owners.Values)
                {
                    if (owner.Matches(role, actor))
                    {
                        sessions.Add(owner.Session);
                    }
                }
            }

            return StopSessions(sessions);
        }

        /// <summary>Stops every conversation and cancels every waiting turn.</summary>
        public void StopAll()
        {
            List<OrchestratorSession> sessions = new();
            lock (_lock)
            {
                foreach (SessionOwner owner in _owners.Values)
                {
                    sessions.Add(owner.Session);
                }
            }

            StopSessions(sessions);
        }

        /// <summary>
        /// Forwards one tool-call event of <paramref name="traceId"/> to the running plans: a sub-agent's step is
        /// counted for the HUD and re-arms its own and its lead's idle deadline. Returns how many plans took it.
        /// </summary>
        public int RecordActivity(string traceId)
        {
            return RecordToolActivity(traceId, "", null);
        }

        /// <summary>
        /// Forwards one tool-call event to the running plans (as <see cref="RecordActivity"/>) and to the Team-style
        /// sub-agents: <paramref name="succeeded"/> null for a started call (it re-arms the sub-agent's idle deadline),
        /// true or false for a finished one, which also lands in its transcript as a step <c>agent_read</c> shows.
        /// Returns how many plans and sub-agents took it.
        /// </summary>
        public int RecordToolActivity(string traceId, string toolName, bool? succeeded)
        {
            if (string.IsNullOrWhiteSpace(traceId))
            {
                return 0;
            }

            int recorded = 0;
            foreach (OrchestratorSession session in _sessions.Sessions)
            {
                if (session.ActivePlan?.TryRecordActivity(traceId) == true)
                {
                    recorded++;
                }

                if (session.Team.TryRecordActivity(traceId, toolName, succeeded))
                {
                    recorded++;
                }
            }

            return recorded;
        }

        /// <summary>
        /// Takes the wake turn conversation <paramref name="conversationKey"/> is due (Team style): its waiting notices
        /// leave the team and travel in <paramref name="wake"/>. Run <see cref="LeadWake.Text"/> as a chat turn of
        /// <see cref="LeadWake.RoleId"/> with <see cref="LeadWake.TraceId"/> through the surface's chat pipeline (it
        /// reaches <see cref="BeginTurnAsync"/>, which recognises it); a surface that cannot run it now hands it back
        /// with <see cref="ReturnWake"/>. False when no wake is due (no notice, a lead turn runs, the player message's
        /// wakes are spent, the mode is off).
        /// </summary>
        public bool TryTakeWake(string conversationKey, out LeadWake wake)
        {
            wake = null;
            if (!IsActive || Style != OrchestrationStyle.Team ||
                !_sessions.TryGet(conversationKey, out OrchestratorSession session) ||
                !session.Team.TryBeginWake(out IReadOnlyList<SubAgentNotice> notices))
            {
                return false;
            }

            string trace = Guid.NewGuid().ToString("N");
            string role;
            lock (_lock)
            {
                _leadRoles.TryGetValue(session.ConversationId, out role);
            }

            wake = new LeadWake(session.ConversationId, role ?? "", trace, SubAgentTeam.FormatWakeText(notices),
                notices);
            lock (_lock)
            {
                _pendingWakes[trace] = new PendingWake(session, wake);
            }

            return true;
        }

        /// <summary>Hands back a wake taken with <see cref="TryTakeWake"/> that the surface could not run; its notices wait again.</summary>
        public void ReturnWake(LeadWake wake)
        {
            if (wake != null)
            {
                ReturnPendingWake(wake.TraceId);
            }
        }

        /// <summary>
        /// The session whose Team-style lead turn (player or wake) runs under <paramref name="leadTraceId"/> now;
        /// <paramref name="isWake"/> tells which.
        /// </summary>
        public bool TryGetTeamSessionForLeadTrace(string leadTraceId, out OrchestratorSession session, out bool isWake)
        {
            session = null;
            isWake = false;
            if (string.IsNullOrWhiteSpace(leadTraceId))
            {
                return false;
            }

            lock (_lock)
            {
                if (!_teamTraces.TryGetValue(leadTraceId.Trim(), out TeamTurnEntry entry))
                {
                    return false;
                }

                session = entry.Session;
                isWake = entry.IsWake;
                return true;
            }
        }

        private async Task<LeadTurn> BeginTeamTurnAsync(
            AiTaskRequest host,
            string key,
            string role,
            OrchestratorSession session,
            SubAgentParentContext parent,
            CancellationToken cancellationToken)
        {
            bool wake;
            lock (_lock)
            {
                _owners[key] = new SessionOwner(session, host.ActorContext, role);
                _leadRoles[key] = role;
                wake = _pendingWakes.TryGetValue(host.TraceId, out PendingWake pending) &&
                       ReferenceEquals(pending.Session, session);
                if (wake)
                {
                    _pendingWakes.Remove(host.TraceId);
                }

                if (_observed.Add(session))
                {
                    session.Team.NoticeQueued += _ => RaiseWakeDueIfDue(session);
                }
            }

            ReturnPendingWake(host.TraceId);
            SubAgentTeam team = session.Team;
            string news = "";
            if (wake)
            {
                team.MarkWakeStarted();
            }
            else
            {
                // WHY: a player message never waits behind sub-agents; it waits only for a wake turn already
                // running in this conversation, so two lead turns do not interleave one history.
                await team.EnterLeadTurnAsync(cancellationToken);
            }

            try
            {
                if (!wake)
                {
                    team.BeginInstructionWindow();
                }

                // WHY also for a wake: a completion that arrived after the wake was taken joins it instead of
                // asking for a wake of its own.
                news = SubAgentTeam.FormatNews(team.TakeNotices());

                team.SetLeadContext(parent);
                AiTaskRequest lead = session.CreateTeamLeadRequest(host, news);
                CancellationTokenSource linked =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, team.LeadToken);
                lock (_lock)
                {
                    _teamTraces[host.TraceId] = new TeamTurnEntry(session, wake);
                }

                return new LeadTurn(LeadTurnDisposition.Team, lead, session, null, linked, linked.Token)
                {
                    IsWake = wake,
                    TraceId = host.TraceId
                };
            }
            catch
            {
                if (wake)
                {
                    team.EndWake();
                }
                else
                {
                    team.ExitLeadTurn();
                }

                throw;
            }
        }

        private void EndTeamTurn(LeadTurn turn)
        {
            if (!turn.Ended)
            {
                turn.Ended = true;
                lock (_lock)
                {
                    _teamTraces.Remove(turn.TraceId);
                }

                if (turn.IsWake)
                {
                    turn.Session.Team.EndWake();
                }
                else
                {
                    turn.Session.Team.ExitLeadTurn();
                }

                RaiseWakeDueIfDue(turn.Session);
            }

            turn.Dispose();
        }

        private void ReturnPendingWake(string traceId)
        {
            if (string.IsNullOrWhiteSpace(traceId))
            {
                return;
            }

            PendingWake pending;
            lock (_lock)
            {
                string trace = traceId.Trim();
                if (!_pendingWakes.TryGetValue(trace, out pending))
                {
                    return;
                }

                _pendingWakes.Remove(trace);
            }

            // WHY: a wake that reached a turn the mode does not take (switched off meanwhile, another conversation)
            // would otherwise hold the wake slot forever and lose its notices.
            pending.Session.Team.CancelWake(pending.Wake.Notices);
        }

        private void RaiseWakeDueIfDue(OrchestratorSession session)
        {
            if (!IsActive || !session.Team.IsWakeDue)
            {
                return;
            }

            Action<string> handlers = WakeDue;
            if (handlers == null)
            {
                return;
            }

            foreach (Delegate handler in handlers.GetInvocationList())
            {
                try
                {
                    ((Action<string>)handler)(session.ConversationId);
                }
                catch (Exception ex)
                {
                    _log.Warn($"[OrchestratorChatDriver] wake observer failed: {ex.Message}", LogTag.Llm);
                }
            }
        }

        /// <summary>
        /// The status of the conversation whose lead turn (running or waiting) has <paramref name="leadTraceId"/>;
        /// <paramref name="waiting"/> is true while that turn still waits in the queue.
        /// </summary>
        public bool TryGetStatusForLeadTrace(string leadTraceId, out OrchestrationStatus status, out bool waiting)
        {
            status = null;
            waiting = false;
            if (string.IsNullOrWhiteSpace(leadTraceId))
            {
                return false;
            }

            string trace = leadTraceId.Trim();
            lock (_lock)
            {
                foreach (Waiter waiter in _waiters)
                {
                    if (string.Equals(waiter.Parent.LeadTraceId, trace, StringComparison.Ordinal))
                    {
                        status = waiter.Session.Current;
                        waiting = true;
                        return true;
                    }
                }
            }

            foreach (OrchestratorSession session in _sessions.Sessions)
            {
                OrchestrationPlan plan = session.ActivePlan;
                if (plan != null && string.Equals(plan.Parent.LeadTraceId, trace, StringComparison.Ordinal))
                {
                    status = session.Current;
                    return true;
                }
            }

            return false;
        }

        /// <summary>Turns waiting in a queue now.</summary>
        public int WaitingTurns
        {
            get
            {
                lock (_lock)
                {
                    return _waiters.Count;
                }
            }
        }

        /// <summary>Stops everything; the driver applies to no further turn.</summary>
        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }

            StopAll();
        }

        private bool StopSessions(List<OrchestratorSession> sessions)
        {
            bool stopped = false;
            List<Waiter> cancelled = new();
            // WHY the queue and the waiters go first: stopping a plan can finish its lead turn inline, and that
            // turn's EndTurn would otherwise start the next queued instruction in the middle of Stop.
            lock (_lock)
            {
                foreach (OrchestratorSession session in sessions)
                {
                    stopped |= session.Queue.CancelAll().Count > 0;
                    TakeWaitersLocked(session, cancelled);
                    DropPendingWakesLocked(session);
                }
            }

            CancelWaiters(cancelled);

            // WHY outside the lock: cancelling a plan's scope runs the unwinding lead and sub-agent code inline.
            foreach (OrchestratorSession session in sessions)
            {
                stopped |= session.Stop();
            }

            // WHY again: an instruction that arrived while the plans were being stopped queued behind them.
            List<Waiter> late = new();
            lock (_lock)
            {
                foreach (OrchestratorSession session in sessions)
                {
                    session.Queue.CancelAll();
                    TakeWaitersLocked(session, late);
                }
            }

            CancelWaiters(late);
            return stopped || cancelled.Count > 0 || late.Count > 0;
        }

        private static void CancelWaiters(List<Waiter> waiters)
        {
            foreach (Waiter waiter in waiters)
            {
                waiter.Started.TrySetCanceled();
            }
        }

        private bool HasWaitersLocked(OrchestratorSession session)
        {
            foreach (Waiter waiter in _waiters)
            {
                if (ReferenceEquals(waiter.Session, session))
                {
                    return true;
                }
            }

            return false;
        }

        private static InstructionSubmission QueueBehindWaiters(OrchestratorSession session, string text)
        {
            InstructionQueueResult queued = session.Queue.Enqueue(text);
            return queued.Accepted
                ? new InstructionSubmission(InstructionDisposition.Queued, null, queued.Entry, "")
                : new InstructionSubmission(InstructionDisposition.Refused, null, null, queued.Refusal);
        }

        private Waiter TryStartNextLocked(OrchestratorSession session, out OrchestrationPlan plan)
        {
            plan = null;
            while (session.ActivePlan == null)
            {
                IReadOnlyList<QueuedInstructionSnapshot> queued = session.Queue.Snapshot();
                if (queued.Count == 0)
                {
                    return null;
                }

                Waiter waiter = FindWaiterLocked(session, queued[0].Id);
                if (waiter == null)
                {
                    // WHY: an entry no waiting turn owns (queued by other code) has nobody to answer; dropping it
                    // keeps the turns behind it from waiting forever.
                    session.Queue.Cancel(queued[0].Id);
                    _log.Warn($"[OrchestratorChatDriver] dropped queued instruction {queued[0].Id}: no waiting turn",
                        LogTag.Llm);
                    continue;
                }

                if (!session.TryStartNext(waiter.Parent, out plan, out QueuedInstructionSnapshot started) ||
                    !string.Equals(started.Id, waiter.EntryId, StringComparison.Ordinal))
                {
                    return null;
                }

                _waiters.Remove(waiter);
                return waiter;
            }

            return null;
        }

        private Waiter FindWaiterLocked(OrchestratorSession session, string entryId)
        {
            foreach (Waiter waiter in _waiters)
            {
                if (ReferenceEquals(waiter.Session, session) &&
                    string.Equals(waiter.EntryId, entryId, StringComparison.Ordinal))
                {
                    return waiter;
                }
            }

            return null;
        }

        private void TakeWaitersLocked(OrchestratorSession session, List<Waiter> into)
        {
            for (int i = _waiters.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(_waiters[i].Session, session))
                {
                    into.Add(_waiters[i]);
                    _waiters.RemoveAt(i);
                }
            }
        }

        private void CancelWaiter(Waiter waiter)
        {
            bool removed;
            lock (_lock)
            {
                removed = _waiters.Remove(waiter);
                if (removed)
                {
                    waiter.Session.Queue.Cancel(waiter.EntryId);
                }
            }

            if (removed)
            {
                waiter.Started.TrySetCanceled();
            }
        }

        private void DropPendingWakesLocked(OrchestratorSession session)
        {
            List<string> traces = new();
            foreach (KeyValuePair<string, PendingWake> pending in _pendingWakes)
            {
                if (ReferenceEquals(pending.Value.Session, session))
                {
                    traces.Add(pending.Key);
                }
            }

            foreach (string trace in traces)
            {
                _pendingWakes.Remove(trace);
            }
        }

        private sealed class PendingWake
        {
            public PendingWake(OrchestratorSession session, LeadWake wake)
            {
                Session = session;
                Wake = wake;
            }

            public OrchestratorSession Session { get; }

            public LeadWake Wake { get; }
        }

        private sealed class TeamTurnEntry
        {
            public TeamTurnEntry(OrchestratorSession session, bool isWake)
            {
                Session = session;
                IsWake = isWake;
            }

            public OrchestratorSession Session { get; }

            public bool IsWake { get; }
        }

        private sealed class Waiter
        {
            public Waiter(OrchestratorSession session, string entryId, SubAgentParentContext parent, AiTaskRequest host)
            {
                Session = session;
                EntryId = entryId;
                Parent = parent;
                Host = host;
            }

            public OrchestratorSession Session { get; }

            public string EntryId { get; }

            public SubAgentParentContext Parent { get; }

            public AiTaskRequest Host { get; }

            public TaskCompletionSource<OrchestrationPlan> Started { get; } = new();
        }

        private sealed class SessionOwner
        {
            public SessionOwner(OrchestratorSession session, ActorContext? actor, string roleId)
            {
                Session = session;
                ActorId = actor?.ActorId ?? "";
                ActorSessionId = actor?.SessionId ?? "";
                RoleId = roleId ?? "";
            }

            public OrchestratorSession Session { get; }

            public string ActorId { get; }

            public string ActorSessionId { get; }

            public string RoleId { get; }

            public bool Matches(string roleId, ActorContext? actor)
            {
                if (!string.Equals(RoleId, roleId, StringComparison.Ordinal))
                {
                    return false;
                }

                return !actor.HasValue ||
                       (string.Equals(ActorId, actor.Value.ActorId ?? "", StringComparison.Ordinal) &&
                        string.Equals(ActorSessionId, actor.Value.SessionId ?? "", StringComparison.Ordinal));
            }
        }
    }
}
