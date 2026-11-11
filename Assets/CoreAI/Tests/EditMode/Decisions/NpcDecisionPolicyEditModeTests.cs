using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Decisions;
using NUnit.Framework;

namespace CoreAI.Core.Tests.EditMode.Decisions
{
    /// <summary>
    /// The NPC brain with a scripted decision client and a fake clock: its tick rate over a minute of frames,
    /// never per frame, events, the confidence threshold and every failure going to the deterministic fallback,
    /// which is marked as such.
    /// </summary>
    [TestFixture]
    public sealed class NpcDecisionPolicyEditModeTests
    {
        private const double Frame = 1d / 60d;

        private static NpcDecisionPolicyOptions Options(double minSpacing = 2d, double idleTick = 2d)
        {
            return new NpcDecisionPolicyOptions
            {
                NpcId = "goblin-1",
                Actions = new DecisionActionSet(new[]
                {
                    new DecisionActionDefinition("attack", "Hit the player."),
                    new DecisionActionDefinition("flee", "Run away."),
                    new DecisionActionDefinition("trade", "Offer a deal."),
                    new DecisionActionDefinition("idle")
                }),
                MinSecondsBetweenAsks = minSpacing,
                IdleTickSeconds = idleTick
            };
        }

        private static NpcDecisionPolicy Policy(ScriptedDecisionClient client, ManualDecisionClock clock,
            RecordingNpcActuator actuator, IDecisionTraceSink trace = null, NpcDecisionPolicyOptions options = null,
            INpcFallbackPolicy fallback = null)
        {
            return new NpcDecisionPolicy(options ?? Options(), client, new FixedState("hp 3/10, player 4 m away"),
                fallback ?? new RuleTableNpcFallbackPolicy("idle", new Dictionary<string, string>
                {
                    { NpcDecisionEventKinds.Attacked, "flee" },
                    { NpcDecisionEventKinds.TradeOffered, "trade" }
                }), actuator, clock, trace);
        }

        [Test]
        public async Task SixtySecondsOfFrames_AskAtMostOncePerTick()
        {
            ManualDecisionClock clock = new();
            ScriptedDecisionClient client = new() { Answer = r => DecisionFixtures.Answer(r, "attack", 0.9) };
            RecordingNpcActuator actuator = new();
            NpcDecisionPolicy policy = Policy(client, clock, actuator);

            for (int frame = 0; frame < 3600; frame++)
            {
                await policy.Tick();
                clock.Advance(Frame);
            }

            Assert.LessOrEqual(client.Calls, 31, "60 s at one ask per 2 s, counting the ask at t = 0");
            Assert.GreaterOrEqual(client.Calls, 30);
            Assert.AreEqual(client.Calls, policy.AskCount);
            Assert.AreEqual(client.Calls, actuator.Applied.Count);
        }

        [Test]
        public async Task ManyTicksInOneFrame_AskOnce()
        {
            ManualDecisionClock clock = new();
            ScriptedDecisionClient client = new();
            NpcDecisionPolicy policy = Policy(client, clock, new RecordingNpcActuator());

            for (int i = 0; i < 100; i++)
            {
                await policy.Tick();
            }

            Assert.AreEqual(1, client.Calls);
        }

        [Test]
        public async Task EventFlood_StillRespectsTheSpacing()
        {
            ManualDecisionClock clock = new();
            ScriptedDecisionClient client = new();
            NpcDecisionPolicy policy = Policy(client, clock, new RecordingNpcActuator());

            for (int frame = 0; frame < 3600; frame++)
            {
                policy.Notify(new NpcDecisionEvent(NpcDecisionEventKinds.Attacked, "hit"));
                await policy.Tick();
                clock.Advance(Frame);
            }

            Assert.LessOrEqual(client.Calls, 31);
        }

        [Test]
        public async Task Event_AsksBeforeTheIdleTick_WhenTheSpacingAllows()
        {
            ManualDecisionClock clock = new();
            ScriptedDecisionClient client = new();
            NpcDecisionPolicy policy = Policy(client, clock, new RecordingNpcActuator(),
                options: Options(minSpacing: 1d, idleTick: 5d));

            await policy.Tick();
            clock.Advance(1.2);
            await policy.Tick();
            Assert.AreEqual(1, client.Calls, "no event: the idle tick waits 5 s");

            policy.Notify(new NpcDecisionEvent(NpcDecisionEventKinds.PlayerApproached, "the player is 1 m away"));
            await policy.Tick();

            Assert.AreEqual(2, client.Calls);
            StringAssert.Contains("player_approached: the player is 1 m away", client.Requests[1].State);
            StringAssert.Contains("hp 3/10", client.Requests[1].State);
        }

