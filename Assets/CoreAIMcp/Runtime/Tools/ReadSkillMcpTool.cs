using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Mcp.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CoreAI.Mcp.Tools
{
    /// <summary>
    /// MCP <c>read_skill</c> tool: returns the FULL instruction text of a skill registered for the
    /// in-game Programmer role - the very same reference (e.g. "Lua Modding", "Rbx API") the on-board
    /// agent reads through its own <c>read_skill</c> catalog. One source of truth: an external agent
    /// (Claude Code, Codex, ...) pulls the exact Lua/Rbx API docs the game ships, so nothing is
    /// duplicated. Read-only; no game mutation.
    /// </summary>
    public sealed class ReadSkillMcpTool : IMcpTool
    {
        private readonly IReadOnlyList<SkillSet> _skills;

        /// <param name="skills">The role's skill catalog snapshot (from AgentMemoryPolicy).</param>
        public ReadSkillMcpTool(IReadOnlyList<SkillSet> skills)
        {
            _skills = skills ?? throw new ArgumentNullException(nameof(skills));
        }

        /// <inheritdoc />
        public string Name => "read_skill";

        /// <inheritdoc />
        public string Description
        {
            get
            {
                string names = AvailableNamesText();
                return "Read the full API reference text for a registered in-game skill - the same docs " +
                       "the on-board agent uses. Call this BEFORE execute_lua or manage_mods so you know " +
                       "the exact globals, hooks, and datatypes the running game exposes. " +
                       (string.IsNullOrEmpty(names)
                           ? "No skills are registered in this composition."
                           : $"Available skills: {names}.");
            }
        }

        /// <inheritdoc />
        public string InputSchemaJson =>
            "{\"type\":\"object\"," +
            "\"properties\":{\"name\":{\"type\":\"string\"," +
            "\"description\":\"Skill name exactly as listed in the description (e.g. 'Lua Modding' or 'Rbx API').\"}}," +
            "\"required\":[\"name\"]}";

        /// <inheritdoc />
        public Task<McpToolResult> InvokeAsync(JObject arguments, CancellationToken cancellationToken)
        {
            string name = arguments?["name"]?.ToString();
            if (string.IsNullOrWhiteSpace(name))
            {
                return Task.FromResult(McpToolResult.Failure(Fail("read_skill: 'name' is required.")));
            }

            string trimmed = name.Trim();
            foreach (SkillSet skill in _skills)
            {
                if (skill != null && string.Equals(skill.Name, trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    string instructions = skill.Instructions ?? "";
                    string payload = JsonConvert.SerializeObject(new
                    {
                        success = true,
                        skill = skill.Name,
                        instructions
                    });
                    return Task.FromResult(new McpToolResult(new[] { McpContent.CreateText(payload) }));
                }
            }

            return Task.FromResult(McpToolResult.Failure(Fail(
                $"read_skill: skill '{trimmed}' not found. Available: {AvailableNamesText()}.")));
        }

        private string AvailableNamesText()
        {
            List<string> names = new();
            foreach (SkillSet skill in _skills)
            {
                if (skill != null && !string.IsNullOrWhiteSpace(skill.Name))
                {
                    names.Add(skill.Name);
                }
            }

            return names.Count == 0 ? "" : string.Join(", ", names);
        }

        private string Fail(string message)
        {
            return JsonConvert.SerializeObject(new
            {
                success = false,
                error = message,
                available = AvailableNamesText()
            });
        }
    }
}
