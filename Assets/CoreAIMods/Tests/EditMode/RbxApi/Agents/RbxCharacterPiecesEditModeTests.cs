using CoreAI.Ai.Npc;
using CoreAI.Decisions;
using CoreAI.Mods.Rbx.Agents;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Agents
{
    /// <summary>
    /// The engine-free character pieces over a headless Rbx world: where the local player stands for the NPC block,
    /// the Humanoid driver (walk, face, jump, stop), the action targets, the state text the policies read and the
    /// input that counts as the player taking control back.
    /// </summary>
    [TestFixture]
    public sealed class RbxCharacterPiecesEditModeTests
    {
        private static readonly RbxVector3 Origin = new(0f, 3f, 0f);

        [Test]
        public void Listener_IsWhereThePlayersCharacterStands_InStuds()
        {
            AgentsWorld world = new();
            world.AddPlayer("steve", new RbxVector3(10f, 3f, -4f));
            RbxCharacterListenerPosition listener = new(world.World);

            NpcPosition? at = listener.Current;

            Assert.IsTrue(at.HasValue);
            Assert.AreEqual(10d, at.Value.X, 1e-4);
            Assert.AreEqual(-4d, at.Value.Z, 1e-4);
            world.MovePlayer(new RbxVector3(20f, 3f, 0f));
            Assert.AreEqual(20d, listener.Current.Value.X, 1e-4, "read live, not cached");
        }

        [Test]
        public void Listener_IsUnknown_WithoutAPlayer_WithoutACharacter_OrWithoutAWorld()
        {
            AgentsWorld world = new();
            RbxCharacterListenerPosition listener = new(world.World);
            Assert.IsFalse(listener.Current.HasValue, "no player");

            world.Players.EnsureActor(world.Registry, "steve");
            Assert.IsFalse(listener.Current.HasValue, "a player without a character");

            world.AddPlayer("alex", Origin);
            RbxCharacterListenerPosition other = new(new DelegateRbxCharacterWorld(() => null));
            Assert.IsFalse(other.Current.HasValue, "no world loaded");
        }

        [Test]
        public void Listener_FollowsTheActorIdWhenGiven()
        {
            AgentsWorld world = new();
            world.AddPlayer("steve", new RbxVector3(1f, 3f, 0f));
            world.AddPlayer("alex", new RbxVector3(50f, 3f, 0f));

            Assert.AreEqual(50d, new RbxCharacterListenerPosition(world.World, "alex").Current.Value.X, 1e-4);
            Assert.AreEqual(1d, new RbxCharacterListenerPosition(world.World, "steve").Current.Value.X, 1e-4);
            Assert.IsFalse(new RbxCharacterListenerPosition(world.World, "nobody").Current.HasValue);
        }

        [Test]
        public void Listener_WithoutAPartSink_ReadsTheMotor_ButNeverTheNullMotorsOrigin()
        {
            AgentsWorld world = new(partSink: false);
            world.AddPlayer("steve", new RbxVector3(7f, 3f, 0f));
            RbxCharacterListenerPosition listener = new(world.World);

            Assert.AreEqual(7d, listener.Current.Value.X, 1e-4);
            world.PlayerHumanoid.DetachHost();
            Assert.IsFalse(listener.Current.HasValue, "the null motor knows no position");
        }

        [Test]
        public void Driver_WalksWithMoveTo_TurnsToTheTarget_AndNeverUsesThePlayersMove()
        {
            AgentsWorld world = new();
            world.AddPlayer("steve", Origin);
            RbxHumanoidDriver driver = new(RbxCharacterSubject.LocalPlayer(world.World));
            RbxVector3 target = new(10f, 3f, 0f);

            Assert.AreEqual(RbxCharacterCommandOutcome.Done, driver.WalkTo(target));

            Assert.AreEqual(target, world.PlayerMotor.LastTarget);
            CollectionAssert.IsEmpty(world.PlayerMotor.Moves, "Humanoid:Move is the player's own path");
            RbxVector3 look = world.Poses[world.PlayerHumanoid.RootPart.Id].LookVector;
            Assert.AreEqual(1f, look.X, 1e-3f, "the character turned toward +X");
            Assert.AreEqual(target, driver.WalkTarget);
        }

        [Test]
        public void Driver_Stop_EndsItsOwnWalk_ButNotThePlayersSteering()
        {
            AgentsWorld world = new();
            world.AddPlayer("steve", Origin);
            RbxHumanoidDriver driver = new(RbxCharacterSubject.LocalPlayer(world.World));
            driver.WalkTo(new RbxVector3(10f, 3f, 0f));

            Assert.AreEqual(RbxCharacterCommandOutcome.Done, driver.Stop());
            Assert.AreEqual(Origin, world.PlayerMotor.LastTarget, "MoveTo where it stands ends the walk");
            Assert.IsFalse(driver.WalkTarget.HasValue);

            driver.WalkTo(new RbxVector3(10f, 3f, 0f));
            world.PlayerHumanoid.Move(new RbxVector3(0f, 0f, -1f));
            int targets = world.PlayerMotor.MoveTargets.Count;
            Assert.AreEqual(RbxCharacterCommandOutcome.AlreadyThere, driver.Stop());
            Assert.AreEqual(targets, world.PlayerMotor.MoveTargets.Count, "the player is steering: hands off");
        }

        [Test]
        public void Driver_Stop_WithoutItsOwnWalk_TouchesNothing()
        {
            AgentsWorld world = new();
            world.AddPlayer("steve", Origin);
            RbxHumanoidDriver driver = new(RbxCharacterSubject.LocalPlayer(world.World));

            driver.Stop();

            CollectionAssert.IsEmpty(world.PlayerMotor.MoveTargets);
        }

        [Test]
        public void Driver_Jumps_AndRefusesADeadOrMissingCharacter()
        {
            AgentsWorld world = new();
            RbxHumanoidDriver driver = new(RbxCharacterSubject.LocalPlayer(world.World));
            Assert.AreEqual(RbxCharacterCommandOutcome.NoCharacter, driver.Jump());

            world.AddPlayer("steve", Origin);
            Assert.AreEqual(RbxCharacterCommandOutcome.Done, driver.Jump());
            Assert.AreEqual(1, world.PlayerMotor.Jumps);

            world.PlayerHumanoid.Health = 0d;
            Assert.AreEqual(RbxCharacterCommandOutcome.Dead, driver.Jump());
            Assert.AreEqual(RbxCharacterCommandOutcome.Dead, driver.WalkTo(new RbxVector3(5f, 3f, 0f)));
            Assert.AreEqual(1, world.PlayerMotor.Jumps);
        }

        [Test]
        public void Driver_Face_NeedsAPartSink()
        {
            AgentsWorld world = new(partSink: false);
            world.AddPlayer("steve", Origin);
            RbxHumanoidDriver driver = new(RbxCharacterSubject.LocalPlayer(world.World));

            Assert.AreEqual(RbxCharacterCommandOutcome.Unsupported, driver.Face(new RbxVector3(5f, 3f, 5f)));
        }

        [Test]
        public void Runner_Follow_WalksToTheNearestCharacter_StoppingShortOfIt()
        {
            AgentsWorld world = new();
            world.AddPlayer("steve", Origin);
            world.AddVillager("Bob", new RbxVector3(20f, 3f, 0f), out RbxHumanoid _, out RecordingMotor _);
            world.AddVillager("Alice", new RbxVector3(-40f, 3f, 0f), out RbxHumanoid _, out RecordingMotor _);
            RbxCharacterCommandRunner runner = new(RbxCharacterSubject.LocalPlayer(world.World));

            Assert.AreEqual(RbxCharacterCommandOutcome.Done, runner.Run("follow"));

            Assert.AreEqual(16f, world.PlayerMotor.LastTarget.Value.X, 1e-3f);
        }

        [Test]
        public void Runner_Follow_WithNobodyInRange_HasNoTarget()
        {
            AgentsWorld world = new();
            world.AddPlayer("steve", Origin);
            world.AddVillager("Alice", new RbxVector3(-400f, 3f, 0f), out RbxHumanoid _, out RecordingMotor _);
            RbxCharacterCommandRunner runner = new(RbxCharacterSubject.LocalPlayer(world.World));

            Assert.AreEqual(RbxCharacterCommandOutcome.NoTarget, runner.Run("follow"));
            CollectionAssert.IsEmpty(world.PlayerMotor.MoveTargets);
        }

        [Test]
        public void Runner_Flee_WalksAwayFromTheThreat()
        {
            AgentsWorld world = new();
            world.AddPlayer("steve", Origin);
            world.AddVillager("Zombie", new RbxVector3(0f, 3f, -10f), out RbxHumanoid _, out RecordingMotor _);
            RbxCharacterCommandRunner runner = new(RbxCharacterSubject.LocalPlayer(world.World));

            runner.Run("flee");

            Assert.AreEqual(16f, world.PlayerMotor.LastTarget.Value.Z, 1e-3f);
        }

        [Test]
        public void Runner_Wander_IsDeterministic_AndStaysNearHome()
        {
            AgentsWorld first = new();
            first.AddPlayer("steve", Origin);
            AgentsWorld second = new();
            second.AddPlayer("steve", Origin);
            RbxCharacterCommandRunner a = new(RbxCharacterSubject.LocalPlayer(first.World), null, 48d, 7d);
            RbxCharacterCommandRunner b = new(RbxCharacterSubject.LocalPlayer(second.World), null, 48d, 7d);

            a.Run("wander");
            b.Run("wander");

            RbxVector3 point = first.PlayerMotor.LastTarget.Value;
            Assert.AreEqual(point, second.PlayerMotor.LastTarget.Value, "same seed, same point");
            float distance = RbxHumanoidDriverPlanar(point - Origin);
            Assert.That(distance, Is.InRange(6f, 12.001f));
        }

        [Test]
        public void Runner_UnknownGoal_IsOnlyPublished_ForTheModToCarryOut()
        {
            AgentsWorld world = new();
            world.AddPlayer("steve", Origin);
            RbxCharacterCommandRunner runner = new(RbxCharacterSubject.LocalPlayer(world.World));

            Assert.AreEqual(RbxCharacterCommandOutcome.Published, runner.Run("gather"));
            CollectionAssert.IsEmpty(world.PlayerMotor.MoveTargets);
            Assert.AreEqual("gather", runner.LastActionId);
        }

        [Test]
        public void Runner_NamedTarget_IsAVector3AttributeAModPublishes()
        {
            AgentsWorld world = new();
            world.AddPlayer("steve", Origin);
            RbxCharacterActionMap map = RbxCharacterActionMap.AutopilotDefaults()
                .With(new RbxCharacterAction("gather", RbxCharacterCommandKind.WalkTo, "NearestWood", 2d));
            RbxCharacterCommandRunner runner = new(RbxCharacterSubject.LocalPlayer(world.World), map);

            Assert.AreEqual(RbxCharacterCommandOutcome.NoTarget, runner.Run("gather"));
            world.Player.SetAttribute("NearestWood", new RbxVector3(0f, 3f, 12f));
            Assert.AreEqual(RbxCharacterCommandOutcome.Done, runner.Run("gather"));

            Assert.AreEqual(10f, world.PlayerMotor.LastTarget.Value.Z, 1e-3f);
        }

        [Test]
        public void NpcRunner_FollowPlayer_AndFacePlayer_AimAtTheLocalPlayer()
        {
            AgentsWorld world = new();
            world.AddPlayer("steve", new RbxVector3(0f, 3f, 20f));
            RbxInstance bob = world.AddVillager("Bob", Origin, out RbxHumanoid _, out RecordingMotor motor);
            RbxHumanoidNpcActuator actuator = new(RbxCharacterSubject.Of(world.World, bob));

            actuator.Apply(new NpcDecision { NpcId = "bob", ActionId = "follow_player" });
            Assert.AreEqual(15f, motor.LastTarget.Value.Z, 1e-3f);
            actuator.Apply(new NpcDecision { NpcId = "bob", ActionId = "face_player" });

            Assert.AreEqual(RbxCharacterCommandOutcome.Done, actuator.LastOutcome);
            Assert.AreEqual("face_player", bob.GetAttribute(RbxAgentAttributes.NpcAction));
            RbxVector3 look = world.Poses[bob.FindFirstChild("HumanoidRootPart").Id].LookVector;
            Assert.AreEqual(1f, look.Z, 1e-3f);
        }

        [Test]
        public void NpcActuator_PublishesActionsItCannotCarryOut()
        {
            AgentsWorld world = new();
            RbxInstance bob = world.AddVillager("Bob", Origin, out RbxHumanoid _, out RecordingMotor motor);
            RbxHumanoidNpcActuator actuator = new(RbxCharacterSubject.Of(world.World, bob));

            actuator.Apply(new NpcDecision { NpcId = "bob", ActionId = "trade" });

            Assert.AreEqual(RbxCharacterCommandOutcome.Published, actuator.LastOutcome);
            Assert.AreEqual("trade", bob.GetAttribute(RbxAgentAttributes.NpcAction));
            CollectionAssert.IsEmpty(motor.MoveTargets);
        }

        [Test]
        public void State_DescribesTheCharacter_ItsSurroundings_AndTheModsFacts()
        {
            AgentsWorld world = new();
            world.AddPlayer("steve", Origin);
            world.AddVillager("Bob", new RbxVector3(6f, 3f, -6f), out RbxHumanoid _, out RecordingMotor _);
            world.AddVillager("Farmer", new RbxVector3(500f, 3f, 0f), out RbxHumanoid _, out RecordingMotor _);
            world.Player.SetAttribute(RbxAgentAttributes.AutopilotState, "Oak logs lie 6 studs north.");
            RbxCharacterStateProvider state = new(RbxCharacterSubject.LocalPlayer(world.World),
                new RbxCharacterStateOptions
                {
                    Role = "the player's character", FactsAttribute = RbxAgentAttributes.AutopilotState
                }, () => "gather");

            string text = state.DescribeState();

            StringAssert.StartsWith("You are " + world.Player.DisplayName + ", the player's character.", text);
            StringAssert.Contains("Health 100 of 100.", text);
            StringAssert.Contains("You stand at (0, 3, 0).", text);
            StringAssert.Contains("Nearby within 48 studs: Bob 8 studs north-east.", text);
            StringAssert.DoesNotContain("Farmer", text);
            StringAssert.Contains("Your current goal: gather.", text);
            StringAssert.EndsWith("Oak logs lie 6 studs north.", text);
        }

        [Test]
        public void State_WithoutABody_SaysSo()
        {
            AgentsWorld world = new();

            Assert.AreEqual("You have no body in the world right now.",
                new RbxCharacterStateProvider(RbxCharacterSubject.LocalPlayer(world.World)).DescribeState());
        }

        [Test]
        public void State_ForAVillager_MentionsThePlayer()
        {
            AgentsWorld world = new();
            world.AddPlayer("steve", new RbxVector3(0f, 3f, 5f));
            RbxInstance bob = world.AddVillager("Bob", Origin, out RbxHumanoid _, out RecordingMotor _);

            string text = new RbxCharacterStateProvider(RbxCharacterSubject.Of(world.World, bob)).DescribeState();

            StringAssert.StartsWith("You are Bob.", text);
            StringAssert.Contains(world.Player.DisplayName + " (a player) 5 studs south", text);
        }

        [Test]
        public void Compass_NorthIsMinusZ_EastIsPlusX()
        {
            Assert.AreEqual("north", RbxCharacterStateProvider.Direction(new RbxVector3(0f, 0f, -3f)));
            Assert.AreEqual("east", RbxCharacterStateProvider.Direction(new RbxVector3(3f, 0f, 0f)));
            Assert.AreEqual("south-west", RbxCharacterStateProvider.Direction(new RbxVector3(-3f, 0f, 3f)));
        }

        [Test]
        public void Input_MovementKeys_StickAndClicks_CountAsThePlayerTakingControl()
        {
            AgentsWorld world = new();
            RbxPlayerInputWatcher watcher = new();
            Assert.IsFalse(watcher.HasPlayerInput(world.Game));

            world.Input.Keys.Add(AgentsWorld.KeyW);
            Assert.IsTrue(watcher.HasPlayerInput(world.Game));
            world.Input.Keys.Clear();
            world.Input.Stick = new RbxVector2(0.5f, 0f);
            Assert.IsTrue(watcher.HasPlayerInput(world.Game));
            world.Input.Stick = new RbxVector2(0.1f, 0f);
            Assert.IsFalse(watcher.HasPlayerInput(world.Game), "inside the dead zone");
            world.Input.Buttons.Add(0);
            Assert.IsTrue(watcher.HasPlayerInput(world.Game));
        }

        [Test]
        public void Input_TheChatKeyAndTheCameraButton_DoNotCount()
        {
            AgentsWorld world = new();
            RbxPlayerInputWatcher watcher = new();

            world.Input.Keys.Add(AgentsWorld.KeyT);
            world.Input.Buttons.Add(1);

            Assert.IsFalse(watcher.HasPlayerInput(world.Game));
        }

        [Test]
        public void Input_TypingInTheChat_OrClickingAPanel_DoesNotCount()
        {
            AgentsWorld world = new();
            RbxPlayerInputWatcher watcher = new();
            world.Input.Focus.SetCaptured("coreai.ui", true);
            world.Input.Keys.Add(AgentsWorld.KeyW);
            world.Input.Buttons.Add(0);

            Assert.IsFalse(watcher.HasPlayerInput(world.Game), "the chat has the keyboard");

            world.Input.Focus.SetCaptured("coreai.ui", false);
            world.Input.Focus.SetPointerOverUi("coreai.ui", true);
            world.Input.Keys.Clear();
            Assert.IsFalse(watcher.HasPlayerInput(world.Game), "the click is the panel's");
            world.Input.Keys.Add(AgentsWorld.KeyW);
            Assert.IsTrue(watcher.HasPlayerInput(world.Game), "hovering a panel does not stop WASD");
        }

        private static float RbxHumanoidDriverPlanar(RbxVector3 offset)
        {
            return new RbxVector3(offset.X, 0f, offset.Z).Magnitude;
        }
    }
}
