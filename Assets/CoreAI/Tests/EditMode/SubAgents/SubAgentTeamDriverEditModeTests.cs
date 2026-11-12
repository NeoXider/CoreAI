using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Ai.SubAgents;
using CoreAI.Authority;
using CoreAI.Messaging;
using CoreAI.Session;
using Microsoft.Extensions.AI;
using NUnit.Framework;

namespace CoreAI.Core.Tests.EditMode.SubAgents
{
    /// <summary>
    /// Team style in the chat driver: a chat turn stays the chat agent's own turn with the four controls and their
    /// rules; the player's next message never waits behind sub-agents; a completion is folded into the next player
    /// turn or runs as a wake turn that opens no new budget; a player turn waits only for a running wake; Stop ends
    /// sub-agents and wakes; with the mode off or no sub-agents nothing is added; and the whole flow through the
    /// real orchestrator.
    /// </summary>
    [TestFixture]
    public sealed class SubAgentTeamDriverEditModeTests
    {
        private static readonly ActorContext Player = SubAgentTestSupport.Actor();

        private SynchronizationContext _previousSynchronizationContext;

        /// <summary>WHY: the driver and the team resume on the captured context; the fixture runs them inline.</summary>
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
        public void TeamTurn_IsTheChatAgentsOwnTurn_WithTheFourControlsAndTheirRules()
        {
            OrchestratorChatDriver driver = CreateDriver(new ScriptedSubAgentRunner());
            AiTaskRequest chat = ChatTurn("build a castle");
            chat.RequestSystemInstructions = "Host tail.";

            LeadTurn turn = driver.BeginTurnAsync(chat, CancellationToken.None).Result;

            Assert.AreEqual(LeadTurnDisposition.Team, turn.Disposition);
            Assert.IsFalse(turn.IsWake);
            Assert.IsNull(turn.Plan, "No plan: the agent decides by itself whether to delegate.");
            Assert.IsNull(turn.Session.ActivePlan);
            Assert.AreEqual("build a castle", turn.Request.Hint, "The player's line is untouched.");
            CollectionAssert.AreEqual(
                new[]
                {
                    SubAgentTeamToolNames.Start, SubAgentTeamToolNames.Send, SubAgentTeamToolNames.Read,
                    SubAgentTeamToolNames.Stop
                },
                ToolNames(turn.Request.RequestTools));
            string instructions = turn.Request.RequestSystemInstructions;
            StringAssert.StartsWith("Host tail.\n\n## Sub-agents", instructions);
            StringAssert.Contains("Do small requests yourself", instructions);
            StringAssert.Contains("Never guess its result", instructions);
            StringAssert.Contains("do not check on it in a loop", instructions);
            StringAssert.Contains("At most 8 sub-agent runs per player message", instructions);
            driver.EndTurn(turn, OrchestrationPlanState.Completed);
        }

        [Test]
        public void ModeOff_OrNoSubAgents_AddsNothingToTheRequest()
        {
            OrchestratorChatDriver driver = CreateDriver(new ScriptedSubAgentRunner());
            driver.IsActive = false;
            AiTaskRequest off = ChatTurn("hello");
            LeadTurn offTurn = driver.BeginTurnAsync(off, CancellationToken.None).Result;
            OrchestratorChatDriver empty = new(new OrchestratorSessionRegistry(
                new OrchestratorModeOptions { Enabled = true }, new SubAgentRegistry(), new ScriptedSubAgentRunner()));
            AiTaskRequest bare = ChatTurn("hello");
            bare.RequestSystemInstructions = "Host tail.";
            LeadTurn bareTurn = empty.BeginTurnAsync(bare, CancellationToken.None).Result;

            Assert.AreEqual(LeadTurnDisposition.PassThrough, offTurn.Disposition);
            Assert.AreSame(off, offTurn.Request, "Mode off: the request is the caller's own.");
            Assert.IsNull(bareTurn.Request.RequestTools, "No sub-agent registered: no tool is offered.");
            Assert.AreEqual("Host tail.", bareTurn.Request.RequestSystemInstructions, "...and no instruction.");
            empty.EndTurn(bareTurn, OrchestrationPlanState.Completed);
        }

