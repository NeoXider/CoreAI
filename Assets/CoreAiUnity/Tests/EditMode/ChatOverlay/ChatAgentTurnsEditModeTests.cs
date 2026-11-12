using System;
using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.ChatOverlay;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.ChatOverlay
{
    /// <summary>
    /// The player's agent for a chat drawn elsewhere (<see cref="ChatAgentTurns"/>): one turn at a time through the
    /// overlay's sender, visible text piece by piece, the status as a stream of changes (thinking, composing a tool
    /// call, running a tool, answering, retrying, the step count and the token estimate), tool-only turns, failures
    /// and cancel. Plus the two pieces the overlay gained for it: the surface switch and the line router, and the
    /// composing state and token count in the overlay's own status line.
    /// </summary>
    [TestFixture]
    public sealed class ChatAgentTurnsEditModeTests
    {
        private ScriptedTurnSender _sender;
        private FakeToolEventSource _tools;
        private ManualChatClock _clock;
        private ChatAgentSelector _agents;
        private ChatAgentTurns _turns;

        [SetUp]
        public void SetUp()
        {
            _sender = new ScriptedTurnSender();
            _tools = new FakeToolEventSource();
            _clock = new ManualChatClock();
            _agents = new ChatAgentSelector(BuiltInAgentRoleIds.Programmer,
                () => new[] { BuiltInAgentRoleIds.Programmer, BuiltInAgentRoleIds.SmartChat });
            _turns = new ChatAgentTurns(_sender, _agents, new FakeModelSelector(), _tools, null, null,
                () => _clock.NowSeconds);
        }

        [TearDown]
        public void TearDown()
        {
            _turns.Dispose();
        }

        private sealed class Recorder
        {
            public readonly List<(string Text, bool StartsNew)> Pieces = new();
            public readonly List<ChatAgentStatus> Statuses = new();
            public readonly List<ChatAgentTurnResult> Ends = new();

            public Recorder(ChatAgentTurn turn)
            {
                turn.TextAdded += (text, startsNew) => Pieces.Add((text, startsNew));
                turn.StatusChanged += Statuses.Add;
                turn.Ended += Ends.Add;
            }

            public ChatAgentStatus Last => Statuses[Statuses.Count - 1];
        }

        [Test]
        public void Start_SendsTheLineAsTheActiveRole_AndTheStatusIsThinking()
        {
            ChatAgentTurn turn = _turns.Start("  build a tower  ");

            Assert.AreEqual(1, _sender.Requests.Count);
            Assert.AreEqual("build a tower", _sender.Requests[0].Text);
            Assert.AreEqual(BuiltInAgentRoleIds.Programmer, _sender.Requests[0].RoleId);
            Assert.AreEqual(turn.TraceId, _sender.Requests[0].TraceId, "tool events find the turn by this id");
            Assert.IsTrue(_turns.IsBusy);
            Assert.AreEqual(AgentStatusKind.Thinking, turn.Status.State);
        }

        [Test]
        public void RoleOverride_AnswersThisTurnOnly()
        {
            _turns.Start("hi", BuiltInAgentRoleIds.SmartChat);

            Assert.AreEqual(BuiltInAgentRoleIds.SmartChat, _sender.Requests[0].RoleId);
            Assert.AreEqual(BuiltInAgentRoleIds.Programmer, _turns.ActiveRoleId);
        }

        [Test]
        public void Text_ArrivesPieceByPiece_WithoutThinkBlocks_AndTheFirstPieceTrimmed()
        {
            Recorder recorder = new(_turns.Start("hi"));

            _sender.Stream("<think>plan</think>  Hel");
            _sender.Stream("lo");
            _sender.Stream("Next part.", true);
            _sender.Complete();

            Assert.AreEqual("Hel", recorder.Pieces[0].Text);
            Assert.IsFalse(recorder.Pieces[0].StartsNew, "the first piece never starts a new message");
            Assert.AreEqual("lo", recorder.Pieces[1].Text);
            Assert.AreEqual(("Next part.", true), recorder.Pieces[2]);
            Assert.AreEqual(1, recorder.Ends.Count);
            Assert.AreEqual(ChatAgentTurnOutcome.Completed, recorder.Ends[0].Outcome);
            Assert.IsTrue(recorder.Ends[0].AnyText);
            Assert.IsFalse(_turns.IsBusy, "the slot is free again");
        }

        [Test]
        public void Status_FollowsThisTurnsToolEvents_OnTheTick_AndCountsSteps()
        {
            ChatAgentTurn turn = _turns.Start("build");
            Recorder recorder = new(turn);

            _tools.Start("someone-else", "execute_lua");
            _tools.Start(turn.TraceId, "execute_lua");
            Assert.AreEqual(0, recorder.Statuses.Count, "events apply on the tick, on the chat's thread");
            _turns.Tick();

            Assert.AreEqual(AgentStatusKind.RunningTool, recorder.Last.State);
            Assert.AreEqual("execute_lua", recorder.Last.Tool);
            Assert.AreEqual(1, recorder.Last.Step);

            _tools.Fail(turn.TraceId, "execute_lua");
            _turns.Tick();
            Assert.AreEqual(AgentStatusKind.Retrying, recorder.Last.State);
            Assert.AreEqual(1, recorder.Last.Attempt);

            _tools.Start(turn.TraceId, "execute_lua");
            _turns.Tick();
            Assert.AreEqual(AgentStatusKind.RunningTool, recorder.Last.State);
            Assert.AreEqual(2, recorder.Last.Step);
            Assert.AreEqual(1, recorder.Last.Attempt, "the repeated attempt is numbered");
        }

        [Test]
        public void ComposingChunk_ShowsTheToolBeingWritten_WithItsSize()
        {
            Recorder recorder = new(_turns.Start("build"));

            _sender.Observer.OnChunk(new LlmStreamChunk
            {
                GenerationInProgress = true, ComposingToolName = "execute_lua", ComposingToolCharacters = 512
            });

            Assert.AreEqual(AgentStatusKind.ComposingTool, recorder.Last.State);
            Assert.AreEqual("execute_lua", recorder.Last.Tool);
            Assert.AreEqual(512, recorder.Last.Characters);
            Assert.Greater(recorder.Last.Tokens, 0, "the arguments count towards the output tokens");
        }

        [Test]
        public void StreamedText_GrowsTheTokenEstimate()
        {
            Recorder recorder = new(_turns.Start("hi"));

            _sender.Stream(new string('a', 400));

            Assert.AreEqual(AgentStatusKind.Answering, recorder.Last.State);
            Assert.AreEqual(100, recorder.Last.Tokens, "four characters a token by default");
        }

        [Test]
        public void ToolOnlyTurn_IsHeldBack_AndReportedAsDoneOrFailed()
        {
            Recorder done = new(_turns.Start("place a block"));
            _sender.Stream("Tool call completed: execute_lua.");
            _sender.Complete();

            Assert.AreEqual(0, done.Pieces.Count, "the orchestrator's sentence is not answer text");
            Assert.AreEqual(ChatAgentToolOnly.Done, done.Ends[0].ToolOnly);
            Assert.AreEqual("Tool call completed: execute_lua.", done.Ends[0].ToolSummary);

            Recorder failed = new(_turns.Start("place a block"));
            _sender.Stream("Tool calls failed: execute_lua: syntax error.");
            _sender.Complete();
            Assert.AreEqual(ChatAgentToolOnly.Failed, failed.Ends[0].ToolOnly);
        }

        [Test]
        public void Negative_HeldToolSummary_ThenAFailure_EndsAsTheFailureAlone()
        {
            Recorder recorder = new(_turns.Start("place a block"));
            _sender.Stream("Tool call completed: execute_lua.");
            _sender.Fail(ChatOverlayTurnFailureKind.Timeout, "The assistant took too long to answer.");

            Assert.AreEqual(ChatAgentTurnOutcome.TimedOut, recorder.Ends[0].Outcome);
            Assert.AreEqual("The assistant took too long to answer.", recorder.Ends[0].Message);
            Assert.AreEqual(ChatAgentToolOnly.None, recorder.Ends[0].ToolOnly);
        }

        [Test]
        public void FailureChunk_EndsTheTurn_AndCancelsTheRequest()
        {
            Recorder recorder = new(_turns.Start("hi"));

            _sender.Observer.OnChunk(new LlmStreamChunk { ErrorCode = LlmErrorCode.ProviderError, Error = "bad" });

            Assert.AreEqual(ChatAgentTurnOutcome.Failed, recorder.Ends[0].Outcome);
            Assert.IsNotEmpty(recorder.Ends[0].Message);
            Assert.AreEqual(1, _sender.LastTurn.CancelCalls);
        }

        [Test]
        public void Cancel_StopsTheRequest_AsksTheAgentToStop_AndFreesTheSlot()
        {
            ChatAgentTurn turn = _turns.Start("hi");
            Recorder recorder = new(turn);

            Assert.IsTrue(_turns.StopCurrent());

            Assert.AreEqual(ChatAgentTurnOutcome.Cancelled, recorder.Ends[0].Outcome);
            Assert.AreEqual(1, _sender.LastTurn.CancelCalls);
            CollectionAssert.AreEqual(new[] { BuiltInAgentRoleIds.Programmer }, _sender.StoppedRoles);
            Assert.IsFalse(_turns.IsBusy);
            Assert.IsFalse(_turns.StopCurrent(), "twin: nothing runs now");

            _sender.Stream("late");
            _sender.Complete();
            Assert.AreEqual(1, recorder.Ends.Count, "a stopped turn ends once; later callbacks are ignored");
            Assert.AreEqual(0, recorder.Pieces.Count);
        }

        [Test]
        public void Negative_ASecondStartWhileBusy_IsRefused()
        {
            _turns.Start("one");

            Assert.Throws<InvalidOperationException>(() => _turns.Start("two"));
            Assert.AreEqual(1, _sender.Requests.Count);
        }

        [Test]
        public void Negative_NoAi_StartIsRefused_AndAnEmptyLineToo()
        {
            Assert.Throws<ArgumentException>(() => _turns.Start("   "));
            _sender.IsAvailable = false;

            Assert.IsFalse(_turns.IsAvailable);
            Assert.Throws<InvalidOperationException>(() => _turns.Start("hi"));
            Assert.AreEqual(0, _sender.Requests.Count);
        }

        [Test]
        public void TrySelectRole_SwitchesTheSharedSelector()
        {
            Assert.IsTrue(_turns.TrySelectRole("smartchat", out string selected));
            Assert.AreEqual(BuiltInAgentRoleIds.SmartChat, selected);
            Assert.AreEqual(BuiltInAgentRoleIds.SmartChat, _agents.ActiveRoleId);
            Assert.IsFalse(_turns.TrySelectRole("nobody", out _));
        }

        [Test]
        public void Overlay_ComposingChunk_ShowsWritingAScript()
        {
            using ChatOverlayHarness harness = new();
            harness.Controller.Submit("build a tower");

            harness.Sender.Observer.OnChunk(new LlmStreamChunk
            {
                GenerationInProgress = true, ComposingToolName = "execute_lua", ComposingToolCharacters = 300
            });
            harness.Controller.Tick();

            Assert.AreEqual(AgentStatusKind.ComposingTool, harness.View.StatusKind);
            Assert.AreEqual("writing a script", harness.View.StatusText);
        }

        [Test]
        public void Overlay_TextChunks_GrowTheTokenCount_ShownAfterTheFirstSecond()
        {
            using ChatOverlayHarness harness = new();
            harness.Controller.Submit("hi");

            harness.Sender.Stream(new string('a', 400));
            harness.Controller.Tick();
            Assert.AreEqual("answering", harness.View.StatusText, "nothing is added before the first second");

            harness.Clock.Advance(2.2d);
            harness.Controller.Tick();
            Assert.AreEqual("answering \u00b7 2s \u00b7 ~100 tokens", harness.View.StatusText);
        }

        [Test]
        public void OfferPlayerLine_AsksTheHooks_AndSaysWhetherOneTookIt()
        {
            using ChatOverlayHarness harness = new();
            Assert.IsFalse(harness.Controller.OfferPlayerLine("hello"), "twin: no hook, nobody takes it");

            List<string> heard = new();
            using IDisposable hook = harness.Controller.AddPreSendHook(new DelegateHook(line =>
            {
                heard.Add(line.Text);
                return line.Text.StartsWith("Bob", StringComparison.Ordinal);
            }));

            Assert.IsTrue(harness.Controller.OfferPlayerLine("  Bob, hi  "));
            Assert.IsFalse(harness.Controller.OfferPlayerLine("build a tower"));
            CollectionAssert.AreEqual(new[] { "Bob, hi", "build a tower" }, heard);
            Assert.AreEqual(0, harness.Sender.Requests.Count, "offering never sends anything to the agent");
            Assert.AreEqual(0, harness.Controller.Feed.Lines.Count, "nor writes into the overlay's feed");
        }

        [Test]
        public void SurfaceSwitch_ClaimsNest_AndChangedFiresOnFlipsOnly()
        {
            ChatOverlaySurfaceSwitch surface = new();
            int changes = 0;
            surface.Changed += () => changes++;

            IDisposable first = surface.Claim("lua chat");
            IDisposable second = surface.Claim("another");
            Assert.IsFalse(surface.IsDefaultSurfaceShown);
            Assert.AreEqual("another", surface.Claimant);
            Assert.AreEqual(1, changes);

            second.Dispose();
            Assert.IsFalse(surface.IsDefaultSurfaceShown, "one claim still holds");
            first.Dispose();
            first.Dispose();
            Assert.IsTrue(surface.IsDefaultSurfaceShown);
            Assert.AreEqual("", surface.Claimant);
            Assert.AreEqual(2, changes);
        }

        private sealed class DelegateHook : IChatOverlayPreSendHook
        {
            private readonly Func<ChatOverlayPlayerLine, bool> _take;

            public DelegateHook(Func<ChatOverlayPlayerLine, bool> take)
            {
                _take = take;
            }

            public bool TryTake(ChatOverlayPlayerLine line)
            {
                return _take(line);
            }
        }
    }
}
