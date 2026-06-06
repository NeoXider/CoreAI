using System;
using System.Collections.Generic;
using CoreAI.AgentMemory;
using CoreAI.Logging;

namespace CoreAI.Ai
{
    /// <summary>
    /// Defines the runtime capabilities enabled for an agent.
    /// </summary>
    public enum AgentMode
    {
        /// <summary>Configures an agent to use tools without conversational chat history.</summary>
        ToolsOnly = 0,

        /// <summary>Configures an agent to use both tools and conversational chat history.</summary>
        ToolsAndChat = 1,

        /// <summary>Configures an agent to use conversational chat history without tool access.</summary>
        ChatOnly = 2
    }

    /// <summary>
    /// Fluent builder for configuring CoreAI agents, memory, tools, and prompt behavior.
    /// </summary>
    public sealed class AgentBuilder
    {
        private readonly string _roleId;
        private readonly List<ILlmTool> _tools = new();
        private readonly List<SkillSet> _skills = new();
        private string _systemPrompt;
        private AgentMode _mode = AgentMode.ToolsAndChat;
        private bool _withChatHistory;
        private int? _contextWindowTokens;
        private bool _persistChatHistory;
        private int _maxChatHistoryMessages = 30;
        private float? _temperature;
        private int? _maxOutputTokens;
        private bool? _allowDuplicateToolCalls;
        private bool? _enableStreaming;
        private MemoryToolAction _memoryDefaultAction = MemoryToolAction.Append;
        private bool _overrideUniversalPrefix;

        /// <summary>Null = default true (LLM-assisted compaction when global setting allows).</summary>
        private bool? _useLlmContextCompaction;

        private readonly ICoreAISettings _settings;

        public AgentBuilder(string roleId, ICoreAISettings settings = null)
        {
            _roleId = roleId ?? throw new ArgumentNullException(nameof(roleId));
            _settings = settings;
        }

        /// <summary>
        /// Adds role-specific prompt text that is appended to the composed system prompt.
        /// </summary>
        /// <remarks>
        /// <para>The final system prompt sent to the model is composed of THREE layers, in order:</para>
        /// <list type="number">
        /// <item><description><see cref="ICoreAISettings.UniversalSystemPromptPrefix"/> for global style, safety, and output rules.</description></item>
        /// <item><description>Base role prompt from Unity prompt manifests, resources, or built-in role fallback text.</description></item>
        /// <item><description>The text passed to this method as additional role guidance.</description></item>
        /// </list>
        /// <para>This means the literal string you pass here is <i>not</i> the full prompt the model sees;
        /// it is concatenated with the universal prefix and the role base prompt. To inspect the final
        /// composed prompt at runtime, enable <c>logLlmInput</c> on <c>CoreAISettingsAsset</c> or read
        /// <c>AgentTurnTrace.SystemPrompt</c>.</para>
        /// </remarks>
        public AgentBuilder WithSystemPrompt(string prompt)
        {
            _systemPrompt = prompt ?? throw new ArgumentNullException(nameof(prompt));
            return this;
        }

        /// <summary>
        /// Adds one tool directly visible to the agent on every request.
        /// </summary>
        public AgentBuilder WithTool(ILlmTool tool)
        {
            if (tool == null)
            {
                throw new ArgumentNullException(nameof(tool));
            }

            _tools.Add(tool);
            return this;
        }

        /// <summary>
        /// Adds a collection of directly visible tools to the agent.
        /// </summary>
        public AgentBuilder WithTools(IEnumerable<ILlmTool> tools)
        {
            if (tools != null)
            {
                foreach (ILlmTool tool in tools)
                {
                    _tools.Add(tool);
                }
            }

            return this;
        }

