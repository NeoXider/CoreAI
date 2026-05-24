using CoreAI.Ai;

namespace CoreAI.Infrastructure.Prompts
{
    /// <summary>Resolves agent system prompts from an AgentPrompts manifest.</summary>
    public sealed class ManifestAgentSystemPromptProvider : IAgentSystemPromptProvider
    {
        private readonly AgentPromptsDefinition _definition;

        /// <param name="manifest">The manifest value.</param>
        public ManifestAgentSystemPromptProvider(AgentPromptsManifest manifest)
            : this(manifest != null ? manifest.ToDefinition() : null)
        {
        }

        /// <param name="definition">Unity-free prompt snapshot.</param>
        public ManifestAgentSystemPromptProvider(AgentPromptsDefinition definition)
        {
            _definition = definition;
        }

        /// <inheritdoc />
        public bool TryGetSystemPrompt(string roleId, out string systemPrompt)
        {
            systemPrompt = null;
            if (_definition == null || string.IsNullOrWhiteSpace(roleId))
            {
                return false;
            }

            foreach (AgentPromptEntryDefinition e in _definition.EnumerateEntries())
            {
                if (e == null || string.IsNullOrWhiteSpace(e.RoleId) || string.IsNullOrWhiteSpace(e.SystemPrompt))
                {
                    continue;
                }

                if (e.RoleId.Trim() != roleId.Trim())
                {
                    continue;
                }

                systemPrompt = e.SystemPrompt;
                return true;
            }

            return false;
        }
    }
}
