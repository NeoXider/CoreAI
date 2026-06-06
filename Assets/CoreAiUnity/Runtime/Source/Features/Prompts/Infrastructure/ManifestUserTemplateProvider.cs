using CoreAI.Ai;

namespace CoreAI.Infrastructure.Prompts
{
    /// <summary>Resolves user prompt templates from an AgentPrompts manifest.</summary>
    public sealed class ManifestUserTemplateProvider : IAgentUserPromptTemplateProvider
    {
        private readonly AgentPromptsDefinition _definition;

        /// <param name="manifest">The manifest value.</param>
        public ManifestUserTemplateProvider(AgentPromptsManifest manifest)
            : this(manifest != null ? manifest.ToDefinition() : null)
        {
        }

        /// <param name="definition">Unity-free prompt snapshot.</param>
        public ManifestUserTemplateProvider(AgentPromptsDefinition definition)
        {
            _definition = definition;
        }

        /// <inheritdoc />
        public bool TryGetUserTemplate(string roleId, out string template)
        {
            template = null;
            if (_definition == null || string.IsNullOrWhiteSpace(roleId))
            {
                return false;
            }

            foreach (AgentPromptEntryDefinition e in _definition.EnumerateEntries())
            {
                if (e == null || string.IsNullOrWhiteSpace(e.RoleId) || string.IsNullOrWhiteSpace(e.UserPromptTemplate))
                {
                    continue;
                }

                if (e.RoleId.Trim() != roleId.Trim())
                {
                    continue;
                }

                template = e.UserPromptTemplate;
                return true;
            }

            return false;
        }
    }
}