        /// <summary>
        /// Register a <see cref="SkillSet"/> with this agent (self-service pattern).
        /// <para>
        /// The skill's tools are registered for the agent. A lightweight catalog
        /// (name + description) is injected into the system prompt, and a
        /// <c>read_skill</c> meta-tool is auto-registered so the model can load
        /// the full instructions only when they are relevant.
        /// </para>
        /// </summary>
        /// <example>
        /// <code>
        /// var craftingSkill = new SkillSet("Crafting",
        ///     "Forge weapons and armor",
        ///     "1. Call get_recipes...\n2. Call craft_item...",
        ///     new DelegateLlmTool("get_recipes", "...", ...));
        /// var agent = new AgentBuilder("GameMaster")
        ///     .WithSkill(craftingSkill)
        ///     .WithSkill(combatSkill)
        ///     .Build();
        /// var request = new AiTaskRequest { RoleId = "GameMaster", Hint = "craft sword" };
        /// // Example run:
        /// await orch.RunTaskAsync(request);
        /// </code>
        /// </example>
        public AgentBuilder WithSkill(SkillSet skill)
        {
            if (skill == null)
            {
                throw new ArgumentNullException(nameof(skill));
            }

            // Skill tools are routed through call_skill_tool to keep the prompt surface small.
            _skills.Add(skill);

            return this;
        }

        /// <summary>
        /// Register tools from multiple <see cref="SkillSet"/> instances.
        /// </summary>
        public AgentBuilder WithSkills(params SkillSet[] skills)
        {
            if (skills != null)
            {
                foreach (SkillSet skill in skills)
                {
                    if (skill != null)
                    {
                        WithSkill(skill);
                    }
                }
            }

            return this;
        }

        /// <summary>
        /// Selects how the agent may respond: chat only, tools only, or both.
        /// </summary>
        public AgentBuilder WithMode(AgentMode mode)
        {
            _mode = mode;
            return this;
        }

        /// <summary>
        /// Enables role-scoped chat history for this agent, optionally persisting it between sessions.
        /// </summary>
        /// <remarks>
        /// When the history exceeds the configured budget, CoreAI keeps recent turns and can fold
        /// older context into a summary depending on global and per-agent compaction settings.
        /// </remarks>
        public AgentBuilder WithChatHistory(int? contextWindowTokens = null, bool persistBetweenSessions = false,
            int maxChatHistoryMessages = 30)
        {
            _withChatHistory = true;
            _contextWindowTokens = contextWindowTokens;
            _persistChatHistory = persistBetweenSessions;
            _maxChatHistoryMessages = maxChatHistoryMessages;
            return this;
        }

        /// <summary>
        /// Adds the built-in memory tool so this agent can read or write durable facts.
        /// </summary>
        public AgentBuilder WithMemory(MemoryToolAction defaultAction = MemoryToolAction.Append)
        {
            _tools.Add(new MemoryLlmTool());
            _memoryDefaultAction = defaultAction;
            return this;
        }

        /// <summary>
        /// Adds a delegate-backed tool that invokes the supplied C# action or function.
        /// </summary>
        public AgentBuilder WithAction(string name, string description, Delegate action)
        {
            _tools.Add(new DelegateLlmTool(name, description, action));
            return this;
        }

        /// <summary>
        /// Adds a tool that publishes a named CoreAI event, optionally with a string payload.
        /// </summary>
        public AgentBuilder WithEventTool(string name, string description, bool hasStringPayload = false)
        {
            if (hasStringPayload)
            {
                _tools.Add(new DelegateLlmTool(name, description,
                    new Action<string>((payload) => CoreAiEvents.Publish(name, payload))));
            }
            else
            {
                _tools.Add(new DelegateLlmTool(name, description, new Action(() => CoreAiEvents.Publish(name))));
            }

            return this;
        }

        /// <summary>
        /// Assigns the preferred sampling temperature for requests issued by this agent.
        /// </summary>
        /// <remarks>
        /// The value is applied only when the active settings allow temperature overrides.
        /// </remarks>
        public AgentBuilder WithTemperature(float temperature)
        {
            _temperature = temperature;
            return this;
        }

        /// <summary>
        /// Set a response token cap for this agent. Null or non-positive values clear the per-agent override.
        /// Per-call <see cref="AiTaskRequest.MaxOutputTokens"/> still has higher priority.
        /// </summary>
        /// <example>
        /// .WithMaxOutputTokens(256)   // Short NPC replies
        /// .WithMaxOutputTokens(2048)  // Longer planning agent
        /// </example>
        public AgentBuilder WithMaxOutputTokens(int? tokens)
        {
            _maxOutputTokens = tokens.HasValue && tokens.Value > 0 ? tokens.Value : null;
            return this;
        }

