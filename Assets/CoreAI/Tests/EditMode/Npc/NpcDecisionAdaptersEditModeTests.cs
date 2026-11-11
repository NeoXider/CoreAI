using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Ai.Npc;
using CoreAI.Core.Tests.EditMode.Decisions;
using CoreAI.Decisions;
using NUnit.Framework;

namespace CoreAI.Core.Tests.EditMode.Npc
{
    /// <summary>
    /// The two adapters between the NPC block and decision models: a dialogue actuator that turns a <c>talk</c>
    /// decision into a model turn and a <c>greet</c> decision into a fixed line, and the feed of
    /// <see cref="INpcDirector.NpcAddressed"/> into the NPC's decision policy.
    /// </summary>
    [TestFixture]
    public sealed class NpcDecisionAdaptersEditModeTests
    {
        private sealed class State : INpcStateProvider
        {
            public string DescribeState()
            {
                return "You are Bob, the baker. Steve stands next to you.";
            }
        }

        private sealed class RecordingActuator : INpcActuator
        {
            public readonly List<string> Applied = new();

            public bool Throw;

            public void Apply(NpcDecision decision)
            {
                Applied.Add(decision.ActionId);
                if (Throw)
                {
                    throw new InvalidOperationException("broken body");
                }
            }
        }

        private static NpcDecision Decision(string npcId, string actionId)
        {
            return new NpcDecision { NpcId = npcId, ActionId = actionId, Source = DecisionSource.Model };
        }

        private static (NpcDirector Director, ScriptedNpcOrchestrator Orchestrator, RecordingNpcSink Sink) Village(
            params NpcAgentDefinition[] npcs)
        {
            ScriptedNpcOrchestrator orchestrator = new() { Script = _ => ScriptedNpcTurn.Reply("Morning, lad.") };
            RecordingNpcSink sink = new();
            NpcDirector director = NpcTestWorld.Director(orchestrator, sink, new ManualNpcClock());
            foreach (NpcAgentDefinition npc in npcs)
            {
                director.Add(npc);
            }

            return (director, orchestrator, sink);
        }

        [Test]
        public void Talk_StartsAModelTurnAimedAtThePartner()
        {
            (NpcDirector director, ScriptedNpcOrchestrator orchestrator, RecordingNpcSink sink) =
                Village(NpcTestWorld.Npc("bob", "Bob"));
            using NpcDirector owned = director;
            NpcDialogueActuator actuator = new(director, null,
                () => new NpcDialoguePartner("steve-id", "Steve", new NpcPosition(1d, 0d, 0d)));

            actuator.Apply(Decision("bob", "talk"));

            Assert.AreEqual(NpcDialogueOutcome.Asked, actuator.LastOutcome);
            Assert.AreEqual(1, orchestrator.Requests.Count);
            StringAssert.Contains("Steve is standing next to you", orchestrator.Requests[0].Hint);
            Assert.AreEqual("steve-id", orchestrator.Requests[0].ActorContext.Value.ActorId);
            CollectionAssert.Contains(sink.Completed, "bob:Morning, lad.");
        }

        [Test]
        public void Greet_SaysAFixedLineInRotation_WithoutTheModel()
        {
            (NpcDirector director, ScriptedNpcOrchestrator orchestrator, RecordingNpcSink sink) =
                Village(NpcTestWorld.Npc("bob", "Bob"));
            using NpcDirector owned = director;
            NpcDialogueActuator actuator = new(director, null, () => new NpcDialoguePartner("steve-id", "Steve"));
            List<NpcSaid> said = new();
            director.NpcSaid += said.Add;

            actuator.Apply(Decision("bob", "greet"));
            actuator.Apply(Decision("bob", "greet"));
            actuator.Apply(Decision("bob", "greet"));

            Assert.AreEqual(NpcDialogueOutcome.Said, actuator.LastOutcome);
            CollectionAssert.IsEmpty(orchestrator.Requests, "a greeting never calls the model");
            Assert.AreEqual(3, said.Count);
            Assert.AreEqual("Hello, Steve!", said[0].Text);
            Assert.AreEqual("Good day, Steve.", said[1].Text);
            Assert.AreEqual("Hello, Steve!", said[2].Text);
            Assert.IsTrue(said[0].IsScripted);
        }

