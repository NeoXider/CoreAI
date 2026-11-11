using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace CoreAI.Ai.SubAgents
{
    /// <summary>
    /// <c>delegate_task</c>: the lead agent's only way to hand work to a sub-agent. Bound to one
    /// <see cref="OrchestratorSession"/> and offered on that session's lead request only, so it is never part of
    /// a role's tool list and never reaches a sub-agent request.
    /// <para>
    /// Tool contract choices: the per-call deadline is off (<see cref="ToolTimeoutMsOverride"/> = 0) because a
    /// sub-agent run lasts minutes and the default 30 s tool budget would cut every delegation; the run is
    /// bounded by its definition's deadline instead. It is declared read-only so the tool policy may start the
    /// delegations of one batch together and the plan's own parallelism decides how many run. Repeated identical
    /// calls are allowed: retrying a failed task with the same words is legitimate, and the plan's call budget
    /// bounds a loop.
    /// </para>
    /// </summary>
    public sealed class DelegateTaskTool : LlmToolBase, IAIFunctionLlmTool
    {
        /// <summary>Default tool name.</summary>
        public const string DefaultName = "delegate_task";

        private const string SubAgentParameterDescription =
            "Id of the sub-agent that should do the task (one of the ids listed in the tool description).";

        private const string TaskParameterDescription =
            "The one task for the sub-agent, complete and self-contained: it sees nothing of this conversation.";

        private const string ContextParameterDescription =
            "Optional facts the sub-agent needs (names, positions, earlier results). Only this text is shared.";

        private const string TaskIdParameterDescription =
            "Optional id of a task already on the plan; leave empty to add a new task.";

        private readonly SubAgentDelegator _delegator;
        private readonly ISubAgentRegistry _registry;
        private readonly string _name;
        private readonly object _descriptionGate = new();
        private IReadOnlyList<SubAgentDefinition> _describedDefinitions;
        private string _description = "";

        /// <summary>Creates the tool over <paramref name="delegator"/>.</summary>
        public DelegateTaskTool(SubAgentDelegator delegator, ISubAgentRegistry registry, string name = DefaultName)
        {
            _delegator = delegator ?? throw new ArgumentNullException(nameof(delegator));
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _name = string.IsNullOrWhiteSpace(name) ? DefaultName : name.Trim();
        }

        /// <inheritdoc />
        public override string Name => _name;

        /// <summary>
        /// The contract text plus the registered sub-agents. Rebuilt only when the registry changes, so the
        /// lead's stable tool prefix (and its prompt cache) stays byte-identical between turns.
        /// </summary>
        public override string Description
        {
            get
            {
                IReadOnlyList<SubAgentDefinition> definitions = _registry.Definitions;
                lock (_descriptionGate)
                {
                    if (!ReferenceEquals(definitions, _describedDefinitions))
                    {
                        _description = BuildDescription(definitions);
                        _describedDefinitions = definitions;
                    }

                    return _description;
                }
            }
        }

        /// <inheritdoc />
        public override string ParametersSchema => JsonParams(
            ("subagent", "string", true, SubAgentParameterDescription),
            ("task", "string", true, TaskParameterDescription),
            ("context", "string", false, ContextParameterDescription),
            ("task_id", "string", false, TaskIdParameterDescription));

        /// <inheritdoc />
        public override bool AllowDuplicates => true;

        /// <inheritdoc />
        public override int? ToolTimeoutMsOverride => 0;

        /// <inheritdoc />
        public override bool IsMutating => false;

        /// <inheritdoc />
        public AIFunction CreateAIFunction()
        {
            Func<string, string, string, string, CancellationToken, Task<string>> func = ExecuteAsync;
            AIFunctionFactoryOptions options = new()
            {
                Name = Name,
                Description = Description
            };
            return AIFunctionFactory.Create(func, options);
        }

        /// <summary>Runs one delegation and returns the JSON tool result.</summary>
        // WHY: MEAI awaits this task with ConfigureAwait(false) inside its binary. In the WebGL player the result
        // must be published with the host SynchronizationContext cleared, or the lead's turn never resumes after
        // a sub-agent run that completed asynchronously (see MeaiToolTaskBridge).
        public Task<string> ExecuteAsync(
            [Description(SubAgentParameterDescription)] string subagent,
            [Description(TaskParameterDescription)] string task,
            [Description(ContextParameterDescription)] string context = "",
            [Description(TaskIdParameterDescription)] string task_id = "",
            CancellationToken cancellationToken = default) =>
            MeaiToolTaskBridge.Publish(ExecuteBodyAsync(subagent, task, context, task_id, cancellationToken));

        private async Task<string> ExecuteBodyAsync(
            string subagent,
            string task,
            string context,
            string taskId,
            CancellationToken cancellationToken)
        {
            SubAgentDelegationResult result = await _delegator.DelegateAsync(new SubAgentDelegationRequest
            {
                SubAgentId = subagent ?? "",
                Task = task ?? "",
                Context = context ?? "",
                TaskId = taskId ?? ""
            }, cancellationToken);
            return result.ToToolResultJson();
        }

        private static string BuildDescription(IReadOnlyList<SubAgentDefinition> definitions)
        {
            StringBuilder text = new();
            text.Append("Hand one focused task to a sub-agent and get its answer back as this tool's result. ");
            text.Append("The sub-agent starts with a clean context: it sees only 'task' and the 'context' you pass, ");
            text.Append("never this conversation, so include every fact it needs. ");
            text.Append("Sub-agents cannot delegate further. A failed call returns ok=false with an error code.");
            if (definitions == null || definitions.Count == 0)
            {
                text.Append(" No sub-agents are registered.");
                return text.ToString();
            }

            text.Append(" Sub-agents:");
            foreach (SubAgentDefinition definition in definitions)
            {
                text.Append("\n- ").Append(definition.Id).Append(": ").Append(definition.Description);
            }

            return text.ToString();
        }
    }
}
