using System;
using System.Collections.Generic;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.Npc;
using CoreAI.Authority;
using NUnit.Framework;

namespace CoreAI.Core.Tests.EditMode.Npc
{
    /// <summary>
    /// The NPC director over a scripted orchestration service: exactly one reply per addressed line, budgets and
    /// cooldowns, a newer line superseding the older turn, per-NPC memory scopes, and the in-character fallback
    /// line that replaces every failure.
    /// </summary>
    [TestFixture]
    public sealed class NpcDirectorEditModeTests
    {
        private static readonly NpcPosition Origin = new(0d, 0d, 0d);

        private SynchronizationContext _previousContext;
        private ScriptedNpcOrchestrator _orchestrator;
        private RecordingNpcSink _sink;
        private ManualNpcClock _clock;
        private List<NpcSaid> _said;
        private List<NpcTurnEnded> _ended;
        private List<NpcLineIgnored> _ignored;

        /// <summary>WHY: the director and the fake resume on the captured context; the fixture runs them inline.</summary>
        [SetUp]
        public void SetUp()
        {
            _previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            _orchestrator = new ScriptedNpcOrchestrator();
            _sink = new RecordingNpcSink();
            _clock = new ManualNpcClock { Now = 100d };
            _said = new List<NpcSaid>();
            _ended = new List<NpcTurnEnded>();
            _ignored = new List<NpcLineIgnored>();
        }

        [TearDown]
        public void TearDown()
        {
            SynchronizationContext.SetSynchronizationContext(_previousContext);
        }

        [Test]
        public void AddressingOneNpcByName_GetsExactlyOneReply_FromThatNpc()
        {
            using NpcDirector director = Create();
            director.Add(NpcTestWorld.Npc("bob", "Bob", Origin));
            director.Add(NpcTestWorld.Npc("alice", "Alice", Origin));
            _orchestrator.Script = request => ScriptedNpcTurn.Reply(request.TraceId.Contains("bob") ? "Fresh bread!" : "Hi.");

            NpcHearResult result = director.Hear(NpcHeardLine.Chat("p1", "Steve", "Bob, do you sell bread?", Origin));

            Assert.IsTrue(result.IsTaken);
            Assert.AreEqual(1, _orchestrator.Requests.Count, "One line, one model call.");
            Assert.AreEqual(1, _said.Count);
            Assert.AreEqual("bob", _said[0].NpcId);
            Assert.AreEqual("Fresh bread!", _said[0].Text);
            Assert.IsFalse(_said[0].IsFallback);
            CollectionAssert.AreEqual(new[] { "bob:Fresh bread!" }, _sink.Completed);
        }

        [Test]
        public void ALineAddressedToNobody_GetsNoReply()
        {
            using NpcDirector director = Create();
            director.Add(NpcTestWorld.Npc("bob", "Bob", Origin));

            NpcHearResult result = director.Hear(NpcHeardLine.Chat("p1", "Steve", "What a day.", Origin));

            Assert.IsFalse(result.IsTaken);
            Assert.AreEqual(0, _orchestrator.Requests.Count);
            Assert.AreEqual(0, _said.Count);
            Assert.AreEqual(1, _ignored.Count);
            Assert.AreEqual(NpcIgnoreReason.NotAddressed, _ignored[0].Reason);
        }

        [Test]
        public void ALineToAnNpcOutOfRange_GetsNoReply()
        {
            using NpcDirector director = Create();
            director.Add(NpcTestWorld.Npc("bob", "Bob", new NpcPosition(500d, 0d, 0d)));

            NpcHearResult result = director.Hear(NpcHeardLine.Chat("p1", "Steve", "Bob!", Origin));

            Assert.IsFalse(result.IsTaken);
            Assert.AreEqual(0, _orchestrator.Requests.Count);
            Assert.AreEqual(NpcIgnoreReason.OutOfRange, _ignored[0].Reason);
            Assert.AreEqual("bob", _ignored[0].NpcId);
        }

