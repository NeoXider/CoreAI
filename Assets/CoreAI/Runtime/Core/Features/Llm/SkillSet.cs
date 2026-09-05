using System;
using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>
    /// Named group of related tools that an agent can load on demand through
    /// <c>read_skill</c> instead of exposing every tool schema on every request.
    /// </summary>
    /// <example>
    /// <code>
    /// var craftingSkill = new SkillSet("Crafting",
    ///     "Forge weapons, armor, and items from raw materials",
    ///     "1. Call get_recipes to see available recipes.\n" +
    ///     "2. Call check_inventory to verify materials.\n" +
    ///     "3. Call craft_item with recipe_id and quality.",
    ///     new DelegateLlmTool("get_recipes", "List recipes", (string type) => ...),
    ///     new DelegateLlmTool("craft_item", "Craft an item", (string id) => ...));
    /// var agent = new AgentBuilder("GameMaster")
    ///     .WithSkill(craftingSkill)
    ///     .WithSkill(combatSkill)
    ///     .Build();
    /// // Model sees: catalog with "Crafting" + "Combat" + read_skill tool.
    /// // Model decides what to read on its own.
    /// await orch.RunTaskAsync(new AiTaskRequest {
    ///     RoleId = "GameMaster",
    ///     Hint = "I want to craft an iron sword"
    /// });
    /// </code>
    /// </example>
    /// <summary>
    /// One addressable part of a skill's instructions: the name a reader asks for, and its body.
    /// </summary>
    public readonly struct SkillSection
    {
        public SkillSection(string name, string content)
        {
            Name = name ?? "";
            Content = content ?? "";
        }

        /// <summary>The name a reader passes to <c>read_skill</c> to fetch this part.</summary>
        public string Name { get; }

        /// <summary>The part's own text, without the joining heading.</summary>
        public string Content { get; }
    }

    public sealed class SkillSet
    {
        /// <summary>Human-readable name of this skill (e.g. "Quiz", "Crafting", "Combat").</summary>
        public string Name { get; }

        /// <summary>
        /// Short catalog description shown to the model before it decides whether to call
        /// <c>read_skill</c>.
        /// </summary>
        /// <example>"Forge weapons, armor, and items from raw materials"</example>
        public string Description { get; }

        /// <summary>
        /// Full procedural instructions returned by <c>read_skill</c> when this skill is selected.
        /// </summary>
        public string Instructions { get; }

        /// <summary>
        /// The named parts this skill's instructions were assembled from, in order. The FIRST is the
        /// entry document; the rest are references a reader fetches only when it needs them.
        /// A single-source skill has exactly one section and behaves as it always did.
        /// </summary>
        /// <remarks>
        /// WHY: this is the third disclosure level, and it exists because the reader has no file
        /// system. Claude Code gets away with plain `[foo](references/foo.md)` links because its agent
        /// can open the file itself; a CoreAI agent reaches a skill only through <c>read_skill</c>, so
        /// an unfetchable link would just be dead text. Keeping the parts addressable lets
        /// <c>read_skill</c> hand back the entry document plus an index, and fetch one section on a
        /// second call — the same staged cost, through the door this reader actually has.
        /// </remarks>
        public IReadOnlyList<SkillSection> Sections { get; }

        /// <summary>Tools that belong to this skill.</summary>
        public IReadOnlyList<ILlmTool> Tools { get; }

        /// <summary>
        /// Stable names of tools available through this skill's <c>call_skill_tool</c> proxy.
        /// </summary>
        public string[] ToolNames { get; }

        /// <summary>
        /// Creates a skill with name, description, full instructions, and tools.
        /// </summary>
        /// <param name="name">Human-readable skill name.</param>
        /// <param name="description">
        /// Short one-line description for the skill catalog.
        /// If null/empty, the name is used as description.
        /// </param>
        /// <param name="instructions">
        /// Full instructions loaded on demand via <c>read_skill</c>.
        /// Null or empty means the tool descriptions are sufficient.
        /// </param>
        /// <param name="tools">Tools that belong to this skill.</param>
        public SkillSet(string name, string description, string instructions, params ILlmTool[] tools)
            : this(null, name, description, instructions, tools)
        {
        }

        /// <summary>
        /// Creates a skill whose instructions were assembled from named parts, keeping those parts
        /// addressable so <c>read_skill</c> can hand back one at a time.
        /// </summary>
        private SkillSet(IReadOnlyList<SkillSection> sections, string name, string description,
            string instructions, params ILlmTool[] tools)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Description = string.IsNullOrWhiteSpace(description) ? name : description;
            Instructions = instructions ?? "";
            Sections = sections ?? new[] { new SkillSection(name, Instructions) };
            tools ??= Array.Empty<ILlmTool>();

            List<ILlmTool> toolList = new(tools.Length);
            foreach (ILlmTool tool in tools)
            {
                if (tool == null)
                {
                    continue;
                }

                toolList.Add(tool);
            }

            Tools = toolList;
            ToolNames = SkillSetToolResolver.BuildToolNames(toolList);
        }

        /// <summary>
        /// Creates a skill without detailed instructions (tool descriptions are sufficient).
        /// </summary>
        public SkillSet(string name, string description, params ILlmTool[] tools)
            : this(name, description, null, tools)
        {
        }

        /// <summary>
        /// Creates a skill from an enumerable of tools.
        /// </summary>
        public SkillSet(string name, string description, string instructions, IEnumerable<ILlmTool> tools)
            : this(name, description, instructions, ToArray(tools))
        {
        }

        /// <summary>
        /// Merges the <see cref="ToolNames"/> of multiple skills into a single allowlist.
        /// Useful when a request should restrict to specific skills via <see cref="AiTaskRequest.AllowedToolNames"/>.
        /// </summary>
        public static string[] MergeToolNames(params SkillSet[] skills)
        {
            if (skills == null || skills.Length == 0)
            {
                return Array.Empty<string>();
            }

            List<string> merged = new();
            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach (SkillSet skill in skills)
            {
                if (skill?.ToolNames == null)
                {
                    continue;
                }

                foreach (string name in skill.ToolNames)
                {
                    if (seen.Add(name))
                    {
                        merged.Add(name);
                    }
                }
            }

            return merged.ToArray();
        }

        /// <summary>
        /// Builds a lightweight skill catalog for the system prompt.
        /// The model uses <c>read_skill(name)</c> to load full instructions on demand.
        /// </summary>
        public static string BuildCatalog(IReadOnlyList<SkillSet> skills)
        {
            if (skills == null || skills.Count == 0)
            {
                return "";
            }

            System.Text.StringBuilder sb = new();
            sb.AppendLine("## Available Skills");
            sb.AppendLine("Call `read_skill(skill_name)` to see full instructions and available tools.");
            sb.AppendLine("Then call `call_skill_tool(tool_name, arguments_json)` to use a tool.");
            sb.AppendLine();

            foreach (SkillSet skill in skills)
            {
                if (skill == null)
                {
                    continue;
                }

                sb.Append("- **").Append(skill.Name).Append("** - ").Append(skill.Description);
                sb.AppendLine();
            }

            return sb.ToString().TrimEnd();
        }

        private static ILlmTool[] ToArray(IEnumerable<ILlmTool> tools)
        {
            if (tools == null)
            {
                throw new ArgumentNullException(nameof(tools));
            }

            List<ILlmTool> list = new();
            foreach (ILlmTool t in tools)
            {
                list.Add(t);
            }

            return list.ToArray();
        }

        /// <summary>
        /// Creates a skill set by loading instructions from a text file at runtime.
        /// </summary>
        /// <param name="name">Skill name.</param>
        /// <param name="description">Short one-line description for catalog.</param>
        /// <param name="instructionsFilePath">
        /// Path to a <c>.txt</c> / <c>.md</c> file containing full skill instructions.
        /// </param>
        /// <param name="tools">Tools that belong to this skill.</param>
        public static SkillSet FromFile(string name, string description,
            string instructionsFilePath, params ILlmTool[] tools)
        {
            if (string.IsNullOrWhiteSpace(instructionsFilePath))
            {
                throw new ArgumentException("File path must not be empty.", nameof(instructionsFilePath));
            }

            string instructions = System.IO.File.ReadAllText(instructionsFilePath);
            return new SkillSet(name, description, instructions, tools);
        }

        /// <summary>
        /// Creates a skill set from pre-loaded text content (e.g. Unity <c>TextAsset.text</c>,
        /// embedded resource, or any string source).
        /// </summary>
        /// <param name="name">Skill name.</param>
        /// <param name="description">Short one-line description for catalog.</param>
        /// <param name="instructionsContent">Pre-loaded instructions text.</param>
        /// <param name="tools">Tools that belong to this skill.</param>
        public static SkillSet FromTextContent(string name, string description,
            string instructionsContent, params ILlmTool[] tools)
        {
            return new SkillSet(name, description, instructionsContent, tools);
        }

        /// <summary>
        /// Creates a skill set from several named instruction parts (e.g. Unity <c>TextAsset</c>
        /// name/text pairs where there is no file system). Parts join in order, each under a
        /// <c>## partName</c> heading; empty parts are skipped.
        /// </summary>
        /// <param name="name">Skill name.</param>
        /// <param name="description">Short one-line description for catalog.</param>
        /// <param name="namedParts">Ordered (partName, content) pairs.</param>
        /// <param name="tools">Tools that belong to this skill.</param>
        public static SkillSet FromTextParts(string name, string description,
            IEnumerable<KeyValuePair<string, string>> namedParts, params ILlmTool[] tools)
        {
            if (namedParts == null)
            {
                throw new ArgumentNullException(nameof(namedParts));
            }

            List<KeyValuePair<string, string>> parts = new(namedParts);
            if (parts.Count == 0)
            {
                throw new ArgumentException("At least one instruction part is required.", nameof(namedParts));
            }

            for (int i = 0; i < parts.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(parts[i].Key))
                {
                    throw new ArgumentException($"Part name at index {i} must not be empty.", nameof(namedParts));
                }
            }

            List<SkillSection> sections = new(parts.Count);
            foreach (KeyValuePair<string, string> part in parts)
            {
                // WHY: an empty part is skipped by the joiner, so keeping it here would advertise a
                // section index entry that fetches nothing.
                if (!string.IsNullOrWhiteSpace(part.Value))
                {
                    sections.Add(new SkillSection(part.Key, part.Value));
                }
            }

            return new SkillSet(sections.Count > 0 ? sections : null, name, description,
                JoinInstructionParts(parts), tools);
        }

        /// <summary>
        /// Finds one instruction section by name, case-insensitively. False when this skill has no
        /// such part.
        /// </summary>
        public bool TryGetSection(string sectionName, out SkillSection section)
        {
            if (!string.IsNullOrWhiteSpace(sectionName) && Sections != null)
            {
                string wanted = sectionName.Trim();
                foreach (SkillSection candidate in Sections)
                {
                    if (string.Equals(candidate.Name, wanted, StringComparison.OrdinalIgnoreCase))
                    {
                        section = candidate;
                        return true;
                    }
                }
            }

            section = default;
            return false;
        }

        /// <summary>
        /// Joins named instruction parts into one <c>read_skill</c> body: every non-empty part under a
        /// <c>## partName</c> heading, in order, separated by a blank line.
        /// </summary>
        /// <remarks>
        /// WHY: exposed so a host that assembles a multi-part skill outside this class — the Unity
        /// <c>SkillSetAsset</c> joining several TextAssets, for one — produces a body identical to
        /// <see cref="FromTextParts"/>. Two joining rules would drift, and the model would then read a
        /// different document depending on which door the same skill came through.
        /// </remarks>
        public static string JoinInstructionParts(IEnumerable<KeyValuePair<string, string>> namedParts)
        {
            if (namedParts == null)
            {
                throw new ArgumentNullException(nameof(namedParts));
            }

            List<string> blocks = new();
            foreach (KeyValuePair<string, string> part in namedParts)
            {
                if (string.IsNullOrWhiteSpace(part.Value))
                {
                    continue;
                }

                blocks.Add("## " + part.Key + "\n" + part.Value);
            }

            return string.Join("\n\n", blocks);
        }

        /// <summary>
        /// Creates a skill set by loading instructions from several text files at runtime.
        /// Files read in the given order; each file becomes a <c>## filename</c> section.
        /// </summary>
        /// <param name="name">Skill name.</param>
        /// <param name="description">Short one-line description for catalog.</param>
        /// <param name="instructionFilePaths">Ordered paths to instruction files.</param>
        /// <param name="tools">Tools that belong to this skill.</param>
        public static SkillSet FromFiles(string name, string description,
            IEnumerable<string> instructionFilePaths, params ILlmTool[] tools)
        {
            if (instructionFilePaths == null)
            {
                throw new ArgumentNullException(nameof(instructionFilePaths));
            }

            List<string> paths = new(instructionFilePaths);
            if (paths.Count == 0)
            {
                throw new ArgumentException("At least one instruction file is required.", nameof(instructionFilePaths));
            }

            for (int i = 0; i < paths.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(paths[i]))
                {
                    throw new ArgumentException($"File path at index {i} must not be empty.", nameof(instructionFilePaths));
                }
            }

            // WHY: Read here and delegate to FromTextParts so the joining rule lives in one place.
            List<KeyValuePair<string, string>> parts = new(paths.Count);
            foreach (string path in paths)
            {
                string content = System.IO.File.ReadAllText(path);
                parts.Add(new KeyValuePair<string, string>(System.IO.Path.GetFileName(path), content));
            }

            return FromTextParts(name, description, parts, tools);
        }
    }
}
