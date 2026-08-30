using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Spatial;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode.RbxApi.Datatypes
{
    /// <summary>
    /// Scene-level handedness golden sourced from the Lane Racer camera in
    /// Assets/CoreAIMods/Runtime/Resources/CoreAIMods/sample_lane_racer.lua.
    /// </summary>
    [TestFixture]
    public sealed class RbxSpaceSceneHandednessGoldenEditModeTests
    {
        private const float Epsilon = 1e-4f;

        private static readonly RbxVector3 Eye = new(0f, 9f, 14f);
        private static readonly RbxVector3 Target = new(0f, 2f, -18f);
        private static readonly RbxVector3 PartL = new(-4f, 1f, 0f);
        private static readonly RbxVector3 PartR = new(4f, 1f, 0f);

        [TearDown]
        public void RestoreDefaultScale()
        {
            RbxSpace.ResetForTests();
        }

        [Test]
        public void D2_LaneRacerScene_ScreenSideAgreementLocksPositiveXOnCameraRight()
        {
            AssertScreenSideAgreement(RbxSpace.DefaultMetersPerStud);
        }

        [Test]
        public void D2_LaneRacerScene_ModSpaceZLocksNegatedUnityZForEveryTracePointBothDirections()
        {
            AssertTracePointBridge(
                0.28f,
                new Vector3(0f, 2.52f, -3.92f),
                new Vector3(0f, 0.56f, 5.04f),
                new Vector3(-1.12f, 0.28f, 0f),
                new Vector3(1.12f, 0.28f, 0f));
        }

        [Test]
        public void D2_LaneRacerScene_ChiralityLocksDirectionBridgeFlipsCameraBasis()
        {
            AssertChirality(RbxSpace.DefaultMetersPerStud);
        }

        [Test]
        public void D3_LaneRacerScene_HandednessConclusionsAreScaleIndependent()
        {
            AssertScreenSideAgreement(0.28f);
            AssertTracePointBridge(
                0.28f,
                new Vector3(0f, 2.52f, -3.92f),
                new Vector3(0f, 0.56f, 5.04f),
                new Vector3(-1.12f, 0.28f, 0f),
                new Vector3(1.12f, 0.28f, 0f));
            AssertChirality(0.28f);

            AssertScreenSideAgreement(1f);
            AssertTracePointBridge(
                1f,
                new Vector3(0f, 9f, -14f),
                new Vector3(0f, 2f, 18f),
                new Vector3(-4f, 1f, 0f),
                new Vector3(4f, 1f, 0f));
            AssertChirality(1f);
        }

        private static void AssertScreenSideAgreement(float scale)
        {
            RbxSpace.ResetForTests(scale);
            RbxCFrame rbxCamera = RbxCFrame.LookAt(Eye, Target);
            RbxVector3 rbxCameraRight = rbxCamera.RightVector;
            float rbxLeftSide = (PartL - Eye).Dot(rbxCameraRight);
            float rbxRightSide = (PartR - Eye).Dot(rbxCameraRight);

            Assert.IsTrue(rbxCameraRight.FuzzyEq(RbxVector3.XAxis, Epsilon));
            Assert.AreEqual(-4f, rbxLeftSide, Epsilon);
            Assert.AreEqual(4f, rbxRightSide, Epsilon);
            Assert.Less(rbxLeftSide, 0f);
            Assert.Greater(rbxRightSide, 0f);
            Assert.Less(rbxLeftSide * rbxRightSide, 0f);

            Vector3 unityEye = RbxSpace.ToUnity(Eye);
            Vector3 unityTarget = RbxSpace.ToUnity(Target);
            Vector3 unityPartL = RbxSpace.ToUnity(PartL);
            Vector3 unityPartR = RbxSpace.ToUnity(PartR);
            Quaternion unityCameraRotation = RbxSpace.ToUnity(rbxCamera);
            Vector3 unityCameraRight = unityCameraRotation * Vector3.right;
            Vector3 unityCameraLook = unityCameraRotation * Vector3.forward;
            float unityLeftSide = Vector3.Dot(unityPartL - unityEye, unityCameraRight);
            float unityRightSide = Vector3.Dot(unityPartR - unityEye, unityCameraRight);

            Assert.Less((unityCameraRight - Vector3.right).magnitude, Epsilon);
            Assert.Less((unityCameraLook - (unityTarget - unityEye).normalized).magnitude, Epsilon);
            Assert.AreEqual(-4f * scale, unityLeftSide, Epsilon);
            Assert.AreEqual(4f * scale, unityRightSide, Epsilon);
            Assert.Less(unityLeftSide, 0f);
            Assert.Greater(unityRightSide, 0f);
            Assert.Less(unityLeftSide * unityRightSide, 0f);
            Assert.AreEqual(Mathf.Sign(rbxLeftSide), Mathf.Sign(unityLeftSide));
            Assert.AreEqual(Mathf.Sign(rbxRightSide), Mathf.Sign(unityRightSide));
        }

        private static void AssertTracePointBridge(
            float scale,
            Vector3 expectedUnityEye,
            Vector3 expectedUnityTarget,
            Vector3 expectedUnityPartL,
            Vector3 expectedUnityPartR)
        {
            RbxSpace.ResetForTests(scale);
            AssertPointBridge(Eye, expectedUnityEye, scale);
            AssertPointBridge(Target, expectedUnityTarget, scale);
            AssertPointBridge(PartL, expectedUnityPartL, scale);
            AssertPointBridge(PartR, expectedUnityPartR, scale);
        }

        private static void AssertPointBridge(RbxVector3 rbxPoint, Vector3 expectedUnityPoint, float scale)
        {
            Vector3 unityPoint = RbxSpace.ToUnity(rbxPoint);
            Assert.Less((unityPoint - expectedUnityPoint).magnitude, Epsilon);
            Assert.AreEqual(-rbxPoint.Z * scale, unityPoint.z, Epsilon);

            RbxVector3 inversePoint = RbxSpace.FromUnity(expectedUnityPoint);
            Assert.IsTrue(inversePoint.FuzzyEq(rbxPoint, Epsilon));
            Assert.AreEqual(-expectedUnityPoint.z / scale, inversePoint.Z, Epsilon);
        }

        private static void AssertChirality(float scale)
        {
            RbxSpace.ResetForTests(scale);
            RbxCFrame rbxCamera = RbxCFrame.LookAt(Eye, Target);
            RbxVector3 rbxRight = rbxCamera.RightVector;
            RbxVector3 rbxUp = rbxCamera.UpVector;
            RbxVector3 rbxZ = rbxCamera.ZVector;
            float rbxTriple = rbxRight.Cross(rbxUp).Dot(rbxZ);

            Vector3 convertedRight = RbxSpace.DirectionToUnity(rbxRight);
            Vector3 convertedUp = RbxSpace.DirectionToUnity(rbxUp);
            Vector3 convertedZ = RbxSpace.DirectionToUnity(rbxZ);
            float convertedTriple = Vector3.Dot(Vector3.Cross(convertedRight, convertedUp), convertedZ);

            Assert.AreEqual(1f, rbxTriple, Epsilon);
            Assert.AreEqual(-1f, convertedTriple, Epsilon);
            Assert.Greater(rbxTriple, 0f);
            Assert.Less(convertedTriple, 0f);
            Assert.Less(rbxTriple * convertedTriple, 0f);
        }
    }
}
