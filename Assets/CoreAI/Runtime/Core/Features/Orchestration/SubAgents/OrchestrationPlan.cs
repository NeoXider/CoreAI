using System;
using System.Collections.Generic;

namespace CoreAI.Ai.SubAgents
{
    /// <summary>Lifecycle of a plan. Every state other than <see cref="Running"/> is terminal.</summary>
    public enum OrchestrationPlanState
    {
        /// <summary>The lead is working; delegations are accepted.</summary>
        Running = 0,

        /// <summary>The lead finished.</summary>
        Completed = 1,

        /// <summary>The lead turn failed.</summary>
        Failed = 2,

        /// <summary>The plan was stopped.</summary>
        Cancelled = 3,

        /// <summary>A budget ran out; no further delegation is accepted and the lead reports what it has.</summary>
        BudgetExhausted = 4
    }

    /// <summary>Lifecycle of one plan task.</summary>
    public enum PlanTaskState
    {
        /// <summary>Declared by the lead, not delegated yet.</summary>
        Pending = 0,

        /// <summary>Delegated and waiting for a free slot.</summary>
        Waiting = 1,

        /// <summary>A sub-agent is working on it.</summary>
        Running = 2,

        /// <summary>The sub-agent answered.</summary>
        Succeeded = 3,

        /// <summary>The sub-agent failed or ran out of time.</summary>
        Failed = 4,

        /// <summary>Stopped before it finished.</summary>
        Cancelled = 5,

        /// <summary>Refused before it started (budget, busy task).</summary>
        Refused = 6
    }

    /// <summary>An immutable view of one plan task, for the HUD.</summary>
    public sealed class PlanTaskSnapshot
    {
        internal PlanTaskSnapshot(PlanTask task)
        {
            Id = task.Id;
            Title = task.Title;
            SubAgentId = task.SubAgentId;
            State = task.State;
            Attempts = task.Attempts;
            TraceId = task.TraceId;
            Steps = task.Steps;
            ResultSummary = task.ResultSummary;
            ErrorCode = task.ErrorCode;
        }

        /// <summary>Plan-local task id (<c>t1</c>, <c>t2</c>, ...).</summary>
        public string Id { get; }

        /// <summary>Short title for the HUD.</summary>
        public string Title { get; }

        /// <summary>Sub-agent the task was (or will be) handed to.</summary>
        public string SubAgentId { get; }

        /// <summary>Current state.</summary>
        public PlanTaskState State { get; }

        /// <summary>How many runs the task has started.</summary>
        public int Attempts { get; }

        /// <summary>Trace id of the latest run; empty before the first run.</summary>
        public string TraceId { get; }

        /// <summary>Tool activity recorded for the latest run (see <see cref="OrchestrationPlan.TryRecordActivity"/>).</summary>
        public int Steps { get; }

        /// <summary>First line of the latest answer, clipped for the HUD.</summary>
        public string ResultSummary { get; }

        /// <summary>Why the latest run did not answer; <see cref="SubAgentErrorCode.None"/> otherwise.</summary>
        public SubAgentErrorCode ErrorCode { get; }

        /// <summary>True when the task is finished in any way (answered, failed, stopped, refused).</summary>
        public bool IsTerminal => State >= PlanTaskState.Succeeded;
    }

    /// <summary>An immutable view of a plan, for the HUD and for tests.</summary>
    public sealed class OrchestrationPlanSnapshot
    {
        internal OrchestrationPlanSnapshot(
            OrchestrationPlan plan,
            OrchestrationPlanState state,
            OrchestrationStopReason stopReason,
            SubAgentErrorCode exhaustedBudget,
            IReadOnlyList<PlanTaskSnapshot> tasks,
            OrchestrationBudgetSnapshot budget)
        {
            PlanId = plan.Id;
            ConversationId = plan.ConversationId;
            Goal = plan.Goal;
            LeadTraceId = plan.Parent.LeadTraceId;
            State = state;
            StopReason = stopReason;
            ExhaustedBudget = exhaustedBudget;
            Tasks = tasks;
            Budget = budget;
            int completed = 0;
            int running = 0;
            foreach (PlanTaskSnapshot task in tasks)
            {
                if (task.State == PlanTaskState.Succeeded)
                {
                    completed++;
                }
                else if (task.State == PlanTaskState.Running || task.State == PlanTaskState.Waiting)
                {
                    running++;
                }
            }

            CompletedTasks = completed;
            ActiveTasks = running;
        }

