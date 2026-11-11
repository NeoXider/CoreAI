using System.Collections.Generic;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.CharacterRig
{
    /// <summary>
    /// Engine-free contracts of swimming: a Humanoid starts swimming once its middle is under water and it is not
    /// standing on the bottom with its head out, stops when its feet find ground with its head out, jumps out at the
    /// surface and rises under it; StateChanged reports Freefall to Swimming with no Landed between (water breaks a
    /// fall); a swimmer moves at three quarters of WalkSpeed along its Move direction, rises while Jump is held and
    /// sinks slowly with no input; and on dry land it never swims (twin).
    /// </summary>
    [TestFixture]
    public sealed class SwimmingEditModeTests
    {
        private static readonly RbxWaterContact Deep = new(true, true, 30d);
        private static readonly RbxWaterContact AtSurface = new(true, false, 30d);

        [Test]
        public void TheRules_StartInDeepWater_WadeInShallowWater_AndStopOnTheBankWithTheHeadOut()
        {
            Assert.IsTrue(RbxSwimRules.ShouldSwim(false, Deep, false), "fell into deep water");
            Assert.IsTrue(RbxSwimRules.ShouldSwim(false, AtSurface, false), "floating with the head out still swims");
            Assert.IsFalse(RbxSwimRules.ShouldSwim(false, AtSurface, true), "standing on the bottom, head out: wading");
            Assert.IsTrue(RbxSwimRules.ShouldSwim(true, Deep, true), "touching the bottom of deep water: still swimming");
            Assert.IsFalse(RbxSwimRules.ShouldSwim(true, AtSurface, true), "feet on ground, head out: out");
            Assert.IsFalse(RbxSwimRules.ShouldSwim(true, RbxWaterContact.Dry, false), "left the water");
            Assert.IsTrue(RbxSwimRules.JumpLeavesWater(AtSurface));
            Assert.IsFalse(RbxSwimRules.JumpLeavesWater(Deep), "under water a jump rises instead");
        }

        [Test]
        public void ASwimmer_MovesAtThreeQuartersOfWalkSpeed_RisesOnJump_AndSinksSlowlyWithNoInput()
        {
            RbxVector3 forward = RbxSwimRules.TargetVelocity(new RbxVector3(0f, 0f, -1f), false, 16d);
            Assert.AreEqual(12f, forward.Magnitude, 1e-4f);
            RbxVector3 dive = RbxSwimRules.TargetVelocity(new RbxVector3(0f, -1f, -1f), false, 16d);
            Assert.Less(dive.Y, 0f, "looking down while swimming forward dives");
            RbxVector3 rise = RbxSwimRules.TargetVelocity(RbxVector3.Zero, true, 16d);
            Assert.AreEqual(9.6f, rise.Y, 1e-4f);
            RbxVector3 idle = RbxSwimRules.TargetVelocity(RbxVector3.Zero, false, 16d);
            Assert.AreEqual((float)-RbxSwimRules.SinkStudsPerSecond, idle.Y, 1e-4f);
            Assert.AreEqual(0f, idle.X, 1e-6f);
        }

        [Test]
        public void FallingIntoDeepWater_Swims_WithNoLandedBetween_AndClimbingOutRuns()
        {
            WaterMotor motor = new();
            RbxHumanoid humanoid = Humanoid(motor, out List<string> states);
            motor.Grounded = false;
            Step(humanoid, states);
            motor.Water = Deep;
            Step(humanoid, states);

            Assert.AreEqual(RbxHumanoidState.Swimming, humanoid.GetState());
            Assert.IsTrue(motor.Swimming, "the motor swims");
            CollectionAssert.AreEqual(new[] { "Running>Freefall", "Freefall>Swimming" }, states,
                "water breaks the fall: no Landed");

            motor.Water = AtSurface;
            motor.Grounded = true;
            Step(humanoid, states);

            Assert.AreEqual(RbxHumanoidState.Running, humanoid.GetState(), "feet on the bank, head out: running");
            Assert.IsFalse(motor.Swimming);
        }

        [Test]
        public void Jump_RisesUnderWater_AndJumpsOutAtTheSurface()
        {
            WaterMotor motor = new() { Grounded = false, Water = Deep };
            RbxHumanoid humanoid = Humanoid(motor, out List<string> _);
            humanoid.Advance(0.02d);

            humanoid.RequestJump();
            Assert.AreEqual(1, motor.Rises, "under water Jump lifts the swimmer");
            Assert.AreEqual(RbxHumanoidState.Swimming, humanoid.GetState());

            motor.Water = AtSurface;
            humanoid.RequestJump();
            Assert.AreEqual(RbxHumanoidState.Jumping, humanoid.GetState(), "at the surface it jumps out");
            Assert.AreEqual(1, motor.Jumps);
            Assert.IsFalse(motor.Swimming);
        }

        [Test]
        public void OnDryLand_AHumanoidNeverSwims_NegativeTwin()
        {
            WaterMotor motor = new();
            RbxHumanoid humanoid = Humanoid(motor, out List<string> states);
            motor.Grounded = false;
            Step(humanoid, states);
            motor.Grounded = true;
            Step(humanoid, states);

            CollectionAssert.DoesNotContain(states, "Freefall>Swimming");
            Assert.IsFalse(motor.Swimming);
            Assert.AreEqual(0, motor.Rises);
        }

        private static RbxHumanoid Humanoid(WaterMotor motor, out List<string> states)
        {
            InstanceRegistry registry = new(ClassCatalog.CreateMvp1());
            RbxHumanoid humanoid = (RbxHumanoid)registry.Create("Humanoid");
            humanoid.AttachHost(null, motor, null);
            humanoid.Advance(0.02d);
            states = new List<string>();
            return humanoid;
        }

        /// <summary>One step, logging the state change it made as "Old>New" (signals are deferred; the state is not).</summary>
        private static void Step(RbxHumanoid humanoid, List<string> states)
        {
            RbxHumanoidState before = humanoid.GetState();
            humanoid.Advance(0.02d);
            RbxHumanoidState after = humanoid.GetState();
            if (after != before)
            {
                states.Add(before + ">" + after);
            }
        }

        private sealed class WaterMotor : IRbxCharacterMotor
        {
            public bool Grounded { get; set; } = true;

            public RbxWaterContact Water { get; set; } = RbxWaterContact.Dry;

            public bool Swimming { get; private set; }

            public int Rises { get; private set; }

            public int Jumps { get; private set; }

            public RbxVector3 Position => RbxVector3.Zero;

            public RbxVector3 MoveDirection => RbxVector3.Zero;

            public bool IsGrounded => Grounded;

            public void SetWalkSpeed(double studsPerSecond)
            {
            }

            public void Jump(double jumpPower, double jumpHeight, bool useJumpPower)
            {
            }

            public bool TryJump(double jumpPower, double jumpHeight, bool useJumpPower)
            {
                if (!Grounded && !(Swimming && RbxSwimRules.JumpLeavesWater(Water)))
                {
                    return false;
                }

                Jumps++;
                return true;
            }

            public void MoveTo(RbxVector3? targetStuds)
            {
            }

            public bool SetSwimming(bool swimming)
            {
                Swimming = swimming;
                return true;
            }

            public void SwimRise()
            {
                Rises++;
            }
        }
    }
}