        [Test]
        public void PlayerMessage_RunsAtOnce_WhileASubAgentStillWorks()
        {
            ScriptedSubAgentRunner runner = new() { HoldRuns = true };
            OrchestratorChatDriver driver = CreateDriver(runner);
            LeadTurn first = driver.BeginTurnAsync(ChatTurn("build a tower"), CancellationToken.None).Result;
            string started = Start(first, "builder", "Build a tower at the spawn");
            driver.EndTurn(first, OrchestrationPlanState.Completed);

            Task<LeadTurn> second = driver.BeginTurnAsync(ChatTurn("what time is it"), CancellationToken.None);

            StringAssert.Contains("\"agent\":\"builder-1\"", started);
            Assert.IsTrue(second.IsCompleted, "Nothing waits behind the sub-agent.");
            Assert.AreEqual(LeadTurnDisposition.Team, second.Result.Disposition);
            Assert.IsEmpty(second.Result.Session.Queue.Snapshot(), "The plan queue is not used in Team style.");
            Assert.AreEqual(SubAgentMemberState.Working, second.Result.Session.Team.Snapshot()[0].State);
            driver.EndTurn(second.Result, OrchestrationPlanState.Completed);
            driver.StopAll();
        }

        [Test]
        public void Completion_IsFoldedIntoTheNextPlayerTurn_AsNews()
        {
            ScriptedSubAgentRunner runner = new() { HoldRuns = true };
            OrchestratorChatDriver driver = CreateDriver(runner);
            LeadTurn first = driver.BeginTurnAsync(ChatTurn("build a tower"), CancellationToken.None).Result;
            Start(first, "builder", "Build a tower");
            driver.EndTurn(first, OrchestrationPlanState.Completed);
            runner.Held[0].SetResult(SubAgentTestSupport.Answer("The tower stands, 12 blocks high."));

            LeadTurn next = driver.BeginTurnAsync(ChatTurn("nice, and now?"), CancellationToken.None).Result;

            StringAssert.Contains(SubAgentTeam.NewsHeading, next.Request.RequestSystemInstructions);
            StringAssert.Contains("builder-1 finished: The tower stands, 12 blocks high.",
                next.Request.RequestSystemInstructions);
            Assert.AreEqual("nice, and now?", next.Request.Hint, "The news is not put in the player's mouth.");
            Assert.AreEqual(0, next.Session.Team.PendingNotices, "A notice is delivered once.");
            driver.EndTurn(next, OrchestrationPlanState.Completed);
        }