        [Test]
        public async Task Request_IsAChoiceOverTheClosedActionList()
        {
            ManualDecisionClock clock = new();
            ScriptedDecisionClient client = new();
            NpcDecisionPolicy policy = Policy(client, clock, new RecordingNpcActuator());

            await policy.Tick();

            DecisionRequest request = client.Requests[0];
            Assert.AreEqual(DecisionConsumers.NpcBrain, request.ConsumerId);
            DecisionQuestion question = request.Questions[0];
            Assert.AreEqual(DecisionQuestionKind.Choice, question.Kind);
            Assert.AreEqual(4, question.Options.Count);
            Assert.AreEqual("Hit the player.", question.Options[0].Description);
        }

        [Test]
        public async Task ConfidentAnswer_IsAppliedAsAModelDecision()
        {
            ManualDecisionClock clock = new();
            ScriptedDecisionClient client = new() { Answer = r => DecisionFixtures.Answer(r, "trade", 0.85) };
            RecordingNpcActuator actuator = new();
            InMemoryDecisionTraceSink trace = new();
            NpcDecisionPolicy policy = Policy(client, clock, actuator, trace);

            await policy.Tick();

            Assert.AreEqual("trade", actuator.Applied[0].ActionId);
            Assert.AreEqual(DecisionSource.Model, actuator.Applied[0].Source);
            Assert.AreEqual(0.85, actuator.Applied[0].Confidence, 1e-9);
            Assert.AreEqual(0, trace.FallbackCount);
            Assert.AreSame(actuator.Applied[0], policy.Current);
        }

        [Test]
        public async Task AnswerBelowTheThreshold_GoesToTheFallback_AndIsMarked()
        {
            ManualDecisionClock clock = new();
            ScriptedDecisionClient client = new() { Answer = r => DecisionFixtures.Answer(r, "attack", 0.55) };
            RecordingNpcActuator actuator = new();
            InMemoryDecisionTraceSink trace = new();
            NpcDecisionPolicy policy = Policy(client, clock, actuator, trace);
            policy.Notify(new NpcDecisionEvent(NpcDecisionEventKinds.Attacked, "the player hit you"));

            await policy.Tick();

            NpcDecision decision = actuator.Applied[0];
            Assert.AreEqual("flee", decision.ActionId, "the rule table maps attacked to flee");
            Assert.AreEqual(DecisionSource.Fallback, decision.Source);
            Assert.AreEqual(DecisionFallbackReasons.LowConfidence, decision.FallbackReason);
            Assert.AreEqual(0.55, decision.Confidence, 1e-9);
            Assert.AreEqual(new[] { NpcDecisionEventKinds.Attacked }, decision.TriggerEventKinds);
            Assert.AreEqual(1, trace.FallbackCount);
            StringAssert.Contains("Fallback: low_confidence", trace.Snapshot()[0].ToString());
        }

        [Test]
        public async Task FailedCall_GoesToTheFallback_WithTheErrorCode()
        {
            ManualDecisionClock clock = new();
            ScriptedDecisionClient client = new()
            {
                Answer = r => DecisionResult.Failure(LlmErrorCode.ClientLimitExceeded,
                    DecisionErrorReasons.RateBudgetExceeded, "spent")
            };
            RecordingNpcActuator actuator = new();
            NpcDecisionPolicy policy = Policy(client, clock, actuator);

            await policy.Tick();

            NpcDecision decision = actuator.Applied[0];
            Assert.AreEqual(DecisionSource.Fallback, decision.Source);
            Assert.AreEqual(DecisionFallbackReasons.CallFailed, decision.FallbackReason);
            Assert.AreEqual(LlmErrorCode.ClientLimitExceeded, decision.ErrorCode);
            Assert.AreEqual(DecisionErrorReasons.RateBudgetExceeded, decision.ErrorReason);
            Assert.AreEqual("idle", decision.ActionId, "the rule table's default");
        }

        [Test]
        public async Task ThrowingClient_GoesToTheFallback()
        {
            ManualDecisionClock clock = new();
            ScriptedDecisionClient client = new() { Throw = new InvalidOperationException("bug") };
            RecordingNpcActuator actuator = new();
            NpcDecisionPolicy policy = Policy(client, clock, actuator);

            await policy.Tick();

            Assert.AreEqual(DecisionErrorReasons.ClientThrew, actuator.Applied[0].ErrorReason);
            Assert.AreEqual(DecisionSource.Fallback, actuator.Applied[0].Source);
            Assert.IsFalse(policy.Asking);
        }

