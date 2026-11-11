using System;
using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Ai.Npc;
using CoreAI.ChatOverlay;
using NUnit.Framework;

namespace CoreAI.Core.Tests.EditMode.Npc
{
    /// <summary>
    /// NPCs hearing the in-world chat through the overlay's pre-send hook: a line that addresses a villager (by
    /// name, as a follow-up, by proximity) is answered by that villager and never reaches the player's own agent; a
    /// line that addresses nobody, or a villager out of earshot, goes to the agent as before.
    /// </summary>
    [TestFixture]
    public sealed class NpcChatHearingPreSendEditModeTests
    {
        private sealed class Clock : IChatOverlayClock
        {
            public double NowSeconds { get; set; } = 10d;
        }

        private sealed class SilentView : IChatOverlayView
        {
            public event Action<string> Submitted
            {
                add { }
                remove { }
            }

            public event Action CloseRequested
            {
                add { }
                remove { }
            }

            public void SetInputOpen(bool open, string prefill)
            {
            }

            public void AddLine(ChatFeedLine line)
            {
            }

            public void UpdateLine(ChatFeedLine line)
            {
            }

            public void RemoveLine(ChatFeedLine line)
            {
            }

            public void SetLineVisible(ChatFeedLine line, bool visible)
            {
            }

            public void SetStatus(AgentStatusKind kind, string text)
            {
            }
        }

        private sealed class AgentSender : IChatOverlayTurnSender, IChatOverlayTurn
        {
            public readonly List<string> Sent = new();

            public IChatOverlayTurnObserver Observer;

            public bool IsAvailable { get; set; } = true;

            public IChatOverlayTurn Send(ChatOverlayTurnRequest request, IChatOverlayTurnObserver observer)
            {
                Sent.Add(request.Text);
                Observer = observer;
                return this;
            }

            public void StopAgent(string roleId)
            {
            }

            public void Cancel()
            {
            }

            public void Finish()
            {
                IChatOverlayTurnObserver observer = Observer;
                Observer = null;
                observer?.OnCompleted();
            }
        }

        private sealed class World : IDisposable
        {
            public World(NpcPosition? listener = null)
            {
                Sender = new AgentSender();
                Controller = new ChatOverlayController(new ChatOverlayOptions(), new SilentView(), Sender,
                    new ChatCommandRegistry(), new ChatAgentSelector(BuiltInAgentRoleIds.SmartChat), new Clock());
                Orchestrator = new ScriptedNpcOrchestrator { Script = r => ScriptedNpcTurn.Reply("Aye, lad.") };
                NpcClock = new ManualNpcClock();
                Director = NpcTestWorld.Director(Orchestrator, new ChatOverlayNpcSpeechSink(Controller), NpcClock);
                ListenerAt = listener;
                Hearing = new NpcChatHearing(Director, "steve-id", "Steve", () => ListenerAt);
                Hearing.AttachTo(Controller);
            }

            public NpcPosition? ListenerAt { get; set; }

            public AgentSender Sender { get; }

            public ChatOverlayController Controller { get; }

            public ScriptedNpcOrchestrator Orchestrator { get; }

            public ManualNpcClock NpcClock { get; }

            public NpcDirector Director { get; }

            public NpcChatHearing Hearing { get; }

            public List<string> Feed()
            {
                List<string> lines = new();
                foreach (ChatFeedLine line in Controller.Feed.Lines)
                {
                    lines.Add(line.Speaker + ": " + line.Text);
                }

                return lines;
            }

            public void Dispose()
            {
                Hearing.Dispose();
                Director.Dispose();
                Controller.Dispose();
            }
        }