        [Test]
        public void AProviderError_IsAnsweredWithAnInCharacterFallback_NeverTheErrorText()
        {
            using NpcDirector director = Create();
            director.Add(NpcTestWorld.Npc("bob", "Bob", Origin, "Ugh, my back. Ask me later."));
            const string providerText = "HTTP 500: upstream model gpt-x crashed, request id 7f3a";
            _orchestrator.Script = _ => ScriptedNpcTurn.Fail(LlmErrorCode.ProviderError, providerText);

            director.Hear(NpcHeardLine.Chat("p1", "Steve", "Bob?", Origin));

            Assert.AreEqual(1, _said.Count);
            Assert.IsTrue(_said[0].IsFallback);
            Assert.AreEqual("Ugh, my back. Ask me later.", _said[0].Text);
            Assert.AreEqual(NpcTurnOutcome.FallbackLine, _ended[0].Outcome);
            Assert.AreEqual(LlmErrorCode.ProviderError, _ended[0].ErrorCode);
            foreach (string call in _sink.Calls)
            {
                StringAssert.DoesNotContain("HTTP 500", call);
                StringAssert.DoesNotContain("7f3a", call);
            }
        }

        [Test]
        public void ASuccessfulTurn_IsNotAFallback()
        {
            using NpcDirector director = Create();
            director.Add(NpcTestWorld.Npc("bob", "Bob", Origin, "fallback"));

            director.Hear(NpcHeardLine.Chat("p1", "Steve", "Bob?", Origin));

            Assert.IsFalse(_said[0].IsFallback);
            Assert.AreEqual(NpcTurnOutcome.Replied, _ended[0].Outcome);
        }

        [Test]
        public void AThrowingPipeline_AndAnEmptyAnswer_BothBecomeFallbackLines()
        {
            using NpcDirector director = Create();
            director.Add(NpcTestWorld.Npc("bob", "Bob", Origin, "Hmm."));
            int call = 0;
            _orchestrator.Script = _ => call++ == 0
                ? new ScriptedNpcTurn { Throw = new InvalidOperationException("socket closed by 10.0.0.7") }
                : ScriptedNpcTurn.Reply("   ");

            director.Hear(NpcHeardLine.Chat("p1", "Steve", "Bob?", Origin));
            director.Hear(NpcHeardLine.Chat("p1", "Steve", "Bob??", Origin));

            Assert.AreEqual(2, _said.Count);
            Assert.IsTrue(_said[0].IsFallback && _said[1].IsFallback);
            Assert.AreEqual(LlmErrorCode.ProviderError, _ended[0].ErrorCode);
            Assert.AreEqual(LlmErrorCode.EmptyResponse, _ended[1].ErrorCode);
        }

        [Test]
        public void ANewerLine_CancelsTheTurnInFlight_AndOnlyTheNewerOneIsAnswered()
        {
            using NpcDirector director = Create();
            director.Add(NpcTestWorld.Npc("bob", "Bob", Origin));
            ScriptedNpcTurn first = ScriptedNpcTurn.Held();
            _orchestrator.Script = request => request.Hint.Contains("first") ? first : ScriptedNpcTurn.Reply("Second answer.");

            director.Hear(NpcHeardLine.Chat("p1", "Steve", "Bob, first question", Origin));
            Assert.IsTrue(director.TryGet("bob", out NpcAgent bob) && bob.IsSpeaking);
            director.Hear(NpcHeardLine.Chat("p1", "Steve", "Bob, second question", Origin));
            first.Gate.SetResult(true);
            director.Tick();

            Assert.IsTrue(_orchestrator.Tokens[0].IsCancellationRequested, "The superseded turn's token is cancelled.");
            Assert.AreEqual(NpcTurnOutcome.Superseded, _ended[0].Outcome);
            Assert.AreEqual(1, _said.Count, "The superseded turn speaks nothing, even when its answer arrives late.");
            Assert.AreEqual("Second answer.", _said[0].Text);
            CollectionAssert.Contains(_sink.Calls, "cancel:bob:1");
        }

        [Test]
        public void ALineInsideTheCooldown_Waits_AndTheLatestLineWins()
        {
            using NpcDirector director = Create();
            NpcAgentDefinition definition = NpcTestWorld.Npc("bob", "Bob", Origin);
            definition.Budget.CooldownSeconds = 5d;
            director.Add(definition);

            director.Hear(NpcHeardLine.Chat("p1", "Steve", "Bob, one", Origin));
            _clock.Now += 1d;
            NpcHearResult second = director.Hear(NpcHeardLine.Chat("p1", "Steve", "Bob, two", Origin));
            NpcHearResult third = director.Hear(NpcHeardLine.Chat("p1", "Steve", "Bob, three", Origin));
            director.Tick();

            Assert.IsFalse(second.Started);
            Assert.IsFalse(third.Started);
            Assert.AreEqual(1, _orchestrator.Requests.Count, "Nothing more starts inside the cooldown.");
            Assert.AreEqual(NpcTurnOutcome.Superseded, _ended[1].Outcome, "The waiting line was replaced by a newer one.");

            _clock.Now += 5d;
            director.Tick();

            Assert.AreEqual(2, _orchestrator.Requests.Count);
            StringAssert.Contains("three", _orchestrator.Requests[1].Hint);
        }

