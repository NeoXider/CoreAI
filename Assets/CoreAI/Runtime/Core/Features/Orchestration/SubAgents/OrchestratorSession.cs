using System;
using System.Collections.Generic;
using System.Threading;
using CoreAI.Logging;

namespace CoreAI.Ai.SubAgents
{
    /// <summary>What happened to a player instruction handed to <see cref="OrchestratorSession.Submit"/>.</summary>
    public enum InstructionDisposition
    {
        /// <summary>No plan was running: a new plan started; run the lead turn for it now.</summary>
        Started = 0,

        /// <summary>A plan is running: the instruction waits in the queue and does not touch the running work.</summary>
        Queued = 1,

        /// <summary>The instruction was refused (blank, or the queue is full).</summary>
        Refused = 2
    }

    /// <summary>The answer to <see cref="OrchestratorSession.Submit"/>.</summary>
    public sealed class InstructionSubmission
    {
        internal InstructionSubmission(
            InstructionDisposition disposition,
            OrchestrationPlan plan,
            QueuedInstructionSnapshot queued,
            string refusal)
        {
            Disposition = disposition;
            Plan = plan;
            Queued = queued;
            Refusal = refusal ?? "";
        }

        /// <summary>What happened.</summary>
        public InstructionDisposition Disposition { get; }

        /// <summary>The plan that started; <c>null</c> unless <see cref="InstructionDisposition.Started"/>.</summary>
        public OrchestrationPlan Plan { get; }

        /// <summary>The queued entry; <c>null</c> unless <see cref="InstructionDisposition.Queued"/>.</summary>
        public QueuedInstructionSnapshot Queued { get; }

        /// <summary>Why it was refused; empty otherwise.</summary>
        public string Refusal { get; }
    }

    /// <summary>
    /// Orchestrator mode for one conversation: at most one running plan, the queue of player instructions
    /// that arrived meanwhile, and the delegate tool bound to this session. It is the
    /// <see cref="IOrchestrationStatusSource"/> the HUD and the chat read.
    /// <para>
    /// The host's loop is: <see cref="Submit"/> a player instruction; on
    /// <see cref="InstructionDisposition.Started"/> run the lead turn built by <see cref="CreateLeadRequest"/>
    /// (the lead tools and instructions added to the host's own request) with its token linked to the plan's
    /// <see cref="OrchestrationScope.Token"/>; when the lead
    /// turn returns call <see cref="CompletePlan"/> (which also stops any run the lead left behind); then
    /// <see cref="TryStartNext"/> for a queued instruction. Stop is <see cref="Stop"/>.
    /// </para>
    /// </summary>
    public sealed class OrchestratorSession : IOrchestrationStatusSource, IDisposable
    {
        private readonly object _lock = new();
        private readonly ILog _log;
        private readonly ILeadInstructionsProvider _leadInstructions;
        private OrchestrationPlan _activePlan;
        private OrchestrationPlan _lastPlan;
        private long _revision;
        private bool _disposed;

        /// <summary>Creates a session.</summary>
        /// <param name="conversationId">The conversation this session serves.</param>
        /// <param name="options">Mode options; budgets are copied when each plan begins.</param>
        /// <param name="registry">Sub-agents the lead may delegate to.</param>
        /// <param name="subAgentRunner">
        /// Runs one sub-agent turn. Compose it with the orchestrator BELOW the admission queue (the
        /// <see cref="AiOrchestrator"/> itself): the lead's turn already holds a queue slot and is idle while it
        /// waits for the tool result, and a queued sub-agent would need a second slot the lead may never free.
        /// </param>
        /// <param name="asyncMarshaler">Host delay for deadlines; a Unity host passes its frame-driven marshaler.</param>
        /// <param name="log">Log for observer failures; null logs nothing.</param>
        /// <param name="leadInstructions">Lead instructions; null uses <see cref="DefaultLeadInstructionsProvider"/>.</param>
        public OrchestratorSession(
            string conversationId,
            OrchestratorModeOptions options,
            ISubAgentRegistry registry,
            IAiTaskResultService subAgentRunner,
            ILlmAsyncMarshaler asyncMarshaler = null,
            ILog log = null,
            ILeadInstructionsProvider leadInstructions = null)
        {
            ConversationId = conversationId?.Trim() ?? "";
            Options = options ?? throw new ArgumentNullException(nameof(options));
            Registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _log = log ?? NullLog.Instance;
            _leadInstructions = leadInstructions ?? new DefaultLeadInstructionsProvider();
            Queue = new PlayerInstructionQueue(options.MaxQueuedInstructions);
            Queue.Changed += NotifyChanged;
            Delegator = new SubAgentDelegator(this, registry, subAgentRunner, asyncMarshaler);
            DelegateTool = new DelegateTaskTool(Delegator, registry, options.DelegateToolName);
            DeclareTool = new DeclareTasksTool(this, registry, options.DeclareToolName);
            LeadTools = new ILlmTool[] { DelegateTool, DeclareTool };
        }