        /// <summary>Plan id.</summary>
        public string PlanId { get; }

        /// <summary>Conversation the plan belongs to.</summary>
        public string ConversationId { get; }

        /// <summary>The player's instruction that started the plan; never sent to a sub-agent.</summary>
        public string Goal { get; }

        /// <summary>Trace id of the lead turn.</summary>
        public string LeadTraceId { get; }

        /// <summary>Plan state.</summary>
        public OrchestrationPlanState State { get; }

        /// <summary>Why the plan's scope was cancelled; <see cref="OrchestrationStopReason.None"/> while live.</summary>
        public OrchestrationStopReason StopReason { get; }

        /// <summary>The budget that ran out, when <see cref="State"/> is <see cref="OrchestrationPlanState.BudgetExhausted"/>.</summary>
        public SubAgentErrorCode ExhaustedBudget { get; }

        /// <summary>Tasks in declaration order.</summary>
        public IReadOnlyList<PlanTaskSnapshot> Tasks { get; }

        /// <summary>Spending so far.</summary>
        public OrchestrationBudgetSnapshot Budget { get; }

        /// <summary>Tasks that succeeded.</summary>
        public int CompletedTasks { get; }

        /// <summary>Tasks waiting for a slot or running.</summary>
        public int ActiveTasks { get; }

        /// <summary>All tasks the plan knows of.</summary>
        public int TotalTasks => Tasks.Count;
    }

    /// <summary>Mutable state of one task; touched only under the owning plan's lock.</summary>
    internal sealed class PlanTask
    {
        public string Id;
        public string Title;
        public string SubAgentId;
        public PlanTaskState State;
        public int Attempts;
        public string TraceId = "";
        public int Steps;
        public LlmRequestProgress RunProgress;
        public string ResultSummary = "";
        public SubAgentErrorCode ErrorCode;
    }

    /// <summary>
    /// One plan: the player's goal, the tasks the lead declared or delegated, their states and the plan's
    /// budget, scope and concurrency gate. The HUD reads <see cref="Snapshot"/>; the delegator drives the
    /// task transitions. Thread-safe.
    /// </summary>
    public sealed class OrchestrationPlan
    {
        /// <summary>Longest task title kept for the HUD.</summary>
        public const int MaxTitleChars = 80;

        /// <summary>Longest result summary kept for the HUD.</summary>
        public const int MaxSummaryChars = 160;

        /// <summary>Longest task id the lead may choose.</summary>
        public const int MaxTaskIdChars = 64;

        private readonly object _lock = new();
        private readonly List<PlanTask> _tasks = new();
        private readonly Action _onChanged;
        private OrchestrationPlanState _state = OrchestrationPlanState.Running;
        private SubAgentErrorCode _exhaustedBudget;
        private int _nextTaskOrdinal;

        internal OrchestrationPlan(
            string id,
            string conversationId,
            string goal,
            SubAgentParentContext parent,
            OrchestrationBudget budget,
            Action onChanged)
        {
            Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id.Trim();
            ConversationId = conversationId ?? "";
            Goal = goal ?? "";
            Parent = parent ?? throw new ArgumentNullException(nameof(parent));
            Ledger = new OrchestrationBudgetLedger(budget);
            Gate = new SubAgentAdmissionGate(budget.MaxParallelSubAgents);
            Scope = new OrchestrationScope();
            _onChanged = onChanged;
        }

        /// <summary>Plan id.</summary>
        public string Id { get; }

        /// <summary>
        /// Reported on every change of the plan (a task declared, started, stepped or finished). The lead turn
        /// carries it as its request progress, so its idle deadline re-arms while sub-agents work.
        /// </summary>
        public LlmRequestProgress Progress { get; } = new();

        /// <summary>Conversation the plan belongs to.</summary>
        public string ConversationId { get; }

        /// <summary>The player's instruction that started the plan.</summary>
        public string Goal { get; }

        /// <summary>What the plan inherits from the lead turn.</summary>
        public SubAgentParentContext Parent { get; }

