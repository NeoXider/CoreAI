using System.Collections.Generic;

namespace CoreAI.Infrastructure.Prompts
{
    public sealed class AgentPromptEntryDefinition
    {
        public string RoleId { get; set; } = "";
        public string SystemPrompt { get; set; } = "";
        public string UserPromptTemplate { get; set; } = "";
        public bool OverrideUniversalPrefix { get; set; }
    }

    /// <summary>
    /// Unity-free snapshot of role prompt overrides and custom agent prompts.
    /// </summary>
    public sealed class AgentPromptsDefinition
    {
        public List<AgentPromptEntryDefinition> RoleOverrides { get; } = new();
        public List<AgentPromptEntryDefinition> CustomAgents { get; } = new();

        public IEnumerable<AgentPromptEntryDefinition> EnumerateEntries()
        {
            foreach (AgentPromptEntryDefinition entry in RoleOverrides)
            {
                yield return entry;
            }

            foreach (AgentPromptEntryDefinition entry in CustomAgents)
            {
                yield return entry;
            }
        }
    }
}