        [Test]
        public async Task ModelChoiceOutsideTheList_GoesToTheFallback()
        {
            ManualDecisionClock clock = new();
            ScriptedDecisionClient client = new()
            {
                Answer = r => DecisionResult.Success(new[]
                {
                    new DecisionAnswer("action", DecisionQuestionKind.Choice, new[] { "dance" }, new[] { 1d }, 0, 0.99, true)
                }, new LlmUsageRecord(), "m", "scripted")
            };
            RecordingNpcActuator actuator = new();
            NpcDecisionPolicy policy = Policy(client, clock, actuator);

            await policy.Tick();

            Assert.AreEqual(DecisionFallbackReasons.UnknownAction, actuator.Applied[0].FallbackReason);
            Assert.AreEqual("idle", actuator.Applied[0].ActionId);
        }

        [Test]
        public async Task FallbackReturningAnUnknownAction_IsClampedToTheList()
        {
            ManualDecisionClock clock = new();
            ScriptedDecisionClient client = new() { Answer = r => DecisionFixtures.Answer(r, "attack", 0.1) };
            RecordingNpcActuator actuator = new();
            NpcDecisionPolicy policy = Policy(client, clock, actuator, fallback: new FixedFallback("teleport"));

            await policy.Tick();

            Assert.AreEqual("attack", actuator.Applied[0].ActionId, "the first action of the list");
        }

        [Test]
        public async Task WhileACallIsInFlight_NoSecondCallStarts()
        {
            ManualDecisionClock clock = new();
            ScriptedDecisionClient client = new() { HoldAnswers = true };
            RecordingNpcActuator actuator = new();
            NpcDecisionPolicy policy = Policy(client, clock, actuator);

            Task first = policy.Tick();
            clock.Advance(10);
            await policy.Tick();
            Assert.AreEqual(1, client.Calls);
            Assert.IsTrue(policy.Asking);

            client.CompleteNext(DecisionFixtures.Answer(client.Requests[0], "flee", 0.9));
            await first;
            Task second = policy.Tick();

            Assert.AreEqual(2, client.Calls, "due again once the first call finished");
            Assert.IsFalse(second.IsCompleted, "the second call is held by the script");
            Assert.AreEqual("flee", actuator.Applied[0].ActionId);
        }

        [Test]
        public async Task Dispose_CancelsTheCall_AndAppliesNothing()
        {
            ManualDecisionClock clock = new();
            ScriptedDecisionClient client = new() { HoldAnswers = true };
            RecordingNpcActuator actuator = new();
            NpcDecisionPolicy policy = Policy(client, clock, actuator);

            Task pending = policy.Tick();
            policy.Dispose();
            await pending;
            await policy.Tick();

            Assert.IsTrue(client.Tokens[0].IsCancellationRequested);
            Assert.AreEqual(0, actuator.Applied.Count);
            Assert.AreEqual(1, client.Calls);
        }

        [Test]
        public void ZeroSpacing_IsRefused_TheModelIsNeverAskedPerFrame()
        {
            Assert.Throws<ArgumentException>(() => Policy(new ScriptedDecisionClient(), new ManualDecisionClock(),
                new RecordingNpcActuator(), options: Options(minSpacing: 0d)));
        }

        [Test]
        public async Task OldEvents_AreDroppedBeyondTheMemory()
        {
            ManualDecisionClock clock = new();
            ScriptedDecisionClient client = new();
            NpcDecisionPolicyOptions options = Options();
            options.MaxRememberedEvents = 2;
            NpcDecisionPolicy policy = Policy(client, clock, new RecordingNpcActuator(), options: options);

            policy.Notify(new NpcDecisionEvent("e1"));
            policy.Notify(new NpcDecisionEvent("e2"));
            policy.Notify(new NpcDecisionEvent("e3"));
            await policy.Tick();

            StringAssert.DoesNotContain("e1", client.Requests[0].State);
            StringAssert.Contains("e2", client.Requests[0].State);
            StringAssert.Contains("e3", client.Requests[0].State);
        }

        private sealed class FixedState : INpcStateProvider
        {
            private readonly string _state;

            public FixedState(string state)
            {
                _state = state;
            }

            public string DescribeState() => _state;
        }

        private sealed class FixedFallback : INpcFallbackPolicy
        {
            private readonly string _action;

            public FixedFallback(string action)
            {
                _action = action;
            }

            public string ChooseAction(NpcFallbackContext context) => _action;
        }

        internal sealed class RecordingNpcActuator : INpcActuator
        {
            public List<NpcDecision> Applied { get; } = new();

            public void Apply(NpcDecision decision)
            {
                Applied.Add(decision);
            }
        }
    }
}
