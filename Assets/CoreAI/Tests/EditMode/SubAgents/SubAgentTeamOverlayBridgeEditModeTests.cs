using System.Collections.Generic;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.SubAgents;
using CoreAI.Authority;
using CoreAI.ChatOverlay;
using NUnit.Framework;

namespace CoreAI.Core.Tests.EditMode.SubAgents
{
    /// <summary>
    /// What the player sees of Team-style sub-agents in the in-world chat: one feed line per state change (started,
    /// working, got a message, finished, stopped), the busy sub-agents on the status line between turns (the
    /// tracker's background detail) and while the agent thinks, the wake turn run as an overlay host turn (and handed
    /// back when the overlay refuses it), and <c>/agents</c>.
    /// </summary>
    [TestFixture]
    public sealed class SubAgentTeamOverlayBridgeEditModeTests
    {
        /// <summary>WHY one actor: its session id is part of the conversation key a wake must find again.</summary>
        private static readonly ActorContext Player = SubAgentTestSupport.Actor();

        private SynchronizationContext _previousSynchronizationContext;

        /// <summary>WHY: the team resumes on the captured context; the fixture runs continuations inline.</summary>
        [SetUp]
        public void DetachSynchronizationContext()
        {
            _previousSynchronizationContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void RestoreSynchronizationContext()
        {
            SynchronizationContext.SetSynchronizationContext(_previousSynchronizationContext);
        }

        [Test]
        public void Feed_GetsOneLinePerSubAgentChange_AndNeverRepeatsOne()
        {
            ScriptedSubAgentRunner runner = new() { HoldRuns = true };
            OrchestratorChatDriver driver = CreateDriver(runner);
            RecordingMessages messages = new();
            OrchestrationChatOverlayBridge bridge = new(driver, null, null, messages);
            LeadTurn turn = driver.BeginTurnAsync(ChatTurn("build", "lead-1"), CancellationToken.None).Result;
            SubAgentTeam team = turn.Session.Team;

            team.Start("builder", "Build a tower", "tower");
            bridge.Tick();
            team.Send("builder-1", "make it taller");
            bridge.Tick();
            runner.Held[0].SetResult(SubAgentTestSupport.Answer("tower built"));
            bridge.Tick();
            runner.Held[1].SetResult(SubAgentTestSupport.Answer("taller now\nsecond line"));
            bridge.Tick();
            team.Send("builder-1", "add a flag");
            bridge.Tick();
            team.Stop("builder-1");
            bridge.Tick();
            bridge.Tick();

            CollectionAssert.AreEqual(new[]
            {
                "builder-1|started: tower",
                "builder-1|got a message",
                "builder-1|working on: tower",
                "builder-1|finished: taller now",
                "builder-1|working on: tower",
                "builder-1|stopped"
            }, messages.Lines);
            driver.EndTurn(turn, OrchestrationPlanState.Completed);
        }

        [Test]
        public void StatusLine_ShowsBusySubAgentsBetweenTurns_AndWhileTheAgentThinks()
        {
            ScriptedSubAgentRunner runner = new() { HoldRuns = true };
            OrchestratorChatDriver driver = CreateDriver(runner,
                new OrchestratorModeOptions { Enabled = true, MaxParallelSubAgents = 1 });
            AgentStatusTracker status = new();
            string activeTrace = "";
            OrchestrationChatOverlayBridge bridge = new(driver, status, () => activeTrace, null);
            LeadTurn turn = driver.BeginTurnAsync(ChatTurn("build", "lead-1"), CancellationToken.None).Result;
            turn.Session.Team.Start("builder", "Build a tower");
            turn.Session.Team.Start("scripter", "Script the door");
            driver.EndTurn(turn, OrchestrationPlanState.Completed);

            bridge.Tick();
            string idle = status.Format(0);
            string teamLine = bridge.TeamLine;
            LeadTurn next = driver.BeginTurnAsync(ChatTurn("hi", "lead-2"), CancellationToken.None).Result;
            activeTrace = "lead-2";
            status.BeginTurn(0);
            bridge.Tick();
            string thinking = status.Format(0);
            status.ToolStarted("execute_lua");
            bridge.Tick();
            string tool = status.Format(0);
            driver.StopAll();
            status.EndTurn();
            bridge.Tick();

            Assert.AreEqual("builder-1 working; scripter-1 started", idle);
            Assert.AreEqual("builder-1 working; scripter-1 started", teamLine);
            Assert.AreEqual("builder-1 working; scripter-1 started", thinking);
            StringAssert.Contains("execute_lua", tool, "The agent's own tool call keeps its status.");
            Assert.AreEqual("", status.Format(0), "Nothing is busy after Stop.");
            driver.EndTurn(next, OrchestrationPlanState.Cancelled);
        }

        [Test]
        public void WakeTurn_RunsAsAnOverlayHostTurn_AndIsHandedBackWhenTheOverlayRefuses()
        {
            ScriptedSubAgentRunner runner = new() { HoldRuns = true };
            OrchestratorChatDriver driver = CreateDriver(runner);
            List<ChatOverlayHostTurnRequest> hostTurns = new();
            bool accept = false;
            bool overlayBusy = true;
            string activeTrace = "overlay-1";
            OrchestrationChatOverlayBridge bridge = new(driver, new AgentStatusTracker(), () => activeTrace, null,
                request =>
                {
                    hostTurns.Add(request);
                    return accept;
                },
                () => overlayBusy);
            LeadTurn turn = driver.BeginTurnAsync(ChatTurn("build", "overlay-1"), CancellationToken.None).Result;
            OrchestratorSession session = turn.Session;
            session.Team.Start("builder", "Build a tower");
            bridge.Tick();
            driver.EndTurn(turn, OrchestrationPlanState.Completed);
            activeTrace = "";
            runner.Held[0].SetResult(SubAgentTestSupport.Answer("tower built"));

            bridge.Tick();
            Assert.IsEmpty(hostTurns, "No wake while the overlay runs a turn.");
            overlayBusy = false;
            bridge.Tick();
            Assert.AreEqual(1, hostTurns.Count);
            Assert.AreEqual(1, session.Team.PendingNotices, "A refused wake hands its notice back.");

            accept = true;
            bridge.Tick();

            Assert.AreEqual(2, hostTurns.Count);
            ChatOverlayHostTurnRequest wake = hostTurns[1];
            StringAssert.Contains("builder-1 finished: tower built", wake.Text);
            Assert.IsTrue(wake.ShowAnswer, "The agent's report streams into the chat.");
            Assert.AreEqual(BuiltInAgentRoleIds.Creator, wake.RoleId);
            Assert.AreEqual(0, session.Team.PendingNotices);
            AiTaskRequest wakeChat = ChatTurn(wake.Text, wake.TraceId);
            LeadTurn wakeTurn = driver.BeginTurnAsync(wakeChat, CancellationToken.None).Result;
            Assert.IsTrue(wakeTurn.IsWake, "The host turn reaches the driver as the wake.");
            driver.EndTurn(wakeTurn, OrchestrationPlanState.Completed);
        }

        [Test]
        public void AgentsCommand_ListsEverySubAgentAndItsState()
        {
            ScriptedSubAgentRunner runner = new() { HoldRuns = true };
            OrchestratorChatDriver driver = CreateDriver(runner);
            OrchestrationChatOverlayBridge bridge = new(driver, null, null, null);
            ChatCommandRegistry registry = new();
            bridge.RegisterCommand(registry);
            ChatCommandResult empty = bridge.HandleAgentsCommand();
            LeadTurn turn = driver.BeginTurnAsync(ChatTurn("build", "lead-1"), CancellationToken.None).Result;
            turn.Session.Team.Start("builder", "Build a tower", "tower");

            ChatCommandResult listed = bridge.HandleAgentsCommand();
            ChatCommandResult status = bridge.HandleCommand("status");

            Assert.AreEqual("No sub-agents.", empty.Reply);
            Assert.AreEqual("builder-1 (builder): working on tower", listed.Reply);
            StringAssert.Contains("builder-1 (builder): working on tower", status.Reply);
            Assert.IsTrue(registry.TryResolve("subagents", out IChatCommand command));
            Assert.AreEqual(OrchestrationChatOverlayBridge.AgentsCommandName, command.Name);

            ChatCommandResult stopOne = bridge.HandleAgentsCommand("stop builder-1");
            ChatCommandResult stopAll = bridge.HandleAgentsCommand("stop");
            ChatCommandResult unknown = bridge.HandleAgentsCommand("stop painter-1");

            Assert.AreEqual("builder-1 stopped.", stopOne.Reply);
            Assert.IsTrue(runner.Tokens[0].IsCancellationRequested, "Between turns too, the player can stop it.");
            Assert.AreEqual("No sub-agent is working.", stopAll.Reply);
            Assert.AreEqual(ChatCommandOutcome.Failed, unknown.Outcome);
            bridge.Dispose();
            Assert.IsFalse(registry.TryResolve(OrchestrationChatOverlayBridge.AgentsCommandName, out _));
            driver.EndTurn(turn, OrchestrationPlanState.Completed);
            driver.StopAll();
        }

        private static AiTaskRequest ChatTurn(string text, string trace)
        {
            return new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Creator,
                Hint = text,
                SourceTag = OrchestratorChatDriver.ChatSourceTag,
                TraceId = trace,
                ActorContext = Player
            };
        }

        private static OrchestratorChatDriver CreateDriver(IAiTaskResultService runner,
            OrchestratorModeOptions options = null)
        {
            return new OrchestratorChatDriver(new OrchestratorSessionRegistry(
                options ?? new OrchestratorModeOptions { Enabled = true },
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
