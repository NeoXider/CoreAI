using System;
using System.Collections.Generic;
using CoreAI.ChatOverlay;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.ChatOverlay
{
    /// <summary>
    /// The overlay's pre-send hooks: a plain line the player types is offered to them before the agent, a line a hook
    /// takes never reaches the agent, a line no hook takes reaches it exactly as before, and commands never reach a
    /// hook.
    /// </summary>
    [TestFixture]
    public sealed class ChatOverlayPreSendHookEditModeTests
    {
        private sealed class RecordingHook : IChatOverlayPreSendHook
        {
            public readonly List<ChatOverlayPlayerLine> Offered = new();

            public Func<ChatOverlayPlayerLine, bool> Take = _ => false;

            public bool TryTake(ChatOverlayPlayerLine line)
            {
                Offered.Add(line);
                return Take(line);
            }
        }

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
        public void TakenLine_IsShownOnce_AndNeverReachesTheAgent()
        {
            using ChatOverlayHarness harness = new();
            RecordingHook hook = new() { Take = _ => true };
            harness.Controller.AddPreSendHook(hook);

            harness.Controller.Submit("  Bob, any bread today?  ");

            Assert.AreEqual(1, hook.Offered.Count);
            Assert.AreEqual("Bob, any bread today?", hook.Offered[0].Text);
            Assert.AreEqual("You", hook.Offered[0].SpeakerName);
            Assert.AreEqual(100d, hook.Offered[0].AtSeconds);
            CollectionAssert.IsEmpty(harness.Sender.Requests, "a line a hook took must not reach the agent");
            List<ChatFeedLine> player = LinesOf(harness, ChatFeedLineKind.Player);
            Assert.AreEqual(1, player.Count);
            Assert.AreEqual("Bob, any bread today?", player[0].Text);
            Assert.IsFalse(harness.Controller.IsTurnActive);
        }

        [Test]
        public void DeclinedLine_ReachesTheAgent_AndIsShownOnce()
        {
            using ChatOverlayHarness harness = new();
            RecordingHook hook = new();
            harness.Controller.AddPreSendHook(hook);

            harness.Controller.Submit("build me a house");

            Assert.AreEqual(1, hook.Offered.Count);
            Assert.AreEqual(1, harness.Sender.Requests.Count);
            Assert.AreEqual("build me a house", harness.Sender.Requests[0].Text);
            Assert.AreEqual(1, LinesOf(harness, ChatFeedLineKind.Player).Count, "the line is not shown twice");
            Assert.IsTrue(harness.Controller.IsTurnActive);
        }

        [Test]
        public void Commands_NeverReachAHook_EvenOneThatSendsTextToTheModel()
        {
            using ChatOverlayHarness harness = new();
            RecordingHook hook = new() { Take = _ => true };
            harness.Controller.AddPreSendHook(hook);
            harness.Registry.Register(new ChatCommand("ask", "Ask the AI.",
                context => ChatCommandResult.SendToModel(context.Invocation.ArgumentText)));

            harness.Controller.Submit("/help");
            harness.Controller.Submit("/ask Bob, hello");

            CollectionAssert.IsEmpty(hook.Offered);
            Assert.AreEqual(1, harness.Sender.Requests.Count);
            Assert.AreEqual("Bob, hello", harness.Sender.Requests[0].Text);
        }

        [Test]
        public void EscapedSlashLine_IsPlainText_AndIsOffered()
        {
            using ChatOverlayHarness harness = new();
            RecordingHook hook = new() { Take = _ => true };
            harness.Controller.AddPreSendHook(hook);

            harness.Controller.Submit("//Bob look");

            Assert.AreEqual(1, hook.Offered.Count);
            Assert.AreEqual("/Bob look", hook.Offered[0].Text);
            CollectionAssert.IsEmpty(harness.Sender.Requests);
        }

        [Test]
        public void TheFirstHookThatTakesTheLine_EndsTheRound()
        {
            using ChatOverlayHarness harness = new();
            RecordingHook first = new();
            RecordingHook second = new() { Take = _ => true };
            RecordingHook third = new() { Take = _ => true };
            harness.Controller.AddPreSendHook(first);
            harness.Controller.AddPreSendHook(second);
            harness.Controller.AddPreSendHook(third);

            harness.Controller.Submit("Alice, hi");

            Assert.AreEqual(1, first.Offered.Count);
            Assert.AreEqual(1, second.Offered.Count);
            CollectionAssert.IsEmpty(third.Offered);
            CollectionAssert.IsEmpty(harness.Sender.Requests);
        }

        [Test]
        public void AThrowingHook_IsSkipped_AndTheLineStillReachesTheAgent()
        {
            using ChatOverlayHarness harness = new();
            RecordingHook broken = new() { Take = _ => throw new InvalidOperationException("hook bug") };
            RecordingHook declining = new();
            harness.Controller.AddPreSendHook(broken);
            harness.Controller.AddPreSendHook(declining);

            Assert.DoesNotThrow(() => harness.Controller.Submit("hello world"));

            Assert.AreEqual(1, declining.Offered.Count);
            Assert.AreEqual(1, harness.Sender.Requests.Count, "a broken hook must never swallow the player's line");
        }

        [Test]
        public void DisposedHook_IsNoLongerAsked()
        {
            using ChatOverlayHarness harness = new();
            RecordingHook hook = new() { Take = _ => true };
            IDisposable registration = harness.Controller.AddPreSendHook(hook);

            registration.Dispose();
            registration.Dispose();
            harness.Controller.Submit("Bob, hello");

            CollectionAssert.IsEmpty(hook.Offered);
            Assert.AreEqual(1, harness.Sender.Requests.Count);
        }

        [Test]
        public void AHookThatRemovesItselfWhileAsked_DoesNotBreakTheRound()
        {
            using ChatOverlayHarness harness = new();
            IDisposable registration = null;
            RecordingHook self = new();
            self.Take = _ =>
            {
                registration.Dispose();
                return false;
            };
            RecordingHook next = new() { Take = _ => true };
            registration = harness.Controller.AddPreSendHook(self);
            harness.Controller.AddPreSendHook(next);

            harness.Controller.Submit("Bob?");
            harness.Controller.Submit("Bob again?");

            Assert.AreEqual(1, self.Offered.Count);
            Assert.AreEqual(2, next.Offered.Count);
        }

        [Test]
        public void TakenLine_GoesThrough_WhileTheAgentIsStillAnswering()
        {
            using ChatOverlayHarness harness = new();
            harness.Controller.Submit("build me a house");
            Assert.IsTrue(harness.Controller.IsTurnActive);
            RecordingHook hook = new() { Take = line => line.Text.StartsWith("Bob", StringComparison.Ordinal) };
            harness.Controller.AddPreSendHook(hook);

            harness.Controller.Submit("Bob, hello");

            CollectionAssert.IsEmpty(LinesOf(harness, ChatFeedLineKind.Error), "no busy refusal for an NPC line");
            Assert.AreEqual(1, harness.Sender.Requests.Count);
        }

        [Test]
        public void DeclinedLine_WhileTheAgentIsAnswering_IsShownThenRefusedAsBusy()
        {
            using ChatOverlayHarness harness = new();
            harness.Controller.Submit("build me a house");
            harness.Controller.AddPreSendHook(new RecordingHook());

            harness.Controller.Submit("and a tower");

            Assert.AreEqual(1, harness.Sender.Requests.Count);
            Assert.AreEqual(1, LinesOf(harness, ChatFeedLineKind.Error).Count);
            List<ChatFeedLine> player = LinesOf(harness, ChatFeedLineKind.Player);
            Assert.AreEqual(2, player.Count, "with a hook the line is shown before the agent refuses it");
            Assert.AreEqual("and a tower", player[1].Text);
        }

        [Test]
        public void WithoutHooks_ABusyLineIsStillNotShown()
        {
            using ChatOverlayHarness harness = new();
            harness.Controller.Submit("build me a house");

            harness.Controller.Submit("and a tower");

            Assert.AreEqual(1, LinesOf(harness, ChatFeedLineKind.Player).Count);
            Assert.AreEqual(1, LinesOf(harness, ChatFeedLineKind.Error).Count);
        }

        [Test]
        public void DeclinedLine_WithoutAnAgent_IsShownOnceWithTheUnavailableLine()
        {
            using ChatOverlayHarness harness = new();
            harness.Sender.IsAvailable = false;
            harness.Controller.AddPreSendHook(new RecordingHook());

            harness.Controller.Submit("hello");

            Assert.AreEqual(1, LinesOf(harness, ChatFeedLineKind.Player).Count);
            Assert.AreEqual(harness.Options.ServiceUnavailableText, harness.LastLine.Text);
        }

        [Test]
        public void TakenLine_WithoutAnAgent_IsAnsweredByTheHookAlone()
        {
            using ChatOverlayHarness harness = new();
            harness.Sender.IsAvailable = false;
            harness.Controller.AddPreSendHook(new RecordingHook { Take = _ => true });

            harness.Controller.Submit("Bob, hello");

            CollectionAssert.IsEmpty(LinesOf(harness, ChatFeedLineKind.Error));
            Assert.AreEqual(1, LinesOf(harness, ChatFeedLineKind.Player).Count);
        }

        [Test]
        public void AnImmediateAnswerFromTheHook_IsShownAfterThePlayersLine()
        {
            using ChatOverlayHarness harness = new();
            harness.Controller.AddPreSendHook(new RecordingHook
            {
                Take = _ =>
                {
                    harness.Controller.PostMessage("Bob", "Not now, lad, the oven!");
                    return true;
                }
            });

            harness.Controller.Submit("Bob, bread?");

            IReadOnlyList<ChatFeedLine> lines = harness.Controller.Feed.Lines;
            Assert.AreEqual(2, lines.Count);
            Assert.AreEqual(ChatFeedLineKind.Player, lines[0].Kind);
            Assert.AreEqual("Bob", lines[1].Speaker);
        }

        [Test]
        public void DisposedController_DropsItsHooks()
        {
            ChatOverlayHarness harness = new();
            RecordingHook hook = new() { Take = _ => true };
            harness.Controller.AddPreSendHook(hook);

            harness.Dispose();
            harness.Controller.Submit("Bob, hello");

            CollectionAssert.IsEmpty(hook.Offered);
        }

        [Test]
        public void NullHook_IsRefused()
        {
            using ChatOverlayHarness harness = new();

            Assert.Throws<ArgumentNullException>(() => harness.Controller.AddPreSendHook(null));
        }
    }
}
