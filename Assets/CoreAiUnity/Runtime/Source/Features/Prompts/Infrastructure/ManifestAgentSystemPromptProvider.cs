using CoreAI.Ai;

namespace CoreAI.Infrastructure.Prompts
{
    /// <summary>Системные промпты из <see cref="AgentPromptsManifest"/>.</summary>
    public sealed class ManifestAgentSystemPromptProvider : IAgentSystemPromptProvider
    {
        private readonly AgentPromptsManifest _manifest;

        /// <param name="manifest">ScriptableObject с записями ролей; может быть <c>null</c>.</param>
        public ManifestAgentSystemPromptProvider(AgentPromptsManifest manifest)
        {
            _manifest = manifest;
        }

        /// <inheritdoc />
        public bool TryGetSystemPrompt(string roleId, out string systemPrompt)
        {
            systemPrompt = null;
            if (_manifest == null || string.IsNullOrWhiteSpace(roleId))
            {
                return false;
            }

            foreach (AgentPromptsManifest.Entry e in _manifest.EnumerateEntries())
            {
                if (e == null || string.IsNullOrWhiteSpace(e.roleId) || e.systemPrompt == null)
                {
                    continue;
                }

                if (e.roleId.Trim() != roleId.Trim())
                {
                    continue;
                }

                string t = e.systemPrompt.text;
                if (string.IsNullOrWhiteSpace(t))
                {
                    return false;
                }

                systemPrompt = t;
                return true;
            }

            return false;
        }
    }
}