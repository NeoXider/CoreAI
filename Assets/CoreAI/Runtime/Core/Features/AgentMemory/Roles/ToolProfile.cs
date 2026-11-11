using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>
    /// Read-only snapshot of one tool profile: the tool names registered into it and its skills, in registration
    /// order. Returned by <see cref="AgentMemoryPolicy.GetToolProfile"/>; changing the profile does not change a
    /// snapshot taken earlier.
    /// </summary>
    public sealed class ToolProfile
    {
        internal ToolProfile(string id, IReadOnlyList<string> toolNames, IReadOnlyList<SkillSet> skills)
        {
            Id = id;
            ToolNames = toolNames;
            Skills = skills;
        }

        /// <summary>Profile name, e.g. <see cref="BuiltInToolProfileIds.LuaBuilder"/>.</summary>
        public string Id { get; }

        /// <summary>Names of the tools a role referencing this profile receives.</summary>
        public IReadOnlyList<string> ToolNames { get; }

        /// <summary>Skills a role referencing this profile receives.</summary>
        public IReadOnlyList<SkillSet> Skills { get; }
    }
}
