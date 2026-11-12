using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CoreAI.Ai.SubAgents
{
    /// <summary>Why a delegation did not produce a sub-agent answer. Each value has a stable wire name.</summary>
    public enum SubAgentErrorCode
    {
        /// <summary>The sub-agent ran and answered.</summary>
        None = 0,

        /// <summary>No plan is running in this session, so there is nothing to delegate for.</summary>
        PlanNotActive = 1,

        /// <summary>The lead named a sub-agent that is not registered.</summary>
        UnknownSubAgent = 2,

        /// <summary>A required argument was missing or blank.</summary>
        InvalidArguments = 3,

        /// <summary>A sub-agent tried to delegate; sub-agents never do.</summary>
        NestedDelegation = 4,

        /// <summary>The plan has started as many sub-agent runs as its budget allows.</summary>
        CallBudgetExhausted = 5,

        /// <summary>The plan has spent its token budget.</summary>
        TokenBudgetExhausted = 6,

        /// <summary>The named plan task is already waiting or running.</summary>
        TaskBusy = 7,

        /// <summary>The plan was stopped (by the player, by completion or by teardown) before or during the run.</summary>
        PlanCancelled = 8,

        /// <summary>The run took longer than its definition's deadline.</summary>
        DeadlineExceeded = 9,

        /// <summary>The sub-agent run failed (provider error, exception, rejected output).</summary>
        SubAgentFailed = 10,

        /// <summary>No sub-agent of the conversation has the name the lead gave (Team style).</summary>
        UnknownAgent = 11,

        /// <summary>The sub-agent already has as many waiting messages as it may hold (Team style).</summary>
        InboxFull = 12,

        /// <summary>The conversation holds as many working sub-agents as it may remember (Team style).</summary>
        AgentLimitReached = 13
    }

    /// <summary>Stable snake_case names of <see cref="SubAgentErrorCode"/>, as the lead reads them in a tool result.</summary>
    public static class SubAgentErrorCodes
    {
        /// <summary>The wire name of <paramref name="code"/>; independent of the enum member names.</summary>
        public static string ToWireName(SubAgentErrorCode code)
        {
            switch (code)
            {
                case SubAgentErrorCode.None: return "none";
                case SubAgentErrorCode.PlanNotActive: return "plan_not_active";
                case SubAgentErrorCode.UnknownSubAgent: return "unknown_subagent";
                case SubAgentErrorCode.InvalidArguments: return "invalid_arguments";
                case SubAgentErrorCode.NestedDelegation: return "nested_delegation_refused";
                case SubAgentErrorCode.CallBudgetExhausted: return "call_budget_exhausted";
                case SubAgentErrorCode.TokenBudgetExhausted: return "token_budget_exhausted";
                case SubAgentErrorCode.TaskBusy: return "task_busy";
                case SubAgentErrorCode.PlanCancelled: return "plan_cancelled";
                case SubAgentErrorCode.DeadlineExceeded: return "deadline_exceeded";
                case SubAgentErrorCode.SubAgentFailed: return "subagent_failed";
                case SubAgentErrorCode.UnknownAgent: return "unknown_agent";
                case SubAgentErrorCode.InboxFull: return "inbox_full";
                case SubAgentErrorCode.AgentLimitReached: return "agent_limit_reached";
                default: return "subagent_failed";
            }
        }
    }

    /// <summary>What the lead asked for in one <c>delegate_task</c> call.</summary>
    public sealed class SubAgentDelegationRequest
    {
        /// <summary>Id of the sub-agent to run (<see cref="SubAgentDefinition.Id"/>).</summary>
        public string SubAgentId { get; set; } = "";

        /// <summary>The task, written by the lead; it becomes the whole user message of the sub-agent turn.</summary>
        public string Task { get; set; } = "";

        /// <summary>Optional context the lead chooses to pass along; nothing else of the lead's turn is sent.</summary>
        public string Context { get; set; } = "";

        /// <summary>Optional id of a task the lead declared on the plan; empty creates a new plan task.</summary>
        public string TaskId { get; set; } = "";
    }

    /// <summary>
    /// The typed outcome of one delegation. <see cref="ToToolResultJson"/> is what the lead receives as the
    /// tool result: <c>ok</c> comes first so a size cut further down the pipeline never hides the status, and
    /// a failure carries <c>ok:false</c> plus <c>error</c>, which the tool policy reads as a failed call.
    /// </summary>
    public sealed class SubAgentDelegationResult
    {
        /// <summary>True when the sub-agent ran and answered.</summary>
        public bool Ok { get; internal set; }

        /// <summary>Why there is no answer; <see cref="SubAgentErrorCode.None"/> when <see cref="Ok"/>.</summary>
        public SubAgentErrorCode ErrorCode { get; internal set; }

        /// <summary>Human-readable explanation for the lead and the logs.</summary>
        public string Message { get; internal set; } = "";

        /// <summary>Plan task the delegation belongs to; empty when it was refused before a task existed.</summary>
        public string TaskId { get; internal set; } = "";

        /// <summary>Sub-agent id as registered (or as the lead wrote it, when unknown).</summary>
        public string SubAgentId { get; internal set; } = "";

        /// <summary>The sub-agent's answer, clipped to <see cref="SubAgentDefinition.MaxResultChars"/>.</summary>
        public string Content { get; internal set; } = "";

        /// <summary>True when <see cref="Content"/> was clipped.</summary>
        public bool Truncated { get; internal set; }

        /// <summary>Provider failure category of a failed run; <see cref="LlmErrorCode.None"/> otherwise.</summary>
        public LlmErrorCode ProviderErrorCode { get; internal set; }

        /// <summary>Tokens the run reported; <c>null</c> when it reported none or never ran.</summary>
        public int? TotalTokens { get; internal set; }

        /// <summary>Trace id of the sub-agent turn; empty when it never ran.</summary>
        public string TraceId { get; internal set; } = "";

        /// <summary>The JSON tool result handed back to the lead.</summary>
        public string ToToolResultJson()
        {
            JObject json = new() { ["ok"] = Ok };
            if (!Ok)
            {
                json["error"] = SubAgentErrorCodes.ToWireName(ErrorCode);
                json["message"] = Message ?? "";
            }

            if (!string.IsNullOrEmpty(TaskId))
            {
                json["task_id"] = TaskId;
            }

            if (!string.IsNullOrEmpty(SubAgentId))
            {
                json["subagent"] = SubAgentId;
            }

            if (Ok)
            {
                json["result"] = Content ?? "";
                json["truncated"] = Truncated;
            }

            return json.ToString(Formatting.None);
        }

        internal static SubAgentDelegationResult Refused(
            SubAgentErrorCode code,
            string message,
            string subAgentId,
            string taskId = "")
        {
            return new SubAgentDelegationResult
            {
                Ok = false,
                ErrorCode = code,
                Message = message ?? "",
                SubAgentId = subAgentId ?? "",
                TaskId = taskId ?? ""
            };
        }
    }
}
