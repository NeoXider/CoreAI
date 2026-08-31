using System.Collections.Generic;
using CoreAI.Authority;

namespace CoreAI.Ai
{
    /// <summary>
    /// Request object passed to AI orchestration services.
    /// </summary>
    public sealed class AiTaskRequest
    {
        /// <summary>Target agent role id.</summary>
        public string RoleId { get; set; } = BuiltInAgentRoleIds.Creator;

        /// <summary>
        /// Optional explicit LLM routing profile. A non-empty value takes precedence over agent,
        /// role-rule, default-profile, and legacy fallback selection.
        /// </summary>
        public string RoutingProfileId { get; set; } = "";

        /// <summary>
        /// Optional legacy base-role prompt override. When non-empty, this replaces the role base prompt
        /// for this request while preserving the universal prefix and registered role additions. Because the
        /// value is part of the first provider system message, request- or user-specific callers should migrate
        /// to <see cref="RequestSystemInstructions"/> to preserve shared prompt-cache reuse.
        /// </summary>
        public string SystemPrompt { get; set; } = "";

        /// <summary>
        /// Optional cache-safe instructions for this request. The orchestrator emits them in the volatile
        /// provider-compatible tail after the stable universal/role/tool prefix and conversation transcript,
        /// but before the current user payload. This property does not replace the role base prompt.
        /// </summary>
        public string RequestSystemInstructions { get; set; } = "";

        /// <summary>User or system hint that describes the requested task.</summary>
        public string Hint { get; set; } = "";

        /// <summary>
        /// Optional files attached to this turn alongside the text prompt. CoreAI routes each
        /// <see cref="AiAttachment"/> by its (possibly inferred) media type when composing the user message:
        /// <list type="number">
        /// <item><description><b>Images</b> (<c>image/png</c>, <c>image/jpeg</c>, <c>image/webp</c>,
        /// <c>image/gif</c>) are sent as native image parts — only <b>vision-capable</b> models receive them;
        /// text-only models typically error or ignore the image per provider.</description></item>
        /// <item><description><b>Text-like files</b> (<c>text/*</c>, <c>application/json</c>, Lua, Markdown,
        /// source code, …) are decoded as UTF-8 and inlined into the prompt as delimited blocks, so they reach
        /// <b>every</b> model, including text-only local ones.</description></item>
        /// <item><description>Any other media type (audio, video, meshes, arbitrary binary) throws at compose
        /// time — attachments are never silently dropped.</description></item>
        /// </list>
        /// <c>null</c> or empty leaves the turn as a plain-text prompt (unchanged behavior). Composition and
        /// validation live in <see cref="AiUserMessageBuilder"/>.
        /// </summary>
        public IReadOnlyList<AiAttachment> Attachments { get; set; }

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
        /// Trusted actor snapshot captured by the production admission caller. When present,
        /// <see cref="QueuedAiOrchestrator"/> uses its durable and connection identities directly.
        /// </summary>
        public ActorContext? ActorContext { get; set; }

        /// <summary>Logical latest-wins cancellation scope for legacy callers.</summary>
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
        /// Per-call override of the LLM response token budget. <c>null</c> = use the
        /// per-agent/default fallback chain; <c>0</c> = explicitly UNLIMITED (no <c>max_tokens</c>
        /// sent to the provider — a reasoning model's thinking never eats the answer budget);
        /// a positive value wins over per-agent and global defaults. Propagated to
        /// <see cref="LlmCompletionRequest.MaxOutputTokens"/> by the orchestrator. Honored uniformly
        /// by HTTP and LLMUnity backends.
        /// </summary>
        public int? MaxOutputTokens { get; set; }

        /// <summary>
        /// Per-call override of the tool-call roundtrip cap. <c>null</c> = use the per-agent/global
        /// fallback chain; <c>0</c> = UNLIMITED (no safety valve); positive = that many roundtrips.
        /// Like <see cref="MaxOutputTokens"/>, <c>0</c> is meaningful here (it means "no limit").
        /// Propagated to <see cref="LlmCompletionRequest.MaxToolCallRoundtrips"/> by the orchestrator.
        /// </summary>
        public int? MaxToolCallRoundtrips { get; set; }
    }
}