        [Test]
        public void WakeTurn_IsDueWhenTheLeadIsIdle_OpensNoNewBudget_AndAPlayerTurnWaitsForIt()
        {
            ScriptedSubAgentRunner runner = new() { HoldRuns = true };
            OrchestratorChatDriver driver = CreateDriver(runner);
            List<string> due = new();
            driver.WakeDue += key => due.Add(key);
            LeadTurn first = driver.BeginTurnAsync(ChatTurn("build a tower"), CancellationToken.None).Result;
            Start(first, "builder", "Build a tower");
            string key = first.Session.ConversationId;
            Assert.IsFalse(driver.TryTakeWake(key, out _), "No wake while the lead's own turn runs.");
            driver.EndTurn(first, OrchestrationPlanState.Completed);
            runner.Held[0].SetResult(SubAgentTestSupport.Answer("tower built"));

            Assert.AreEqual(new[] { key }, due.ToArray(), "The completion asked for a wake.");
            Assert.IsTrue(driver.TryTakeWake(key, out LeadWake wake));
            Assert.AreEqual(BuiltInAgentRoleIds.Creator, wake.RoleId);
            StringAssert.Contains("builder-1 finished: tower built", wake.Text);
            StringAssert.Contains("not from the player", wake.Text);
            Assert.IsFalse(driver.TryTakeWake(key, out _), "One wake at a time.");

            AiTaskRequest wakeChat = ChatTurn(wake.Text);
            wakeChat.TraceId = wake.TraceId;
            LeadTurn wakeTurn = driver.BeginTurnAsync(wakeChat, CancellationToken.None).Result;
            Task<LeadTurn> player = driver.BeginTurnAsync(ChatTurn("hello?"), CancellationToken.None);

            Assert.IsTrue(wakeTurn.IsWake);
            Assert.AreEqual(LeadTurnDisposition.Team, wakeTurn.Disposition);
            Assert.AreEqual(1, wakeTurn.Session.Team.Budget.CallsStarted, "A wake does not reset the budget.");
            CollectionAssert.Contains(ToolNames(wakeTurn.Request.RequestTools), SubAgentTeamToolNames.Send,
                "A wake turn may continue the sub-agent.");
            Assert.IsFalse(player.IsCompleted, "The player's turn waits for the one running wake.");

            driver.EndTurn(wakeTurn, OrchestrationPlanState.Completed);

            Assert.IsTrue(player.IsCompleted);
            Assert.AreEqual(0, player.Result.Session.Team.Budget.CallsStarted, "The player message opened a window.");
            driver.EndTurn(player.Result, OrchestrationPlanState.Completed);
        }

        [Test]
        public void WakeTurns_AreBoundedPerPlayerMessage_AndTheRestWaitsForThePlayer()
        {
            ScriptedSubAgentRunner runner = new() { HoldRuns = true };
            OrchestratorChatDriver driver = CreateDriver(runner,
                new OrchestratorModeOptions { Enabled = true, MaxWakeTurnsPerInstruction = 1, MaxParallelSubAgents = 2 });
            LeadTurn first = driver.BeginTurnAsync(ChatTurn("build"), CancellationToken.None).Result;
            Start(first, "builder", "one");
            Start(first, "scripter", "two");
            string key = first.Session.ConversationId;
            driver.EndTurn(first, OrchestrationPlanState.Completed);
            runner.Held[0].SetResult(SubAgentTestSupport.Answer("one done"));
            Assert.IsTrue(driver.TryTakeWake(key, out LeadWake wake));
            AiTaskRequest wakeChat = ChatTurn(wake.Text);
            wakeChat.TraceId = wake.TraceId;
            LeadTurn wakeTurn = driver.BeginTurnAsync(wakeChat, CancellationToken.None).Result;
            driver.EndTurn(wakeTurn, OrchestrationPlanState.Completed);

            runner.Held[1].SetResult(SubAgentTestSupport.Answer("two done"));

            Assert.IsFalse(driver.TryTakeWake(key, out _), "This player message's wakes are spent.");
            LeadTurn next = driver.BeginTurnAsync(ChatTurn("and?"), CancellationToken.None).Result;
            StringAssert.Contains("scripter-1 finished: two done", next.Request.RequestSystemInstructions);
            driver.EndTurn(next, OrchestrationPlanState.Completed);
        }

        [Test]
        public void ReturnedWake_PutsItsNoticesBack()
        {
            OrchestratorChatDriver driver = CreateDriver(new ScriptedSubAgentRunner());
            LeadTurn first = driver.BeginTurnAsync(ChatTurn("build"), CancellationToken.None).Result;
            OrchestratorSession session = first.Session;
            driver.EndTurn(first, OrchestrationPlanState.Completed);
            session.Team.Start("builder", "Build a hut");

            Assert.IsTrue(driver.TryTakeWake(session.ConversationId, out LeadWake wake));
            Assert.AreEqual(0, session.Team.PendingNotices);
            driver.ReturnWake(wake);

            Assert.AreEqual(1, session.Team.PendingNotices);
            Assert.IsTrue(driver.TryTakeWake(session.ConversationId, out LeadWake again), "It is due again.");
            driver.ReturnWake(again);
        }

