using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Ai.SubAgents;
using NUnit.Framework;

namespace CoreAI.Core.Tests.EditMode.SubAgents
{
    /// <summary>
    /// The plan object the HUD renders, the player instruction queue (enqueue, promote, cancel) and the one
    /// status source both are read through.
    /// </summary>
    [TestFixture]
    public sealed class OrchestrationPlanAndQueueEditModeTests
    {
        private SynchronizationContext _previousSynchronizationContext;

        /// <summary>WHY: scripted runs complete inline only without a context; see the delegation fixture.</summary>
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
        public void Submit_WithoutARunningPlan_StartsOne()
        {
            OrchestratorSession session = SubAgentTestSupport.Session(new ScriptedSubAgentRunner());

            InstructionSubmission submission = session.Submit("make me a survival game", SubAgentTestSupport.Parent());

            Assert.AreEqual(InstructionDisposition.Started, submission.Disposition);
            Assert.AreSame(submission.Plan, session.ActivePlan);
            Assert.AreEqual("make me a survival game", submission.Plan.Goal);
            Assert.AreEqual(0, session.Queue.Count);
        }

        [Test]
        public async Task Submit_WhileAPlanRuns_IsQueued_AndNeverReachesTheRunningSubAgent()
        {
            ScriptedSubAgentRunner runner = new() { HoldRuns = true };
            OrchestratorSession session = SubAgentTestSupport.Session(runner);
            session.Submit("build a castle", SubAgentTestSupport.Parent());
            Task<SubAgentDelegationResult> running = SubAgentTestSupport.Delegate(session, "Build the keep");
            string hintBefore = runner.Requests[0].Hint;

            InstructionSubmission second = session.Submit("also add a moat", SubAgentTestSupport.Parent("trace-2"));

            Assert.AreEqual(InstructionDisposition.Queued, second.Disposition);
            Assert.AreEqual("also add a moat", second.Queued.Text);
            Assert.AreEqual(1, runner.Requests.Count, "A queued instruction starts nothing.");
            Assert.AreEqual(hintBefore, runner.Requests[0].Hint, "The running sub-agent's request is untouched.");
            Assert.AreEqual("build a castle", session.ActivePlan.Goal);
            Assert.AreEqual("next: also add a moat", OrchestrationStatusText.FormatNextLine(session.Current));

            runner.Held[0].TrySetResult(SubAgentTestSupport.Answer("keep built"));
            Assert.IsTrue((await running).Ok);
            Assert.AreEqual(1, runner.Requests.Count, "The queue does not run while the plan is still open.");
        }

        [Test]
        public void QueuedInstruction_StartsTheNextPlan_OnlyAfterTheCurrentOneEnds()
        {
            OrchestratorSession session = SubAgentTestSupport.Session(new ScriptedSubAgentRunner());
            session.Submit("first", SubAgentTestSupport.Parent());
            session.Submit("second", SubAgentTestSupport.Parent("trace-2"));

            Assert.IsFalse(session.TryStartNext(SubAgentTestSupport.Parent("trace-2"), out _, out _),
                "Nothing starts while a plan is running.");
            session.CompletePlan(OrchestrationPlanState.Completed);
            bool started = session.TryStartNext(SubAgentTestSupport.Parent("trace-2"),
                out OrchestrationPlan next, out QueuedInstructionSnapshot instruction);

            Assert.IsTrue(started);
            Assert.AreEqual("second", instruction.Text);
            Assert.AreEqual("second", next.Goal);
            Assert.AreSame(next, session.ActivePlan);
            Assert.AreEqual(0, session.Queue.Count);
            Assert.AreEqual("trace-2", next.Parent.LeadTraceId);
        }

