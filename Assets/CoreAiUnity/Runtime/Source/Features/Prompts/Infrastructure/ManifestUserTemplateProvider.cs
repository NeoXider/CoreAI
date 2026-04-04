using CoreAI.Ai;

namespace CoreAI.Infrastructure.Prompts
{
    /// <summary>User-шаблоны из <see cref="AgentPromptsManifest"/> (переопределения и кастомные роли).</summary>
    public sealed class ManifestUserTemplateProvider : IAgentUserPromptTemplateProvider
    {
        private readonly AgentPromptsManifest _manifest;

        /// <param name="manifest">ScriptableObject с записями ролей; может быть <c>null</c> (всегда miss).</param>
        public ManifestUserTemplateProvider(AgentPromptsManifest manifest)
        {
            _manifest = manifest;
        }

        /// <inheritdoc />
        public bool TryGetUserTemplate(string roleId, out string template)
        {
            template = null;
            if (_manifest == null || string.IsNullOrWhiteSpace(roleId))
            {
                return false;
            }

            foreach (AgentPromptsManifest.Entry e in _manifest.EnumerateEntries())
            {
                if (e == null || string.IsNullOrWhiteSpace(e.roleId) || e.userPromptTemplate == null)
                {
                    continue;
                }

                if (e.roleId.Trim() != roleId.Trim())
                {
                    continue;
                }

                string t = e.userPromptTemplate.text;
                if (string.IsNullOrWhiteSpace(t))
                {
                    return false;
                }

                template = t;
                return true;
            }

            return false;
        }
    }
}