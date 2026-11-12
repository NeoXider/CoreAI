using System;
using System.Collections.Generic;
using CoreAI.AgentMemory;

namespace CoreAI.Ai
{
    /// <summary>
    /// Mutable role policy for memory tools, chat history, prompt overlays, and per-role
    /// LLM behaviour overrides. A fresh policy holds the shipped <see cref="BuiltInRoleDefinitions"/>; tools
    /// reach a role directly (<see cref="AddToolForRole"/>) or through the tool profiles its definition lists
    /// (<see cref="AddToolToProfile(string, ILlmTool)"/>).
    /// </summary>
    public sealed partial class AgentMemoryPolicy
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, RoleMemoryConfig> _roleConfigs;
        private readonly Dictionary<string, List<ILlmTool>> _customTools = new();

        private readonly Dictionary<string, MutableSkillCatalog> _roleSkillCatalogs =
            new(StringComparer.Ordinal);

        private readonly Dictionary<string, IAgentRuntimeContextProvider> _runtimeContextProviders = new();
        private static readonly MemoryLlmTool _memoryToolInstance = new();

        /// <summary>
        /// Detached, fully validated role publication unit built by <see cref="AgentConfig"/>.
        /// Applied atomically by <see cref="ApplyPreparedRole"/>; a role is never partially visible.
        /// </summary>
        internal sealed class PreparedAgentRole
        {
            public string RoleId;
            public List<ILlmTool> Tools;
            public MemoryToolAction MemoryDefaultAction;
            public bool? AllowDuplicateToolCalls;
            public bool HasMemoryTool;
            public bool WithChatHistory;
            public int ContextWindowTokens;
            public bool PersistChatHistory;
            public int MaxChatHistoryMessages;
            public bool UseLlmContextCompaction;
            public int? MaxOutputTokens;
            public int? MaxToolCallRoundtrips;
            public float? Temperature;
            public LlmReasoningOptions? Reasoning;
            public LlmSamplingOptions Sampling;
            public ToolResultMemoryPolicy ToolResultMemory;
            public float? CompactionTriggerRatio;
            public bool OverrideUniversalPrefix;
            public bool? StreamingOverride;
            public string AdditionalPrompt;
            public MutableSkillCatalog LiveCatalog;

            /// <summary>Tool profiles the role draws tools from; empty detaches it from every profile.</summary>
            public List<string> ToolProfiles;

            /// <summary>Skills that came from <see cref="ToolProfiles"/> and were merged into the live catalog.</summary>
            public List<SkillSet> ProfileSkills;

            /// <summary>The agent mode the policy enforces for the role, or null when none is enforced.</summary>
            public AgentMode? EnforcedMode;
        }

        /// <summary>
        /// Atomically publishes a prepared role: tool list, role config, prompt overlay, streaming
        /// override, and skill catalog registration happen under a single lock with no awaits or
        /// callbacks inside. The same live catalog instance backs role lookup and agent proxies;
        /// unrelated runtime context providers are left untouched.
        /// </summary>
        internal void ApplyPreparedRole(PreparedAgentRole prepared)
        {
            if (prepared == null)
            {
                throw new ArgumentNullException(nameof(prepared));
            }

            string roleId = (prepared.RoleId ?? "").Trim();
            if (roleId.Length == 0)
            {
                throw new ArgumentException("Role id is missing.", nameof(prepared));
            }

            lock (_lock)
            {
                if (prepared.LiveCatalog != null)
                {
                    _roleSkillCatalogs[roleId] = prepared.LiveCatalog;
                }
                else
                {
                    _roleSkillCatalogs.Remove(roleId);
                }

                if (prepared.Tools == null || prepared.Tools.Count == 0)
                {
                    _customTools.Remove(roleId);
                }
                else
                {
                    _customTools[roleId] = prepared.Tools;
                }

                ConfigureRole(roleId, useMemoryTool: null,
                    defaultAction: prepared.MemoryDefaultAction,
                    allowDuplicateToolCalls: prepared.AllowDuplicateToolCalls);
                if (prepared.Tools == null || prepared.Tools.Count == 0 || !prepared.HasMemoryTool)
                {
                    DisableMemoryTool(roleId);
                }

                ConfigureChatHistory(roleId, prepared.WithChatHistory,
                    prepared.ContextWindowTokens, prepared.PersistChatHistory,
                    prepared.MaxChatHistoryMessages);
                ConfigureLlmContextCompaction(roleId, prepared.UseLlmContextCompaction);
                SetMaxOutputTokens(roleId, prepared.MaxOutputTokens);
                SetMaxToolCallRoundtrips(roleId, prepared.MaxToolCallRoundtrips);
                SetTemperature(roleId, prepared.Temperature);
                SetReasoning(roleId, prepared.Reasoning);
                SetSampling(roleId, prepared.Sampling);
                SetToolResultMemoryPolicy(roleId, prepared.ToolResultMemory);
                SetCompactionTriggerRatio(roleId, prepared.CompactionTriggerRatio);

                if (prepared.OverrideUniversalPrefix)
                {
                    _overrideUniversalPrefix.Add(roleId);
                }
                else
                {
                    _overrideUniversalPrefix.Remove(roleId);
                }

                if (prepared.StreamingOverride.HasValue)
                {
                    _streamingOverrides[roleId] = prepared.StreamingOverride.Value;
                }
                else
                {
                    _streamingOverrides.Remove(roleId);
                }

                if (!string.IsNullOrWhiteSpace(prepared.AdditionalPrompt))
                {
                    _additionalSystemPrompts[roleId] = prepared.AdditionalPrompt.Trim();
                }
                else
                {
                    _additionalSystemPrompts.Remove(roleId);
                }

                // WHY profiles follow the builder: an AgentBuilder for a role has always REPLACED its tools,
                // installer tools included, so a role built without WithToolProfile keeps today's meaning and
                // is detached from the profiles its definition listed.
                SetRoleToolProfilesLocked(roleId, RoleDefinition.NormalizeProfileIds(prepared.ToolProfiles));
                _roleInlineSkills.Remove(roleId);
                if (prepared.LiveCatalog != null && prepared.ProfileSkills != null && prepared.ProfileSkills.Count > 0)
                {
                    _derivedSkills[roleId] = new List<SkillSet>(prepared.ProfileSkills);
                }
                else
                {
                    _derivedSkills.Remove(roleId);
                }

                if (prepared.EnforcedMode.HasValue)
                {
                    _enforcedModes[roleId] = prepared.EnforcedMode.Value;
                }
                else
                {
                    _enforcedModes.Remove(roleId);
                }
            }
        }