        /// <summary>
        /// Per-agent override for duplicate tool-call detection. Default behaviour is to <b>reject</b>
        /// a tool call whose <c>(name, args)</c> signature exactly matches a previous one within the
        /// same model turn.
        /// <para>
        /// Pass <c>true</c> to <b>opt out</b> (large/strong models occasionally re-call a tool on
        /// purpose, e.g. polling for state). Pass <c>false</c> to force-enable the guard for this
        /// role even if the global <see cref="ICoreAISettings.AllowDuplicateToolCalls"/> is <c>true</c>.
        /// </para>
        /// <para>
        /// Granularity:
        /// <list type="number">
        ///   <item><description>Per-role override: this method.</description></item>
        ///   <item><description>Per-tool opt-out:
        ///     even when role/global reject duplicates, a tool that returns <c>true</c> here is
        ///     never blocked (useful for read-only "ping" tools).</description></item>
        /// </list>
        /// </para>
        /// </summary>
        /// <example>
        /// // Allow this agent to call its tool repeatedly (e.g. status-poll loop).
        /// new AgentBuilder("Watchdog").WithAllowDuplicateToolCalls(true).Build();
        /// </example>
        public AgentBuilder WithAllowDuplicateToolCalls(bool allow)
        {
            _allowDuplicateToolCalls = allow;
            return this;
        }

        /// <summary>
        /// Assigns the agent-level streaming preference used by chat and orchestrator streaming paths.
        /// </summary>
        /// <example>
        /// new AgentBuilder("FastChat").WithStreaming(true).Build();
        /// new AgentBuilder("StrictJsonRole").WithStreaming(false).Build();
        /// </example>
        public AgentBuilder WithStreaming(bool enabled)
        {
            _enableStreaming = enabled;
            return this;
        }

        /// <summary>
        /// Enables or disables LLM-assisted folding of overflowing chat history for this agent.
        /// When disabled, only deterministic compaction applies. Default when not called: enabled (still gated by global <see cref="ICoreAISettings.EnableLlmContextCompaction"/>).
        /// </summary>
        public AgentBuilder WithLlmContextCompaction(bool enabled)
        {
            _useLlmContextCompaction = enabled;
            return this;
        }

        /// <summary>
        /// Controls whether this agent ignores the global universal system prompt prefix.
        /// </summary>
        /// <example>
        /// new AgentBuilder("JsonParser")
        ///     .WithSystemPrompt("You are a strict JSON parser.")
        ///     .WithOverrideUniversalPrefix()
        ///     .Build();
        /// </example>
        public AgentBuilder WithOverrideUniversalPrefix(bool shouldOverride = true)
        {
            _overrideUniversalPrefix = shouldOverride;
            return this;
        }

        /// <summary>
        /// Builds the <see cref="AgentConfig"/>. Emits non-fatal warnings via <see cref="Log.Instance"/>
        /// for likely misconfigurations (empty system prompt, tool-using mode without tools, compaction
        /// disabled while requested, and similar host setup issues).
        /// </summary>
        /// <remarks>
        /// Set <see cref="SuppressBuildWarnings"/> to <c>true</c> to silence validation (e.g. for tests
        /// that intentionally build minimal agents). Use <see cref="ValidateOnBuild"/> for the full set
        /// of issue codes if you want to assert on them in your own checks.
        /// </remarks>
        public AgentConfig Build()
        {
            int ctxTokens = _contextWindowTokens ??
                            _settings?.ContextWindowTokens ?? CoreAISettings.ContextWindowTokens;

            // final composition time (three-layer architecture):
            //   Layer 1: universalSystemPromptPrefix (project-wide rules)
            //   Layer 2: role base prompt from Manifest / Resources (.txt files)
            //   Layer 3: extra prompt from this builder (the one above)

            if (!SuppressBuildWarnings)
            {
                EmitBuildWarnings();
            }

            return new AgentConfig
            {
                RoleId = _roleId,
                SystemPrompt = _systemPrompt,
                Tools = new List<ILlmTool>(_tools),
                Skills = _skills.Count > 0 ? new List<SkillSet>(_skills) : null,
                Mode = _mode,
                WithChatHistory = _withChatHistory,
                ContextWindowTokens = ctxTokens,
                PersistChatHistoryBetweenSessions = _persistChatHistory,
                MaxChatHistoryMessages = _maxChatHistoryMessages,
                Temperature = _temperature,
                MaxOutputTokens = _maxOutputTokens,
                AllowDuplicateToolCalls = _allowDuplicateToolCalls,
                EnableStreaming = _enableStreaming,
                MemoryDefaultAction = _memoryDefaultAction,
                OverrideUniversalPrefix = _overrideUniversalPrefix,
                UseLlmContextCompaction = _useLlmContextCompaction ?? true
            };
        }

