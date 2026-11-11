using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.CharacterRig
{
    /// <summary>
    /// Engine-free contract of a MoveTo blocked by another character: once the motor has waited a second behind it,
    /// the Humanoid ends the walk as not reached (MoveToFinished false), so the script can choose another way; a walk
    /// that is not blocked keeps going past that second (twin).
    /// </summary>
    [TestFixture]
    public sealed class MoveToBlockedEditModeTests
    {
        [Test]
        public void AWalkBlockedForASecond_EndsAsNotReached()
        {
            BlockedMotor motor = new() { Blocked = RbxHumanoid.MoveToBlockedSeconds };
            RbxHumanoid humanoid = Humanoid(motor);
            humanoid.MoveTo(new RbxVector3(40f, 0f, 0f));
            humanoid.Advance(0.02d);

            Assert.IsNull(motor.Target, "the walk ended: the motor's target was cleared");
        }

        [Test]
        public void AWalkThatIsNotBlocked_KeepsGoing_NegativeTwin()
        {
            BlockedMotor motor = new() { Blocked = 0d };
            RbxHumanoid humanoid = Humanoid(motor);
            humanoid.MoveTo(new RbxVector3(40f, 0f, 0f));
            for (int step = 0; step < 100; step++)
            {
                humanoid.Advance(0.02d);
            }

            Assert.IsNotNull(motor.Target, "two seconds later it still walks toward its target");
        }

        private static RbxHumanoid Humanoid(BlockedMotor motor)
        {
            InstanceRegistry registry = new(ClassCatalog.CreateMvp1());
            RbxHumanoid humanoid = (RbxHumanoid)registry.Create("Humanoid");
            humanoid.AttachHost(null, motor, null);
            return humanoid;
        }

        private sealed class BlockedMotor : IRbxCharacterMotor
        {
            public double Blocked { get; set; }

            public RbxVector3? Target { get; private set; }

            public RbxVector3 Position => RbxVector3.Zero;

            public RbxVector3 MoveDirection => RbxVector3.Zero;

            public bool IsGrounded => true;

            public double WalkBlockedSeconds => Blocked;

            public void SetWalkSpeed(double studsPerSecond)
            {
            }

            public void Jump(double jumpPower, double jumpHeight, bool useJumpPower)
            {
            }

            public void MoveTo(RbxVector3? targetStuds)
            {
                Target = targetStuds;
            }
        }
    }
}
