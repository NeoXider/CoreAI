using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Microsoft.Extensions.AI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CoreAI.Ai
{
    /// <summary>
    /// LLM tool that reads skill instructions and metadata. Public so hosts and installers can
    /// attach a read_skill catalog to built-in roles that are not assembled via AgentBuilder.
    /// </summary>
    public static class ReadSkillLlmTool
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
            // When the backing list is a live MutableSkillCatalog (skill authoring), the lookup is
            // rebuilt per call so a skill the model just created/updated is immediately visible here.
            private readonly bool _isLive;
            private readonly Dictionary<string, SkillSet> _skillsByName;
            private readonly IReadOnlyCollection<string> _allowedToolNames;
            private readonly HashSet<string> _skillToolNames;

            public ReadSkillProxy(IReadOnlyList<SkillSet> skills, IReadOnlyCollection<string> allowedToolNames)
            {
                _skills = skills ?? throw new ArgumentNullException(nameof(skills));
                _isLive = skills is MutableSkillCatalog;
                _allowedToolNames = allowedToolNames;
                _skillsByName = new Dictionary<string, SkillSet>(StringComparer.OrdinalIgnoreCase);
                _skillToolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                if (!_isLive)
                {
                    IndexSkills(_skills, _skillsByName, _skillToolNames);
                }
            }

            private static void IndexSkills(IReadOnlyList<SkillSet> skills,
                Dictionary<string, SkillSet> skillsByName, HashSet<string> skillToolNames)
            {
                foreach (SkillSet skill in skills)
                {
                    if (skill != null && !string.IsNullOrWhiteSpace(skill.Name))
                    {
                        skillsByName[skill.Name] = skill;
                    }

                    foreach (SkillToolDescriptor descriptor in SkillSetToolResolver.BuildDescriptors(skill))
                    {
                        if (!string.IsNullOrWhiteSpace(descriptor.Name))
                        {
                            skillToolNames.Add(descriptor.Name);
                        }
                    }
                }
            }

            private Dictionary<string, SkillSet> ResolveSkillsByName()
            {
                if (!_isLive)
                {
                    return _skillsByName;
                }

                Dictionary<string, SkillSet> map = new(StringComparer.OrdinalIgnoreCase);
                HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
                IndexSkills(_skills, map, names);
                return map;
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
                if (string.IsNullOrWhiteSpace(toolName))
                {
                    return false;
                }

                if (!_isLive)
                {
                    return _skillToolNames.Contains(toolName.Trim());
                }

                string trimmed = toolName.Trim();
                foreach (SkillToolDescriptor descriptor in SkillSetToolResolver.BuildDescriptors(_skills))
                {
                    if (string.Equals(descriptor.Name, trimmed, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                return false;
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

            private string Execute(
                [Description("Skill name exactly as listed in the catalog.")]
                string skill_name)
            {
                return ReadSkillLlmTool.Execute(skill_name, ResolveSkillsByName(), _allowedToolNames);
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

                // A skill that declares tools but has them all filtered out by the allowlist is genuinely
                // unavailable. An instructions-only skill (no tools at all — common for agent-authored
                // skills) is still readable: return its instructions with an empty tool list.
                bool declaresTools = skill.Tools != null && skill.Tools.Count > 0;
                if (toolSchemas.Count == 0 && declaresTools)
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
                // Instructions-only skills (no tools) are always listable; tool-bearing skills are listed
                // only when at least one of their tools survives the allowlist.
                bool declaresTools = kvp.Value?.Tools != null && kvp.Value.Tools.Count > 0;
                bool anyToolAllowed = !declaresTools || SkillSetToolResolver.BuildDescriptors(kvp.Value)
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
