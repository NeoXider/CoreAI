using CoreAI.Ai;

namespace CoreAI.Infrastructure.Prompts
{
    public sealed class ManifestUserTemplateProvider : IAgentUserPromptTemplateProvider
    {
        private readonly AgentPromptsManifest _manifest;

        public ManifestUserTemplateProvider(AgentPromptsManifest manifest)
        {
            _manifest = manifest;
        }

        public bool TryGetUserTemplate(string roleId, out string template)
        {
            template = null;
            if (_manifest == null || string.IsNullOrWhiteSpace(roleId))
                return false;

            foreach (var e in _manifest.EnumerateEntries())
            {
                if (e == null || string.IsNullOrWhiteSpace(e.roleId) || e.userPromptTemplate == null)
                    continue;
                if (e.roleId.Trim() != roleId.Trim())
                    continue;
                var t = e.userPromptTemplate.text;
                if (string.IsNullOrWhiteSpace(t))
                    return false;
                template = t;
                return true;
            }

            return false;
        }
    }
}
