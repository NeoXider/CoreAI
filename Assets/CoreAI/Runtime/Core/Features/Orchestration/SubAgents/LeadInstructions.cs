using System;
using System.Collections.Generic;
using System.Text;

namespace CoreAI.Ai.SubAgents
{
    /// <summary>What a lead-instructions provider may build its text from.</summary>
    public sealed class LeadInstructionsContext
    {
        /// <summary>Creates the context of a Plan-style lead turn.</summary>
        public LeadInstructionsContext(
            IReadOnlyList<SubAgentDefinition> subAgents,
            string delegateToolName,
            string declareToolName,
            int maxSubAgentCallsPerPlan,
            int maxParallelSubAgents)
            : this(subAgents, delegateToolName, declareToolName, maxSubAgentCallsPerPlan, maxParallelSubAgents,
                OrchestrationStyle.Plan, null, null, null, null)
        {
        }

        /// <summary>Creates the context of a lead turn of either style.</summary>
        public LeadInstructionsContext(
            IReadOnlyList<SubAgentDefinition> subAgents,
            string delegateToolName,
            string declareToolName,
            int maxSubAgentCallsPerPlan,
            int maxParallelSubAgents,
            OrchestrationStyle style,
            string startToolName,
            string sendToolName,
            string readToolName,
            string stopToolName)
        {
            SubAgents = subAgents ?? Array.Empty<SubAgentDefinition>();
            DelegateToolName = OrDefault(delegateToolName, DelegateTaskTool.DefaultName);
            DeclareToolName = OrDefault(declareToolName, DeclareTasksTool.DefaultName);
            MaxSubAgentCallsPerPlan = maxSubAgentCallsPerPlan;
            MaxParallelSubAgents = maxParallelSubAgents;
            Style = style;
            StartToolName = OrDefault(startToolName, SubAgentTeamToolNames.Start);
            SendToolName = OrDefault(sendToolName, SubAgentTeamToolNames.Send);
            ReadToolName = OrDefault(readToolName, SubAgentTeamToolNames.Read);
            StopToolName = OrDefault(stopToolName, SubAgentTeamToolNames.Stop);
        }

        /// <summary>The sub-agents the lead may delegate to.</summary>
        public IReadOnlyList<SubAgentDefinition> SubAgents { get; }

        /// <summary>Name of the delegate tool on the lead request (Plan style).</summary>
        public string DelegateToolName { get; }

        /// <summary>Name of the plan tool on the lead request (Plan style).</summary>
        public string DeclareToolName { get; }

        /// <summary>Sub-agent runs one plan (Team style: one player message) may start.</summary>
        public int MaxSubAgentCallsPerPlan { get; }

        /// <summary>Sub-agent runs that may run at the same time.</summary>
        public int MaxParallelSubAgents { get; }

        /// <summary>The style of the lead turn.</summary>
        public OrchestrationStyle Style { get; }

        /// <summary>Name of the start tool (Team style).</summary>
        public string StartToolName { get; }

        /// <summary>Name of the send tool (Team style).</summary>
        public string SendToolName { get; }

        /// <summary>Name of the read tool (Team style).</summary>
        public string ReadToolName { get; }

        /// <summary>Name of the stop tool (Team style).</summary>
        public string StopToolName { get; }

        private static string OrDefault(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }
    }

    /// <summary>
    /// Builds the instructions a lead turn receives in its request's cache-safe tail
    /// (<see cref="AiTaskRequest.RequestSystemInstructions"/>): in Plan style how to plan, declare, delegate, verify
    /// and report; in Team style when to hand a job to a background sub-agent and how the controls behave. Replace it
    /// to give a game or app its own lead voice; the default is <see cref="DefaultLeadInstructionsProvider"/>. Return
    /// the same text for the same context, so the lead's prompt stays stable from one turn to the next.
    /// </summary>
    public interface ILeadInstructionsProvider
    {
        /// <summary>The lead instructions for <paramref name="context"/>; empty sends none.</summary>
        string BuildInstructions(LeadInstructionsContext context);
    }

