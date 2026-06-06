using System.Collections.Generic;
using UnityEngine;

namespace CoreAI.Infrastructure.Prompts
{
    /// <summary>
    /// Agent Prompts Manifest component used by CoreAI.
    /// </summary>
    [CreateAssetMenu(fileName = "AgentPromptsManifest", menuName = "CoreAI/Agent Prompts Manifest")]
    public sealed class AgentPromptsManifest : ScriptableObject
    {
        /// <summary>Prompt override entry for one built-in or custom role.</summary>
        [System.Serializable]
        public sealed class Entry
        {
            /// <summary>Role id.</summary>
            [Tooltip("Role id, for example Creator, Programmer, PlainChat, SmartChat, or a game-specific role.")]
            public string roleId;

            /// <summary>System prompt.</summary>
            [Tooltip("System prompt text for the model.")]
            public TextAsset systemPrompt;

            /// <summary>User prompt template.</summary>
            [Tooltip(
                "User prompt template for the orchestrator. Supported placeholders: {wave}, {mode}, {party}, {hint}.")]
            public TextAsset userPromptTemplate;

            /// <summary>
            /// Whether this role should ignore the global universal system prompt prefix.
            /// </summary>
            [Tooltip("Disable universalSystemPromptPrefix for this role when the prompt must be fully custom.")]
            public bool overrideUniversalPrefix;
        }

        /// <summary>Role overrides.</summary>
        [Header("Built-in Role Overrides")] public List<Entry> roleOverrides = new();

        /// <summary>Custom agents.</summary>
        [Header("Custom Agents")] public List<Entry> customAgents = new();

        /// <summary>Enumerates all prompt manifest entries configured in this asset.</summary>
        public IEnumerable<Entry> EnumerateEntries()
        {
            if (roleOverrides != null)
            {
                foreach (Entry e in roleOverrides)
                {
                    yield return e;
                }
            }

            if (customAgents != null)
            {
                foreach (Entry e in customAgents)
                {
                    yield return e;
                }
            }
        }

        /// <summary>
        /// Builds a Unity-free prompt snapshot by reading TextAsset contents.
        /// </summary>
        public AgentPromptsDefinition ToDefinition()
        {
            AgentPromptsDefinition definition = new();
            AddEntries(roleOverrides, definition.RoleOverrides);
            AddEntries(customAgents, definition.CustomAgents);
            return definition;
        }

        private static void AddEntries(
            IEnumerable<Entry> source,
            ICollection<AgentPromptEntryDefinition> destination)
        {
            if (source == null)
            {
                return;
            }

            foreach (Entry entry in source)
            {
                if (entry == null)
                {
                    continue;
                }

                destination.Add(new AgentPromptEntryDefinition
                {
                    RoleId = entry.roleId ?? "",
                    SystemPrompt = entry.systemPrompt != null ? entry.systemPrompt.text : "",
                    UserPromptTemplate = entry.userPromptTemplate != null ? entry.userPromptTemplate.text : "",
                    OverrideUniversalPrefix = entry.overrideUniversalPrefix
                });
            }
        }
    }
}