        [Test]
        public void Stop_EndsTheSubAgents_DropsTheirNews_AndCancelsAWaitingPlayerTurn()
        {
            ScriptedSubAgentRunner runner = new() { HoldRuns = true };
            OrchestratorChatDriver driver = CreateDriver(runner,
                new OrchestratorModeOptions { Enabled = true, MaxParallelSubAgents = 2 });
            LeadTurn first = driver.BeginTurnAsync(ChatTurn("build"), CancellationToken.None).Result;
            Start(first, "builder", "one");
            Start(first, "scripter", "two");
            OrchestratorSession session = first.Session;
            driver.EndTurn(first, OrchestrationPlanState.Completed);
            runner.Held[1].SetResult(SubAgentTestSupport.Answer("two done"));
            Assert.IsTrue(driver.TryTakeWake(session.ConversationId, out LeadWake wake));
            AiTaskRequest wakeChat = ChatTurn(wake.Text);
            wakeChat.TraceId = wake.TraceId;
            LeadTurn wakeTurn = driver.BeginTurnAsync(wakeChat, CancellationToken.None).Result;
            Task<LeadTurn> waiting = driver.BeginTurnAsync(ChatTurn("hello?"), CancellationToken.None);

            bool stopped = driver.Stop(BuiltInAgentRoleIds.Creator, Player);

            Assert.IsTrue(stopped);
            Assert.IsTrue(runner.Tokens[0].IsCancellationRequested, "The working sub-agent is stopped.");
            Assert.IsTrue(wakeTurn.Token.IsCancellationRequested, "The running wake turn is stopped.");
            Assert.IsTrue(waiting.IsCanceled, "The player turn waiting for it is cancelled, not started.");
            Assert.AreEqual(SubAgentMemberState.Stopped, session.Team.Snapshot()[0].State);
            driver.EndTurn(wakeTurn, OrchestrationPlanState.Cancelled);
            Assert.IsFalse(driver.TryTakeWake(session.ConversationId, out _), "Nothing of the stopped work wakes.");
        }

        [Test]
        public void PlayerTurnWaitingForAWake_CancelledByItsCaller_NeverStarts()
        {
            OrchestratorChatDriver driver = CreateDriver(new ScriptedSubAgentRunner());
            LeadTurn first = driver.BeginTurnAsync(ChatTurn("build"), CancellationToken.None).Result;
            OrchestratorSession session = first.Session;
            driver.EndTurn(first, OrchestrationPlanState.Completed);
            session.Team.Start("builder", "Build a hut");
            Assert.IsTrue(driver.TryTakeWake(session.ConversationId, out LeadWake wake));
            AiTaskRequest wakeChat = ChatTurn(wake.Text);
            wakeChat.TraceId = wake.TraceId;
            LeadTurn wakeTurn = driver.BeginTurnAsync(wakeChat, CancellationToken.None).Result;
            using CancellationTokenSource caller = new();
            Task<LeadTurn> waiting = driver.BeginTurnAsync(ChatTurn("hello?"), caller.Token);

            caller.Cancel();
            driver.EndTurn(wakeTurn, OrchestrationPlanState.Completed);
            Task<LeadTurn> after = driver.BeginTurnAsync(ChatTurn("again"), CancellationToken.None);

            Assert.IsTrue(waiting.IsCanceled);
            Assert.IsTrue(after.IsCompleted, "The cancelled waiter left no lead turn behind.");
            driver.EndTurn(after.Result, OrchestrationPlanState.Completed);
            Assert.IsFalse(session.Team.IsLeadBusy);
        }

