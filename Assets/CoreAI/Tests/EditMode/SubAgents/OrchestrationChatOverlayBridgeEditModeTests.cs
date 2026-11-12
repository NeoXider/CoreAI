using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Ai.SubAgents;
using CoreAI.ChatOverlay;
using NUnit.Framework;

namespace CoreAI.Core.Tests.EditMode.SubAgents
{
    /// <summary>
    /// Integration change I7: the overlay's status line carries the plan line of its own lead turn, the feed gets
    /// one line per sub-agent task start and finish and one when the plan ends (never repeated), and the
    /// <c>/orchestrator</c> command switches the mode. Also the overlay member the bridge needs,
    /// <see cref="AgentStatusTracker.SetDetail"/>.
    /// </summary>
    [TestFixture]
    public sealed class OrchestrationChatOverlayBridgeEditModeTests
    {
        [Test]
        public async Task StatusLine_ShowsThePlanOfTheOverlaysOwnTurn_AndClearsForAnotherTurn()
        {
            ScriptedSubAgentRunner runner = new() { HoldRuns = true };
            OrchestratorChatDriver driver = CreateDriver(runner);
            LeadTurn turn = await driver.BeginTurnAsync(ChatTurn("castle", "overlay-trace"), CancellationToken.None);
            turn.Session.DeclareTool.Declare("builder: walls\nbuilder: towers");
            Task<SubAgentDelegationResult> delegation =
                SubAgentTestSupport.Delegate(turn.Session, "Build the walls", taskId: "t1");
            Assert.IsTrue(await SubAgentTestSupport.EventuallyAsync(() => runner.Requests.Count == 1));
            AgentStatusTracker status = new();
            status.BeginTurn(0);
            status.ToolStarted(DelegateTaskTool.DefaultName);
            string activeTrace = "overlay-trace";
            OrchestrationChatOverlayBridge bridge = new(driver, status, () => activeTrace, null);

            bridge.Tick();
            string planLine = status.Format(0);
            activeTrace = "another-turn";
            bridge.Tick();
            string otherLine = status.Format(0);

            StringAssert.Contains("builder", planLine);
            StringAssert.Contains("0/2", planLine);
            StringAssert.DoesNotContain(DelegateTaskTool.DefaultName, planLine, "The plan line replaces the tool name.");
            StringAssert.Contains(DelegateTaskTool.DefaultName, otherLine, "Another turn keeps its own status.");

            runner.Held[0].SetResult(SubAgentTestSupport.Answer("walls up"));
            await delegation;
            driver.EndTurn(turn, OrchestrationPlanState.Completed);
        }

        [Test]
        public async Task Feed_GetsOneLinePerTaskChange_AndOneWhenThePlanEnds()
        {
            ScriptedSubAgentRunner runner = new() { HoldRuns = true };
            OrchestratorChatDriver driver = CreateDriver(runner);
            RecordingMessages messages = new();
            OrchestrationChatOverlayBridge bridge = new(driver, null, null, messages);
            LeadTurn turn = await driver.BeginTurnAsync(ChatTurn("castle", "lead"), CancellationToken.None);
            turn.Session.DeclareTool.Declare("builder: walls");

            bridge.Tick();
            Task<SubAgentDelegationResult> delegation =
                SubAgentTestSupport.Delegate(turn.Session, "Build the walls", taskId: "t1");
            await SubAgentTestSupport.EventuallyAsync(() => runner.Requests.Count == 1);
            bridge.Tick();
            bridge.Tick();
            runner.Held[0].SetResult(SubAgentTestSupport.Answer("walls up"));
            await delegation;
            bridge.Tick();
            driver.EndTurn(turn, OrchestrationPlanState.Completed);
            bridge.Tick();
            bridge.Tick();

            CollectionAssert.AreEqual(new[]
            {
                "builder|planned: walls",
                "builder|working on: walls",
                "builder|done: walls",
                "|Plan done 1/1."
            }, messages.Lines);
        }

        [Test]
        public void Command_SwitchesTheModeOnAndOff_AndRefusesAnUnknownArgument()
        {
            OrchestratorChatDriver driver = CreateDriver(new ScriptedSubAgentRunner());
            OrchestrationChatOverlayBridge bridge = new(driver, null, null, null);
            ChatCommandRegistry registry = new();
            bridge.RegisterCommand(registry);

            ChatCommandResult off = bridge.HandleCommand("off");
            bool afterOff = driver.IsActive;
            ChatCommandResult on = bridge.HandleCommand("ON");
            ChatCommandResult status = bridge.HandleCommand("");
            ChatCommandResult unknown = bridge.HandleCommand("maybe");

            Assert.IsFalse(afterOff);
            Assert.IsTrue(driver.IsActive);
            Assert.AreEqual(ChatCommandOutcome.Handled, off.Outcome);
            Assert.AreEqual(ChatCommandOutcome.Handled, on.Outcome);
            StringAssert.Contains("is on", status.Reply);
            Assert.AreEqual(ChatCommandOutcome.Failed, unknown.Outcome);
            Assert.IsTrue(registry.TryResolve("lead", out IChatCommand command), "The alias /lead is registered too.");
            Assert.AreEqual(OrchestrationChatOverlayBridge.CommandName, command.Name);
            bridge.Dispose();
            Assert.IsFalse(registry.TryResolve(OrchestrationChatOverlayBridge.CommandName, out _));
        }

        [Test]
        public void StatusDetail_ReplacesThinkingAndTools_ButNotAnswering_AndClearsWithTheTurn()
        {
            AgentStatusTracker status = new();
            status.SetDetail("ignored while idle");
            Assert.AreEqual("", status.Detail);

            status.BeginTurn(0);
            status.SetDetail("builder: walls 1/3");
            string thinking = status.Format(0);
            status.ToolStarted("delegate_task");
            string tool = status.Format(0);
            status.ToolCompleted("delegate_task");
            status.MarkAnswering();
            string answering = status.Format(0);
            int version = status.Version;
            status.SetDetail("builder: walls 1/3");
            int unchanged = status.Version;
            status.EndTurn();

            Assert.AreEqual("builder: walls 1/3", thinking);
            Assert.AreEqual("builder: walls 1/3", tool);
            Assert.AreEqual(new ChatOverlayOptions().AnsweringText, answering);
            Assert.AreEqual(version, unchanged, "Setting the same detail changes nothing.");
            Assert.AreEqual("", status.Detail);
        }

        private static AiTaskRequest ChatTurn(string text, string trace)
        {
            return new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Creator,
                Hint = text,
                SourceTag = OrchestratorChatDriver.ChatSourceTag,
                TraceId = trace,
                ActorContext = SubAgentTestSupport.Actor()
            };
        }

        private static OrchestratorChatDriver CreateDriver(IAiTaskResultService runner)
        {
            return new OrchestratorChatDriver(new OrchestratorSessionRegistry(
                new OrchestratorModeOptions { Enabled = true, Style = OrchestrationStyle.Plan },
                new SubAgentRegistry(new[] { SubAgentTestSupport.Builder(), SubAgentTestSupport.Scripter() }),
                runner));
        }

        private sealed class RecordingMessages : IChatOverlayMessages
        {
            public List<string> Lines { get; } = new();

            public void PostSystemMessage(string text)
            {
                Lines.Add("|" + text);
            }

            public void PostMessage(string speaker, string text, ChatFeedLineKind kind = ChatFeedLineKind.System)
            {
                Lines.Add(speaker + "|" + text);
            }
        }
    }
}
