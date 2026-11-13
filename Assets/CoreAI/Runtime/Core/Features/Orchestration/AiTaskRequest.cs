using System.Collections.Generic;
using System.Threading;
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
        /// source code, …) are decoded (byte-order mark, else the media type's <c>charset</c>, else UTF-8) and
        /// inlined into the prompt as delimited blocks, so they reach <b>every</b> model, including text-only local
        /// ones. A payload that is not text in that encoding throws naming the file.</description></item>
        /// <item><description>Any other media type (audio, video, meshes, arbitrary binary) throws at compose
        /// time — attachments are never silently dropped.</description></item>
        /// </list>
        /// <c>null</c>, empty or all-null leaves the turn as a plain-text prompt (unchanged behavior). Composition
        /// and validation live in <see cref="AiUserMessageBuilder"/>.
        /// <para>
        /// Lifetime: neither this list nor the attachment bytes are copied, and they are read again for every
        /// provider request of the turn — each tool-call roundtrip, the final summary request and every orchestrator
        /// retry — so keep them unchanged until the task's <see cref="System.Threading.Tasks.Task"/> completes or its
        /// stream is fully enumerated or disposed.
        /// </para>
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

        /// <summary>Optional logical latest-wins scope; actor-aware requests isolate it by actor, connection and role.</summary>
        public string CancellationScope { get; set; } = "";

        /// <summary>
        /// Optional original caller token used only to keep caller cancellation distinct when a deadline
        /// token is also signalled during teardown.
        /// </summary>
        public CancellationToken CallerCancellationToken { get; set; }

        /// <summary>
        /// Optional caller-owned deadline token used only to attribute cancellation metrics. The operation's
        /// actual cancellation remains controlled by the token passed to the orchestration method.
        /// </summary>
        public CancellationToken DeadlineCancellationToken { get; set; }

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

        /// <summary>Per-call sampling temperature; null inherits the agent and endpoint defaults.</summary>
        public float? Temperature { get; set; }

        /// <summary>Per-call reasoning overrides; null or Default effort inherits lower layers.</summary>
        public LlmReasoningOptions? Reasoning { get; set; }

        /// <summary>Per-call sampling overrides; null fields inherit the agent settings.</summary>
        public LlmSamplingOptions Sampling { get; set; }

        /// <summary>
        /// Per-call override of the tool-call roundtrip cap. <c>null</c> = use the per-agent/global
        /// fallback chain; <c>0</c> = UNLIMITED (no safety valve); positive = that many roundtrips.
        /// Like <see cref="MaxOutputTokens"/>, <c>0</c> is meaningful here (it means "no limit").
        /// Propagated to <see cref="LlmCompletionRequest.MaxToolCallRoundtrips"/> by the orchestrator.
        /// </summary>
        public int? MaxToolCallRoundtrips { get; set; }

        /// <summary>
        /// Per-call opt-in: let the engine read the assistant's PROSE as tool calls even on an endpoint
        /// that advertises a native tool channel. <c>null</c>/<c>false</c> (the default) does not.
        /// <para>
        /// The default is the safe one: interpreting prose means acting on what the model SAID instead of
        /// on the channel it said it through, and an agent explaining JSON writes objects shaped exactly
        /// like a call — the engine would execute the example instead of showing it.
        /// </para>
        /// <para>
        /// Set it only for an endpoint you have observed advertising a native channel and then answering
        /// with JSON in the text; the symptom is a tool that never runs while the reply contains its call.
        /// The right fix for a local llama.cpp/LLMUnity server is not this flag but declaring the endpoint
        /// honestly (<c>supportsNativeToolCalling: false</c> where the client is created).
        /// Propagated to <see cref="LlmCompletionRequest.AllowTextShapedToolCallsOnNativeEndpoint"/>.
        /// </para>
        /// </summary>
        public bool? AllowTextShapedToolCallsOnNativeEndpoint { get; set; }

        /// <summary>
        /// Whether this turn takes part in the role's chat history. <see cref="AiTaskHistoryMode.RoleDefault"/>
        /// (the default) follows the role's memory policy; <see cref="AiTaskHistoryMode.None"/> makes the turn
        /// neither read the stored conversation nor append to it, whatever the role or <see cref="SourceTag"/> says.
        /// A sub-agent turn uses <c>None</c>: it starts with a clean context and leaves the role's history as it was.
        /// </summary>
        public AiTaskHistoryMode HistoryMode { get; set; }

        /// <summary>
        /// Whether this turn sees the role's durable memory snapshot. <see cref="AiTaskMemoryMode.RoleDefault"/>
        /// (the default) follows the role's memory policy; <see cref="AiTaskMemoryMode.None"/> leaves the snapshot
        /// out of the prompt and does not consolidate it. The memory tool itself is governed by
        /// <see cref="AllowedToolNames"/> like any other role tool.
        /// </summary>
        public AiTaskMemoryMode MemoryMode { get; set; }

        /// <summary>
        /// Tools offered on this request only, in addition to the role's tools; <c>null</c> (the default) adds none.
        /// A request tool replaces a role tool of the same name, and the merged list is then filtered by
        /// <see cref="ForcedToolMode"/> and <see cref="AllowedToolNames"/> exactly like role tools, so an allowlist
        /// must name a request tool to keep it. Use it for a tool bound to one conversation or plan (the
        /// orchestrator mode's <c>delegate_task</c>), which a role-wide registration would share with every caller.
        /// </summary>
        public IReadOnlyList<ILlmTool> RequestTools { get; set; }

        /// <summary>
        /// The conversation this turn belongs to; empty (the default) means the role's single conversation, as
        /// before. Two chats on one role use two ids. Orchestrator mode keys its plan, queue and budget by it
        /// (falling back to the actor's session plus the role); history keyed by it is the conversation-identity
        /// work of the memory store, which reads the same value.
        /// </summary>
        public string ConversationId { get; set; } = "";

        /// <summary>
        /// Optional "still working" signal for this turn; <c>null</c> (the default) adds none. Reports re-arm the
        /// idle deadlines (<see cref="CoreAI.Infrastructure.Llm.TimeoutLlmClientDecorator"/> and the chat service's), so a turn that waits on
        /// tools or sub-agents without streaming text is not cut off by the wall-clock window, while a turn nobody
        /// reports for times out as before. Copied to <see cref="LlmCompletionRequest.Progress"/>.
        /// </summary>
        public LlmRequestProgress Progress { get; set; }

        /// <summary>
        /// Lines the player sends while this turn runs; <c>null</c> (the default) takes none. The tool loop appends them
        /// as user messages at its next step boundary, and the orchestrator records them in the role's history in the
        /// order the model saw them (<see cref="LlmTurnInbox"/>). Copied to <see cref="LlmCompletionRequest.Inbox"/>; a
        /// clone shares the same inbox, so a lead request built from a chat turn reads the player's lines too.
        /// </summary>
        public LlmTurnInbox Inbox { get; set; }

        /// <summary>
        /// Creates an independent copy carrying every request-scoped field. Consumers that must observe a
        /// request without owning it (session inspection, retry of the same turn) copy through this helper
        /// instead of re-listing properties: a partial copy silently drops whatever the list forgot, and the
        /// drop surfaces far from its cause — a runtime-context provider rejecting a request the orchestrator
        /// had accepted, or a diagnostic showing a prompt no real call builds. The copy shares reference
        /// fields (attachments, tool names, sampling); it is a request snapshot, not a deep copy.
        /// </summary>
        public AiTaskRequest Clone()
        {
            return new AiTaskRequest
            {
                RoleId = RoleId,
                RoutingProfileId = RoutingProfileId,
                SystemPrompt = SystemPrompt,
                RequestSystemInstructions = RequestSystemInstructions,
                Hint = Hint,
                Attachments = Attachments,
                LuaRepairGeneration = LuaRepairGeneration,
                LuaRepairPreviousCode = LuaRepairPreviousCode,
                LuaRepairErrorMessage = LuaRepairErrorMessage,
                TraceId = TraceId,
                Priority = Priority,
                SourceTag = SourceTag,
                ActorContext = ActorContext,
                CancellationScope = CancellationScope,
                CallerCancellationToken = CallerCancellationToken,
                DeadlineCancellationToken = DeadlineCancellationToken,
                LuaScriptVersionKey = LuaScriptVersionKey,
                DataOverlayVersionKeysCsv = DataOverlayVersionKeysCsv,
                ForcedToolMode = ForcedToolMode,
                RequiredToolName = RequiredToolName,
                AllowedToolNames = AllowedToolNames,
                MaxOutputTokens = MaxOutputTokens,
                Temperature = Temperature,
                Reasoning = Reasoning,
                Sampling = Sampling,
                MaxToolCallRoundtrips = MaxToolCallRoundtrips,
                AllowTextShapedToolCallsOnNativeEndpoint = AllowTextShapedToolCallsOnNativeEndpoint,
                HistoryMode = HistoryMode,
                MemoryMode = MemoryMode,
                RequestTools = RequestTools,
                ConversationId = ConversationId,
                Progress = Progress,
                Inbox = Inbox
            };
        }

        /// <summary>
        /// Creates a copy bound to <paramref name="roleId"/>, preserving every other request-scoped field.
        /// </summary>
        public AiTaskRequest CloneForRole(string roleId)
        {
            AiTaskRequest clone = Clone();
            clone.RoleId = roleId;
            return clone;
        }
    }

    /// <summary>How a turn uses the role's chat history (<see cref="AiTaskRequest.HistoryMode"/>).</summary>
    public enum AiTaskHistoryMode
    {
        /// <summary>Follow the role's memory policy (and the chat source rule).</summary>
        RoleDefault = 0,

        /// <summary>Neither read nor append chat history for this turn.</summary>
        None = 1
    }

    /// <summary>How a turn uses the role's durable memory (<see cref="AiTaskRequest.MemoryMode"/>).</summary>
    public enum AiTaskMemoryMode
    {
        /// <summary>Follow the role's memory policy.</summary>
        RoleDefault = 0,

        /// <summary>Leave the role's memory snapshot out of this turn.</summary>
        None = 1
    }

    /// <summary>
    /// The request-scoped context rules shared by the orchestrator and the session inspector, so the two never
    /// disagree about what a request with <see cref="AiTaskRequest.HistoryMode"/>,
    /// <see cref="AiTaskRequest.MemoryMode"/> or <see cref="AiTaskRequest.RequestTools"/> sends.
    /// </summary>
    internal static class AiTaskRequestScopeRules
    {
        /// <summary>Turns chat history off (read and append) when the request asks for a history-free turn.</summary>
        public static AgentMemoryPolicy.RoleMemoryConfig ApplyHistoryMode(
            AgentMemoryPolicy.RoleMemoryConfig roleConfig,
            AiTaskRequest task)
        {
            if (task?.HistoryMode != AiTaskHistoryMode.None)
            {
                return roleConfig;
            }

            roleConfig.WithChatHistory = false;
            roleConfig.PersistChatHistory = false;
            return roleConfig;
        }

        /// <summary>False when the request leaves the role's durable memory out.</summary>
        public static bool AllowsDurableMemory(AiTaskRequest task)
        {
            return task?.MemoryMode != AiTaskMemoryMode.None;
        }

        /// <summary>
        /// The role's tools followed by the request's own; a request tool replaces a role tool of the same name
        /// (ordinal), so a stale role registration cannot shadow a tool bound to this request.
        /// </summary>
        public static IReadOnlyList<ILlmTool> MergeRequestTools(
            IReadOnlyList<ILlmTool> roleTools,
            IReadOnlyList<ILlmTool> requestTools)
        {
            if (requestTools == null || requestTools.Count == 0)
            {
                return roleTools;
            }

            HashSet<string> requestNames = new(System.StringComparer.Ordinal);
            List<ILlmTool> added = new(requestTools.Count);
            foreach (ILlmTool tool in requestTools)
            {
                if (tool != null && !string.IsNullOrWhiteSpace(tool.Name) && requestNames.Add(tool.Name))
                {
                    added.Add(tool);
                }
            }

            if (added.Count == 0)
            {
                return roleTools;
            }

            List<ILlmTool> merged = new((roleTools?.Count ?? 0) + added.Count);
            if (roleTools != null)
            {
                foreach (ILlmTool tool in roleTools)
                {
                    if (tool != null && !requestNames.Contains(tool.Name ?? ""))
                    {
                        merged.Add(tool);
                    }
                }
            }

            merged.AddRange(added);
            return merged;
        }
    }
}