        [Test]
        public void NonDialogueAction_IsLeftToTheOtherActuators()
        {
            (NpcDirector director, ScriptedNpcOrchestrator orchestrator, RecordingNpcSink sink) =
                Village(NpcTestWorld.Npc("bob", "Bob"));
            using NpcDirector owned = director;
            NpcDialogueActuator actuator = new(director);

            actuator.Apply(Decision("bob", "wander"));

            Assert.AreEqual(NpcDialogueOutcome.NotDialogue, actuator.LastOutcome);
            Assert.IsFalse(actuator.Handles("wander"));
            CollectionAssert.IsEmpty(orchestrator.Requests);
            CollectionAssert.IsEmpty(sink.Calls);
        }

        [Test]
        public void DialogueDecision_WhileTheNpcIsAnsweringSomeone_IsSkipped_SoTheReplyIsNotCutShort()
        {
            (NpcDirector director, ScriptedNpcOrchestrator orchestrator, RecordingNpcSink sink) =
                Village(NpcTestWorld.Npc("bob", "Bob"));
            using NpcDirector owned = director;
            orchestrator.Script = _ => ScriptedNpcTurn.Held();
            director.Hear(NpcHeardLine.Chat("steve-id", "Steve", "Bob, what is the price of rye?"));
            Assert.IsTrue(director.Agents[0].IsSpeaking);
            NpcDialogueActuator actuator = new(director);

            actuator.Apply(Decision("bob", "talk"));
            actuator.Apply(Decision("bob", "greet"));

            Assert.AreEqual(NpcDialogueOutcome.SkippedBusy, actuator.LastOutcome);
            Assert.AreEqual(1, orchestrator.Requests.Count);
            Assert.IsTrue(director.Agents[0].IsSpeaking, "the reply to the player is still running");
        }

        [Test]
        public void DialogueDecision_WhenAllowedWhileSpeaking_SupersedesTheReply()
        {
            (NpcDirector director, ScriptedNpcOrchestrator orchestrator, RecordingNpcSink sink) =
                Village(NpcTestWorld.Npc("bob", "Bob"));
            using NpcDirector owned = director;
            orchestrator.Script = _ => ScriptedNpcTurn.Held();
            director.Hear(NpcHeardLine.Chat("steve-id", "Steve", "Bob, what is the price of rye?"));
            NpcDialogueActuator actuator = new(director, new NpcDialogueActuatorOptions { SkipWhileSpeaking = false });

            actuator.Apply(Decision("bob", "talk"));

            Assert.AreEqual(NpcDialogueOutcome.Asked, actuator.LastOutcome);
            Assert.AreEqual(2, orchestrator.Requests.Count);
        }

        [Test]
        public void UnknownNpc_IsRefused()
        {
            (NpcDirector director, ScriptedNpcOrchestrator orchestrator, RecordingNpcSink sink) = Village();
            using NpcDirector owned = director;
            NpcDialogueActuator actuator = new(director);

            actuator.Apply(Decision("ghost", "greet"));

            Assert.AreEqual(NpcDialogueOutcome.Refused, actuator.LastOutcome);
            CollectionAssert.IsEmpty(sink.Calls);
        }

        [Test]
        public void WithoutAPartner_TheNpcGreetsAGenericTraveller()
        {
            (NpcDirector director, ScriptedNpcOrchestrator orchestrator, RecordingNpcSink sink) =
                Village(NpcTestWorld.Npc("bob", "Bob"));
            using NpcDirector owned = director;
            NpcDialogueActuator actuator = new(director, null, () => throw new InvalidOperationException("no world"));
            List<NpcSaid> said = new();
            director.NpcSaid += said.Add;

            actuator.Apply(Decision("bob", "greet"));

            Assert.AreEqual("Hello, the traveller!", said[0].Text);
        }

