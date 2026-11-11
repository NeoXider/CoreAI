using System;
using System.Collections.Generic;
using CoreAI.Ai;
using UnityEngine;

namespace CoreAI.Unity
{
    /// <summary>
    /// Inspector-authored <see cref="RoleDefinition"/>: what one agent role is, can do and knows. Assigning an asset
    /// whose role id names a built-in role replaces that role's shipped definition, with no code; an asset with a
    /// new id adds a role. <see cref="CoreAI.Composition.AgentRoleDefinitionsInstaller"/> applies the assets to the
    /// agent policy. Only data lives here; nothing references scene objects.
    /// </summary>
    [CreateAssetMenu(fileName = "NewAgentRoleDefinition", menuName = "CoreAI/Agent Role Definition", order = 201)]
    public sealed class AgentRoleDefinitionAsset : ScriptableObject
    {
        /// <summary>A per-role setting that may inherit the global value or force it on or off.</summary>
        public enum Toggle
        {
            /// <summary>Use the global setting.</summary>
            Inherit = 0,

            /// <summary>Force the setting on for this role.</summary>
            On = 1,

            /// <summary>Force the setting off for this role.</summary>
            Off = 2
        }

        [Header("Identity")]
        [Tooltip("Role id this definition configures, e.g. Programmer (replaces the built-in) or Blacksmith (a new role).")]
        [SerializeField]
        private string roleId = "";

        [Header("Prompt")]
        [Tooltip("Role id whose prompt chain supplies the base prompt. Empty uses the role id above.")]
        [SerializeField]
        private string promptRoleId = "";

        [Tooltip("Inline base prompt. When set it replaces the prompt-chain lookup for this role.")]
        [TextArea(3, 12)]
        [SerializeField]
        private string systemPrompt = "";

        [Tooltip("Role-specific text appended after the base prompt.")]
        [TextArea(2, 8)]
        [SerializeField]
        private string additionalSystemPrompt = "";

        [Tooltip("Skip the universal system prompt prefix for this role.")]
        [SerializeField]
        private bool overrideUniversalPrefix;

        [Header("Memory and history")]
        [Tooltip("Offer the built-in memory tool to the role.")]
        [SerializeField]
        private bool useMemoryTool = true;

        [SerializeField]
        private MemoryToolAction memoryDefaultAction = MemoryToolAction.Append;

        [Tooltip("Send role-scoped chat history with each request.")]
        [SerializeField]
        private bool withChatHistory = true;

        [Tooltip("Keep that chat history between sessions.")]
        [SerializeField]
        private bool persistChatHistory;

        [Tooltip("Per-role context window in tokens; 0 inherits the global setting.")]
        [Min(0)]
        [SerializeField]
        private int contextTokens;

        [Min(1)]
        [SerializeField]
        private int maxChatHistoryMessages = 30;

        [Tooltip("Allow overflowing history to be folded by an auxiliary LLM request (the global gate still applies).")]
        [SerializeField]
        private bool useLlmContextCompaction = true;

        [SerializeField]
        private bool overrideCompactionTriggerRatio;

        [Range(0.05f, 1f)]
        [SerializeField]
        private float compactionTriggerRatio = CoreAISettings.DefaultConversationCompactionTriggerRatio;

        [SerializeField]
        private ToolResultMemoryPolicy toolResultMemory = ToolResultMemoryPolicy.CompactSummary;

        [Header("LLM limits")]
        [Tooltip("Set a per-role tool-call roundtrip cap instead of inheriting the global one.")]
        [SerializeField]
        private bool overrideMaxToolCallRoundtrips;

        [Tooltip("0 = unlimited.")]
        [Min(0)]
        [SerializeField]
        private int maxToolCallRoundtrips;

        [SerializeField]
        private bool overrideMaxOutputTokens;

        [Tooltip("0 = unlimited.")]
        [Min(0)]
        [SerializeField]
        private int maxOutputTokens;

        [SerializeField]
        private bool overrideTemperature;

        [Range(0f, 2f)]
        [SerializeField]
        private float temperature = 0.1f;

        [SerializeField]
        private Toggle allowDuplicateToolCalls = Toggle.Inherit;

        [SerializeField]
        private Toggle enableStreaming = Toggle.Inherit;

        [Header("Tools and skills")]
        [Tooltip("Tool profiles the role draws tools and skills from, e.g. lua-builder, world-builder, vision, npc, " +
                 "observer, inventory, or a profile your game registers.")]
        [SerializeField]
        private string[] toolProfiles = Array.Empty<string>();

        [Tooltip("Skills the role carries in addition to the skills of its tool profiles.")]
        [SerializeField]
        private SkillSetAsset[] skills = Array.Empty<SkillSetAsset>();

        [Header("Routing")]
        [Tooltip("Preferred LLM routing profile; empty uses role/default routing.")]
        [SerializeField]
        private string routingProfileId = "";

        /// <summary>Role id the asset configures.</summary>
        public string RoleId => roleId ?? "";