        /// <summary>The conversation this session serves.</summary>
        public string ConversationId { get; }

        /// <summary>Mode options.</summary>
        public OrchestratorModeOptions Options { get; }

        /// <summary>Player instructions waiting behind the running plan.</summary>
        public PlayerInstructionQueue Queue { get; }

        /// <summary>Runs delegations for this session's plans.</summary>
        public SubAgentDelegator Delegator { get; }

        /// <summary>
        /// The <c>delegate_task</c> tool bound to this session. Offer it on the lead turn's request only; it is
        /// never registered on a role, so a sub-agent request never carries it.
        /// </summary>
        public DelegateTaskTool DelegateTool { get; }

        /// <summary>The <c>declare_tasks</c> tool bound to this session; lead request only, like <see cref="DelegateTool"/>.</summary>
        public DeclareTasksTool DeclareTool { get; }

        /// <summary>The tools a lead turn of this session gets as request tools: delegate and declare.</summary>
        public IReadOnlyList<ILlmTool> LeadTools { get; }

        /// <summary>Sub-agents the lead may delegate to.</summary>
        public ISubAgentRegistry Registry { get; }

        /// <summary>The running plan, or <c>null</c>.</summary>
        public OrchestrationPlan ActivePlan
        {
            get
            {
                lock (_lock)
                {
                    return _activePlan;
                }
            }
        }

        /// <summary>The most recent finished plan, or <c>null</c>.</summary>
        public OrchestrationPlan LastPlan
        {
            get
            {
                lock (_lock)
                {
                    return _lastPlan;
                }
            }
        }

        /// <inheritdoc />
        public event Action<OrchestrationStatus> Changed;

        /// <inheritdoc />
        public OrchestrationStatus Current
        {
            get
            {
                OrchestrationPlan plan;
                long revision;
                lock (_lock)
                {
                    plan = _activePlan ?? _lastPlan;
                }

                revision = Interlocked.Read(ref _revision);

                return new OrchestrationStatus(ConversationId, revision, plan?.Snapshot(), Queue.Snapshot());
            }
        }

        /// <summary>
        /// Hands a player instruction to the session: it starts a plan when none is running, and otherwise
        /// waits in <see cref="Queue"/> without touching the running plan.
        /// </summary>
        /// <param name="text">The player's instruction.</param>
        /// <param name="parent">The lead turn that would start now (its trace, scope and actor).</param>
        public InstructionSubmission Submit(string text, SubAgentParentContext parent)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return new InstructionSubmission(InstructionDisposition.Refused, null, null,
                    PlayerInstructionQueue.EmptyInstructionRefusal);
            }

            if (TryBeginPlanCore(text, parent, null, out OrchestrationPlan plan))
            {
                return new InstructionSubmission(InstructionDisposition.Started, plan, null, "");
            }