        [Test]
        public void TheWorldBudget_SpeaksAFallbackWithoutCallingTheModel_UntilTheWindowPasses()
        {
            using NpcDirector director = Create(new NpcDirectorOptions { MaxCallsPerMinute = 1 });
            director.Add(NpcTestWorld.Npc("bob", "Bob", Origin, "Busy, busy."));

            director.Hear(NpcHeardLine.Chat("p1", "Steve", "Bob?", Origin));
            director.Hear(NpcHeardLine.Chat("p1", "Steve", "Bob??", Origin));
            _clock.Now += 61d;
            director.Hear(NpcHeardLine.Chat("p1", "Steve", "Bob???", Origin));

            Assert.AreEqual(2, _orchestrator.Requests.Count);
            Assert.AreEqual(3, _said.Count);
            Assert.IsTrue(_said[1].IsFallback);
            Assert.AreEqual("Busy, busy.", _said[1].Text);
            Assert.AreEqual(LlmErrorCode.QuotaExceeded, _ended[1].ErrorCode);
            Assert.IsFalse(_said[2].IsFallback);
        }

        [Test]
        public void TheConcurrentTurnLimit_MakesASecondNpcWait_UntilTheFirstEnds()
        {
            using NpcDirector director = Create(new NpcDirectorOptions { MaxConcurrentTurns = 1 });
            director.Add(NpcTestWorld.Npc("bob", "Bob", Origin));
            director.Add(NpcTestWorld.Npc("alice", "Alice", Origin));
            ScriptedNpcTurn held = ScriptedNpcTurn.Held();
            _orchestrator.Script = request => request.TraceId.Contains("bob") ? held : ScriptedNpcTurn.Reply("Alice here.");

            director.Hear(NpcHeardLine.Chat("p1", "Steve", "Bob?", Origin));
            NpcHearResult alice = director.Hear(NpcHeardLine.Chat("p2", "Anna", "Alice?", Origin));

            Assert.IsFalse(alice.Started);
            Assert.AreEqual(1, director.RunningTurnCount);
            Assert.AreEqual(1, _orchestrator.Requests.Count);

            held.Gate.SetResult(true);
            director.Tick();

            Assert.AreEqual(2, _orchestrator.Requests.Count, "Alice starts once Bob's slot is free.");
            CollectionAssert.AreEqual(new[] { "bob", "alice" }, new[] { _said[0].NpcId, _said[1].NpcId });
        }

        [Test]
        public void ATurnThatRunsTooLong_IsStoppedOnTick_AndAFallbackIsSpoken()
        {
            using NpcDirector director = Create(new NpcDirectorOptions { TurnTimeoutSeconds = 10d });
            director.Add(NpcTestWorld.Npc("bob", "Bob", Origin, "Where was I?"));
            ScriptedNpcTurn held = ScriptedNpcTurn.Held();
            _orchestrator.Script = _ => held;

            director.Hear(NpcHeardLine.Chat("p1", "Steve", "Bob?", Origin));
            _clock.Now += 11d;
            director.Tick();

            Assert.IsTrue(_orchestrator.Tokens[0].IsCancellationRequested);
            Assert.AreEqual("Where was I?", _said[0].Text);
            Assert.AreEqual(LlmErrorCode.Timeout, _ended[0].ErrorCode);
            Assert.AreEqual(0, director.RunningTurnCount);
        }

