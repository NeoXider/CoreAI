using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.Npc;
using CoreAI.ChatOverlay;
using NUnit.Framework;

namespace CoreAI.Core.Tests.EditMode.Npc
{
    /// <summary>
    /// The engine-free halves of the speech sinks (the chat-feed poster and the bubble model), the chat hearing
    /// adapter, and the text rules that turn a model reply into a spoken line.
    /// </summary>
    [TestFixture]
    public sealed class NpcSpeechAndTextEditModeTests
    {
        private static readonly NpcSpeech BobTurn1 = new("bob", "Bob", "p1", "Steve", 1, "ai:village");

        private SynchronizationContext _previousContext;

        [SetUp]
        public void SetUp()
        {
            _previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void TearDown()
        {
            SynchronizationContext.SetSynchronizationContext(_previousContext);
        }

        [Test]
        public void TheChatSink_PostsOneFinishedLine_WithTheNpcAsSpeaker()
        {
            RecordingChatMessages messages = new();
            ChatOverlayNpcSpeechSink sink = new(messages);

            sink.SpeechStarted(BobTurn1);
            sink.SpeechGrew(BobTurn1, "Fresh");
            sink.SpeechGrew(BobTurn1, "Fresh bread");
            sink.SpeechCompleted(BobTurn1, "Fresh bread today.", false);
            sink.SpeechCancelled(BobTurn1);

            CollectionAssert.AreEqual(new[] { "Bob|Fresh bread today.|System" }, messages.Lines);
        }

        [Test]
        public void TheChatSink_SkipsAnEmptyLine()
        {
            RecordingChatMessages messages = new();
            new ChatOverlayNpcSpeechSink(messages).SpeechCompleted(BobTurn1, "  ", false);

            Assert.AreEqual(0, messages.Lines.Count);
        }

        [Test]
        public void TheBubble_ThinksGrowsAndHolds_ThenHides()
        {
            ManualNpcClock clock = new() { Now = 10d };
            NpcSpeechBubbleBoard board = new(new NpcSpeechBubbleOptions
            {
                MinHoldSeconds = 2d,
                HoldSecondsPerCharacter = 0.1d,
                MaxHoldSeconds = 10d
            }, clock);

            board.SpeechStarted(BobTurn1);
            Assert.IsTrue(board.TryGet("bob", out NpcSpeechBubble bubble));
            Assert.IsTrue(bubble.IsThinking && bubble.IsVisible);
            Assert.AreEqual("...", bubble.Text);

            board.SpeechGrew(BobTurn1, "Fresh");
            Assert.IsFalse(bubble.IsThinking);
            Assert.AreEqual("Fresh", bubble.Text);
            board.SpeechGrew(BobTurn1, "Fresh bread");
            Assert.AreEqual("Fresh bread", bubble.Text, "The bubble grows with the stream.");

            board.SpeechCompleted(BobTurn1, "Fresh bread today.", false);
            Assert.IsFalse(bubble.IsStreaming);
            Assert.AreEqual(10d + 2d + 18 * 0.1d, bubble.HideAt, 1e-9);

            clock.Now = bubble.HideAt - 0.01d;
            board.Tick();
            Assert.IsTrue(bubble.IsVisible);
            clock.Now = bubble.HideAt;
            board.Tick();
            Assert.IsFalse(bubble.IsVisible);
        }

        [Test]
        public void TheBubble_IgnoresAStaleTurn_AndHidesOnCancel()
        {
            NpcSpeechBubbleBoard board = new(null, new ManualNpcClock());
            NpcSpeech turn2 = new("bob", "Bob", "p1", "Steve", 2, "");

            board.SpeechStarted(BobTurn1);
            board.SpeechStarted(turn2);
            board.SpeechGrew(BobTurn1, "old text");
            board.TryGet("bob", out NpcSpeechBubble bubble);

            Assert.AreEqual("...", bubble.Text, "A superseded turn cannot write into the newer bubble.");
            board.SpeechCancelled(BobTurn1);
            Assert.IsTrue(bubble.IsVisible, "Cancelling the stale turn leaves the current one.");
            board.SpeechCancelled(turn2);
            Assert.IsFalse(bubble.IsVisible);
        }

        [Test]
        public void TheBubble_AScriptedLineTakesOverAndALongLineKeepsItsEnd()
        {
            NpcSpeechBubbleBoard board = new(new NpcSpeechBubbleOptions { MaxCharacters = 10 }, new ManualNpcClock());
            NpcSpeech scripted = new("bob", "Bob", "", "", 0, "");

            board.SpeechStarted(BobTurn1);
            board.SpeechCompleted(scripted, "0123456789ABCDEF", false);
            board.TryGet("bob", out NpcSpeechBubble bubble);

            Assert.AreEqual(0, bubble.TurnId);
            Assert.AreEqual("…789ABCDEF", bubble.Text);
            Assert.AreEqual(10, bubble.Text.Length);
        }

        [Test]
        public void TheBubbleVersion_ChangesOnEveryUpdate()
        {
            NpcSpeechBubbleBoard board = new(null, new ManualNpcClock());
            int changes = 0;
            board.BubbleChanged += _ => changes++;

            board.SpeechStarted(BobTurn1);
            board.SpeechGrew(BobTurn1, "a");
            board.SpeechCompleted(BobTurn1, "ab", false);
            board.TryGet("bob", out NpcSpeechBubble bubble);

            Assert.AreEqual(3, changes);
            Assert.AreEqual(3, bubble.Version);
            Assert.IsTrue(board.Forget("bob"));
            Assert.IsFalse(board.TryGet("bob", out _));
        }

        [Test]
        public void ChatHearing_RoutesPlayerLines_AndIgnoresEveryOtherKind()
        {
            ScriptedNpcOrchestrator orchestrator = new();
            RecordingNpcSink sink = new();
            using NpcDirector director = new(orchestrator, sink, new NpcDirectorOptions(), new ManualNpcClock());
            director.Add(NpcTestWorld.Npc("bob", "Bob"));
            ChatFeed feed = new();
            using NpcChatHearing hearing = new(director, "steve-id", "Steve");
            hearing.AttachTo(feed);

            feed.Add(ChatFeedLineKind.Assistant, "AI", "Bob is a villager.", 0d);
            feed.Add(ChatFeedLineKind.System, "Bob", "Bob says hello.", 0d);
            feed.Add(ChatFeedLineKind.Player, "You", "Bob, hello!", 0d);
            hearing.Detach();
            feed.Add(ChatFeedLineKind.Player, "You", "Bob, again!", 0d);

            Assert.AreEqual(1, orchestrator.Requests.Count, "Only the player's own line reaches the NPCs.");
            Assert.AreEqual("Steve: Bob, hello!", orchestrator.Requests[0].Hint);
            Assert.AreEqual("steve-id", orchestrator.Requests[0].ActorContext.Value.ActorId);
        }

        [Test]
        public void ChatHearing_ReportsWhetherAnNpcTookTheLine()
        {
            using NpcDirector director = new(new ScriptedNpcOrchestrator(), null, null, new ManualNpcClock());
            NpcAgentDefinition bob = NpcTestWorld.Npc("bob", "Bob");
            bob.Addressing.FollowUpSeconds = 0d;
            director.Add(bob);
            NpcChatHearing hearing = new(director);

            Assert.IsTrue(hearing.HearPlayerLine("Steve", "Bob?"));
            Assert.IsFalse(hearing.HearPlayerLine("Steve", "build me a house"));
            Assert.IsFalse(hearing.HearPlayerLine("Steve", ""));
        }

        [TestCase("Bob: Hello.", "Bob", "Hello.")]
        [TestCase("bob : Hello.", "Bob", "Hello.")]
        [TestCase("Bobcat: Hello.", "Bob", "Bobcat: Hello.")]
        [TestCase("\"Quoted line.\"", "Bob", "Quoted line.")]
        [TestCase("*nods* Sure thing.", "Bob", "Sure thing.")]
        [TestCase("*nods*", "Bob", "nods")]
        [TestCase("One.\n\nTwo.   Three.", "Bob", "One. Two. Three.")]
        public void CleanReply_MakesOneSpokenLine(string raw, string name, string expected)
        {
            Assert.AreEqual(expected, NpcText.CleanReply(raw, name, 0));
        }

        [Test]
        public void CleanReply_CutsALongReplyOnAWord_WithAnEllipsis()
        {
            string cleaned = NpcText.CleanReply("The quick brown fox jumps over the lazy dog", "Bob", 20);

            Assert.LessOrEqual(cleaned.Length, 20);
            Assert.AreEqual("The quick brown fox…", cleaned);
        }

        [TestCase("hey @Bob!", "Bob", 5)]
        [TestCase("BOB?", "Bob", 0)]
        [TestCase("Bobby", "Bob", -1)]
        [TestCase("old bob the farmer", "Old Bob", 0)]
        [TestCase("", "Bob", -1)]
        public void IndexOfName_MatchesWholeWordsOnly(string text, string name, int expected)
        {
            Assert.AreEqual(expected, NpcText.IndexOfName(text, name));
        }

        [Test]
        public void MemoryTopics_AreNpcPrefixed_AndAnEmptyKeyIsRefused()
        {
            AgentMemoryScope scoped = NpcMemoryScopes.ScopeFor(new AgentMemoryScope("t", "u", "s", "quest"), "bob");

            Assert.AreEqual("npc:bob", scoped.TopicId);
            Assert.AreEqual("t", scoped.TenantId);
            Assert.AreEqual("npc:bob", NpcMemoryScopes.CancellationScopeFor("bob"));
            Assert.Throws<System.ArgumentException>(() => NpcMemoryScopes.TopicFor("  "));
        }

        [Test]
        public void ThePersona_AnswersToItsNameAndAliases_WithoutDuplicates()
        {
            NpcPersona persona = new()
            {
                Id = "bob",
                DisplayName = "Bob",
                Aliases = new[] { "farmer", "BOB", " ", null }
            };

            CollectionAssert.AreEqual(new[] { "Bob", "farmer" }, persona.NamesToAnswer());
            Assert.AreEqual("bob", new NpcPersona { Id = "bob" }.EffectiveName);
            Assert.AreEqual(BuiltInAgentRoleIds.AiNpc, new NpcPersona { RoleId = "" }.EffectiveRoleId);
        }
    }
}
