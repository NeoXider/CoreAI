using System;
using CoreAI.Mods.Rbx.Instances.CharacterRig;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.CharacterRig
{
    /// <summary>
    /// The built-in poser on its own: the idle breathes, the walk swings arms against legs with a stride
    /// matched to the speed, a jump raises the arms, a landing dips, a flight leans, death holds, and a
    /// paused world changes nothing.
    /// </summary>
    [TestFixture]
    public sealed class CharacterRigPoserEditModeTests
    {
        private const double Frame = 1d / 60d;

        private static void Play(RbxRigPoser poser, RbxRigPoseKind kind, double speed, double seconds)
        {
            int frames = (int)Math.Round(seconds / Frame);
            for (int frame = 0; frame < frames; frame++)
            {
                poser.Step(Frame, kind, speed);
            }
        }

        private static double MaxLegSwing(RbxRigPoser poser, double speed, double seconds)
        {
            double max = 0d;
            int frames = (int)Math.Round(seconds / Frame);
            for (int frame = 0; frame < frames; frame++)
            {
                poser.Step(Frame, RbxRigPoseKind.Walk, speed);
                max = Math.Max(max, Math.Abs(poser.LegForwardRight));
            }

            return max;
        }

        [Test]
        public void Idle_SwaysAndBreathesSlightly_AndTheLegsStayStill()
        {
            RbxRigPoser poser = new();
            double maxArm = 0d;
            double minArm = 0d;
            double maxBob = 0d;
            for (int frame = 0; frame < 300; frame++)
            {
                poser.Step(Frame, RbxRigPoseKind.Idle, 0d);
                maxArm = Math.Max(maxArm, poser.ArmForwardRight);
                minArm = Math.Min(minArm, poser.ArmForwardRight);
                maxBob = Math.Max(maxBob, Math.Abs(poser.Bob));
                Assert.AreEqual(0d, poser.LegForwardRight, 1e-12d, "an idle character does not walk-cycle");
                Assert.AreEqual(0d, poser.LegForwardLeft, 1e-12d);
            }

            Assert.Greater(maxArm - minArm, 0.02d, "the idle is alive: the arms sway");
            Assert.Less(Math.Max(maxArm, -minArm), 0.1d, "but only slightly");
            Assert.Greater(maxBob, 0.01d, "the chest rises with each breath");
            Assert.Less(maxBob, 0.06d);
            Assert.AreEqual(RbxRigPoseKind.Idle, poser.Kind);
            Assert.IsFalse(poser.IsWalking);
        }

        [Test]
        public void Walk_SwingsEachArmAgainstItsLeg_AndTheLegsAgainstEachOther()
        {
            RbxRigPoser poser = new();
            int checkedFrames = 0;
            for (int frame = 0; frame < 120; frame++)
            {
                poser.Step(Frame, RbxRigPoseKind.Walk, 16d);
                if (Math.Abs(poser.LegForwardRight) < 0.2d)
                {
                    continue;
                }

                checkedFrames++;
                Assert.AreEqual(-poser.LegForwardRight, poser.LegForwardLeft, 1e-6d, "the legs are opposite");
                Assert.AreEqual(-Math.Sign(poser.LegForwardRight), Math.Sign(poser.ArmForwardRight),
                    "the right arm swings against the right leg");
                Assert.AreEqual(-poser.ArmForwardRight, poser.ArmForwardLeft, 1e-6d, "the arms are opposite");
            }

            Assert.Greater(checkedFrames, 30, "the walk swung for most of two seconds");
            Assert.IsTrue(poser.IsWalking);
        }

        [Test]
        public void Walk_TheSwingGrowsWithTheSpeed_AndStaysBounded()
        {
            double slow = MaxLegSwing(new RbxRigPoser(), 4d, 2d);
            double normal = MaxLegSwing(new RbxRigPoser(), 16d, 2d);
            double fast = MaxLegSwing(new RbxRigPoser(), 64d, 2d);
            Assert.Less(slow, normal, "a slow walk takes shorter steps");
            Assert.AreEqual(RbxRigPoser.WalkSwingRadians, normal, 0.02d, "the reference walk swings its full swing");
            Assert.LessOrEqual(fast, RbxRigPoser.WalkSwingRadians * RbxRigPoser.WalkSwingMaxScale + 1e-9d,
                "however fast, the legs never swing past the cap");
        }

        [Test]
        public void Walk_TheGaitRate_MatchesTheGroundSpeed()
        {
            double swing = RbxRigPoser.WalkSwingRadians;
            double rate = RbxRigPoser.GaitRate(RbxRigPoser.WalkReferenceSpeedStuds, swing);
            double secondsPerCycle = 2d * Math.PI / rate;
            double feetCover = 4d * RbxRigPoser.LegReachStuds * Math.Sin(swing);
            Assert.AreEqual(RbxRigPoser.StrideCoverage * RbxRigPoser.WalkReferenceSpeedStuds * secondsPerCycle,
                feetCover, 1e-6d, "per cycle the two steps cover the stated share of the distance walked");
            Assert.Less(RbxRigPoser.GaitRate(8d, swing), rate, "slower on the same stride steps slower");
            Assert.AreEqual(RbxRigPoser.MinGaitRate, RbxRigPoser.GaitRate(0.2d, swing), 1e-12d, "a creep still shuffles");
            Assert.AreEqual(RbxRigPoser.MaxGaitRate, RbxRigPoser.GaitRate(500d, swing), 1e-12d, "a sprint is capped");
        }

        [Test]
        public void Jump_RaisesBothArms_AndAFallKeepsThemUp()
        {
            RbxRigPoser poser = new();
            Play(poser, RbxRigPoseKind.Jump, 16d, 0.3d);
            Assert.AreEqual(RbxRigPoser.JumpArmRadians, poser.ArmForwardRight, 1e-6d, "arms straight up");
            Assert.AreEqual(RbxRigPoser.JumpArmRadians, poser.ArmForwardLeft, 1e-6d);
            Play(poser, RbxRigPoseKind.Fall, 16d, 0.3d);
            Assert.Greater(poser.ArmForwardRight, 2.5d, "falling, the arms stay up");
            Assert.Greater(poser.ArmSpread, 0.4d, "and spread");
            Assert.Greater(poser.LegForwardRight, 0.1d, "the legs part");
            Assert.Less(poser.LegForwardLeft, -0.1d);
        }

        [Test]
        public void Landing_AfterAFall_DipsBriefly_ThenStandsAgain()
        {
            RbxRigPoser poser = new();
            Play(poser, RbxRigPoseKind.Fall, 0d, 0.5d);
            poser.Step(Frame, RbxRigPoseKind.Idle, 0d);
            Assert.Greater(poser.LandingRemaining, 0d, "touching the ground starts the landing");
            Play(poser, RbxRigPoseKind.Idle, 0d, 0.05d);
            Assert.Less(poser.Bob, -0.1d, "the body dips");
            Play(poser, RbxRigPoseKind.Idle, 0d, 0.6d);
            Assert.AreEqual(0d, poser.LandingRemaining, 1e-12d);
            Assert.Greater(poser.Bob, -0.05d, "and stands up again");

            RbxRigPoser walking = new();
            Play(walking, RbxRigPoseKind.Walk, 16d, 0.5d);
            Play(walking, RbxRigPoseKind.Idle, 0d, 0.1d);
            Assert.AreEqual(0d, walking.LandingRemaining, 1e-12d, "twin: stopping a walk is not a landing");
        }

        [Test]
        public void LandedState_PlaysTheDipOnce()
        {
            RbxRigPoser poser = new();
            poser.Step(Frame, RbxRigPoseKind.Land, 0d);
            double first = poser.LandingRemaining;
            poser.Step(Frame, RbxRigPoseKind.Land, 0d);
            Assert.Greater(first, 0d);
            Assert.Less(poser.LandingRemaining, first, "a Landed that lasts does not restart the dip");
        }

        [Test]
        public void Fly_LeansIntoTheTravel_AndHoversInPlace()
        {
            RbxRigPoser travelling = new();
            Play(travelling, RbxRigPoseKind.Fly, RbxRigPoser.FlyReferenceSpeedStuds, 1d);
            Assert.AreEqual(0.55d, travelling.Lean, 1e-6d, "full speed, full lean");
            Assert.Less(travelling.ArmForwardRight, 0d, "the arms trail behind");

            RbxRigPoser hovering = new();
            double minBob = 0d;
            double maxBob = 0d;
            for (int frame = 0; frame < 120; frame++)
            {
                hovering.Step(Frame, RbxRigPoseKind.Fly, 0d);
                minBob = Math.Min(minBob, hovering.Bob);
                maxBob = Math.Max(maxBob, hovering.Bob);
            }

            Assert.AreEqual(0d, hovering.Lean, 1e-6d, "hovering, the body is upright");
            Assert.Greater(maxBob - minBob, 0.1d, "and bobs");
        }

        [Test]
        public void Dead_HoldsTheLastPose()
        {
            RbxRigPoser poser = new();
            Play(poser, RbxRigPoseKind.Walk, 16d, 0.4d);
            double leg = poser.LegForwardRight;
            double arm = poser.ArmForwardLeft;
            Play(poser, RbxRigPoseKind.Dead, 16d, 1d);
            Assert.AreEqual(leg, poser.LegForwardRight, 1e-12d);
            Assert.AreEqual(arm, poser.ArmForwardLeft, 1e-12d);
            Assert.AreEqual(RbxRigPoseKind.Dead, poser.Kind);
        }

        [Test]
        public void ZeroStep_ChangesNothing_AndBadInputIsHarmless()
        {
            RbxRigPoser poser = new();
            Play(poser, RbxRigPoseKind.Walk, 16d, 0.3d);
            double leg = poser.LegForwardRight;
            double phase = poser.GaitPhase;
            for (int frame = 0; frame < 30; frame++)
            {
                poser.Step(0d, RbxRigPoseKind.Walk, 16d);
            }

            Assert.AreEqual(leg, poser.LegForwardRight, 1e-12d, "a paused world freezes the walk");
            Assert.AreEqual(phase, poser.GaitPhase, 1e-12d);
            poser.Step(double.NaN, RbxRigPoseKind.Walk, double.PositiveInfinity);
            poser.Step(-1d, RbxRigPoseKind.Walk, double.NaN);
            Assert.IsFalse(double.IsNaN(poser.LegForwardRight), "NaN never reaches a joint");
            Assert.IsFalse(double.IsNaN(poser.GetTransform(4).X));
        }

        [Test]
        public void Sit_PutsTheLegsForward()
        {
            RbxRigPoser poser = new();
            Play(poser, RbxRigPoseKind.Sit, 16d, 0.5d);
            Assert.AreEqual(Math.PI / 2d, poser.LegForwardRight, 1e-6d);
            Assert.AreEqual(Math.PI / 2d, poser.LegForwardLeft, 1e-6d, "both legs, no stride: a seated character does not walk");
        }
    }
}
