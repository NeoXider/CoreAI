using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Logging;

namespace CoreAI.Ai.SubAgents
{
    /// <summary>
    /// The background sub-agents of one conversation in <see cref="OrchestrationStyle.Team"/>: the chat agent starts
    /// them (<see cref="Start"/>), messages or continues them (<see cref="Send"/>), lists and reads them
    /// (<see cref="Read"/>) and stops them (<see cref="Stop"/>), and they outlive the turn that started them. A member
    /// that goes idle on its own leaves a <see cref="SubAgentNotice"/> for the lead (<see cref="TakeNotices"/>).
    /// <para>
    /// A member's turn is one run of <see cref="IAiTaskResultService"/> in a fresh request: its user message is the
    /// task (first turn) or the waiting messages after its earlier conversation as text (a continuation). Messages
    /// that arrive while it works wait in its inbox and start its next turn when the current one ends; only when it
    /// goes idle with an empty inbox does the lead get a notice.
    /// </para>
    /// <para>
    /// Budget: <see cref="BeginInstructionWindow"/> (one player message) opens a window of
    /// <see cref="OrchestratorModeOptions.MaxSubAgentCallsPerPlan"/> runs and the token cap; every turn reserves one
    /// run when it is asked for, so a refusal is immediate, and a stop refunds what never ran.
    /// </para>
    /// <para>
    /// WebGL-safe: no thread, no timer, no blocking wait. A member's turns run in an async loop started by the call
    /// that gave it work; it waits on the team's FIFO <see cref="SubAgentAdmissionGate"/>, and its deadline is the
    /// host's <see cref="ILlmAsyncMarshaler.DelayAsync"/> (frame-driven in the Unity host). Task sources are created
    /// without <c>RunContinuationsAsynchronously</c> and completed outside the lock. Thread-safe.
    /// </para>
    /// </summary>
    public sealed class SubAgentTeam : IDisposable
    {
        /// <summary>Heading of the news block a lead turn gets in its request's cache-safe tail.</summary>
        public const string NewsHeading = "## Sub-agent news";

        /// <summary>Transcript entries one member keeps; older tool steps go first.</summary>
        public const int MaxTranscriptEntries = 200;

        private readonly object _lock = new();
        private readonly ISubAgentRegistry _registry;
        private readonly IAiTaskResultService _runner;
        private readonly ILlmAsyncMarshaler _asyncMarshaler;
        private readonly ILog _log;
        private readonly SubAgentRunMarker _marker;
        private readonly SubAgentAdmissionGate _gate;
        private readonly List<SubAgentMember> _members = new();
        private readonly Dictionary<string, int> _counters = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<SubAgentNotice> _notices = new();
        private readonly List<LeadWaiter> _leadWaiters = new();
        private readonly string[] _guardedToolNames;
        private readonly SynchronizationContext _home;
        private OrchestrationBudgetLedger _ledger;
        private OrchestrationScope _leadScope = new();
        private SubAgentParentContext _leadContext;
        private int _leadTurns;
        private bool _wakePending;
        private bool _wakeRunning;
        private int _wakes;
        private bool _disposed;

        /// <summary>Creates a team.</summary>
        /// <param name="id">The conversation the team belongs to; part of every member's trace and scope.</param>
        /// <param name="options">Mode options (budgets, limits, tool names).</param>
        /// <param name="registry">The sub-agent types members are started from.</param>
        /// <param name="runner">
        /// Runs one member turn. Compose it BELOW the admission queue (the <see cref="AiOrchestrator"/> itself), as
        /// for <see cref="OrchestratorSession"/>.
        /// </param>
        /// <param name="asyncMarshaler">Host delay for deadlines; a Unity host passes its frame-driven marshaler.</param>
        /// <param name="log">Log for observer and loop failures; null logs nothing.</param>
        public SubAgentTeam(
            string id,
            OrchestratorModeOptions options,
            ISubAgentRegistry registry,
            IAiTaskResultService runner,
            ILlmAsyncMarshaler asyncMarshaler = null,
            ILog log = null)
            : this(id, options, registry, runner, asyncMarshaler, log, null)
        {
        }

        internal SubAgentTeam(
            string id,
            OrchestratorModeOptions options,
            ISubAgentRegistry registry,
            IAiTaskResultService runner,
            ILlmAsyncMarshaler asyncMarshaler,
            ILog log,
            SubAgentRunMarker marker)
        {
            Id = id?.Trim() ?? "";
            Options = options ?? throw new ArgumentNullException(nameof(options));
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _runner = runner ?? throw new ArgumentNullException(nameof(runner));
            // WHY: the portable default delays on Task.Delay, which never fires in a WebGL player; a Unity host
            // passes its frame-driven marshaler so the deadline exists there.
            _asyncMarshaler = asyncMarshaler ?? PassThroughLlmAsyncMarshaler.Instance;
            _log = log ?? NullLog.Instance;
            _marker = marker ?? new SubAgentRunMarker();
            _gate = new SubAgentAdmissionGate(options.MaxParallelSubAgents);
            _ledger = new OrchestrationBudgetLedger(options.CreateBudget());
            _home = SynchronizationContext.Current;
            _guardedToolNames = new[]
            {
                options.StartToolName, options.SendToolName, options.ReadToolName, options.StopToolName,
                options.DeclareToolName
            };
        }

        /// <summary>The conversation the team belongs to.</summary>
        public string Id { get; }

        /// <summary>Mode options.</summary>
        public OrchestratorModeOptions Options { get; }

        /// <summary>Raised after every change of a member, the notices or the budget window; never under the lock.</summary>
        public event Action Changed;

        /// <summary>Raised when a member that went idle on its own leaves a notice for the lead.</summary>
        public event Action<SubAgentNotice> NoticeQueued;