        [Test]
        public void LineNamingAVillager_IsAnsweredByHim_AndNeverReachesThePlayersAgent()
        {
            using World world = new();
            world.Director.Add(NpcTestWorld.Npc("bob", "Bob"));

            world.Controller.Submit("Bob, any bread today?");

            Assert.IsTrue(world.Hearing.IsPreSendHook);
            CollectionAssert.IsEmpty(world.Sender.Sent, "a line an NPC took must not reach the player's agent");
            Assert.AreEqual(1, world.Orchestrator.Requests.Count);
            Assert.AreEqual("Steve: Bob, any bread today?", world.Orchestrator.Requests[0].Hint);
            CollectionAssert.AreEqual(new[] { "You: Bob, any bread today?", "Bob: Aye, lad." }, world.Feed());
        }

        [Test]
        public void LineNamingNobody_GoesToThePlayersAgent_AndNoVillagerAnswers()
        {
            using World world = new();
            world.Director.Add(NpcTestWorld.Npc("bob", "Bob"));

            world.Controller.Submit("build me a house");

            CollectionAssert.AreEqual(new[] { "build me a house" }, world.Sender.Sent);
            CollectionAssert.IsEmpty(world.Orchestrator.Requests);
            CollectionAssert.AreEqual(new[] { "You: build me a house" }, world.Feed());
        }

        [Test]
        public void FollowUpLine_WithoutTheName_StaysWithTheVillager()
        {
            using World world = new();
            world.Director.Add(NpcTestWorld.Npc("bob", "Bob"));
            world.Controller.Submit("Bob, any bread today?");
            world.NpcClock.Now = 5d;

            world.Controller.Submit("and rye?");

            CollectionAssert.IsEmpty(world.Sender.Sent);
            Assert.AreEqual(2, world.Orchestrator.Requests.Count);
            Assert.AreEqual("Steve: and rye?", world.Orchestrator.Requests[1].Hint);
        }

        [Test]
        public void FollowUpLine_AfterTheConversationLapsed_GoesToTheAgent()
        {
            using World world = new();
            world.Director.Add(NpcTestWorld.Npc("bob", "Bob"));
            world.Controller.Submit("Bob, any bread today?");
            world.NpcClock.Now = 60d;

            world.Controller.Submit("and rye?");

            CollectionAssert.AreEqual(new[] { "and rye?" }, world.Sender.Sent);
            Assert.AreEqual(1, world.Orchestrator.Requests.Count);
        }

        [Test]
        public void FollowUpLine_WhileThePlayerStaysInEarshot_StaysWithTheVillager()
        {
            using World world = new(new NpcPosition(0d, 0d, 0d));
            world.Director.Add(NpcTestWorld.Npc("bob", "Bob", new NpcPosition(10d, 0d, 0d)));
            world.Controller.Submit("Bob, any bread today?");
            world.NpcClock.Now = 5d;
            world.ListenerAt = new NpcPosition(4d, 0d, 0d);

            world.Controller.Submit("and rye?");

            CollectionAssert.IsEmpty(world.Sender.Sent);
            Assert.AreEqual(2, world.Orchestrator.Requests.Count);
        }

        [Test]
        public void FollowUpLine_AfterThePlayerWalkedOutOfEarshot_GoesToTheAgent_NegativeTwin()
        {
            using World world = new(new NpcPosition(0d, 0d, 0d));
            world.Director.Add(NpcTestWorld.Npc("bob", "Bob", new NpcPosition(10d, 0d, 0d)));
            world.Controller.Submit("Bob, any bread today?");
            world.NpcClock.Now = 5d;
            world.ListenerAt = new NpcPosition(100d, 0d, 0d);

            world.Controller.Submit("build a tower here");

            CollectionAssert.AreEqual(new[] { "build a tower here" }, world.Sender.Sent,
                "walking away ends the conversation inside its time window");
            Assert.AreEqual(1, world.Orchestrator.Requests.Count);
        }