        [Test]
        public void TheRequest_CarriesPersonaBudgetScopeAndALowerPriority()
        {
            using NpcDirector director = Create();
            NpcAgentDefinition definition = NpcTestWorld.Npc("bob", "Bob", Origin);
            definition.Budget.MaxOutputTokens = 77;
            definition.Budget.QueuePriority = -20;
            definition.SourceTag = "Mod:village";
            director.Add(definition);

            director.Hear(NpcHeardLine.Chat("p1", "Steve", "Bob, hi", Origin));

            AiTaskRequest request = _orchestrator.Requests[0];
            Assert.AreEqual(BuiltInAgentRoleIds.AiNpc, request.RoleId);
            Assert.AreEqual(77, request.MaxOutputTokens);
            Assert.AreEqual(-20, request.Priority);
            Assert.Less(request.Priority, 0, "NPC turns rank below the player's own agents (priority 0).");
            Assert.AreEqual("npc:bob", request.CancellationScope);
            Assert.AreEqual("Mod:village", request.SourceTag);
            StringAssert.Contains("You are Bob", request.RequestSystemInstructions);
            StringAssert.Contains("A villager named Bob.", request.RequestSystemInstructions);
            StringAssert.Contains("Short, warm sentences.", request.RequestSystemInstructions);
            StringAssert.Contains("Steve", request.RequestSystemInstructions);
            Assert.AreEqual("Steve: Bob, hi", request.Hint);
        }

        [Test]
        public void EachNpcTurn_RunsInItsOwnMemoryTopic_OverTheHostScope_WithNoGrants()
        {
            HostScopes scopes = new();
            using NpcDirector director = new(_orchestrator, _sink, new NpcDirectorOptions(), _clock,
                actors: new NpcActorContextFactory(null, scopes));
            Subscribe(director);
            director.Add(NpcTestWorld.Npc("bob", "Bob", Origin));
            NpcAgentDefinition alice = NpcTestWorld.Npc("alice", "Alice", Origin);
            alice.MemoryKey = "village/alice";
            director.Add(alice);

            director.Hear(NpcHeardLine.Chat("p1", "Steve", "Bob?", Origin));
            director.Hear(NpcHeardLine.Chat("p1", "Steve", "Alice?", Origin));

            ActorContext bobActor = _orchestrator.Requests[0].ActorContext.Value;
            ActorContext aliceActor = _orchestrator.Requests[1].ActorContext.Value;
            Assert.AreEqual("npc:bob", bobActor.MemoryScope.TopicId);
            Assert.AreEqual("npc:village/alice", aliceActor.MemoryScope.TopicId);
            Assert.AreEqual("tenant-1", bobActor.MemoryScope.TenantId, "The host's tenant is kept.");
            Assert.AreEqual("learner-9", bobActor.MemoryScope.UserId, "The host's user is kept.");
            Assert.AreEqual("p1", bobActor.ActorId, "Memory follows the speaker.");
            Assert.IsTrue(bobActor.IsTrusted);
            Assert.IsFalse(bobActor.Grants.IsUnrestricted);
            Assert.IsFalse(bobActor.Grants.Contains("anything"));
        }

        [Test]
        public void ATrustedSpeakerActor_KeepsItsIdentity_ButLosesItsGrants()
        {
            using NpcDirector director = Create();
            director.Add(NpcTestWorld.Npc("bob", "Bob", Origin));
            ActorContext speaker = new LocalActorIdentityProvider("player-7", "session-3", "world-1",
                ActorGrantSet.Create(new[] { "world:edit" }),
                new AgentMemoryScope("t", "u", "s", "quest-1")).GetActorContext(BuiltInAgentRoleIds.Creator);
            NpcHeardLine line = NpcHeardLine.Chat("p7", "Steve", "Bob?", Origin);
            line.SpeakerActor = speaker;

            director.Hear(line);

            ActorContext turn = _orchestrator.Requests[0].ActorContext.Value;
            Assert.AreEqual("player-7", turn.ActorId);
            Assert.AreEqual("session-3", turn.SessionId);
            Assert.AreEqual("world-1", turn.WorldId);
            Assert.AreEqual("npc:bob", turn.MemoryScope.TopicId);
            Assert.AreEqual("t", turn.MemoryScope.TenantId);
            Assert.IsFalse(turn.Grants.Contains("world:edit"), "An NPC turn never carries the speaker's grants.");
        }

        [Test]
        public void TheStreamedAnswer_GrowsTheSpeech_BeforeItCompletes()
        {
            using NpcDirector director = Create();
            director.Add(NpcTestWorld.Npc("bob", "Bob", Origin));
            _orchestrator.Script = _ => new ScriptedNpcTurn { Chunks = new[] { "Fresh ", "bread ", "today." } };

            director.Hear(NpcHeardLine.Chat("p1", "Steve", "Bob?", Origin));

            CollectionAssert.AreEqual(new[]
            {
                "start:bob:1", "grew:bob:Fresh", "grew:bob:Fresh bread", "grew:bob:Fresh bread today.",
                "done:bob:Fresh bread today."
            }, _sink.Calls);
            Assert.AreEqual(1, _orchestrator.StreamingCalls);
        }