            InstructionQueueResult queued = Queue.Enqueue(text);
            return queued.Accepted
                ? new InstructionSubmission(InstructionDisposition.Queued, null, queued.Entry, "")
                : new InstructionSubmission(InstructionDisposition.Refused, null, null, queued.Refusal);
        }

        /// <summary>
        /// The lead turn's request for <paramref name="plan"/>: a copy of the host's own request (its role, hint,
        /// actor, history and routing stay as they are) with <see cref="LeadTools"/> added to its request tools
        /// and the lead instructions appended to its request system instructions; a host allowlist gets the lead
        /// tool names added, so narrowing the lead's tools never removes delegation. A request without a trace id
        /// takes the plan's lead trace, so status, deadlines and sub-agent traces agree, and the request's progress
        /// is the plan's <see cref="OrchestrationPlan.Progress"/>. Cancellation is the host's: pass a token linked to
        /// <see cref="OrchestrationScope.Token"/> when running it (<see cref="OrchestratorChatDriver"/> does).
        /// </summary>
        public AiTaskRequest CreateLeadRequest(AiTaskRequest hostRequest, OrchestrationPlan plan)
        {
            if (hostRequest == null)
            {
                throw new ArgumentNullException(nameof(hostRequest));
            }

            if (plan == null)
            {
                throw new ArgumentNullException(nameof(plan));
            }

            AiTaskRequest request = hostRequest.Clone();
            List<ILlmTool> tools = new();
            if (hostRequest.RequestTools != null)
            {
                foreach (ILlmTool tool in hostRequest.RequestTools)
                {
                    if (tool != null && !IsLeadToolName(tool.Name))
                    {
                        tools.Add(tool);
                    }
                }
            }

            tools.AddRange(LeadTools);
            request.RequestTools = tools;
            if (hostRequest.AllowedToolNames != null)
            {
                // WHY: request tools pass the allowlist like role tools; a host that narrowed the lead's tools
                // still means it to plan and delegate, so the lead tools join its list instead of vanishing.
                List<string> allowed = new(hostRequest.AllowedToolNames);
                foreach (ILlmTool tool in LeadTools)
                {
                    if (!allowed.Contains(tool.Name))
                    {
                        allowed.Add(tool.Name);
                    }
                }

                request.AllowedToolNames = allowed.ToArray();
            }

            string instructions = _leadInstructions.BuildInstructions(new LeadInstructionsContext(
                Registry.Definitions,
                DelegateTool.Name,
                DeclareTool.Name,
                plan.Budget.MaxSubAgentCalls,
                plan.Budget.MaxParallelSubAgents))?.Trim() ?? "";
            string existing = hostRequest.RequestSystemInstructions?.Trim() ?? "";
            request.RequestSystemInstructions = existing.Length == 0
                ? instructions
                : instructions.Length == 0
                    ? existing
                    : existing + "\n\n" + instructions;

            if (string.IsNullOrWhiteSpace(request.TraceId) && plan.Parent.LeadTraceId.Length > 0)
            {
                request.TraceId = plan.Parent.LeadTraceId;
            }

            // WHY: the lead streams nothing while a delegation runs; the plan's progress (every task start, step
            // and finish) keeps its idle deadline from cutting it off, while a stalled plan still times out.
            request.Progress = plan.Progress;

            return request;
        }

        /// <summary>Starts a plan for <paramref name="goal"/>; throws when one is already running.</summary>
        public OrchestrationPlan BeginPlan(string goal, SubAgentParentContext parent, string planId = null)
        {
            if (!TryBeginPlanCore(goal, parent, planId, out OrchestrationPlan plan))
            {
                throw new InvalidOperationException(
                    $"Conversation '{ConversationId}' already runs a plan; queue the instruction instead.");
            }

            return plan;
        }

        /// <summary>
        /// When no plan is running and an instruction is queued, takes the front one and starts its plan.
        /// </summary>
        public bool TryStartNext(
            SubAgentParentContext parent,
            out OrchestrationPlan plan,
            out QueuedInstructionSnapshot instruction)
        {
            plan = null;
            instruction = null;
            if (ActivePlan != null || !Queue.TryDequeue(out instruction))
            {
                return false;
            }

            if (TryBeginPlanCore(instruction.Text, parent, null, out plan))
            {
                return true;
            }

            // WHY: a plan started between the check and the dequeue; the instruction goes back to the front
            // rather than being lost.
            InstructionQueueResult requeued = Queue.Enqueue(instruction.Text);
            if (requeued.Accepted)
            {
                Queue.Promote(requeued.Entry.Id);
            }

            instruction = null;
            return false;
        }

        /// <summary>
        /// Ends the running plan with <paramref name="outcome"/> once the lead turn has returned, and stops every
        /// run the lead left behind. Returns false when no plan was running.
        /// </summary>
        public bool CompletePlan(OrchestrationPlanState outcome)
        {
            OrchestrationPlan plan = DetachActivePlan();
            if (plan == null)
            {
                return false;
            }

            OrchestrationPlanState terminal = outcome == OrchestrationPlanState.Running
                ? OrchestrationPlanState.Completed
                : outcome;
            plan.TryFinish(terminal);
            plan.Scope.Cancel(terminal == OrchestrationPlanState.Failed
                ? OrchestrationStopReason.LeadFailed
                : terminal == OrchestrationPlanState.Cancelled
                    ? OrchestrationStopReason.PlayerStop
                    : OrchestrationStopReason.PlanCompleted);
            NotifyChanged();
            return true;
        }

        /// <summary>
        /// Stop: cancels the lead (when linked) and every sub-agent run of the running plan through its one
        /// scope, and by default also drops the queued instructions. Returns whether anything was stopped.
        /// </summary>
        public bool Stop(bool clearQueue = true)
        {
            return StopCore(OrchestrationStopReason.PlayerStop, clearQueue);
        }

        /// <summary>Stops everything and detaches the queue observer.</summary>
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

            StopCore(OrchestrationStopReason.SessionDisposed, true);
            Queue.Changed -= NotifyChanged;
        }

        private bool StopCore(OrchestrationStopReason reason, bool clearQueue)
        {
            OrchestrationPlan plan = DetachActivePlan();
            bool stopped = false;
            if (plan != null)
            {
                plan.TryFinish(OrchestrationPlanState.Cancelled);
                plan.Scope.Cancel(reason);
                stopped = true;
            }

            if (clearQueue && Queue.CancelAll().Count > 0)
            {
                stopped = true;
            }

            if (plan != null)
            {
                NotifyChanged();
            }

            return stopped;
        }

        private bool TryBeginPlanCore(string goal, SubAgentParentContext parent, string planId,
            out OrchestrationPlan plan)
        {
            if (parent == null)
            {
                throw new ArgumentNullException(nameof(parent));
            }

            lock (_lock)
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(OrchestratorSession));
                }

                if (_activePlan != null)
                {
                    plan = null;
                    return false;
                }

                plan = new OrchestrationPlan(planId, ConversationId, goal, parent, Options.CreateBudget(),
                    NotifyChanged);
                _activePlan = plan;
            }

            NotifyChanged();
            return true;
        }

        private bool IsLeadToolName(string name)
        {
            return string.Equals(name, DelegateTool.Name, StringComparison.Ordinal) ||
                   string.Equals(name, DeclareTool.Name, StringComparison.Ordinal);
        }

        private OrchestrationPlan DetachActivePlan()
        {
            lock (_lock)
            {
                OrchestrationPlan plan = _activePlan;
                if (plan != null)
                {
                    _lastPlan = plan;
                    _activePlan = null;
                }

                return plan;
            }
        }

        private void NotifyChanged()
        {
            Interlocked.Increment(ref _revision);
            Action<OrchestrationStatus> handlers = Changed;
            if (handlers == null)
            {
                return;
            }

            OrchestrationStatus status = Current;
            foreach (Delegate handler in handlers.GetInvocationList())
            {
                try
                {
                    ((Action<OrchestrationStatus>)handler)(status);
                }
                catch (Exception ex)
                {
                    // WHY: an observer runs inside a delegation's bookkeeping (slot release, task state); letting
                    // a HUD exception escape there would leak the slot and wedge every later delegation.
                    _log.Warn($"[OrchestratorSession] status observer failed: {ex.Message}", LogTag.Llm);
                }
            }
        }
    }
}