        /// <summary>True while the current execution flow is inside a member's (or a delegation's) run.</summary>
        public bool IsInsideMemberRun => _marker.IsInside;

        /// <summary>Spending of the current budget window.</summary>
        public OrchestrationBudgetSnapshot Budget
        {
            get
            {
                lock (_lock)
                {
                    return _ledger.Snapshot();
                }
            }
        }

        /// <summary>Notices waiting for the lead.</summary>
        public int PendingNotices
        {
            get
            {
                lock (_lock)
                {
                    return _notices.Count;
                }
            }
        }

        /// <summary>True while a lead turn of the conversation runs or a wake turn is pending or running.</summary>
        public bool IsLeadBusy
        {
            get
            {
                lock (_lock)
                {
                    return _leadTurns > 0 || _wakePending || _wakeRunning;
                }
            }
        }

        /// <summary>True when notices wait, no lead turn runs and the window still allows a wake turn.</summary>
        public bool IsWakeDue
        {
            get
            {
                lock (_lock)
                {
                    return IsWakeDueLocked();
                }
            }
        }

        /// <summary>Token of the lead turns of this conversation; Stop cancels it (a new one serves the next turn).</summary>
        internal CancellationToken LeadToken
        {
            get
            {
                lock (_lock)
                {
                    return _leadScope.Token;
                }
            }
        }

        /// <summary>Every member, in creation order.</summary>
        public IReadOnlyList<SubAgentMemberSnapshot> Snapshot()
        {
            lock (_lock)
            {
                if (_members.Count == 0)
                {
                    // WHY: the HUD asks every frame; a conversation that never delegated allocates nothing.
                    return Array.Empty<SubAgentMemberSnapshot>();
                }

                List<SubAgentMemberSnapshot> members = new(_members.Count);
                foreach (SubAgentMember member in _members)
                {
                    members.Add(new SubAgentMemberSnapshot(member));
                }

                return members.AsReadOnly();
            }
        }

        /// <summary>The transcript of member <paramref name="agent"/>; empty for an unknown name.</summary>
        public IReadOnlyList<SubAgentTranscriptEntry> Transcript(string agent)
        {
            lock (_lock)
            {
                SubAgentMember member = FindLocked(agent);
                return member == null
                    ? Array.Empty<SubAgentTranscriptEntry>()
                    : new List<SubAgentTranscriptEntry>(member.Transcript).AsReadOnly();
            }
        }

        /// <summary>
        /// The lead turn now running: members started or continued from now on run as its actor, with its priority
        /// and traces derived from its trace. The driver sets it when a lead turn begins.
        /// </summary>
        public void SetLeadContext(SubAgentParentContext parent)
        {
            lock (_lock)
            {
                _leadContext = parent;
            }
        }

        /// <summary>Opens a fresh budget window (one player message): runs, tokens and wake turns start from zero.</summary>
        public void BeginInstructionWindow()
        {
            lock (_lock)
            {
                _ledger = new OrchestrationBudgetLedger(Options.CreateBudget());
                _wakes = 0;
            }

            RaiseChanged();
        }

        /// <summary>
        /// Starts a member of type <paramref name="subAgentId"/> on <paramref name="task"/> and returns at once with
        /// its name; it never waits for the run.
        /// </summary>
        public SubAgentControlResult Start(string subAgentId, string task, string label = "")
        {
            const SubAgentControlAction action = SubAgentControlAction.Start;
            if (_marker.IsInside)
            {
                return RefuseNested(action);
            }

            if (string.IsNullOrWhiteSpace(task))
            {
                return SubAgentControlResult.Refused(action, SubAgentErrorCode.InvalidArguments,
                    "'task' is required: the whole job with every fact the sub-agent needs.");
            }

            string id = subAgentId?.Trim() ?? "";
            if (id.Length == 0 || !_registry.TryGet(id, out SubAgentDefinition definition))
            {
                return SubAgentControlResult.Refused(action,
                    id.Length == 0 ? SubAgentErrorCode.InvalidArguments : SubAgentErrorCode.UnknownSubAgent,
                    (id.Length == 0 ? "'subagent' is required." : $"There is no sub-agent type '{id}'.") +
                    " Types: " + DescribeTypes() + ".");
            }

            SubAgentMember member;
            lock (_lock)
            {
                if (_disposed)
                {
                    return RefuseClosed(action);
                }

                if (!TryMakeRoomLocked())
                {
                    return SubAgentControlResult.Refused(action, SubAgentErrorCode.AgentLimitReached,
                        $"{_members.Count} sub-agents are busy; wait for one to finish or stop one.");
                }

                SubAgentErrorCode reserve = _ledger.TryReserveCall();
                if (reserve != SubAgentErrorCode.None)
                {
                    return SubAgentControlResult.Refused(action, reserve, DescribeBudgetLocked(reserve));
                }

                string trimmedTask = task.Trim();
                string title = OrchestrationPlan.ClipLine(string.IsNullOrWhiteSpace(label) ? trimmedTask : label,
                    OrchestrationPlan.MaxTitleChars);
                member = new SubAgentMember(NextNameLocked(definition.Id), definition, title,
                    _leadContext ?? DefaultParent())
                {
                    PendingTask = trimmedTask,
                    Activity = new CancellationTokenSource(),
                    LoopRunning = true
                };
                member.Reservations.Enqueue(_ledger);
                member.Append(SubAgentTranscriptKind.Task, trimmedTask, 1);
                _members.Add(member);
            }

            RaiseChanged();
            StartLoop(member);
            lock (_lock)
            {
                return new SubAgentControlResult
                {
                    Ok = true,
                    Action = action,
                    Agent = member.Name,
                    State = member.State,
                    Turns = member.Turns
                };
            }
        }