        [Test]
        public void AiAddressedLine_GoesToTheAgent_EvenOneSecondAfterTalkingToAVillager()
        {
            using World world = new();
            world.Director.Add(NpcTestWorld.Npc("bob", "Bob"));
            world.Controller.Submit("Bob, any bread today?");
            world.NpcClock.Now = 1d;

            world.Controller.Submit("@ai build a tower");

            CollectionAssert.AreEqual(new[] { "@ai build a tower" }, world.Sender.Sent);
            Assert.AreEqual(1, world.Orchestrator.Requests.Count, "the villager never hears it");
        }

        [Test]
        public void ALineThatOnlyBeginsWithTheLettersOfTheAlias_StaysAFollowUp_NegativeTwin()
        {
            using World world = new();
            world.Director.Add(NpcTestWorld.Npc("bob", "Bob"));
            world.Controller.Submit("Bob, any bread today?");
            world.NpcClock.Now = 5d;

            world.Controller.Submit("aim for the rye loaf");

            CollectionAssert.IsEmpty(world.Sender.Sent);
            Assert.AreEqual(2, world.Orchestrator.Requests.Count);
        }

        [Test]
        public void TheAisChatName_AddressesIt_WithTheAliasesCleared()
        {
            using World world = new();
            world.Director.Add(NpcTestWorld.Npc("bob", "Bob"));
            world.Hearing.AiAddressAliases = Array.Empty<string>();
            world.Hearing.AiDisplayName = () => "Builder";
            world.Controller.Submit("Bob, any bread today?");
            world.NpcClock.Now = 1d;

            world.Controller.Submit("Builder: make a wall");

            CollectionAssert.AreEqual(new[] { "Builder: make a wall" }, world.Sender.Sent);
            Assert.AreEqual(1, world.Orchestrator.Requests.Count);
        }

        [Test]
        public void AVillagerNamedAi_IsAnswered_WhenTheHostClearsTheAliases()
        {
            using World world = new();
            world.Director.Add(NpcTestWorld.Npc("ai-npc", "Ai"));
            world.Hearing.AiAddressAliases = Array.Empty<string>();
            world.Hearing.AiDisplayName = () => "AI";

            world.Controller.Submit("Ai, how are you?");

            CollectionAssert.IsEmpty(world.Sender.Sent, "the AI's chat name yields to a villager of that name");
            Assert.AreEqual(1, world.Orchestrator.Requests.Count);
        }

        [Test]
        public void AVillagerNamedAi_IsNotOffered_TheLine_WhileTheDefaultAliasStands_NegativeTwin()
        {
            using World world = new();
            world.Director.Add(NpcTestWorld.Npc("ai-npc", "Ai"));

            world.Controller.Submit("Ai, how are you?");

            CollectionAssert.AreEqual(new[] { "Ai, how are you?" }, world.Sender.Sent);
            CollectionAssert.IsEmpty(world.Orchestrator.Requests);
        }

        [TestCase("ai build a tower", true)]
        [TestCase("@ai build a tower", true)]
        [TestCase("  @AI: build", true)]
        [TestCase("Ai, hello", true)]
        [TestCase("ai", true)]
        [TestCase("@ai", true)]
        [TestCase("ai-build", true)]
        [TestCase("aim higher", false)]
        [TestCase("air", false)]
        [TestCase("say ai", false)]
        [TestCase("@", false)]
        [TestCase("", false)]
        public void AiAddress_DefaultAlias_MatchesOnlyAWholeLeadingWord(string text, bool expected)
        {
            Assert.AreEqual(expected, NpcAiAddress.IsAddressedToAi(text, NpcAiAddress.DefaultAliases, null), text);
        }

        [Test]
        public void AiAddress_AMultiWordChatName_AndEmptyNames_TheTwin()
        {
            Assert.IsTrue(NpcAiAddress.StartsWithAddress("build bot, make a wall", "Build Bot"));
            Assert.IsFalse(NpcAiAddress.StartsWithAddress("build botany", "Build Bot"));
            Assert.IsFalse(NpcAiAddress.StartsWithAddress("anything", " "));
            Assert.IsFalse(NpcAiAddress.IsAddressedToAi("ai, go", Array.Empty<string>(), ""));
        }

