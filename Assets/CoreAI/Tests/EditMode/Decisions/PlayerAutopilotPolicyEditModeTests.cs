using System.Collections.Generic;
using System.Threading.Tasks;
using CoreAI.Decisions;
using NUnit.Framework;

namespace CoreAI.Core.Tests.EditMode.Decisions
{
    /// <summary>
    /// The player autopilot: opt-in, goal choice on the slow tick, the threshold fallback, and the instant hand-back
    /// on player input with the late answer dropped.
    /// </summary>
    [TestFixture]
    public sealed class PlayerAutopilotPolicyEditModeTests
    {
        private static PlayerAutopilotPolicy Policy(ScriptedDecisionClient client, ManualDecisionClock clock,
            RecordingAutopilotActuator actuator, InMemoryDecisionTraceSink trace = null)
        {
            return new PlayerAutopilotPolicy(new PlayerAutopilotOptions
            {
                Goals = new DecisionActionSet(new[]
                {
                    new DecisionActionDefinition("gather", "Collect wood and stone."),
                    new DecisionActionDefinition("build", "Place blocks for a shelter."),
                    new DecisionActionDefinition("follow", "Follow the nearest friend."),
                    new DecisionActionDefinition("flee", "Run from danger.")
                })
            }, client, new FixedState(), new KeepGoalAutopilotFallbackPolicy("gather"), actuator, clock, trace);
        }

        [Test]
        public async Task Disengaged_ByDefault_NeverAsks()
        {
            ManualDecisionClock clock = new();
            ScriptedDecisionClient client = new();
            RecordingAutopilotActuator actuator = new();
            PlayerAutopilotPolicy policy = Policy(client, clock, actuator);

            for (int frame = 0; frame < 600; frame++)
            {
                await policy.Tick();
                clock.Advance(1d / 60d);
            }

            Assert.IsFalse(policy.Engaged);
            Assert.AreEqual(0, client.Calls);
            Assert.AreEqual(0, actuator.Pursued.Count);
        }

        [Test]
        public async Task Engaged_AsksOnTheSlowTick_AndPursuesTheGoal()
        {
            ManualDecisionClock clock = new();
            ScriptedDecisionClient client = new() { Answer = r => DecisionFixtures.Answer(r, "build", 0.8) };
            RecordingAutopilotActuator actuator = new();
            PlayerAutopilotPolicy policy = Policy(client, clock, actuator);

            policy.Engage();
            for (int frame = 0; frame < 600; frame++)
            {
                await policy.Tick();
                clock.Advance(1d / 60d);
            }

            Assert.LessOrEqual(client.Calls, 6, "10 s at one ask per 2 s");
            Assert.AreEqual("build", actuator.Pursued[0].GoalId);
            Assert.AreEqual(DecisionSource.Model, actuator.Pursued[0].Source);
            Assert.AreEqual(DecisionConsumers.Autopilot, client.Requests[0].ConsumerId);
            Assert.AreEqual("goal", client.Requests[0].Questions[0].Id);
        }

        [Test]
        public async Task PlayerInput_ReleasesBeforeReturning_AndDropsTheLateAnswer()
        {
            ManualDecisionClock clock = new();
            ScriptedDecisionClient client = new() { HoldAnswers = true, HonourCancellation = false };
            RecordingAutopilotActuator actuator = new();
            List<string> released = new();
            PlayerAutopilotPolicy policy = Policy(client, clock, actuator);
            policy.Released += released.Add;
            policy.Engage();

            Task asking = policy.Tick();
            policy.NotifyPlayerInput();
            int releasesAtReturn = actuator.Releases;
            client.CompleteNext(DecisionFixtures.Answer(client.Requests[0], "flee", 0.95));
            await asking;

            Assert.AreEqual(1, releasesAtReturn, "control is back before NotifyPlayerInput returns");
            Assert.IsFalse(policy.Engaged);
            Assert.IsTrue(client.Tokens[0].IsCancellationRequested, "the call in flight is cancelled");
            Assert.AreEqual(0, actuator.Pursued.Count, "a late answer never takes control back");
            Assert.AreEqual(new[] { PlayerAutopilotPolicy.ReleasedByPlayerInput }, released);
        }

        [Test]
        public async Task PlayerInput_StopsFurtherAsks_UntilReEngaged()
        {
            ManualDecisionClock clock = new();
            ScriptedDecisionClient client = new();
            RecordingAutopilotActuator actuator = new();
            PlayerAutopilotPolicy policy = Policy(client, clock, actuator);
            policy.Engage();
            await policy.Tick();

            policy.NotifyPlayerInput();
            clock.Advance(10);
            await policy.Tick();
            Assert.AreEqual(1, client.Calls);

            policy.Engage();
            await policy.Tick();
            Assert.AreEqual(2, client.Calls, "re-engaging asks at once");
        }

        [Test]
        public void PlayerInput_WhenDisengaged_DoesNothing()
        {
            RecordingAutopilotActuator actuator = new();
            PlayerAutopilotPolicy policy = Policy(new ScriptedDecisionClient(), new ManualDecisionClock(), actuator);

            policy.NotifyPlayerInput();

            Assert.AreEqual(0, actuator.Releases);
        }

        [Test]
        public async Task LowConfidence_KeepsTheCurrentGoal_AndIsMarked()
        {
            ManualDecisionClock clock = new();
            double confidence = 0.9;
            string goal = "follow";
            ScriptedDecisionClient client = new() { Answer = r => DecisionFixtures.Answer(r, goal, confidence) };
            RecordingAutopilotActuator actuator = new();
            InMemoryDecisionTraceSink trace = new();
            PlayerAutopilotPolicy policy = Policy(client, clock, actuator, trace);
            policy.Engage();
            await policy.Tick();

            confidence = 0.4;
            goal = "flee";
            clock.Advance(2);
            await policy.Tick();

            AutopilotDecision second = actuator.Pursued[1];
            Assert.AreEqual("follow", second.GoalId, "the fallback keeps the current goal");
            Assert.AreEqual(DecisionSource.Fallback, second.Source);
            Assert.AreEqual(DecisionFallbackReasons.LowConfidence, second.FallbackReason);
            Assert.AreEqual(1, trace.FallbackCount);
        }

        [Test]
        public async Task Events_ReachTheNextAsk()
        {
            ManualDecisionClock clock = new();
            ScriptedDecisionClient client = new();
            PlayerAutopilotPolicy policy = Policy(client, clock, new RecordingAutopilotActuator());
            policy.Engage();
            policy.Notify("a creeper is 3 blocks away");

            await policy.Tick();

            StringAssert.Contains("a creeper is 3 blocks away", client.Requests[0].State);
        }

        [Test]
        public async Task Dispose_ReleasesControl()
        {
            RecordingAutopilotActuator actuator = new();
            PlayerAutopilotPolicy policy = Policy(new ScriptedDecisionClient(), new ManualDecisionClock(), actuator);
            policy.Engage();
            await policy.Tick();

            policy.Dispose();
            policy.Engage();

            Assert.AreEqual(1, actuator.Releases);
            Assert.IsFalse(policy.Engaged, "a disposed autopilot cannot be engaged");
        }

        private sealed class FixedState : IAutopilotStateProvider
        {
            public string DescribeState() => "wood 2, stone 0, night falls in 30 s";
        }

        internal sealed class RecordingAutopilotActuator : IAutopilotActuator
        {
            public List<AutopilotDecision> Pursued { get; } = new();

            public int Releases { get; private set; }

            public void Pursue(AutopilotDecision decision)
            {
                Pursued.Add(decision);
            }

            public void Release()
            {
                Releases++;
            }
        }
    }
}