        /// <summary>
        /// Sends <paramref name="message"/> to member <paramref name="agent"/>: a busy member gets it when its current
        /// turn ends (<see cref="SubAgentDelivery.Queued"/>), an idle one continues its job with its conversation now
        /// (<see cref="SubAgentDelivery.Started"/>).
        /// </summary>
        public SubAgentControlResult Send(string agent, string message)
        {
            const SubAgentControlAction action = SubAgentControlAction.Send;
            if (_marker.IsInside)
            {
                return RefuseNested(action);
            }

            if (string.IsNullOrWhiteSpace(message))
            {
                return SubAgentControlResult.Refused(action, SubAgentErrorCode.InvalidArguments,
                    "'message' is required.", agent?.Trim());
            }

            SubAgentMember member;
            SubAgentDelivery delivery;
            bool startLoop = false;
            lock (_lock)
            {
                if (_disposed)
                {
                    return RefuseClosed(action);
                }

                member = FindLocked(agent);
                if (member == null)
                {
                    return RefuseUnknownLocked(action, agent);
                }

                string text = message.Trim();
                bool idle = member.State >= SubAgentMemberState.Finished;
                if (!idle && member.Inbox.Count >= Math.Max(1, Options.MaxInboxMessages))
                {
                    return SubAgentControlResult.Refused(action, SubAgentErrorCode.InboxFull,
                        $"{member.Name} already has {member.Inbox.Count} messages waiting; wait for its answer.",
                        member.Name);
                }

                // WHY only these two reserve: a queued member's next turn is already reserved (or is its first),
                // and a second message to a working member joins the extra turn the first one asked for.
                bool needsTurn = idle || (member.State == SubAgentMemberState.Working && member.Inbox.Count == 0);
                if (needsTurn)
                {
                    SubAgentErrorCode reserve = _ledger.TryReserveCall();
                    if (reserve != SubAgentErrorCode.None)
                    {
                        return SubAgentControlResult.Refused(action, reserve, DescribeBudgetLocked(reserve),
                            member.Name);
                    }

                    member.Reservations.Enqueue(_ledger);
                }

                member.Inbox.Add(text);
                member.Append(SubAgentTranscriptKind.Message, text, member.Turns + 1);
                member.Parent = _leadContext ?? member.Parent;
                if (idle)
                {
                    member.State = SubAgentMemberState.Queued;
                    member.LastError = "";
                    member.Activity = new CancellationTokenSource();
                    startLoop = !member.LoopRunning;
                    member.LoopRunning = true;
                    delivery = SubAgentDelivery.Started;
                }
                else
                {
                    delivery = SubAgentDelivery.Queued;
                }
            }

            RaiseChanged();
            if (startLoop)
            {
                StartLoop(member);
            }

            lock (_lock)
            {
                return new SubAgentControlResult
                {
                    Ok = true,
                    Action = action,
                    Agent = member.Name,
                    Delivery = delivery,
                    State = member.State,
                    Turns = member.Turns,
                    PendingMessages = member.Inbox.Count
                };
            }
        }

        /// <summary>
        /// Without <paramref name="agent"/>: every member and its state. With it: the member's state and its own
        /// output (answers, failures, tool steps) after <paramref name="cursor"/>, newest kept within
        /// <see cref="OrchestratorModeOptions.MaxReadChars"/>. Reading changes nothing.
        /// </summary>
        public SubAgentControlResult Read(string agent = "", int cursor = 0)
        {
            if (_marker.IsInside)
            {
                return RefuseNested(SubAgentControlAction.Read);
            }

            if (string.IsNullOrWhiteSpace(agent))
            {
                return new SubAgentControlResult
                {
                    Ok = true,
                    Action = SubAgentControlAction.List,
                    Members = Snapshot()
                };
            }

            lock (_lock)
            {
                SubAgentMember member = FindLocked(agent);
                if (member == null)
                {
                    return RefuseUnknownLocked(SubAgentControlAction.Read, agent);
                }

                string output = RenderOutputLocked(member, cursor, out int skipped);
                return new SubAgentControlResult
                {
                    Ok = true,
                    Action = SubAgentControlAction.Read,
                    Agent = member.Name,
                    State = member.State,
                    Turns = member.Turns,
                    PendingMessages = member.Inbox.Count,
                    Output = output,
                    Skipped = skipped,
                    Cursor = member.NextSeq
                };
            }
        }

        /// <summary>
        /// Stops member <paramref name="agent"/> now without waiting: its running turn is cancelled, its waiting
        /// messages are dropped and their runs refunded. Returns its last answer. An idle member is left as it is and
        /// its final answer returned. The member keeps its conversation; <see cref="Send"/> continues it.
        /// </summary>
        public SubAgentControlResult Stop(string agent)
        {
            const SubAgentControlAction action = SubAgentControlAction.Stop;
            if (_marker.IsInside)
            {
                return RefuseNested(action);
            }

            SubAgentControlResult result;
            CancellationTokenSource cancel = null;
            lock (_lock)
            {
                SubAgentMember member = FindLocked(agent);
                if (member == null)
                {
                    return RefuseUnknownLocked(action, agent);
                }

                SubAgentMemberState was = member.State;
                int steps = 0;
                if (was < SubAgentMemberState.Finished)
                {
                    steps = member.Steps;
                    cancel = StopMemberLocked(member, "stopped by the lead");
                }

                result = new SubAgentControlResult
                {
                    Ok = true,
                    Action = action,
                    Agent = member.Name,
                    WasState = was,
                    State = member.State,
                    Turns = member.Turns,
                    Answer = member.LastAnswer,
                    Steps = steps
                };
            }

            if (cancel != null)
            {
                cancel.Cancel();
                RaiseChanged();
            }

            return result;
        }

