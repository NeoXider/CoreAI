using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.AI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CoreAI.Ai
{
    /// <summary>
    /// LLM tool that reads skill instructions and metadata.
    /// </summary>
    internal static class ReadSkillLlmTool
    {
        /// <summary>
        /// Creates the <c>read_skill</c> tool.
        /// </summary>
        public static ILlmTool Create(IReadOnlyList<SkillSet> skills)
        {
            return Create(skills, null);
        }

        internal static ILlmTool Create(IReadOnlyList<SkillSet> skills, IReadOnlyCollection<string> allowedToolNames)
        {
            return new ReadSkillProxy(skills, allowedToolNames);
        }

        private sealed class ReadSkillProxy : LlmToolBase, IAIFunctionLlmTool, ISkillSetMetaLlmTool
        {
            private readonly IReadOnlyList<SkillSet> _skills;
            private readonly Dictionary<string, SkillSet> _skillsByName;
            private readonly IReadOnlyCollection<string> _allowedToolNames;
            private readonly HashSet<string> _skillToolNames;

            public ReadSkillProxy(IReadOnlyList<SkillSet> skills, IReadOnlyCollection<string> allowedToolNames)
            {
                _skills = skills ?? throw new ArgumentNullException(nameof(skills));
                _allowedToolNames = allowedToolNames;
                _skillsByName = new Dictionary<string, SkillSet>(StringComparer.OrdinalIgnoreCase);
                _skillToolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (SkillSet skill in _skills)
                {
                    if (skill != null && !string.IsNullOrWhiteSpace(skill.Name))
                    {
                        _skillsByName[skill.Name] = skill;
                    }

                    foreach (SkillToolDescriptor descriptor in SkillSetToolResolver.BuildDescriptors(skill))
                    {
                        if (!string.IsNullOrWhiteSpace(descriptor.Name))
                        {
                            _skillToolNames.Add(descriptor.Name);
                        }
                    }
                }
            }

            public override string Name => "read_skill";

            public override string Description =>
                "Read the full instructions and tool list for a skill. Call this BEFORE using " +
                "call_skill_tool so you know which tools are available and what parameters they need. " +
                "Pass the skill name exactly as listed in the catalog.";

            public override string ParametersSchema =>
                "{\"type\":\"object\",\"properties\":{\"skill_name\":{\"type\":\"string\",\"description\":\"Skill name exactly as listed in the catalog.\"}},\"required\":[\"skill_name\"]}";

            public override bool AllowDuplicates => true;

            public bool ContainsSkillTool(string toolName)
            {
                return !string.IsNullOrWhiteSpace(toolName) && _skillToolNames.Contains(toolName.Trim());
            }

            public ILlmTool RestrictTo(IReadOnlyCollection<string> allowedToolNames)
            {
                return new ReadSkillProxy(_skills, allowedToolNames);
            }

            public AIFunction CreateAIFunction()
            {
                return AIFunctionFactory.Create(
                    (Func<string, string>)Execute,
                    new AIFunctionFactoryOptions
                    {
                        Name = Name,
                        Description = Description
                    });
            }

            private string Execute(string skill_name)
            {
                return ReadSkillLlmTool.Execute(skill_name, _skillsByName, _allowedToolNames);
            }
        }

        private static string Execute(string skillName, Dictionary<string, SkillSet> skillsByName,
            IReadOnlyCollection<string> allowedToolNames)
        {
            return JsonConvert.SerializeObject(ExecuteObject(skillName, skillsByName, allowedToolNames));
        }

        private static object ExecuteObject(string skillName, Dictionary<string, SkillSet> skillsByName,
            IReadOnlyCollection<string> allowedToolNames)
        {
            if (string.IsNullOrWhiteSpace(skillName))
            {
                return new
                {
                    success = false,
                    error = "skill_name is required.",
                    available = AvailableSkillNames(skillsByName, allowedToolNames)
                };
            }

            string trimmed = skillName.Trim();

            if (skillsByName.TryGetValue(trimmed, out SkillSet skill))
            {
                List<object> toolSchemas = new();
                foreach (SkillToolDescriptor descriptor in SkillSetToolResolver.BuildDescriptors(skill))
                {
                    if (!IsAllowed(descriptor.Name, allowedToolNames))
                    {
                        continue;
                    }

                    toolSchemas.Add(new
                    {
                        tool_name = descriptor.Name,
                        description = descriptor.Description,
                        parameters_schema = ParseSchemaOrRaw(descriptor.ParametersSchema),
                        invocable = descriptor.CanInvoke
                    });
                }

                if (toolSchemas.Count == 0)
                {
                    return new
                    {
                        success = false,
                        error = $"Skill '{trimmed}' is not available for the current tool allowlist.",
                        available = AvailableSkillNames(skillsByName, allowedToolNames)
                    };
                }

                return new
                {
                    success = true,
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
                        success = false,
                        error = $"Skill '{trimmed}' not found. Did you mean '{kvp.Key}'?",
                        available = AvailableSkillNames(skillsByName, allowedToolNames)
                    };
                }
            }

            return new
            {
                success = false,
                error = $"Skill '{trimmed}' not found.",
                available = AvailableSkillNames(skillsByName, allowedToolNames)
            };
        }

        /// <summary>
        /// Builds the same JSON payload <c>read_skill</c> returns for a single skill (name, instructions,
        /// tool schemas, usage), for host-side preloading of a skill into agent history without the agent
        /// having to call the tool. Unlike the interactive path this is not gated on the skill having
        /// callable tools — an instructions-only skill still yields a payload. Returns null for a null or
        /// unnamed skill.
        /// </summary>
        internal static string BuildSkillPayloadJson(SkillSet skill)
        {
            if (skill == null || string.IsNullOrWhiteSpace(skill.Name))
            {
                return null;
            }

            List<object> toolSchemas = new();
            foreach (SkillToolDescriptor descriptor in SkillSetToolResolver.BuildDescriptors(skill))
            {
                if (string.IsNullOrWhiteSpace(descriptor.Name))
                {
                    continue;
                }

                toolSchemas.Add(new
                {
                    tool_name = descriptor.Name,
                    description = descriptor.Description,
                    parameters_schema = ParseSchemaOrRaw(descriptor.ParametersSchema),
                    invocable = descriptor.CanInvoke
                });
            }

            return JsonConvert.SerializeObject(new
            {
                success = true,
                skill = skill.Name,
                instructions = skill.Instructions,
                tools = toolSchemas,
                usage = "Call call_skill_tool(tool_name, arguments_json) to use any tool listed above. " +
                        "arguments_json is a JSON object string with the parameter names and values."
            });
        }

        private static bool IsAllowed(string toolName, IReadOnlyCollection<string> allowedToolNames)
        {
            if (allowedToolNames == null || allowedToolNames.Count == 0)
            {
                return true;
            }

            foreach (string allowed in allowedToolNames)
            {
                if (string.Equals(allowed?.Trim(), toolName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static List<string> AvailableSkillNames(Dictionary<string, SkillSet> skillsByName,
            IReadOnlyCollection<string> allowedToolNames)
        {
            if (allowedToolNames == null || allowedToolNames.Count == 0)
            {
                return new List<string>(skillsByName.Keys);
            }

            List<string> names = new();
            foreach (KeyValuePair<string, SkillSet> kvp in skillsByName)
            {
                bool anyToolAllowed = SkillSetToolResolver.BuildDescriptors(kvp.Value)
                    .Any(d => IsAllowed(d.Name, allowedToolNames));
                if (anyToolAllowed)
                {
                    names.Add(kvp.Key);
                }
            }

            return names;
        }

        private static object ParseSchemaOrRaw(string schema)
        {
            if (string.IsNullOrWhiteSpace(schema))
            {
                return new JObject();
            }

            try
            {
                return JToken.Parse(schema);
            }
            catch
            {
                return schema;
            }
        }
    }
}