        /// <summary>Builds the portable definition this asset describes (a fresh instance on every call).</summary>
        public RoleDefinition ToRoleDefinition()
        {
            List<string> profiles = new();
            if (toolProfiles != null)
            {
                foreach (string profile in toolProfiles)
                {
                    if (!string.IsNullOrWhiteSpace(profile))
                    {
                        profiles.Add(profile.Trim());
                    }
                }
            }

            List<SkillSet> skillSets = new();
            if (skills != null)
            {
                foreach (SkillSetAsset skill in skills)
                {
                    if (skill != null)
                    {
                        skillSets.Add(skill.BuildSkillSet());
                    }
                }
            }

            return new RoleDefinition((roleId ?? "").Trim())
            {
                PromptRoleId = promptRoleId ?? "",
                SystemPrompt = systemPrompt ?? "",
                AdditionalSystemPrompt = additionalSystemPrompt ?? "",
                OverrideUniversalPrefix = overrideUniversalPrefix,
                UseMemoryTool = useMemoryTool,
                MemoryDefaultAction = memoryDefaultAction,
                WithChatHistory = withChatHistory,
                PersistChatHistory = persistChatHistory,
                ContextTokens = Math.Max(0, contextTokens),
                MaxChatHistoryMessages = maxChatHistoryMessages,
                UseLlmContextCompaction = useLlmContextCompaction,
                CompactionTriggerRatio = overrideCompactionTriggerRatio ? compactionTriggerRatio : (float?)null,
                ToolResultMemory = toolResultMemory,
                MaxToolCallRoundtrips = overrideMaxToolCallRoundtrips ? Math.Max(0, maxToolCallRoundtrips) : (int?)null,
                MaxOutputTokens = overrideMaxOutputTokens ? Math.Max(0, maxOutputTokens) : (int?)null,
                Temperature = overrideTemperature ? temperature : (float?)null,
                AllowDuplicateToolCalls = ToNullable(allowDuplicateToolCalls),
                EnableStreaming = ToNullable(enableStreaming),
                ToolProfiles = profiles,
                Skills = skillSets,
                RoutingProfileId = routingProfileId ?? ""
            };
        }

        /// <summary>
        /// Overwrites this asset with <paramref name="definition"/>'s values (skills excepted: assets keep their
        /// own <see cref="SkillSetAsset"/> references). Editor tooling uses it to start from a built-in role.
        /// </summary>
        public void CopyFrom(RoleDefinition definition)
        {
            if (definition == null)
            {
                throw new ArgumentNullException(nameof(definition));
            }

            roleId = definition.Id ?? "";
            promptRoleId = definition.PromptRoleId ?? "";
            systemPrompt = definition.SystemPrompt ?? "";
            additionalSystemPrompt = definition.AdditionalSystemPrompt ?? "";
            overrideUniversalPrefix = definition.OverrideUniversalPrefix;
            useMemoryTool = definition.UseMemoryTool;
            memoryDefaultAction = definition.MemoryDefaultAction;
            withChatHistory = definition.WithChatHistory;
            persistChatHistory = definition.PersistChatHistory;
            contextTokens = definition.ContextTokens;
            maxChatHistoryMessages = definition.MaxChatHistoryMessages;
            useLlmContextCompaction = definition.UseLlmContextCompaction;
            overrideCompactionTriggerRatio = definition.CompactionTriggerRatio.HasValue;
            compactionTriggerRatio = definition.CompactionTriggerRatio ??
                                     CoreAISettings.DefaultConversationCompactionTriggerRatio;
            toolResultMemory = definition.ToolResultMemory;
            overrideMaxToolCallRoundtrips = definition.MaxToolCallRoundtrips.HasValue;
            maxToolCallRoundtrips = definition.MaxToolCallRoundtrips ?? 0;
            overrideMaxOutputTokens = definition.MaxOutputTokens.HasValue;
            maxOutputTokens = definition.MaxOutputTokens ?? 0;
            overrideTemperature = definition.Temperature.HasValue;
            temperature = definition.Temperature ?? 0.1f;
            allowDuplicateToolCalls = ToToggle(definition.AllowDuplicateToolCalls);
            enableStreaming = ToToggle(definition.EnableStreaming);
            toolProfiles = new List<string>(definition.ToolProfiles).ToArray();
            routingProfileId = definition.RoutingProfileId ?? "";
        }

        /// <summary>Resets every field to the shipped definition of the built-in role named by the role id.</summary>
        [ContextMenu("Load the built-in definition of this role id")]
        private void LoadBuiltInDefinition()
        {
            if (BuiltInRoleDefinitions.TryCreate((roleId ?? "").Trim(), out RoleDefinition builtIn))
            {
                CopyFrom(builtIn);
            }
        }

        private static bool? ToNullable(Toggle toggle)
        {
            return toggle switch
            {
                Toggle.On => true,
                Toggle.Off => false,
                _ => null
            };
        }

        private static Toggle ToToggle(bool? value)
        {
            return value.HasValue ? value.Value ? Toggle.On : Toggle.Off : Toggle.Inherit;
        }
    }
}