    /// <summary>
    /// The default lead instructions. Plan style: plan, declare, delegate with full context, check, verify, report.
    /// Team style: do small things yourself, start a sub-agent for a big job with every fact it needs, keep talking
    /// while it works, never guess its result (you are told), continue, stop and read it with the other controls.
    /// </summary>
    public sealed class DefaultLeadInstructionsProvider : ILeadInstructionsProvider
    {
        /// <inheritdoc />
        public string BuildInstructions(LeadInstructionsContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            return context.Style == OrchestrationStyle.Team ? BuildTeam(context) : BuildPlan(context);
        }

        private static string BuildTeam(LeadInstructionsContext context)
        {
            if (context.SubAgents.Count == 0)
            {
                return "";
            }

            StringBuilder text = new();
            text.Append("## Sub-agents\n");
            text.Append("A big job (many builds, several steps, a whole feature) you may hand to a sub-agent that ")
                .Append("works in the background while you keep talking with the player. Do small requests yourself.\n");
            text.Append("- ").Append(context.StartToolName)
                .Append(": one job with every fact it needs; it sees nothing of this chat. It returns at once: tell ")
                .Append("the player it started.\n");
            text.Append("- You are told when a sub-agent finishes, with its answer. Never guess its result and do not ")
                .Append("check on it in a loop.\n");
            text.Append("- ").Append(context.SendToolName).Append(" adds to or corrects a working sub-agent, or ")
                .Append("continues a finished one; ").Append(context.StopToolName).Append(" stops one; ")
                .Append(context.ReadToolName).Append(" lists them or shows one's progress.\n");
            text.Append("- At most ").Append(context.MaxSubAgentCallsPerPlan)
                .Append(" sub-agent runs per player message.");
            return text.ToString();
        }

        private static string BuildPlan(LeadInstructionsContext context)
        {
            string declare = context.DeclareToolName;
            string delegateTool = context.DelegateToolName;
            StringBuilder text = new();
            text.Append("## Orchestrator mode\n");
            text.Append("You lead a team of sub-agents. You plan and check; they do the work. Follow these steps:\n");
            text.Append("1. Plan: split the player's request into small tasks, each one job a single sub-agent can ")
                .Append("finish on its own.\n");
            text.Append("2. Declare: call ").Append(declare)
                .Append(" once with every task, one per line, written as subagent_id: task.\n");
            text.Append("3. Delegate: call ").Append(delegateTool)
                .Append(" for each task with its task_id, the sub-agent id and the complete task. A sub-agent sees ")
                .Append("nothing of this conversation, so put every fact it needs (names, positions, sizes, results of ")
                .Append("earlier tasks) in context.\n");
            text.Append("4. Order: delegate a task that depends on another only after that one has returned");
            text.Append(context.MaxParallelSubAgents > 1
                ? "; independent tasks may be delegated in the same turn.\n"
                : ".\n");
            text.Append("5. Check: read every result. If a task failed, retry it once with a clearer task or report ")
                .Append("the problem; never repeat the same failing call.\n");
            text.Append("6. Verify: when your own tools can check the outcome (logs, a camera capture, reading the ")
                .Append("state), check it before you report.\n");
            text.Append("7. Report: tell the player in a few sentences what was done and what did not work.\n");
            text.Append("This plan may start at most ").Append(context.MaxSubAgentCallsPerPlan)
                .Append(" sub-agent runs. When a result has ok=false with error call_budget_exhausted or ")
                .Append("token_budget_exhausted, stop delegating and report what is finished.\n");
            if (context.SubAgents.Count == 0)
            {
                text.Append("No sub-agents are registered: answer the player yourself.");
                return text.ToString();
            }

            text.Append("Sub-agents:");
            foreach (SubAgentDefinition definition in context.SubAgents)
            {
                text.Append("\n- ").Append(definition.Id).Append(": ").Append(definition.Description);
            }

            return text.ToString();
        }
    }
}