        [Test]
        public void WithoutStreaming_TheTypedResultPathIsUsed()
        {
            using NpcDirector director = Create(new NpcDirectorOptions { UseStreaming = false });
            director.Add(NpcTestWorld.Npc("bob", "Bob", Origin, "fallback"));
            _orchestrator.Script = _ => ScriptedNpcTurn.Fail(LlmErrorCode.RateLimited, "429 from provider");

            director.Hear(NpcHeardLine.Chat("p1", "Steve", "Bob?", Origin));

            Assert.AreEqual(1, _orchestrator.TypedCalls);
            Assert.AreEqual(0, _orchestrator.StreamingCalls);
            Assert.AreEqual("fallback", _said[0].Text);
            Assert.AreEqual(LlmErrorCode.RateLimited, _ended[0].ErrorCode);
        }

        [Test]
        public void TheReply_IsCleanedIntoOneSpokenLine()
        {
            using NpcDirector director = Create();
            director.Add(NpcTestWorld.Npc("bob", "Bob", Origin));
            _orchestrator.Script = _ => ScriptedNpcTurn.Reply("Bob: \"*wipes hands* Fresh\n\nbread, friend.\"");

            director.Hear(NpcHeardLine.Chat("p1", "Steve", "Bob?", Origin));

            Assert.AreEqual("Fresh bread, friend.", _said[0].Text);
        }

        [Test]
        public void SayingAScriptedLine_GoesToTheSinksWithoutAModelCall()
        {
            using NpcDirector director = Create();
            director.Add(NpcTestWorld.Npc("bob", "Bob", Origin));

            Assert.IsTrue(director.Say("bob", "Good morning!"));
            Assert.IsFalse(director.Say("ghost", "Boo"));

            Assert.AreEqual(0, _orchestrator.Requests.Count);
            Assert.IsTrue(_said[0].IsScripted);
            CollectionAssert.AreEqual(new[] { "done:bob:Good morning!" }, _sink.Calls);
        }

        [Test]
        public void AskingAnNpcDirectly_SkipsTheAddressingRules_ButAnUnknownIdIsRefused()
        {
            using NpcDirector director = Create();
            director.Add(NpcTestWorld.Npc("bob", "Bob", new NpcPosition(900d, 0d, 0d)));

            NpcHearResult asked = director.Ask("bob", NpcHeardLine.Chat("p1", "Steve", "no name in here", Origin));
            NpcHearResult unknown = director.Ask("ghost", NpcHeardLine.Chat("p1", "Steve", "hello", Origin));

            Assert.IsTrue(asked.IsTaken);
            Assert.AreEqual(NpcAddressReason.Interaction, asked.Decision.Reason);
            Assert.IsFalse(unknown.IsTaken);
            Assert.AreEqual(NpcIgnoreReason.UnknownNpc, unknown.Decision.IgnoreReason);
        }

        [Test]
        public void RemovingAnNpc_CancelsItsTurn_AndSilencesItsLateAnswer()
        {
            using NpcDirector director = Create();
            director.Add(NpcTestWorld.Npc("bob", "Bob", Origin));
            ScriptedNpcTurn held = ScriptedNpcTurn.Held();
            _orchestrator.Script = _ => held;
            director.Hear(NpcHeardLine.Chat("p1", "Steve", "Bob?", Origin));

            Assert.IsTrue(director.Remove("bob"));
            held.Gate.SetResult(true);
            director.Tick();

            Assert.IsTrue(_orchestrator.Tokens[0].IsCancellationRequested);
            Assert.AreEqual(NpcTurnOutcome.Cancelled, _ended[0].Outcome);
            Assert.AreEqual(0, _said.Count);
            Assert.IsFalse(director.TryGet("bob", out _));
        }

        [Test]
        public void AFollowUpLine_GoesToTheNpcThatJustAnswered()
        {
            using NpcDirector director = Create();
            director.Add(NpcTestWorld.Npc("bob", "Bob", Origin));
            director.Add(NpcTestWorld.Npc("alice", "Alice", Origin));

            director.Hear(NpcHeardLine.Chat("p1", "Steve", "Bob, hello", Origin));
            _clock.Now += 3d;
            NpcHearResult followUp = director.Hear(NpcHeardLine.Chat("p1", "Steve", "how much is it?", Origin));

            Assert.AreEqual(NpcAddressReason.FollowUp, followUp.Decision.Reason);
            Assert.AreEqual("bob", followUp.Decision.NpcId);
        }

