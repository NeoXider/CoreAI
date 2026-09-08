using System;
using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>
    /// Unity-free skill authoring snapshot. Tools are still supplied by code when building a SkillSet.
    /// </summary>
    public sealed class SkillSetDefinition
    {
        public string Name { get; set; } = "NewSkill";
        public string Description { get; set; } = "";
        public string Instructions { get; set; } = "";
        /// <summary>Ordered documents; the first is the complete entry file. Empty preserves legacy Instructions.</summary>
        public SkillSection[] Sections { get; set; } = Array.Empty<SkillSection>();

        public SkillSet BuildSkillSet(params ILlmTool[] tools)
        {
            if (Sections == null || Sections.Length == 0)
            {
                return new SkillSet(Name, Description, Instructions, tools);
            }

            List<KeyValuePair<string, string>> parts = new(Sections.Length);
            foreach (SkillSection section in Sections)
            {
                parts.Add(new KeyValuePair<string, string>(section.Name, section.Content));
            }
            return SkillSet.FromTextParts(Name, Description, parts, tools);
        }
    }
}
