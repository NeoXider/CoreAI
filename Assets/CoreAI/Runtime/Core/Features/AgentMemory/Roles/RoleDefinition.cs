using System;
using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>
    /// Data that says what one agent role is, can do and knows: where its base prompt comes from, its memory,
    /// history and compaction policy, its tool-call roundtrip cap, the tool profiles it draws tools from, the
    /// skills it carries and the LLM routing profile it prefers. A game changes a role by applying a different
    /// definition (<see cref="AgentMemoryPolicy.ApplyRoleDefinition"/>, or the Unity role definition asset), not
    /// by editing package code. The nine built-in roles ship as definitions in
    /// <see cref="BuiltInRoleDefinitions"/>.
    /// </summary>
    /// <remarks>
    /// A plain data object so it can be filled from code, a ScriptableObject or deserialized JSON. The policy
    /// copies it when it is applied, so changing an instance afterwards does not change the live role.
    /// </remarks>
    public sealed class RoleDefinition
    {
        private IReadOnlyList<string> _toolProfiles = Array.Empty<string>();
        private IReadOnlyList<SkillSet> _skills = Array.Empty<SkillSet>();

        /// <summary>Creates an empty definition; set <see cref="Id"/> before applying it.</summary>
        public RoleDefinition()
        {
        }

        /// <summary>Creates a definition for <paramref name="id"/> with the package's default role settings.</summary>
        public RoleDefinition(string id)
        {
            Id = id;
        }

        /// <summary>Role id the definition configures (ordinal, trimmed when applied).</summary>
        public string Id { get; set; } = "";

        /// <summary>
        /// Prompt reference: the role id whose system-prompt chain (manifest, Resources, built-in text) supplies
        /// this role's base prompt. Empty means the role's own <see cref="Id"/>. Lets a host role reuse a
        /// built-in prompt, e.g. a <c>Blacksmith</c> that speaks with the Merchant prompt.
        /// </summary>
        public string PromptRoleId { get; set; } = "";

        /// <summary>
        /// Inline base system prompt. When non-empty it replaces the prompt-chain lookup for this role; the
        /// universal prefix and <see cref="AdditionalSystemPrompt"/> still apply around it.
        /// </summary>
        public string SystemPrompt { get; set; } = "";

        /// <summary>Role-specific text appended after the base prompt (the third prompt layer).</summary>
        public string AdditionalSystemPrompt { get; set; } = "";

        /// <summary>When true the role skips the global universal system prompt prefix.</summary>
        public bool OverrideUniversalPrefix { get; set; }

        /// <summary>Whether the built-in <c>memory</c> tool is offered to the role.</summary>
        public bool UseMemoryTool { get; set; } = true;

        /// <summary>Default memory action of the role's memory tool.</summary>
        public MemoryToolAction MemoryDefaultAction { get; set; } = MemoryToolAction.Append;

        /// <summary>Whether the role sends role-scoped chat history with each request.</summary>
        public bool WithChatHistory { get; set; } = true;

        /// <summary>Whether that chat history survives between sessions.</summary>
        public bool PersistChatHistory { get; set; }

        /// <summary>Per-role context window in tokens; 0 inherits the global setting.</summary>
        public int ContextTokens { get; set; }

        /// <summary>Maximum chat history messages kept for the role.</summary>
        public int MaxChatHistoryMessages { get; set; } = 30;

        /// <summary>Whether overflowing history may be folded by an auxiliary LLM request (global gate still applies).</summary>
        public bool UseLlmContextCompaction { get; set; } = true;

        /// <summary>Per-role compaction trigger ratio in (0, 1]; null inherits the global setting.</summary>
        public float? CompactionTriggerRatio { get; set; }

        /// <summary>How observed tool results are kept in the role's chat history.</summary>
        public ToolResultMemoryPolicy ToolResultMemory { get; set; } = ToolResultMemoryPolicy.CompactSummary;

        /// <summary>Tool-call roundtrip cap: null inherits the global cap, 0 is unlimited, a positive value caps the loop.</summary>
        public int? MaxToolCallRoundtrips { get; set; }

        /// <summary>Per-role response token cap: null inherits, 0 is unlimited.</summary>
        public int? MaxOutputTokens { get; set; }

        /// <summary>Per-role sampling temperature; null inherits the global setting.</summary>
        public float? Temperature { get; set; }

        /// <summary>Per-role override of duplicate tool-call rejection; null inherits the global setting.</summary>
        public bool? AllowDuplicateToolCalls { get; set; }

        /// <summary>Per-role streaming preference; null inherits <see cref="ICoreAISettings.EnableStreaming"/>.</summary>
        public bool? EnableStreaming { get; set; }

        /// <summary>
        /// Tool profiles (see <see cref="BuiltInToolProfileIds"/>) whose tools and skills the role receives, in
        /// order. Installers register tools into profiles; replacing this list changes what the role can call.
        /// </summary>
        public IReadOnlyList<string> ToolProfiles
        {
            get => _toolProfiles;
            set => _toolProfiles = value ?? Array.Empty<string>();
        }

        /// <summary>Skills the role carries in addition to the skills of its tool profiles.</summary>
        public IReadOnlyList<SkillSet> Skills
        {
            get => _skills;
            set => _skills = value ?? Array.Empty<SkillSet>();
        }

        /// <summary>Preferred LLM routing profile for the role; empty uses role/default routing.</summary>
        public string RoutingProfileId { get; set; } = "";

        /// <summary>Returns a copy whose lists are detached from this instance.</summary>
        public RoleDefinition Clone()
        {
            RoleDefinition copy = (RoleDefinition)MemberwiseClone();
            copy._toolProfiles = new List<string>(_toolProfiles).AsReadOnly();
            copy._skills = new List<SkillSet>(_skills).AsReadOnly();
            return copy;
        }

        /// <summary>The memory, history and LLM settings of this definition as the policy stores them.</summary>
        public AgentMemoryPolicy.RoleMemoryConfig ToRoleMemoryConfig()
        {
            AgentMemoryPolicy.RoleMemoryConfig config = new(
                UseMemoryTool,
                MemoryDefaultAction,
                WithChatHistory,
                PersistChatHistory,
                ContextTokens,
                AllowDuplicateToolCalls,
                MaxChatHistoryMessages,
                MaxOutputTokens.HasValue && MaxOutputTokens.Value >= 0 ? MaxOutputTokens : null,
                UseLlmContextCompaction,
                Temperature,
                ToolResultMemory,
                CompactionTriggerRatio);
            config.MaxToolCallRoundtrips = MaxToolCallRoundtrips.HasValue && MaxToolCallRoundtrips.Value >= 0
                ? MaxToolCallRoundtrips
                : null;
            return config;
        }

        /// <summary>Trimmed, de-duplicated (ordinal) tool profile ids, in order.</summary>
        internal List<string> NormalizedToolProfiles()
        {
            return NormalizeProfileIds(_toolProfiles);
        }

        internal static List<string> NormalizeProfileIds(IEnumerable<string> profileIds)
        {
            List<string> result = new();
            if (profileIds == null)
            {
                return result;
            }

            foreach (string profileId in profileIds)
            {
                string trimmed = profileId?.Trim();
                if (string.IsNullOrEmpty(trimmed) || result.Contains(trimmed))
                {
                    continue;
                }

                result.Add(trimmed);
            }

            return result;
        }
    }
}
