using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.ChatOverlay;
using CoreAI.Decisions;
using NUnit.Framework;

namespace CoreAI.Core.Tests.EditMode.Decisions
{
    /// <summary>
    /// The host side of the player autopilot and its <c>/autopilot</c> command: engagement gated by the character's
    /// readiness, a fresh goal list per engagement, the instant hand-back on player input, and a status that says
    /// which model decides, or that the fallback does and why. Deterministic without any decision endpoint.
    /// </summary>
    [TestFixture]
    public sealed class AutopilotControllerEditModeTests
    {
        private sealed class State : IAutopilotStateProvider
        {
            public string DescribeState()
            {
                return "You stand in a forest. Oak logs 6 studs north.";
            }
        }

        private sealed class Actuator : IAutopilotActuator, IAutopilotAvailability
        {
            public readonly List<string> Pursued = new();

            public int Releases;

            public string Unavailable = "";

            public void Pursue(AutopilotDecision decision)
            {
                Pursued.Add(decision.GoalId);
            }

            public void Release()
            {
                Releases++;
            }

            public bool CanDrive(out string reason)
            {
                reason = Unavailable;
                return Unavailable.Length == 0;
            }
        }

        private sealed class Messages : IChatOverlayMessages
        {
            public readonly List<string> Lines = new();

            public void PostSystemMessage(string text)
            {
                Lines.Add(text);
            }

            public void PostMessage(string speaker, string text, ChatFeedLineKind kind = ChatFeedLineKind.System)
            {
                Lines.Add(text);
            }
        }

        private sealed class FixedFactory : IDecisionClientFactory
        {
            public readonly ScriptedDecisionClient Client = new();

            public IDecisionClient Create(LlmEndpointDescriptor endpoint)
            {
                return Client;
            }
        }

        private static readonly DecisionActionSet Goals = new(new[]
        {
            new DecisionActionDefinition("gather", "Collect wood and stone."),
            new DecisionActionDefinition("build", "Place blocks for a shelter."),
            new DecisionActionDefinition("follow", "Follow the nearest friend.")
        });

        private sealed class Rig
        {
            public Rig(bool routed, Func<DecisionActionSet> goals = null)
            {
                Clock = new ManualDecisionClock();
                List<LlmEndpointDescriptor> endpoints = new()
                {
                    DecisionFixtures.Endpoint("local-decisions", LlmDecisionProtocol.SystemOne)
                };
                Router = new DecisionRouter(routed ? DecisionRouteTable.AllTo("local-decisions") : new DecisionRouteTable(),
                    new StaticDecisionEndpointSource(endpoints));
                Factory = new FixedFactory();
                Factory.Client.Answer = r => DecisionFixtures.Answer(r, "build", 0.86d);
                Provider = new DecisionClientProvider(Router, Factory, Clock);
                Actuator = new Actuator();
                Trace = new InMemoryDecisionTraceSink();
                Controller = new AutopilotController(new PlayerAutopilotOptions { Goals = Goals },
                    Provider.ForConsumer(DecisionConsumers.Autopilot), new State(), Actuator, Clock, Trace,
                    new KeepGoalAutopilotFallbackPolicy("gather"), Router, goals);
            }

            public ManualDecisionClock Clock { get; }

            public DecisionRouter Router { get; }

            public FixedFactory Factory { get; }

            public DecisionClientProvider Provider { get; }

            public Actuator Actuator { get; }

            public InMemoryDecisionTraceSink Trace { get; }

            public AutopilotController Controller { get; }
        }

        private static string Run(IChatCommandRegistry registry, string line)
        {
            ChatDispatchResult result = new ChatCommandDispatcher(registry).Dispatch(line);
            return result.Kind + ": " + result.Reply;
        }

        [Test]
        public async Task WithoutADecisionEndpoint_TheFallbackDecides_AndTheStatusSaysWhy()
        {
            Rig rig = new(false);

            Assert.IsTrue(rig.Controller.Engage().Engaged);
            await rig.Controller.Tick();

            CollectionAssert.AreEqual(new[] { "gather" }, rig.Actuator.Pursued);
            Assert.AreEqual(0, rig.Factory.Client.Calls, "nothing was sent anywhere");
            AutopilotStatus status = rig.Controller.GetStatus();
            Assert.AreEqual(DecisionSource.Fallback, status.LastDecision.Source);
            Assert.AreEqual(DecisionErrorReasons.NoRoute, status.LastDecision.ErrorReason);
            string text = AutopilotChatCommand.DescribeStatus(status);
            StringAssert.Contains("goal 'gather', chosen by the fallback: the decision call failed (RoutingError/no_route)", text);
            StringAssert.Contains("the built-in fallback, because no decision model is available (no_route: " +
                                  "No decision route matches consumer 'autopilot')", text);
            Assert.AreEqual(1, rig.Trace.FallbackCount);
        }

