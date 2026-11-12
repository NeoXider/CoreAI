using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Ai.SubAgents;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace CoreAI.Core.Tests.EditMode.SubAgents
{
    /// <summary>
    /// Team-style sub-agents (<see cref="SubAgentTeam"/>): each control's contract (start, send, read, stop), the
    /// lifecycle (start, read while running, message, finish, continue, stop returning the answer), the budget
    /// window, parallelism, the member limit, the deadline, no recursion and the player's Stop.
    /// </summary>
    [TestFixture]
    public sealed class SubAgentTeamEditModeTests
    {
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
        public void Start_ReturnsAtOnceWithAName_WhileTheRunIsStillWorking()
        {
            ScriptedSubAgentRunner runner = new() { HoldRuns = true };
            SubAgentTeam team = Team(runner);
            team.SetLeadContext(SubAgentTestSupport.Parent());

            SubAgentControlResult started = team.Start("builder", "Build a stone tower at the spawn", "tower");

            Assert.IsTrue(started.Ok, started.Message);
            Assert.AreEqual("builder-1", started.Agent);
            Assert.AreEqual(SubAgentMemberState.Working, started.State, "The start did not wait for the run.");
            Assert.AreEqual(1, runner.Requests.Count);
            AiTaskRequest request = runner.Requests[0];
            Assert.AreEqual("Build a stone tower at the spawn", request.Hint, "The task is the whole brief.");
            Assert.AreEqual(SubAgentRequestFactory.SourceTag, request.SourceTag);
            Assert.AreEqual(AiTaskHistoryMode.None, request.HistoryMode, "A sub-agent starts from a clean slate.");
            Assert.AreEqual(SubAgentTestSupport.ActorId, request.ActorContext?.ActorId, "It runs as the player.");
            StringAssert.StartsWith(SubAgentTestSupport.LeadTrace + ".sub.builder-1.1", request.TraceId);
            Assert.AreEqual("tower", team.Snapshot()[0].Label);
            Assert.AreEqual("{\"ok\":true,\"agent\":\"builder-1\",\"state\":\"working\"}", started.ToToolResultJson());

            runner.Held[0].SetResult(SubAgentTestSupport.Answer("tower built"));
        }

        [Test]
        public void Start_RefusesAnUnknownTypeOrABlankTask_AndNamesTheTypes()
        {
            SubAgentTeam team = Team(new ScriptedSubAgentRunner());

            SubAgentControlResult unknown = team.Start("painter", "Paint the walls");
            SubAgentControlResult blank = team.Start("builder", "  ");

            Assert.AreEqual(SubAgentErrorCode.UnknownSubAgent, unknown.ErrorCode);
            StringAssert.Contains("builder, scripter", unknown.Message);
            Assert.AreEqual(SubAgentErrorCode.InvalidArguments, blank.ErrorCode);
            StringAssert.StartsWith("{\"ok\":false,\"error\":\"unknown_subagent\"", unknown.ToToolResultJson());
            Assert.IsEmpty(team.Snapshot());
        }

        [Test]
        public void Lifecycle_StartReadMessageFinishContinueStop()
        {
            ScriptedSubAgentRunner runner = new() { HoldRuns = true };
            SubAgentTeam team = Team(runner);
            List<SubAgentNotice> notified = new();
            team.NoticeQueued += notice => notified.Add(notice);

            team.Start("builder", "Build four walls");
            string trace = team.Snapshot()[0].TraceId;
            Assert.IsTrue(team.TryRecordActivity(trace, "world_command", null), "A started step re-arms the run.");
            Assert.IsTrue(team.TryRecordActivity(trace, "world_command", true));
            SubAgentControlResult whileRunning = team.Read("builder-1");
            SubAgentControlResult message = team.Send("builder-1", "Make them two blocks high");
            Assert.AreEqual(SubAgentDelivery.Queued, message.Delivery, "A working member gets it at its next turn.");
            Assert.AreEqual(1, message.PendingMessages);

            runner.Held[0].SetResult(SubAgentTestSupport.Answer("four walls up"));

            Assert.AreEqual(2, runner.Requests.Count, "The waiting message started the next turn at once.");
            Assert.IsEmpty(notified, "No notice while the member still has work.");
            string continuation = runner.Requests[1].Hint;
            StringAssert.Contains("[lead, task] Build four walls", continuation);
            StringAssert.Contains("[you] four walls up", continuation);
            StringAssert.EndsWith("New message from the lead:\nMake them two blocks high", continuation);
            Assert.AreNotEqual(runner.Requests[0].TraceId, runner.Requests[1].TraceId);

            runner.Held[1].SetResult(SubAgentTestSupport.Answer("walls are two blocks high"));

            SubAgentMemberSnapshot finished = team.Snapshot()[0];
            Assert.AreEqual(SubAgentMemberState.Finished, finished.State);
            Assert.AreEqual(2, finished.Turns);
            Assert.AreEqual(1, notified.Count, "One notice when it went idle.");
            Assert.AreEqual("builder-1 finished: walls are two blocks high", notified[0].Text);
            Assert.AreEqual(1, team.PendingNotices);

            SubAgentControlResult resumed = team.Send("builder-1", "Add a door on the south side");
            Assert.AreEqual(SubAgentDelivery.Started, resumed.Delivery, "An idle member continues at once.");
            Assert.AreEqual(3, runner.Requests.Count);
            StringAssert.Contains("[lead] Make them two blocks high", runner.Requests[2].Hint);
            StringAssert.Contains("[you] walls are two blocks high", runner.Requests[2].Hint);

            SubAgentControlResult stopped = team.Stop("builder-1");

            Assert.IsTrue(stopped.Ok);
            Assert.AreEqual(SubAgentMemberState.Working, stopped.WasState);
            Assert.AreEqual(SubAgentMemberState.Stopped, stopped.State);
            Assert.AreEqual("walls are two blocks high", stopped.Answer, "A stop returns what it had.");
            Assert.IsTrue(runner.Tokens[2].IsCancellationRequested, "The running turn is cancelled.");
            Assert.AreEqual(1, notified.Count, "A stop asked for by the lead sends no notice.");
            Assert.AreEqual(SubAgentMemberState.Stopped, team.Snapshot()[0].State);

            StringAssert.Contains("[tool] world_command ok", whileRunning.Output);
            Assert.AreEqual(SubAgentMemberState.Working, whileRunning.State, "Reading did not end it.");
        }

        [Test]
        public void Read_ReturnsOnlyOutputAfterTheCursor_AndKeepsTheNewestWithinTheBound()
        {
            ScriptedSubAgentRunner runner = new()
            {
                OnRun = (request, token) => Task.FromResult(SubAgentTestSupport.Answer(new string('a', 300) + " done"))
            };
            SubAgentTeam team = Team(runner, new OrchestratorModeOptions { MaxReadChars = 120 });
            team.Start("builder", "Build");

            SubAgentControlResult first = team.Read("builder-1");
            SubAgentControlResult again = team.Read("builder-1", first.Cursor);
            SubAgentControlResult list = team.Read();

            Assert.IsTrue(first.Output.Length <= 120, first.Output);
            StringAssert.EndsWith("done", first.Output, "The newest characters are kept.");
            Assert.Greater(first.Skipped, 0);
            Assert.AreEqual("", again.Output, "Nothing new after the cursor.");
            Assert.AreEqual(first.Cursor, again.Cursor);
            Assert.AreEqual(SubAgentControlAction.List, list.Action);
            JObject json = JObject.Parse(list.ToToolResultJson());
            Assert.AreEqual("builder-1", (string)json["agents"][0]["agent"]);
            Assert.AreEqual("finished", (string)json["agents"][0]["state"]);
        }

        [Test]
        public void Stop_OfAnIdleMember_ChangesNothing_AndReturnsItsFinalAnswer()
        {
            SubAgentTeam team = Team(new ScriptedSubAgentRunner());
            team.Start("builder", "Build a hut");

            SubAgentControlResult stop = team.Stop("builder");

            Assert.IsTrue(stop.Ok, "The type name finds the only member of that type.");
            Assert.AreEqual(SubAgentMemberState.Finished, stop.WasState);
            Assert.AreEqual(SubAgentMemberState.Finished, stop.State);
            Assert.AreEqual("answer to: Build a hut", stop.Answer);
            JObject json = JObject.Parse(stop.ToToolResultJson());
            Assert.AreEqual("finished", (string)json["was"]);
        }

        [Test]
        public void Stop_DropsWaitingMessages_RefundsTheirRuns_AndAMessageLaterContinuesWithoutThem()
        {
            ScriptedSubAgentRunner runner = new() { HoldRuns = true };
            SubAgentTeam team = Team(runner, new OrchestratorModeOptions { MaxSubAgentCallsPerPlan = 3 });
            team.Start("builder", "Build walls");
            team.Send("builder-1", "dropped message");
            Assert.AreEqual(2, team.Budget.CallsStarted, "The extra turn was reserved when the message arrived.");

            team.Stop("builder-1");
            Assert.AreEqual(1, team.Budget.CallsStarted, "The unstarted turn was refunded.");

            team.Send("builder-1", "Build only one wall");
            runner.Held[1].SetResult(SubAgentTestSupport.Answer("one wall"));

            StringAssert.DoesNotContain("dropped message", runner.Requests[1].Hint);
            StringAssert.Contains("[stopped by the lead]", runner.Requests[1].Hint);
            Assert.AreEqual(SubAgentMemberState.Finished, team.Snapshot()[0].State);
        }

        [Test]
        public void Send_RefusesAnUnknownAgent_AndAFullInbox()
        {
            ScriptedSubAgentRunner runner = new() { HoldRuns = true };
            SubAgentTeam team = Team(runner, new OrchestratorModeOptions { MaxInboxMessages = 1 });
            team.Start("builder", "Build");

            SubAgentControlResult unknown = team.Send("builder-9", "hello");
            SubAgentControlResult first = team.Send("builder-1", "one");
            SubAgentControlResult full = team.Send("builder-1", "two");

            Assert.AreEqual(SubAgentErrorCode.UnknownAgent, unknown.ErrorCode);
            StringAssert.Contains("builder-1", unknown.Message, "The refusal names the members.");
            Assert.IsTrue(first.Ok);
            Assert.AreEqual(SubAgentErrorCode.InboxFull, full.ErrorCode);
            Assert.AreEqual("inbox_full", SubAgentErrorCodes.ToWireName(full.ErrorCode));
            team.StopAll();
        }

        [Test]
        public void BudgetWindow_RefusesRunsPastTheCap_AndTheNextPlayerMessageOpensANewOne()
        {
            ScriptedSubAgentRunner runner = new() { HoldRuns = true };
            SubAgentTeam team = Team(runner, new OrchestratorModeOptions { MaxSubAgentCallsPerPlan = 2 });

            Assert.IsTrue(team.Start("builder", "one").Ok);
            Assert.IsTrue(team.Send("builder-1", "and more").Ok);
            SubAgentControlResult refused = team.Start("scripter", "two");
            team.BeginInstructionWindow();
            SubAgentControlResult opened = team.Start("scripter", "two");

            Assert.AreEqual(SubAgentErrorCode.CallBudgetExhausted, refused.ErrorCode);
            StringAssert.Contains("next message opens a new budget", refused.Message);
            Assert.IsTrue(opened.Ok);
            team.StopAll();
        }

        [Test]
        public void TokenBudget_RefusesTheNextRunOnceReportedTokensReachTheCap()
        {
            ScriptedSubAgentRunner runner = new()
            {
                OnRun = (request, token) => Task.FromResult(SubAgentTestSupport.Answer("ok", 150))
            };
            SubAgentTeam team = Team(runner, new OrchestratorModeOptions { MaxTokensPerPlan = 100 });

            SubAgentControlResult first = team.Start("builder", "one");
            SubAgentControlResult second = team.Start("builder", "two");

            Assert.IsTrue(first.Ok, "The run that crosses the cap finishes.");
            Assert.AreEqual(SubAgentErrorCode.TokenBudgetExhausted, second.ErrorCode);
            Assert.AreEqual(150, team.Budget.TokensUsed);
        }

        [Test]
        public void Parallelism_OneSlot_RunsTheSecondMemberAfterTheFirst()
        {
            ScriptedSubAgentRunner runner = new() { HoldRuns = true };
            SubAgentTeam team = Team(runner);

            team.Start("builder", "one");
            SubAgentControlResult second = team.Start("scripter", "two");

            Assert.AreEqual(SubAgentMemberState.Queued, second.State, "It waits for the one model slot.");
            Assert.AreEqual(1, runner.Requests.Count);
            Assert.AreEqual("scripter-1 started", OrchestrationStatusText.FormatTeamLine(team.Snapshot())
                .Split(new[] { "; " }, StringSplitOptions.None)[1]);

            runner.Held[0].SetResult(SubAgentTestSupport.Answer("one done"));

            Assert.AreEqual(2, runner.Requests.Count, "The slot went to the waiting member.");
            Assert.AreEqual(1, runner.MaxActive);
            team.StopAll();
        }

        [Test]
        public void MemberLimit_RefusesWhileAllAreBusy_AndForgetsTheOldestIdleOne()
        {
            ScriptedSubAgentRunner runner = new() { HoldRuns = true };
            SubAgentTeam team = Team(runner, new OrchestratorModeOptions { MaxTeamMembers = 2, MaxParallelSubAgents = 2 });
            team.Start("builder", "one");
            team.Start("builder", "two");

            SubAgentControlResult refused = team.Start("builder", "three");
            runner.Held[0].SetResult(SubAgentTestSupport.Answer("one done"));
            SubAgentControlResult admitted = team.Start("builder", "three");

            Assert.AreEqual(SubAgentErrorCode.AgentLimitReached, refused.ErrorCode);
            Assert.IsTrue(admitted.Ok);
            Assert.AreEqual("builder-3", admitted.Agent);
            CollectionAssert.AreEqual(new[] { "builder-2", "builder-3" },
                new[] { team.Snapshot()[0].Name, team.Snapshot()[1].Name });
            team.StopAll();
        }

        [Test]
        public void Deadline_FailsTheTurn_AndTheLeadGetsAFailureNotice()
        {
            ScriptedSubAgentRunner runner = new() { HoldRuns = true };
            ManualDelayMarshaler marshaler = new();
            SubAgentTeam team = new("conversation-1", new OrchestratorModeOptions(),
                new SubAgentRegistry(new[] { SubAgentTestSupport.Builder(deadline: TimeSpan.FromSeconds(30)) }),
                runner, marshaler);

            team.Start("builder", "Build slowly");
            Assert.AreEqual(30000, marshaler.Delays[0].Milliseconds, "The deadline is a host delay, not a timer.");
            marshaler.ElapseAll();

            SubAgentMemberSnapshot member = team.Snapshot()[0];
            Assert.AreEqual(SubAgentMemberState.Failed, member.State);
            StringAssert.Contains("did not finish within 30 s", member.LastError);
            IReadOnlyList<SubAgentNotice> notices = team.TakeNotices();
            Assert.AreEqual(1, notices.Count);
            StringAssert.StartsWith("builder-1 failed:", notices[0].Text);
            Assert.IsFalse(notices[0].Ok);
        }

        [Test]
        public void NoRecursion_AMembersRunCannotUseAnyControl_OrThePlanDelegateTool()
        {
            List<SubAgentControlResult> attempts = new();
            SubAgentDelegationResult delegation = null;
            OrchestratorSession session = null;
            ScriptedSubAgentRunner runner = new()
            {
                OnRun = async (request, token) =>
                {
                    attempts.Add(session.Team.Start("builder", "nested"));
                    attempts.Add(session.Team.Send("builder-1", "nested"));
                    attempts.Add(session.Team.Read());
                    attempts.Add(session.Team.Stop("builder-1"));
                    delegation = await SubAgentTestSupport.Delegate(session, "nested");
                    return SubAgentTestSupport.Answer("did it myself");
                }
            };
            session = SubAgentTestSupport.Session(runner);

            SubAgentControlResult outer = session.Team.Start("builder", "Build");

            Assert.IsTrue(outer.Ok);
            Assert.AreEqual(4, attempts.Count);
            foreach (SubAgentControlResult attempt in attempts)
            {
                Assert.AreEqual(SubAgentErrorCode.NestedDelegation, attempt.ErrorCode, attempt.Action.ToString());
            }

            Assert.AreEqual(SubAgentErrorCode.NestedDelegation, delegation.ErrorCode);
            Assert.IsFalse(session.Team.IsInsideMemberRun, "The lead's own flow is not marked.");
            Assert.AreEqual(1, runner.Requests.Count);
        }

        [Test]
        public void NoRecursion_TheTeamToolsNeverReachASubAgentRequest()
        {
            SubAgentDefinition sneaky = new("helper", "Builder", "Helps.", new[] { "world_command", "renamed_start" });
            AiTaskRequest request = SubAgentRequestFactory.Create(sneaky, "task", "", SubAgentTestSupport.Parent(),
                "plan", "helper-1", 1, DelegateTaskTool.DefaultName, new[] { "renamed_start" });

            CollectionAssert.AreEqual(new[] { "world_command" }, request.AllowedToolNames);
            ArgumentException refused = Assert.Throws<ArgumentException>(() =>
                new SubAgentDefinition("helper", "Builder", "Helps.", new[] { SubAgentTeamToolNames.Start }));
            StringAssert.Contains(SubAgentTeamToolNames.Start, refused.Message);
        }

        [Test]
        public void StopAll_StopsEveryMember_DropsTheNotices_AndCancelsTheLeadToken()
        {
            ScriptedSubAgentRunner runner = new() { HoldRuns = true };
            SubAgentTeam team = Team(runner, new OrchestratorModeOptions { MaxParallelSubAgents = 2 });
            team.Start("builder", "one");
            team.Start("scripter", "two");
            CancellationToken lead = team.LeadToken;
            runner.Held[1].SetResult(SubAgentTestSupport.Answer("two done"));
            Assert.AreEqual(1, team.PendingNotices);

            bool stopped = team.StopAll();

            Assert.IsTrue(stopped);
            Assert.IsTrue(runner.Tokens[0].IsCancellationRequested);
            Assert.AreEqual(0, team.PendingNotices, "Nothing of the stopped work reaches the lead.");
            Assert.IsTrue(lead.IsCancellationRequested, "A lead turn linked to the team stops too.");
            Assert.IsFalse(team.LeadToken.IsCancellationRequested, "The next lead turn gets a live token.");
            Assert.AreEqual(SubAgentMemberState.Stopped, team.Snapshot()[0].State);
            Assert.AreEqual(SubAgentMemberState.Finished, team.Snapshot()[1].State, "A finished member stays finished.");
            Assert.IsFalse(team.StopAll(), "A second Stop finds nothing.");
        }

        [Test]
        public void Tools_ReturnTheContractJson_AndCarryTheWaitingNoticesOnce()
        {
            ScriptedSubAgentRunner runner = new();
            OrchestratorSession session = SubAgentTestSupport.Session(runner);
            AgentStartTool start = (AgentStartTool)session.TeamTools[0];
            AgentReadTool read = (AgentReadTool)session.TeamTools[2];

            JObject started = JObject.Parse(start.ExecuteAsync("builder", "Build a hut").Result);
            JObject listed = JObject.Parse(read.ExecuteAsync().Result);

            Assert.AreEqual(true, (bool)started["ok"]);
            Assert.AreEqual("builder-1", (string)started["agent"]);
            Assert.AreEqual("finished", (string)started["state"], "This runner answers at once.");
            Assert.AreEqual("builder-1 finished: answer to: Build a hut", (string)started["news"][0],
                "The notice rides on the next tool result.");
            Assert.IsNull(listed["news"], "A notice is delivered once.");
            Assert.AreEqual(SubAgentTeamToolNames.Start, start.Name);
            StringAssert.Contains("- builder: Builds structures in the world.", start.Description);
            Assert.IsFalse(start.IsMutating, "Several starts of one batch may run together.");
        }

        /// <summary>
        /// WebGL-safe scheduling: a start made with the host context cleared (a tool body continuing inline after a
        /// MEAI await) does not run there; the turn starts on the context the team was created on and resumes there
        /// after its await, with no thread and no timer, under a single-threaded pumped context like a player's.
        /// </summary>
        [Test]
        public void Scheduling_AStartWithTheHostContextCleared_RunsAndResumesOnTheHostContext()
        {
            PumpedSynchronizationContext.Run(SchedulingAsync);
        }

        private async Task SchedulingAsync()
        {
            SynchronizationContext home = SynchronizationContext.Current;
            List<SynchronizationContext> runContexts = new();
            TaskCompletionSource<LlmCompletionResult> model = new(TaskCreationOptions.None);
            ScriptedSubAgentRunner runner = new()
            {
                OnRun = (request, token) =>
                {
                    runContexts.Add(SynchronizationContext.Current);
                    return model.Task;
                }
            };
            SubAgentTeam team = Team(runner);
            SynchronizationContext noticeContext = null;
            team.NoticeQueued += _ => noticeContext = SynchronizationContext.Current;

            SubAgentControlResult started;
            SynchronizationContext.SetSynchronizationContext(null);
            try
            {
                started = team.Start("builder", "Build");
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(home);
            }

            Assert.AreEqual(SubAgentMemberState.Queued, started.State, "The turn was handed to the host context.");
            Assert.AreEqual(0, runner.Requests.Count);
            Assert.IsTrue(await SubAgentTestSupport.EventuallyAsync(() => runner.Requests.Count == 1));
            Assert.AreSame(home, runContexts[0], "The run started on the host context.");

            SynchronizationContext.SetSynchronizationContext(null);
            try
            {
                model.SetResult(SubAgentTestSupport.Answer("built"));
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(home);
            }

            Assert.IsTrue(await SubAgentTestSupport.EventuallyAsync(() => noticeContext != null));
            Assert.AreSame(home, noticeContext, "After its await the member resumed on the host context.");
            Assert.AreEqual(SubAgentMemberState.Finished, team.Snapshot()[0].State);
        }

        private static SubAgentTeam Team(ScriptedSubAgentRunner runner, OrchestratorModeOptions options = null)
        {
            return new SubAgentTeam("conversation-1", options ?? new OrchestratorModeOptions(),
                new SubAgentRegistry(new[] { SubAgentTestSupport.Builder(), SubAgentTestSupport.Scripter() }),
                runner);
        }
    }
}
