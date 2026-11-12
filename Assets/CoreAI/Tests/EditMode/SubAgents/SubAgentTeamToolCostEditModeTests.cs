using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Ai.SubAgents;
using NUnit.Framework;

namespace CoreAI.Core.Tests.EditMode.SubAgents
{
    /// <summary>
    /// What Team style adds to every request of the chat agent, counted the way <c>PROMPT_TOKEN_COSTS.md</c> counts
    /// (<see cref="PromptTokenCostMeter"/>, provider tool JSON): the four tool definitions with the shipped default
    /// sub-agents in the roster and the instruction block, pinned under a budget, every description short, and
    /// nothing at all with the mode off. The Plan-style pair and a one-tool <c>action</c> variant are measured on the
    /// same meter for the design note's comparison.
    /// </summary>
    [TestFixture]
    public sealed class SubAgentTeamToolCostEditModeTests
    {
        /// <summary>Most tokens the four definitions may add per request (with the two default sub-agents).</summary>
        public const int MaxTeamToolTokens = 600;

        /// <summary>Most tokens the Team-style instruction block may add per request.</summary>
        public const int MaxTeamInstructionTokens = 220;

        /// <summary>
        /// Longest contract text one tool may have (without the host's sub-agent roster); the how-to lives in the
        /// instructions.
        /// </summary>
        public const int MaxDescriptionChars = 250;

        [Test]
        public void TeamControls_StayWithinTheirTokenBudget_AndTheModeOffCostsNothing()
        {
            PromptTokenCostMeter meter = new();
            OrchestratorSession session = new("conversation-1", new OrchestratorModeOptions(),
                new SubAgentRegistry(ShippedDefaults()), new ScriptedSubAgentRunner());
            int tools = 0;
            foreach (ILlmTool tool in session.TeamTools)
            {
                int cost = meter.CountTool(tool);
                tools += cost;
                TestContext.Out.WriteLine($"{tool.Name}: {cost} tokens, description {tool.Description.Length} chars");
            }

            OrchestratorSession bare = new("conversation-2", new OrchestratorModeOptions(), new SubAgentRegistry(),
                new ScriptedSubAgentRunner());
            foreach (ILlmTool tool in bare.TeamTools)
            {
                // WHY without the roster: the sub-agent descriptions are the host's data; the pin is on the contract.
                Assert.LessOrEqual(tool.Description.Length, MaxDescriptionChars, tool.Name);
            }

            AiTaskRequest host = new() { Hint = "hello", RequestSystemInstructions = "" };
            AiTaskRequest team = session.CreateTeamLeadRequest(host);
            int instructions = meter.CountText(team.RequestSystemInstructions);
            int planTools = meter.CountTool(session.DelegateTool) + meter.CountTool(session.DeclareTool);
            int planInstructions = meter.CountText(new DefaultLeadInstructionsProvider().BuildInstructions(
                new LeadInstructionsContext(ShippedDefaults(), "delegate_task", "declare_tasks", 8, 1)));
            int oneTool = meter.CountText(OneToolVariantJson());

            TestContext.Out.WriteLine($"Team style: tools {tools}, instructions {instructions}, total {tools + instructions}");
            TestContext.Out.WriteLine($"Plan style: tools {planTools}, instructions {planInstructions}");
            TestContext.Out.WriteLine($"One tool with an action field (same texts): {oneTool}");
            Assert.LessOrEqual(tools, MaxTeamToolTokens);
            Assert.LessOrEqual(instructions, MaxTeamInstructionTokens);

            OrchestratorChatDriver driver = new(new OrchestratorSessionRegistry(
                new OrchestratorModeOptions { Enabled = true }, new SubAgentRegistry(ShippedDefaults()),
                new ScriptedSubAgentRunner())) { IsActive = false };
            AiTaskRequest off = new() { Hint = "hello", SourceTag = OrchestratorChatDriver.ChatSourceTag };
            LeadTurn offTurn = driver.BeginTurnAsync(off, System.Threading.CancellationToken.None).Result;
            Assert.AreSame(off, offTurn.Request, "With the mode off the request is the caller's own: zero tokens.");
            Assert.IsNull(offTurn.Request.RequestTools);
        }

        /// <summary>The two default sub-agents with the texts <c>CoreAiDefaultSubAgents</c> ships.</summary>
        private static SubAgentDefinition[] ShippedDefaults()
        {
            return new[]
            {
                new SubAgentDefinition("builder", BuiltInAgentRoleIds.Builder,
                    "Places, moves and removes scene objects with the native world tools. Give it one concrete " +
                    "building task, for example 'build a small stone tower at the spawn'.",
                    new[] { "world_command" }),
                new SubAgentDefinition("scripter", BuiltInAgentRoleIds.Programmer,
                    "Builds and scripts the Roblox-style world in Lua: parts, models, mods with behaviour. Give it one " +
                    "self-contained task, for example 'make the door open when a player touches it'.",
                    new[] { "execute_lua" })
            };
        }

        /// <summary>The same contract as one function with an <c>action</c> field, for the comparison only.</summary>
        private static string OneToolVariantJson()
        {
            List<string> roster = new();
            foreach (SubAgentDefinition definition in ShippedDefaults())
            {
                roster.Add("- " + definition.Id + ": " + definition.Description);
            }

            string description = "Control your background sub-agents. action=start: start one on a big job (needs " +
                                 "subagent, task); returns its name at once; it sees nothing of this chat. " +
                                 "action=send: message one (needs agent, message); a working one gets it at its next " +
                                 "step, a finished one continues. action=read: list them, or with agent its state and " +
                                 "output after cursor. action=stop: stop one (needs agent); returns its last answer. " +
                                 "You are told when one finishes. Types:\\n" + string.Join("\\n", roster);
            return "[{\"type\":\"function\",\"function\":{\"name\":\"agents\",\"description\":\"" + description +
                   "\",\"parameters\":{\"type\":\"object\",\"properties\":{" +
                   "\"action\":{\"type\":\"string\",\"description\":\"start, send, read or stop.\"}," +
                   "\"subagent\":{\"type\":\"string\",\"description\":\"start: sub-agent type.\"}," +
                   "\"task\":{\"type\":\"string\",\"description\":\"start: the whole job with every fact it needs.\"}," +
                   "\"label\":{\"type\":\"string\",\"description\":\"start: optional short title.\"}," +
                   "\"agent\":{\"type\":\"string\",\"description\":\"send, read, stop: sub-agent name.\"}," +
                   "\"message\":{\"type\":\"string\",\"description\":\"send: what to tell it.\"}," +
                   "\"cursor\":{\"type\":\"integer\",\"description\":\"read: cursor from your last read.\"}}," +
                   "\"required\":[\"action\"]}}}]";
        }
    }
}
