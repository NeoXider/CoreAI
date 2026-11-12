using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Ai.Npc;
using CoreAI.Decisions;
using CoreAI.Mods.Rbx.Agents;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Scheduling;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Agents
{
    /// <summary>
    /// A villager's decision brain over a headless Rbx world: the player walking up is an event, the rule table greets
    /// back (with a line, no model) and turns to the player when no decision model answers, the player speaking to
    /// the villager reaches its next decision, and a confident model's choice moves the body.
    /// </summary>
    [TestFixture]
    public sealed class RbxNpcBrainEditModeTests
    {
        private sealed class Orchestrator : IAiOrchestrationService
        {
            public readonly List<AiTaskRequest> Requests = new();

            public Task<string> RunTaskAsync(AiTaskRequest task, CancellationToken cancellationToken = default)
            {
                Requests.Add(task);
                return Task.FromResult("Aye.");
            }

            public async IAsyncEnumerable<LlmStreamChunk> RunStreamingAsync(AiTaskRequest task,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                Requests.Add(task);
                await Task.CompletedTask;
                yield return new LlmStreamChunk { Text = "Aye." };
                yield return new LlmStreamChunk { IsDone = true };
            }

            public void CancelTasks(string cancellationScope)
            {
            }
        }

        private sealed class Clock : INpcClock
        {
            public double NowSeconds { get; set; }
        }

        private static readonly RbxVector3 Home = new(0f, 3f, 0f);

        private static (NpcDirector Director, Orchestrator Orchestrator, RbxInstance Bob, RecordingMotor Motor)
            Village(AgentsWorld world)
        {
            RbxInstance bob = world.AddVillager("Bob", Home, out RbxHumanoid _, out RecordingMotor motor);
            Orchestrator orchestrator = new();
            NpcDirector director = new(orchestrator, null, new NpcDirectorOptions(), new Clock());
            director.Add(new NpcAgentDefinition
            {
                Persona = new NpcPersona { Id = "bob", DisplayName = "Bob" },
                Budget = new NpcBudget { CooldownSeconds = 0d }
            });
            return (director, orchestrator, bob, motor);
        }

        [Test]
        public async Task ThePlayerWalkingUp_IsGreetedByTheRuleTable_WithoutAModel_AndTheDecisionSaysItWasTheFallback()
        {
            AgentsWorld world = new();
            world.AddPlayer("steve", new RbxVector3(0f, 3f, 40f));
            (NpcDirector director, Orchestrator orchestrator, RbxInstance bob, RecordingMotor motor) = Village(world);
            using NpcDirector owned = director;
            StepClock clock = new();
            InMemoryDecisionTraceSink trace = new();
            using RbxNpcBrain brain = new("bob", RbxCharacterSubject.Of(world.World, bob), director,
                new AnsweringDecisionClient(), clock, trace);
            List<NpcSaid> said = new();
            director.NpcSaid += said.Add;
            await brain.Tick();
            Assert.AreEqual("wander", brain.Policy.Current.ActionId, "nothing happened: the default action");

            world.MovePlayer(new RbxVector3(0f, 3f, 6f));
            clock.NowSeconds = 3d;
            await brain.Tick();

            Assert.AreEqual("greet", brain.Policy.Current.ActionId);
            Assert.AreEqual(DecisionSource.Fallback, brain.Policy.Current.Source);
            CollectionAssert.Contains(brain.Policy.Current.TriggerEventKinds, NpcDecisionEventKinds.PlayerApproached);
            Assert.AreEqual("Hello, " + world.Player.DisplayName + "!", said[said.Count - 1].Text);
            CollectionAssert.IsEmpty(orchestrator.Requests, "a greeting needs no model");
            Assert.AreEqual("greet", bob.GetAttribute(RbxAgentAttributes.NpcAction));
            RbxVector3 look = world.Poses[bob.FindFirstChild("HumanoidRootPart").Id].LookVector;
            Assert.AreEqual(1f, look.Z, 1e-3f, "Bob turned to the player while greeting");
            Assert.AreEqual(2, trace.FallbackCount);
        }

        [Test]
        public async Task ThePlayerStayingClose_IsNotAnotherApproach()
        {
            AgentsWorld world = new();
            world.AddPlayer("steve", new RbxVector3(0f, 3f, 6f));
            (NpcDirector director, Orchestrator _, RbxInstance bob, RecordingMotor _) = Village(world);
            using NpcDirector owned = director;
            StepClock clock = new();
            AnsweringDecisionClient client = new();
            using RbxNpcBrain brain = new("bob", RbxCharacterSubject.Of(world.World, bob), director, client, clock);

            await brain.Tick();
            clock.NowSeconds = 3d;
            await brain.Tick();
            clock.NowSeconds = 10d;
            await brain.Tick();

            Assert.AreEqual(2, client.Requests.Count, "the approach and one idle tick");
            StringAssert.DoesNotContain("player_approached", client.Requests[1].State);
        }

        [Test]
        public async Task AModelThatKeepsChoosingGreet_GreetsAPlayerWhoStays_Once_AndIsToldSo()
        {
            AgentsWorld world = new();
            world.AddPlayer("steve", new RbxVector3(0f, 3f, 6f));
            (NpcDirector director, Orchestrator _, RbxInstance bob, RecordingMotor _) = Village(world);
            using NpcDirector owned = director;
            StepClock clock = new();
            AnsweringDecisionClient client = new() { Answer = r => AnsweringDecisionClient.Choose(r, "greet") };
            using RbxNpcBrain brain = new("bob", RbxCharacterSubject.Of(world.World, bob), director, client, clock);
            List<NpcSaid> said = new();
            director.NpcSaid += said.Add;

            // WHY 50 idle ticks of 6 s: five minutes of a player standing next to Bob, longer than the quiet time.
            for (int tick = 0; tick < 50; tick++)
            {
                clock.NowSeconds = tick * 6d;
                await brain.Tick();
            }

            Assert.AreEqual(50, client.Requests.Count, "the model chose greet on every idle tick");
            Assert.AreEqual(1, said.Count, "but a player who never left is greeted once");
            Assert.AreEqual(49, brain.GreetingsHeld);
            Assert.AreEqual("greet", brain.Policy.Current.ActionId, "the decision stands: Bob only turns to the player");
            StringAssert.DoesNotContain("You have greeted the player already", client.Requests[0].State);
            StringAssert.Contains("You have greeted the player already", client.Requests[1].State,
                "the model is told, so it can choose something else");
        }

        [Test]
        public async Task APlayerWhoLeftAndCameBack_IsGreetedAgain_OnlyAfterTheQuietTime()
        {
            AgentsWorld world = new();
            world.AddPlayer("steve", new RbxVector3(0f, 3f, 40f));
            (NpcDirector director, Orchestrator _, RbxInstance bob, RecordingMotor _) = Village(world);
            using NpcDirector owned = director;
            StepClock clock = new();
            AnsweringDecisionClient client = new() { Answer = r => AnsweringDecisionClient.Choose(r, "greet") };
            using RbxNpcBrain brain = new("bob", RbxCharacterSubject.Of(world.World, bob), director, client, clock);
            List<NpcSaid> said = new();
            director.NpcSaid += said.Add;

            async Task At(double seconds, float playerZ)
            {
                world.MovePlayer(new RbxVector3(0f, 3f, playerZ));
                clock.NowSeconds = seconds;
                await brain.Tick();
            }

            await At(0d, 40f);
            Assert.AreEqual(0, said.Count, "nobody is close to greet");
            await At(3d, 6f);
            Assert.AreEqual(1, said.Count, "the player who walks up is greeted");
            await At(10d, 40f);
            await At(20d, 6f);
            Assert.AreEqual(1, said.Count, "back after 17 s: within the quiet time, no second greeting");
            await At(40d, 6f);
            await At(130d, 6f);
            Assert.AreEqual(1, said.Count, "the quiet time passing does not greet a player who has been there all along");
            await At(140d, 40f);
            await At(150d, 6f);

            Assert.AreEqual(8, client.Requests.Count, "the model chose greet at every step");
            Assert.AreEqual(2, said.Count, "gone and back after the quiet time: greeted again");
            Assert.AreEqual("Good day, " + world.Player.DisplayName + ".", said[1].Text);
            Assert.AreEqual(6, brain.GreetingsHeld);
        }

        [Test]
        public async Task SpeakingToTheVillager_ReachesItsNextDecision()
        {
            AgentsWorld world = new();
            world.AddPlayer("steve", new RbxVector3(0f, 3f, 40f));
            (NpcDirector director, Orchestrator orchestrator, RbxInstance bob, RecordingMotor _) = Village(world);
            using NpcDirector owned = director;
            StepClock clock = new();
            AnsweringDecisionClient client = new();
            using RbxNpcBrain brain = new("bob", RbxCharacterSubject.Of(world.World, bob), director, client, clock);
            await brain.Tick();

            director.Hear(NpcHeardLine.Chat("player", "Steve", "Bob, where is the mill?"));
            clock.NowSeconds = 2.5d;
            await brain.Tick();

            Assert.AreEqual(1, orchestrator.Requests.Count, "Bob answered with his voice");
            StringAssert.Contains("spoken_to: Steve spoke to you by name", client.Requests[1].State);
            Assert.AreEqual("face_player", brain.Policy.Current.ActionId, "the rule table turns him to the speaker");
        }

        [Test]
        public async Task AConfidentModel_MovesTheBody()
        {
            AgentsWorld world = new();
            world.AddPlayer("steve", new RbxVector3(0f, 3f, 30f));
            (NpcDirector director, Orchestrator _, RbxInstance bob, RecordingMotor motor) = Village(world);
            using NpcDirector owned = director;
            AnsweringDecisionClient client = new() { Answer = r => AnsweringDecisionClient.Choose(r, "follow_player") };
            using RbxNpcBrain brain = new("bob", RbxCharacterSubject.Of(world.World, bob), director, client,
                new StepClock());

            await brain.Tick();

            Assert.AreEqual(DecisionSource.Model, brain.Policy.Current.Source);
            Assert.AreEqual(25f, motor.LastTarget.Value.Z, 1e-3f);
            StringAssert.Contains("You are Bob.", client.Requests[0].State);
        }

        [Test]
        public async Task ADisposedBrain_DecidesNothing()
        {
            AgentsWorld world = new();
            world.AddPlayer("steve", new RbxVector3(0f, 3f, 6f));
            (NpcDirector director, Orchestrator _, RbxInstance bob, RecordingMotor motor) = Village(world);
            using NpcDirector owned = director;
            AnsweringDecisionClient client = new();
            RbxNpcBrain brain = new("bob", RbxCharacterSubject.Of(world.World, bob), director, client, new StepClock());

            brain.Dispose();
            await brain.Tick();
            director.Hear(NpcHeardLine.Chat("player", "Steve", "Bob?"));

            CollectionAssert.IsEmpty(client.Requests);
            CollectionAssert.IsEmpty(motor.MoveTargets);
        }

        [Test]
        public void Host_GivesABrainToEveryVillagerAModMarks_AndTakesItBackWhenUnmarked()
        {
            AgentsWorld world = new();
            world.AddPlayer("steve", new RbxVector3(0f, 3f, 40f));
            (NpcDirector director, Orchestrator _, RbxInstance bob, RecordingMotor motor) = Village(world);
            using NpcDirector owned = director;
            StepClock clock = new();
            AnsweringDecisionClient client = new();
            using RbxNpcBrainHost host = new(world.World, director, client, clock);
            ModScheduler scheduler = AgentsWorld.Scheduler();
            host.AttachScheduler(scheduler);

            scheduler.Advance(1d / 60d);
            Assert.AreEqual(0, host.Count, "an unmarked villager has no brain");

            bob.SetAttribute(RbxNpcBrainAttributes.Agent, "bob");
            bob.SetAttribute(RbxNpcBrainAttributes.Actions, "idle, wander, greet, face_player");
            clock.NowSeconds = 1.5d;
            scheduler.Advance(1d / 60d);

            Assert.AreEqual(1, host.Count);
            Assert.IsTrue(host.TryGetBrain("bob", out RbxNpcBrain brain));
            Assert.AreEqual("wander", brain.Policy.Current.ActionId);
            Assert.AreEqual(1, motor.MoveTargets.Count, "the fallback made him stroll");

            bob.SetAttribute(RbxNpcBrainAttributes.Actions, null);
            clock.NowSeconds = 3d;
            scheduler.Advance(1d / 60d);
            Assert.AreEqual(0, host.Count);
        }

        [Test]
        public void Host_ABrainGivenTheBlockWorldVillagersList_GreetsAPlayerWhoStays_Once()
        {
            AgentsWorld world = new();
            world.AddPlayer("steve", new RbxVector3(0f, 3f, 6f));
            (NpcDirector director, Orchestrator _, RbxInstance bob, RecordingMotor _) = Village(world);
            using NpcDirector owned = director;
            StepClock clock = new();
            AnsweringDecisionClient client = new() { Answer = r => AnsweringDecisionClient.Choose(r, "greet") };
            using RbxNpcBrainHost host = new(world.World, director, client, clock);
            List<NpcSaid> said = new();
            director.NpcSaid += said.Add;
            bob.SetAttribute(RbxNpcBrainAttributes.Agent, "bob");
            bob.SetAttribute(RbxNpcBrainAttributes.Actions, "idle, wander, greet, face_player, follow_player, go_home");

            for (int second = 0; second <= 300; second++)
            {
                clock.NowSeconds = second;
                host.Tick();
            }

            Assert.AreEqual(51, client.Requests.Count, "five minutes of idle ticks, each choosing greet");
            Assert.AreEqual(1, said.Count, "the villager greeted the player who stood by it once");
        }

        [Test]
        public void Host_IgnoresAMarkForAnAgentTheDirectorDoesNotKnow_AndAnInvalidList()
        {
            AgentsWorld world = new();
            (NpcDirector director, Orchestrator _, RbxInstance bob, RecordingMotor _) = Village(world);
            using NpcDirector owned = director;
            AnsweringDecisionClient client = new();
            using RbxNpcBrainHost host = new(world.World, director, client, new StepClock());

            bob.SetAttribute(RbxNpcBrainAttributes.Agent, "ghost");
            bob.SetAttribute(RbxNpcBrainAttributes.Actions, "idle");
            host.Tick();
            Assert.AreEqual(0, host.Count);

            bob.SetAttribute(RbxNpcBrainAttributes.Agent, "bob");
            bob.SetAttribute(RbxNpcBrainAttributes.Actions, "say hello!, wave");
            host.Tick();

            Assert.AreEqual(0, host.Count, "an action id with spaces is not a valid decision option");
            CollectionAssert.IsEmpty(client.Requests);
        }

        [Test]
        public void Host_HonoursItsBrainCap_AndADestroyedVillagerLosesItsBrain()
        {
            AgentsWorld world = new();
            (NpcDirector director, Orchestrator _, RbxInstance bob, RecordingMotor _) = Village(world);
            using NpcDirector owned = director;
            RbxInstance alice = world.AddVillager("Alice", new RbxVector3(10f, 3f, 0f), out RbxHumanoid _,
                out RecordingMotor _);
            director.Add(new NpcAgentDefinition { Persona = new NpcPersona { Id = "alice", DisplayName = "Alice" } });
            StepClock clock = new();
            using RbxNpcBrainHost host = new(world.World, director, new AnsweringDecisionClient(), clock, null,
                new RbxNpcBrainHostOptions { MaxBrains = 1 });
            foreach ((RbxInstance model, string id) in new[] { (bob, "bob"), (alice, "alice") })
            {
                model.SetAttribute(RbxNpcBrainAttributes.Agent, id);
                model.SetAttribute(RbxNpcBrainAttributes.Actions, "idle, wander");
            }

            host.Tick();
            Assert.AreEqual(1, host.Count);

            bob.Destroy();
            clock.NowSeconds = 2d;
            host.Tick();

            Assert.AreEqual(1, host.Count);
            Assert.IsTrue(host.TryGetBrain("alice", out RbxNpcBrain _), "the freed slot went to Alice");
        }

        [Test]
        public void ABrainNeedsAnNpcId()
        {
            AgentsWorld world = new();
            (NpcDirector director, Orchestrator _, RbxInstance bob, RecordingMotor _) = Village(world);
            using NpcDirector owned = director;

            Assert.Throws<ArgumentException>(() => new RbxNpcBrain(" ", RbxCharacterSubject.Of(world.World, bob),
                director, new AnsweringDecisionClient(), new StepClock()));
        }
    }
}
