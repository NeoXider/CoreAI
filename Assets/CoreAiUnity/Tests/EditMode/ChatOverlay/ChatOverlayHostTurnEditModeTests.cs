using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.ChatOverlay;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.ChatOverlay
{
    /// <summary>
    /// Host turns of the in-world chat (<see cref="ChatOverlayController.TryStartHostTurn"/>): a turn the host asks for
    /// takes the chat's one turn slot, shows its announcement instead of a player line, keeps the answer out of the
    /// feed unless asked, follows the status line, is stopped by <c>/stop</c> and reports its end exactly once.
    /// </summary>
    [TestFixture]
    public sealed class ChatOverlayHostTurnEditModeTests
    {
        private static ChatOverlayHostTurnRequest Request(List<ChatOverlayHostTurnResult> ended, bool showAnswer = false)
        {
            return new ChatOverlayHostTurnRequest
            {
                Text = "fix the tower",
                RoleId = BuiltInAgentRoleIds.Programmer,
                Announcement = "AI is fixing 'tower' (try 1 of 3). /stop cancels.",
                ShowAnswer = showAnswer,
                Ended = ended.Add
            };
        }

        [Test]
        public void HostTurn_ShowsTheAnnouncement_SendsTheText_AndKeepsTheAnswerOutOfTheFeed()
        {
            using ChatOverlayHarness harness = new();
            List<ChatOverlayHostTurnResult> ended = new();

            Assert.IsTrue(harness.Controller.TryStartHostTurn(Request(ended)));
            Assert.IsTrue(harness.Controller.IsTurnActive);
            harness.Sender.Stream("I fixed the nil check.");
            harness.Sender.Complete();
            harness.Controller.Tick();

            Assert.AreEqual("fix the tower", harness.Sender.Requests[0].Text);
            Assert.AreEqual(BuiltInAgentRoleIds.Programmer, harness.Sender.Requests[0].RoleId);
            Assert.AreEqual(1, harness.Controller.Feed.Lines.Count, "only the announcement");
            Assert.AreEqual(ChatFeedLineKind.System, harness.Controller.Feed.Lines[0].Kind);
            StringAssert.StartsWith("AI is fixing 'tower'", harness.Controller.Feed.Lines[0].Text);
            Assert.AreEqual(1, ended.Count);
            Assert.AreEqual(ChatOverlayHostTurnEnd.Completed, ended[0].End);
            Assert.IsFalse(harness.Controller.IsTurnActive);
        }

        [Test]
        public void HostTurn_ReportsTheVisibleAnswer_WithoutReasoning_EvenWhenTheFeedStaysQuiet()
        {
            using ChatOverlayHarness harness = new();
            List<ChatOverlayHostTurnResult> ended = new();
            harness.Controller.TryStartHostTurn(Request(ended));

            harness.Sender.Stream("<think>the part is nil</think>Changed: the handler ");
            harness.Sender.Stream("now checks that the part exists.");
            harness.Sender.Complete();

            Assert.AreEqual(1, harness.Controller.Feed.Lines.Count, "the answer is not shown");
            Assert.AreEqual("Changed: the handler now checks that the part exists.", ended[0].Answer);
        }

        [Test]
        public void HostTurn_ThatFails_ReportsNoAnswer_AndALongAnswerIsBounded()
        {
            using ChatOverlayHarness harness = new();
            List<ChatOverlayHostTurnResult> ended = new();
            harness.Controller.TryStartHostTurn(Request(ended));
            harness.Sender.Stream("Changed: half of it");
            harness.Sender.Fail(ChatOverlayTurnFailureKind.Error);
            harness.Controller.TryStartHostTurn(Request(ended));
            harness.Sender.Stream(new string('x', ChatOverlayHostTurnResult.MaximumAnswerCharacters + 50));
            harness.Sender.Complete();

            Assert.AreEqual(ChatOverlayHostTurnEnd.Failed, ended[0].End);
            Assert.AreEqual("", ended[0].Answer, "a failed turn has no answer to report");
            Assert.AreEqual(ChatOverlayHostTurnResult.MaximumAnswerCharacters, ended[1].Answer.Length);
        }

        [Test]
        public void HostTurn_PassesItsHistoryMode_ToTheSender_AndATypedTurnKeepsTheRoleDefault()
        {
            using ChatOverlayHarness harness = new();
            List<ChatOverlayHostTurnResult> ended = new();
            ChatOverlayHostTurnRequest request = Request(ended);
            request.HistoryMode = AiTaskHistoryMode.None;

            harness.Controller.TryStartHostTurn(request);
            harness.Sender.Complete();
            harness.Controller.Submit("build a tower");

            Assert.AreEqual(AiTaskHistoryMode.None, harness.Sender.Requests[0].HistoryMode,
                "a repair report the player never typed stays out of the role's history");
            Assert.AreEqual(AiTaskHistoryMode.RoleDefault, harness.Sender.Requests[1].HistoryMode);
        }

        [Test]
        public void HostTurn_FollowsTheStatusLine_ByItsTraceId()
        {
            using ChatOverlayHarness harness = new();
            List<ChatOverlayHostTurnResult> ended = new();
            ChatOverlayHostTurnRequest request = Request(ended);
            request.TraceId = "repair-1";

            harness.Controller.TryStartHostTurn(request);
            harness.ToolEvents.Start("repair-1", "manage_mods");
            harness.Controller.Tick();

            Assert.AreEqual("repair-1", harness.Controller.ActiveTraceId);
            StringAssert.Contains("loading mod", harness.View.StatusText);
        }

        [Test]
        public void Stop_EndsAHostTurn_AsCancelled()
        {
            using ChatOverlayHarness harness = new();
            List<ChatOverlayHostTurnResult> ended = new();
            harness.Controller.TryStartHostTurn(Request(ended));

            harness.Controller.Submit("/stop");
            harness.Sender.Fail(ChatOverlayTurnFailureKind.Cancelled);

            Assert.AreEqual(1, harness.Sender.LastTurn.CancelCalls, "the request is cancelled");
            Assert.AreEqual(1, ended.Count);
            Assert.AreEqual(ChatOverlayHostTurnEnd.Cancelled, ended[0].End);
            Assert.IsFalse(harness.Controller.IsTurnActive);
        }

        [Test]
        public void Stop_ThenALateCompletion_IsStillReportedAsCancelled()
        {
            using ChatOverlayHarness harness = new();
            List<ChatOverlayHostTurnResult> ended = new();
            harness.Controller.TryStartHostTurn(Request(ended));

            harness.Controller.StopActiveTurn();
            harness.Sender.Complete();

            Assert.AreEqual(1, ended.Count);
            Assert.AreEqual(ChatOverlayHostTurnEnd.Cancelled, ended[0].End);
        }

        [Test]
        public void PlayerLine_WhileAHostTurnRuns_GetsTheBusyNotice()
        {
            using ChatOverlayHarness harness = new();
            harness.Controller.TryStartHostTurn(Request(new List<ChatOverlayHostTurnResult>()));

            harness.Controller.Submit("build a castle");

            Assert.AreEqual(1, harness.Sender.Requests.Count, "the player's line did not start a second turn");
            StringAssert.Contains("still answering", harness.LastLine.Text);
        }

        [Test]
        public void SecondHostTurn_WhileOneRuns_IsRefused_WithoutACallback()
        {
            using ChatOverlayHarness harness = new();
            List<ChatOverlayHostTurnResult> ended = new();
            harness.Controller.TryStartHostTurn(Request(ended));

            Assert.IsFalse(harness.Controller.TryStartHostTurn(Request(ended)));
            Assert.IsEmpty(ended);
        }

        [Test]
        public void HostTurn_IsRefused_WithNoAiConnected_OrNoText()
        {
            using ChatOverlayHarness harness = new();
            List<ChatOverlayHostTurnResult> ended = new();
            ChatOverlayHostTurnRequest empty = Request(ended);
            empty.Text = " ";

            Assert.IsFalse(harness.Controller.TryStartHostTurn(empty));
            harness.Sender.IsAvailable = false;
            Assert.IsFalse(harness.Controller.TryStartHostTurn(Request(ended)));
            Assert.IsEmpty(harness.Controller.Feed.Lines);
            Assert.IsEmpty(ended);
        }

        [Test]
        public void FailedHostTurn_ReportsTheFailure_WithoutAFeedLine()
        {
            using ChatOverlayHarness harness = new();
            List<ChatOverlayHostTurnResult> ended = new();
            harness.Controller.TryStartHostTurn(Request(ended));

            harness.Sender.Observer.OnChunk(new LlmStreamChunk { IsDone = true, ErrorCode = LlmErrorCode.BackendUnavailable });

            Assert.AreEqual(1, ended.Count);
            Assert.AreEqual(ChatOverlayHostTurnEnd.Failed, ended[0].End);
            Assert.AreEqual(LlmErrorCode.BackendUnavailable, ended[0].ErrorCode);
            Assert.AreEqual(1, harness.Controller.Feed.Lines.Count, "the host reports the outcome itself");
            Assert.IsFalse(harness.Controller.IsTurnActive);
        }

        [Test]
        public void ShowAnswer_StreamsTheAnswer_LikeATypedTurn()
        {
            using ChatOverlayHarness harness = new();
            List<ChatOverlayHostTurnResult> ended = new();
            harness.Controller.TryStartHostTurn(Request(ended, true));

            harness.Sender.Stream("Fixed.");
            harness.Sender.Complete();
            harness.Controller.Tick();

            Assert.AreEqual(ChatFeedLineKind.Assistant, harness.LastLine.Kind);
            Assert.AreEqual("Fixed.", harness.LastLine.Text);
            Assert.AreEqual(ChatOverlayHostTurnEnd.Completed, ended[0].End);
        }
    }
}