        /// <summary>
        /// When <c>true</c>, <see cref="Build"/> does not emit validation warnings.
        /// Default <c>false</c>. Useful for unit tests that intentionally construct partial agents.
        /// </summary>
        public bool SuppressBuildWarnings { get; set; }

        /// <summary>
        /// Returns the validation issues that <see cref="Build"/> would emit, without actually building.
        /// Useful for editor tooling and tests. Returns an empty list when there is nothing to flag.
        /// </summary>
        public IReadOnlyList<AgentBuilderIssue> ValidateOnBuild()
        {
            List<AgentBuilderIssue> issues = new();
            CollectIssues(issues);
            return issues;
        }

        private void EmitBuildWarnings()
        {
            List<AgentBuilderIssue> issues = new();
            CollectIssues(issues);
            if (issues.Count == 0)
            {
                return;
            }

            ILog log = Log.Instance ?? NullLog.Instance;
            foreach (AgentBuilderIssue issue in issues)
            {
                log.Warn($"[AgentBuilder:{_roleId}] {issue.Code}: {issue.Message}", LogTag.Core);
            }
        }

        private void CollectIssues(List<AgentBuilderIssue> issues)
        {
            if (string.IsNullOrWhiteSpace(_systemPrompt))
            {
                bool hasManifestFallback = !string.IsNullOrEmpty(_roleId)
                                           && BuiltInAgentRoleIds.IsBuiltIn(_roleId);
                if (!hasManifestFallback)
                {
                    issues.Add(new AgentBuilderIssue(
                        AgentBuilderIssueCode.MissingSystemPrompt,
                        "WithSystemPrompt(...) was not called and the role has no built-in fallback. " +
                        "The agent will rely solely on the universal prefix (Layer 1) and any manifest entry (Layer 2). " +
                        "If neither exists, the model gets an empty role prompt."));
                }
            }

            if ((_mode == AgentMode.ToolsAndChat || _mode == AgentMode.ToolsOnly) && _tools.Count == 0)
            {
                issues.Add(new AgentBuilderIssue(
                    AgentBuilderIssueCode.NoToolsForToolMode,
                    $"Mode is {_mode} but no tools were registered. " +
                    "Add tools with WithTool(...), WithAction(...), WithEventTool(...), or WithMemory(), " +
                    "or switch to AgentMode.ChatOnly."));
            }

            if (_mode == AgentMode.ToolsOnly && _tools.Count == 0)
            {
                // Already reported by the rule above; no extra issue here.
            }

            if (_useLlmContextCompaction == true)
            {
                bool? globalGate = _settings?.EnableLlmContextCompaction;
                bool effective = globalGate ?? CoreAISettings.EnableLlmContextCompaction;
                if (!effective)
                {
                    issues.Add(new AgentBuilderIssue(
                        AgentBuilderIssueCode.CompactionGateDisabled,
                        "WithLlmContextCompaction(true) was requested but the global gate " +
                        "ICoreAISettings.EnableLlmContextCompaction is false. Compaction will fall back to " +
                        "deterministic-only behavior. Enable the global gate on CoreAISettingsAsset to opt in."));
                }
            }

            if (_maxChatHistoryMessages <= 0 && _withChatHistory)
            {
                issues.Add(new AgentBuilderIssue(
                    AgentBuilderIssueCode.InvalidChatHistorySize,
                    $"WithChatHistory was enabled with maxChatHistoryMessages={_maxChatHistoryMessages}. " +
                    "Use a positive value (default 30) or omit the parameter."));
            }

            if (_temperature is < 0f or > 2f)
            {
                issues.Add(new AgentBuilderIssue(
                    AgentBuilderIssueCode.TemperatureOutOfRange,
                    $"WithTemperature({_temperature}) is outside the typical 0.0-2.0 range. " +
                    "Most providers clamp or reject values outside this range."));
            }
        }
    }

