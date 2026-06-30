namespace CoreAI.Ai
{
    /// <summary>
    /// Request object passed to AI orchestration services.
    /// </summary>
    public sealed class AiTaskRequest
    {
        /// <summary>Target agent role id.</summary>
        public string RoleId { get; set; } = BuiltInAgentRoleIds.Creator;

        /// <summary>User or system hint that describes the requested task.</summary>
        public string Hint { get; set; } = "";

        /// <summary>Lua repair generation associated with this command.</summary>
        public int LuaRepairGeneration { get; set; }

        /// <summary>Lua code from the previous failed repair attempt.</summary>
        public string LuaRepairPreviousCode { get; set; } = "";

        /// <summary>Lua error message that should guide repair.</summary>
        public string LuaRepairErrorMessage { get; set; } = "";

        /// <summary>Trace id used to correlate logs and metrics.</summary>
        public string TraceId { get; set; } = "";

        /// <summary>Scheduling priority for this AI task.</summary>
        public int Priority { get; set; }

        /// <summary>
        /// Source tag.
        /// </summary>
        public string SourceTag { get; set; } = "";

        /// <summary>
        /// Cancellation scope.
        /// </summary>
        public string CancellationScope { get; set; } = "";

        /// <summary>
        /// Lua script version key.
        /// </summary>
        public string LuaScriptVersionKey { get; set; } = "";

        /// <summary>
        /// Data overlay version keys csv.
        /// </summary>
        public string DataOverlayVersionKeysCsv { get; set; } = "";

        /// <summary>
        /// Per-call override of how the model picks tools. <see cref="LlmToolChoiceMode.Auto"/>
        /// is the default and matches the legacy behaviour (model decides).
        /// Application-layer logic (intent classifiers, retry pipelines) sets this when it needs
        /// guaranteed tool emission for the current request without changing the agent definition.
        /// Propagated to <see cref="LlmCompletionRequest.ForcedToolMode"/> by the orchestrator.
        /// </summary>
        public LlmToolChoiceMode ForcedToolMode { get; set; } = LlmToolChoiceMode.Auto;

        /// <summary>
        /// Tool name to require when <see cref="ForcedToolMode"/> is
        /// <see cref="LlmToolChoiceMode.RequireSpecific"/>. Ignored otherwise.
        /// Must match an <see cref="ILlmTool.Name"/> registered for this role.
        /// </summary>
        public string RequiredToolName { get; set; } = "";

        /// <summary>
        /// Allowed tool names.
        /// </summary>
        public string[] AllowedToolNames { get; set; }

        /// <summary>
        /// Per-call override of the LLM response token budget. <c>null</c> or <c>0</c> = use the
        /// per-agent/default fallback chain. Positive value wins over per-agent and global defaults.
        /// Propagated to
        /// <see cref="LlmCompletionRequest.MaxOutputTokens"/> by the orchestrator. Honored uniformly
        /// by HTTP and LLMUnity backends.
        /// </summary>
        public int? MaxOutputTokens { get; set; }

        /// <summary>
        /// Per-call override of the tool-call roundtrip cap. <c>null</c> = use the per-agent/global
        /// fallback chain; <c>0</c> = UNLIMITED (no safety valve); positive = that many roundtrips.
        /// Unlike <see cref="MaxOutputTokens"/>, <c>0</c> is meaningful here (it means "no limit").
        /// Propagated to <see cref="LlmCompletionRequest.MaxToolCallRoundtrips"/> by the orchestrator.
        /// </summary>
        public int? MaxToolCallRoundtrips { get; set; }
    }
}