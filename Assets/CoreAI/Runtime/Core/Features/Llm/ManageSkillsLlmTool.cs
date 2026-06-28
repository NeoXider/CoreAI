using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CoreAI.Ai
{
    /// <summary>
    /// LLM tool (<c>manage_skills</c>) that lets an agent author, persist, refine, and reuse its own
    /// skills. A skill bundles procedural <c>instructions</c> with an allowlist of <b>existing</b>
    /// registered tool names; once created it appears in the same agent's <c>read_skill</c> catalog so
    /// the model can immediately reuse what it just wrote. Mirrors the <c>manage_mods</c> action-dispatch
    /// and success/failure JSON shape.
    /// <para>
    /// Actions: <c>create</c>, <c>update</c>, <c>list</c>, <c>get</c>, <c>delete</c>. Each create/update is
    /// persisted via <see cref="ISkillStore"/> and recorded as a new revision (auditable, auto-incrementing
    /// version). The model cannot invent C# tools — a skill may only reference tools already registered for
    /// the role.
    /// </para>
    /// </summary>
    public sealed class ManageSkillsLlmTool : LlmToolBase, IAIFunctionLlmTool
    {
        private readonly SkillAuthoringCoordinator _coordinator;

        /// <param name="coordinator">Authoring brain that persists, versions, and surfaces skills.</param>
        public ManageSkillsLlmTool(SkillAuthoringCoordinator coordinator)
        {
            _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        }

        /// <inheritdoc />
        public override string Name => "manage_skills";

        /// <inheritdoc />
        public override bool AllowDuplicates => true;

        /// <inheritdoc />
        public override string Description =>
            "Author and reuse your own skills. A skill bundles step-by-step instructions with an allowlist " +
            "of EXISTING tool names (you cannot invent new tools; only reference tools already available to " +
            "you). After create/update the skill appears in your skill catalog - call read_skill(name) to " +
            "load it and call_skill_tool to use its tools. " +
            "Actions: create (name, description, instructions, tool_names[]), " +
            "update (revise description/instructions/tool_names of an existing skill; bumps its version), " +
            "list (all skills with versions), get (read one skill's full definition), delete (remove a skill). " +
            "Each create/update records a new auditable revision; the original is version 0.";

        /// <inheritdoc />
        public override string ParametersSchema => JsonParams(
            ("action", "string", true, "One of: create, update, list, get, delete"),
            ("name", "string", false, "Skill name/id (required for create, update, get, delete)"),
            ("description", "string", false, "Short one-line catalog description (create/update)"),
            ("instructions", "string", false, "Full step-by-step instructions returned by read_skill (create/update)"),
            ("tool_names", "string", false,
                "JSON array (or comma-separated string) of EXISTING tool names this skill exposes via call_skill_tool")
        );

        /// <summary>Creates the MEAI function surface for <c>manage_skills</c>.</summary>
        public AIFunction CreateAIFunction()
        {
            Func<string, string, string, string, string, CancellationToken, Task<string>> func = ExecuteAsync;
            AIFunctionFactoryOptions options = new()
            {
                Name = Name,
                Description = Description
            };
            return AIFunctionFactory.Create(func, options);
        }

        /// <summary>Executes a skill-management action and returns a JSON result for the model.</summary>
        public Task<string> ExecuteAsync(
            string action,
            string name = null,
            string description = null,
            string instructions = null,
            string tool_names = null,
            CancellationToken cancellationToken = default)
        {
            string normalized = (action ?? "").Trim().ToLowerInvariant();
            string result;
            try
            {
                result = normalized switch
                {
                    "create" => Create(name, description, instructions, tool_names),
                    "update" => Update(name, description, instructions, tool_names),
                    "list" => ListSkills(),
                    "get" => GetSkill(name),
                    "delete" => Delete(name),
                    _ => Fail($"Unknown action '{normalized}'. Valid: create, update, list, get, delete.")
                };
            }
            catch (Exception ex)
            {
                result = Fail($"manage_skills '{normalized}' failed: {ex.Message}");
            }

            return Task.FromResult(result);
        }

        private string Create(string name, string description, string instructions, string toolNames)
        {
            SkillAuthoringResult r = _coordinator.Create(name, description, instructions, ParseToolNames(toolNames));
            return FromResult(r);
        }

        private string Update(string name, string description, string instructions, string toolNames)
        {
            // For update, only a supplied tool_names replaces the allowlist; null leaves it unchanged.
            List<string> parsed = toolNames == null ? null : ParseToolNames(toolNames);
            SkillAuthoringResult r = _coordinator.Update(name, description, instructions, parsed);
            return FromResult(r);
        }

        private string ListSkills()
        {
            IReadOnlyList<SkillRecord> skills = _coordinator.ListSkills();
            List<object> items = new(skills.Count);
            foreach (SkillRecord s in skills)
            {
                items.Add(new
                {
                    name = s.Id,
                    description = s.Description,
                    version = s.Version,
                    tool_names = s.ToolNames
                });
            }

            return Ok($"{items.Count} skill(s).", items);
        }

        private string GetSkill(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return Fail("get: 'name' is required.");
            }

            SkillRecord record = _coordinator.GetSkill(name);
            if (record == null)
            {
                return Fail($"get: skill '{name.Trim()}' not found.");
            }

            IReadOnlyList<LuaScriptRevision> revisions = _coordinator.ListRevisions(record.Id);
            return Ok($"Skill '{record.Id}' (version {record.Version}).", new
            {
                name = record.Id,
                description = record.Description,
                instructions = record.Instructions,
                tool_names = record.ToolNames,
                version = record.Version,
                revision_count = revisions.Count
            });
        }

        private string Delete(string name)
        {
            SkillAuthoringResult r = _coordinator.Delete(name);
            return FromResult(r);
        }

        private static List<string> ParseToolNames(string raw)
        {
            List<string> names = new();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return names;
            }

            string trimmed = raw.Trim();
            if (trimmed.StartsWith("["))
            {
                try
                {
                    JArray array = JArray.Parse(trimmed);
                    foreach (JToken token in array)
                    {
                        string value = token?.ToString();
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            names.Add(value.Trim());
                        }
                    }

                    return names;
                }
                catch
                {
                    // Fall through to comma-separated parsing.
                }
            }

            foreach (string part in trimmed.Split(','))
            {
                if (!string.IsNullOrWhiteSpace(part))
                {
                    names.Add(part.Trim());
                }
            }

            return names;
        }

        private static string FromResult(SkillAuthoringResult r)
        {
            return r.Success
                ? Ok(r.Message, r.Record == null
                    ? null
                    : new { name = r.Record.Id, version = r.Record.Version })
                : Fail(r.Message);
        }

        private static string Ok(string message, object data = null)
        {
            return JsonConvert.SerializeObject(new { success = true, message, data });
        }

        private static string Fail(string message)
        {
            return JsonConvert.SerializeObject(new { success = false, message });
        }
    }
}
