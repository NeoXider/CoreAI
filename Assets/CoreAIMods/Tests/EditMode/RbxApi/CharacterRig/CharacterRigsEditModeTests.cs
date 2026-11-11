using System;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.CharacterRig;
using CoreAI.Mods.Rbx.Instances.Networking;
using CoreAI.Tests.EditMode.Performance;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.CharacterRig
{
    /// <summary>
    /// <see cref="RbxCharacterRigs"/> over a fake pose port: any Model in the Workspace with a Humanoid and R6
    /// part names is posed from its root with no code in the mod, a model with other names is left alone, an
    /// anchored root stands still, an anchored limb stays where it is, a renamed or removed part is let go,
    /// and the default character the factory builds is the R6 body only where parts can be seeded. Posing an
    /// idle or a walking character allocates nothing per frame.
    /// </summary>
    [TestFixture]
    public sealed class CharacterRigsEditModeTests
    {
        private const double Frame = 1d / 60d;

        private InstanceRegistry _registry;
        private RbxDataModel _game;
        private FakeRigPosePort _port;
        private RbxCharacterRigs _rigs;

        [SetUp]
        public void SetUp()
        {
            _registry = new InstanceRegistry();
            _game = DataModelBootstrap.CreateGame(_registry);
            _port = new FakeRigPosePort();
            _rigs = new RbxCharacterRigs(_registry, null, _port);
        }

        [TearDown]
        public void TearDown()
        {
            _rigs.Dispose();
        }

        private void Frames(int count)
        {
            for (int index = 0; index < count; index++)
            {
                _rigs.Step(Frame);
            }
        }

        private static RbxCFrame RootAt(float x, float z, float yaw)
        {
            return RbxCFrame.FromPosition(x, 1f, z) * RbxCFrame.Angles(0f, yaw, 0f);
        }

        private static float MaxSwingFromRest(FakeRigPosePort port, RbxInstance model, RbxR6Part part, RbxCFrame root)
        {
            RbxCFrame rest = root * RbxR6Rig.RestOffsets[(int)part];
            RbxCFrame posed = port.PoseOf(CharacterRigTestWorld.Part(model, part));
            return (posed.Position - rest.Position).Magnitude;
        }

        [Test]
        public void AModelWithAHumanoidAndTheR6Names_IsPosedFromItsRoot_AndTheRootIsNeverWritten()
        {
            RbxCFrame root = RootAt(10f, 5f, (float)(Math.PI / 2d));
            RbxInstance villager = CharacterRigTestWorld.BuildR6(_registry, _port, root, new FakeRigMotor());

            Frames(1);

            Assert.AreEqual(1, _rigs.PosedCount);
            Assert.AreEqual(0, _port.WritesOf(CharacterRigTestWorld.Part(villager, RbxR6Part.HumanoidRootPart)),
                "the motor owns the root");
            for (int index = 1; index < RbxR6Rig.PartCount; index++)
            {
                RbxInstance part = CharacterRigTestWorld.Part(villager, (RbxR6Part)index);
                Assert.AreEqual(1, _port.WritesOf(part), RbxR6Rig.PartNames[index] + " is posed once a frame");
                Assert.IsTrue(_port.Driven.Contains(part.Id), RbxR6Rig.PartNames[index] + " is held for the rig");
                RbxVector3 local = root.PointToObjectSpace(_port.PoseOf(part).Position);
                RbxVector3 rest = RbxR6Rig.RestOffsets[index].Position;
                Assert.Less((local - rest).Magnitude, 0.1f, RbxR6Rig.PartNames[index] + " sits at its rest place, turned with the root");
            }

            Assert.AreEqual(6, _port.Driven.Count, "the root is not held");
        }

        [Test]
        public void AModelWithOtherPartNames_IsLeftAlone_NegativeTwin()
        {
            RbxInstance model = CharacterRigTestWorld.BuildModel(_registry, "Villager", "HumanoidRootPart", "Legs",
                "Head", "Hair");
            foreach (RbxInstance child in model.GetChildren())
            {
                if (child is RbxBasePart)
                {
                    _port.Place(child, RbxCFrame.Identity);
                }
            }

            Frames(30);

            Assert.AreEqual(0, _rigs.PosedCount, "no Torso, no rig");
            Assert.AreEqual(0, _port.TotalWrites, "not a single part is written");
            Assert.AreEqual(0, _port.Driven.Count);
            Assert.AreEqual(1, _rigs.TrackedCount, "the Humanoid is still watched, for parts added later");
        }

        [Test]
        public void ATorsoAddedLater_MakesItARig_OnTheNextSearch()
        {
            RbxInstance model = CharacterRigTestWorld.BuildModel(_registry, "Late", "HumanoidRootPart");
            _port.Place(model.FindFirstChild("HumanoidRootPart"), RootAt(0f, 0f, 0f));
            Frames(2);
            RbxInstance torso = _registry.Create("Part");
            torso.Name = "Torso";
            torso.Parent = model;
            _port.Place(torso, RbxCFrame.Identity);

            int frames = (int)Math.Ceiling(RbxCharacterRigs.IncompleteRigRecheckSeconds / Frame) + 1;
            Frames(frames);

            Assert.AreEqual(1, _rigs.PosedCount, "an incomplete rig is searched again");
            Assert.Greater(_port.WritesOf(torso), 0);
        }

        [Test]
        public void AWalkingCharacter_SwingsItsLimbs_AndAnAnchoredOneStandsStill_NegativeTwin()
        {
            RbxCFrame walkingRoot = RootAt(0f, 0f, 0f);
            FakeRigMotor walkingMotor = new() { Speed = 16d };
            RbxInstance walker = CharacterRigTestWorld.BuildR6(_registry, _port, walkingRoot, walkingMotor);
            RbxCFrame anchoredRoot = RootAt(20f, 0f, 0f);
            FakeRigMotor anchoredMotor = new() { Speed = 16d };
            RbxInstance statue = CharacterRigTestWorld.BuildR6(_registry, _port, anchoredRoot, anchoredMotor,
                rootAnchored: true);

            float walkerSwing = 0f;
            float statueSwing = 0f;
            for (int frame = 0; frame < 60; frame++)
            {
                _rigs.Step(Frame);
                walkerSwing = Math.Max(walkerSwing, MaxSwingFromRest(_port, walker, RbxR6Part.RightLeg, walkingRoot));
                statueSwing = Math.Max(statueSwing, MaxSwingFromRest(_port, statue, RbxR6Part.RightLeg, anchoredRoot));
            }

            Assert.IsTrue(_rigs.TryGetPoser(CharacterRigTestWorld.Humanoid(walker), out RbxRigPoser walking));
            Assert.IsTrue(_rigs.TryGetPoser(CharacterRigTestWorld.Humanoid(statue), out RbxRigPoser standing));
            Assert.AreEqual(RbxRigPoseKind.Walk, walking.Kind);
            Assert.AreEqual(RbxRigPoseKind.Idle, standing.Kind, "an anchored root does not walk-cycle");
            Assert.Greater(walkerSwing, 0.8f, "the walker's leg swings well away from hanging");
            Assert.Less(statueSwing, 0.05f, "the anchored character's legs stay put");
            Assert.AreEqual(2, _rigs.PosedCount, "an anchored character is still posed (it breathes)");
        }

        [Test]
        public void TheHumanoidsState_PicksThePose()
        {
            FakeRigMotor motor = new();
            RbxInstance model = CharacterRigTestWorld.BuildR6(_registry, _port, RootAt(0f, 0f, 0f), motor);
            RbxHumanoid humanoid = CharacterRigTestWorld.Humanoid(model);
            Assert.IsTrue(_rigs.TryGetPoser(humanoid, out RbxRigPoser poser));

            motor.Grounded = false;
            humanoid.Advance(Frame);
            Frames(20);
            Assert.AreEqual(RbxRigPoseKind.Fall, poser.Kind);
            Assert.Greater(poser.ArmForwardRight, 2.5d, "a falling character throws its arms up");

            motor.Grounded = true;
            humanoid.Advance(Frame);
            Frames(1);
            Assert.AreEqual(RbxRigPoseKind.Land, poser.Kind);
            humanoid.Advance(Frame);
            Frames(1);
            Assert.AreEqual(RbxRigPoseKind.Idle, poser.Kind);

            Assert.IsTrue(humanoid.SetFlying(true));
            Frames(1);
            Assert.AreEqual(RbxRigPoseKind.Fly, poser.Kind);

            humanoid.Health = 0d;
            Frames(1);
            Assert.AreEqual(RbxRigPoseKind.Dead, poser.Kind);
        }

        [Test]
        public void AnAnchoredLimb_IsLeftWhereItIs_AndAnAnchoredTorsoCarriesTheLimbs()
        {
            RbxCFrame root = RootAt(0f, 0f, 0f);
            RbxInstance model = CharacterRigTestWorld.BuildR6(_registry, _port, root, new FakeRigMotor());
            RbxInstance arm = CharacterRigTestWorld.Part(model, RbxR6Part.LeftArm);
            RbxInstance torso = CharacterRigTestWorld.Part(model, RbxR6Part.Torso);
            _port.Place(arm, RbxCFrame.FromPosition(99f, 99f, 99f), anchored: true);
            RbxCFrame heldTorso = RbxCFrame.FromPosition(0f, 10f, 0f);
            _port.Place(torso, heldTorso, anchored: true);

            Frames(3);

            Assert.AreEqual(0, _port.WritesOf(arm), "a joint cannot move an anchored part");
            Assert.IsFalse(_port.Driven.Contains(arm.Id));
            Assert.AreEqual(0, _port.WritesOf(torso));
            RbxInstance head = CharacterRigTestWorld.Part(model, RbxR6Part.Head);
            Assert.AreEqual(11.5f, _port.PoseOf(head).Y, 0.1f, "the head hangs from the Torso where it is");
        }

        [Test]
        public void ARenamedPart_IsLetGo_AndARemovedHumanoidIsForgotten()
        {
            RbxInstance model = CharacterRigTestWorld.BuildR6(_registry, _port, RootAt(0f, 0f, 0f), new FakeRigMotor());
            RbxInstance arm = CharacterRigTestWorld.Part(model, RbxR6Part.RightArm);
            Frames(1);
            Assert.IsTrue(_port.Driven.Contains(arm.Id));

            arm.Name = "Prop";
            int writes = _port.WritesOf(arm);
            Frames(2);
            Assert.IsFalse(_port.Driven.Contains(arm.Id), "a part that left its slot is released");
            Assert.AreEqual(writes, _port.WritesOf(arm), "and no longer posed");

            RbxInstance torso = CharacterRigTestWorld.Part(model, RbxR6Part.Torso);
            torso.Name = "Chest";
            Frames(2);
            Assert.AreEqual(0, _rigs.PosedCount, "without a Torso the model is not a rig any more");
            Assert.AreEqual(0, _port.Driven.Count, "and every part it held is released");

            CharacterRigTestWorld.Humanoid(model).Destroy();
            Assert.AreEqual(0, _rigs.TrackedCount, "a destroyed Humanoid is forgotten");
        }

        [Test]
        public void OutsideTheWorkspace_NothingIsPosed_AndSwitchingTheRigOff_ReleasesEverything()
        {
            RbxInstance model = CharacterRigTestWorld.BuildR6(_registry, _port, RootAt(0f, 0f, 0f), new FakeRigMotor());
            Frames(1);
            Assert.AreEqual(6, _port.Driven.Count);

            _rigs.Enabled = false;
            Assert.AreEqual(0, _port.Driven.Count, "switched off, every part is let go");
            int writes = _port.TotalWrites;
            Frames(3);
            Assert.AreEqual(writes, _port.TotalWrites, "and nothing is posed");
            _rigs.Enabled = true;

            model.Parent = null;
            Frames(2);
            Assert.AreEqual(0, _rigs.PosedCount, "a template outside the Workspace is not posed");
            Assert.AreEqual(0, _port.Driven.Count);
        }

        [Test]
        public void ALegacyRigWithoutARootPart_IsPosedAroundItsTorso()
        {
            RbxInstance model = CharacterRigTestWorld.BuildModel(_registry, "Legacy", "Torso", "Head", "Left Leg");
            RbxInstance torso = model.FindFirstChild("Torso");
            _port.Place(torso, RbxCFrame.FromPosition(0f, 3f, 0f));
            _port.Place(model.FindFirstChild("Head"), RbxCFrame.Identity);
            _port.Place(model.FindFirstChild("Left Leg"), RbxCFrame.Identity);

            Frames(1);

            Assert.AreEqual(1, _rigs.PosedCount);
            Assert.AreEqual(0, _port.WritesOf(torso), "the Torso is the root here and is never written");
            Assert.AreEqual(4.5f, _port.PoseOf(model.FindFirstChild("Head")).Y, 0.1f);
            Assert.AreEqual(1f, _port.PoseOf(model.FindFirstChild("Left Leg")).Y, 0.1f);
        }

        [Test]
        public void TheFactory_BuildsTheR6Body_WhenItCanSeedParts()
        {
            RbxPlayers players = (RbxPlayers)_game.GetService("Players");
            players.CharacterAutoLoads = false;
            players.CharacterPartSeeder = _port;
            int rootSeeds = 0;
            players.RootPartSpawnSeeder = (part, size, position) => rootSeeds++;
            RbxPlayer player = players.EnsureActor(_registry, "builder");

            RbxInstance character = RbxCharacterFactory.Load(_registry, _registry.WorldRoot, player);

            for (int index = 0; index < RbxR6Rig.PartCount; index++)
            {
                RbxInstance part = character.FindFirstChild(RbxR6Rig.PartNames[index]);
                Assert.IsNotNull(part, RbxR6Rig.PartNames[index]);
                Assert.IsInstanceOf<RbxBasePart>(part);
            }

            Assert.IsInstanceOf<RbxHumanoid>(character.FindFirstChild("Humanoid"));
            Assert.AreSame(character.FindFirstChild("HumanoidRootPart"), ((RbxModel)character).PrimaryPart,
                "PivotTo moves the character by its root, as in Roblox");
            Assert.AreEqual(RbxR6Rig.PartCount, _port.Seeds.Count, "every part seeded once");
            Assert.AreEqual(1, rootSeeds, "the root-part spawn seeder still sees the root");
            RbxCharacterPartSeed rootSeed = _port.Seeds[0].Seed;
            Assert.AreEqual("HumanoidRootPart", _port.Seeds[0].Part.Name);
            Assert.AreEqual(RbxCharacterFactory.RootPartSize, rootSeed.Size);
            Assert.AreEqual(1f, rootSeed.Transparency, "the root is invisible");
            Assert.IsTrue(rootSeed.CanCollide, "and stays the character's collider");
            foreach ((RbxInstance part, RbxCharacterPartSeed seed) in _port.Seeds)
            {
                Assert.IsFalse(seed.Anchored, part.Name + " is unanchored, as in Roblox");
                Assert.AreEqual(part.Name == "Torso" || part.Name == "Head" || part.Name == "HumanoidRootPart",
                    seed.CanCollide, part.Name + " carries R6's CanCollide");
            }

            Assert.IsFalse(character.Archivable, "Roblox's character Model is not archivable");
        }

        [Test]
        public void TheFactory_WithoutASeeder_KeepsTheMinimalCharacter_NegativeTwin()
        {
            RbxPlayers players = (RbxPlayers)_game.GetService("Players");
            players.CharacterAutoLoads = false;
            RbxPlayer player = players.EnsureActor(_registry, "plain");

            RbxInstance character = RbxCharacterFactory.Load(_registry, _registry.WorldRoot, player);

            Assert.AreEqual(2, character.GetChildren().Count, "a root part and a Humanoid, nothing else");
            Assert.IsNotNull(character.FindFirstChild("HumanoidRootPart"));
            Assert.IsNull(character.FindFirstChild("Torso"));
            Assert.IsNull(((RbxModel)character).PrimaryPart, "the minimal character is unchanged");
        }

        [Test]
        public void APlayersOwnSeeder_WinsOverTheService()
        {
            RbxPlayers players = (RbxPlayers)_game.GetService("Players");
            players.CharacterAutoLoads = false;
            FakeRigPosePort serviceSeeder = new();
            players.CharacterPartSeeder = serviceSeeder;
            RbxPlayer player = players.EnsureActor(_registry, "own");
            player.CharacterPartSeeder = _port;

            RbxCharacterFactory.Load(_registry, _registry.WorldRoot, player);

            Assert.AreEqual(RbxR6Rig.PartCount, _port.Seeds.Count);
            Assert.AreEqual(0, serviceSeeder.Seeds.Count);
        }

        [Test]
        public void AutoRotate_DefaultsOn_ReachesTheMotor_AndSurvivesAClone()
        {
            FakeRigMotor motor = new();
            RbxInstance model = CharacterRigTestWorld.BuildR6(_registry, _port, RootAt(0f, 0f, 0f), motor);
            RbxHumanoid humanoid = CharacterRigTestWorld.Humanoid(model);
            Assert.IsTrue(humanoid.AutoRotate, "mirror default");
            Assert.IsTrue(motor.LastAutoRotate);

            humanoid.AutoRotate = false;
            Assert.IsFalse(motor.LastAutoRotate, "the motor stops turning the character");
            FakeRigMotor rebuilt = new();
            humanoid.AttachHost(null, rebuilt, model.FindFirstChild("HumanoidRootPart"));
            Assert.IsFalse(rebuilt.LastAutoRotate, "a rebuilt motor hears the Humanoid's setting");

            RbxInstance copy = model.Clone();
            Assert.IsNotNull(copy);
            Assert.IsFalse(((RbxHumanoid)copy.FindFirstChildOfClass("Humanoid")).AutoRotate, "Clone keeps AutoRotate");
        }

        [Test]
        public void PosingAnIdleOrAWalkingCharacter_AllocatesNothingPerFrame()
        {
            FakeRigMotor motor = new();
            CharacterRigTestWorld.BuildR6(_registry, _port, RootAt(0f, 0f, 0f), motor);
            Action<int> frames = count =>
            {
                for (int index = 0; index < count; index++)
                {
                    _rigs.Step(Frame);
                }
            };

            AllocationMeter.AssertWithinBudget("idle R6 rig frame", frames, 4000, 0);
            motor.Speed = 16d;
            AllocationMeter.AssertWithinBudget("walking R6 rig frame", frames, 4000, 0);
            motor.Speed = 0d;
            motor.Grounded = false;
            AllocationMeter.AssertWithinBudget("falling R6 rig frame", frames, 4000, 0);
        }

        [Test]
        public void TheAllocationBudget_CatchesAnAllocatingFrame_NegativeTwin()
        {
            CharacterRigTestWorld.BuildR6(_registry, _port, RootAt(0f, 0f, 0f), new FakeRigMotor { Speed = 16d });
            AllocationMeter.AssertTwinBreaksBudget("walking R6 rig frame", count =>
            {
                for (int index = 0; index < count; index++)
                {
                    _rigs.Step(Frame);
                }
            }, 4000, 0);
        }
    }
}