        /// <summary>
        /// The player's Stop: stops every member's turn, drops every waiting message and notice, cancels the lead
        /// turns of the conversation (through <see cref="LeadToken"/>) and every turn waiting for a wake to end.
        /// Members keep their conversations. Returns whether anything was stopped.
        /// </summary>
        public bool StopAll()
        {
            return StopAllCore("stopped by the player");
        }

        /// <summary>Takes the notices waiting for the lead, oldest first.</summary>
        public IReadOnlyList<SubAgentNotice> TakeNotices()
        {
            List<SubAgentNotice> taken;
            lock (_lock)
            {
                if (_notices.Count == 0)
                {
                    return Array.Empty<SubAgentNotice>();
                }

                taken = new List<SubAgentNotice>(_notices);
                _notices.Clear();
            }

            RaiseChanged();
            return taken.AsReadOnly();
        }

        /// <summary>
        /// Counts one tool step of the member whose running turn has <paramref name="traceId"/>:
        /// <paramref name="succeeded"/> null is a step that started (it only re-arms the member's idle deadline);
        /// true or false is a finished step, recorded in its transcript. Returns false for an unknown trace.
        /// </summary>
        public bool TryRecordActivity(string traceId, string toolName = "", bool? succeeded = null)
        {
            if (string.IsNullOrWhiteSpace(traceId))
            {
                return false;
            }

            string trace = traceId.Trim();
            LlmRequestProgress progress = null;
            bool found = false;
            lock (_lock)
            {
                foreach (SubAgentMember member in _members)
                {
                    if (member.State != SubAgentMemberState.Working ||
                        !string.Equals(member.TraceId, trace, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    found = true;
                    progress = member.Progress;
                    if (succeeded.HasValue)
                    {
                        member.Steps++;
                        string tool = string.IsNullOrWhiteSpace(toolName) ? "tool" : toolName.Trim();
                        AppendBoundedLocked(member, SubAgentTranscriptKind.Tool,
                            tool + (succeeded.Value ? " ok" : " failed"), member.Turns);
                    }

                    break;
                }
            }

            if (found)
            {
                progress?.Report();
                if (succeeded.HasValue)
                {
                    RaiseChanged();
                }
            }

            return found;
        }

        /// <summary>The news block a lead turn gets: <see cref="NewsHeading"/> and one line per notice; empty for none.</summary>
        public static string FormatNews(IReadOnlyList<SubAgentNotice> notices)
        {
            if (notices == null || notices.Count == 0)
            {
                return "";
            }

            StringBuilder text = new();
            text.Append(NewsHeading).Append('\n');
            text.Append("Your sub-agents reported since your last turn (this is not the player speaking):");
            foreach (SubAgentNotice notice in notices)
            {
                text.Append("\n- ").Append(notice.Text);
            }

            return text.ToString();
        }

        /// <summary>The user message of a wake turn: the notices and what to do with them.</summary>
        public static string FormatWakeText(IReadOnlyList<SubAgentNotice> notices)
        {
            StringBuilder text = new();
            text.Append("Sub-agent news (not from the player):");
            if (notices != null)
            {
                foreach (SubAgentNotice notice in notices)
                {
                    text.Append("\n- ").Append(notice.Text);
                }
            }

            text.Append("\nTell the player in a sentence or two what changed. If the job needs another step, take it.");
            return text.ToString();
        }

        /// <summary>Stops everything and refuses further work.</summary>
        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }
            }

            StopAllCore("stopped: the conversation closed");
            lock (_lock)
            {
                _disposed = true;
            }
        }

        internal Task EnterLeadTurnAsync(CancellationToken cancellationToken)
        {
            LeadWaiter waiter;
            lock (_lock)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return Task.FromCanceled(cancellationToken);
                }

                if (!_wakeRunning)
                {
                    _leadTurns++;
                    return Task.CompletedTask;
                }

