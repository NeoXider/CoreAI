using System;
using CoreAI.Mods.Rbx.Datatypes;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Datatypes
{
    /// <summary>
    /// Hand-verified golden fixtures pinning CFrame math to documented Roblox behavior:
    /// right-handed, LookVector = -Z (ROBLOX_API_ROADMAP.md D1, §5.1.1
    /// RbxSpaceGoldenFixtureTests half concerning pure CFrame chirality).
    /// </summary>
    [TestFixture]
    public sealed class RbxCFrameGoldenFixtureEditModeTests
    {
        private const float Epsilon = 1e-5f;
        private const float HalfPi = MathF.PI / 2f;

        [Test]
        public void D1_IdentityAxes_LookVectorIsNegativeZ()
        {
            RbxCFrame cf = RbxCFrame.Identity;
            Assert.IsTrue(cf.LookVector.FuzzyEq(new RbxVector3(0f, 0f, -1f), Epsilon));
            Assert.IsTrue(cf.RightVector.FuzzyEq(RbxVector3.XAxis, Epsilon));
            Assert.IsTrue(cf.UpVector.FuzzyEq(RbxVector3.YAxis, Epsilon));
            Assert.IsTrue(cf.ZVector.FuzzyEq(RbxVector3.ZAxis, Epsilon));
        }

        [Test]
        public void D1_AnglesChirality_PositiveYawTurnsLeft()
        {
            // WHY: right-handed +90 deg about Y carries look (0,0,-1) onto (-1,0,0) — the
            // canonical chirality golden; a left-handed implementation returns (1,0,0).
            RbxCFrame cf = RbxCFrame.Angles(0f, HalfPi, 0f);
            Assert.IsTrue(cf.LookVector.FuzzyEq(new RbxVector3(-1f, 0f, 0f), Epsilon),
                $"LookVector was {cf.LookVector}");
        }

        [Test]
        public void D1_AnglesChirality_PositivePitchLooksUp()
        {
            RbxCFrame cf = RbxCFrame.Angles(HalfPi, 0f, 0f);
            Assert.IsTrue(cf.LookVector.FuzzyEq(new RbxVector3(0f, 1f, 0f), Epsilon),
                $"LookVector was {cf.LookVector}");
        }

        [Test]
        public void LookAt_FacingPositiveX()
        {
            RbxCFrame cf = RbxCFrame.LookAt(RbxVector3.Zero, new RbxVector3(10f, 0f, 0f));
            Assert.IsTrue(cf.LookVector.FuzzyEq(new RbxVector3(1f, 0f, 0f), Epsilon));
            Assert.IsTrue(cf.RightVector.FuzzyEq(new RbxVector3(0f, 0f, 1f), Epsilon));
            Assert.IsTrue(cf.UpVector.FuzzyEq(RbxVector3.YAxis, Epsilon));
        }

        [Test]
        public void LookAt_TowardNegativeZ_IsIdentityRotation()
        {
            RbxCFrame cf = RbxCFrame.LookAt(new RbxVector3(1f, 2f, 3f), new RbxVector3(1f, 2f, -7f));
            Assert.IsTrue(cf.FuzzyEq(RbxCFrame.FromPosition(1f, 2f, 3f), Epsilon));
        }

        [Test]
        public void LookAt_StraightUp_UsesDocumentedXAxisFallback()
        {
            RbxCFrame cf = RbxCFrame.LookAt(RbxVector3.Zero, new RbxVector3(0f, 10f, 0f));
            Assert.IsTrue(cf.LookVector.FuzzyEq(RbxVector3.YAxis, Epsilon));
            // No NaNs and still orthonormal.
            Assert.AreEqual(1f, cf.RightVector.Magnitude, Epsilon);
            Assert.AreEqual(1f, cf.UpVector.Magnitude, Epsilon);
            Assert.AreEqual(0f, cf.RightVector.Dot(cf.UpVector), Epsilon);
        }

        [Test]
        public void DeprecatedPositionLookAtConstructor_MatchesLookAt()
        {
            RbxCFrame a = RbxCFrame.FromPositionLookAt(new RbxVector3(1f, 0f, 0f), new RbxVector3(1f, 0f, -5f));
            RbxCFrame b = RbxCFrame.LookAt(new RbxVector3(1f, 0f, 0f), new RbxVector3(1f, 0f, -5f));
            Assert.IsTrue(a.FuzzyEq(b, Epsilon));
        }

        [Test]
        public void QuaternionConstructor_MatchesAngles()
        {
            float s = MathF.Sin(HalfPi / 2f);
            float c = MathF.Cos(HalfPi / 2f);
            RbxCFrame fromQuat = RbxCFrame.FromQuaternion(0f, 0f, 0f, 0f, s, 0f, c);
            Assert.IsTrue(fromQuat.FuzzyEq(RbxCFrame.Angles(0f, HalfPi, 0f), Epsilon));
        }

        [Test]
        public void QuaternionConstructor_NormalizesNonUnitInput()
        {
            float s = MathF.Sin(HalfPi / 2f);
            float c = MathF.Cos(HalfPi / 2f);
            RbxCFrame scaled = RbxCFrame.FromQuaternion(0f, 0f, 0f, 0f, s * 3f, 0f, c * 3f);
            Assert.IsTrue(scaled.FuzzyEq(RbxCFrame.Angles(0f, HalfPi, 0f), Epsilon));
        }

        [Test]
        public void FromAxisAngle_MatchesAngles()
        {
            RbxCFrame axisAngle = RbxCFrame.FromAxisAngle(RbxVector3.YAxis, HalfPi);
            Assert.IsTrue(axisAngle.FuzzyEq(RbxCFrame.Angles(0f, HalfPi, 0f), Epsilon));
        }

        [Test]
        public void ToWorldSpace_NestedComposition_Golden()
        {
            // Base at (5,0,0) yawed +90 (look = -X); two studs "forward" lands at (3,0,0).
            RbxCFrame parent = RbxCFrame.Angles(0f, HalfPi, 0f) + new RbxVector3(5f, 0f, 0f);
            RbxCFrame world = parent.ToWorldSpace(RbxCFrame.FromPosition(0f, 0f, -2f));
            Assert.IsTrue(world.Position.FuzzyEq(new RbxVector3(3f, 0f, 0f), 1e-4f),
                $"Position was {world.Position}");
        }

        [Test]
        public void ToObjectSpace_IsInverseOfToWorldSpace()
        {
            RbxCFrame parent = RbxCFrame.LookAt(new RbxVector3(2f, 3f, 4f), new RbxVector3(-1f, 0f, 9f));
            RbxCFrame child = RbxCFrame.Angles(0.3f, -1.1f, 2.2f) + new RbxVector3(-5f, 1f, 0.5f);

            RbxCFrame roundTrip = parent.ToObjectSpace(parent.ToWorldSpace(child));
            Assert.IsTrue(roundTrip.FuzzyEq(child, 1e-4f));
        }

        [Test]
        public void Inverse_ComposesToIdentity()
        {
            RbxCFrame cf = RbxCFrame.Angles(0.5f, 1.2f, -0.7f) + new RbxVector3(10f, -3f, 6f);
            Assert.IsTrue((cf * cf.Inverse()).FuzzyEq(RbxCFrame.Identity, 1e-4f));
        }

        [Test]
        public void PointAndVectorTransforms_RespectTranslationRules()
        {
            RbxCFrame cf = RbxCFrame.Angles(0f, HalfPi, 0f) + new RbxVector3(0f, 5f, 0f);

            // Points translate...
            RbxVector3 p = cf.PointToWorldSpace(new RbxVector3(0f, 0f, -1f));
            Assert.IsTrue(p.FuzzyEq(new RbxVector3(-1f, 5f, 0f), Epsilon), $"point was {p}");
            // ...vectors do not.
            RbxVector3 v = cf.VectorToWorldSpace(new RbxVector3(0f, 0f, -1f));
            Assert.IsTrue(v.FuzzyEq(new RbxVector3(-1f, 0f, 0f), Epsilon), $"vector was {v}");

            Assert.IsTrue(cf.PointToObjectSpace(p).FuzzyEq(new RbxVector3(0f, 0f, -1f), Epsilon));
            Assert.IsTrue(cf.VectorToObjectSpace(v).FuzzyEq(new RbxVector3(0f, 0f, -1f), Epsilon));
        }

        [Test]
        public void MultiplyOperator_CFrameTimesVector_TransformsPoint()
        {
            RbxCFrame cf = RbxCFrame.FromPosition(1f, 2f, 3f);
            Assert.IsTrue((cf * new RbxVector3(1f, 1f, 1f)).FuzzyEq(new RbxVector3(2f, 3f, 4f), Epsilon));
        }

        [Test]
        public void Lerp_HalfwayYaw_IsQuarterTurn()
        {
            RbxCFrame goal = RbxCFrame.Angles(0f, HalfPi, 0f) + new RbxVector3(10f, 0f, 0f);
            RbxCFrame mid = RbxCFrame.Identity.Lerp(goal, 0.5f);
            Assert.IsTrue(mid.FuzzyEq(RbxCFrame.Angles(0f, HalfPi / 2f, 0f) + new RbxVector3(5f, 0f, 0f), 1e-4f));
        }

        [Test]
        public void GetComponents_RowMajorOrder_Golden()
        {
            RbxCFrame cf = RbxCFrame.Angles(0f, HalfPi, 0f) + new RbxVector3(1f, 2f, 3f);
            float[] c = cf.GetComponents();
            Assert.AreEqual(12, c.Length);
            Assert.AreEqual(1f, c[0], Epsilon);
            Assert.AreEqual(2f, c[1], Epsilon);
            Assert.AreEqual(3f, c[2], Epsilon);
            // Ry(90) row-major: [0 0 1; 0 1 0; -1 0 0]
            float[] expected = { 0f, 0f, 1f, 0f, 1f, 0f, -1f, 0f, 0f };
            for (int i = 0; i < 9; i++)
            {
                Assert.AreEqual(expected[i], c[3 + i], Epsilon, $"rotation component {i}");
            }
        }

        [Test]
        public void EulerRoundTrips_XYZAndYXZAndOrientation()
        {
            RbxCFrame cf = RbxCFrame.FromEulerAnglesXYZ(0.4f, -0.9f, 1.3f);
            (float rx, float ry, float rz) = cf.ToEulerAnglesXYZ();
            Assert.IsTrue(RbxCFrame.FromEulerAnglesXYZ(rx, ry, rz).FuzzyEq(cf, 1e-4f));

            RbxCFrame yxz = RbxCFrame.FromOrientation(0.4f, -0.9f, 1.3f);
            (float ox, float oy, float oz) = yxz.ToOrientation();
            Assert.IsTrue(RbxCFrame.FromOrientation(ox, oy, oz).FuzzyEq(yxz, 1e-4f));
        }

        [Test]
        public void RotationOrders_XYZEqualsAnglesAndYXZEqualsOrientation()
        {
            Assert.IsTrue(RbxCFrame.FromEulerAngles(0.2f, 0.3f, 0.4f)
                .FuzzyEq(RbxCFrame.Angles(0.2f, 0.3f, 0.4f), Epsilon));
            Assert.IsTrue(RbxCFrame.FromEulerAngles(0.2f, 0.3f, 0.4f, RbxRotationOrder.YXZ)
                .FuzzyEq(RbxCFrame.FromOrientation(0.2f, 0.3f, 0.4f), Epsilon));
        }

        [Test]
        public void ToAxisAngle_RecoverAxisAndAngle()
        {
            (RbxVector3 axis, float angle) = RbxCFrame.FromAxisAngle(RbxVector3.YAxis, 1.1f).ToAxisAngle();
            Assert.IsTrue(axis.FuzzyEq(RbxVector3.YAxis, 1e-4f));
            Assert.AreEqual(1.1f, angle, 1e-4f);

            (RbxVector3 idAxis, float idAngle) = RbxCFrame.Identity.ToAxisAngle();
            Assert.AreEqual(0f, idAngle, Epsilon);
            Assert.IsTrue(idAxis.FuzzyEq(RbxVector3.XAxis, Epsilon));
        }

        [Test]
        public void AngleBetween_QuarterTurn()
        {
            float angle = RbxCFrame.Identity.AngleBetween(RbxCFrame.Angles(0f, HalfPi, 0f));
            Assert.AreEqual(HalfPi, angle, 1e-4f);
        }

        [Test]
        public void FromRotationBetweenVectors_CarriesFromOntoTo()
        {
            RbxCFrame rot = RbxCFrame.FromRotationBetweenVectors(RbxVector3.XAxis, RbxVector3.YAxis);
            Assert.IsTrue(rot.VectorToWorldSpace(RbxVector3.XAxis).FuzzyEq(RbxVector3.YAxis, 1e-4f));
        }

        [Test]
        public void Orthonormalize_RepairsDriftedRotation()
        {
            RbxCFrame drifted = new(
                0f, 0f, 0f,
                1.02f, 0.01f, 0f,
                0f, 0.98f, 0.02f,
                0.01f, 0f, 1.01f);
            RbxCFrame fixedCf = drifted.Orthonormalize();
            Assert.AreEqual(1f, fixedCf.XVector.Magnitude, 1e-4f);
            Assert.AreEqual(1f, fixedCf.YVector.Magnitude, 1e-4f);
            Assert.AreEqual(1f, fixedCf.ZVector.Magnitude, 1e-4f);
            Assert.AreEqual(0f, fixedCf.XVector.Dot(fixedCf.YVector), 1e-4f);
            // Right-handed: x cross y == z.
            Assert.IsTrue(fixedCf.XVector.Cross(fixedCf.YVector).FuzzyEq(fixedCf.ZVector, 1e-4f));
        }

        [Test]
        public void PlusMinusVector_TranslateWithoutRotating()
        {
            RbxCFrame cf = RbxCFrame.Angles(0f, HalfPi, 0f);
            RbxCFrame moved = cf + new RbxVector3(1f, 2f, 3f);
            Assert.IsTrue(moved.Position.FuzzyEq(new RbxVector3(1f, 2f, 3f), Epsilon));
            Assert.IsTrue(moved.LookVector.FuzzyEq(cf.LookVector, Epsilon));
            Assert.IsTrue((moved - new RbxVector3(1f, 2f, 3f)).FuzzyEq(cf, Epsilon));
        }
    }
}