        [Test]
        public void Promote_MovesAnInstructionToTheFront()
        {
            PlayerInstructionQueue queue = new(8);
            queue.Enqueue("a");
            queue.Enqueue("b");
            string c = queue.Enqueue("c").Entry.Id;

            Assert.IsTrue(queue.Promote(c));

            IReadOnlyList<QueuedInstructionSnapshot> order = queue.Snapshot();
            Assert.AreEqual("c", order[0].Text);
            Assert.AreEqual(0, order[0].Position);
            Assert.AreEqual("a", order[1].Text);
            Assert.AreEqual("b", order[2].Text);
            Assert.AreEqual("c", queue.Next.Text);
        }

        [Test]
        public void Promote_OfTheFrontOrAnUnknownInstruction_ChangesNothing()
        {
            PlayerInstructionQueue queue = new(8);
            string a = queue.Enqueue("a").Entry.Id;
            queue.Enqueue("b");
            int changes = 0;
            queue.Changed += () => changes++;

            Assert.IsTrue(queue.Promote(a));
            Assert.IsFalse(queue.Promote("q99"));
            Assert.AreEqual(0, changes);
            Assert.AreEqual("a", queue.Next.Text);
        }

        [Test]
        public void Cancel_DropsOnlyThatInstruction()
        {
            PlayerInstructionQueue queue = new(8);
            queue.Enqueue("a");
            string b = queue.Enqueue("b").Entry.Id;
            queue.Enqueue("c");

            Assert.IsTrue(queue.Cancel(b));
            Assert.IsFalse(queue.Cancel(b), "A dropped instruction cannot be dropped twice.");

            IReadOnlyList<QueuedInstructionSnapshot> order = queue.Snapshot();
            Assert.AreEqual(2, order.Count);
            Assert.AreEqual("a", order[0].Text);
            Assert.AreEqual("c", order[1].Text);
        }

        [Test]
        public void FullQueue_RefusesWithATypedReason_AndBlankIsRefused()
        {
            PlayerInstructionQueue queue = new(2);
            Assert.IsTrue(queue.Enqueue("a").Accepted);
            Assert.IsTrue(queue.Enqueue("b").Accepted);

            InstructionQueueResult full = queue.Enqueue("c");
            InstructionQueueResult blank = queue.Enqueue("   ");

            Assert.IsFalse(full.Accepted);
            Assert.AreEqual(PlayerInstructionQueue.QueueFullRefusal, full.Refusal);
            Assert.IsFalse(blank.Accepted);
            Assert.AreEqual(PlayerInstructionQueue.EmptyInstructionRefusal, blank.Refusal);
            Assert.AreEqual(2, queue.Count);
        }

        [Test]
        public void Submit_IntoAFullQueue_IsRefused()
        {
            OrchestratorSession session = SubAgentTestSupport.Session(new ScriptedSubAgentRunner(),
                new OrchestratorModeOptions { MaxQueuedInstructions = 1 });
            session.Submit("running", SubAgentTestSupport.Parent());
            session.Submit("queued", SubAgentTestSupport.Parent());

            InstructionSubmission refused = session.Submit("one too many", SubAgentTestSupport.Parent());

            Assert.AreEqual(InstructionDisposition.Refused, refused.Disposition);
            Assert.AreEqual(PlayerInstructionQueue.QueueFullRefusal, refused.Refusal);
        }

        [Test]
        public void Stop_ClearsTheQueueByDefault_AndCanKeepIt()
        {
            OrchestratorSession clearing = SubAgentTestSupport.Session(new ScriptedSubAgentRunner());
            clearing.Submit("running", SubAgentTestSupport.Parent());
            clearing.Submit("queued", SubAgentTestSupport.Parent());
            OrchestratorSession keeping = SubAgentTestSupport.Session(new ScriptedSubAgentRunner());
            keeping.Submit("running", SubAgentTestSupport.Parent());
            keeping.Submit("queued", SubAgentTestSupport.Parent());

            Assert.IsTrue(clearing.Stop());
            Assert.IsTrue(keeping.Stop(clearQueue: false));

            Assert.AreEqual(0, clearing.Queue.Count);
            Assert.AreEqual(1, keeping.Queue.Count);
            Assert.IsNull(clearing.ActivePlan);
            Assert.IsNull(keeping.ActivePlan);
        }