        [Test]
        public async Task WithARoutedModel_TheModelDecides_AndTheStatusNamesIt()
        {
            Rig rig = new(true);

            rig.Controller.Engage();
            await rig.Controller.Tick();

            CollectionAssert.AreEqual(new[] { "build" }, rig.Actuator.Pursued);
            AutopilotStatus status = rig.Controller.GetStatus();
            Assert.AreEqual(DecisionSource.Model, status.LastDecision.Source);
            string text = AutopilotChatCommand.DescribeStatus(status);
            StringAssert.Contains("goal 'build', chosen by the model, confidence 0.86", text);
            StringAssert.Contains("the decision model d1-2B on endpoint 'local-decisions'", text);
        }

        [Test]
        public async Task ALowConfidenceAnswer_IsHandedToTheFallback_AndTheStatusSaysHowSureTheModelWas()
        {
            Rig rig = new(true);
            rig.Factory.Client.Answer = r => DecisionFixtures.Answer(r, "build", 0.41d);

            rig.Controller.Engage();
            await rig.Controller.Tick();

            CollectionAssert.AreEqual(new[] { "gather" }, rig.Actuator.Pursued);
            StringAssert.Contains("the model was only 0.41 sure (it needs 0.70)",
                AutopilotChatCommand.DescribeStatus(rig.Controller.GetStatus()));
        }

        [Test]
        public void ACharacterThatCannotBeDriven_RefusesTheEngagement_WithTheReason()
        {
            Rig rig = new(false);
            rig.Actuator.Unavailable = "Your character has not spawned yet.";

            AutopilotEngageResult result = rig.Controller.Engage();

            Assert.IsFalse(result.Engaged);
            Assert.AreEqual("Your character has not spawned yet.", result.Message);
            Assert.IsFalse(rig.Controller.Engaged);
            StringAssert.Contains("Your character has not spawned yet.",
                AutopilotChatCommand.DescribeStatus(rig.Controller.GetStatus()));
        }

        [Test]
        public void EngagingTwice_KeepsTheEngagement()
        {
            Rig rig = new(false);
            int changes = 0;
            rig.Controller.EngagedChanged += (_, _) => changes++;

            rig.Controller.Engage();
            AutopilotEngageResult again = rig.Controller.Engage();

            Assert.IsTrue(again.Engaged);
            Assert.IsTrue(again.WasEngaged);
            Assert.AreEqual(1, changes);
        }

        [Test]
        public async Task PlayerInput_HandsControlBackAtOnce_AndTheStatusRemembersWhy()
        {
            Rig rig = new(false);
            List<string> reasons = new();
            rig.Controller.EngagedChanged += (engaged, reason) => reasons.Add(engaged + ":" + reason);
            rig.Controller.Engage();
            await rig.Controller.Tick();

            rig.Controller.NotifyPlayerInput();

            Assert.IsFalse(rig.Controller.Engaged);
            Assert.AreEqual(1, rig.Actuator.Releases);
            CollectionAssert.AreEqual(new[] { "True:engaged", "False:player_input" }, reasons);
            StringAssert.StartsWith("Autopilot off (you took control).",
                AutopilotChatCommand.DescribeStatus(rig.Controller.GetStatus()));
        }

        [Test]
        public async Task PlayerInput_WhileDisengaged_DoesNothing()
        {
            Rig rig = new(false);

            rig.Controller.NotifyPlayerInput();
            await rig.Controller.Tick();

            Assert.AreEqual(0, rig.Actuator.Releases);
            CollectionAssert.IsEmpty(rig.Actuator.Pursued);
            Assert.IsFalse(rig.Controller.Disengage());
        }

        [Test]
        public async Task TheGoalList_IsReadAtEveryEngagement()
        {
            DecisionActionSet current = DecisionActionSet.Of("gather", "build");
            Rig rig = new(false, () => current);
            rig.Controller.Engage();
            await rig.Controller.Tick();
            rig.Controller.Disengage();
            current = DecisionActionSet.Of("flee", "follow");

            rig.Controller.Engage();
            await rig.Controller.Tick();

            CollectionAssert.AreEqual(new[] { "gather", "flee" }, rig.Actuator.Pursued);
            CollectionAssert.AreEqual(new[] { "flee", "follow" }, rig.Controller.GetStatus().GoalIds);
        }