        [Test]
        public void AReplacedArbiterAndSink_AreTheOnesUsed()
        {
            AlwaysAliceArbiter arbiter = new();
            using NpcDirector director = new(_orchestrator, _sink, new NpcDirectorOptions(), _clock, arbiter);
            Subscribe(director);
            director.Add(NpcTestWorld.Npc("bob", "Bob", Origin));
            director.Add(NpcTestWorld.Npc("alice", "Alice", Origin));

            director.Hear(NpcHeardLine.Chat("p1", "Steve", "Bob!", Origin));

            Assert.AreEqual(1, arbiter.Calls);
            Assert.AreEqual("alice", _said[0].NpcId);
            Assert.AreEqual(1, _sink.Completed.Count);
        }

        [Test]
        public void AFailingSink_DoesNotSilenceTheOthers()
        {
            CompositeNpcSpeechSink sinks = new(new INpcSpeechSink[] { new ThrowingSink(), _sink });
            using NpcDirector director = new(_orchestrator, sinks, new NpcDirectorOptions(), _clock);
            director.Add(NpcTestWorld.Npc("bob", "Bob", Origin));

            director.Hear(NpcHeardLine.Chat("p1", "Steve", "Bob?", Origin));

            CollectionAssert.AreEqual(new[] { "bob:Hello there." }, _sink.Completed);
        }

        [Test]
        public void AddingADuplicateId_OrPastCapacity_IsRefused()
        {
            using NpcDirector director = Create(new NpcDirectorOptions { MaxAgents = 1 });
            director.Add(NpcTestWorld.Npc("bob"));

            Assert.Throws<InvalidOperationException>(() => director.Add(NpcTestWorld.Npc("bob")));
            Assert.Throws<InvalidOperationException>(() => director.Add(NpcTestWorld.Npc("alice")));
            Assert.Throws<ArgumentException>(() => director.Add(new NpcAgentDefinition()));
        }

        [Test]
        public void AResultArrivingOutsideADirectorCall_IsAppliedOnTheNextTick()
        {
            using NpcDirector director = Create();
            director.Add(NpcTestWorld.Npc("bob", "Bob", Origin));
            ScriptedNpcTurn held = ScriptedNpcTurn.Held();
            _orchestrator.Script = _ => held;
            director.Hear(NpcHeardLine.Chat("p1", "Steve", "Bob?", Origin));

            held.Gate.SetResult(true);

            Assert.AreEqual(0, _said.Count, "Nothing is applied between director calls.");
            director.Tick();
            Assert.AreEqual(1, _said.Count);
        }

        private NpcDirector Create(NpcDirectorOptions options = null)
        {
            NpcDirector director = NpcTestWorld.Director(_orchestrator, _sink, _clock, options);
            Subscribe(director);
            return director;
        }

        private void Subscribe(INpcDirector director)
        {
            director.NpcSaid += said => _said.Add(said);
            director.TurnEnded += ended => _ended.Add(ended);
            director.LineIgnored += ignored => _ignored.Add(ignored);
        }

        private sealed class HostScopes : IAgentMemoryScopeProvider
        {
            public AgentMemoryScope GetScope(string roleId)
            {
                return new AgentMemoryScope("tenant-1", "learner-9", "session-1", "");
            }
        }

        private sealed class AlwaysAliceArbiter : INpcAddressArbiter
        {
            public int Calls;

            public NpcAddressDecision Decide(NpcHeardLine line, IReadOnlyList<NpcAddressCandidate> candidates,
                double nowSeconds, INpcLineOfSight lineOfSight)
            {
                Calls++;
                return NpcAddressDecision.Addressed("alice", NpcAddressReason.Name);
            }
        }

        private sealed class ThrowingSink : INpcSpeechSink
        {
            public void SpeechStarted(NpcSpeech speech)
            {
                throw new InvalidOperationException("broken bubble");
            }

            public void SpeechGrew(NpcSpeech speech, string textSoFar)
            {
                throw new InvalidOperationException("broken bubble");
            }

            public void SpeechCompleted(NpcSpeech speech, string text, bool isFallback)
            {
                throw new InvalidOperationException("broken bubble");
            }

            public void SpeechCancelled(NpcSpeech speech)
            {
                throw new InvalidOperationException("broken bubble");
            }
        }
    }
}
