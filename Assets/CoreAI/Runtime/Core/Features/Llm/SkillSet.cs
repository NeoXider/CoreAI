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
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Description = string.IsNullOrWhiteSpace(description) ? name : description;
            Instructions = instructions ?? "";
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
    }
}
