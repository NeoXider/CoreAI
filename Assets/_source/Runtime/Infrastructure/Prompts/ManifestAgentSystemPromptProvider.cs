using CoreAI.Ai;

namespace CoreAI.Infrastructure.Prompts
{
    public sealed class ManifestAgentSystemPromptProvider : IAgentSystemPromptProvider
    {
        private readonly AgentPromptsManifest _manifest;

        public ManifestAgentSystemPromptProvider(AgentPromptsManifest manifest)
        {
            _manifest = manifest;
        }

        public bool TryGetSystemPrompt(string roleId, out string systemPrompt)
        {
            systemPrompt = null;
            if (_manifest == null || string.IsNullOrWhiteSpace(roleId))
                return false;

            foreach (var e in _manifest.EnumerateEntries())
            {
                if (e == null || string.IsNullOrWhiteSpace(e.roleId) || e.systemPrompt == null)
                    continue;
                if (e.roleId.Trim() != roleId.Trim())
                    continue;
                var t = e.systemPrompt.text;
                if (string.IsNullOrWhiteSpace(t))
                    return false;
                systemPrompt = t;
                return true;
            }

            return false;
        }
    }
}
