using System;
using System.Collections.Generic;
using System.ComponentModel;
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
            "update (revise description/main instructions/tool_names; preserves reference documents and bumps version), " +
            "list (all skills with versions), get (read one skill's full definition), delete (remove a skill). " +
            "Each create/update records a new auditable revision; the original is version 0.";

        /// <inheritdoc />
        public override string ParametersSchema => JsonParams(
            ("action", "string", true, "One of: create, update, list, get, delete"),
            ("name", "string", false, "Skill name/id (required for create, update, get, delete)"),
            ("description", "string", false, "Short one-line catalog description (create/update)"),
            ("instructions", "string", false, "Full main-document instructions; update replaces only the main document and preserves references; omit to keep unchanged"),
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
            [Description("One of: create, update, list, get, delete")]
            string action,
            [Description("Skill name/id (required for create, update, get, delete)")]
            string name = null,
            [Description("Short one-line catalog description (create/update)")]
            string description = null,
            [Description("Full main-document instructions; update replaces only the main document and preserves references; omit to keep unchanged")]
            string instructions = null,
            [Description(
                "JSON array (or comma-separated string) of EXISTING tool names this skill exposes via call_skill_tool")]
            string tool_names = null,
            CancellationToken cancellationToken = default)
        {
            return MeaiToolTaskBridge.Publish(ExecuteCoreAsync(action, name, description, instructions, tool_names, cancellationToken));
        }

        private async Task<string> ExecuteCoreAsync(string action, string name, string description, string instructions,
            string toolNames, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string normalized = (action ?? "").Trim().ToLowerInvariant();
            string result;
            try
            {
                result = normalized switch
                {
                    "create" => await CreateAsync(name, description, instructions, toolNames, cancellationToken),
                    "update" => await UpdateAsync(name, description, instructions, toolNames, cancellationToken),
                    "list" => await ListSkillsAsync(cancellationToken),
                    "get" => await GetSkillAsync(name, cancellationToken),
                    "delete" => await DeleteAsync(name, cancellationToken),
                    _ => Fail($"Unknown action '{normalized}'. Valid: create, update, list, get, delete.")
                };
            }
            catch (OperationCanceledException) { throw; }
            catch (SkillStoreDurabilityException ex)
            {
                result = JsonConvert.SerializeObject(new { success = false, message = ex.Message,
                    committed = ex.Committed, durable = ex.Durable, published = ex.Published, retryable = ex.Retryable });
            }
            catch (SkillStorePublicationException ex)
            {
                result = JsonConvert.SerializeObject(new { success = false, message = ex.Message,
                    committed = true, durable = true, published = false, retryable = false });
            }
            catch (Exception ex)
            {
                result = Fail($"manage_skills '{normalized}' failed: {ex.Message}");
            }

            return result;
        }

        private async Task<string> CreateAsync(string name, string description, string instructions, string toolNames, CancellationToken cancellationToken)
        {
            SkillAuthoringResult r = await _coordinator.CreateAsync(name, description, instructions, ParseToolNames(toolNames), cancellationToken);
            return FromResult(r);
        }

        private async Task<string> UpdateAsync(string name, string description, string instructions, string toolNames, CancellationToken cancellationToken)
        {
            // For update, only a supplied tool_names replaces the allowlist; null leaves it unchanged.
            List<string> parsed = toolNames == null ? null : ParseToolNames(toolNames);
            SkillAuthoringResult r = await _coordinator.UpdateAsync(name, description, instructions, parsed, cancellationToken);
            return FromResult(r);
        }

        private async Task<string> ListSkillsAsync(CancellationToken cancellationToken)
        {
            IReadOnlyList<SkillRecord> skills = await _coordinator.ListSkillsAsync(cancellationToken);
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

        private async Task<string> GetSkillAsync(string name, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return Fail("get: 'name' is required.");
            }

            SkillRecord record = await _coordinator.GetSkillAsync(name, cancellationToken);
            if (record == null)
            {
                return Fail($"get: skill '{name.Trim()}' not found.");
            }

            IReadOnlyList<LuaScriptRevision> revisions = await _coordinator.ListRevisionsAsync(record.Id, cancellationToken);
            return Ok($"Skill '{record.Id}' (version {record.Version}).", new
            {
                name = record.Id,
                description = record.Description,
                instructions = record.Instructions,
                sections = record.Sections,
                tool_names = record.ToolNames,
                version = record.Version,
                revision_count = revisions.Count
            });
        }

        private async Task<string> DeleteAsync(string name, CancellationToken cancellationToken)
        {
            SkillAuthoringResult r = await _coordinator.DeleteAsync(name, cancellationToken);
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
                    : new
                    {
                        name = r.Record.Id, version = r.Record.Version, committed = r.Committed,
                        revision_recorded = r.RevisionRecorded, warning = r.Warning
                    })
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
