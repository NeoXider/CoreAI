using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Binding.CharacterRig;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.CharacterRig;
using CoreAI.Mods.Rbx.Instances.Networking;
using CoreAI.Mods.Rbx.Spatial;
using CoreAI.Tests.EditMode.RbxApi.CharacterRig;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode.RbxApi.Binding
{
    /// <summary>
    /// The character rig on the Unity binder, with a fake motor standing in for physics: the default character
    /// materializes as seven GameObjects, the poses reach the limb transforms, a held limb runs a kinematic
    /// trigger while the root stays the one physical body, a released or anchored limb gets its own physics
    /// back, and the bundled motor turns the root to face where it walks unless AutoRotate is off.
    /// </summary>
    [TestFixture]
    public sealed class CharacterRigBinderEditModeTests
    {
        private const float Tolerance = 1e-3f;
        private const double Frame = 1d / 60d;

        private GameObject _root;
        private InstanceGameObjectBinder _binder;
        private InstanceRegistry _registry;
        private RbxDataModel _game;
        private RbxCharacterRigs _rigs;
        private FakeRigMotor _motor;
        private RbxInstance _character;

        [SetUp]
        public void SetUp()
        {
            RbxSpace.ResetForTests(0.28f);
            _root = new GameObject("CharacterRigTestRoot");
            _binder = new InstanceGameObjectBinder(_root.transform);
            _registry = new InstanceRegistry(null, _binder);
            _game = DataModelBootstrap.CreateGame(_registry);
            PartSinkCharacterRigPort port = new(_binder);
            RbxPlayers players = (RbxPlayers)_game.GetService("Players");
            players.CharacterAutoLoads = false;
            players.CharacterPartSeeder = port;
            RbxPlayer player = players.EnsureActor(_registry, "rig-player");
            _character = RbxCharacterFactory.Load(_registry, _registry.WorldRoot, player);
            _motor = new FakeRigMotor();
            Humanoid().AttachHost(null, _motor, Part(RbxR6Part.HumanoidRootPart));
            _rigs = new RbxCharacterRigs(_registry, null, port);
        }

        [TearDown]
        public void TearDown()
        {
            _rigs.Dispose();
            _game.Destroy();
            Object.DestroyImmediate(_root);
            RbxSpace.ResetForTests();
        }

        private RbxHumanoid Humanoid()
        {
            return (RbxHumanoid)_character.FindFirstChildOfClass("Humanoid");
        }

        private RbxInstance Part(RbxR6Part part)
        {
            return _character.FindFirstChild(RbxR6Rig.NameOf(part));
        }

        private GameObject Bound(RbxR6Part part)
        {
            Assert.IsTrue(_binder.TryGetBoundObject(Part(part).Id, out GameObject gameObject), part + " is materialized");
            return gameObject;
        }

        private void Frames(int count)
        {
            for (int index = 0; index < count; index++)
            {
                _rigs.Step(Frame);
            }
        }

        [Test]
        public void ThePosesReachTheLimbTransforms_AndTheRootStaysTheOnePhysicalBody()
        {
            Frames(1);

            for (int index = 1; index < RbxR6Rig.PartCount; index++)
            {
                RbxR6Part part = (RbxR6Part)index;
                Transform limb = Bound(part).transform;
                RbxCFrame posed = _binder.GetPartPropertiesOrDefault(Part(part).Id).CFrame;
                (Vector3 position, Quaternion rotation) = RbxSpace.ToUnityPose(posed);
                Assert.Less((limb.position - position).magnitude, Tolerance, part + " is drawn where the rig posed it");
                Assert.Greater(Mathf.Abs(Quaternion.Dot(limb.rotation, rotation)), 0.9999f);
                Rigidbody body = limb.GetComponent<Rigidbody>();
                Assert.IsNotNull(body, part + " is unanchored, so it has a body");
                Assert.IsTrue(body.isKinematic, part + " is held: no gravity, no velocity of its own");
                Assert.IsTrue(limb.GetComponent<Collider>().isTrigger, part + " pushes nothing while held");
                Assert.IsTrue(_binder.IsPoseDriven(Part(part).Id));
            }

            Assert.IsTrue(_binder.GetPartPropertiesOrDefault(Part(RbxR6Part.Torso).Id).CanCollide,
                "the Torso still reports R6's CanCollide true to scripts and the camera");
            GameObject root = Bound(RbxR6Part.HumanoidRootPart);
            Assert.IsFalse(root.GetComponent<Rigidbody>().isKinematic, "the root is the simulated body");
            Assert.IsFalse(root.GetComponent<Collider>().isTrigger, "and the character's collider");
            Assert.IsFalse(root.GetComponent<Renderer>().enabled, "and invisible");
            Assert.IsFalse(_binder.IsPoseDriven(Part(RbxR6Part.HumanoidRootPart).Id));
            Vector3 lift = Bound(RbxR6Part.Torso).transform.position - root.transform.position;
            Assert.AreEqual(RbxSpace.LengthToUnity(RbxR6Rig.TorsoLiftStuds), lift.y, 0.05f,
                "the Torso rides one leg length above the root");
        }

        [Test]
        public void TheHead_IsDrawnAtTheHeadMeshSize_WhileItsPartKeepsRobloxsSize()
        {
            RbxInstance head = Part(RbxR6Part.Head);
            Assert.IsTrue(_binder.TryGetBoundObject(head.Id, out GameObject drawn));
            Assert.IsTrue(_binder.TryGetPartProperties(head.Id, out PartProperties properties));

            Assert.AreEqual(new RbxVector3(2f, 1f, 1f), properties.Size, "a script still reads the R6 Head's 2x1x1");
            Vector3 expected = RbxSpace.SizeToUnity(RbxR6Rig.HeadMeshSize);
            Assert.AreEqual(expected.x, drawn.transform.localScale.x, Tolerance, "drawn as the head mesh: narrower");
            Assert.AreEqual(expected.y, drawn.transform.localScale.y, Tolerance, "and taller than the part");
        }

        [Test]
        public void TheTorso_IsDrawnAtItsOwnSize_AndAZeroDrawnSizeRestoresTheHead_NegativeTwin()
        {
            RbxInstance torso = Part(RbxR6Part.Torso);
            Assert.IsTrue(_binder.TryGetBoundObject(torso.Id, out GameObject torsoObject));
            Assert.AreEqual(RbxSpace.SizeToUnity(new RbxVector3(2f, 2f, 1f)).x, torsoObject.transform.localScale.x,
                Tolerance, "no mesh: the part's own size");

            RbxInstance head = Part(RbxR6Part.Head);
            _binder.SetDrawnSize(head.Id, RbxVector3.Zero);
            Assert.IsTrue(_binder.TryGetBoundObject(head.Id, out GameObject headObject));
            Assert.AreEqual(RbxSpace.SizeToUnity(new RbxVector3(2f, 1f, 1f)).x, headObject.transform.localScale.x,
                Tolerance, "a zero drawn size draws the Head at its Size again");
        }

        [Test]
        public void AWalk_SwingsTheLegTransforms_AndStandingStillDoesNot()
        {
            Frames(30);
            Quaternion rootRotation = Bound(RbxR6Part.HumanoidRootPart).transform.rotation;
            float idleAngle = Quaternion.Angle(rootRotation, Bound(RbxR6Part.RightLeg).transform.rotation);
            Assert.Less(idleAngle, 1f, "standing, the legs hang straight");

            _motor.Speed = 16d;
            float maxAngle = 0f;
            for (int frame = 0; frame < 60; frame++)
            {
                _rigs.Step(Frame);
                maxAngle = Mathf.Max(maxAngle,
                    Quaternion.Angle(rootRotation, Bound(RbxR6Part.RightLeg).transform.rotation));
            }

            Assert.Greater(maxAngle, 30f, "walking, the right leg swings tens of degrees");
        }

        [Test]
        public void ALetGoLimb_GetsItsOwnPhysicsBack_NegativeTwin()
        {
            Frames(1);
            RbxInstance torso = Part(RbxR6Part.Torso);
            GameObject torsoObject = Bound(RbxR6Part.Torso);
            torso.Name = "Chest";
            Frames(1);

            Assert.IsFalse(_binder.IsPoseDriven(torso.Id));
            Assert.IsFalse(torsoObject.GetComponent<Rigidbody>().isKinematic, "an unjointed part simulates again");
            Assert.IsFalse(torsoObject.GetComponent<Collider>().isTrigger, "and collides as its CanCollide says");
            Assert.IsTrue(Bound(RbxR6Part.LeftLeg).GetComponent<Collider>().isTrigger,
                "a CanCollide-false leg stays a trigger once let go");
        }

        [Test]
        public void AnchoringAHeldLimb_DropsItsBody_AndUnanchoringBringsItBackHeld()
        {
            Frames(1);
            RbxInstance arm = Part(RbxR6Part.RightArm);
            _binder.SetAnchored(arm.Id, true);
            Assert.IsNull(Bound(RbxR6Part.RightArm).GetComponent<Rigidbody>(), "anchored parts have no body");
            Frames(1);
            Assert.IsFalse(_binder.IsPoseDriven(arm.Id), "a joint cannot move an anchored part");

            _binder.SetAnchored(arm.Id, false);
            Frames(1);
            Assert.IsTrue(_binder.IsPoseDriven(arm.Id));
            Assert.IsTrue(Bound(RbxR6Part.RightArm).GetComponent<Rigidbody>().isKinematic,
                "the new body is held again");
        }

        [Test]
        public void ARespectCanCollideRay_HitsAHeldTorso_ButNotAHeldArm()
        {
            Frames(1);
            Physics.SyncTransforms();
            UnityRbxPhysicsPort port = new(_binder);
            try
            {
                RbxVector3 torso = _binder.GetPartPropertiesOrDefault(Part(RbxR6Part.Torso).Id).CFrame.Position;
                RbxVector3 arm = _binder.GetPartPropertiesOrDefault(Part(RbxR6Part.RightArm).Id).CFrame.Position;
                RbxVector3 down = new(0f, -6f, 0f);
                Assert.IsTrue(port.TryRaycast(torso + new RbxVector3(0f, 4f, 0f), down, true, null,
                    out RbxPhysicsRaycastHit torsoHit), "a held Torso (and Head) still collide for queries");
                Assert.AreNotEqual(Part(RbxR6Part.HumanoidRootPart).Id, torsoHit.Instance);
                Assert.IsTrue(port.TryRaycast(arm + new RbxVector3(0f, 3f, 0f), down, true, null,
                    out RbxPhysicsRaycastHit armHit) == false || armHit.Instance != Part(RbxR6Part.RightArm).Id,
                    "twin: a held arm (CanCollide false) is not hit by a RespectCanCollide ray");
                Assert.IsTrue(port.TryRaycast(arm + new RbxVector3(0f, 3f, 0f), down, false, null,
                    out RbxPhysicsRaycastHit anyHit) && anyHit.Instance == Part(RbxR6Part.RightArm).Id,
                    "but a plain ray hits it, as CanQuery says");
            }
            finally
            {
                port.Dispose();
            }
        }

        [Test]
        public void TheBundledMotor_TurnsTheRootToFaceItsWalk_UnlessAutoRotateIsOff()
        {
            Rigidbody body = Bound(RbxR6Part.HumanoidRootPart).GetComponent<Rigidbody>();
            UnityRbxCharacterMotor motor = new(body);
            Humanoid().AttachHost(null, motor, Part(RbxR6Part.HumanoidRootPart));
            RbxVector3 east = new(1f, 0f, 0f);
            Humanoid().Move(east);
            for (int step = 0; step < 50; step++)
            {
                motor.Step(0.02d);
            }

            RbxVector3 look = RbxSpace.RotationFromUnity(body.transform.rotation).LookVector;
            Assert.AreEqual(1f, look.X, 0.01f, "after a second the character faces +X, where it walks");
            Assert.AreEqual(0f, look.Y, 0.01f, "upright");

            Humanoid().AutoRotate = false;
            Humanoid().Move(new RbxVector3(0f, 0f, 1f));
            for (int step = 0; step < 50; step++)
            {
                motor.Step(0.02d);
            }

            RbxVector3 kept = RbxSpace.RotationFromUnity(body.transform.rotation).LookVector;
            Assert.AreEqual(1f, kept.X, 0.01f, "twin: with AutoRotate off the heading is the script's");
            Assert.IsFalse(motor.AutoRotate);
        }
    }
}
