using CoreAI.Mods.Roblox.Datatypes;
using CoreAI.Mods.Roblox.Spatial;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode.RobloxApi.Datatypes
{
    /// <summary>
    /// RobloxSpaceRoundTripTests per ROBLOX_API_ROADMAP.md §5.1.1: property-style round trips
    /// FromUnity(ToUnity(x)) == x over randomized positions/rotations/CFrames, both directions,
    /// at both locked scales (0.28 default and 1:1), via the internal test-only scale hook.
    /// </summary>
    [TestFixture]
    public sealed class RobloxSpaceRoundTripEditModeTests
    {
        private const float Epsilon = 1e-3f;

        [TearDown]
        public void RestoreDefaultScale()
        {
            RobloxSpace.ResetForTests();
        }

        [TestCase(0.28f)]
        [TestCase(1f)]
        public void DEV4_PositionRoundTrip_RobloxFirst(float scale)
        {
            RobloxSpace.ResetForTests(scale);
            var rng = new System.Random(1234);
            for (int i = 0; i < 200; i++)
            {
                var rbx = new RbxVector3(NextCoord(rng), NextCoord(rng), NextCoord(rng));
                RbxVector3 roundTrip = RobloxSpace.FromUnity(RobloxSpace.ToUnity(rbx));
                Assert.IsTrue(roundTrip.FuzzyEq(rbx, Epsilon), $"{rbx} -> {roundTrip} at scale {scale}");
            }
        }

        [TestCase(0.28f)]
        [TestCase(1f)]
        public void DEV4_PositionRoundTrip_UnityFirst(float scale)
        {
            RobloxSpace.ResetForTests(scale);
            var rng = new System.Random(4321);
            for (int i = 0; i < 200; i++)
            {
                var unity = new Vector3(NextCoord(rng), NextCoord(rng), NextCoord(rng));
                Vector3 roundTrip = RobloxSpace.ToUnity(RobloxSpace.FromUnity(unity));
                Assert.Less((roundTrip - unity).magnitude, Epsilon, $"{unity} -> {roundTrip} at scale {scale}");
            }
        }

        [TestCase(0.28f)]
        [TestCase(1f)]
        public void DEV4_CFrameRoundTrip_RobloxFirst(float scale)
        {
            RobloxSpace.ResetForTests(scale);
            var rng = new System.Random(777);
            for (int i = 0; i < 100; i++)
            {
                RbxCFrame cf = RandomCFrame(rng);
                (Vector3 pos, Quaternion rot) = RobloxSpace.ToUnityPose(cf);
                RbxCFrame roundTrip = RobloxSpace.FromUnity(pos, rot);
                Assert.IsTrue(roundTrip.FuzzyEq(cf, Epsilon),
                    $"iteration {i}: {cf} -> {roundTrip} at scale {scale}");
            }
        }

        [TestCase(0.28f)]
        [TestCase(1f)]
        public void DEV4_RotationRoundTrip_UnityFirst(float scale)
        {
            RobloxSpace.ResetForTests(scale);
            var rng = new System.Random(555);
            for (int i = 0; i < 100; i++)
            {
                Quaternion q = Quaternion.Euler(NextAngle(rng), NextAngle(rng), NextAngle(rng));
                Quaternion roundTrip = RobloxSpace.ToUnity(RobloxSpace.RotationFromUnity(q));
                float angle = Quaternion.Angle(q, roundTrip);
                Assert.Less(angle, 0.05f, $"iteration {i}: angle drift {angle} deg at scale {scale}");
            }
        }

        [Test]
        public void D2_ModSpaceZIsNegatedUnityZ()
        {
            // WHY: the documented visible artifact of the handedness bridge (D2) — stated
            // explicitly so a sign regression fails by name.
            RobloxSpace.ResetForTests(1f);
            Vector3 unity = RobloxSpace.ToUnity(new RbxVector3(0f, 0f, 5f));
            Assert.AreEqual(-5f, unity.z, 1e-5f);
            Assert.AreEqual(5f, RobloxSpace.FromUnity(new Vector3(0f, 0f, -5f)).Z, 1e-5f);
        }

        [Test]
        public void D3_DefaultScaleIsPoint28MetersPerStud()
        {
            RobloxSpace.ResetForTests();
            Assert.AreEqual(0.28f, RobloxSpace.MetersPerStud, 1e-6f);
            Assert.AreEqual(0.28f, RobloxSpace.DefaultMetersPerStud, 1e-6f);
        }

        [Test]
        public void D3_ConfigureIsOncePerSession()
        {
            RobloxSpace.ResetForTests();
            RobloxSpace.Configure(1f);
            Assert.AreEqual(1f, RobloxSpace.MetersPerStud, 1e-6f);
            // Same value again is tolerated; a different value is the session-constant violation.
            Assert.DoesNotThrow(() => RobloxSpace.Configure(1f));
            Assert.Throws<System.InvalidOperationException>(() => RobloxSpace.Configure(0.28f));
        }

        [Test]
        public void D3_ScaleSwitchTouchesOnlyTheConstant()
        {
            // Acceptance §5.1.8 item 11 (datatype half): the same stud-space size maps through
            // Size * MetersPerStud under both configs with zero code/asset differences.
            var size = new RbxVector3(4f, 1f, 2f);

            RobloxSpace.ResetForTests(0.28f);
            Vector3 at028 = RobloxSpace.SizeToUnity(size);
            Assert.Less((at028 - new Vector3(1.12f, 0.28f, 0.56f)).magnitude, 1e-5f);

            RobloxSpace.ResetForTests(1f);
            Vector3 at1 = RobloxSpace.SizeToUnity(size);
            Assert.Less((at1 - new Vector3(4f, 1f, 2f)).magnitude, 1e-5f);
        }

        private static float NextCoord(System.Random rng) => (float)(rng.NextDouble() * 2000.0 - 1000.0);

        private static float NextAngle(System.Random rng) => (float)(rng.NextDouble() * 720.0 - 360.0);

        private static RbxCFrame RandomCFrame(System.Random rng)
        {
            RbxCFrame rotation = RbxCFrame.FromEulerAnglesXYZ(
                NextAngle(rng) * Mathf.Deg2Rad,
                NextAngle(rng) * Mathf.Deg2Rad,
                NextAngle(rng) * Mathf.Deg2Rad);
            return rotation + new RbxVector3(NextCoord(rng), NextCoord(rng), NextCoord(rng));
        }
    }
}