                waiter = new LeadWaiter();
                _leadWaiters.Add(waiter);
            }

            if (cancellationToken.CanBeCanceled)
            {
                waiter.Registration = cancellationToken.Register(() => CancelLeadWaiter(waiter, cancellationToken));
            }

            return waiter.Source.Task;
        }

        internal void ExitLeadTurn()
        {
            bool due;
            lock (_lock)
            {
                if (_leadTurns > 0)
                {
                    _leadTurns--;
                }

                due = IsWakeDueLocked();
            }

            if (due)
            {
                RaiseChanged();
            }
        }

        internal bool TryBeginWake(out IReadOnlyList<SubAgentNotice> notices)
        {
            notices = Array.Empty<SubAgentNotice>();
            lock (_lock)
            {
                if (!IsWakeDueLocked())
                {
                    return false;
                }

                _wakePending = true;
                _wakes++;
                notices = new List<SubAgentNotice>(_notices).AsReadOnly();
                _notices.Clear();
            }

            RaiseChanged();
            return true;
        }

        internal void MarkWakeStarted()
        {
            lock (_lock)
            {
                _wakePending = false;
                _wakeRunning = true;
            }
        }

        internal void CancelWake(IReadOnlyList<SubAgentNotice> notices)
        {
            lock (_lock)
            {
                _wakePending = false;
                if (_wakes > 0)
                {
                    _wakes--;
                }

                if (notices != null && notices.Count > 0)
                {
                    _notices.InsertRange(0, notices);
                }
            }

            RaiseChanged();
        }

        internal void EndWake()
        {
            List<LeadWaiter> released;
            lock (_lock)
            {
                _wakeRunning = false;
                released = new List<LeadWaiter>(_leadWaiters);
                _leadWaiters.Clear();
                _leadTurns += released.Count;
            }

            foreach (LeadWaiter waiter in released)
            {
                waiter.Registration.Dispose();
                if (!waiter.Source.TrySetResult(true))
                {
                    ExitLeadTurn();
                }
            }

            RaiseChanged();
        }

        private bool StopAllCore(string reason)
        {
            List<CancellationTokenSource> cancel = new();
            List<LeadWaiter> waiters;
            OrchestrationScope scope;
            bool any;
            lock (_lock)
            {
                any = _notices.Count > 0 || _wakePending;
                foreach (SubAgentMember member in _members)
                {
                    if (member.State < SubAgentMemberState.Finished)
                    {
                        CancellationTokenSource source = StopMemberLocked(member, reason);
                        if (source != null)
                        {
                            cancel.Add(source);
                        }

                        any = true;
                    }
                }

                _notices.Clear();
                _wakePending = false;
                waiters = new List<LeadWaiter>(_leadWaiters);
                _leadWaiters.Clear();
                scope = _leadScope;
                _leadScope = new OrchestrationScope();
            }

            // WHY outside the lock: cancellation callbacks run the unwinding member and lead code inline.
            foreach (CancellationTokenSource source in cancel)
            {
                source.Cancel();
            }

            scope.Cancel(OrchestrationStopReason.PlayerStop);
            foreach (LeadWaiter waiter in waiters)
            {
                waiter.Registration.Dispose();
                waiter.Source.TrySetCanceled();
            }

            any |= waiters.Count > 0;
            if (any)
            {
                RaiseChanged();
            }

            return any;
        }

        private CancellationTokenSource StopMemberLocked(SubAgentMember member, string reason)
        {
            member.State = SubAgentMemberState.Stopped;
            member.Inbox.Clear();
            while (member.Reservations.Count > 0)
            {
                member.Reservations.Dequeue().Refund();
            }

            // WHY: the messages no turn has taken yet are dropped with the inbox; leaving them in the transcript
            // would hand them to the member as part of its conversation on a later continuation.
            for (int i = member.Transcript.Count - 1; i >= 0; i--)
            {
                SubAgentTranscriptEntry entry = member.Transcript[i];
                if (entry.Kind == SubAgentTranscriptKind.Message && entry.Turn > member.Turns)
                {
                    member.Transcript.RemoveAt(i);
                }
            }

            member.Append(SubAgentTranscriptKind.Stop, reason, member.Turns);
            CancellationTokenSource activity = member.Activity;
            member.Activity = null;
            return activity;
        }

        private void StartLoop(SubAgentMember member)
        {
            SynchronizationContext home = _home;
            if (home != null && !ReferenceEquals(SynchronizationContext.Current, home))
            {
                // WHY: a tool body can run with the host context cleared (a MEAI continuation completing inline, see
                // MeaiToolTaskBridge). A run started there awaits with no context, and in a WebGL player such a
                // continuation is queued to a thread pool that does not exist. The context the team was created on
                // (the host's main thread) starts it instead; it then resumes there after every await.
                home.Post(state => { _ = RunLoopAsync((SubAgentMember)state); }, member);
                return;
            }

            _ = RunLoopAsync(member);
        }

        private async Task RunLoopAsync(SubAgentMember member)
        {
            try
            {
                while (true)
                {
                    TurnClaim claim;
                    lock (_lock)
                    {
                        claim = ClaimTurnLocked(member);
                    }

                    if (claim == null)
                    {
                        RaiseChanged();
                        return;
                    }

                    bool admitted = false;
                    try
                    {
                        await _gate.WaitAsync(claim.Activity);
                        admitted = true;
                    }
                    catch (OperationCanceledException)
                    {
                        admitted = false;
                    }

                    TurnOutcome outcome;
                    if (!admitted)
                    {
                        outcome = TurnOutcome.StoppedBeforeStart;
                    }
                    else
                    {
                        try
                        {
                            outcome = await RunTurnAsync(member, claim);
                        }
                        finally
                        {
                            _gate.Release();
                        }
                    }

                    if (!Settle(member, claim, outcome))
                    {
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                // WHY: a loop failure must end in a state the lead can see and continue from, never in a member
                // that claims to work forever.
                _log.Warn($"[SubAgentTeam] {member.Name} stopped working: {ex.Message}", LogTag.Llm);
                lock (_lock)
                {
                    member.LoopRunning = false;
                    if (member.State < SubAgentMemberState.Finished)
                    {
                        member.State = SubAgentMemberState.Failed;
                        member.LastError = "The sub-agent stopped unexpectedly: " + ex.Message;
                    }
                }

                RaiseChanged();
            }
        }

        private TurnClaim ClaimTurnLocked(SubAgentMember member)
        {
            CancellationTokenSource activity = member.Activity;
            bool hasInput = member.Inbox.Count > 0 || (member.Turns == 0 && member.PendingTask.Length > 0);
            if (_disposed || activity == null || activity.IsCancellationRequested || !hasInput)
            {
                member.LoopRunning = false;
                if (member.State < SubAgentMemberState.Finished)
                {
                    member.State = SubAgentMemberState.Stopped;
                }

                return null;
            }

            TurnClaim claim = new()
            {
                Activity = activity.Token,
                Ledger = member.Reservations.Count > 0 ? member.Reservations.Dequeue() : _ledger,
                Turn = member.Turns + 1,
                Task = member.Turns == 0 ? member.PendingTask : "",
                Messages = new List<string>(member.Inbox)
            };
            member.Inbox.Clear();
            member.State = SubAgentMemberState.Queued;
            return claim;
        }

        private async Task<TurnOutcome> RunTurnAsync(SubAgentMember member, TurnClaim claim)
        {
            AiTaskRequest request;
            lock (_lock)
            {
                if (claim.Activity.IsCancellationRequested || _disposed)
                {
                    return TurnOutcome.StoppedBeforeStart;
                }

                string hint = ComposeHintLocked(member, claim);
                request = SubAgentRequestFactory.Create(member.Definition, hint, "", member.Parent, Id, member.Name,
                    claim.Turn, Options.DelegateToolName, _guardedToolNames);
                request.Progress = new LlmRequestProgress();
                member.State = SubAgentMemberState.Working;
                member.Turns = claim.Turn;
                member.Steps = 0;
                member.PendingTask = "";
                member.TraceId = request.TraceId;
                member.Progress = request.Progress;
            }

            RaiseChanged();
            CancellationTokenSource deadline = new();
            CancellationTokenSource deadlineStop = new();
            CancellationTokenSource run = CancellationTokenSource.CreateLinkedTokenSource(claim.Activity, deadline.Token);
            _ = ArmDeadlineAsync(member.Definition.Deadline, deadline, deadlineStop.Token);
            _marker.Enter();
            LlmCompletionResult result = null;
            Exception failure = null;
            try
            {
                result = await _runner.RunTaskResultAsync(request, run.Token);
            }
            catch (OperationCanceledException)
            {
                result = null;
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                deadlineStop.Cancel();
                run.Dispose();
            }

            int? tokens = ResolveTokens(result);
            if (result != null && result.Ok)
            {
                return TurnOutcome.Answered(result.Content, tokens);
            }

            if (claim.Activity.IsCancellationRequested)
            {
                return TurnOutcome.Stopped(tokens);
            }

            string message = deadline.IsCancellationRequested
                ? $"it did not finish within {member.Definition.Deadline.TotalSeconds:0.#} s."
                : failure != null
                    ? "its run failed: " + failure.GetBaseException().Message
                    : "its run failed: " + DescribeFailure(result);
            return TurnOutcome.Failed(message, tokens);
        }

        private bool Settle(SubAgentMember member, TurnClaim claim, TurnOutcome outcome)
        {
            SubAgentNotice notice = null;
            bool again;
            lock (_lock)
            {
                if (outcome.Ran)
                {
                    claim.Ledger.RecordCompletion(outcome.Tokens);
                }
                else
                {
                    claim.Ledger.Refund();
                }

                bool stopped = outcome.WasStopped || claim.Activity.IsCancellationRequested;
                if (outcome.Ok)
                {
                    member.LastAnswer = SubAgentDelegator.Clip(outcome.Content, member.Definition.MaxResultChars,
                        out _);
                    member.LastError = "";
                    member.Append(SubAgentTranscriptKind.Answer, member.LastAnswer, claim.Turn);
                }
                else if (!stopped)
                {
                    member.LastError = outcome.Error;
                    member.Append(SubAgentTranscriptKind.Failure, outcome.Error, claim.Turn);
                }

                if (_disposed)
                {
                    member.LoopRunning = false;
                    again = false;
                }
                else if (stopped)
                {
                    // WHY: a message sent after the stop resumed the member while this cancelled turn unwound.
                    again = member.State == SubAgentMemberState.Queued && member.Inbox.Count > 0 &&
                            member.Activity != null && !member.Activity.IsCancellationRequested;
                    if (!again)
                    {
                        member.LoopRunning = false;
                        if (member.State < SubAgentMemberState.Finished)
                        {
                            member.State = SubAgentMemberState.Stopped;
                        }
                    }
                }
                else if (member.Inbox.Count > 0)
                {
                    member.State = SubAgentMemberState.Queued;
                    again = true;
                }
                else
                {
                    member.State = outcome.Ok ? SubAgentMemberState.Finished : SubAgentMemberState.Failed;
                    member.LoopRunning = false;
                    notice = new SubAgentNotice(member.Name, outcome.Ok, outcome.Ok
                        ? member.Name + " finished: " + member.LastAnswer
                        : member.Name + " failed: " + member.LastError);
                    _notices.Add(notice);
                    again = false;
                }
            }

            RaiseChanged();
            if (notice != null)
            {
                RaiseNoticeQueued(notice);
            }

            return again;
        }

        private string ComposeHintLocked(SubAgentMember member, TurnClaim claim)
        {
            StringBuilder text = new();
            if (claim.Turn == 1)
            {
                text.Append(claim.Task);
                if (claim.Messages.Count > 0)
                {
                    text.Append(text.Length > 0 ? "\n\n" : "").Append("More from the lead:");
                    foreach (string message in claim.Messages)
                    {
                        text.Append("\n- ").Append(message);
                    }
                }

                return text.ToString();
            }

            List<string> lines = new();
            foreach (SubAgentTranscriptEntry entry in member.Transcript)
            {
                if (entry.Turn >= claim.Turn || entry.Kind == SubAgentTranscriptKind.Tool)
                {
                    continue;
                }

                lines.Add(RenderConversationLine(entry));
            }

            text.Append("You are continuing a job. Your conversation with the lead so far:\n");
            text.Append(BoundConversation(lines, Math.Max(200, Options.MaxContinuationChars)));
            text.Append("\n\nNew message from the lead:");
            foreach (string message in claim.Messages)
            {
                text.Append('\n').Append(message);
            }

            return text.ToString();
        }

        private static string RenderConversationLine(SubAgentTranscriptEntry entry)
        {
            switch (entry.Kind)
            {
                case SubAgentTranscriptKind.Task:
                    return "[lead, task] " + entry.Text;
                case SubAgentTranscriptKind.Message:
                    return "[lead] " + entry.Text;
                case SubAgentTranscriptKind.Answer:
                    return "[you] " + entry.Text;
                case SubAgentTranscriptKind.Failure:
                    return "[your turn failed] " + entry.Text;
                default:
                    return "[" + entry.Text + "]";
            }
        }

        /// <summary>
        /// Keeps the first line (the task) and as many of the newest lines as fit in <paramref name="maxChars"/>,
        /// with a marker where older lines were left out.
        /// </summary>
        internal static string BoundConversation(List<string> lines, int maxChars)
        {
            if (lines.Count == 0)
            {
                return "";
            }

            string first = SubAgentDelegator.Clip(lines[0], maxChars / 2, out _);
            int budget = maxChars - first.Length;
            List<string> newest = new();
            int dropped = 0;
            for (int i = lines.Count - 1; i >= 1; i--)
            {
                if (lines[i].Length + 1 <= budget)
                {
                    newest.Insert(0, lines[i]);
                    budget -= lines[i].Length + 1;
                }
                else
                {
                    dropped = i;
                    break;
                }
            }

            StringBuilder text = new(first);
            if (dropped > 0)
            {
                text.Append("\n[... ").Append(dropped).Append(" earlier lines left out ...]");
            }

            foreach (string line in newest)
            {
                text.Append('\n').Append(line);
            }

            return text.ToString();
        }

        private string RenderOutputLocked(SubAgentMember member, int cursor, out int skipped)
        {
            skipped = 0;
            int budget = Math.Max(100, Options.MaxReadChars);
            List<string> lines = new();
            for (int i = member.Transcript.Count - 1; i >= 0; i--)
            {
                SubAgentTranscriptEntry entry = member.Transcript[i];
                if (entry.Seq <= cursor)
                {
                    break;
                }

                if (!entry.IsMemberOutput)
                {
                    continue;
                }

                string line = RenderOutputLine(entry);
                if (skipped == 0 && line.Length + 1 <= budget)
                {
                    lines.Insert(0, line);
                    budget -= line.Length + 1;
                }
                else if (skipped == 0 && lines.Count == 0)
                {
                    // WHY the tail: the end of an answer usually says what was done; the reader asked for the newest.
                    string tail = line.Substring(line.Length - budget);
                    lines.Insert(0, "..." + tail.Substring(Math.Min(3, tail.Length)));
                    skipped += line.Length - budget;
                    budget = 0;
                }
                else
                {
                    skipped += line.Length;
                }
            }

            return string.Join("\n", lines);
        }

        private static string RenderOutputLine(SubAgentTranscriptEntry entry)
        {
            switch (entry.Kind)
            {
                case SubAgentTranscriptKind.Answer:
                    return "[answer] " + entry.Text;
                case SubAgentTranscriptKind.Failure:
                    return "[failed] " + entry.Text;
                case SubAgentTranscriptKind.Tool:
                    return "[tool] " + entry.Text;
                default:
                    return "[" + entry.Text + "]";
            }
        }

        private void AppendBoundedLocked(SubAgentMember member, SubAgentTranscriptKind kind, string text, int turn)
        {
            member.Append(kind, text, turn);
            if (member.Transcript.Count <= MaxTranscriptEntries)
            {
                return;
            }

            for (int i = 0; i < member.Transcript.Count; i++)
            {
                if (member.Transcript[i].Kind == SubAgentTranscriptKind.Tool)
                {
                    member.Transcript.RemoveAt(i);
                    return;
                }
            }

            member.Transcript.RemoveAt(Math.Min(1, member.Transcript.Count - 1));
        }

        private async Task ArmDeadlineAsync(TimeSpan deadline, CancellationTokenSource expire, CancellationToken stop)
        {
            double totalMs = Math.Ceiling(deadline.TotalMilliseconds);
            int milliseconds = totalMs >= int.MaxValue ? int.MaxValue : Math.Max(1, (int)totalMs);
            try
            {
                await _asyncMarshaler.DelayAsync(milliseconds, stop);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // WHY: as in SubAgentDelegator, a faulted host delay is not an expired deadline.
                return;
            }

            if (!stop.IsCancellationRequested)
            {
                expire.Cancel();
            }
        }

        private bool TryMakeRoomLocked()
        {
            if (_members.Count < Math.Max(1, Options.MaxTeamMembers))
            {
                return true;
            }

            for (int i = 0; i < _members.Count; i++)
            {
                SubAgentMember candidate = _members[i];
                if (!candidate.LoopRunning && candidate.State >= SubAgentMemberState.Finished)
                {
                    _members.RemoveAt(i);
                    return true;
                }
            }

            return false;
        }

        private string NextNameLocked(string subAgentId)
        {
            _counters.TryGetValue(subAgentId, out int count);
            count++;
            _counters[subAgentId] = count;
            return subAgentId + "-" + count;
        }

        private SubAgentMember FindLocked(string agent)
        {
            string name = agent?.Trim() ?? "";
            if (name.Length == 0)
            {
                return null;
            }

            SubAgentMember byType = null;
            int ofType = 0;
            foreach (SubAgentMember member in _members)
            {
                if (string.Equals(member.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return member;
                }

                if (string.Equals(member.Definition.Id, name, StringComparison.OrdinalIgnoreCase))
                {
                    byType = member;
                    ofType++;
                }
            }

            // WHY: a small model often writes the type ("builder") for the only member of that type.
            return ofType == 1 ? byType : null;
        }

        private SubAgentControlResult RefuseUnknownLocked(SubAgentControlAction action, string agent)
        {
            List<string> names = new(_members.Count);
            foreach (SubAgentMember member in _members)
            {
                names.Add(member.Name);
            }

            string known = names.Count == 0
                ? " You have no sub-agents; start one with " + Options.StartToolName + "."
                : " Your sub-agents: " + string.Join(", ", names) + ".";
            string requested = agent?.Trim() ?? "";
            return SubAgentControlResult.Refused(action,
                requested.Length == 0 ? SubAgentErrorCode.InvalidArguments : SubAgentErrorCode.UnknownAgent,
                (requested.Length == 0 ? "'agent' is required." : $"No sub-agent is called '{requested}'.") + known,
                requested);
        }

        private static SubAgentControlResult RefuseNested(SubAgentControlAction action)
        {
            return SubAgentControlResult.Refused(action, SubAgentErrorCode.NestedDelegation,
                "A sub-agent cannot control sub-agents: finish your task yourself with the tools you have.");
        }

        private static SubAgentControlResult RefuseClosed(SubAgentControlAction action)
        {
            return SubAgentControlResult.Refused(action, SubAgentErrorCode.PlanNotActive,
                "This conversation is closed; no sub-agent can start in it.");
        }

        private string DescribeBudgetLocked(SubAgentErrorCode budget)
        {
            OrchestrationBudgetSnapshot spent = _ledger.Snapshot();
            return budget == SubAgentErrorCode.TokenBudgetExhausted
                ? $"The sub-agent token budget of this player message is spent ({spent.TokensUsed} of " +
                  $"{spent.MaxTotalTokens}). Tell the player what is done; their next message opens a new budget."
                : $"The sub-agent budget of this player message is spent ({spent.MaxSubAgentCalls} runs). " +
                  "Tell the player what is done; their next message opens a new budget.";
        }

        private string DescribeTypes()
        {
            IReadOnlyList<SubAgentDefinition> definitions = _registry.Definitions;
            if (definitions.Count == 0)
            {
                return "none are registered";
            }

            List<string> ids = new(definitions.Count);
            foreach (SubAgentDefinition definition in definitions)
            {
                ids.Add(definition.Id);
            }

            return string.Join(", ", ids);
        }

        private bool IsWakeDueLocked()
        {
            return !_disposed && _notices.Count > 0 && _leadTurns == 0 && !_wakePending && !_wakeRunning &&
                   _wakes < Math.Max(0, Options.MaxWakeTurnsPerInstruction);
        }

        private SubAgentParentContext DefaultParent()
        {
            return new SubAgentParentContext("", "team." + Id);
        }

        private void CancelLeadWaiter(LeadWaiter waiter, CancellationToken cancellationToken)
        {
            bool removed;
            lock (_lock)
            {
                removed = _leadWaiters.Remove(waiter);
            }

            if (removed)
            {
                waiter.Source.TrySetCanceled(cancellationToken);
            }
        }

        private void RaiseChanged()
        {
            Action handlers = Changed;
            if (handlers == null)
            {
                return;
            }

            foreach (Delegate handler in handlers.GetInvocationList())
            {
                try
                {
                    ((Action)handler)();
                }
                catch (Exception ex)
                {
                    // WHY: an observer runs inside a member's bookkeeping; its exception must not wedge the loop.
                    _log.Warn($"[SubAgentTeam] status observer failed: {ex.Message}", LogTag.Llm);
                }
            }
        }

        private void RaiseNoticeQueued(SubAgentNotice notice)
        {
            Action<SubAgentNotice> handlers = NoticeQueued;
            if (handlers == null)
            {
                return;
            }

            foreach (Delegate handler in handlers.GetInvocationList())
            {
                try
                {
                    ((Action<SubAgentNotice>)handler)(notice);
                }
                catch (Exception ex)
                {
                    _log.Warn($"[SubAgentTeam] notice observer failed: {ex.Message}", LogTag.Llm);
                }
            }
        }

        private static string DescribeFailure(LlmCompletionResult result)
        {
            if (result == null)
            {
                return "no result was returned.";
            }

            string error = string.IsNullOrWhiteSpace(result.Error) ? "no error text" : result.Error.Trim();
            return result.ErrorCode == LlmErrorCode.None ? error : result.ErrorCode + ": " + error;
        }

        private static int? ResolveTokens(LlmCompletionResult result)
        {
            if (result == null)
            {
                return null;
            }

            if (result.TotalTokens.HasValue)
            {
                return result.TotalTokens.Value;
            }

            if (result.PromptTokens.HasValue || result.CompletionTokens.HasValue)
            {
                return (result.PromptTokens ?? 0) + (result.CompletionTokens ?? 0);
            }

            return null;
        }

        private sealed class TurnClaim
        {
            public CancellationToken Activity;
            public OrchestrationBudgetLedger Ledger;
            public int Turn;
            public string Task = "";
            public List<string> Messages = new();
        }

        private sealed class TurnOutcome
        {
            public static readonly TurnOutcome StoppedBeforeStart = new() { WasStopped = true };

            public bool Ran;
            public bool Ok;
            public bool WasStopped;
            public string Content = "";
            public string Error = "";
            public int? Tokens;

            public static TurnOutcome Answered(string content, int? tokens)
            {
                return new TurnOutcome { Ran = true, Ok = true, Content = content ?? "", Tokens = tokens };
            }

            public static TurnOutcome Stopped(int? tokens)
            {
                return new TurnOutcome { Ran = true, WasStopped = true, Tokens = tokens };
            }

            public static TurnOutcome Failed(string error, int? tokens)
            {
                return new TurnOutcome { Ran = true, Error = error ?? "", Tokens = tokens };
            }
        }

        private sealed class LeadWaiter
        {
            public readonly TaskCompletionSource<bool> Source = new(TaskCreationOptions.None);
            public CancellationTokenRegistration Registration;
        }
    }
}