        [Test]
        public void UnaddressedLine_TakenByANearbyVillagerByProximity_SkipsTheAgent()
        {
            using World world = new(new NpcPosition(0d, 0d, 0d));
            NpcAgentDefinition bob = NpcTestWorld.Npc("bob", "Bob", new NpcPosition(3d, 0d, 0d));
            bob.Addressing.AnswerUnaddressedWithin = 5d;
            world.Director.Add(bob);

            world.Controller.Submit("good morning");

            CollectionAssert.IsEmpty(world.Sender.Sent);
            Assert.AreEqual(1, world.Orchestrator.Requests.Count);
        }

        [Test]
        public void UnaddressedLine_OutsideTheProximityRadius_GoesToTheAgent()
        {
            using World world = new(new NpcPosition(0d, 0d, 0d));
            NpcAgentDefinition bob = NpcTestWorld.Npc("bob", "Bob", new NpcPosition(30d, 0d, 0d));
            bob.Addressing.AnswerUnaddressedWithin = 5d;
            world.Director.Add(bob);

            world.Controller.Submit("good morning");

            CollectionAssert.AreEqual(new[] { "good morning" }, world.Sender.Sent);
            CollectionAssert.IsEmpty(world.Orchestrator.Requests);
        }

        [Test]
        public void NamedVillagerOutOfEarshot_DoesNotAnswer_AndTheLineGoesToTheAgent()
        {
            using World world = new(new NpcPosition(0d, 0d, 0d));
            world.Director.Add(NpcTestWorld.Npc("bob", "Bob", new NpcPosition(100d, 0d, 0d)));

            world.Controller.Submit("Bob, are you there?");

            CollectionAssert.AreEqual(new[] { "Bob, are you there?" }, world.Sender.Sent);
            CollectionAssert.IsEmpty(world.Orchestrator.Requests);
        }

        [Test]
        public void NamedVillagerWithinEarshot_Answers()
        {
            using World world = new(new NpcPosition(0d, 0d, 0d));
            world.Director.Add(NpcTestWorld.Npc("bob", "Bob", new NpcPosition(10d, 0d, 0d)));

            world.Controller.Submit("Bob, are you there?");

            CollectionAssert.IsEmpty(world.Sender.Sent);
            Assert.AreEqual(1, world.Orchestrator.Requests.Count);
        }

        [Test]
        public void ImmediateFallbackLine_IsShownAfterThePlayersLine()
        {
            using World world = new();
            world.Orchestrator.Script = _ => ScriptedNpcTurn.Fail(LlmErrorCode.ProviderError, "upstream 500 secret");
            world.Director.Add(NpcTestWorld.Npc("bob", "Bob", null, "Not now, lad, the oven!"));

            world.Controller.Submit("Bob, bread?");

            CollectionAssert.AreEqual(new[] { "You: Bob, bread?", "Bob: Not now, lad, the oven!" }, world.Feed());
            CollectionAssert.IsEmpty(world.Sender.Sent);
        }

        [Test]
        public void VillagerLine_GoesThrough_WhileTheAgentIsStillAnswering()
        {
            using World world = new();
            world.Director.Add(NpcTestWorld.Npc("bob", "Bob"));
            world.Controller.Submit("build me a house");
            Assert.IsTrue(world.Controller.IsTurnActive);

            world.Controller.Submit("Bob, hello");

            Assert.AreEqual(1, world.Sender.Sent.Count);
            Assert.AreEqual(1, world.Orchestrator.Requests.Count);
            world.Sender.Finish();
        }

