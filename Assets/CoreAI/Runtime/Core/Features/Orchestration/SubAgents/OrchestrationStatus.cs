using System;
using System.Collections.Generic;

namespace CoreAI.Ai.SubAgents
{
    /// <summary>
    /// The one status source a HUD line, the in-world chat and a completion notification read, so they can
    /// never disagree. <see cref="Changed"/> is raised on the thread that changed the state: a Unity host
    /// marshals to the main thread before touching UI (on WebGL that is already the only thread).
    /// </summary>
    public interface IOrchestrationStatusSource
    {
        /// <summary>The status now.</summary>
        OrchestrationStatus Current { get; }

        /// <summary>Raised after every change with the new status.</summary>
        event Action<OrchestrationStatus> Changed;
    }

    /// <summary>An immutable status of one orchestrator session: the plan, the queue and a revision counter.</summary>
    public sealed class OrchestrationStatus
    {
        internal OrchestrationStatus(
            string conversationId,
            long revision,
            OrchestrationPlanSnapshot plan,
            IReadOnlyList<QueuedInstructionSnapshot> queue)
        {
            ConversationId = conversationId ?? "";
            Revision = revision;
            Plan = plan;
            Queue = queue ?? Array.Empty<QueuedInstructionSnapshot>();
        }

        /// <summary>Conversation the session belongs to.</summary>
        public string ConversationId { get; }

        /// <summary>Grows by one with every change, so a view can skip a stale update.</summary>
        public long Revision { get; }

        /// <summary>The running plan, else the last finished one, else <c>null</c>.</summary>
        public OrchestrationPlanSnapshot Plan { get; }

        /// <summary>Queued player instructions, front first.</summary>
        public IReadOnlyList<QueuedInstructionSnapshot> Queue { get; }

        /// <summary>True while a plan is running.</summary>
        public bool IsRunning => Plan != null && Plan.State == OrchestrationPlanState.Running;
    }

    /// <summary>
    /// Plain-text renderings of an <see cref="OrchestrationStatus"/> for a HUD line or a chat line. Pure
    /// functions; a game that wants other wording formats the snapshot itself.
    /// </summary>
    public static class OrchestrationStatusText
    {
        /// <summary>
        /// One line for the HUD: <c>builder: walls 2/5</c> while a task runs, <c>planning 0/0</c> before the
        /// first delegation, <c>done 5/5</c>, <c>stopped 2/5</c>, <c>failed 2/5</c> or
        /// <c>budget exhausted (calls) 3/5</c> afterwards; empty when there is no plan.
        /// </summary>
        public static string FormatLine(OrchestrationStatus status)
        {
            OrchestrationPlanSnapshot plan = status?.Plan;
            if (plan == null)
            {
                return "";
            }

            string progress = plan.CompletedTasks + "/" + plan.TotalTasks;
            switch (plan.State)
            {
                case OrchestrationPlanState.Completed:
                    return "done " + progress;
                case OrchestrationPlanState.Cancelled:
                    return "stopped " + progress;
                case OrchestrationPlanState.Failed:
                    return "failed " + progress;
                case OrchestrationPlanState.BudgetExhausted:
                    return "budget exhausted (" + DescribeBudget(plan.ExhaustedBudget) + ") " + progress;
            }

            PlanTaskSnapshot active = FindActive(plan);
            if (active == null)
            {
                return "planning " + progress;
            }

            string who = string.IsNullOrEmpty(active.SubAgentId) ? "" : active.SubAgentId + ": ";
            return who + active.Title + " " + progress;
        }

        /// <summary><c>next: build a tower</c> for the queue head, or empty when nothing is queued.</summary>
        public static string FormatNextLine(OrchestrationStatus status)
        {
            if (status == null || status.Queue.Count == 0)
            {
                return "";
            }

            string next = OrchestrationPlan.ClipLine(status.Queue[0].Text, OrchestrationPlan.MaxTitleChars);
            return status.Queue.Count == 1
                ? "next: " + next
                : "next: " + next + " (+" + (status.Queue.Count - 1) + ")";
        }

        private static PlanTaskSnapshot FindActive(OrchestrationPlanSnapshot plan)
        {
            PlanTaskSnapshot waiting = null;
            foreach (PlanTaskSnapshot task in plan.Tasks)
            {
                if (task.State == PlanTaskState.Running)
                {
                    return task;
                }

                if (waiting == null && task.State == PlanTaskState.Waiting)
                {
                    waiting = task;
                }
            }

            return waiting;
        }

        private static string DescribeBudget(SubAgentErrorCode budget)
        {
            return budget == SubAgentErrorCode.TokenBudgetExhausted ? "tokens" : "calls";
        }
    }
}
