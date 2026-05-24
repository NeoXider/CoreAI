using System;
using System.Collections.Generic;
using System.Reflection;

namespace CoreAI.Ai
{
    /// <summary>
    /// LLM tool that reads skill instructions and metadata.
    /// </summary>
    internal static class ReadSkillLlmTool
    {
        /// <summary>
        /// Creates the <c>read_skill</c> <see cref="DelegateLlmTool"/>.
        /// Uses <see cref="DelegateLlmTool"/> so MEAI auto-generates the JSON schema.
        /// </summary>
        public static DelegateLlmTool Create(IReadOnlyList<SkillSet> skills)
        {
            if (skills == null)
            {
                throw new ArgumentNullException(nameof(skills));
            }

            Dictionary<string, SkillSet> skillsByName = new(StringComparer.OrdinalIgnoreCase);
            foreach (SkillSet skill in skills)
            {
                if (skill != null && !string.IsNullOrWhiteSpace(skill.Name))
                {
                    skillsByName[skill.Name] = skill;
                }
            }

            // Use local function so MEAI sees the parameter name 'skill_name' in the schema
            object ReadSkillFn(string skill_name)
            {
                return Execute(skill_name, skillsByName);
            }

            DelegateLlmTool tool = new(
                "read_skill",
                "Read the full instructions and tool list for a skill. Call this BEFORE using " +
                "call_skill_tool so you know which tools are available and what parameters they need. " +
                "Pass the skill name exactly as listed in the catalog.",
                new Func<string, object>(ReadSkillFn));

            tool.AllowDuplicates = true; // Model may read multiple skills
            return tool;
        }

        private static object Execute(string skillName, Dictionary<string, SkillSet> skillsByName)
        {
            if (string.IsNullOrWhiteSpace(skillName))
            {
                return new { error = "skill_name is required.", available = new List<string>(skillsByName.Keys) };
            }

            string trimmed = skillName.Trim();

            if (skillsByName.TryGetValue(trimmed, out SkillSet skill))
            {
                // Return full instructions + tool schemas with parameters
                List<object> toolSchemas = new();
                foreach (ILlmTool t in skill.Tools)
                {
                    List<object> parameters = new();
                    if (t is DelegateLlmTool dt)
                    {
                        ParameterInfo[] pars = dt.ActionDelegate.Method.GetParameters();
                        foreach (ParameterInfo p in pars)
                        {
                            parameters.Add(new
                            {
                                name = p.Name,
                                type = GetFriendlyTypeName(p.ParameterType),
                                required = !p.HasDefaultValue
                            });
                        }
                    }

                    toolSchemas.Add(new
                    {
                        tool_name = t.Name,
                        description = t.Description,
                        parameters
                    });
                }

                return new
                {
                    skill = skill.Name,
                    instructions = skill.Instructions,
                    tools = toolSchemas,
                    usage = "Call call_skill_tool(tool_name, arguments_json) to use any tool listed above. " +
                            "arguments_json is a JSON object string with the parameter names and values."
                };
            }

            // Fuzzy match
            foreach (KeyValuePair<string, SkillSet> kvp in skillsByName)
            {
                if (kvp.Key.IndexOf(trimmed, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    trimmed.IndexOf(kvp.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return new
                    {
                        error = $"Skill '{trimmed}' not found. Did you mean '{kvp.Key}'?",
                        available = new List<string>(skillsByName.Keys)
                    };
                }
            }

            return new
            {
                error = $"Skill '{trimmed}' not found.",
                available = new List<string>(skillsByName.Keys)
            };
        }

        private static string GetFriendlyTypeName(Type type)
        {
            if (type == typeof(string))
            {
                return "string";
            }

            if (type == typeof(int))
            {
                return "int";
            }

            if (type == typeof(float))
            {
                return "float";
            }

            if (type == typeof(double))
            {
                return "double";
            }

            if (type == typeof(bool))
            {
                return "bool";
            }

            if (type == typeof(long))
            {
                return "long";
            }

            return type.Name;
        }
    }
}
