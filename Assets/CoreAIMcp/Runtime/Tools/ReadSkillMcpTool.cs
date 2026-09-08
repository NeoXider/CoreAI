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
                return "Read the complete entry document and reference index of an in-game skill; " +
                       "use section for one reference or all=true for every document. These are the same docs " +
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
            "\"description\":\"Skill name exactly as listed in the description (e.g. 'Lua Modding' or 'Rbx API').\"}," +
            "\"section\":{\"type\":\"string\",\"description\":\"Relative document path from the section index.\"}," +
            "\"all\":{\"type\":\"boolean\",\"description\":\"Read all documents. Cannot be combined with section.\"}}," +
            "\"required\":[\"name\"]}";

        /// <inheritdoc />
        public Task<McpToolResult> InvokeAsync(JObject arguments, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            JToken nameToken = arguments?["name"];
            JToken sectionToken = arguments?["section"];
            JToken allToken = arguments?["all"];
            if (nameToken?.Type != JTokenType.String ||
                (sectionToken != null && sectionToken.Type != JTokenType.String) ||
                (allToken != null && allToken.Type != JTokenType.Boolean))
            {
                return Task.FromResult(McpToolResult.Failure(Fail("name and section must be strings; all must be a boolean.")));
            }
            string name = nameToken.Value<string>();
            if (string.IsNullOrWhiteSpace(name))
            {
                return Task.FromResult(McpToolResult.Failure(Fail("read_skill: 'name' is required.")));
            }

            JObject content = JObject.Parse(ReadSkillLlmTool.ReadSkillJson(_skills, name,
                sectionToken?.Value<string>(), allToken?.Value<bool>() ?? false));
            bool success = content["success"]?.Value<bool>() == true;
            if (success)
            {
                // WHY: skill execution in an external client goes through the MCP composition, not the in-game proxy.
                content["usage"] = "Use section to read a referenced document or all=true to read the whole skill. " +
                    "Discover executable tools through MCP tools/list and coreai_tools; reading a skill grants no additional tools.";
            }
            string payload = content.ToString(Formatting.None);
            return Task.FromResult(success ? McpToolResult.Text(payload) : McpToolResult.Failure(payload));
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