        /// <summary>Copies direct tool references without invoking user tool metadata.</summary>
        internal IReadOnlyList<ILlmTool> SnapshotCustomToolsForPreparation(string roleId)
        {
            lock (_lock)
            {
                return _customTools.TryGetValue(roleId.Trim(), out List<ILlmTool> tools)
                    ? new List<ILlmTool>(tools)
                    : Array.Empty<ILlmTool>();
            }
        }

        /// <summary>
        /// Replaces the direct tool list for a role; pass an empty list to clear custom tools. The role is also
        /// detached from its tool profiles, so the list passed here is the whole tool set (plus the memory tool
        /// and skill meta-tools); use <see cref="AddToolForRole"/> to add a tool next to the profile tools.
        /// </summary>
        public void SetToolsForRole(string roleId, IReadOnlyList<ILlmTool> tools)
        {
            roleId = roleId?.Trim();
            lock (_lock)
            {
                if (roleId != null)
                {
                    SetRoleToolProfilesLocked(roleId, null);
                }

                List<ILlmTool> replacement = tools == null
                    ? new List<ILlmTool>()
                    : new List<ILlmTool>(tools);

                if (_roleSkillCatalogs.TryGetValue(roleId, out MutableSkillCatalog catalog))
                {
                    RemoveSkillMetaTools(replacement);
                    replacement.Add(ReadSkillLlmTool.Create(catalog));
                    replacement.Add(CallSkillToolLlmTool.Create(catalog, DirectToolsProviderFor(roleId)));
                }
                else
                {
                    // WHY: an agent built with WithSkill() but WITHOUT skill authoring registers no
                    // live catalog — its read_skill / call_skill_tool were appended through
                    // AddToolForRole. Replacing the role's tools then dropped both, and every skill
                    // the agent was built with silently stopped being callable: call_skill_tool
                    // answers a missing tool with an ordinary RESULT, so the model reads "not found",
                    // apologises in prose and moves on, with nothing thrown and nothing logged.
                    // Carrying the existing proxies across keeps skills reachable for the whole life
                    // of the role, whatever a host does to its tool list afterwards.
                    List<ILlmTool> carried = CarriedSkillMetaTools(roleId, replacement);
                    if (carried.Count > 0)
                    {
                        RemoveSkillMetaTools(replacement);
                        replacement.AddRange(carried);
                    }
                }

                if (replacement.Count == 0)
                {
                    _customTools.Remove(roleId);
                }
                else
                {
                    _customTools[roleId] = replacement;
                }
            }
        }

