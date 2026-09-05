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

            // WHY: When the backing list is a live MutableSkillCatalog (skill authoring), the lookup is
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
                "{\"type\":\"object\",\"properties\":{\"skill_name\":{\"type\":\"string\",\"description\":\"Skill name exactly as listed in the catalog.\"},\"section\":{\"type\":\"string\",\"description\":\"Optional. One section name from the sections index of a previous read_skill call. Omit it to get the entry document plus that index.\"}},\"required\":[\"skill_name\"]}";

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
                    (Func<string, string, string>)Execute,
                    new AIFunctionFactoryOptions
                    {
                        Name = Name,
                        Description = Description
                    });
            }

            private string Execute(
                [Description("Skill name exactly as listed in the catalog.")]
                string skill_name,
                [Description("Optional section name from a previous read_skill call's sections index.")]
                string section = null)
            {
                return ReadSkillLlmTool.Execute(skill_name, section, ResolveSkillsByName(),
                    _allowedToolNames);
            }
        }

        private static string Execute(string skillName, string sectionName,
            Dictionary<string, SkillSet> skillsByName,
            IReadOnlyCollection<string> allowedToolNames)
        {
            return JsonConvert.SerializeObject(
                ExecuteObject(skillName, sectionName, skillsByName, allowedToolNames));
        }

        private static object ExecuteObject(string skillName, string sectionName,
            Dictionary<string, SkillSet> skillsByName,
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

                // WHY: A skill that declares tools but has them all filtered out by the allowlist is genuinely
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

                return BuildSkillResult(skill, sectionName, toolSchemas);
            }

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
        /// <para>
        /// WHY this path is deliberately NOT staged like the interactive one: preloading is the host
        /// putting a skill into history on purpose, so it wants the whole document. Handing back an
        /// entry page plus an index nobody asked the agent to follow would leave a preloaded skill
        /// permanently half-loaded.
        /// </para>
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


        /// <summary>
        /// Builds the read_skill payload, staged: the entry document plus a section index when the
        /// skill has several parts, or one named section when the caller asks for it.
        /// </summary>
        /// <remarks>
        /// WHY: a skill assembled from five documents used to arrive as one blob, so a reader paid for
        /// all of it to use any of it. A single-part skill is returned exactly as it always was — the
        /// staging must not change what an existing skill looks like.
        /// </remarks>
        private static object BuildSkillResult(SkillSet skill, string sectionName,
            List<object> toolSchemas)
        {
            const string ToolUsage =
                "Call call_skill_tool(tool_name, arguments_json) to use any tool listed above. " +
                "arguments_json is a JSON object string with the parameter names and values.";

            IReadOnlyList<SkillSection> sections = skill.Sections;
            bool staged = sections != null && sections.Count > 1;

            if (!string.IsNullOrWhiteSpace(sectionName))
            {
                if (skill.TryGetSection(sectionName, out SkillSection wanted))
                {
                    return new
                    {
                        success = true,
                        skill = skill.Name,
                        section = wanted.Name,
                        instructions = wanted.Content,
                        tools = toolSchemas,
                        usage = ToolUsage
                    };
                }

                return new
                {
                    success = false,
                    skill = skill.Name,
                    error = $"Skill '{skill.Name}' has no section '{sectionName.Trim()}'.",
                    sections = SectionNames(sections, 0)
                };
            }

            if (!staged)
            {
                return new
                {
                    success = true,
                    skill = skill.Name,
                    instructions = skill.Instructions,
                    tools = toolSchemas,
                    usage = ToolUsage
                };
            }

            return new
            {
                success = true,
                skill = skill.Name,
                section = sections[0].Name,
                instructions = sections[0].Content,
                sections = SectionNames(sections, 1),
                tools = toolSchemas,
                usage = ToolUsage +
                        " This skill is written across several documents: the text above is its entry " +
                        "document, and `sections` lists the rest. Call read_skill(skill_name, section) " +
                        "for one of them only when you need it."
            };
        }

        /// <summary>Section names from <paramref name="startIndex"/> onward, for the index a reader picks from.</summary>
        private static string[] SectionNames(IReadOnlyList<SkillSection> sections, int startIndex)
        {
            if (sections == null || sections.Count <= startIndex)
            {
                return Array.Empty<string>();
            }

            string[] names = new string[sections.Count - startIndex];
            for (int i = startIndex; i < sections.Count; i++)
            {
                names[i - startIndex] = sections[i].Name;
            }

            return names;
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
                // WHY: Instructions-only skills (no tools) are always listable; tool-bearing skills are listed
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