        [Test]
        public void AnEmptyGoalList_IsRefused()
        {
            Rig rig = new(false, () => DecisionActionSet.Of());

            AutopilotEngageResult result = rig.Controller.Engage();

            Assert.IsFalse(result.Engaged);
            StringAssert.Contains("The goal list is invalid", result.Message);
        }

        [Test]
        public void ABrokenGoalSource_FallsBackToTheConfiguredGoals()
        {
            Rig rig = new(false, () => throw new InvalidOperationException("no world"));

            Assert.IsTrue(rig.Controller.Engage().Engaged);
            CollectionAssert.AreEqual(new[] { "gather", "build", "follow" }, rig.Controller.GetStatus().GoalIds);
        }

        [Test]
        public void Dispose_HandsControlBack_AndRefusesLaterEngagements()
        {
            Rig rig = new(false);
            rig.Controller.Engage();

            rig.Controller.Dispose();

            Assert.AreEqual(1, rig.Actuator.Releases);
            Assert.IsFalse(rig.Controller.Engage().Engaged);
        }

        [Test]
        public void Command_On_EngagesAndSaysWhatDecides()
        {
            Rig rig = new(false);
            ChatCommandRegistry registry = new();
            using IDisposable registration = AutopilotChatCommand.Register(registry, rig.Controller);

            string reply = Run(registry, "/autopilot on");

            Assert.IsTrue(rig.Controller.Engaged);
            StringAssert.StartsWith("Handled: Autopilot on: decisions come from the built-in fallback", reply);
            StringAssert.Contains("Goals: gather, build, follow.", reply);
            StringAssert.Contains("Press any movement key, or type /autopilot off, to take control back.", reply);
        }

        [Test]
        public void Command_On_WithARoutedModel_NamesTheModel()
        {
            Rig rig = new(true);
            ChatCommandRegistry registry = new();
            using IDisposable registration = AutopilotChatCommand.Register(registry, rig.Controller);

            StringAssert.Contains("decisions come from the decision model d1-2B on endpoint 'local-decisions'",
                Run(registry, "/autopilot on"));
        }

        [Test]
        public void Command_On_WhenTheCharacterCannotBeDriven_Fails()
        {
            Rig rig = new(false);
            rig.Actuator.Unavailable = "Your character has not spawned yet.";
            ChatCommandRegistry registry = new();
            using IDisposable registration = AutopilotChatCommand.Register(registry, rig.Controller);

            Assert.AreEqual("Failed: Autopilot not started: Your character has not spawned yet.",
                Run(registry, "/autopilot on"));
        }

        [Test]
        public void Command_Off_AndStatus_AndBadArguments()
        {
            Rig rig = new(false);
            ChatCommandRegistry registry = new();
            using IDisposable registration = AutopilotChatCommand.Register(registry, rig.Controller);

            Assert.AreEqual("Handled: Autopilot is already off.", Run(registry, "/autopilot off"));
            StringAssert.StartsWith("Handled: Autopilot off. Decisions would come from", Run(registry, "/autopilot"));
            StringAssert.StartsWith("Handled: Autopilot off.", Run(registry, "/AUTOPILOT status"));
            Run(registry, "/autopilot on");
            StringAssert.StartsWith("Handled: Autopilot on, no goal chosen yet.", Run(registry, "/autopilot status"));
            Assert.AreEqual("Handled: Autopilot off: you have control.", Run(registry, "/autopilot off"));
            Assert.AreEqual("Failed: Usage: /autopilot [on|off|status]", Run(registry, "/autopilot fly"));
            Assert.AreEqual("Failed: Usage: /autopilot [on|off|status]", Run(registry, "/autopilot on now"));
        }

        [Test]
        public void Registration_PostsALine_WhenThePlayerTakesControl_ButNotForTheirOwnOff()
        {
            Rig rig = new(false);
            ChatCommandRegistry registry = new();
            Messages messages = new();
            IDisposable registration = AutopilotChatCommand.Register(registry, rig.Controller, messages);

            Run(registry, "/autopilot on");
            Run(registry, "/autopilot off");
            Run(registry, "/autopilot on");
            rig.Controller.NotifyPlayerInput();
            registration.Dispose();
            rig.Controller.Engage();
            rig.Controller.NotifyPlayerInput();

            CollectionAssert.AreEqual(new[] { "Autopilot off: you took control." }, messages.Lines);
            Assert.IsFalse(registry.TryResolve("autopilot", out IChatCommand _), "disposing removes the command");
        }
    }
}
