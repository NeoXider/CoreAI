namespace CoreAI.Ai.SubAgents
{
    /// <summary>How orchestrator mode treats a chat turn.</summary>
    public enum OrchestrationStyle
    {
        /// <summary>
        /// The chat agent keeps answering every message itself and decides on its own when a job is big enough to
        /// hand to background sub-agents (<c>agent_start</c>, <c>agent_send</c>, <c>agent_read</c>,
        /// <c>agent_stop</c>). Sub-agents outlive the turn that started them; the player keeps talking meanwhile.
        /// </summary>
        Team = 0,

        /// <summary>
        /// Every chat message becomes the lead turn of a plan (<c>declare_tasks</c>, <c>delegate_task</c>): the lead
        /// waits for each delegation, and a message sent meanwhile waits in the queue until the plan ends.
        /// </summary>
        Plan = 1
    }

    /// <summary>
    /// Host options for orchestrator mode, registered as an instance in the container (the same shape as
    /// <see cref="AiOrchestrationQueueOptions"/>) rather than read from static settings. A plan (or, in
    /// <see cref="OrchestrationStyle.Team"/>, a player message) copies the budget fields when it begins, so a change
    /// made meanwhile applies from the next one on.
    /// </summary>
    public sealed class OrchestratorModeOptions
    {
        /// <summary>
        /// Whether a chat may run in orchestrator mode at all. Off by default: with it off nothing offers the
        /// sub-agent tools and a single-agent turn is exactly what it was.
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>How a chat turn is treated; <see cref="OrchestrationStyle.Team"/> by default.</summary>
        public OrchestrationStyle Style { get; set; } = OrchestrationStyle.Team;

        /// <summary>Name the delegate tool is offered under.</summary>
        public string DelegateToolName { get; set; } = DelegateTaskTool.DefaultName;

        /// <summary>Name the plan tool (declare the tasks up front) is offered under.</summary>
        public string DeclareToolName { get; set; } = DeclareTasksTool.DefaultName;

        /// <summary>
        /// Most sub-agent runs one plan may start; a further delegation is refused with a typed reason. In
        /// <see cref="OrchestrationStyle.Team"/> it is the runs one player message may lead to (starts, continuations
        /// and extra turns for messages, including those started from wake turns).
        /// </summary>
        public int MaxSubAgentCallsPerPlan { get; set; } = 8;

        /// <summary>
        /// Token budget of one plan (in <see cref="OrchestrationStyle.Team"/>, of one player message), summed from what
        /// the sub-agent runs report; <c>0</c> means no token cap. The cap is checked before a run starts, so the run
        /// that crosses it finishes and the next is refused.
        /// </summary>
        public int MaxTokensPerPlan { get; set; }

        /// <summary>
        /// Sub-agents of one plan (or one conversation's team) that may run at the same time. <c>1</c> by default
        /// because one local endpoint answers one request at a time; raise it for a cloud endpoint. In Plan style the
        /// delegations of one batch are started by the tool policy, so a value above
        /// <c>ICoreAISettings.MaxParallelToolCalls</c> has no effect there.
        /// </summary>
        public int MaxParallelSubAgents { get; set; } = 1;

        /// <summary>
        /// Largest <c>context</c> text the lead may hand a sub-agent; longer text is clipped with a marker, and
        /// <c>0</c> or less keeps it whole.
        /// </summary>
        public int MaxContextChars { get; set; } = 4000;

        /// <summary>Player instructions that may wait behind a running plan; one more is refused (Plan style).</summary>
        public int MaxQueuedInstructions { get; set; } = 8;

        /// <summary>Sub-agents one conversation remembers; a new one replaces the oldest idle one (Team style).</summary>
        public int MaxTeamMembers { get; set; } = 8;

        /// <summary>Messages that may wait for one working sub-agent; one more is refused (Team style).</summary>
        public int MaxInboxMessages { get; set; } = 4;

        /// <summary>Most characters of a sub-agent's own output one <c>agent_read</c> returns (Team style).</summary>
        public int MaxReadChars { get; set; } = 1200;

        /// <summary>
        /// Most characters of a sub-agent's earlier conversation sent with a continuation; the first task and the
        /// newest turns are kept (Team style).
        /// </summary>
        public int MaxContinuationChars { get; set; } = 6000;

        /// <summary>
        /// Lead turns a sub-agent's completion may start by itself after one player message (wake turns); later
        /// completions wait for the player's next message (Team style).
        /// </summary>
        public int MaxWakeTurnsPerInstruction { get; set; } = 4;

        /// <summary>Name of the tool that starts a background sub-agent (Team style).</summary>
        public string StartToolName { get; set; } = SubAgentTeamToolNames.Start;

        /// <summary>Name of the tool that messages or continues a sub-agent (Team style).</summary>
        public string SendToolName { get; set; } = SubAgentTeamToolNames.Send;

        /// <summary>Name of the tool that lists the sub-agents or reads one (Team style).</summary>
        public string ReadToolName { get; set; } = SubAgentTeamToolNames.Read;

        /// <summary>Name of the tool that stops a sub-agent (Team style).</summary>
        public string StopToolName { get; set; } = SubAgentTeamToolNames.Stop;

        /// <summary>The per-plan (Team style: per player message) budget these options describe (values below the minimum are clamped).</summary>
        public OrchestrationBudget CreateBudget()
        {
            return new OrchestrationBudget(
                MaxSubAgentCallsPerPlan < 1 ? 1 : MaxSubAgentCallsPerPlan,
                MaxTokensPerPlan < 0 ? 0 : MaxTokensPerPlan,
                MaxParallelSubAgents < 1 ? 1 : MaxParallelSubAgents);
        }
    }
}
