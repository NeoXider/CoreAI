using System;
using System.Collections.Generic;
using CoreAI.ChatOverlay;
using CoreAI.Decisions;
using CoreAI.Mods.Rbx.Agents;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances.Scheduling;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Agents
{
    /// <summary>
    /// The local player's autopilot over a headless Rbx world: driven by the deterministic fallback when no decision
    /// endpoint answers (and saying so), by the model when one does; ended at once by real input but not by typing in
    /// the chat; asked for and ended by a mod through the Player's <c>Autopilot</c> attribute; goals and facts from
    /// the mod; and ticked by the world's own Heartbeat.
    /// </summary>
    [TestFixture]
    public sealed class RbxCharacterAutopilotEditModeTests
    {
        private static readonly RbxVector3 Origin = new(0f, 3f, 0f);

        private static RbxCharacterAutopilot Autopilot(AgentsWorld world, AnsweringDecisionClient client,
            StepClock clock, RbxCharacterAutopilotOptions options = null)
        {
            return new RbxCharacterAutopilot(world.World, client, clock, null, null, options);
        }

        [Test]
        public void WithoutADecisionModel_TheFallbackWanders_AndTheCommandSaysSo()
        {
            AgentsWorld world = new();
            world.AddPlayer("steve", Origin);
            AnsweringDecisionClient client = new();
            using RbxCharacterAutopilot autopilot = Autopilot(world, client, new StepClock());
            ChatCommandRegistry registry = new();
            using IDisposable command = AutopilotChatCommand.Register(registry, autopilot.Controller);

            ChatDispatchResult on = new ChatCommandDispatcher(registry).Dispatch("/autopilot on");
            autopilot.Tick();

            Assert.AreEqual(ChatDispatchKind.Handled, on.Kind);
            Assert.AreEqual(1, client.Requests.Count);
            Assert.AreEqual("wander", autopilot.Actuator.CurrentGoalId);
            Assert.AreEqual(1, world.PlayerMotor.MoveTargets.Count, "the character walks");
            Assert.AreEqual(true, world.Player.GetAttribute(RbxAgentAttributes.Autopilot));
            Assert.AreEqual("wander", world.Player.GetAttribute(RbxAgentAttributes.AutopilotGoal));
            string status = new ChatCommandDispatcher(registry).Dispatch("/autopilot status").Reply;
            StringAssert.Contains("goal 'wander', chosen by the fallback: the decision call failed " +
                                  "(RoutingError/no_route)", status);
        }

        [Test]
        public void WithAConfidentModel_ItsGoalIsPursued()
        {
            AgentsWorld world = new();
            world.AddPlayer("steve", Origin);
            world.AddVillager("Bob", new RbxVector3(20f, 3f, 0f), out _, out _);
            AnsweringDecisionClient client = new() { Answer = r => AnsweringDecisionClient.Choose(r, "follow") };
            using RbxCharacterAutopilot autopilot = Autopilot(world, client, new StepClock());

            autopilot.Controller.Engage();
            autopilot.Tick();

            Assert.AreEqual("follow", autopilot.Actuator.CurrentGoalId);
            Assert.AreEqual(16f, world.PlayerMotor.LastTarget.Value.X, 1e-3f);
            StringAssert.Contains("Bob 20 studs east", client.Requests[0].State, "the model sees the villager");
        }

        [Test]
        public void AMovementKey_EndsTheAutopilotAtOnce_AndStopsItsWalk()
        {
            AgentsWorld world = new();
            world.AddPlayer("steve", Origin);
            List<string> released = new();
            using RbxCharacterAutopilot autopilot = Autopilot(world, new AnsweringDecisionClient(), new StepClock());
            autopilot.Controller.EngagedChanged += (engaged, reason) =>
            {
                if (!engaged)
                {
                    released.Add(reason);
                }
            };
            autopilot.Controller.Engage();
            autopilot.Tick();

            world.Input.Keys.Add(AgentsWorld.KeyW);
            autopilot.Tick();

            Assert.IsFalse(autopilot.Controller.Engaged);
            CollectionAssert.AreEqual(new[] { PlayerAutopilotPolicy.ReleasedByPlayerInput }, released);
            Assert.AreEqual(Origin, world.PlayerMotor.LastTarget, "its own walk was stopped where it stood");
            Assert.AreEqual(false, world.Player.GetAttribute(RbxAgentAttributes.Autopilot));
            Assert.AreEqual("", world.Player.GetAttribute(RbxAgentAttributes.AutopilotGoal));
        }

        [Test]
        public void TypingInTheChat_DoesNotEndTheAutopilot()
        {
            AgentsWorld world = new();
            world.AddPlayer("steve", Origin);
            StepClock clock = new();
            using RbxCharacterAutopilot autopilot = Autopilot(world, new AnsweringDecisionClient(), clock);
            autopilot.Controller.Engage();
            autopilot.Tick();

            world.Input.Focus.SetCaptured("coreai.ui", true);
            world.Input.Keys.Add(AgentsWorld.KeyW);
            for (int frame = 0; frame < 30; frame++)
            {
                clock.NowSeconds += 1d / 30d;
                autopilot.Tick();
            }

            Assert.IsTrue(autopilot.Controller.Engaged);
        }

        [Test]
        public void TheChatKey_DoesNotEndTheAutopilot()
        {
            AgentsWorld world = new();
            world.AddPlayer("steve", Origin);
            using RbxCharacterAutopilot autopilot = Autopilot(world, new AnsweringDecisionClient(), new StepClock());
            autopilot.Controller.Engage();

            world.Input.Keys.Add(AgentsWorld.KeyT);
            autopilot.Tick();

            Assert.IsTrue(autopilot.Controller.Engaged);
        }

        [Test]
        public void WithoutACharacter_TheAutopilotRefusesToStart()
        {
            AgentsWorld world = new();
            using RbxCharacterAutopilot autopilot = Autopilot(world, new AnsweringDecisionClient(), new StepClock());

            AutopilotEngageResult result = autopilot.Controller.Engage();

            Assert.IsFalse(result.Engaged);
            Assert.AreEqual("No player is in the world yet.", result.Message);
        }

        [Test]
        public void ACharacterThatDies_EndsTheAutopilot()
        {
            AgentsWorld world = new();
            world.AddPlayer("steve", Origin);
            List<string> released = new();
            using RbxCharacterAutopilot autopilot = Autopilot(world, new AnsweringDecisionClient(), new StepClock());
            autopilot.Controller.EngagedChanged += (engaged, reason) => released.Add(engaged + ":" + reason);
            autopilot.Controller.Engage();

            world.PlayerHumanoid.Health = 0d;
            autopilot.Tick();

            Assert.IsFalse(autopilot.Controller.Engaged);
            CollectionAssert.Contains(released, "False:" + RbxCharacterAutopilot.ReleasedCharacterLost);
        }

        [Test]
        public void AModAsksForTheAutopilot_ThroughTheAttribute_AndEndsItTheSameWay()
        {
            AgentsWorld world = new();
            world.AddPlayer("steve", Origin);
            using RbxCharacterAutopilot autopilot = Autopilot(world, new AnsweringDecisionClient(), new StepClock());

            world.Player.SetAttribute(RbxAgentAttributes.Autopilot, true);
            autopilot.Tick();
            Assert.IsTrue(autopilot.Controller.Engaged);

            world.Player.SetAttribute(RbxAgentAttributes.Autopilot, false);
            autopilot.Tick();

            Assert.IsFalse(autopilot.Controller.Engaged);
            StringAssert.StartsWith("Autopilot off (world).",
                AutopilotChatCommand.DescribeStatus(autopilot.Controller.GetStatus()));
        }

        [Test]
        public void ARefusedModRequest_IsAnsweredWithFalse()
        {
            AgentsWorld world = new();
            world.AddPlayer("steve", Origin);
            world.PlayerHumanoid.Health = 0d;
            using RbxCharacterAutopilot autopilot = Autopilot(world, new AnsweringDecisionClient(), new StepClock());

            world.Player.SetAttribute(RbxAgentAttributes.Autopilot, true);
            autopilot.Tick();

            Assert.IsFalse(autopilot.Controller.Engaged);
            Assert.AreEqual(false, world.Player.GetAttribute(RbxAgentAttributes.Autopilot));
        }

        [Test]
        public void WithAttributesOff_TheWorldCanNeitherAskNorSeeIt()
        {
            AgentsWorld world = new();
            world.AddPlayer("steve", Origin);
            using RbxCharacterAutopilot autopilot = Autopilot(world, new AnsweringDecisionClient(), new StepClock(),
                new RbxCharacterAutopilotOptions { UseWorldAttributes = false });

            world.Player.SetAttribute(RbxAgentAttributes.Autopilot, true);
            autopilot.Tick();
            Assert.IsFalse(autopilot.Controller.Engaged);
            world.Player.SetAttribute(RbxAgentAttributes.Autopilot, null);

            autopilot.Controller.Engage();
            autopilot.Tick();

            Assert.IsNull(world.Player.GetAttribute(RbxAgentAttributes.Autopilot));
            Assert.IsNull(world.Player.GetAttribute(RbxAgentAttributes.AutopilotGoal));
        }

        [Test]
        public void TheModsGoals_AndFacts_ReachTheModel_AndItsGoalsArePublishedForTheMod()
        {
            AgentsWorld world = new();
            world.AddPlayer("steve", Origin);
            world.Player.SetAttribute(RbxAgentAttributes.AutopilotGoals,
                "gather: collect wood, then stone; build: place blocks for a shelter; follow");
            world.Player.SetAttribute(RbxAgentAttributes.AutopilotState, "Night falls in 30 seconds.");
            AnsweringDecisionClient client = new() { Answer = r => AnsweringDecisionClient.Choose(r, "build") };
            using RbxCharacterAutopilot autopilot = Autopilot(world, client, new StepClock());

            autopilot.Controller.Engage();
            autopilot.Tick();

            DecisionQuestion question = client.Requests[0].Questions[0];
            Assert.AreEqual(3, question.Options.Count);
            Assert.AreEqual("collect wood, then stone", question.Options[0].Description);
            StringAssert.Contains("Night falls in 30 seconds.", client.Requests[0].State);
            Assert.AreEqual("build", world.Player.GetAttribute(RbxAgentAttributes.AutopilotGoal));
            CollectionAssert.IsEmpty(world.PlayerMotor.MoveTargets, "building is the mod's rule, not C#'s");
        }

        [Test]
        public void ParseGoals_SplitsOnCommasOrSemicolons()
        {
            DecisionActionSet commas = RbxCharacterAutopilotOptions.ParseGoals(" gather, build ,gather,, flee ");
            DecisionActionSet semicolons = RbxCharacterAutopilotOptions.ParseGoals("a: one, two; b");

            Assert.AreEqual(3, commas.Actions.Count);
            Assert.AreEqual("flee", commas.Actions[2].Id);
            Assert.AreEqual("one, two", semicolons.Actions[0].Description);
            Assert.IsNull(RbxCharacterAutopilotOptions.ParseGoals("  "));
        }

        [Test]
        public void AnotherWorld_EndsTheAutopilot()
        {
            AgentsWorld world = new();
            world.AddPlayer("steve", Origin);
            List<string> released = new();
            using RbxCharacterAutopilot autopilot = Autopilot(world, new AnsweringDecisionClient(), new StepClock());
            autopilot.Tick();
            autopilot.Controller.EngagedChanged += (engaged, reason) => released.Add(engaged + ":" + reason);
            autopilot.Controller.Engage();

            AgentsWorld next = new();
            next.AddPlayer("steve", Origin);
            world.Game = next.Game;
            autopilot.Tick();

            Assert.IsFalse(autopilot.Controller.Engaged);
            CollectionAssert.Contains(released, "False:" + RbxCharacterAutopilot.ReleasedWorldChanged);
        }

        [Test]
        public void TheWorldsHeartbeat_TicksTheAutopilot_AndOtherPhasesDoNot()
        {
            AgentsWorld world = new();
            world.AddPlayer("steve", Origin);
            AnsweringDecisionClient client = new();
            using RbxCharacterAutopilot autopilot = Autopilot(world, client, new StepClock());
            ModScheduler scheduler = AgentsWorld.Scheduler();
            IDisposable link = autopilot.AttachScheduler(scheduler);
            Assert.AreSame(link, autopilot.AttachScheduler(scheduler), "attaching twice ticks once");
            autopilot.Controller.Engage();

            scheduler.Advance(1d / 60d);
            Assert.AreEqual(1, client.Requests.Count, "the Heartbeat asked once");

            world.Input.Keys.Add(AgentsWorld.KeyW);
            scheduler.Advance(1d / 60d);
            Assert.IsFalse(autopilot.Controller.Engaged, "input seen on the next Heartbeat");

            link.Dispose();
            world.Input.Keys.Clear();
            autopilot.Controller.Engage();
            scheduler.Advance(1d / 60d);
            Assert.AreEqual(1, client.Requests.Count, "a detached scheduler ticks nothing");
        }

        [Test]
        public void Dispose_HandsControlBack_AndLeavesTheScheduler()
        {
            AgentsWorld world = new();
            world.AddPlayer("steve", Origin);
            AnsweringDecisionClient client = new();
            RbxCharacterAutopilot autopilot = Autopilot(world, client, new StepClock());
            ModScheduler scheduler = AgentsWorld.Scheduler();
            autopilot.AttachScheduler(scheduler);
            autopilot.Controller.Engage();

            autopilot.Dispose();
            scheduler.Advance(1d / 60d);

            Assert.IsFalse(autopilot.Controller.Engaged);
            Assert.AreEqual(0, client.Requests.Count);
            Assert.AreEqual(false, world.Player.GetAttribute(RbxAgentAttributes.Autopilot));
        }
    }
}
