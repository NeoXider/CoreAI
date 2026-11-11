namespace CoreAI.Ai.SubAgents
{
    /// <summary>
    /// Host options for orchestrator mode, registered as an instance in the container (the same shape as
    /// <see cref="AiOrchestrationQueueOptions"/>) rather than read from static settings. A plan copies the
    /// budget fields when it begins, so a change made while a plan runs applies from the next plan on.
    /// </summary>
    public sealed class OrchestratorModeOptions
    {
        /// <summary>
        /// Whether a chat may run in orchestrator mode at all. Off by default: with it off nothing offers the
        /// delegate tool and a single-agent turn is exactly what it was.
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>Name the delegate tool is offered under.</summary>
        public string DelegateToolName { get; set; } = DelegateTaskTool.DefaultName;

        /// <summary>Name the plan tool (declare the tasks up front) is offered under.</summary>
        public string DeclareToolName { get; set; } = DeclareTasksTool.DefaultName;

        /// <summary>Most sub-agent runs one plan may start; a further delegation is refused with a typed reason.</summary>
        public int MaxSubAgentCallsPerPlan { get; set; } = 8;

        /// <summary>
        /// Token budget of one plan, summed from what the sub-agent runs report; <c>0</c> means no token cap.
        /// The cap is checked before a run starts, so the run that crosses it finishes and the next is refused.
        /// </summary>
        public int MaxTokensPerPlan { get; set; }

        /// <summary>
        /// Sub-agents of one plan that may run at the same time. <c>1</c> by default because one local
        /// endpoint answers one request at a time; raise it for a cloud endpoint. Delegations of one batch are
        /// started by the tool policy, so a value above <c>ICoreAISettings.MaxParallelToolCalls</c> has no effect.
        /// </summary>
        public int MaxParallelSubAgents { get; set; } = 1;

        /// <summary>
        /// Largest <c>context</c> text the lead may hand a sub-agent; longer text is clipped with a marker, and
        /// <c>0</c> or less keeps it whole.
        /// </summary>
        public int MaxContextChars { get; set; } = 4000;

        /// <summary>Player instructions that may wait behind a running plan; one more is refused.</summary>
        public int MaxQueuedInstructions { get; set; } = 8;

        /// <summary>The per-plan budget these options describe (values below the minimum are clamped).</summary>
        public OrchestrationBudget CreateBudget()
        {
            return new OrchestrationBudget(
                MaxSubAgentCallsPerPlan < 1 ? 1 : MaxSubAgentCallsPerPlan,
                MaxTokensPerPlan < 0 ? 0 : MaxTokensPerPlan,
                MaxParallelSubAgents < 1 ? 1 : MaxParallelSubAgents);
        }
    }
}
