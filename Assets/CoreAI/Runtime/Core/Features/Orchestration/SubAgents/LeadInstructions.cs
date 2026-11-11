using System;
using System.Collections.Generic;
using System.Text;

namespace CoreAI.Ai.SubAgents
{
    /// <summary>What a lead-instructions provider may build its text from.</summary>
    public sealed class LeadInstructionsContext
    {
        /// <summary>Creates the context.</summary>
        public LeadInstructionsContext(
            IReadOnlyList<SubAgentDefinition> subAgents,
            string delegateToolName,
            string declareToolName,
            int maxSubAgentCallsPerPlan,
            int maxParallelSubAgents)
        {
            SubAgents = subAgents ?? Array.Empty<SubAgentDefinition>();
            DelegateToolName = string.IsNullOrWhiteSpace(delegateToolName)
                ? DelegateTaskTool.DefaultName
                : delegateToolName.Trim();
            DeclareToolName = string.IsNullOrWhiteSpace(declareToolName)
                ? DeclareTasksTool.DefaultName
                : declareToolName.Trim();
            MaxSubAgentCallsPerPlan = maxSubAgentCallsPerPlan;
            MaxParallelSubAgents = maxParallelSubAgents;
        }

        /// <summary>The sub-agents the lead may delegate to.</summary>
        public IReadOnlyList<SubAgentDefinition> SubAgents { get; }

        /// <summary>Name of the delegate tool on the lead request.</summary>
        public string DelegateToolName { get; }

        /// <summary>Name of the plan tool on the lead request.</summary>
        public string DeclareToolName { get; }

        /// <summary>Sub-agent runs one plan may start.</summary>
        public int MaxSubAgentCallsPerPlan { get; }

        /// <summary>Sub-agent runs of one plan that may run at the same time.</summary>
        public int MaxParallelSubAgents { get; }
    }

    /// <summary>
    /// Builds the instructions a lead turn receives in its request's cache-safe tail
    /// (<see cref="AiTaskRequest.RequestSystemInstructions"/>): how to plan, declare, delegate, verify and report.
    /// Replace it to give a game or app its own lead voice; the default is
    /// <see cref="DefaultLeadInstructionsProvider"/>. Return the same text for the same context, so the lead's
    /// prompt stays stable from one turn to the next.
    /// </summary>
    public interface ILeadInstructionsProvider
    {
        /// <summary>The lead instructions for <paramref name="context"/>; empty sends none.</summary>
        string BuildInstructions(LeadInstructionsContext context);
    }

    /// <summary>The default lead instructions: plan, declare, delegate with full context, check, verify, report.</summary>
    public sealed class DefaultLeadInstructionsProvider : ILeadInstructionsProvider
    {
        /// <inheritdoc />
        public string BuildInstructions(LeadInstructionsContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

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