        [Test]
        public void AttachingToTheHook_StopsFeedListening_SoALineIsHeardOnce()
        {
            using World world = new();
            world.Director.Add(NpcTestWorld.Npc("bob", "Bob"));
            world.Hearing.AttachTo(world.Controller.Feed);
            world.Hearing.AttachTo(world.Controller);

            world.Controller.Submit("Bob, hello");

            Assert.AreEqual(1, world.Orchestrator.Requests.Count);
            CollectionAssert.IsEmpty(world.Sender.Sent);
        }

        [Test]
        public void FeedListening_StillSendsTheLineToTheAgentToo()
        {
            using World world = new();
            world.Director.Add(NpcTestWorld.Npc("bob", "Bob"));
            world.Hearing.AttachTo(world.Controller.Feed);

            world.Controller.Submit("Bob, hello");

            Assert.IsFalse(world.Hearing.IsPreSendHook);
            Assert.AreEqual(1, world.Orchestrator.Requests.Count);
            CollectionAssert.AreEqual(new[] { "Bob, hello" }, world.Sender.Sent);
        }

        private sealed class FixedListener : INpcListenerPosition
        {
            public NpcPosition? At;

            public NpcPosition? Current => At;
        }

        [Test]
        public void Relay_AnswersWithTheLatestLiveSource_AndForgetsDisposedOnes()
        {
            NpcListenerPositionRelay relay = new();
            FixedListener first = new() { At = new NpcPosition(1d, 0d, 0d) };
            FixedListener second = new() { At = new NpcPosition(2d, 0d, 0d) };

            Assert.IsFalse(relay.Current.HasValue);
            IDisposable a = relay.Attach(first);
            IDisposable b = relay.Attach(second);
            Assert.AreEqual(2d, relay.Current.Value.X);
            b.Dispose();
            b.Dispose();
            Assert.AreEqual(1d, relay.Current.Value.X);
            a.Dispose();

            Assert.IsFalse(relay.HasSource);
            Assert.IsFalse(relay.Current.HasValue);
            Assert.Throws<ArgumentException>(() => relay.Attach(relay));
        }

        [Test]
        public void Relay_FedLater_HoldsChatToTheHearingRadius_AndWithoutASourceNamesStillWork()
        {
            NpcListenerPositionRelay relay = new();
            using World world = new();
            world.Hearing.Dispose();
            NpcChatHearing hearing = new(world.Director, "steve-id", "Steve", () => relay.Current);
            hearing.AttachTo(world.Controller);
            world.Director.Add(NpcTestWorld.Npc("bob", "Bob", new NpcPosition(100d, 0d, 0d)));

            world.Controller.Submit("Bob, are you there?");
            Assert.AreEqual(1, world.Orchestrator.Requests.Count, "no listener position: names work at any distance");

            using IDisposable attached = relay.Attach(new FixedListener { At = new NpcPosition(0d, 0d, 0d) });
            world.NpcClock.Now = 100d;
            world.Controller.Submit("Bob, are you there?");

            Assert.AreEqual(1, world.Orchestrator.Requests.Count, "with the listener 100 units away Bob cannot hear");
            CollectionAssert.AreEqual(new[] { "Bob, are you there?" }, world.Sender.Sent);
            hearing.Dispose();
        }

        [Test]
        public void ABrokenListenerSource_ReadsAsUnknown()
        {
            NpcListenerPositionRelay relay = new();
            relay.Attach(new ThrowingListener());

            Assert.IsFalse(relay.Current.HasValue);
        }

        private sealed class ThrowingListener : INpcListenerPosition
        {
            public NpcPosition? Current => throw new InvalidOperationException("world gone");
        }

        [Test]
        public void DisposedHearing_LeavesEveryLineToTheAgent()
        {
            using World world = new();
            world.Director.Add(NpcTestWorld.Npc("bob", "Bob"));
            world.Hearing.Dispose();

            world.Controller.Submit("Bob, hello");

            CollectionAssert.AreEqual(new[] { "Bob, hello" }, world.Sender.Sent);
            CollectionAssert.IsEmpty(world.Orchestrator.Requests);
        }
    }
}