        [Test]
        public void Composite_AppliesEveryActuatorInOrder_AndSurvivesOneThatThrows()
        {
            RecordingActuator broken = new() { Throw = true };
            RecordingActuator body = new();
            CompositeNpcActuator composite = new(new INpcActuator[] { broken, null, body });

            Assert.DoesNotThrow(() => composite.Apply(Decision("bob", "wander")));

            CollectionAssert.AreEqual(new[] { "wander" }, broken.Applied);
            CollectionAssert.AreEqual(new[] { "wander" }, body.Applied);
            Assert.AreEqual(2, composite.Actuators.Count);
        }

        [Test]
        public async Task Feed_ReportsThatTheNpcWasSpokenTo_IntoItsNextAsk()
        {
            (NpcDirector director, ScriptedNpcOrchestrator orchestrator, RecordingNpcSink sink) =
                Village(NpcTestWorld.Npc("bob", "Bob"));
            using NpcDirector owned = director;
            ManualDecisionClock clock = new();
            ScriptedDecisionClient client = new();
            RecordingActuator body = new();
            using NpcDecisionPolicy policy = new(new NpcDecisionPolicyOptions
            {
                NpcId = "bob", Actions = DecisionActionSet.Of("idle", "greet")
            }, client, new State(), null, body, clock);
            using NpcAddressedDecisionFeed feed = new(director);
            feed.Attach("bob", policy);
            await policy.Tick();
            clock.Advance(2.5d);

            director.Hear(NpcHeardLine.Chat("steve-id", "Steve", "Bob, any bread today?"));
            await policy.Tick();

            Assert.AreEqual(1, feed.FedCount);
            Assert.AreEqual(2, client.Calls);
            StringAssert.Contains("spoken_to: Steve spoke to you by name: \"Bob, any bread today?\"",
                client.Requests[1].State);
        }

        [Test]
        public async Task Feed_IgnoresNpcsWithoutAPolicy_AndStopsWhenDetachedOrDisposed()
        {
            (NpcDirector director, ScriptedNpcOrchestrator orchestrator, RecordingNpcSink sink) =
                Village(NpcTestWorld.Npc("bob", "Bob"), NpcTestWorld.Npc("alice", "Alice"));
            using NpcDirector owned = director;
            ManualDecisionClock clock = new();
            ScriptedDecisionClient client = new();
            using NpcDecisionPolicy policy = new(new NpcDecisionPolicyOptions
            {
                NpcId = "bob", Actions = DecisionActionSet.Of("idle")
            }, client, new State(), null, new RecordingActuator(), clock);
            NpcAddressedDecisionFeed feed = new(director);
            IDisposable attachment = feed.Attach("bob", policy);

            director.Hear(NpcHeardLine.Chat("steve-id", "Steve", "Alice, hello"));
            Assert.AreEqual(0, feed.FedCount, "Alice has no brain to feed");
            attachment.Dispose();
            director.Hear(NpcHeardLine.Chat("steve-id", "Steve", "Bob, hello"));
            Assert.AreEqual(0, feed.FedCount, "a detached policy hears nothing");
            feed.Attach("bob", policy);
            feed.Dispose();
            director.Hear(NpcHeardLine.Chat("steve-id", "Steve", "Bob, hello again"));

            Assert.AreEqual(0, feed.FedCount);
            await policy.Tick();
            StringAssert.DoesNotContain("spoken_to", client.Requests[0].State);
        }

        [Test]
        public void Feed_ReplacingAPolicy_FeedsOnlyTheNewOne_AndTheOldHandleCannotRemoveIt()
        {
            (NpcDirector director, ScriptedNpcOrchestrator orchestrator, RecordingNpcSink sink) =
                Village(NpcTestWorld.Npc("bob", "Bob"));
            using NpcDirector owned = director;
            ManualDecisionClock clock = new();
            NpcDecisionPolicyOptions options = new() { NpcId = "bob", Actions = DecisionActionSet.Of("idle") };
            using NpcDecisionPolicy first = new(options, new ScriptedDecisionClient(), new State(), null,
                new RecordingActuator(), clock);
            using NpcDecisionPolicy second = new(options, new ScriptedDecisionClient(), new State(), null,
                new RecordingActuator(), clock);
            using NpcAddressedDecisionFeed feed = new(director);
            IDisposable old = feed.Attach("bob", first);
            feed.Attach("bob", second);

            old.Dispose();
            director.Hear(NpcHeardLine.Chat("steve-id", "Steve", "Bob, hello"));

            Assert.AreEqual(1, feed.FedCount);
        }