        [Test]
        public void ToolActivity_ReachesTheSubAgentsTranscript_ByItsTrace()
        {
            ScriptedSubAgentRunner runner = new() { HoldRuns = true };
            OrchestratorChatDriver driver = CreateDriver(runner);
            LeadTurn first = driver.BeginTurnAsync(ChatTurn("build"), CancellationToken.None).Result;
            Start(first, "builder", "one");
            string trace = runner.Requests[0].TraceId;

            int started = driver.RecordToolActivity(trace, "world_command", null);
            int finished = driver.RecordToolActivity(trace, "world_command", false);

            Assert.AreEqual(1, started);
            Assert.AreEqual(1, finished);
            Assert.AreEqual(1, first.Session.Team.Snapshot()[0].Steps);
            StringAssert.Contains("[tool] world_command failed", first.Session.Team.Read("builder-1").Output);
            driver.EndTurn(first, OrchestrationPlanState.Completed);
            driver.StopAll();
        }

        [Test]
        public void TeamInstructions_AreEmptyWithoutSubAgents_AndPlanInstructionsKeepTheirSteps()
        {
            DefaultLeadInstructionsProvider provider = new();

            string none = provider.BuildInstructions(new LeadInstructionsContext(
                Array.Empty<SubAgentDefinition>(), "", "", 8, 1, OrchestrationStyle.Team, "", "", "", ""));
            string plan = provider.BuildInstructions(new LeadInstructionsContext(
                new[] { SubAgentTestSupport.Builder() }, "delegate_task", "declare_tasks", 8, 1));

            Assert.AreEqual("", none);
            StringAssert.Contains("2. Declare: call declare_tasks", plan);
        }

        [Test]
        public void ThroughTheRealOrchestrator_StartReturnsAtOnce_TheSubAgentWorks_AndTheWakeReportsIt()
        {
            PumpedSynchronizationContext.Run(ThroughTheRealOrchestratorAsync);
        }

        private async Task ThroughTheRealOrchestratorAsync()
        {
            TeamModel model = new();
            AiOrchestrator orchestrator = CreateOrchestrator(model);
            OrchestratorChatDriver driver = new(new OrchestratorSessionRegistry(
                new OrchestratorModeOptions { Enabled = true },
                new SubAgentRegistry(new[] { SubAgentTestSupport.Builder(), SubAgentTestSupport.Scripter() }),
                orchestrator));
            List<string> due = new();
            driver.WakeDue += key => due.Add(key);

            LlmCompletionResult first = await driver.RunAsync(ChatTurn("build a tower"),
                (lead, token) => orchestrator.RunTaskResultAsync(lead, token), CancellationToken.None, r => r.Ok);

            Assert.IsTrue(first.Ok, first.Error);
            Assert.AreEqual("Started builder-1 on the tower.", first.Content);
            StringAssert.Contains("\"agent\":\"builder-1\"", model.StartResult);
            Assert.IsTrue(await SubAgentTestSupport.EventuallyAsync(() => model.SubAgentRequests.Count == 1));
            CollectionAssert.DoesNotContain(ToolNames(model.SubAgentRequests[0].Tools), SubAgentTeamToolNames.Start,
                "A sub-agent request never carries the controls.");

            model.ReleaseSubAgent.SetResult("tower built");
            Assert.IsTrue(await SubAgentTestSupport.EventuallyAsync(() => due.Count == 1), "The completion asked for a wake.");
            Assert.IsTrue(driver.TryTakeWake(due[0], out LeadWake wake));
            AiTaskRequest wakeChat = ChatTurn(wake.Text);
            wakeChat.TraceId = wake.TraceId;
            LlmCompletionResult reported = await driver.RunAsync(wakeChat,
                (lead, token) => orchestrator.RunTaskResultAsync(lead, token), CancellationToken.None, r => r.Ok);

            Assert.IsTrue(reported.Ok, reported.Error);
            Assert.AreEqual("Your tower is done.", reported.Content);
            StringAssert.Contains("builder-1 finished: tower built", model.LeadRequests[1].UserPayload);
        }

