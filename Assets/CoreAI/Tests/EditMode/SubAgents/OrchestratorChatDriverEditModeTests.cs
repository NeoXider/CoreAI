using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
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
    /// Integration change I4 (and the conversation keys of I8): the chat driver turns a chat turn into a lead
    /// turn, runs a three-task plan through the real orchestrator, queues an instruction behind a running plan
    /// and runs it afterwards in its own caller's flow, and Stop ends the plan, empties the queue and cancels the
    /// waiting turn so nothing further runs.
    /// </summary>
    [TestFixture]
    public sealed class OrchestratorChatDriverEditModeTests
    {
        /// <summary>WHY one actor: a host's identity is stable, and its session id is part of the conversation key.</summary>
        private static readonly ActorContext Player = SubAgentTestSupport.Actor();

        private SynchronizationContext _previousSynchronizationContext;

        /// <summary>WHY: the orchestrator resumes on the captured context; the fixture runs it inline.</summary>
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
        public void ThreeTaskPlan_RunsThroughTheRealOrchestrator_AndCompletes()
        {
            PumpedSynchronizationContext.Run(ThreeTaskPlan_RunsThroughTheRealOrchestrator_AndCompletesAsync);
        }

        private async Task ThreeTaskPlan_RunsThroughTheRealOrchestrator_AndCompletesAsync()
        {
            ScriptedModel model = new();
            AiOrchestrator orchestrator = CreateOrchestrator(model);
            OrchestratorChatDriver driver = CreateDriver(orchestrator);

            LlmCompletionResult result = await driver.RunAsync(ChatTurn("build a castle"),
                (lead, token) => orchestrator.RunTaskResultAsync(lead, token), CancellationToken.None,
                r => r.Ok);

            Assert.IsTrue(result.Ok, result.Error);
            Assert.AreEqual("castle built", result.Content);
            OrchestratorSession session = SingleSession(driver);
            OrchestrationPlanSnapshot plan = session.Current.Plan;
            Assert.AreEqual(OrchestrationPlanState.Completed, plan.State);
            Assert.AreEqual(3, plan.TotalTasks);
            Assert.AreEqual(3, plan.CompletedTasks);
            Assert.AreEqual(3, model.SubAgentRequests.Count);
            Assert.IsEmpty(session.Queue.Snapshot());
            Assert.AreSame(session.LastPlan.Progress, model.LeadRequests[0].Progress,
                "The lead's provider request carries the plan's progress (I5).");
            Assert.IsNotNull(model.SubAgentRequests[0].Progress, "Each sub-agent run carries its own progress.");
            CollectionAssert.IsSubsetOf(new[] { DelegateTaskTool.DefaultName, DeclareTasksTool.DefaultName },
                ToolNames(model.LeadRequests[0]));
        }

        /// <summary>WHY pumped: under a main-thread context the unwinding after Stop runs on later pumps, as in the game.</summary>
        [Test]
        public void StopMidPlan_EndsThePlan_EmptiesTheQueue_AndNothingFurtherRuns()
        {
            PumpedSynchronizationContext.Run(StopMidPlanAsync);
        }

        private async Task StopMidPlanAsync()
        {
            ScriptedModel model = new() { HoldTask = 2 };
            AiOrchestrator orchestrator = CreateOrchestrator(model);
            OrchestratorChatDriver driver = CreateDriver(orchestrator);
            Task<LlmCompletionResult> first = driver.RunAsync(ChatTurn("build a castle"),
                (lead, token) => orchestrator.RunTaskResultAsync(lead, token), CancellationToken.None);
            Assert.IsTrue(await WithinAsync(() => model.SubAgentRequests.Count == 2),
                "The plan reaches its second task.");
            Task<LlmCompletionResult> queued = driver.RunAsync(ChatTurn("then add a moat"),
                (lead, token) => orchestrator.RunTaskResultAsync(lead, token), CancellationToken.None);
            OrchestratorSession session = SingleSession(driver);
            Assert.AreEqual(1, session.Queue.Snapshot().Count, "The second instruction waits in the queue.");

            bool stopped = driver.Stop(BuiltInAgentRoleIds.Creator, Player);

            Assert.IsTrue(stopped);
            Assert.IsTrue(await WithinAsync(() => first.IsCompleted && queued.IsCompleted),
                "lead " + first.Status + ", queued " + queued.Status + ", " + Describe(session.LastPlan) +
                ", held token cancelled " + model.HeldToken.IsCancellationRequested);
            Assert.IsTrue(first.IsCanceled, "The lead turn ends as stopped: " + Outcome(first));
            Assert.IsTrue(queued.IsCanceled, "The waiting turn is cancelled, not started.");
            Assert.IsEmpty(session.Queue.Snapshot());
            Assert.AreEqual(0, driver.WaitingTurns);
            Assert.AreEqual(OrchestrationPlanState.Cancelled, session.LastPlan.Snapshot().State);
            for (int i = 0; i < 50; i++)
            {
                await Task.Yield();
            }

            Assert.AreEqual(2, model.SubAgentRequests.Count, "The third task never runs.");
            Assert.AreEqual(1, model.LeadRequests.Count, "No queued plan starts after Stop.");
        }

        [Test]
        public void InstructionArrivingDuringAPlan_WaitsAndRunsAfterIt_InItsOwnTurn()
        {
            PumpedSynchronizationContext.Run(InstructionArrivingDuringAPlan_WaitsAndRunsAfterIt_InItsOwnTurnAsync);
        }

        private async Task InstructionArrivingDuringAPlan_WaitsAndRunsAfterIt_InItsOwnTurnAsync()
        {
            TaskCompletionSource<string> releaseFirst = new();
            List<string> leadHints = new();
            OrchestratorChatDriver driver = CreateDriver(new ScriptedSubAgentRunner());
            Func<AiTaskRequest, CancellationToken, Task<string>> runLead = async (lead, token) =>
            {
                leadHints.Add(lead.Hint);
                return lead.Hint == "first" ? await releaseFirst.Task : "second done";
            };

            Task<string> first = driver.RunAsync(ChatTurn("first"), runLead, CancellationToken.None);
            Task<string> second = driver.RunAsync(ChatTurn("second"), runLead, CancellationToken.None);

            Assert.IsFalse(second.IsCompleted);
            CollectionAssert.AreEqual(new[] { "first" }, leadHints, "The queued instruction does not run yet.");
            Assert.IsTrue(driver.TryGetStatusForLeadTrace("trace-second", out OrchestrationStatus waitingStatus,
                out bool waiting));
            Assert.IsTrue(waiting);
            Assert.AreEqual("first", waitingStatus.Plan.Goal);

            releaseFirst.SetResult("first done");

            Assert.AreEqual("first done", await first);
            Assert.AreEqual("second done", await second);
            CollectionAssert.AreEqual(new[] { "first", "second" }, leadHints);
            Assert.AreEqual("second", SingleSession(driver).LastPlan.Goal);
            Assert.AreEqual(OrchestrationPlanState.Completed, SingleSession(driver).LastPlan.Snapshot().State);
        }

        [Test]
        public void WaitingTurn_CancelledByItsCaller_LeavesTheQueue_AndNeverRuns()
        {
            PumpedSynchronizationContext.Run(WaitingTurn_CancelledByItsCaller_LeavesTheQueue_AndNeverRunsAsync);
        }

        private async Task WaitingTurn_CancelledByItsCaller_LeavesTheQueue_AndNeverRunsAsync()
        {
            TaskCompletionSource<string> releaseFirst = new();
            List<string> leadHints = new();
            OrchestratorChatDriver driver = CreateDriver(new ScriptedSubAgentRunner());
            Func<AiTaskRequest, CancellationToken, Task<string>> runLead = async (lead, token) =>
            {
                leadHints.Add(lead.Hint);
                return await releaseFirst.Task;
            };
            using CancellationTokenSource secondCaller = new();
            Task<string> first = driver.RunAsync(ChatTurn("first"), runLead, CancellationToken.None);
            Task<string> second = driver.RunAsync(ChatTurn("second"), runLead, secondCaller.Token);

            secondCaller.Cancel();
            releaseFirst.SetResult("first done");

            Assert.AreEqual("first done", await first);
            Assert.IsTrue(await WithinAsync(() => second.IsCompleted));
            Assert.IsTrue(second.IsCanceled);
            Assert.IsEmpty(SingleSession(driver).Queue.Snapshot());
            CollectionAssert.AreEqual(new[] { "first" }, leadHints);
        }

        [Test]
        public void ModeOff_OrANonChatTurn_PassesThroughUnchanged()
        {
            PumpedSynchronizationContext.Run(ModeOff_OrANonChatTurn_PassesThroughUnchangedAsync);
        }

        private async Task ModeOff_OrANonChatTurn_PassesThroughUnchangedAsync()
        {
            OrchestratorChatDriver driver = CreateDriver(new ScriptedSubAgentRunner());
            List<AiTaskRequest> seen = new();
            Func<AiTaskRequest, CancellationToken, Task<string>> runLead = (request, token) =>
            {
                seen.Add(request);
                return Task.FromResult("ok");
            };
            AiTaskRequest npc = ChatTurn("hello");
            npc.SourceTag = "Npc";
            AiTaskRequest subAgent = ChatTurn("hello");
            subAgent.SourceTag = SubAgentRequestFactory.SourceTag;

            driver.IsActive = false;
            AiTaskRequest offTurn = ChatTurn("hello");
            await driver.RunAsync(offTurn, runLead, CancellationToken.None);
            driver.IsActive = true;
            await driver.RunAsync(npc, runLead, CancellationToken.None);
            await driver.RunAsync(subAgent, runLead, CancellationToken.None);
            await driver.RunAsync(ChatTurn("build"), runLead, CancellationToken.None);

            Assert.AreSame(offTurn, seen[0]);
            Assert.AreSame(npc, seen[1]);
            Assert.AreSame(subAgent, seen[2]);
            Assert.IsNull(seen[0].RequestTools);
            Assert.IsNotNull(seen[3].RequestTools, "The chat turn with the mode on is a lead turn.");
            Assert.AreEqual(1, driver.Sessions.Sessions.Count, "Only the lead turn opened a session.");
        }

        [Test]
        public void FullQueue_RefusesTheInstruction()
        {
            PumpedSynchronizationContext.Run(FullQueue_RefusesTheInstructionAsync);
        }

        private async Task FullQueue_RefusesTheInstructionAsync()
        {
            TaskCompletionSource<string> hold = new();
            OrchestratorChatDriver driver = CreateDriver(new ScriptedSubAgentRunner(),
                new OrchestratorModeOptions { Enabled = true, Style = OrchestrationStyle.Plan, MaxQueuedInstructions = 1 });
            Func<AiTaskRequest, CancellationToken, Task<string>> runLead = (lead, token) => hold.Task;
            Task<string> first = driver.RunAsync(ChatTurn("first"), runLead, CancellationToken.None);
            Task<string> second = driver.RunAsync(ChatTurn("second"), runLead, CancellationToken.None);

            // WHY: Assert.ThrowsAsync blocks the editor's main thread and deadlocks the awaited continuation.
            await SubAgentTestSupport.CaptureExceptionAsync<OrchestratorInstructionRefusedException>(
                () => driver.RunAsync(ChatTurn("third"), runLead, CancellationToken.None));

            driver.Stop(BuiltInAgentRoleIds.Creator);
            hold.SetResult("done");
            await WithinAsync(() => first.IsCompleted && second.IsCompleted);
            Assert.IsTrue(second.IsCanceled);
        }

        [Test]
        public void TwoConversationsOnOneRole_RunSideBySide_AndStopMatchesItsRoleOnly()
        {
            PumpedSynchronizationContext.Run(TwoConversationsOnOneRole_RunSideBySide_AndStopMatchesItsRoleOnlyAsync);
        }

        private async Task TwoConversationsOnOneRole_RunSideBySide_AndStopMatchesItsRoleOnlyAsync()
        {
            TaskCompletionSource<string> hold = new();
            OrchestratorChatDriver driver = CreateDriver(new ScriptedSubAgentRunner());
            Func<AiTaskRequest, CancellationToken, Task<string>> runLead = async (lead, token) =>
            {
                using (token.Register(() => hold.TrySetCanceled()))
                {
                    return await hold.Task;
                }
            };
            AiTaskRequest chatA = ChatTurn("castle");
            chatA.ConversationId = "chat-a";
            AiTaskRequest chatB = ChatTurn("bridge");
            chatB.ConversationId = "chat-b";

            Task<string> a = driver.RunAsync(chatA, runLead, CancellationToken.None);
            Task<string> b = driver.RunAsync(chatB, runLead, CancellationToken.None);

            Assert.AreEqual(2, driver.Sessions.Sessions.Count, "Each conversation id has its own session.");
            Assert.AreEqual(0, driver.WaitingTurns, "Neither waits for the other.");
            Assert.IsFalse(driver.Stop(BuiltInAgentRoleIds.Programmer), "Stop for another role stops nothing.");
            Assert.IsTrue(driver.Stop(BuiltInAgentRoleIds.Creator, Player));
            await WithinAsync(() => a.IsCompleted && b.IsCompleted);
            Assert.IsTrue(a.IsCanceled && b.IsCanceled, "a " + a.Status + ", b " + b.Status);
        }

        [Test]
        public void ToolActivityOfASubAgent_CountsASteps_AndReportsProgressToBothDeadlines()
        {
            PumpedSynchronizationContext.Run(ToolActivityOfASubAgent_CountsASteps_AndReportsProgressToBothDeadlinesAsync);
        }

        private async Task ToolActivityOfASubAgent_CountsASteps_AndReportsProgressToBothDeadlinesAsync()
        {
            ScriptedSubAgentRunner runner = new() { HoldRuns = true };
            OrchestratorChatDriver driver = CreateDriver(runner);
            LeadTurn turn = await driver.BeginTurnAsync(ChatTurn("castle"), CancellationToken.None);
            Task<SubAgentDelegationResult> delegation =
                SubAgentTestSupport.Delegate(turn.Session, "Build four walls");
            Assert.IsTrue(await SubAgentTestSupport.EventuallyAsync(() => runner.Requests.Count == 1));
            AiTaskRequest subRequest = runner.Requests[0];
            long leadBefore = turn.Plan.Progress.Reports;
            long runBefore = subRequest.Progress.Reports;

            int recorded = driver.RecordActivity(subRequest.TraceId);

            Assert.AreEqual(1, recorded);
            Assert.AreEqual(1, turn.Plan.Snapshot().Tasks[0].Steps);
            Assert.Greater(subRequest.Progress.Reports, runBefore, "The sub-agent's own deadline is re-armed.");
            Assert.Greater(turn.Plan.Progress.Reports, leadBefore, "The waiting lead's deadline is re-armed.");
            Assert.AreEqual(0, driver.RecordActivity("unrelated-trace"));
            Assert.AreSame(turn.Plan.Progress, turn.Request.Progress);

            runner.Held[0].SetResult(SubAgentTestSupport.Answer("walls"));
            await delegation;
            driver.EndTurn(turn, OrchestrationPlanState.Completed);
            Assert.AreEqual(OrchestrationPlanState.Completed, turn.Plan.Snapshot().State);
        }

        [Test]
        public void StreamingLeadTurn_EndsThePlanWithTheStream()
        {
            PumpedSynchronizationContext.Run(StreamingLeadTurn_EndsThePlanWithTheStreamAsync);
        }

        private async Task StreamingLeadTurn_EndsThePlanWithTheStreamAsync()
        {
            OrchestratorChatDriver driver = CreateDriver(new ScriptedSubAgentRunner());
            List<string> texts = new();

            await foreach (LlmStreamChunk chunk in driver.RunStreamingAsync(ChatTurn("castle"), StreamTwo))
            {
                texts.Add(chunk.Text);
            }

            CollectionAssert.AreEqual(new[] { "plan", "done" }, texts);
            Assert.AreEqual(OrchestrationPlanState.Completed, SingleSession(driver).LastPlan.Snapshot().State);
        }

        [Test]
        public void StreamingLeadTurn_WithAnErrorChunk_EndsThePlanAsFailed()
        {
            PumpedSynchronizationContext.Run(StreamingLeadTurn_WithAnErrorChunk_EndsThePlanAsFailedAsync);
        }

        private async Task StreamingLeadTurn_WithAnErrorChunk_EndsThePlanAsFailedAsync()
        {
            OrchestratorChatDriver driver = CreateDriver(new ScriptedSubAgentRunner());

            await foreach (LlmStreamChunk unused in driver.RunStreamingAsync(ChatTurn("castle"), StreamError))
            {
            }

            Assert.AreEqual(OrchestrationPlanState.Failed, SingleSession(driver).LastPlan.Snapshot().State);
        }

        /// <summary>
        /// Stop wins over however the stopped lead run reports itself: a reply it finished anyway, or an error it
        /// raised while unwinding, still ends the caller's turn as stopped. The twin without Stop returns the reply.
        /// </summary>
        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void LeadRunThatEndsAfterStop_IsReportedAsStopped(bool stop, bool fault)
        {
            PumpedSynchronizationContext.Run(async () =>
            {
                TaskCompletionSource<bool> release = new();
                OrchestratorChatDriver driver = CreateDriver(new ScriptedSubAgentRunner());
                Func<AiTaskRequest, CancellationToken, Task<string>> runLead = async (lead, token) =>
                {
                    await release.Task;
                    if (fault)
                    {
                        throw new InvalidOperationException("unwound badly");
                    }

                    return "finished anyway";
                };
                Task<string> turn = driver.RunAsync(ChatTurn("castle"), runLead, CancellationToken.None);
                if (stop)
                {
                    driver.Stop(BuiltInAgentRoleIds.Creator, Player);
                }

                release.SetResult(true);
                Exception error = null;
                string reply = null;
                try
                {
                    reply = await turn;
                }
                catch (Exception caught)
                {
                    error = caught;
                }

                if (stop)
                {
                    Assert.IsInstanceOf<OperationCanceledException>(error, "Stop wins: " + error);
                    Assert.AreEqual(OrchestrationPlanState.Cancelled, SingleSession(driver).LastPlan.Snapshot().State);
                }
                else
                {
                    Assert.IsNull(error);
                    Assert.AreEqual("finished anyway", reply);
                    Assert.AreEqual(OrchestrationPlanState.Completed, SingleSession(driver).LastPlan.Snapshot().State);
                }
            });
        }

        private static async IAsyncEnumerable<LlmStreamChunk> StreamTwo(
            AiTaskRequest request,
            [EnumeratorCancellation] CancellationToken token)
        {
            await Task.Yield();
            yield return new LlmStreamChunk { Text = "plan" };
            yield return new LlmStreamChunk { Text = "done", IsDone = true };
        }

        private static async IAsyncEnumerable<LlmStreamChunk> StreamError(
            AiTaskRequest request,
            [EnumeratorCancellation] CancellationToken token)
        {
            await Task.Yield();
            yield return new LlmStreamChunk { IsDone = true, Error = "boom", ErrorCode = LlmErrorCode.ProviderError };
        }

        /// <summary>
        /// WHY real time, not a yield count: the real orchestrator unwinds a cancelled run through timers and pool
        /// hops, so how many yields that takes depends on the machine.
        /// </summary>
        private static async Task<bool> WithinAsync(Func<bool> condition, int milliseconds = 10000)
        {
            System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
            while (!condition() && clock.ElapsedMilliseconds < milliseconds)
            {
                await Task.Delay(5);
            }

            return condition();
        }

        private static string Outcome(Task<LlmCompletionResult> task)
        {
            if (task.IsFaulted)
            {
                Exception error = task.Exception.GetBaseException();
                return "faulted " + error.GetType().Name + ": " + error.Message;
            }

            return task.Status == TaskStatus.RanToCompletion
                ? "ok=" + task.Result.Ok + " code=" + task.Result.ErrorCode + " content=" + task.Result.Content
                : task.Status.ToString();
        }

        private static string Describe(OrchestrationPlan plan)
        {
            if (plan == null)
            {
                return "no plan";
            }

            OrchestrationPlanSnapshot snapshot = plan.Snapshot();
            List<string> parts = new() { "plan " + snapshot.State };
            foreach (PlanTaskSnapshot task in snapshot.Tasks)
            {
                parts.Add(task.Id + "=" + task.State);
            }

            return string.Join(" ", parts);
        }

        private static AiTaskRequest ChatTurn(string text)
        {
            return new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Creator,
                Hint = text,
                SourceTag = OrchestratorChatDriver.ChatSourceTag,
                TraceId = "trace-" + text.Split(' ')[0],
                ActorContext = Player
            };
        }

        private static OrchestratorChatDriver CreateDriver(IAiTaskResultService runner,
            OrchestratorModeOptions options = null)
        {
            OrchestratorModeOptions resolved = options ?? new OrchestratorModeOptions { Enabled = true, Style = OrchestrationStyle.Plan };
            return new OrchestratorChatDriver(new OrchestratorSessionRegistry(resolved,
                new SubAgentRegistry(new[] { SubAgentTestSupport.Builder(), SubAgentTestSupport.Scripter() }),
                runner));
        }

        private static OrchestratorSession SingleSession(OrchestratorChatDriver driver)
        {
            Assert.AreEqual(1, driver.Sessions.Sessions.Count);
            return driver.Sessions.Sessions[0];
        }

        private static List<string> ToolNames(LlmCompletionRequest request)
        {
            List<string> names = new();
            foreach (ILlmTool tool in request.Tools ?? Array.Empty<ILlmTool>())
            {
                names.Add(tool.Name);
            }

            return names;
        }

        private static AiOrchestrator CreateOrchestrator(ILlmClient model)
        {
            AgentMemoryPolicy policy = new();
            AiPromptComposer composer = new(new StubSystemPrompts(), new StubUserTemplates(), null);
            return new AiOrchestrator(
                new SoloAuthorityHost(),
                model,
                new NoOpCommandSink(),
                new SessionTelemetryCollector(),
                composer,
                new InMemoryAgentMemoryStore(),
                policy,
                null,
                null,
                new CoreAISettingsOptions(),
                new LocalActorIdentityProvider(SubAgentTestSupport.ActorId));
        }

        /// <summary>
        /// A scripted model in place of a provider and its tool loop: on a lead request it calls the request's own
        /// tools as the loop would (declare three tasks, then delegate them one by one) and reports; on a sub-agent
        /// request it answers at once, or holds task <see cref="HoldTask"/> until its run is cancelled.
        /// </summary>
        internal sealed class ScriptedModel : ILlmClient
        {
            private readonly object _lock = new();

            public int HoldTask { get; set; }

            public CancellationToken HeldToken { get; private set; }

            public List<LlmCompletionRequest> LeadRequests { get; } = new();

            public List<LlmCompletionRequest> SubAgentRequests { get; } = new();

            public async Task<LlmCompletionResult> CompleteAsync(
                LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                ILlmTool delegateTool = Find(request, DelegateTaskTool.DefaultName);
                if (delegateTool == null)
                {
                    int ordinal;
                    lock (_lock)
                    {
                        SubAgentRequests.Add(request);
                        ordinal = SubAgentRequests.Count;
                    }

                    if (ordinal == HoldTask)
                    {
                        HeldToken = cancellationToken;
                        await Task.Delay(Timeout.Infinite, cancellationToken);
                    }

                    return new LlmCompletionResult { Ok = true, Content = "done " + ordinal, TotalTokens = 10 };
                }

                lock (_lock)
                {
                    LeadRequests.Add(request);
                }

                AIFunction declare = ((IAIFunctionLlmTool)Find(request, DeclareTasksTool.DefaultName)).CreateAIFunction();
                await declare.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object>
                {
                    ["tasks"] = "builder: Build four walls\nbuilder: Add two towers\nscripter: Open the gate on touch"
                }), cancellationToken);
                AIFunction delegation = ((IAIFunctionLlmTool)delegateTool).CreateAIFunction();
                string[] subAgents = { "builder", "builder", "scripter" };
                string[] tasks = { "Build four walls", "Add two towers", "Open the gate on touch" };
                for (int i = 0; i < tasks.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await delegation.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object>
                    {
                        ["subagent"] = subAgents[i],
                        ["task"] = tasks[i],
                        ["task_id"] = "t" + (i + 1)
                    }), cancellationToken);
                }

                cancellationToken.ThrowIfCancellationRequested();
                return new LlmCompletionResult { Ok = true, Content = "castle built" };
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