        [Test]
        public void Stop_WithNothingRunning_ReportsNothingStopped()
        {
            OrchestratorSession session = SubAgentTestSupport.Session(new ScriptedSubAgentRunner());

            Assert.IsFalse(session.Stop());
        }

        [Test]
        public void BeginPlan_WhileOneRuns_Throws()
        {
            OrchestratorSession session = SubAgentTestSupport.Session(new ScriptedSubAgentRunner());
            session.BeginPlan("first", SubAgentTestSupport.Parent());

            Assert.Throws<InvalidOperationException>(() => session.BeginPlan("second", SubAgentTestSupport.Parent()));
        }

        [Test]
        public async Task StatusLine_FollowsThePlan()
        {
            ScriptedSubAgentRunner runner = new() { HoldRuns = true };
            OrchestratorSession session = SubAgentTestSupport.Session(runner);
            Assert.AreEqual("", OrchestrationStatusText.FormatLine(session.Current), "No plan, no line.");
            OrchestrationPlan plan = session.BeginPlan("castle", SubAgentTestSupport.Parent());
            string walls = plan.DeclareTask("walls", "builder");
            plan.DeclareTask("towers", "builder");
            plan.DeclareTask("gate logic", "scripter");
            Assert.AreEqual("planning 0/3", OrchestrationStatusText.FormatLine(session.Current));

            Task<SubAgentDelegationResult> running = SubAgentTestSupport.Delegate(session, "Build walls", taskId: walls);
            Assert.AreEqual("builder: walls 0/3", OrchestrationStatusText.FormatLine(session.Current));
            runner.Held[0].TrySetResult(SubAgentTestSupport.Answer("Walls up.\nDetails follow."));
            await running;

            OrchestrationPlanSnapshot snapshot = session.Current.Plan;
            Assert.AreEqual(1, snapshot.CompletedTasks);
            Assert.AreEqual("Walls up.", snapshot.Tasks[0].ResultSummary);
            Assert.AreEqual("planning 1/3", OrchestrationStatusText.FormatLine(session.Current));
            session.CompletePlan(OrchestrationPlanState.Completed);
            Assert.AreEqual("done 1/3", OrchestrationStatusText.FormatLine(session.Current),
                "The last plan stays visible after it ends.");
        }

        [Test]
        public void Changed_RaisesWithAGrowingRevision()
        {
            OrchestratorSession session = SubAgentTestSupport.Session(new ScriptedSubAgentRunner());
            List<long> revisions = new();
            session.Changed += status => revisions.Add(status.Revision);

            OrchestrationPlan plan = session.BeginPlan("goal", SubAgentTestSupport.Parent());
            plan.DeclareTask("walls");
            session.Queue.Enqueue("later");

            Assert.GreaterOrEqual(revisions.Count, 3);
            for (int i = 1; i < revisions.Count; i++)
            {
                Assert.Greater(revisions[i], revisions[i - 1]);
            }
        }

        [Test]
        public async Task ThrowingObserver_DoesNotWedgeTheNextDelegation()
        {
            ScriptedSubAgentRunner runner = new();
            OrchestratorSession session = SubAgentTestSupport.Session(runner);
            session.Changed += _ => throw new InvalidOperationException("HUD bug");
            session.BeginPlan("goal", SubAgentTestSupport.Parent());

            SubAgentDelegationResult first = await SubAgentTestSupport.Delegate(session, "one");
            SubAgentDelegationResult second = await SubAgentTestSupport.Delegate(session, "two");

            Assert.IsTrue(first.Ok, first.Message);
            Assert.IsTrue(second.Ok, "The slot was released although an observer threw.");
        }

