using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>
    /// Builds the skill catalog prompt section for roles that use on-demand tools.
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