        /// <summary>The cancellation root of the plan; link the lead turn's token to <see cref="OrchestrationScope.Token"/>.</summary>
        public OrchestrationScope Scope { get; }

        /// <summary>Budget the plan was started with.</summary>
        public OrchestrationBudget Budget => Ledger.Budget;

        /// <summary>Current state.</summary>
        public OrchestrationPlanState State
        {
            get
            {
                lock (_lock)
                {
                    return _state;
                }
            }
        }

        internal OrchestrationBudgetLedger Ledger { get; }

        internal SubAgentAdmissionGate Gate { get; }

        /// <summary>
        /// Declares a task up front so the HUD can show the whole plan ("walls 2/5") before anything runs.
        /// Returns the task id the lead later passes as <c>task_id</c>.
        /// </summary>
        public string DeclareTask(string title, string subAgentId = "")
        {
            string id;
            lock (_lock)
            {
                id = NextTaskIdLocked();
                _tasks.Add(new PlanTask
                {
                    Id = id,
                    Title = ClipLine(title, MaxTitleChars),
                    SubAgentId = subAgentId?.Trim() ?? "",
                    State = PlanTaskState.Pending
                });
            }

            NotifyChanged();
            return id;
        }

        /// <summary>
        /// Counts one step of tool activity for the task whose latest run has <paramref name="traceId"/>
        /// (a host forwards the sub-agent's tool-call events here). Returns false for an unknown trace.
        /// </summary>
        public bool TryRecordActivity(string traceId)
        {
            if (string.IsNullOrWhiteSpace(traceId))
            {
                return false;
            }

            string trimmed = traceId.Trim();
            bool recorded = false;
            LlmRequestProgress runProgress = null;
            lock (_lock)
            {
                foreach (PlanTask task in _tasks)
                {
                    if (task.State == PlanTaskState.Running &&
                        string.Equals(task.TraceId, trimmed, StringComparison.Ordinal))
                    {
                        task.Steps++;
                        runProgress = task.RunProgress;
                        recorded = true;
                        break;
                    }
                }
            }

            if (recorded)
            {
                // WHY both signals: the sub-agent's own deadline is re-armed by its tool activity, and the lead
                // waiting for it is re-armed by every plan change (NotifyChanged reports Progress).
                runProgress?.Report();
                NotifyChanged();
            }

            return recorded;
        }

        /// <summary>An immutable view of the plan now.</summary>
        public OrchestrationPlanSnapshot Snapshot()
        {
            OrchestrationPlanState state;
            SubAgentErrorCode exhausted;
            List<PlanTaskSnapshot> tasks;
            lock (_lock)
            {
                state = _state;
                exhausted = _exhaustedBudget;
                tasks = new List<PlanTaskSnapshot>(_tasks.Count);
                foreach (PlanTask task in _tasks)
                {
                    tasks.Add(new PlanTaskSnapshot(task));
                }
            }

            return new OrchestrationPlanSnapshot(this, state, Scope.StopReason, exhausted, tasks.AsReadOnly(),
                Ledger.Snapshot());
        }

        /// <summary>
        /// Finds the declared task <paramref name="taskId"/> or creates a new one, and marks it waiting for a
        /// slot. Refuses a task that is already waiting or running.
        /// </summary>
        internal SubAgentErrorCode TryBeginTask(
            string taskId,
            string subAgentId,
            string taskText,
            out string resolvedTaskId,
            out int attempt)
        {
            resolvedTaskId = "";
            attempt = 0;
            lock (_lock)
            {
                PlanTask task = null;
                if (!string.IsNullOrWhiteSpace(taskId))
                {
                    task = FindLocked(taskId.Trim());
                }

                if (task == null)
                {
                    task = new PlanTask
                    {
                        Id = string.IsNullOrWhiteSpace(taskId)
                            ? NextTaskIdLocked()
                            : ClipLine(taskId, MaxTaskIdChars),
                        Title = ClipLine(taskText, MaxTitleChars)
                    };
                    _tasks.Add(task);
                }
                else if (task.State == PlanTaskState.Waiting || task.State == PlanTaskState.Running)
                {
                    resolvedTaskId = task.Id;
                    return SubAgentErrorCode.TaskBusy;
                }

                task.SubAgentId = subAgentId ?? "";
                task.State = PlanTaskState.Waiting;
                task.Attempts++;
                task.Steps = 0;
                task.ErrorCode = SubAgentErrorCode.None;
                resolvedTaskId = task.Id;
                attempt = task.Attempts;
            }

            NotifyChanged();
            return SubAgentErrorCode.None;
        }

