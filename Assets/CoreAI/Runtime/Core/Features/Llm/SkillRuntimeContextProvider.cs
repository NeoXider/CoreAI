using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>
    /// <see cref="IAgentRuntimeContextProvider"/> that injects the skill catalog
    /// into the system prompt — self-service pattern.
    /// <para>
    /// Instead of injecting full instructions for active skills, this provider
    /// adds a lightweight catalog (name + description + tool names per skill).
    /// The model reads full instructions on demand via <see cref="ReadSkillLlmTool"/>.
    /// </para>
    /// <para>
    /// Registered automatically by <see cref="AgentConfig.ApplyToPolicy"/> when the agent
    /// has skills registered via <see cref="AgentBuilder.WithSkill"/>.
    /// </para>
    /// </summary>
    internal sealed class SkillRuntimeContextProvider : IAgentRuntimeContextProvider
    {
        private readonly IReadOnlyList<SkillSet> _skills;
        private string _cachedCatalog;

        public SkillRuntimeContextProvider(IReadOnlyList<SkillSet> skills)
        {
            _skills = skills ?? throw new System.ArgumentNullException(nameof(skills));
        }

        public string BuildContext(AiTaskRequest request, string roleId, string traceId)
        {
            if (_skills.Count == 0)
            {
                return "";
            }

            // Cache the catalog since it doesn't change between requests
            if (_cachedCatalog == null)
            {
                _cachedCatalog = SkillSet.BuildCatalog(_skills);
            }

            return _cachedCatalog;
        }
    }
}
