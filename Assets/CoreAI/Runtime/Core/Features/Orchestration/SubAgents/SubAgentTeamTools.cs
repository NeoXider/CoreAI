using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace CoreAI.Ai.SubAgents
{
    /// <summary>Default names of the Team-style sub-agent tools.</summary>
    public static class SubAgentTeamToolNames
    {
        /// <summary>Starts a background sub-agent.</summary>
        public const string Start = "agent_start";

        /// <summary>Messages or continues a sub-agent.</summary>
        public const string Send = "agent_send";

        /// <summary>Lists the sub-agents or reads one.</summary>
        public const string Read = "agent_read";

        /// <summary>Stops a sub-agent.</summary>
        public const string Stop = "agent_stop";

        /// <summary>True when <paramref name="toolName"/> is one of the default names (case-insensitive).</summary>
        public static bool IsDefaultName(string toolName)
        {
            string name = toolName?.Trim() ?? "";
            return string.Equals(name, Start, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, Send, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, Read, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, Stop, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The shared part of the four Team-style tools: bound to one <see cref="SubAgentTeam"/>, offered on the lead's
    /// request only (never registered on a role, so a sub-agent request never carries them), and every result carries
    /// the completion notices that were waiting (<c>news</c>), which the call consumes.
    /// <para>
    /// Contract choices: the bodies return at once (no tool waits for a sub-agent), so the default per-call deadline
    /// is fine; they are declared read-only so a batch of starts runs together, the team being thread-safe; repeated
    /// identical calls are allowed (a second identical start is a second sub-agent, a repeated read is a fresh read).
    /// </para>
    /// </summary>
    public abstract class SubAgentTeamTool : LlmToolBase, IAIFunctionLlmTool
    {
        private readonly string _name;

        /// <summary>Creates the tool over <paramref name="team"/> under <paramref name="name"/>.</summary>
        protected SubAgentTeamTool(SubAgentTeam team, string name, string defaultName)
        {
            Team = team ?? throw new ArgumentNullException(nameof(team));
            _name = string.IsNullOrWhiteSpace(name) ? defaultName : name.Trim();
        }

        /// <summary>The team the tool controls.</summary>
        protected SubAgentTeam Team { get; }

        /// <inheritdoc />
        public override string Name => _name;

        /// <inheritdoc />
        public override bool AllowDuplicates => true;

        /// <inheritdoc />
        public override bool IsMutating => false;

        /// <inheritdoc />
        public abstract AIFunction CreateAIFunction();

        /// <summary>The JSON result of <paramref name="result"/> with the waiting notices appended.</summary>
        protected string Finish(SubAgentControlResult result)
        {
            IReadOnlyList<SubAgentNotice> notices = Team.TakeNotices();
            List<string> news = new(notices.Count);
            foreach (SubAgentNotice notice in notices)
            {
                news.Add(notice.Text);
            }

            return result.ToToolResultJson(news);
        }

        /// <summary>The configured tool names, for descriptions that refer to each other.</summary>
        protected OrchestratorModeOptions Options => Team.Options;
    }

    /// <summary><c>agent_start</c>: starts a background sub-agent and returns its name at once.</summary>
    public sealed class AgentStartTool : SubAgentTeamTool
    {
        private const string SubAgentParameterDescription = "Sub-agent type, one of those listed above.";

        private const string TaskParameterDescription =
            "The whole job with every fact it needs (names, places, sizes).";

        private const string LabelParameterDescription = "Optional short title the player sees.";

        private readonly ISubAgentRegistry _registry;
        private readonly object _descriptionGate = new();
        private IReadOnlyList<SubAgentDefinition> _describedDefinitions;
        private string _description = "";

        /// <summary>Creates the tool.</summary>
        public AgentStartTool(SubAgentTeam team, ISubAgentRegistry registry, string name = SubAgentTeamToolNames.Start)
            : base(team, name, SubAgentTeamToolNames.Start)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        }

        /// <summary>
        /// The contract text plus the registered sub-agent types. Rebuilt only when the registry changes, so the
        /// chat agent's stable tool prefix (and its prompt cache) stays byte-identical between turns.
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
            ("label", "string", false, LabelParameterDescription));

        /// <inheritdoc />
        public override AIFunction CreateAIFunction()
        {
            Func<string, string, string, CancellationToken, Task<string>> func = ExecuteAsync;
            return AIFunctionFactory.Create(func, new AIFunctionFactoryOptions { Name = Name, Description = Description });
        }

        /// <summary>Starts the sub-agent and returns the JSON tool result.</summary>
        public Task<string> ExecuteAsync(
            [Description(SubAgentParameterDescription)] string subagent,
            [Description(TaskParameterDescription)] string task,
            [Description(LabelParameterDescription)] string label = "",
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Finish(Team.Start(subagent, task, label)));
        }

        private string BuildDescription(IReadOnlyList<SubAgentDefinition> definitions)
        {
            StringBuilder text = new();
            text.Append("Start a sub-agent on a big job; it works in the background and this returns its name at ");
            text.Append("once. It sees nothing of this chat. You are told when it finishes. Types:");
            if (definitions == null || definitions.Count == 0)
            {
                text.Append(" none.");
                return text.ToString();
            }

            foreach (SubAgentDefinition definition in definitions)
            {
                text.Append("\n- ").Append(definition.Id).Append(": ").Append(definition.Description);
            }

            return text.ToString();
        }
    }

    /// <summary><c>agent_send</c>: messages a working sub-agent or continues an idle one with its conversation.</summary>
    public sealed class AgentSendTool : SubAgentTeamTool
    {
        private const string AgentParameterDescription = "Sub-agent name, e.g. builder-1.";

        private const string MessageParameterDescription = "What to tell it, complete in itself.";

        /// <summary>Creates the tool.</summary>
        public AgentSendTool(SubAgentTeam team, string name = SubAgentTeamToolNames.Send)
            : base(team, name, SubAgentTeamToolNames.Send)
        {
        }

        /// <inheritdoc />
        public override string Description =>
            "Message a sub-agent. A working one gets it after its current step; an idle one continues the job " +
            "with its memory of it.";

        /// <inheritdoc />
        public override string ParametersSchema => JsonParams(
            ("agent", "string", true, AgentParameterDescription),
            ("message", "string", true, MessageParameterDescription));

        /// <inheritdoc />
        public override AIFunction CreateAIFunction()
        {
            Func<string, string, CancellationToken, Task<string>> func = ExecuteAsync;
            return AIFunctionFactory.Create(func, new AIFunctionFactoryOptions { Name = Name, Description = Description });
        }

        /// <summary>Sends the message and returns the JSON tool result.</summary>
        public Task<string> ExecuteAsync(
            [Description(AgentParameterDescription)] string agent,
            [Description(MessageParameterDescription)] string message,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Finish(Team.Send(agent, message)));
        }
    }

    /// <summary><c>agent_read</c>: lists the sub-agents, or reads one's state and its new output.</summary>
    public sealed class AgentReadTool : SubAgentTeamTool
    {
        private const string AgentParameterDescription = "Sub-agent name; omit it to list them all.";

        private const string CursorParameterDescription = "Cursor from your last read; only newer output comes.";

        /// <summary>Creates the tool.</summary>
        public AgentReadTool(SubAgentTeam team, string name = SubAgentTeamToolNames.Read)
            : base(team, name, SubAgentTeamToolNames.Read)
        {
        }

        /// <inheritdoc />
        public override string Description =>
            "Your sub-agents and their states, or one sub-agent's state and output after cursor. Do not poll: " +
            "you are told when one finishes.";

        /// <inheritdoc />
        public override string ParametersSchema => JsonParams(
            ("agent", "string", false, AgentParameterDescription),
            ("cursor", "integer", false, CursorParameterDescription));

        /// <inheritdoc />
        public override AIFunction CreateAIFunction()
        {
            Func<string, int, CancellationToken, Task<string>> func = ExecuteAsync;
            return AIFunctionFactory.Create(func, new AIFunctionFactoryOptions { Name = Name, Description = Description });
        }

        /// <summary>Reads and returns the JSON tool result.</summary>
        public Task<string> ExecuteAsync(
            [Description(AgentParameterDescription)] string agent = "",
            [Description(CursorParameterDescription)] int cursor = 0,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Finish(Team.Read(agent, cursor)));
        }
    }

    /// <summary><c>agent_stop</c>: stops a sub-agent and returns its last answer; it stays continuable.</summary>
    public sealed class AgentStopTool : SubAgentTeamTool
    {
        private const string AgentParameterDescription = "Sub-agent name.";

        /// <summary>Creates the tool.</summary>
        public AgentStopTool(SubAgentTeam team, string name = SubAgentTeamToolNames.Stop)
            : base(team, name, SubAgentTeamToolNames.Stop)
        {
        }

        /// <inheritdoc />
        public override string Description =>
            "Stop a sub-agent now; returns its last answer. It remembers the job: " + Options.SendToolName +
            " continues it.";

        /// <inheritdoc />
        public override string ParametersSchema => JsonParams(("agent", "string", true, AgentParameterDescription));

        /// <inheritdoc />
        public override AIFunction CreateAIFunction()
        {
            Func<string, CancellationToken, Task<string>> func = ExecuteAsync;
            return AIFunctionFactory.Create(func, new AIFunctionFactoryOptions { Name = Name, Description = Description });
        }

        /// <summary>Stops the sub-agent and returns the JSON tool result.</summary>
        public Task<string> ExecuteAsync(
            [Description(AgentParameterDescription)] string agent,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Finish(Team.Stop(agent)));
        }
    }
}