        private static string Start(LeadTurn turn, string subAgent, string task)
        {
            AgentStartTool start = (AgentStartTool)turn.Request.RequestTools[0];
            return start.ExecuteAsync(subAgent, task).Result;
        }

        private static List<string> ToolNames(IReadOnlyList<ILlmTool> tools)
        {
            List<string> names = new();
            foreach (ILlmTool tool in tools ?? Array.Empty<ILlmTool>())
            {
                names.Add(tool.Name);
            }

            return names;
        }

        private static AiTaskRequest ChatTurn(string text)
        {
            return new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Creator,
                Hint = text,
                SourceTag = OrchestratorChatDriver.ChatSourceTag,
                TraceId = "trace-" + Guid.NewGuid().ToString("N"),
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

        private static AiOrchestrator CreateOrchestrator(ILlmClient model)
        {
            return new AiOrchestrator(
                new SoloAuthorityHost(),
                model,
                new NoOpCommandSink(),
                new SessionTelemetryCollector(),
                new AiPromptComposer(new StubSystemPrompts(), new StubUserTemplates(), null),
                new InMemoryAgentMemoryStore(),
                new AgentMemoryPolicy(),
                null,
                null,
                new CoreAISettingsOptions(),
                new LocalActorIdentityProvider(SubAgentTestSupport.ActorId));
        }

        /// <summary>
        /// A scripted model in place of a provider and its tool loop: a lead request with the controls calls
        /// <c>agent_start</c> as the loop would and answers; a wake request (its user message is the news) answers with
        /// the report; a sub-agent request waits until the test releases it.
        /// </summary>
        private sealed class TeamModel : ILlmClient
        {
            public List<LlmCompletionRequest> LeadRequests { get; } = new();

            public List<LlmCompletionRequest> SubAgentRequests { get; } = new();

            public TaskCompletionSource<string> ReleaseSubAgent { get; } = new(TaskCreationOptions.None);

            public string StartResult { get; private set; } = "";

            public async Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                ILlmTool start = Find(request, SubAgentTeamToolNames.Start);
                if (start == null)
                {
                    SubAgentRequests.Add(request);
                    string answer = await ReleaseSubAgent.Task;
                    return new LlmCompletionResult { Ok = true, Content = answer, TotalTokens = 10 };
                }

                LeadRequests.Add(request);
                if ((request.UserPayload ?? "").Contains("Sub-agent news"))
                {
                    return new LlmCompletionResult { Ok = true, Content = "Your tower is done." };
                }

                AIFunction function = ((IAIFunctionLlmTool)start).CreateAIFunction();
                object raw = await function.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object>
                {
                    ["subagent"] = "builder",
                    ["task"] = "Build a stone tower at the spawn, 12 blocks high."
                }), cancellationToken);
                StartResult = SubAgentTestSupport.AsText(raw);
                return new LlmCompletionResult { Ok = true, Content = "Started builder-1 on the tower." };
            }

            private static ILlmTool Find(LlmCompletionRequest request, string name)
            {
                foreach (ILlmTool tool in request.Tools ?? Array.Empty<ILlmTool>())
                {
                    if (tool.Name == name)
                    {
                        return tool;
                    }
                }

                return null;
            }
        }

        private sealed class StubSystemPrompts : IAgentSystemPromptProvider
        {
            public bool TryGetSystemPrompt(string roleId, out string systemPrompt)
            {
                systemPrompt = "You are " + roleId + ".";
                return true;
            }
        }

        private sealed class StubUserTemplates : IAgentUserPromptTemplateProvider
        {
            public bool TryGetUserTemplate(string roleId, out string template)
            {
                template = "{hint}";
                return true;
            }
        }

        private sealed class NoOpCommandSink : IAiGameCommandSink
        {
            public void Publish(ApplyAiGameCommand command)
            {
            }
        }
    }
}
