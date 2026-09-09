using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
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

        /// <summary>Shared read contract for model tools and host adapters: complete entry, one document, or all.</summary>
        public static string ReadSkillJson(IReadOnlyList<SkillSet> skills, string skillName,
            string section = null, bool all = false, IReadOnlyCollection<string> allowedToolNames = null)
        {
            if (skills == null) throw new ArgumentNullException(nameof(skills));
            List<SkillSet> snapshot = new(skills);
            SkillSetToolResolver.ValidateCatalog(snapshot);
            Dictionary<string, SkillSet> byName = new(StringComparer.OrdinalIgnoreCase);
            foreach (SkillSet skill in snapshot)
            {
                if (skill != null) byName.Add(skill.Name, skill);
            }
            return Execute(skillName, section, all, byName, allowedToolNames);
        }

        private sealed class ReadSkillProxy : LlmToolBase, IAIFunctionLlmTool, ISkillSetMetaLlmTool
        {
            private readonly IReadOnlyList<SkillSet> _skills;

            // WHY: When the backing list is a live MutableSkillCatalog (skill authoring), the index follows
            // the catalog so a skill the model just created/updated is immediately visible here. It is
            // rebuilt only when the catalog's version moves: indexing creates every skill tool's MEAI
            // function by reflection, and doing that per call made every read_skill and every per-request
            // allowlist probe pay the cost of registering the whole catalog again.
            private readonly MutableSkillCatalog _liveCatalog;
            private readonly object _liveIndexGate = new();
            private readonly SkillIndex _staticIndex;
            private readonly IReadOnlyCollection<string> _allowedToolNames;
            private SkillIndex _liveIndex;
            private string _parametersSchema;

            private sealed class SkillIndex
            {
                public SkillIndex(long version)
                {
                    Version = version;
                }

                public long Version { get; }
                public Dictionary<string, SkillSet> SkillsByName { get; } = new(StringComparer.OrdinalIgnoreCase);
                public HashSet<string> ToolNames { get; } = new(StringComparer.OrdinalIgnoreCase);
            }

            public ReadSkillProxy(IReadOnlyList<SkillSet> skills, IReadOnlyCollection<string> allowedToolNames)
            {
                if (skills == null) throw new ArgumentNullException(nameof(skills));
                _liveCatalog = skills as MutableSkillCatalog;
                _skills = _liveCatalog ?? (IReadOnlyList<SkillSet>)new List<SkillSet>(skills).AsReadOnly();
                SkillSetToolResolver.ValidateCatalog(_skills);
                _allowedToolNames = allowedToolNames == null ? null : new List<string>(allowedToolNames).AsReadOnly();
                _staticIndex = _liveCatalog != null ? null : IndexSkills(_skills, 0);
            }

            private static SkillIndex IndexSkills(IReadOnlyList<SkillSet> skills, long version)
            {
                SkillIndex index = new(version);
                foreach (SkillSet skill in skills)
                {
                    if (skill != null && !string.IsNullOrWhiteSpace(skill.Name))
                    {
                        index.SkillsByName[skill.Name] = skill;
                    }

                    foreach (SkillToolDescriptor descriptor in SkillSetToolResolver.BuildDescriptors(skill))
                    {
                        if (!string.IsNullOrWhiteSpace(descriptor.Name))
                        {
                            index.ToolNames.Add(descriptor.Name);
                        }
                    }
                }

                return index;
            }

            private SkillIndex ResolveIndex()
            {
                if (_liveCatalog == null)
                {
                    return _staticIndex;
                }

                SkillIndex cached = Volatile.Read(ref _liveIndex);
                if (cached != null && cached.Version == _liveCatalog.Version)
                {
                    return cached;
                }

                lock (_liveIndexGate)
                {
                    cached = _liveIndex;
                    // WHY the version is read BEFORE indexing: a catalog change that lands during the
                    // build leaves the index tagged with the older version, so the next call rebuilds.
                    long version = _liveCatalog.Version;
                    if (cached != null && cached.Version == version)
                    {
                        return cached;
                    }

                    SkillIndex index = IndexSkills(_skills, version);
                    Volatile.Write(ref _liveIndex, index);
                    return index;
                }
            }

            private Dictionary<string, SkillSet> ResolveSkillsByName()
            {
                return ResolveIndex().SkillsByName;
            }

            public override string Name => "read_skill";

            public override string Description =>
                "Read the full instructions and tool list for a skill. Call this BEFORE using " +
                "call_skill_tool so you know which tools are available and what parameters they need. " +
                "Pass the skill name exactly as listed in the catalog. The default is the complete entry document " +
                "and a reference index; use section for one reference or all=true for every document.";

            // WHY cached: the schema is derived by reflection from a fixed delegate and never changes, yet
            // this property is read several times per request (token budgeting, logging, the text tool
            // contract), and each read used to create a whole MEAI function and serialize its schema.
            public override string ParametersSchema =>
                _parametersSchema ??= CreateAIFunction().JsonSchema.GetRawText();

            public override bool AllowDuplicates => true;

            public bool ContainsSkillTool(string toolName)
            {
                return !string.IsNullOrWhiteSpace(toolName) && ResolveIndex().ToolNames.Contains(toolName.Trim());
            }

            public ILlmTool RestrictTo(IReadOnlyCollection<string> allowedToolNames)
            {
                return new ReadSkillProxy(_skills,
                    SkillSetToolResolver.IntersectAllowlist(_allowedToolNames, allowedToolNames));
            }

            public AIFunction CreateAIFunction()
            {
                return AIFunctionFactory.Create(
                    (Func<string, string, bool, CancellationToken, string>)Execute,
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
                string section = null,
                [Description("Read every document in order. Cannot be combined with section.")]
                bool all = false,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ReadSkillLlmTool.Execute(skill_name, section, all, ResolveSkillsByName(),
                    _allowedToolNames);
            }
        }

        private static string Execute(string skillName, string sectionName, bool all,
            Dictionary<string, SkillSet> skillsByName,
            IReadOnlyCollection<string> allowedToolNames)
        {
            return JsonConvert.SerializeObject(
                ExecuteObject(skillName, sectionName, all, skillsByName, allowedToolNames));
        }

        private static object ExecuteObject(string skillName, string sectionName, bool all,
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

                return BuildSkillResult(skill, sectionName, all, toolSchemas);
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

            return ReadSkillJson(new[] { skill }, skill.Name, all: true);
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
        private static object BuildSkillResult(SkillSet skill, string sectionName, bool all,
            List<object> toolSchemas)
        {
            const string ToolUsage =
                "Call call_skill_tool(tool_name, arguments_json) to use any tool listed above. " +
                "arguments_json is a JSON object string with the parameter names and values.";

            IReadOnlyList<SkillSection> sections = skill.Sections;
            bool staged = sections != null && sections.Count > 1;

            if (all && !string.IsNullOrWhiteSpace(sectionName))
            {
                return new { success = false, skill = skill.Name, error = "Choose either section or all, not both." };
            }

            if (all)
            {
                return new
                {
                    success = true,
                    skill = skill.Name,
                    instructions = skill.Instructions,
                    sections = SectionNames(sections, 0),
                    tools = toolSchemas,
                    usage = ToolUsage
                };
            }

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
                        "for one of them only when you need it, or read_skill(skill_name, all=true) for all documents."
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
            if (allowedToolNames == null)
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
            if (allowedToolNames == null)
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
