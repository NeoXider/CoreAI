using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Spatial;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode.RobloxApi.Acceptance
{
    /// <summary>
    /// MVP1 acceptance round trips (§5.1.8 item 10) for the RobloxSpace members the position/
    /// CFrame suite (RobloxSpaceRoundTripEditModeTests) does not cover: sizes (scaled, NO
    /// mirror), directions (mirrored, NO scale), scalar lengths/accelerations, velocities, and
    /// the combined pose starting from the Unity side — property-style over seeded random
    /// spreads at both locked scales.
    /// </summary>
    [TestFixture]
    public sealed class Mvp1RobloxSpaceSizeAndDirectionRoundTripEditModeTests
    {
        private const float Epsilon = 1e-3f;

        [TearDown]
        public void RestoreDefaultScale()
        {
            RobloxSpace.ResetForTests();
        }

        // ---- Sizes: stud <-> meter, no mirror ------------------------------------------------

        [TestCase(0.28f)]
        [TestCase(1f)]
        public void SizeRoundTrip_RobloxFirst(float scale)
        {
            RobloxSpace.ResetForTests(scale);
            var rng = new System.Random(2026);
            for (int i = 0; i < 200; i++)
            {
                var size = new RbxVector3(NextExtent(rng), NextExtent(rng), NextExtent(rng));
                RbxVector3 roundTrip = RobloxSpace.SizeFromUnity(RobloxSpace.SizeToUnity(size));
                Assert.IsTrue(roundTrip.FuzzyEq(size, Epsilon), $"{size} -> {roundTrip} at scale {scale}");
            }
        }

        [TestCase(0.28f)]
        [TestCase(1f)]
        public void SizeRoundTrip_UnityFirst(float scale)
        {
            RobloxSpace.ResetForTests(scale);
            var rng = new System.Random(6202);
            for (int i = 0; i < 200; i++)
            {
                var size = new Vector3(NextExtent(rng), NextExtent(rng), NextExtent(rng));
                Vector3 roundTrip = RobloxSpace.SizeToUnity(RobloxSpace.SizeFromUnity(size));
                Assert.Less((roundTrip - size).magnitude, Epsilon, $"{size} -> {roundTrip} at scale {scale}");
            }
        }

        [Test]
        public void SizeConversion_NeverMirrors()
        {
            // WHY: sizes are extents, not positions — a mirrored size axis would turn every
            // Part inside-out. The z component must keep its sign in both directions.
            RobloxSpace.ResetForTests(0.28f);
            Assert.Greater(RobloxSpace.SizeToUnity(new RbxVector3(1f, 2f, 3f)).z, 0f);
            Assert.Greater(RobloxSpace.SizeFromUnity(new Vector3(1f, 2f, 3f)).Z, 0f);
        }

        // ---- Directions: mirror, no scale ---------------------------------------------------

        [TestCase(0.28f)]
        [TestCase(1f)]
        public void DirectionRoundTrip_PreservesUnitLength_AtAnyScale(float scale)
        {
            RobloxSpace.ResetForTests(scale);
            var rng = new System.Random(31337);
            for (int i = 0; i < 200; i++)
            {
                RbxVector3 direction = new RbxVector3(
                    NextCoord(rng), NextCoord(rng), NextCoord(rng)).Unit;
                Vector3 unity = RobloxSpace.DirectionToUnity(direction);
                Assert.AreEqual(1f, unity.magnitude, Epsilon,
                    "directions must not pick up the stud scale");
                RbxVector3 roundTrip = RobloxSpace.DirectionFromUnity(unity);
                Assert.IsTrue(roundTrip.FuzzyEq(direction, Epsilon),
                    $"{direction} -> {roundTrip} at scale {scale}");
                Assert.AreEqual(-direction.Z, unity.z, Epsilon, "chirality mirror on z only");
            }
        }

        // ---- Velocities and scalars ---------------------------------------------------------

        [TestCase(0.28f)]
        [TestCase(1f)]
        public void VelocityRoundTrip_BothDirections(float scale)
        {
            RobloxSpace.ResetForTests(scale);
            var rng = new System.Random(90210);
            for (int i = 0; i < 100; i++)
            {
                var v = new RbxVector3(NextCoord(rng), NextCoord(rng), NextCoord(rng));
                Assert.IsTrue(RobloxSpace.VelocityFromUnity(RobloxSpace.VelocityToUnity(v))
                    .FuzzyEq(v, Epsilon));
            }
        }

        [TestCase(0.28f)]
        [TestCase(1f)]
        public void LengthAndAccelerationRoundTrip(float scale)
        {
            RobloxSpace.ResetForTests(scale);
            var rng = new System.Random(404);
            for (int i = 0; i < 100; i++)
            {
                float studs = NextCoord(rng);
                Assert.AreEqual(studs,
                    RobloxSpace.LengthFromUnity(RobloxSpace.LengthToUnity(studs)), Epsilon);
                Assert.AreEqual(studs,
                    RobloxSpace.AccelerationFromUnity(RobloxSpace.AccelerationToUnity(studs)),
                    Epsilon);
            }
        }

        // ---- Combined pose, Unity-first -----------------------------------------------------

        [TestCase(0.28f)]
        [TestCase(1f)]
        public void PoseRoundTrip_UnityFirst(float scale)
        {
            // WHY: the CFrame suite proves Roblox->Unity->Roblox; a host object handed to a mod
            // takes the opposite path (FromUnity then ToUnityPose), so both closures are locked.
            RobloxSpace.ResetForTests(scale);
            var rng = new System.Random(112);
            for (int i = 0; i < 100; i++)
            {
                var position = new Vector3(NextCoord(rng), NextCoord(rng), NextCoord(rng));
                Quaternion rotation = Quaternion.Euler(
                    NextAngle(rng), NextAngle(rng), NextAngle(rng));
                RbxCFrame cf = RobloxSpace.FromUnity(position, rotation);
                (Vector3 outPosition, Quaternion outRotation) = RobloxSpace.ToUnityPose(cf);
                Assert.Less((outPosition - position).magnitude, Epsilon,
                    $"iteration {i}: {position} -> {outPosition} at scale {scale}");
                Assert.Less(Quaternion.Angle(rotation, outRotation), 0.05f,
                    $"iteration {i}: rotation drift at scale {scale}");
            }
        }

        private static float NextCoord(System.Random rng) =>
            (float)(rng.NextDouble() * 2000.0 - 1000.0);

        private static float NextExtent(System.Random rng) =>
            (float)(rng.NextDouble() * 512.0 + 0.05);

        private static float NextAngle(System.Random rng) =>
            (float)(rng.NextDouble() * 720.0 - 360.0);
    }
}