    /// <summary>
    /// Immutable configuration produced by AgentBuilder.
    /// </summary>
    public sealed class AgentConfig
    {
        public string RoleId { get; internal set; }
        public string SystemPrompt { get; internal set; }
        public IReadOnlyList<ILlmTool> Tools { get; internal set; }
        public AgentMode Mode { get; internal set; }
        public bool WithChatHistory { get; internal set; }
        public int ContextWindowTokens { get; internal set; }
        public bool PersistChatHistoryBetweenSessions { get; internal set; }
        public int MaxChatHistoryMessages { get; internal set; }
        public float? Temperature { get; internal set; }
        public int? MaxOutputTokens { get; internal set; }
        public bool? AllowDuplicateToolCalls { get; internal set; }

        /// <summary>Whether this agent prefers streaming responses when supported.</summary>
        public bool? EnableStreaming { get; internal set; }

        public MemoryToolAction MemoryDefaultAction { get; internal set; }
        public bool OverrideUniversalPrefix { get; internal set; }

        /// <summary>LLM-assisted transcript compaction for this agent (global gate still applies).</summary>
        public bool UseLlmContextCompaction { get; internal set; }

        /// <summary>
        /// Skills registered via <see cref="AgentBuilder.WithSkill"/>. Null when no skills.
        /// Each skill carries its own <see cref="SkillSet.Instructions"/> and <see cref="SkillSet.ToolNames"/>.
        /// </summary>
        public IReadOnlyList<SkillSet> Skills { get; internal set; }

        /// <summary>
        /// Applies this built agent configuration to a mutable <see cref="AgentMemoryPolicy"/>.
        /// </summary>
        public void ApplyToPolicy(AgentMemoryPolicy policy)
        {
            policy.SetToolsForRole(RoleId, Tools);

            policy.ConfigureRole(RoleId, defaultAction: MemoryDefaultAction,
                allowDuplicateToolCalls: AllowDuplicateToolCalls);

            if (Tools.Count == 0 || !HasMemoryTool())
            {
                policy.DisableMemoryTool(RoleId);
            }

            policy.ConfigureChatHistory(RoleId, WithChatHistory, ContextWindowTokens,
                PersistChatHistoryBetweenSessions, MaxChatHistoryMessages);
            policy.ConfigureLlmContextCompaction(RoleId, UseLlmContextCompaction);
            policy.SetMaxOutputTokens(RoleId, MaxOutputTokens);
            policy.SetTemperature(RoleId, Temperature);

            if (!string.IsNullOrWhiteSpace(SystemPrompt))
            {
                policy.SetAdditionalSystemPrompt(RoleId, SystemPrompt);
            }

            if (OverrideUniversalPrefix)
            {
                policy.SetOverrideUniversalPrefix(RoleId, true);
            }

            bool? streamingOverride = EnableStreaming;
            if (!streamingOverride.HasValue &&
                (Mode == AgentMode.ToolsAndChat || Mode == AgentMode.ToolsOnly))
            {
                streamingOverride = true;
            }

            policy.SetStreamingEnabled(RoleId, streamingOverride);

            // Self-service skills: register catalog context provider + meta-tools.
            // The catalog (name + description per skill) goes into the system prompt.
            // The model calls read_skill(name) to load instructions + tool schemas,
            // then call_skill_tool(tool_name, args_json) to execute them.
            // This keeps the model's visible tool count at exactly 2 regardless of skill count.
            if (Skills != null && Skills.Count > 0)
            {
                // Inject lightweight catalog into system prompt
                policy.SetRuntimeContextProvider(RoleId, new SkillRuntimeContextProvider(Skills));

                // Register read_skill meta-tool (loads instructions + tool schemas)
                policy.AddToolForRole(RoleId, ReadSkillLlmTool.Create(Skills));

                // Register call_skill_tool proxy (routes to real skill tools)
                policy.AddToolForRole(RoleId, CallSkillToolLlmTool.Create(Skills));
            }
        }

        private bool HasMemoryTool()
        {
            foreach (ILlmTool tool in Tools)
            {
                if (tool is MemoryLlmTool)
                {
                    return true;
                }
            }

            return false;
        }
    }
}