using System.Collections.Generic;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Instances
{
    /// <summary>
    /// P1 stage B, engine-free: <c>Humanoid:Move</c> resolution, the flight integrator, the Flying state
    /// over a replaceable motor, noclip, and the <c>StarterPlayer</c> control settings.
    /// </summary>
    [TestFixture]
    public sealed class P1CharacterMovementEditModeTests
    {
        private const float Tolerance = 1e-4f;

        /// <summary>A replaceable motor that records what the Humanoid asks of it.</summary>
        private sealed class RecordingMotor : IRbxCharacterMotor
        {
            public readonly List<RbxVector3> Moves = new();
            public readonly List<RbxVector3?> MoveTargets = new();
            public bool CanFly = true;
            public bool Flying;
            public bool Noclip;
            public bool Grounded = true;

            public RbxVector3 Position { get; set; } = RbxVector3.Zero;

            public RbxVector3 MoveDirection => RbxVector3.Zero;

            public bool IsGrounded => Grounded;

            public bool IsFlying => Flying;

            public void SetWalkSpeed(double studsPerSecond)
            {
            }

            public void Jump(double jumpPower, double jumpHeight, bool useJumpPower)
            {
            }

            public void MoveTo(RbxVector3? targetStuds)
            {
                MoveTargets.Add(targetStuds);
            }

            public void Move(RbxVector3 directionStuds)
            {
                Moves.Add(directionStuds);
            }

            public bool SetFlying(bool flying)
            {
                if (!CanFly)
                {
                    return false;
                }

                Flying = flying;
                return true;
            }

            public void SetNoclip(bool noclip)
            {
                Noclip = noclip;
            }
        }

        /// <summary>A motor written before Move existed: only MoveTo.</summary>
        private sealed class LegacyMotor : IRbxCharacterMotor
        {
            public readonly List<RbxVector3?> MoveTargets = new();

            public RbxVector3 Position => new(10f, 0f, 0f);

            public RbxVector3 MoveDirection => RbxVector3.Zero;

            public bool IsGrounded => true;

            public void SetWalkSpeed(double studsPerSecond)
            {
            }

            public void Jump(double jumpPower, double jumpHeight, bool useJumpPower)
            {
            }

            public void MoveTo(RbxVector3? targetStuds)
            {
                MoveTargets.Add(targetStuds);
            }
        }

        private static void AssertVector(RbxVector3 expected, RbxVector3 actual)
        {
            Assert.AreEqual(expected.X, actual.X, Tolerance, "X");
            Assert.AreEqual(expected.Y, actual.Y, Tolerance, "Y");
            Assert.AreEqual(expected.Z, actual.Z, Tolerance, "Z");
        }

        private static RbxHumanoid NewHumanoid(IRbxCharacterMotor motor)
        {
            InstanceRegistry registry = new();
            DataModelBootstrap.CreateGame(registry);
            RbxHumanoid humanoid = (RbxHumanoid)registry.Create("Humanoid");
            humanoid.AttachHost(null, motor, null);
            return humanoid;
        }

        [Test]
        public void ResolveMoveDirection_RelativeToCamera_ReadsNegativeZAsTheCameraForward()
        {
            RbxCFrame camera = RbxCFrame.LookAt(new RbxVector3(0f, 5f, 0f), new RbxVector3(10f, 5f, 0f));

            RbxVector3 forward = RbxCharacterControlMath.ResolveMoveDirection(
                new RbxVector3(0f, 0f, -1f), true, camera, keepVertical: false);
            RbxVector3 right = RbxCharacterControlMath.ResolveMoveDirection(
                new RbxVector3(1f, 0f, 0f), true, camera, keepVertical: false);

            AssertVector(new RbxVector3(1f, 0f, 0f), forward);
            AssertVector(new RbxVector3(0f, 0f, 1f), right);
        }

        [Test]
        public void ResolveMoveDirection_LookingDown_StillWalksAtFullSpeed_AndClampsToUnitLength()
        {
            RbxCFrame camera = RbxCFrame.LookAt(new RbxVector3(0f, 10f, 0f), new RbxVector3(0f, 0f, -1f));

            RbxVector3 diagonal = RbxCharacterControlMath.ResolveMoveDirection(
                new RbxVector3(1f, 0f, -1f), true, camera, keepVertical: false);

            Assert.AreEqual(1f, diagonal.Magnitude, Tolerance);
            Assert.AreEqual(0f, diagonal.Y, Tolerance);
        }

        [Test]
        public void ResolveMoveDirection_DropsVerticalWhenWalking_AndKeepsItWhenFlying()
        {
            RbxVector3 walking = RbxCharacterControlMath.ResolveMoveDirection(
                new RbxVector3(0f, 1f, 0f), false, null, keepVertical: false);
            RbxVector3 flying = RbxCharacterControlMath.ResolveMoveDirection(
                new RbxVector3(0f, 1f, 0f), false, null, keepVertical: true);

            AssertVector(RbxVector3.Zero, walking);
            AssertVector(new RbxVector3(0f, 1f, 0f), flying);
        }

        [Test]
        public void ResolveMoveDirection_NonFiniteInput_IsAStop()
        {
            RbxVector3 resolved = RbxCharacterControlMath.ResolveMoveDirection(
                new RbxVector3(float.NaN, 0f, 1f), false, null, keepVertical: false);

            AssertVector(RbxVector3.Zero, resolved);
        }

        [Test]
        public void StepFlightVelocity_ReachesTheTarget_AndBrakesToAHoverWithNoInput()
        {
            RbxVector3 velocity = RbxVector3.Zero;
            for (int step = 0; step < 50; step++)
            {
                velocity = RbxCharacterControlMath.StepFlightVelocity(
                    velocity, new RbxVector3(32f, 0f, 0f), 120d, 0.02d);
            }

            AssertVector(new RbxVector3(32f, 0f, 0f), velocity);

            double height = 0d;
            for (int step = 0; step < 250; step++)
            {
                velocity = RbxCharacterControlMath.StepFlightVelocity(velocity, RbxVector3.Zero, 120d, 0.02d);
                height += velocity.Y * 0.02d;
            }

            AssertVector(RbxVector3.Zero, velocity);
            Assert.LessOrEqual(System.Math.Abs(height), 0.05d, "no input, no gravity: the height holds");
        }

        [Test]
        public void Move_ForwardsTheResolvedDirectionToTheMotor_AndAZeroMoveStops()
        {
            RecordingMotor motor = new();
            RbxHumanoid humanoid = NewHumanoid(motor);

            humanoid.Move(new RbxVector3(3f, 0f, 4f));
            humanoid.Move(RbxVector3.Zero);

            Assert.AreEqual(2, motor.Moves.Count);
            AssertVector(new RbxVector3(0.6f, 0f, 0.8f), motor.Moves[0]);
            AssertVector(RbxVector3.Zero, motor.Moves[1]);
        }

        [Test]
        public void Move_RelativeToCamera_ReadsTheCameraProvider()
        {
            RecordingMotor motor = new();
            RbxHumanoid humanoid = NewHumanoid(motor);
            humanoid.CameraFrameProvider = () =>
                RbxCFrame.LookAt(RbxVector3.Zero, new RbxVector3(-1f, 0f, 0f));

            humanoid.Move(new RbxVector3(0f, 0f, -1f), relativeToCamera: true);

            AssertVector(new RbxVector3(-1f, 0f, 0f), motor.Moves[0]);
        }

        [Test]
        public void Move_OnAMotorWithoutMove_WalksThroughMoveTo()
        {
            LegacyMotor motor = new();
            RbxHumanoid humanoid = NewHumanoid(motor);

            humanoid.Move(new RbxVector3(1f, 0f, 0f));

            Assert.IsTrue(motor.MoveTargets[motor.MoveTargets.Count - 1].HasValue);
            AssertVector(new RbxVector3(14f, 0f, 0f), motor.MoveTargets[motor.MoveTargets.Count - 1].Value);
        }

        [Test]
        public void SetFlying_EntersFlyingOnAMotorThatFlies_AndHoldsItWhileAirborne()
        {
            RecordingMotor motor = new() { Grounded = false };
            RbxHumanoid humanoid = NewHumanoid(motor);

            Assert.IsTrue(humanoid.SetFlying(true));
            humanoid.Advance(0.1d);

            Assert.AreEqual(RbxHumanoidState.Flying, humanoid.GetState(), "no Freefall while flying");
            Assert.IsTrue(motor.Flying);
        }

        [Test]
        public void SetFlying_IsRefusedByAMotorThatCannotFly()
        {
            RecordingMotor motor = new() { CanFly = false };
            RbxHumanoid humanoid = NewHumanoid(motor);

            Assert.IsFalse(humanoid.SetFlying(true));
            Assert.AreNotEqual(RbxHumanoidState.Flying, humanoid.GetState());
        }

        [Test]
        public void SetFlying_False_DropsToFreefall_ThenLandsOnTheGround()
        {
            RecordingMotor motor = new();
            RbxHumanoid humanoid = NewHumanoid(motor);
            humanoid.SetFlying(true);

            humanoid.SetFlying(false);
            Assert.AreEqual(RbxHumanoidState.Freefall, humanoid.GetState());
            humanoid.Advance(0.1d);

            Assert.AreEqual(RbxHumanoidState.Landed, humanoid.GetState());
            Assert.IsFalse(motor.Flying);
        }

        [Test]
        public void Noclip_ReachesTheMotorOnlyWhileFlying()
        {
            RecordingMotor motor = new();
            RbxHumanoid humanoid = NewHumanoid(motor);

            humanoid.Noclip = true;
            Assert.IsFalse(motor.Noclip, "walking: noclip waits for flight");

            humanoid.SetFlying(true);
            Assert.IsTrue(motor.Noclip);

            humanoid.SetFlying(false);
            Assert.IsFalse(motor.Noclip, "leaving flight ends noclip");
        }

        [Test]
        public void Noclip_Off_KeepsTheMotorColliding_InFlight()
        {
            RecordingMotor motor = new();
            RbxHumanoid humanoid = NewHumanoid(motor);

            humanoid.SetFlying(true);

            Assert.IsFalse(motor.Noclip);
        }

        [Test]
        public void Move_WhileFlying_KeepsTheVerticalPart()
        {
            RecordingMotor motor = new();
            RbxHumanoid humanoid = NewHumanoid(motor);
            humanoid.SetFlying(true);

            humanoid.Move(new RbxVector3(0f, 1f, 0f));

            AssertVector(new RbxVector3(0f, 1f, 0f), motor.Moves[motor.Moves.Count - 1]);
        }

        [Test]
        public void StarterPlayer_Scriptable_TurnsTheDefaultControlsOff()
        {
            InstanceRegistry registry = new();
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            RbxStarterPlayer starter = (RbxStarterPlayer)game.GetService("StarterPlayer");

            Assert.IsTrue(starter.DefaultControlsEnabled);
            starter.DevComputerMovementMode = RbxDevComputerMovementMode.Scriptable;
            Assert.IsFalse(starter.DefaultControlsEnabled);
            starter.DevComputerMovementMode = RbxDevComputerMovementMode.KeyboardMouse;
            Assert.IsTrue(starter.DefaultControlsEnabled);
        }

        [Test]
        public void StarterPlayer_ZoomDistances_DefaultToTheMirror_AndStayOrdered()
        {
            InstanceRegistry registry = new();
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            RbxStarterPlayer starter = (RbxStarterPlayer)game.GetService("StarterPlayer");

            Assert.AreEqual(400d, starter.CameraMaxZoomDistance);
            Assert.AreEqual(0.5d, starter.CameraMinZoomDistance);

            starter.CameraMinZoomDistance = 1000d;
            Assert.AreEqual(400d, starter.CameraMinZoomDistance, "min never exceeds max");
            starter.CameraMaxZoomDistance = 1d;
            Assert.AreEqual(400d, starter.CameraMaxZoomDistance, "max never drops below min");
        }
    }
}