        [Test]
        public void Feed_MapCanSkipEvents()
        {
            (NpcDirector director, ScriptedNpcOrchestrator orchestrator, RecordingNpcSink sink) =
                Village(NpcTestWorld.Npc("bob", "Bob"));
            using NpcDirector owned = director;
            using NpcDecisionPolicy policy = new(new NpcDecisionPolicyOptions
            {
                NpcId = "bob", Actions = DecisionActionSet.Of("idle")
            }, new ScriptedDecisionClient(), new State(), null, new RecordingActuator(), new ManualDecisionClock());
            using NpcAddressedDecisionFeed feed = new(director, _ => null);
            feed.Attach("bob", policy);

            director.Hear(NpcHeardLine.Chat("steve-id", "Steve", "Bob, hello"));

            Assert.AreEqual(0, feed.FedCount);
        }

        [Test]
        public void DefaultEvent_NamesHowTheNpcWasAddressed()
        {
            NpcDecisionEvent byName = NpcAddressedDecisionFeed.DefaultEvent(new NpcAddressed("bob", "Bob", "steve-id",
                "Steve", "Bob, hi", NpcAddressReason.Name, 1, ""));
            NpcDecisionEvent followUp = NpcAddressedDecisionFeed.DefaultEvent(new NpcAddressed("bob", "Bob",
                "steve-id", "", "and rye?", NpcAddressReason.FollowUp, 2, ""));

            Assert.AreEqual(NpcDialogueEventKinds.SpokenTo, byName.Kind);
            Assert.AreEqual("Steve spoke to you by name: \"Bob, hi\"", byName.Description);
            Assert.AreEqual("steve-id went on talking to you: \"and rye?\"", followUp.Description);
        }

        [Test]
        public async Task WithoutADecisionModel_TheRuleTableGreetsBack_AndSaysItWasTheFallback()
        {
            (NpcDirector director, ScriptedNpcOrchestrator orchestrator, RecordingNpcSink sink) =
                Village(NpcTestWorld.Npc("bob", "Bob"));
            using NpcDirector owned = director;
            ManualDecisionClock clock = new();
            DecisionRouter router = new(new DecisionRouteTable(), new StaticDecisionEndpointSource());
            DecisionClientProvider provider = new(router, new NeverBuiltFactory(), clock);
            InMemoryDecisionTraceSink trace = new();
            NpcDialogueActuator dialogue = new(director, null, () => new NpcDialoguePartner("steve-id", "Steve"));
            using NpcDecisionPolicy policy = new(new NpcDecisionPolicyOptions
                {
                    NpcId = "bob", Actions = DecisionActionSet.Of("idle", "greet", "talk")
                }, provider.ForConsumer(DecisionConsumers.NpcBrain), new State(),
                new RuleTableNpcFallbackPolicy("idle", new Dictionary<string, string>
                {
                    [NpcDialogueEventKinds.SpokenTo] = "greet"
                }),
                new CompositeNpcActuator(new INpcActuator[] { dialogue }), clock, trace);
            using NpcAddressedDecisionFeed feed = new(director);
            feed.Attach("bob", policy);
            List<NpcSaid> said = new();
            director.NpcSaid += said.Add;

            director.Hear(NpcHeardLine.Chat("steve-id", "Steve", "Bob, hello"));
            await policy.Tick();

            Assert.AreEqual("greet", policy.Current.ActionId);
            Assert.AreEqual(DecisionSource.Fallback, policy.Current.Source);
            Assert.AreEqual(DecisionFallbackReasons.CallFailed, policy.Current.FallbackReason);
            Assert.AreEqual(DecisionErrorReasons.NoRoute, policy.Current.ErrorReason);
            Assert.AreEqual(1, trace.FallbackCount);
            Assert.AreEqual("Hello, Steve!", said[said.Count - 1].Text);
        }

        private sealed class NeverBuiltFactory : IDecisionClientFactory
        {
            public IDecisionClient Create(LlmEndpointDescriptor endpoint)
            {
                throw new InvalidOperationException("routing must refuse before a backend is built");
            }
        }
    }
}