        [Test]
        public async Task RecordActivity_CountsStepsOfTheRunningTaskOnly()
        {
            ScriptedSubAgentRunner runner = new() { HoldRuns = true };
            OrchestratorSession session = SubAgentTestSupport.Session(runner);
            OrchestrationPlan plan = session.BeginPlan("goal", SubAgentTestSupport.Parent());
            Task<SubAgentDelegationResult> running = SubAgentTestSupport.Delegate(session, "Build walls");
            string trace = runner.Requests[0].TraceId;

            Assert.IsTrue(plan.TryRecordActivity(trace));
            Assert.IsTrue(plan.TryRecordActivity(trace));
            Assert.IsFalse(plan.TryRecordActivity("someone-else"));
            Assert.AreEqual(2, plan.Snapshot().Tasks[0].Steps);

            runner.Held[0].TrySetResult(SubAgentTestSupport.Answer("done"));
            await running;
            Assert.IsFalse(plan.TryRecordActivity(trace), "A finished task takes no more activity.");
        }

        [Test]
        public void DeclaredTasks_AreCancelledWhenThePlanStops()
        {
            OrchestratorSession session = SubAgentTestSupport.Session(new ScriptedSubAgentRunner());
            OrchestrationPlan plan = session.BeginPlan("goal", SubAgentTestSupport.Parent());
            plan.DeclareTask("walls");

            session.Stop();

            Assert.AreEqual(PlanTaskState.Cancelled, plan.Snapshot().Tasks[0].State);
        }

        [Test]
        public async Task StoppedPlan_RefusesNewDelegations()
        {
            ScriptedSubAgentRunner runner = new();
            OrchestratorSession session = SubAgentTestSupport.Session(runner);
            OrchestrationPlan plan = session.BeginPlan("goal", SubAgentTestSupport.Parent());
            session.Stop();

            SubAgentDelegationResult result = await SubAgentTestSupport.Delegate(session, "late");

            Assert.AreEqual(SubAgentErrorCode.PlanNotActive, result.ErrorCode,
                "A stopped plan is no longer the session's plan.");
            Assert.AreEqual(OrchestrationPlanState.Cancelled, plan.State);
            Assert.AreEqual(0, runner.Requests.Count);
        }

        [Test]
        public void SessionRegistry_KeepsOneSessionPerConversation()
        {
            using OrchestratorSessionRegistry sessions = new(new OrchestratorModeOptions(),
                new SubAgentRegistry(new[] { SubAgentTestSupport.Builder() }), new ScriptedSubAgentRunner());

            OrchestratorSession a = sessions.GetOrCreate("chat-a");
            OrchestratorSession again = sessions.GetOrCreate(" chat-a ");
            OrchestratorSession b = sessions.GetOrCreate("chat-b");
            a.BeginPlan("goal", SubAgentTestSupport.Parent());

            Assert.AreSame(a, again);
            Assert.AreNotSame(a, b);
            Assert.AreNotSame(a.DelegateTool, b.DelegateTool, "Each conversation has its own bound tool.");
            Assert.IsNull(b.ActivePlan);
            Assert.IsTrue(sessions.Remove("chat-a"));
            Assert.AreEqual(OrchestrationPlanState.Cancelled, a.LastPlan.State, "Removing a session stops it.");
            Assert.IsFalse(sessions.TryGet("chat-a", out _));
        }

        [Test]
        public void ConversationKey_FallsBackFromConversationToActorToRole()
        {
            Assert.AreEqual("chat-7",
                OrchestratorSessionRegistry.ResolveConversationKey(" chat-7 ", SubAgentTestSupport.Actor(), "Creator"));
            string actorKey =
                OrchestratorSessionRegistry.ResolveConversationKey("", SubAgentTestSupport.Actor(), "Creator");
            StringAssert.StartsWith("actor:" + SubAgentTestSupport.ActorId + "/", actorKey);
            StringAssert.EndsWith("/Creator", actorKey);
            Assert.AreEqual("Creator", OrchestratorSessionRegistry.ResolveConversationKey(null, null, "Creator"));
        }
    }
}