        internal void MarkTaskRunning(string taskId, string traceId, LlmRequestProgress runProgress = null)
        {
            lock (_lock)
            {
                PlanTask task = FindLocked(taskId);
                if (task == null)
                {
                    return;
                }

                task.State = PlanTaskState.Running;
                task.TraceId = traceId ?? "";
                task.RunProgress = runProgress;
            }

            NotifyChanged();
        }

        internal void FinishTask(string taskId, PlanTaskState state, SubAgentErrorCode errorCode, string result)
        {
            lock (_lock)
            {
                PlanTask task = FindLocked(taskId);
                if (task == null)
                {
                    return;
                }

                task.State = state;
                task.ErrorCode = errorCode;
                task.ResultSummary = state == PlanTaskState.Succeeded ? ClipLine(result, MaxSummaryChars) : "";
            }

            NotifyChanged();
        }

        /// <summary>Records that <paramref name="budget"/> ran out; the plan accepts no further delegation.</summary>
        internal void MarkBudgetExhausted(SubAgentErrorCode budget)
        {
            bool changed = false;
            lock (_lock)
            {
                if (_state == OrchestrationPlanState.Running)
                {
                    _state = OrchestrationPlanState.BudgetExhausted;
                    _exhaustedBudget = budget;
                    changed = true;
                }
            }

            if (changed)
            {
                NotifyChanged();
            }
        }

        /// <summary>The budget that ran out, or <see cref="SubAgentErrorCode.None"/>.</summary>
        internal SubAgentErrorCode ExhaustedBudget
        {
            get
            {
                lock (_lock)
                {
                    return _exhaustedBudget;
                }
            }
        }

        /// <summary>
        /// Moves the plan to a terminal state. A plan already terminal keeps its first terminal state, except
        /// that <see cref="OrchestrationPlanState.Cancelled"/> overrides a budget stop, since the player's Stop
        /// is the more recent fact. Returns whether the state changed.
        /// </summary>
        internal bool TryFinish(OrchestrationPlanState state)
        {
            if (state == OrchestrationPlanState.Running)
            {
                return false;
            }

            bool changed = false;
            lock (_lock)
            {
                bool budgetStop = _state == OrchestrationPlanState.BudgetExhausted;
                if (_state == OrchestrationPlanState.Running ||
                    (budgetStop && state == OrchestrationPlanState.Cancelled))
                {
                    _state = state;
                    changed = true;
                }

                if (state == OrchestrationPlanState.Cancelled)
                {
                    foreach (PlanTask task in _tasks)
                    {
                        if (task.State == PlanTaskState.Pending)
                        {
                            task.State = PlanTaskState.Cancelled;
                            changed = true;
                        }
                    }
                }
            }

            if (changed)
            {
                NotifyChanged();
            }

            return changed;
        }

        private PlanTask FindLocked(string taskId)
        {
            foreach (PlanTask task in _tasks)
            {
                if (string.Equals(task.Id, taskId, StringComparison.Ordinal))
                {
                    return task;
                }
            }

            return null;
        }

        private string NextTaskIdLocked()
        {
            string id;
            do
            {
                _nextTaskOrdinal++;
                id = "t" + _nextTaskOrdinal;
            } while (FindLocked(id) != null);

            return id;
        }

        private void NotifyChanged()
        {
            Progress.Report();
            _onChanged?.Invoke();
        }

        internal static string ClipLine(string text, int maxChars)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return "";
            }

            string trimmed = text.Trim();
            int newline = trimmed.IndexOfAny(new[] { '\r', '\n' });
            string line = newline >= 0 ? trimmed.Substring(0, newline).TrimEnd() : trimmed;
            if (line.Length <= maxChars)
            {
                return line;
            }

            return line.Substring(0, Math.Max(1, maxChars - 3)).TrimEnd() + "...";
        }
    }
}
