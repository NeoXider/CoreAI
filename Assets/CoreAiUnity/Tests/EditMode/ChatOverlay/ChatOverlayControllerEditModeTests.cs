using System.Collections.Generic;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.ChatOverlay;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.ChatOverlay
{
    /// <summary>
    /// The engine-free core of the in-world chat, driven only through fakes of its replaceable blocks (view,
    /// sender, clock, tool events): routing before the model, the input focus state, the streamed line, the
    /// fade and the agent HUD.
    /// </summary>
    [TestFixture]
    public sealed class ChatOverlayControllerEditModeTests
    {
        private static List<ChatFeedLine> LinesOf(ChatOverlayHarness harness, ChatFeedLineKind kind)
        {
            List<ChatFeedLine> lines = new();
            foreach (ChatFeedLine line in harness.Controller.Feed.Lines)
            {
                if (line.Kind == kind)
                {
                    lines.Add(line);
                }
            }

            return lines;
        }

        [Test]
        public void OpenInput_OwnsTheKeyboardAtOnce()
        {
            using ChatOverlayHarness harness = new();
            List<bool> changes = new();
            harness.Controller.InputOpenChanged += changes.Add;

            harness.Controller.OpenInput();

            Assert.IsTrue(harness.Controller.IsInputOpen);
            Assert.IsTrue(harness.View.InputOpen);
            CollectionAssert.AreEqual(new[] { true }, changes);
        }

        [Test]
        public void ClosedInput_DoesNotOwnTheKeyboard()
        {
            using ChatOverlayHarness harness = new();
            List<bool> changes = new();
            harness.Controller.InputOpenChanged += changes.Add;

            harness.Controller.Tick();

            Assert.IsFalse(harness.Controller.IsInputOpen);
            Assert.IsFalse(harness.View.InputOpen);
            CollectionAssert.IsEmpty(changes);
        }

        [Test]
        public void CloseInput_KeepsTheKeyboardUntilTheNextTick()
        {
            using ChatOverlayHarness harness = new();
            List<bool> changes = new();
            harness.Controller.OpenInput();
            harness.Controller.InputOpenChanged += changes.Add;

            harness.Controller.CloseInput();

            Assert.IsFalse(harness.View.InputOpen, "the field hides at once");
            Assert.IsFalse(harness.Controller.IsInputVisible);
            Assert.IsTrue(harness.Controller.IsInputOpen, "the closing key must not reach the game this frame");
            CollectionAssert.IsEmpty(changes);

            harness.Controller.Tick();

            Assert.IsFalse(harness.Controller.IsInputOpen);
            CollectionAssert.AreEqual(new[] { false }, changes);
        }

        [Test]
        public void ViewCloseRequest_ClosesTheInputOnly()
        {
            using ChatOverlayHarness harness = new();
            harness.Controller.OpenInput("half typed");

            harness.View.RaiseCloseRequested();

            Assert.IsFalse(harness.View.InputOpen);
            Assert.AreEqual(0, harness.Sender.Requests.Count, "closing never sends");
            Assert.AreEqual(0, harness.Controller.Feed.Lines.Count);
        }

        [Test]
        public void OpenCommandInput_PrefillsThePrefix()
        {
            using ChatOverlayHarness harness = new();

            harness.Controller.OpenCommandInput();

            Assert.AreEqual("/", harness.View.Prefill);
        }

        [Test]
        public void Submit_PlainText_IsSentToTheActiveRole_AndEchoed()
        {
            using ChatOverlayHarness harness = new();
            harness.Controller.OpenInput();

            harness.View.RaiseSubmitted("  put a red block in front of me ");

            Assert.AreEqual(1, harness.Sender.Requests.Count);
            ChatOverlayTurnRequest request = harness.Sender.Requests[0];
            Assert.AreEqual("put a red block in front of me", request.Text);
            Assert.AreEqual(BuiltInAgentRoleIds.SmartChat, request.RoleId);
            Assert.IsFalse(string.IsNullOrEmpty(request.TraceId));
            Assert.IsNull(request.StreamingOverride);
            Assert.AreEqual(request.TraceId, harness.Controller.ActiveTraceId);
            List<ChatFeedLine> player = LinesOf(harness, ChatFeedLineKind.Player);
            Assert.AreEqual(1, player.Count);
            Assert.AreEqual("You", player[0].Speaker);
            Assert.AreEqual("put a red block in front of me", player[0].Text);
            Assert.IsFalse(harness.View.InputOpen, "Enter closes the input");
        }

        [Test]
        public void Submit_UnknownCommand_AnswersLocally_AndSendsNothing()
        {
            using ChatOverlayHarness harness = new();

            harness.Controller.Submit("/teleport home");

            Assert.AreEqual(0, harness.Sender.Requests.Count);
            Assert.IsFalse(harness.Controller.IsTurnActive);
            ChatFeedLine reply = harness.LastLine;
            Assert.AreEqual(ChatFeedLineKind.Error, reply.Kind);
            StringAssert.Contains("/teleport", reply.Text);
            StringAssert.Contains("/help", reply.Text);
            Assert.AreEqual(0, LinesOf(harness, ChatFeedLineKind.Player).Count, "a command is not echoed as speech");
        }

        [Test]
        public void Submit_BarePrefix_AnswersLocally_AndSendsNothing()
        {
            using ChatOverlayHarness harness = new();

            harness.Controller.Submit("/");

            Assert.AreEqual(0, harness.Sender.Requests.Count);
            Assert.AreEqual(ChatFeedLineKind.Error, harness.LastLine.Kind);
        }

        [Test]
        public void Submit_EscapedSlash_IsSentAsText()
        {
            using ChatOverlayHarness harness = new();

            harness.Controller.Submit("//help me");

            Assert.AreEqual(1, harness.Sender.Requests.Count);
            Assert.AreEqual("/help me", harness.Sender.Requests[0].Text);
        }

        [Test]
        public void Submit_EmptyLine_OnlyClosesTheInput()
        {
            using ChatOverlayHarness harness = new();
            harness.Controller.OpenInput();

            harness.View.RaiseSubmitted("   ");

            Assert.IsFalse(harness.View.InputOpen);
            Assert.AreEqual(0, harness.Sender.Requests.Count);
            Assert.AreEqual(0, harness.Controller.Feed.Lines.Count);
        }

        [Test]
        public void Submit_TooLong_IsCutToTheInputLimit()
        {
            using ChatOverlayHarness harness = new(new ChatOverlayOptions { MaxInputLength = 5 });

            harness.Controller.Submit("abcdefghij");

            Assert.AreEqual("abcde", harness.Sender.Requests[0].Text);
        }

        [Test]
        public void Submit_CommandThatThrows_IsReportedAndNothingIsSent()
        {
            using ChatOverlayHarness harness = new();
            harness.Registry.Register(new ChatCommand("explode", "Throws.",
                _ => throw new System.InvalidOperationException("kaboom")));

            harness.Controller.Submit("/explode");

            Assert.AreEqual(0, harness.Sender.Requests.Count);
            Assert.AreEqual(ChatFeedLineKind.Error, harness.LastLine.Kind);
            StringAssert.Contains("kaboom", harness.LastLine.Text);
        }

        [Test]
        public void Submit_CommandAskingForTheModel_SendsItsText()
        {
            using ChatOverlayHarness harness = new();
            harness.Registry.Register(new ChatCommand("build", "Ask the builder.",
                ctx => ChatCommandResult.SendToModel("Build " + ctx.Invocation.ArgumentText,
                    BuiltInAgentRoleIds.Programmer), "<what>"));

            harness.Controller.Submit("/build a tower");

            Assert.AreEqual(1, harness.Sender.Requests.Count);
            Assert.AreEqual("Build a tower", harness.Sender.Requests[0].Text);
            Assert.AreEqual(BuiltInAgentRoleIds.Programmer, harness.Sender.Requests[0].RoleId);
        }

        [Test]
        public void Submit_WhileAnswering_IsRefusedLocally()
        {
            using ChatOverlayHarness harness = new();
            harness.Controller.Submit("first");

            harness.Controller.Submit("second");

            Assert.AreEqual(1, harness.Sender.Requests.Count);
            Assert.AreEqual(ChatFeedLineKind.Error, harness.LastLine.Kind);
            StringAssert.Contains("/stop", harness.LastLine.Text);
        }

        [Test]
        public void Submit_WithoutService_SaysSo_AndSendsNothing()
        {
            using ChatOverlayHarness harness = new();
            harness.Sender.IsAvailable = false;

            harness.Controller.Submit("hello");

            Assert.AreEqual(0, harness.Sender.Requests.Count);
            Assert.AreEqual(ChatFeedLineKind.Error, harness.LastLine.Kind);
            Assert.AreEqual(harness.Options.ServiceUnavailableText, harness.LastLine.Text);
        }

        [Test]
        public void StreamedAnswer_GrowsOneLineInPlace()
        {
            using ChatOverlayHarness harness = new();
            harness.Controller.Submit("hi");

            harness.Sender.Stream("Hel");
            harness.Controller.Tick();
            List<ChatFeedLine> assistant = LinesOf(harness, ChatFeedLineKind.Assistant);
            Assert.AreEqual(1, assistant.Count);
            long id = assistant[0].Id;
            Assert.AreEqual("Hel", harness.View.RenderedText[id]);
            Assert.IsTrue(assistant[0].IsStreaming);

            harness.Sender.Stream("lo");
            harness.Sender.Stream(" there");
            harness.Controller.Tick();
            harness.Sender.Complete();
            harness.Controller.Tick();

            assistant = LinesOf(harness, ChatFeedLineKind.Assistant);
            Assert.AreEqual(1, assistant.Count, "one feed line for one streamed answer");
            Assert.AreEqual(id, assistant[0].Id);
            Assert.AreEqual("Hello there", harness.View.RenderedText[id]);
            Assert.AreEqual("AI", assistant[0].Speaker);
            Assert.IsFalse(assistant[0].IsStreaming);
            Assert.IsFalse(harness.Controller.IsTurnActive);
        }

        [Test]
        public void StreamedChunks_InOneFrame_RenderOnce()
        {
            using ChatOverlayHarness harness = new();
            harness.Controller.Submit("hi");
            harness.Sender.Stream("a");
            harness.Controller.Tick();
            int before = harness.View.UpdateCalls;

            harness.Sender.Stream("b");
            harness.Sender.Stream("c");
            harness.Sender.Stream("d");
            harness.Controller.Tick();

            Assert.AreEqual(before + 1, harness.View.UpdateCalls);
        }

        [Test]
        public void NewMessageInTheSameStream_OpensANewLine()
        {
            using ChatOverlayHarness harness = new();
            harness.Controller.Submit("hi");

            harness.Sender.Stream("Placing the block.");
            harness.Sender.Stream("Done, it is in front of you.", startsNewMessage: true);
            harness.Sender.Complete();

            List<ChatFeedLine> assistant = LinesOf(harness, ChatFeedLineKind.Assistant);
            Assert.AreEqual(2, assistant.Count);
            Assert.AreEqual("Placing the block.", assistant[0].Text);
            Assert.AreEqual("Done, it is in front of you.", assistant[1].Text);
        }

        [Test]
        public void ThinkBlocks_NeverReachTheFeed()
        {
            using ChatOverlayHarness harness = new();
            harness.Controller.Submit("hi");

            harness.Sender.Stream("<think>plan the tower</think>");
            harness.Sender.Stream("Here it is.");
            harness.Sender.Complete();

            List<ChatFeedLine> assistant = LinesOf(harness, ChatFeedLineKind.Assistant);
            Assert.AreEqual(1, assistant.Count);
            Assert.AreEqual("Here it is.", assistant[0].Text);
        }

        [Test]
        public void ToolProgressHintChunk_AddsNoText()
        {
            using ChatOverlayHarness harness = new();
            harness.Controller.Submit("hi");

            harness.Sender.Observer.OnChunk(new LlmStreamChunk
            {
                Text = "{\"tool\":\"execute_lua\"}",
                BufferedStreamingNoToolBinding = true,
                BufferedStreamingUseToolProgressHint = true
            });
            harness.Sender.Stream("Built.");
            harness.Sender.Complete();

            List<ChatFeedLine> assistant = LinesOf(harness, ChatFeedLineKind.Assistant);
            Assert.AreEqual(1, assistant.Count);
            Assert.AreEqual("Built.", assistant[0].Text);
        }

        [Test]
        public void AnswerWithoutVisibleText_SaysNoAnswer()
        {
            using ChatOverlayHarness harness = new();
            harness.Controller.Submit("hi");

            harness.Sender.Complete();

            Assert.AreEqual(0, LinesOf(harness, ChatFeedLineKind.Assistant).Count);
            Assert.AreEqual(harness.Options.NoResponseText, harness.LastLine.Text);
            Assert.IsFalse(harness.Controller.IsTurnActive);
        }

        [Test]
        public void FailureChunk_EndsTheTurnWithAnErrorLine_AndCancelsTheRequest()
        {
            using ChatOverlayHarness harness = new();
            harness.Controller.Submit("hi");
            harness.Sender.Stream("Partial");

            harness.Sender.Observer.OnChunk(new LlmStreamChunk
            {
                IsDone = true,
                Error = "HTTP 429",
                ErrorCode = LlmErrorCode.RateLimited,
                RetryAfterSeconds = 3
            });
            harness.Sender.Observer.OnCompleted();

            Assert.IsFalse(harness.Controller.IsTurnActive);
            Assert.AreEqual(1, harness.Sender.LastTurn.CancelCalls);
            Assert.AreEqual(ChatFeedLineKind.Error, harness.LastLine.Kind);
            Assert.AreEqual(LlmErrorPresentation.ForErrorCode(LlmErrorCode.RateLimited, 3), harness.LastLine.Text);
            Assert.AreEqual("Partial", LinesOf(harness, ChatFeedLineKind.Assistant)[0].Text,
                "text already shown stays");
            Assert.AreEqual(0, LinesOf(harness, ChatFeedLineKind.System).Count, "OnCompleted after the failure is ignored");
        }

        [Test]
        public void FailedTurn_ShowsTheSentence()
        {
            using ChatOverlayHarness harness = new(new ChatOverlayOptions { ErrorPrefix = "Error: " });
            harness.Controller.Submit("hi");

            harness.Sender.Fail(ChatOverlayTurnFailureKind.Error, "The assistant is unavailable.");

            Assert.AreEqual("Error: The assistant is unavailable.", harness.LastLine.Text);
            Assert.AreEqual(ChatFeedLineKind.Error, harness.LastLine.Kind);
        }

        [Test]
        public void CancelledTurn_SaysStopped()
        {
            using ChatOverlayHarness harness = new();
            harness.Controller.Submit("hi");

            harness.Sender.Fail(ChatOverlayTurnFailureKind.Cancelled);

            Assert.AreEqual(ChatFeedLineKind.System, harness.LastLine.Kind);
            Assert.AreEqual(harness.Options.StoppedText, harness.LastLine.Text);
        }

        [Test]
        public void StopActiveTurn_CancelsTheRequest_AndStopsTheAgent()
        {
            using ChatOverlayHarness harness = new();
            harness.Controller.Submit("hi");
            harness.Sender.Stream("Work");

            bool stopped = harness.Controller.StopActiveTurn();
            harness.Sender.Fail(ChatOverlayTurnFailureKind.Cancelled);

            Assert.IsTrue(stopped);
            Assert.IsFalse(harness.Controller.IsTurnActive);
            Assert.AreEqual(1, harness.Sender.LastTurn.CancelCalls);
            CollectionAssert.AreEqual(new[] { BuiltInAgentRoleIds.SmartChat }, harness.Sender.StoppedRoles);
            Assert.AreEqual(1, LinesOf(harness, ChatFeedLineKind.System).Count, "one Stopped line, not two");
            Assert.IsFalse(LinesOf(harness, ChatFeedLineKind.Assistant)[0].IsStreaming);
        }

        [Test]
        public void StopActiveTurn_WhenIdle_ReturnsFalse()
        {
            using ChatOverlayHarness harness = new();

            Assert.IsFalse(harness.Controller.StopActiveTurn());
            CollectionAssert.IsEmpty(harness.Sender.StoppedRoles);
        }

        [Test]
        public void SenderThatFailsSynchronously_EndsTheTurnBeforeSendReturns()
        {
            using ChatOverlayHarness harness = new();
            harness.Sender.OnSend = (_, observer) => observer.OnFailed(
                new ChatOverlayTurnFailure(ChatOverlayTurnFailureKind.Error, "offline"));

            harness.Controller.Submit("hi");

            Assert.IsFalse(harness.Controller.IsTurnActive);
            Assert.AreEqual(1, harness.Sender.LastTurn.CancelCalls, "the handle that arrives after the end is cancelled");
            Assert.AreEqual("offline", harness.LastLine.Text);
            Assert.AreEqual("", harness.View.StatusText);
        }

        [Test]
        public void Lines_FadeAfterTheDelay_AndReturnWhileTheInputIsOpen()
        {
            using ChatOverlayHarness harness = new(new ChatOverlayOptions { FadeDelaySeconds = 10d });
            harness.Controller.PostSystemMessage("Welcome to the world.");
            long id = harness.LastLine.Id;

            harness.Clock.Advance(9.5d);
            harness.Controller.Tick();
            Assert.IsTrue(harness.View.Visible[id]);

            harness.Clock.Advance(1d);
            harness.Controller.Tick();
            Assert.IsFalse(harness.View.Visible[id], "faded after 10 s");

            harness.Controller.OpenInput();
            Assert.IsTrue(harness.View.Visible[id], "back while the input is open");

            harness.Controller.CloseInput();
            Assert.IsFalse(harness.View.Visible[id], "faded again once closed");
        }

        [Test]
        public void ZeroFadeDelay_NeverFades()
        {
            using ChatOverlayHarness harness = new(new ChatOverlayOptions { FadeDelaySeconds = 0d });
            harness.Controller.PostSystemMessage("Stays.");
            long id = harness.LastLine.Id;

            harness.Clock.Advance(3600d);
            harness.Controller.Tick();

            Assert.IsTrue(harness.View.Visible[id]);
        }

        [Test]
        public void StreamingLine_DoesNotFadeUntilItCompletes()
        {
            using ChatOverlayHarness harness = new(new ChatOverlayOptions { FadeDelaySeconds = 2d });
            harness.Controller.Submit("hi");
            harness.Sender.Stream("Thinking hard");
            long id = LinesOf(harness, ChatFeedLineKind.Assistant)[0].Id;

            harness.Clock.Advance(30d);
            harness.Controller.Tick();
            Assert.IsTrue(harness.View.Visible[id]);

            harness.Sender.Complete();
            harness.Clock.Advance(2.5d);
            harness.Controller.Tick();
            Assert.IsFalse(harness.View.Visible[id]);
        }

        [Test]
        public void Feed_DropsTheOldestLineAtTheCap()
        {
            using ChatOverlayHarness harness = new(new ChatOverlayOptions { MaxFeedLines = 2 });
            harness.Controller.PostSystemMessage("one");
            harness.Controller.PostSystemMessage("two");

            harness.Controller.PostSystemMessage("three");

            Assert.AreEqual(2, harness.Controller.Feed.Lines.Count);
            Assert.AreEqual("two", harness.Controller.Feed.Lines[0].Text);
            Assert.AreEqual(1, harness.View.Removed.Count);
            Assert.AreEqual("one", harness.View.Removed[0].Text);
        }

        [Test]
        public void PostMessage_WithSpeaker_AddsAnNpcLine()
        {
            using ChatOverlayHarness harness = new();

            harness.Controller.PostMessage("Guard", "Halt! Who goes there?");

            Assert.AreEqual("Guard", harness.LastLine.Speaker);
            Assert.AreEqual(ChatFeedLineKind.System, harness.LastLine.Kind);
            Assert.AreSame(harness.LastLine, harness.View.Added[harness.View.Added.Count - 1]);
            Assert.AreEqual(0, harness.Sender.Requests.Count, "a posted line never reaches the model");
        }

        [Test]
        public async Task PostMessage_FromAnotherThread_IsShownOnTheNextTick()
        {
            using ChatOverlayHarness harness = new();

            await Task.Run(() => harness.Controller.PostMessage("Merchant", "Fresh bread!"));

            Assert.AreEqual(0, harness.Controller.Feed.Lines.Count);
            harness.Controller.Tick();
            Assert.AreEqual("Fresh bread!", harness.LastLine.Text);
        }

        [Test]
        public void AssistantSpeaker_CanNameTheRole()
        {
            using ChatOverlayHarness harness = new(new ChatOverlayOptions { AssistantSpeakerName = "AI ({role})" });
            harness.Controller.Submit("hi");

            harness.Sender.Stream("Hello");

            Assert.AreEqual("AI (SmartChat)", LinesOf(harness, ChatFeedLineKind.Assistant)[0].Speaker);
        }

        [Test]
        public void Dispose_DetachesFromTheView_AndAbandonsTheTurnSilently()
        {
            ChatOverlayHarness harness = new();
            harness.Controller.Submit("hi");
            int lines = harness.Controller.Feed.Lines.Count;

            harness.Dispose();
            harness.Sender.Stream("late");

            Assert.AreEqual(0, harness.View.SubmittedSubscribers);
            Assert.AreEqual(1, harness.Sender.LastTurn.CancelCalls);
            Assert.AreEqual(lines, harness.Controller.Feed.Lines.Count, "no Stopped line and no late text");
        }
    }
}