        /// <summary>True for the two skill proxies that must never be lost from a role.</summary>
        private static bool IsSkillMetaTool(ILlmTool tool)
        {
            return tool != null &&
                   (string.Equals(tool.Name, "read_skill", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(tool.Name, "call_skill_tool", StringComparison.OrdinalIgnoreCase));
        }

        private static void RemoveSkillMetaTools(List<ILlmTool> tools)
        {
            tools.RemoveAll(IsSkillMetaTool);
        }

        /// <summary>
        /// The role's currently registered skill proxies, preferring any the caller already supplied.
        /// </summary>
        private List<ILlmTool> CarriedSkillMetaTools(string roleId, List<ILlmTool> replacement)
        {
            List<ILlmTool> carried = new();
            foreach (ILlmTool tool in replacement)
            {
                if (IsSkillMetaTool(tool))
                {
                    carried.Add(tool);
                }
            }

            if (carried.Count > 0)
            {
                return carried;
            }

            if (roleId != null && _customTools.TryGetValue(roleId, out List<ILlmTool> existing))
            {
                foreach (ILlmTool tool in existing)
                {
                    if (IsSkillMetaTool(tool))
                    {
                        carried.Add(tool);
                    }
                }
            }

            return carried;
        }

        /// <summary>What <see cref="AddOrReplaceToolForRole"/> changed in a role's tool list.</summary>
        public enum ToolRegistrationChange
        {
            /// <summary>Nothing changed: invalid arguments, or this very tool instance was already registered.</summary>
            None = 0,

            /// <summary>The tool was appended; the role had no tool with that name.</summary>
            Added = 1,

            /// <summary>A tool with the same name was replaced in place, keeping its position.</summary>
            Replaced = 2
        }

        /// <summary>
        /// Adds a single tool to a role's tool list (e.g. the <c>read_skill</c> meta-tool), replacing a tool of the
        /// same <see cref="ILlmTool.Name"/> (ordinal) instead of appending a duplicate. If no tools exist for the
        /// role yet, a new list is created. Use <see cref="AddOrReplaceToolForRole"/> to learn what changed.
        /// </summary>
        public void AddToolForRole(string roleId, ILlmTool tool)
        {
            AddOrReplaceToolForRole(roleId, tool);
        }

        /// <summary>
        /// Adds <paramref name="tool"/> to <paramref name="roleId"/>, or replaces the registered tool with the same
        /// name (ordinal) in place. Repeated registrations of one tool - e.g. <c>CoreAi.RegisterGameStateTool()</c>
        /// in <c>OnEnable</c> - therefore keep exactly one function per name: strict providers reject a request that
        /// declares a name twice. A tool with an empty name is always appended.
        /// </summary>
        public ToolRegistrationChange AddOrReplaceToolForRole(string roleId, ILlmTool tool)
        {
            if (string.IsNullOrWhiteSpace(roleId) || tool == null)
            {
                return ToolRegistrationChange.None;
            }

            roleId = roleId.Trim();
            lock (_lock)
            {
                if (!_customTools.TryGetValue(roleId, out List<ILlmTool> list))
                {
                    list = new List<ILlmTool>();
                    _customTools[roleId] = list;
                }

                string name = tool.Name;
                if (string.IsNullOrEmpty(name))
                {
                    list.Add(tool);
                    return ToolRegistrationChange.Added;
                }

                int first = -1;
                bool removedDuplicates = false;
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    if (list[i] == null || !string.Equals(list[i].Name, name, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (first >= 0)
                    {
                        list.RemoveAt(first);
                        removedDuplicates = true;
                    }

                    first = i;
                }

                if (first < 0)
                {
                    list.Add(tool);
                    return ToolRegistrationChange.Added;
                }

                if (ReferenceEquals(list[first], tool))
                {
                    return removedDuplicates ? ToolRegistrationChange.Replaced : ToolRegistrationChange.None;
                }

                list[first] = tool;
                return ToolRegistrationChange.Replaced;
            }
        }

        /// <summary>
        /// Adds a skill to the role's on-demand catalog. The first skill for a role creates the
        /// catalog and registers the <c>read_skill</c> / <c>call_skill_tool</c> meta-tools; every
        /// later call only appends — the meta-tools read from a live catalog, so skills added at
        /// any point (even mid-session) are immediately readable. A skill with an existing name
        /// replaces the previous one. This is the code-path counterpart of assigning
        /// SkillSetAssets in the inspector; build the <see cref="SkillSet"/> from code, a text
        /// file (<see cref="SkillSet.FromFile"/>), or loaded content
        /// (<see cref="SkillSet.FromTextContent"/>).
        /// </summary>
        public void AddSkillForRole(string roleId, SkillSet skill)
        {
            if (string.IsNullOrWhiteSpace(roleId) || skill == null)
            {
                return;
            }

            roleId = roleId.Trim();
            MutableSkillCatalog catalog;
            bool createdCatalog = false;
            lock (_lock)
            {
                if (!_roleSkillCatalogs.TryGetValue(roleId, out catalog))
                {
                    catalog = new MutableSkillCatalog();
                    _roleSkillCatalogs[roleId] = catalog;
                    createdCatalog = true;
                }
            }

            catalog.AddOrReplace(skill);
            if (createdCatalog)
            {
                AddToolForRole(roleId, ReadSkillLlmTool.Create(catalog));
                AddToolForRole(roleId, CallSkillToolLlmTool.Create(catalog, DirectToolsProviderFor(roleId)));
            }
        }

        /// <summary>
        /// Callback that reports the role's own top-level tools to <c>call_skill_tool</c>.
        /// </summary>
        /// <remarks>
        /// A callback rather than a list because the meta-tool is registered while the role's tools are
        /// still being assembled — a snapshot taken here would miss every tool added afterwards. It is
        /// only consulted when a name misses the skill catalog, so the lock is never taken on the
        /// normal path (and never while this one is already held: the call happens during a tool
        /// invocation, long after registration).
        /// </remarks>
        private Func<IReadOnlyList<ILlmTool>> DirectToolsProviderFor(string roleId)
        {
            string capturedRoleId = roleId;
            return () => GetToolsForRole(capturedRoleId);
        }

        /// <summary>
        /// Returns a snapshot of the skills registered for <paramref name="roleId"/> (empty when the role
        /// has none). Exposed so out-of-band surfaces — e.g. the MCP server's <c>read_skill</c> tool —
        /// can serve the SAME skill reference the in-game agent gets from its <c>read_skill</c> catalog,
        /// keeping one source of truth instead of duplicating the docs.
        /// </summary>
        public IReadOnlyList<SkillSet> GetSkillsForRole(string roleId)
        {
            if (string.IsNullOrWhiteSpace(roleId))
            {
                return Array.Empty<SkillSet>();
            }

            roleId = roleId.Trim();
            lock (_lock)
            {
                if (_roleSkillCatalogs.TryGetValue(roleId, out MutableSkillCatalog catalog))
                {
                    return new List<SkillSet>(catalog);
                }
            }

            return Array.Empty<SkillSet>();
        }

        /// <summary>Registers a runtime context provider for a single role.</summary>
        public void SetRuntimeContextProvider(string roleId, IAgentRuntimeContextProvider provider)
        {
            if (string.IsNullOrWhiteSpace(roleId))
            {
                return;
            }

            roleId = roleId.Trim();
            lock (_lock)
            {
                if (provider == null)
                {
                    _runtimeContextProviders.Remove(roleId);
                    return;
                }

                _runtimeContextProviders[roleId] = provider;
            }
        }

        /// <summary>Clears the runtime context provider for a single role.</summary>
        public void ClearRuntimeContextProvider(string roleId)
        {
            if (!string.IsNullOrWhiteSpace(roleId))
            {
                lock (_lock)
                {
                    _runtimeContextProviders.Remove(roleId.Trim());
                }
            }
        }

        /// <summary>Returns the runtime context provider for a role, when configured.</summary>
        public bool TryGetRuntimeContextProvider(string roleId, out IAgentRuntimeContextProvider provider)
        {
            provider = null;
            if (string.IsNullOrWhiteSpace(roleId))
            {
                roleId = BuiltInAgentRoleIds.Creator;
            }

            lock (_lock)
            {
                return _runtimeContextProviders.TryGetValue(roleId.Trim(), out provider);
            }
        }

        /// <summary>Per-role memory, history, and LLM override settings.</summary>
        public struct RoleMemoryConfig
        {
            /// <summary>Whether the built-in memory tool is available for the role.</summary>
            public bool UseMemoryTool;

            /// <summary>Default memory action when the role uses the memory tool.</summary>
            public MemoryToolAction DefaultAction;

            /// <summary>Allow duplicate tool calls.</summary>
            public bool? AllowDuplicateToolCalls;

            /// <summary>
            /// With chat history. Enabled by default for continuity, but still capped because raw
            /// chat consumes prompt tokens faster than compact MemoryTool facts.
            /// </summary>
            public bool WithChatHistory;

            /// <summary>Persist chat history.</summary>
            public bool PersistChatHistory;

            /// <summary>
            /// Per-role context window in tokens; 0 = inherit the global ICoreAISettings.ContextWindowTokens (default 128K).
            /// Set explicitly only to override a single role.
            /// </summary>
            public int ContextTokens;

            /// <summary>Max chat history messages.</summary>
            public int MaxChatHistoryMessages;

            /// <summary>Per-role LLM response token cap; null = use per-call/global/provider fallback.</summary>
            public int? MaxOutputTokens;

            /// <summary>
            /// Per-role tool-call roundtrip cap. <c>null</c> = inherit per-call/global
            /// <see cref="ICoreAISettings.MaxToolCallRoundtrips"/>; <c>0</c> = UNLIMITED (no safety valve);
            /// positive = that cap. Set via <see cref="AgentBuilder.WithMaxToolCallRoundtrips"/>.
            /// </summary>
            public int? MaxToolCallRoundtrips;

            /// <summary>Controls how observed tool results are persisted into chat history.</summary>
            public ToolResultMemoryPolicy ToolResultMemory;

            /// <summary>
            /// Per-role sampling temperature override; null = use global <see cref="ICoreAISettings.Temperature"/>.
            /// Set via <see cref="AgentBuilder.WithTemperature"/>.
            /// </summary>
            public float? Temperature;
            public LlmReasoningOptions? Reasoning;
            public LlmSamplingOptions Sampling;

            /// <summary>
            /// When true, long chat history may be folded with an auxiliary LLM request (requires global <see cref="ICoreAISettings.EnableLlmContextCompaction"/>).
            /// When false, only deterministic truncation/summary applies. Built-in <see cref="BuiltInAgentRoleIds.Programmer"/> defaults to false.
            /// </summary>
            public bool UseLlmContextCompaction;

            /// <summary>
            /// Per-role compaction trigger ratio; null = use global <see cref="ICoreAISettings.ConversationCompactionTriggerRatio"/>.
            /// Valid values are greater than 0 and less than or equal to 1.
            /// </summary>
            public float? CompactionTriggerRatio;

            /// <summary>Preserves the original constructor signature for existing compiled hosts.</summary>
            public RoleMemoryConfig(bool useMemoryTool, MemoryToolAction defaultAction,
                bool withChatHistory, bool persistChatHistory, int contextTokens,
                bool? allowDuplicateToolCalls, int maxChatHistoryMessages, int? maxOutputTokens,
                bool useLlmContextCompaction, float? temperature,
                ToolResultMemoryPolicy toolResultMemory, float? compactionTriggerRatio)
                : this(useMemoryTool, defaultAction, withChatHistory, persistChatHistory, contextTokens,
                    allowDuplicateToolCalls, maxChatHistoryMessages, maxOutputTokens, useLlmContextCompaction,
                    temperature, toolResultMemory, compactionTriggerRatio, null, null)
            {
            }

            public RoleMemoryConfig(bool useMemoryTool = true, MemoryToolAction defaultAction = MemoryToolAction.Append,
                bool withChatHistory = true, bool persistChatHistory = false, int contextTokens = 0,
                bool? allowDuplicateToolCalls = null, int maxChatHistoryMessages = 30, int? maxOutputTokens = null,
                bool useLlmContextCompaction = true, float? temperature = null,
                ToolResultMemoryPolicy toolResultMemory = ToolResultMemoryPolicy.CompactSummary,
                float? compactionTriggerRatio = null,
                LlmReasoningOptions? reasoning = null, LlmSamplingOptions sampling = null)
            {
                UseMemoryTool = useMemoryTool;
                DefaultAction = defaultAction;
                WithChatHistory = withChatHistory;
                PersistChatHistory = persistChatHistory;
                ContextTokens = contextTokens;
                AllowDuplicateToolCalls = allowDuplicateToolCalls;
                MaxChatHistoryMessages = maxChatHistoryMessages;
                MaxOutputTokens = maxOutputTokens;
                UseLlmContextCompaction = useLlmContextCompaction;
                Temperature = temperature;
                Reasoning = reasoning;
                Sampling = sampling;
                ToolResultMemory = toolResultMemory;
                CompactionTriggerRatio = NormalizeCompactionTriggerRatio(compactionTriggerRatio);
                MaxToolCallRoundtrips = null;
            }
        }

        public void ConfigureChatHistory(string roleId, bool enabled, int tokens, bool persist,
            int maxChatHistoryMessages = 30)
        {
            roleId = roleId?.Trim();
            lock (_lock)
            {
                if (!_roleConfigs.TryGetValue(roleId, out RoleMemoryConfig c))
                {
                    c = new RoleMemoryConfig(true, MemoryToolAction.Append);
                }

                c.WithChatHistory = enabled;
                c.ContextTokens = tokens;
                c.PersistChatHistory = persist;
                c.MaxChatHistoryMessages = maxChatHistoryMessages;
                _roleConfigs[roleId] = c;
            }
        }

        /// <summary>
        /// Per-role opt-in/out for LLM-assisted transcript compaction (still requires global <see cref="ICoreAISettings.EnableLlmContextCompaction"/>).
        /// </summary>
        public void ConfigureLlmContextCompaction(string roleId, bool enabled)
        {
            if (string.IsNullOrWhiteSpace(roleId))
            {
                return;
            }

            roleId = roleId.Trim();
            lock (_lock)
            {
                RoleMemoryConfig c = GetRoleConfigLocked(roleId);
                c.UseLlmContextCompaction = enabled;
                _roleConfigs[roleId] = c;
            }
        }


        /// <summary>Creates a policy holding the shipped <see cref="BuiltInRoleDefinitions"/>.</summary>
        public AgentMemoryPolicy()
        {
            _roleConfigs = new Dictionary<string, RoleMemoryConfig>();
            foreach (RoleDefinition definition in BuiltInRoleDefinitions.CreateAll())
            {
                ApplyRoleDefinition(definition);
            }
        }

        /// <summary>
        /// Creates a policy holding the shipped built-in definitions, then applies <paramref name="definitions"/>
        /// over them in order, so a host definition for a built-in role replaces the shipped one.
        /// </summary>
        /// <remarks>
        /// A factory rather than a constructor on purpose: VContainer builds this type with its widest
        /// constructor, and a parameterised one would change how every container resolves the policy.
        /// </remarks>
        public static AgentMemoryPolicy Create(IEnumerable<RoleDefinition> definitions)
        {
            AgentMemoryPolicy policy = new();
            policy.ApplyRoleDefinitions(definitions);
            return policy;
        }

        /// <summary>
        /// Returns whether the built-in memory tool should be available for the role.
        /// </summary>
        public bool IsMemoryEnabled(string roleId)
        {
            if (string.IsNullOrWhiteSpace(roleId))
            {
                roleId = BuiltInAgentRoleIds.Creator;
            }

            roleId = roleId.Trim();

            lock (_lock)
            {
                if (_roleConfigs.TryGetValue(roleId, out RoleMemoryConfig config))
                {
                    return config.UseMemoryTool;
                }
            }

            return true;
        }

        /// <summary>
        /// Returns <c>true</c> when the role has an explicit policy entry.
        /// Use this to distinguish explicit registration from implicit defaulting.
        /// </summary>
        public bool HasRole(string roleId)
        {
            if (string.IsNullOrWhiteSpace(roleId))
            {
                return false;
            }

            roleId = roleId.Trim();

            lock (_lock)
            {
                return _roleConfigs.ContainsKey(roleId);
            }
        }

        /// <summary>
        /// Returns a snapshot of role ids currently configured on this policy.
        /// Includes built-in roles and any roles registered through <see cref="AgentConfig.ApplyToPolicy"/>.
        /// </summary>
        public IReadOnlyList<string> GetKnownRoleIds()
        {
            lock (_lock)
            {
                List<string> roleIds = new(_roleConfigs.Keys);
                roleIds.Sort(StringComparer.Ordinal);
                return roleIds;
            }
        }

        /// <summary>
        /// Returns the effective role configuration, falling back to Creator defaults for empty role ids.
        /// </summary>
        public RoleMemoryConfig GetRoleConfig(string roleId)
        {
            if (string.IsNullOrWhiteSpace(roleId))
            {
                roleId = BuiltInAgentRoleIds.Creator;
            }

            roleId = roleId.Trim();

            lock (_lock)
            {
                return GetRoleConfigLocked(roleId);
            }
        }

        /// <summary>Internal helper called under <see cref="_lock"/>.</summary>
        private RoleMemoryConfig GetRoleConfigLocked(string roleId)
        {
            if (_roleConfigs.TryGetValue(roleId, out RoleMemoryConfig config))
            {
                return config;
            }

            return new RoleMemoryConfig(true, MemoryToolAction.Append);
        }

        /// <summary>
        /// Updates memory-tool and duplicate-call settings for a role while preserving other role options.
        /// </summary>
        public void ConfigureRole(
            string roleId,
            bool? useMemoryTool = null,
            MemoryToolAction? defaultAction = null,
            bool? allowDuplicateToolCalls = null)
        {
            if (string.IsNullOrWhiteSpace(roleId))
            {
                return;
            }

            roleId = roleId.Trim();

            lock (_lock)
            {
                RoleMemoryConfig existing = GetRoleConfigLocked(roleId);

                _roleConfigs[roleId] = new RoleMemoryConfig
                {
                    UseMemoryTool = useMemoryTool ?? existing.UseMemoryTool,
                    DefaultAction = defaultAction ?? existing.DefaultAction,
                    AllowDuplicateToolCalls = allowDuplicateToolCalls ?? existing.AllowDuplicateToolCalls,
                    WithChatHistory = existing.WithChatHistory,
                    PersistChatHistory = existing.PersistChatHistory,
                    ContextTokens = existing.ContextTokens,
                    MaxChatHistoryMessages = existing.MaxChatHistoryMessages,
                    MaxOutputTokens = existing.MaxOutputTokens,
                    MaxToolCallRoundtrips = existing.MaxToolCallRoundtrips,
                    ToolResultMemory = existing.ToolResultMemory,
                    Temperature = existing.Temperature,
                    Reasoning = existing.Reasoning,
                    Sampling = existing.Sampling,
                    UseLlmContextCompaction = existing.UseLlmContextCompaction,
                    CompactionTriggerRatio = existing.CompactionTriggerRatio
                };
            }
        }

        /// <summary>
        /// Set a per-role LLM response token cap. <c>null</c> or negative clears the override;
        /// <c>0</c> = explicitly unlimited (kept and propagated to the request as 0);
        /// a positive value caps the response.
        /// </summary>
        public void SetMaxOutputTokens(string roleId, int? maxOutputTokens)
        {
            if (string.IsNullOrWhiteSpace(roleId))
            {
                return;
            }

            roleId = roleId.Trim();
            lock (_lock)
            {
                RoleMemoryConfig existing = GetRoleConfigLocked(roleId);
                existing.MaxOutputTokens = maxOutputTokens.HasValue && maxOutputTokens.Value >= 0
                    ? maxOutputTokens.Value
                    : null;
                _roleConfigs[roleId] = existing;
            }
        }

        /// <summary>
        /// Set a per-role tool-call roundtrip cap. <c>null</c> clears the override (inherit per-call/global);
        /// <c>0</c> means UNLIMITED; a positive value caps the loop. Negative values are treated as cleared.
        /// </summary>
        public void SetMaxToolCallRoundtrips(string roleId, int? maxRoundtrips)
        {
            if (string.IsNullOrWhiteSpace(roleId))
            {
                return;
            }

            roleId = roleId.Trim();
            lock (_lock)
            {
                RoleMemoryConfig existing = GetRoleConfigLocked(roleId);
                existing.MaxToolCallRoundtrips = maxRoundtrips.HasValue && maxRoundtrips.Value >= 0
                    ? maxRoundtrips.Value
                    : null;
                _roleConfigs[roleId] = existing;
            }
        }

        /// <summary>Sets per-role reasoning overrides; null clears them.</summary>
        public void SetReasoning(string roleId, LlmReasoningOptions? reasoning)
        {
            if (string.IsNullOrWhiteSpace(roleId))
            {
                return;
            }

            roleId = roleId.Trim();
            lock (_lock)
            {
                RoleMemoryConfig existing = GetRoleConfigLocked(roleId);
                existing.Reasoning = reasoning;
                _roleConfigs[roleId] = existing;
            }
        }

        /// <summary>Sets per-role sampling overrides; null clears them.</summary>
        public void SetSampling(string roleId, LlmSamplingOptions sampling)
        {
            if (string.IsNullOrWhiteSpace(roleId))
            {
                return;
            }

            roleId = roleId.Trim();
            lock (_lock)
            {
                RoleMemoryConfig existing = GetRoleConfigLocked(roleId);
                existing.Sampling = sampling;
                _roleConfigs[roleId] = existing;
            }
        }

        /// <summary>
        /// Set a per-role sampling temperature override. Null clears the override (falls back to global).
        /// </summary>
        public void SetTemperature(string roleId, float? temperature)
        {
            if (string.IsNullOrWhiteSpace(roleId))
            {
                return;
            }

            roleId = roleId.Trim();
            lock (_lock)
            {
                RoleMemoryConfig existing = GetRoleConfigLocked(roleId);
                existing.Temperature = temperature;
                _roleConfigs[roleId] = existing;
            }
        }

        /// <summary>
        /// Set how tool results are persisted into chat history for a role.
        /// </summary>
        public void SetToolResultMemoryPolicy(string roleId, ToolResultMemoryPolicy policy)
        {
            if (string.IsNullOrWhiteSpace(roleId))
            {
                return;
            }

            roleId = roleId.Trim();
            lock (_lock)
            {
                RoleMemoryConfig existing = GetRoleConfigLocked(roleId);
                existing.ToolResultMemory = policy;
                _roleConfigs[roleId] = existing;
            }
        }

        /// <summary>
        /// Set a per-role compaction trigger ratio. Null or invalid values clear the override.
        /// </summary>
        public void SetCompactionTriggerRatio(string roleId, float? ratio)
        {
            if (string.IsNullOrWhiteSpace(roleId))
            {
                return;
            }

            roleId = roleId.Trim();
            lock (_lock)
            {
                RoleMemoryConfig existing = GetRoleConfigLocked(roleId);
                existing.CompactionTriggerRatio = NormalizeCompactionTriggerRatio(ratio);
                _roleConfigs[roleId] = existing;
            }
        }

        private static float? NormalizeCompactionTriggerRatio(float? ratio)
        {
            if (!ratio.HasValue)
            {
                return null;
            }

            float value = ratio.Value;
            if (value <= 0f || value > 1f || float.IsNaN(value) || float.IsInfinity(value))
            {
                return null;
            }

            return value;
        }

        /// <summary>
        /// Enables the built-in memory tool for a role.
        /// </summary>
        public void EnableMemoryTool(string roleId)
        {
            ConfigureRole(roleId, true);
        }

        /// <summary>
        /// Disables the built-in memory tool for a role.
        /// </summary>
        public void DisableMemoryTool(string roleId)
        {
            ConfigureRole(roleId, false);
        }

        /// <summary>
        /// Enables or disables the built-in memory tool for every built-in role.
        /// </summary>
        public void SetMemoryToolForAll(bool enabled)
        {
            foreach (string roleId in BuiltInAgentRoleIds.AllBuiltInRoles)
            {
                ConfigureRole(roleId, enabled);
            }
        }

        /// <summary>
        /// Returns whether memory instructions/tools should be injected for a role.
        /// </summary>
        public bool ShouldInjectMemory(string roleId)
        {
            return IsMemoryEnabled(roleId);
        }

        /// <summary>
        /// Returns the effective tools for a role: the built-in memory tool when enabled, then the tools of the
        /// role's tool profiles, then its direct tools (a direct tool wins over a profile tool of the same name).
        /// A role whose <see cref="AgentMode.ChatOnly"/> mode is enforced gets none.
        /// </summary>
        public IReadOnlyList<ILlmTool> GetToolsForRole(string roleId)
        {
            roleId = roleId?.Trim();
            List<PendingProfileTool> missing = CollectMissingProfileTools(roleId);
            if (missing != null)
            {
                MaterializeProfileTools(roleId, missing);
            }

            bool warnUnknown;
            IReadOnlyList<ILlmTool> result;
            lock (_lock)
            {
                List<ILlmTool> tools = new();

                bool hasCustom = _customTools.TryGetValue(roleId, out List<ILlmTool> custom) &&
                                 custom != null && custom.Count > 0;
                warnUnknown = ShouldWarnUnknownRoleLocked(roleId);
                if (IsChatOnlyEnforcedLocked(roleId))
                {
                    result = Array.Empty<ILlmTool>();
                }
                else
                {
                    List<ILlmTool> fromProfiles = ResolveProfileToolsLocked(roleId, hasCustom ? custom : null);
                    bool hasMemory = (hasCustom && ListContainsMemoryTool(custom)) ||
                                     ListContainsMemoryTool(fromProfiles);
                    if (IsMemoryEnabledLocked(roleId) && !hasMemory)
                    {
                        tools.Add(_memoryToolInstance);
                    }

                    tools.AddRange(fromProfiles);
                    if (hasCustom)
                    {
                        tools.AddRange(custom);
                    }

                    // WHY: with no skill tool to reach, call_skill_tool was only a second door into the role's
                    // own tools (a model used it to repeat a manage_mods call the duplicate guard had answered).
                    // The proxy stays registered, so a skill with tools added later offers it again. A role that
                    // authors skills keeps it: a skill it creates in this turn must be callable in this turn.
                    if (!tools.Exists(tool => tool != null &&
                                              string.Equals(tool.Name, "manage_skills", StringComparison.Ordinal)))
                    {
                        tools.RemoveAll(CallSkillToolLlmTool.OffersNoSkillTools);
                    }
                    result = tools.Count > 0 ? tools : Array.Empty<ILlmTool>();
                }
            }

            if (warnUnknown)
            {
                WarnUnknownRole(roleId);
            }

            return result;
        }

        /// <summary>Check memory enabled under <see cref="_lock"/>.</summary>
        private bool IsMemoryEnabledLocked(string roleId)
        {
            if (string.IsNullOrWhiteSpace(roleId))
            {
                roleId = BuiltInAgentRoleIds.Creator;
            }

            roleId = roleId.Trim();
            return _roleConfigs.TryGetValue(roleId, out RoleMemoryConfig config) ? config.UseMemoryTool : true;
        }

        private static bool ListContainsMemoryTool(List<ILlmTool> list)
        {
            foreach (ILlmTool t in list)
            {
                if (t != null && string.Equals(t.Name, "memory", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private readonly Dictionary<string, string> _additionalSystemPrompts = new();

        private readonly HashSet<string> _overrideUniversalPrefix = new();

        private readonly Dictionary<string, bool> _streamingOverrides = new();

        /// <summary>
        /// Replaces the extra system-prompt suffix for a role, or clears it when the value is blank.
        /// </summary>
        public void SetAdditionalSystemPrompt(string roleId, string prompt)
        {
            if (string.IsNullOrWhiteSpace(roleId))
            {
                return;
            }

            roleId = roleId.Trim();

            lock (_lock)
            {
                if (string.IsNullOrWhiteSpace(prompt))
                {
                    _additionalSystemPrompts.Remove(roleId);
                }
                else
                {
                    _additionalSystemPrompts[roleId] = prompt.Trim();
                }
            }
        }

        /// <summary>
        /// Returns the extra system-prompt suffix configured for a role.
        /// </summary>
        public bool TryGetAdditionalSystemPrompt(string roleId, out string prompt)
        {
            prompt = null;
            if (string.IsNullOrWhiteSpace(roleId))
            {
                return false;
            }

            lock (_lock)
            {
                return _additionalSystemPrompts.TryGetValue(roleId.Trim(), out prompt);
            }
        }

        /// <summary>
        /// Controls whether a role opts out of the global universal system prompt prefix.
        /// </summary>
        public void SetOverrideUniversalPrefix(string roleId, bool shouldOverride)
        {
            if (string.IsNullOrWhiteSpace(roleId))
            {
                return;
            }

            roleId = roleId.Trim();

            lock (_lock)
            {
                if (shouldOverride)
                {
                    _overrideUniversalPrefix.Add(roleId);
                }
                else
                {
                    _overrideUniversalPrefix.Remove(roleId);
                }
            }
        }

        /// <summary>
        /// Returns whether a role skips the global universal system prompt prefix.
        /// </summary>
        public bool IsUniversalPrefixOverridden(string roleId)
        {
            if (string.IsNullOrWhiteSpace(roleId))
            {
                return false;
            }

            lock (_lock)
            {
                return _overrideUniversalPrefix.Contains(roleId.Trim());
            }
        }

        /// <summary>
        /// Applies a per-role streaming override; <c>null</c> clears the override and uses
        /// <see cref="ICoreAISettings.EnableStreaming"/>.
        /// </summary>
        public void SetStreamingEnabled(string roleId, bool? enabled)
        {
            if (string.IsNullOrWhiteSpace(roleId))
            {
                return;
            }

            roleId = roleId.Trim();

            lock (_lock)
            {
                if (enabled.HasValue)
                {
                    _streamingOverrides[roleId] = enabled.Value;
                }
                else
                {
                    _streamingOverrides.Remove(roleId);
                }
            }
        }

        /// <summary>
        /// Returns the explicit streaming override for a role when one has been configured.
        /// </summary>
        public bool TryGetStreamingOverride(string roleId, out bool enabled)
        {
            enabled = false;
            if (string.IsNullOrWhiteSpace(roleId))
            {
                return false;
            }

            lock (_lock)
            {
                return _streamingOverrides.TryGetValue(roleId.Trim(), out enabled);
            }
        }

        /// <summary>
        /// Resolves streaming for a role using the per-role override, then the supplied global
        /// fallback, then static <see cref="CoreAISettings"/>.
        /// </summary>
        public bool IsStreamingEnabled(string roleId, ICoreAISettings globalFallback = null)
        {
            if (TryGetStreamingOverride(roleId, out bool overriden))
            {
                return overriden;
            }

            if (globalFallback != null)
            {
                return globalFallback.EnableStreaming;
            }

            return CoreAISettings.EnableStreaming;
        }
    }
}
