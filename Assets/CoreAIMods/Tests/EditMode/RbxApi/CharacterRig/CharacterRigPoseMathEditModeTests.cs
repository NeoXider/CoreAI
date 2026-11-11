using System;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.CharacterRig;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.CharacterRig
{
    /// <summary>
    /// The engine-free maths of the R6 rig: the rest layout and Roblox's joint values, which way each joint
    /// swings, the state-to-pose mapping with its twins (an anchored or seated character does not walk-cycle),
    /// and the AutoRotate turn.
    /// </summary>
    [TestFixture]
    public sealed class CharacterRigPoseMathEditModeTests
    {
        private const float Tolerance = 1e-4f;

        private static void AssertVector(RbxVector3 expected, RbxVector3 actual, string what)
        {
            Assert.AreEqual(expected.X, actual.X, Tolerance, what + " X");
            Assert.AreEqual(expected.Y, actual.Y, Tolerance, what + " Y");
            Assert.AreEqual(expected.Z, actual.Z, Tolerance, what + " Z");
        }

        private static RbxVector3 LimbCentre(int joint, RbxCFrame transform)
        {
            RbxCFrame torso = RbxR6Rig.RestOffsets[(int)RbxR6Part.Torso];
            return RbxR6Rig.Joints[joint].Part1CFrame(torso, transform).Position;
        }

        [Test]
        public void RestPose_IsTheR6Layout_StandingOnTheFloorUnderTheRoot()
        {
            AssertVector(new RbxVector3(0f, 2f, 0f), RbxR6Rig.RestOffsets[(int)RbxR6Part.Torso].Position, "Torso");
            AssertVector(new RbxVector3(0f, 3.5f, 0f), RbxR6Rig.RestOffsets[(int)RbxR6Part.Head].Position, "Head");
            AssertVector(new RbxVector3(1.5f, 2f, 0f), RbxR6Rig.RestOffsets[(int)RbxR6Part.RightArm].Position,
                "Right Arm");
            AssertVector(new RbxVector3(-1.5f, 2f, 0f), RbxR6Rig.RestOffsets[(int)RbxR6Part.LeftArm].Position,
                "Left Arm");
            AssertVector(new RbxVector3(0.5f, 0f, 0f), RbxR6Rig.RestOffsets[(int)RbxR6Part.RightLeg].Position,
                "Right Leg");
            AssertVector(new RbxVector3(-0.5f, 0f, 0f), RbxR6Rig.RestOffsets[(int)RbxR6Part.LeftLeg].Position,
                "Left Leg");
            for (int index = 0; index < RbxR6Rig.PartCount; index++)
            {
                Assert.IsTrue(RbxR6Rig.RestOffsets[index].Rotation.FuzzyEq(RbxCFrame.Identity),
                    RbxR6Rig.PartNames[index] + " rests unrotated relative to the root");
            }

            float soles = RbxR6Rig.RestOffsets[(int)RbxR6Part.LeftLeg].Y - RbxR6Rig.PartSizes[(int)RbxR6Part.LeftLeg].Y / 2f;
            float rootBottom = -RbxR6Rig.PartSizes[(int)RbxR6Part.HumanoidRootPart].Y / 2f;
            Assert.AreEqual(rootBottom, soles, Tolerance,
                "the soles meet the floor the root's box rests on, so the controls' root height is unchanged");
            AssertVector(RbxR6Rig.RestOffsets[(int)RbxR6Part.Head].Position, RbxR6Rig.HeadOffsetFromRoot,
                "the head offset the camera and eye-height scripts can use");
        }

        [Test]
        public void Joints_CarryRobloxsR6Names_PartsAndMotor6DValues_WithTheRootJointLift()
        {
            string[] names = { "RootJoint", "Neck", "Right Shoulder", "Left Shoulder", "Right Hip", "Left Hip" };
            for (int joint = 0; joint < RbxR6Rig.JointCount; joint++)
            {
                Assert.AreEqual(names[joint], RbxR6Rig.Joints[joint].Name);
                Assert.AreEqual((RbxR6Part)(joint + 1), RbxR6Rig.Joints[joint].Part1, names[joint] + " moves its part");
                Assert.AreEqual(joint + 1, (int)RbxR6Rig.Joints[joint].Part1);
                Assert.AreEqual(joint, RbxR6Rig.JointOf(RbxR6Rig.Joints[joint].Part1));
            }

            Assert.AreEqual(RbxR6Part.HumanoidRootPart, RbxR6Rig.Joints[0].Part0);
            Assert.AreEqual(-1, RbxR6Rig.JointOf(RbxR6Part.HumanoidRootPart), "no joint moves the root");
            Assert.AreEqual(new RbxCFrame(0f, 1f, 0f, -1f, 0f, 0f, 0f, 0f, 1f, 0f, 1f, 0f), RbxR6Rig.Joints[1].C0,
                "Neck.C0 is Roblox's");
            Assert.AreEqual(new RbxCFrame(-0.5f, 0.5f, 0f, 0f, 0f, 1f, 0f, 1f, 0f, -1f, 0f, 0f),
                RbxR6Rig.Joints[2].C1, "Right Shoulder.C1 is Roblox's");
            Assert.AreEqual(new RbxCFrame(-1f, -1f, 0f, 0f, 0f, -1f, 0f, 1f, 0f, 1f, 0f, 0f),
                RbxR6Rig.Joints[5].C0, "Left Hip.C0 is Roblox's");
            Assert.AreEqual(RbxR6Rig.TorsoLiftStuds, RbxR6Rig.Joints[0].C0.Y,
                "DEV: the RootJoint lifts the Torso one leg length above the root");
        }

        [Test]
        public void ShoulderAndHipSwing_ForwardIsMinusZ_OnBothSides_AndTheLeftIsMirrored()
        {
            RbxCFrame swingForward = RbxCFrame.Angles(0f, 0f, 0.5f);
            RbxCFrame swingMirrored = RbxCFrame.Angles(0f, 0f, -0.5f);
            Assert.Less(LimbCentre(2, swingForward).Z, -0.05f, "a positive Right Shoulder angle swings the arm forward");
            Assert.Less(LimbCentre(3, swingMirrored).Z, -0.05f,
                "the Left Shoulder's frame is mirrored: a negative angle swings it forward");
            Assert.Greater(LimbCentre(3, swingForward).Z, 0.05f, "twin: the same angle swings the left arm back");
            Assert.Less(LimbCentre(4, swingForward).Z, -0.05f, "a positive Right Hip angle swings the leg forward");
            Assert.Less(LimbCentre(5, swingMirrored).Z, -0.05f, "and the Left Hip is mirrored the same way");
            AssertVector(new RbxVector3(1.5f, 2f, 0f), LimbCentre(2, RbxCFrame.Identity), "an identity transform rests");
        }

        [Test]
        public void ArmSwing_PivotsAtTheShoulder_HalfAStudBelowTheArmsTop()
        {
            RbxVector3 hanging = LimbCentre(2, RbxCFrame.Identity);
            RbxVector3 raised = LimbCentre(2, RbxCFrame.Angles(0f, 0f, (float)Math.PI));
            AssertVector(new RbxVector3(1.5f, 3f, 0f), raised, "pointing straight up, the arm's centre is mirrored about the pivot");
            Assert.AreEqual(2.5f, (hanging.Y + raised.Y) / 2f, Tolerance, "Roblox's shoulder pivot: 0.5 below the arm's top");
        }

        [Test]
        public void Spread_MovesBothArmsAwayFromTheBody()
        {
            RbxCFrame spread = RbxCFrame.Angles(-0.4f, 0f, 0f);
            Assert.Greater(LimbCentre(2, spread).X, 1.55f, "the right arm moves to +X");
            Assert.Less(LimbCentre(3, spread).X, -1.55f, "the left arm moves to -X");
        }

        [TestCase(RbxHumanoidState.Running, 8d, RbxRigPoseKind.Walk)]
        [TestCase(RbxHumanoidState.Running, 0d, RbxRigPoseKind.Idle)]
        [TestCase(RbxHumanoidState.Jumping, 16d, RbxRigPoseKind.Jump)]
        [TestCase(RbxHumanoidState.Freefall, 16d, RbxRigPoseKind.Fall)]
        [TestCase(RbxHumanoidState.Landed, 16d, RbxRigPoseKind.Land)]
        [TestCase(RbxHumanoidState.Flying, 30d, RbxRigPoseKind.Fly)]
        [TestCase(RbxHumanoidState.Dead, 16d, RbxRigPoseKind.Dead)]
        public void Selector_MapsTheHumanoidState_ToItsPose(RbxHumanoidState state, double speed,
            RbxRigPoseKind expected)
        {
            Assert.AreEqual(expected, RbxRigPoseSelector.Select(state, speed, false, false, false));
        }

        [TestCase(RbxHumanoidState.Running, 16d)]
        [TestCase(RbxHumanoidState.Jumping, 16d)]
        [TestCase(RbxHumanoidState.Freefall, 16d)]
        [TestCase(RbxHumanoidState.Flying, 30d)]
        public void Selector_AnAnchoredRoot_Stands_NegativeTwin(RbxHumanoidState state, double speed)
        {
            Assert.AreEqual(RbxRigPoseKind.Idle, RbxRigPoseSelector.Select(state, speed, true, false, true),
                "an anchored character does not walk-cycle, jump or fall, whatever it reports");
        }

        [Test]
        public void Selector_ASeatedCharacter_Sits_AndADeadOneStaysDead_NegativeTwins()
        {
            Assert.AreEqual(RbxRigPoseKind.Sit,
                RbxRigPoseSelector.Select(RbxHumanoidState.Running, 16d, false, true, true),
                "a seated character does not walk-cycle");
            Assert.AreEqual(RbxRigPoseKind.Dead,
                RbxRigPoseSelector.Select(RbxHumanoidState.Dead, 16d, true, true, true),
                "death wins over everything");
        }

        [Test]
        public void Selector_TheWalkStartsAndStopsOnTheHumanoidsTwoLines()
        {
            double between = (RbxRigPoseSelector.WalkStartSpeedStuds + RbxRigPoseSelector.WalkStopSpeedStuds) / 2d;
            Assert.AreEqual(RbxRigPoseKind.Idle,
                RbxRigPoseSelector.Select(RbxHumanoidState.Running, between, false, false, false),
                "a standing character needs the start speed");
            Assert.AreEqual(RbxRigPoseKind.Walk,
                RbxRigPoseSelector.Select(RbxHumanoidState.Running, between, false, false, true),
                "a walking one keeps walking until the stop speed");
            Assert.AreEqual(RbxRigPoseKind.Idle,
                RbxRigPoseSelector.Select(RbxHumanoidState.Running, 0.05d, false, false, true));
        }

        [Test]
        public void Facing_YawOf_IsTheInverseOfCFrameAnglesLookVector()
        {
            double[] yaws = { 0d, 0.7d, Math.PI / 2d, 2.5d, -1.2d, -Math.PI / 2d };
            foreach (double yaw in yaws)
            {
                RbxVector3 look = RbxCFrame.Angles(0f, (float)yaw, 0f).LookVector;
                Assert.AreEqual(yaw, RbxCharacterFacing.YawOf(look), 1e-5d, "yaw " + yaw);
                Assert.IsTrue(RbxCharacterFacing.TryYawOfDirection(look * 3f, out double fromDirection));
                Assert.AreEqual(yaw, fromDirection, 1e-5d, "a longer direction has the same heading");
            }

            Assert.IsFalse(RbxCharacterFacing.TryYawOfDirection(new RbxVector3(0f, 1f, 0f), out _),
                "twin: straight up has no heading, so a character flying up keeps its own");
        }

        [Test]
        public void Facing_TurnsTheShortWay_NeverOvershoots_AndSettles()
        {
            double from = 170d * Math.PI / 180d;
            double to = -170d * Math.PI / 180d;
            double step = RbxCharacterFacing.StepYaw(from, to, 1d / 60d);
            Assert.Greater(RbxCharacterFacing.WrapAngle(step - from), 0d, "170 to -170 degrees turns 20 degrees, not 340");

            double yaw = 0d;
            double target = 2d;
            for (int frame = 0; frame < 60; frame++)
            {
                double next = RbxCharacterFacing.StepYaw(yaw, target, 1d / 60d);
                Assert.LessOrEqual(next, target + 1e-12d, "never past the heading");
                Assert.GreaterOrEqual(next, yaw, "always toward it");
                yaw = next;
            }

            Assert.AreEqual(target, yaw, 1e-3d, "a second of walking faces the heading");
            Assert.AreEqual(1.3d, RbxCharacterFacing.StepYaw(1.3d, 2.9d, 0d), 1e-12d, "a paused world does not turn");
        }

        [Test]
        public void Facing_IsFrameRateIndependent()
        {
            double at60 = 0d;
            for (int frame = 0; frame < 6; frame++)
            {
                at60 = RbxCharacterFacing.StepYaw(at60, 1.5d, 1d / 60d);
            }

            double at30 = 0d;
            for (int frame = 0; frame < 3; frame++)
            {
                at30 = RbxCharacterFacing.StepYaw(at30, 1.5d, 1d / 30d);
            }

            Assert.AreEqual(at60, at30, 1e-9d, "a tenth of a second turns as far at 30 Hz as at 60 Hz");
            double ninetyPercent = Math.Log(10d) / RbxCharacterFacing.DefaultTurnResponsePerSecond;
            Assert.Less(ninetyPercent, 0.2d, "a half turn is nine tenths done in under 0.2 s");
        }
    }
}
