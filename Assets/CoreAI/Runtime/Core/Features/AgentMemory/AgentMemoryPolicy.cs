using System;
using System.Collections.Generic;
using CoreAI.AgentMemory;

namespace CoreAI.Ai
{
    /// <summary>
    /// Mutable role policy for memory tools, chat history, prompt overlays, and per-role
    /// LLM behaviour overrides.
    /// </summary>
    public sealed class AgentMemoryPolicy
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, RoleMemoryConfig> _roleConfigs;
        private readonly Dictionary<string, List<ILlmTool>> _customTools = new();

        private readonly Dictionary<string, MutableSkillCatalog> _roleSkillCatalogs =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, IAgentRuntimeContextProvider> _runtimeContextProviders = new();
        private static readonly MemoryLlmTool _memoryToolInstance = new();

        /// <summary>
        /// Replaces the direct tool list for a role; pass an empty list to clear custom tools.
        /// </summary>
        public void SetToolsForRole(string roleId, IReadOnlyList<ILlmTool> tools)
        {
            lock (_lock)
            {
                List<ILlmTool> replacement = tools == null
                    ? new List<ILlmTool>()
                    : new List<ILlmTool>(tools);

                if (_roleSkillCatalogs.TryGetValue(roleId, out MutableSkillCatalog catalog))
                {
                    replacement.RemoveAll(tool => tool != null &&
                        (string.Equals(tool.Name, "read_skill", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(tool.Name, "call_skill_tool", StringComparison.OrdinalIgnoreCase)));
                    replacement.Add(ReadSkillLlmTool.Create(catalog));
                    replacement.Add(CallSkillToolLlmTool.Create(catalog));
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

        /// <summary>
        /// Append a single tool to an existing role's tool list (e.g. the <c>read_skill</c> meta-tool).
        /// If no tools exist for the role yet, a new list is created.
        /// </summary>
        public void AddToolForRole(string roleId, ILlmTool tool)
        {
            if (string.IsNullOrWhiteSpace(roleId) || tool == null)
            {
                return;
            }

            lock (_lock)
            {
                if (!_customTools.TryGetValue(roleId, out List<ILlmTool> list))
                {
                    list = new List<ILlmTool>();
                    _customTools[roleId] = list;
                }

                list.Add(tool);
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
                AddToolForRole(roleId, CallSkillToolLlmTool.Create(catalog));
            }
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

            public RoleMemoryConfig(bool useMemoryTool = true, MemoryToolAction defaultAction = MemoryToolAction.Append,
                bool withChatHistory = true, bool persistChatHistory = false, int contextTokens = 0,
                bool? allowDuplicateToolCalls = null, int maxChatHistoryMessages = 30, int? maxOutputTokens = null,
                bool useLlmContextCompaction = true, float? temperature = null,
                ToolResultMemoryPolicy toolResultMemory = ToolResultMemoryPolicy.CompactSummary,
                float? compactionTriggerRatio = null)
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
                ToolResultMemory = toolResultMemory;
                CompactionTriggerRatio = NormalizeCompactionTriggerRatio(compactionTriggerRatio);
                MaxToolCallRoundtrips = null;
            }
        }

        public void ConfigureChatHistory(string roleId, bool enabled, int tokens, bool persist,
            int maxChatHistoryMessages = 30)
        {
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


        public AgentMemoryPolicy()
        {
            _roleConfigs = new Dictionary<string, RoleMemoryConfig>();

            foreach (string roleId in BuiltInAgentRoleIds.AllBuiltInRoles)
            {
                bool isProgrammer = roleId == BuiltInAgentRoleIds.Programmer;
                bool needsExactToolOutput = isProgrammer || roleId == BuiltInAgentRoleIds.CoreMechanic;
                bool smartCompaction = !isProgrammer;
                // WHY: Programmer keeps history off by default; chat-source runs enable it per-run
                // without mutating global policy (see AiOrchestratorHistoryEditModeTests).
                // Code/mechanics agents need exact tool output across turns for iterative correctness.
                RoleMemoryConfig builtIn = new(
                    true,
                    MemoryToolAction.Append,
                    !isProgrammer,
                    toolResultMemory: needsExactToolOutput
                        ? ToolResultMemoryPolicy.Full
                        : ToolResultMemoryPolicy.CompactSummary,
                    useLlmContextCompaction: smartCompaction);

                // WHY: The Programmer writes/iterates Lua and routinely needs many tool roundtrips in one turn
                // (generate → run → read error → fix → re-run …), and the Creator orchestrates a whole
                // build across many tool calls. Cap both at 0 = unlimited so they are never cut off
                // mid-task. Other built-in roles inherit the global default (null = 20).
                if (isProgrammer || roleId == BuiltInAgentRoleIds.Creator)
                {
                    builtIn.MaxToolCallRoundtrips = 0;
                }

                _roleConfigs[roleId] = builtIn;
            }

            // WHY: Built-in chat roles:
            // - PlainChat: no MemoryTool by default, persistent chat history only.
            // - SmartChat: MemoryTool + persistent chat history by default.
            _roleConfigs[BuiltInAgentRoleIds.PlainChat] = new RoleMemoryConfig(
                false,
                withChatHistory: true,
                persistChatHistory: true,
                useLlmContextCompaction: true);
            _roleConfigs[BuiltInAgentRoleIds.SmartChat] = new RoleMemoryConfig(
                true,
                MemoryToolAction.Append,
                true,
                true,
                useLlmContextCompaction: true);
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
        /// Returns the effective tools for a role, including the built-in memory tool when enabled.
        /// </summary>
        public IReadOnlyList<ILlmTool> GetToolsForRole(string roleId)
        {
            lock (_lock)
            {
                List<ILlmTool> tools = new();

                if (_customTools.TryGetValue(roleId, out List<ILlmTool> custom) && custom != null && custom.Count > 0)
                {
                    bool customHasMemory = ListContainsMemoryTool(custom);

                    if (IsMemoryEnabledLocked(roleId) && !customHasMemory)
                    {
                        tools.Add(_memoryToolInstance);
                    }

                    tools.AddRange(custom);
                }
                else if (IsMemoryEnabledLocked(roleId))
                {
                    tools.Add(_memoryToolInstance);
                }

                return tools.Count > 0 ? tools : Array.Empty<ILlmTool>();
            }
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
