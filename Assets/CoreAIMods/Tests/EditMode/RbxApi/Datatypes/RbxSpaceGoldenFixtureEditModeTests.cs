using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Spatial;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode.RbxApi.Datatypes
{
    /// <summary>
    /// RbxSpaceGoldenFixtureTests per ROBLOX_API_ROADMAP.md §5.1.1: hand-verified fixtures
    /// pinning the handedness bridge (Roblox LookVector -Z onto Unity forward +Z) and the
    /// 0.28 m/stud scale numbers.
    /// </summary>
    [TestFixture]
    public sealed class RbxSpaceGoldenFixtureEditModeTests
    {
        private const float Epsilon = 1e-4f;

        [TearDown]
        public void RestoreDefaultScale()
        {
            RbxSpace.ResetForTests();
        }

        [Test]
        public void D2_IdentityCFrame_MapsToUnityIdentity()
        {
            RbxSpace.ResetForTests();
            Quaternion q = RbxSpace.ToUnity(RbxCFrame.Identity);
            Assert.Less(Quaternion.Angle(q, Quaternion.identity), 1e-3f);
        }

        [Test]
        public void D2_LookVectorMapsToUnityForward()
        {
            RbxSpace.ResetForTests();
            // Roblox identity look = (0,0,-1); the mirrored Unity rotation must aim +Z forward
            // at the mirrored direction.
            RbxCFrame yawed = RbxCFrame.Angles(0f, Mathf.PI / 2f, 0f);
            Quaternion q = RbxSpace.ToUnity(yawed);
            Vector3 unityForward = q * Vector3.forward;
            Vector3 expected = RbxSpace.DirectionToUnity(yawed.LookVector);
            Assert.Less((unityForward - expected).magnitude, Epsilon,
                $"forward {unityForward} vs mirrored LookVector {expected}");
            Assert.Less((unityForward - new Vector3(-1f, 0f, 0f)).magnitude, Epsilon);
        }

        [Test]
        public void D3_PositionScaleGolden_Point28()
        {
            RbxSpace.ResetForTests(0.28f);
            Vector3 unity = RbxSpace.ToUnity(new RbxVector3(1f, 2f, 3f));
            Assert.Less((unity - new Vector3(0.28f, 0.56f, -0.84f)).magnitude, Epsilon);
        }

        [Test]
        public void D3_MeterAuthoredHostObjectReadsAsStuds()
        {
            RbxSpace.ResetForTests(0.28f);
            GameObject hostObject = new("RbxSpaceGoldenWorldHost");
            GameObject worldObject = new("RbxSpaceGoldenMeterObject");
            worldObject.transform.position = new Vector3(0f, 1.8f, 0f);
            RbxWorldHost host = hostObject.AddComponent<RbxWorldHost>();
            try
            {
                host.Initialize();

                Assert.IsTrue(host.Registry.TryGetByWorldName(worldObject.name, out RbxInstance wrapped),
                    "the golden must traverse the real lazy host-world wrapper path");
                PartProperties properties = host.Binder.GetPartPropertiesOrDefault(wrapped.Id);
                Assert.AreEqual(1.8f / 0.28f, properties.Position.Y, 1e-3f,
                    "a meter-authored object at y=1.8 m reads about 6.43 studs");
                Assert.AreEqual(0f, properties.Position.X, Epsilon);
                Assert.AreEqual(0f, properties.Position.Z, Epsilon);
            }
            finally
            {
                Object.DestroyImmediate(hostObject);
                Object.DestroyImmediate(worldObject);
            }
        }

        [Test]
        public void DEV6_GravityGolden_Roblox196Point2Is54Point9MetersPerSecSq()
        {
            RbxSpace.ResetForTests(0.28f);
            Assert.AreEqual(54.936f, RbxSpace.AccelerationToUnity(196.2f), 1e-3f);
        }

        [Test]
        public void Directions_MirrorWithoutScale()
        {
            RbxSpace.ResetForTests(0.28f);
            Vector3 unity = RbxSpace.DirectionToUnity(new RbxVector3(0f, 0f, -1f));
            Assert.Less((unity - Vector3.forward).magnitude, Epsilon,
                "Roblox forward (-Z) is Unity forward (+Z), unit length preserved");
            Assert.AreEqual(1f, unity.magnitude, Epsilon);
        }

        [Test]
        public void Velocities_ScaleAndMirror()
        {
            RbxSpace.ResetForTests(0.28f);
            Vector3 unity = RbxSpace.VelocityToUnity(new RbxVector3(16f, 0f, 0f));
            Assert.Less((unity - new Vector3(4.48f, 0f, 0f)).magnitude, Epsilon,
                "WalkSpeed 16 studs/s is 4.48 m/s at the default scale (feel-parity anchor)");
        }

        [Test]
        public void Sizes_ScaleWithoutMirror()
        {
            RbxSpace.ResetForTests(0.28f);
            Vector3 unity = RbxSpace.SizeToUnity(new RbxVector3(4f, 1f, 2f));
            Assert.Less((unity - new Vector3(1.12f, 0.28f, 0.56f)).magnitude, Epsilon);
            Assert.Greater(unity.z, 0f, "sizes are extents — the Z mirror must not apply");
        }

        [Test]
        public void FullPose_WorldExampleGolden()
        {
            RbxSpace.ResetForTests(1f);
            // A part 10 studs ahead of origin (Roblox -Z), facing origin's forward:
            RbxCFrame cf = RbxCFrame.FromPosition(0f, 0f, -10f);
            (Vector3 pos, Quaternion rot) = RbxSpace.ToUnityPose(cf);
            Assert.Less((pos - new Vector3(0f, 0f, 10f)).magnitude, Epsilon,
                "10 studs 'ahead' in Roblox is +10 on Unity Z");
            Assert.Less(Quaternion.Angle(rot, Quaternion.identity), 1e-3f);
        }
    }
}
