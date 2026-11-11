using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CoreAI.Ai.SubAgents
{
    /// <summary>
    /// <c>declare_tasks</c>: the lead writes its plan down before it delegates, so the HUD can show the whole
    /// plan ("builder: walls 0/5") from the start and each delegation can name the task it belongs to. Bound to
    /// one <see cref="OrchestratorSession"/> and offered on the lead request only, like
    /// <see cref="DelegateTaskTool"/>.
    /// <para>
    /// The argument is plain text, one task per line, optionally prefixed with a sub-agent id
    /// (<c>builder: Build four walls</c>): a line format small local models write reliably, where a nested JSON
    /// array is often malformed. A prefix that is not a registered sub-agent id stays part of the task title,
    /// so a title may contain a colon. The call is all or nothing: a refused call declares no task.
    /// </para>
    /// </summary>
    public sealed class DeclareTasksTool : LlmToolBase, IAIFunctionLlmTool
    {
        /// <summary>Default tool name.</summary>
        public const string DefaultName = "declare_tasks";

        /// <summary>Most tasks one call may declare; a longer plan is refused so a runaway list cannot flood the HUD.</summary>
        public const int MaxTasksPerCall = 20;

        private const string TasksParameterDescription =
            "The plan: one task per line, each written as subagent_id: task. Declare every task before the first delegation.";

        private readonly OrchestratorSession _session;
        private readonly ISubAgentRegistry _registry;
        private readonly string _name;

        /// <summary>Creates the tool for <paramref name="session"/>.</summary>
        public DeclareTasksTool(OrchestratorSession session, ISubAgentRegistry registry, string name = DefaultName)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _name = string.IsNullOrWhiteSpace(name) ? DefaultName : name.Trim();
        }

        /// <inheritdoc />
        public override string Name => _name;

        /// <inheritdoc />
        public override string Description =>
            "Write down the plan before you delegate: one task per line as subagent_id: task. " +
            "Returns a task_id for every task; pass it to the delegate tool as task_id so progress is shown per task. " +
            "Call it again to add tasks later. At most " + MaxTasksPerCall + " tasks per call.";

        /// <inheritdoc />
        public override string ParametersSchema => JsonParams(("tasks", "string", true, TasksParameterDescription));

        /// <inheritdoc />
        public override bool IsMutating => false;

        /// <inheritdoc />
        public AIFunction CreateAIFunction()
        {
            Func<string, CancellationToken, Task<string>> func = ExecuteAsync;
            AIFunctionFactoryOptions options = new()
            {
                Name = Name,
                Description = Description
            };
            return AIFunctionFactory.Create(func, options);
        }

        /// <summary>Declares the tasks and returns the JSON tool result.</summary>
        public Task<string> ExecuteAsync(
            [Description(TasksParameterDescription)] string tasks,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Declare(tasks));
        }

        /// <summary>Declares the tasks in <paramref name="tasks"/> on the running plan; returns the JSON tool result.</summary>
        public string Declare(string tasks)
        {
            List<DeclaredLine> lines = new();
            string[] rawLines = (tasks ?? "").Split('\n');
            for (int i = 0; i < rawLines.Length; i++)
            {
                string line = StripListMarker(rawLines[i].Trim());
                if (line.Length == 0)
                {
                    continue;
                }

                string subAgentId = "";
                int colon = line.IndexOf(':');
                if (colon > 0 && _registry.TryGet(line.Substring(0, colon).Trim(), out SubAgentDefinition definition))
                {
                    subAgentId = definition.Id;
                    line = line.Substring(colon + 1).Trim();
                    if (line.Length == 0)
                    {
                        return Refusal(SubAgentErrorCode.InvalidArguments,
                            $"Line {i + 1} names the sub-agent '{subAgentId}' but no task.");
                    }
                }

                lines.Add(new DeclaredLine(line, subAgentId));
            }

            if (lines.Count == 0)
            {
                return Refusal(SubAgentErrorCode.InvalidArguments,
                    "No task was given: write one task per line as subagent_id: task.");
            }

            if (lines.Count > MaxTasksPerCall)
            {
                return Refusal(SubAgentErrorCode.InvalidArguments,
                    $"{lines.Count} tasks in one call; declare at most {MaxTasksPerCall} and split the plan.");
            }

            OrchestrationPlan plan = _session.ActivePlan;
            if (plan == null)
            {
                return Refusal(SubAgentErrorCode.PlanNotActive, "No plan is running in this conversation.");
            }

            JArray declared = new();
            foreach (DeclaredLine line in lines)
            {
                string taskId = plan.DeclareTask(line.Title, line.SubAgentId);
                JObject entry = new() { ["task_id"] = taskId, ["title"] = line.Title };
                if (line.SubAgentId.Length > 0)
                {
                    entry["subagent"] = line.SubAgentId;
                }

                declared.Add(entry);
            }

            JObject json = new()
            {
                ["ok"] = true,
                ["declared"] = declared,
                ["total_tasks"] = plan.Snapshot().TotalTasks
            };
            return json.ToString(Formatting.None);
        }

        private static string StripListMarker(string line)
        {
            if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
            {
                return line.Substring(2).Trim();
            }

            int digits = 0;
            while (digits < line.Length && char.IsDigit(line[digits]))
            {
                digits++;
            }

            if (digits > 0 && digits + 1 < line.Length && (line[digits] == '.' || line[digits] == ')') &&
                line[digits + 1] == ' ')
            {
                return line.Substring(digits + 2).Trim();
            }

            return line;
        }

        private static string Refusal(SubAgentErrorCode code, string message)
        {
            JObject json = new()
            {
                ["ok"] = false,
                ["error"] = SubAgentErrorCodes.ToWireName(code),
                ["message"] = message
            };
            return json.ToString(Formatting.None);
        }

        private readonly struct DeclaredLine
        {
            public DeclaredLine(string title, string subAgentId)
            {
                Title = title;
                SubAgentId = subAgentId;
            }

            public string Title { get; }

            public string SubAgentId { get; }
        }
    }
}
