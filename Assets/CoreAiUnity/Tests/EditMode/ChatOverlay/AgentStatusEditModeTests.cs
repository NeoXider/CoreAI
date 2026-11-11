using CoreAI.ChatOverlay;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.ChatOverlay
{
    /// <summary>
    /// The agent HUD: the status line follows the turn's own tool events within one tick, ignores other turns,
    /// shows retries and elapsed time, and clears on done, error and cancel.
    /// </summary>
    [TestFixture]
    public sealed class AgentStatusEditModeTests
    {
        [Test]
        public void Turn_StartsThinking()
        {
            using ChatOverlayHarness harness = new();

            harness.Controller.Submit("build a tower");

            Assert.AreEqual(AgentStatusKind.Thinking, harness.View.StatusKind);
            Assert.AreEqual("thinking", harness.View.StatusText);
        }

        [Test]
        public void ToolEventOfThisTurn_ShowsTheToolOnTheNextTick()
        {
            using ChatOverlayHarness harness = new();
            harness.Controller.Submit("build a tower");
            string trace = harness.Controller.ActiveTraceId;

            harness.ToolEvents.Start(trace, "execute_lua");
            Assert.AreEqual("thinking", harness.View.StatusText, "events apply on the tick, on the owning thread");
            harness.Controller.Tick();

            Assert.AreEqual(AgentStatusKind.RunningTool, harness.View.StatusKind);
            Assert.AreEqual("running execute_lua", harness.View.StatusText);

            harness.ToolEvents.Complete(trace, "execute_lua");
            harness.Controller.Tick();
            Assert.AreEqual("thinking", harness.View.StatusText);
        }

        [Test]
        public void ToolEventOfAnotherTurn_IsIgnored()
        {
            using ChatOverlayHarness harness = new();
            harness.Controller.Submit("build a tower");

            harness.ToolEvents.Start("someone-else", "execute_lua");
            harness.Controller.Tick();

            Assert.AreEqual("thinking", harness.View.StatusText);
        }

        [Test]
        public void ToolEvent_WhileIdle_ShowsNothing()
        {
            using ChatOverlayHarness harness = new();

            harness.ToolEvents.Start("any", "execute_lua");
            harness.Controller.Tick();

            Assert.AreEqual("", harness.View.StatusText);
            Assert.AreEqual(0, harness.View.StatusHistory.Count);
        }

        [Test]
        public void ConfiguredToolText_ReplacesTheRunningFormat()
        {
            using ChatOverlayHarness harness = new();
            harness.Controller.Submit("load my mod");

            harness.ToolEvents.Start(harness.Controller.ActiveTraceId, "manage_mods");
            harness.Controller.Tick();

            Assert.AreEqual("loading mod", harness.View.StatusText);
        }

        [Test]
        public void FailedTool_ShowsRetry_ThenTheNumberedAttempt()
        {
            using ChatOverlayHarness harness = new();
            harness.Controller.Submit("build a tower");
            string trace = harness.Controller.ActiveTraceId;
            harness.ToolEvents.Start(trace, "execute_lua");
            harness.ToolEvents.Fail(trace, "execute_lua");
            harness.Controller.Tick();

            Assert.AreEqual(AgentStatusKind.Retrying, harness.View.StatusKind);
            Assert.AreEqual("execute_lua failed, retrying", harness.View.StatusText);

            harness.ToolEvents.Start(trace, "execute_lua");
            harness.Controller.Tick();
            Assert.AreEqual("running execute_lua (retry 1)", harness.View.StatusText);
        }

        [Test]
        public void StreamingText_ShowsAnswering()
        {
            using ChatOverlayHarness harness = new();
            harness.Controller.Submit("hi");

            harness.Sender.Stream("Hello");
            harness.Controller.Tick();

            Assert.AreEqual(AgentStatusKind.Answering, harness.View.StatusKind);
        }

        [Test]
        public void ElapsedSeconds_AreShown_AndRefreshOncePerSecond()
        {
            using ChatOverlayHarness harness = new();
            harness.Controller.Submit("hi");

            harness.Clock.Advance(3.2d);
            harness.Controller.Tick();
            Assert.AreEqual("thinking \u00b7 3s", harness.View.StatusText);
            int pushes = harness.View.StatusHistory.Count;

            harness.Clock.Advance(0.3d);
            harness.Controller.Tick();
            Assert.AreEqual(pushes, harness.View.StatusHistory.Count, "no re-render inside the same second");
        }

        [Test]
        public void Status_ClearsWhenTheTurnIsDone()
        {
            using ChatOverlayHarness harness = new();
            harness.Controller.Submit("hi");
            harness.ToolEvents.Start(harness.Controller.ActiveTraceId, "execute_lua");
            harness.Controller.Tick();

            harness.Sender.Stream("Built.");
            harness.Sender.Complete();

            Assert.AreEqual("", harness.View.StatusText);
            Assert.AreEqual(AgentStatusKind.Idle, harness.View.StatusKind);
        }

        [Test]
        public void Status_ClearsWhenTheTurnFails()
        {
            using ChatOverlayHarness harness = new();
            harness.Controller.Submit("hi");
            harness.ToolEvents.Start(harness.Controller.ActiveTraceId, "execute_lua");
            harness.Controller.Tick();

            harness.Sender.Fail(ChatOverlayTurnFailureKind.Error, "provider down");

            Assert.AreEqual("", harness.View.StatusText);
        }

        [Test]
        public void Status_ClearsWhenTheTurnIsCancelled()
        {
            using ChatOverlayHarness harness = new();
            harness.Controller.Submit("hi");
            harness.ToolEvents.Start(harness.Controller.ActiveTraceId, "execute_lua");
            harness.Controller.Tick();

            harness.Controller.Submit("/stop");

            Assert.AreEqual("", harness.View.StatusText);
            Assert.IsFalse(harness.Controller.IsTurnActive);
        }

        [Test]
        public void ParallelTools_ShowTheOneStillRunning()
        {
            AgentStatusTracker tracker = new();
            tracker.BeginTurn(0d);

            tracker.ToolStarted("execute_lua");
            tracker.ToolStarted("camera_capture");
            tracker.ToolCompleted("camera_capture");

            Assert.AreEqual(AgentStatusKind.RunningTool, tracker.Kind);
            Assert.AreEqual("execute_lua", tracker.ToolName);
        }

        [Test]
        public void ReportRetry_FromAHost_UsesTheRetryFormat()
        {
            AgentStatusTracker tracker = new();
            tracker.BeginTurn(0d);

            tracker.ReportRetry("request", 2);

            Assert.AreEqual(AgentStatusKind.Retrying, tracker.Kind);
            Assert.AreEqual("request failed, retrying", tracker.Format(0.5d));
        }

        [Test]
        public void Version_ChangesOnlyOnTransitions()
        {
            AgentStatusTracker tracker = new();
            tracker.BeginTurn(0d);
            int started = tracker.Version;

            tracker.MarkThinking();
            Assert.AreEqual(started, tracker.Version);

            tracker.MarkAnswering();
            Assert.AreEqual(started + 1, tracker.Version);
        }
    }
}
