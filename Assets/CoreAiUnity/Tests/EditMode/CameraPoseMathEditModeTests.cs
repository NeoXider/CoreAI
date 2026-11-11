using System.Numerics;
using CoreAI.Vision;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Pins <see cref="CameraPoseMath"/>, the engine-free orbit and aim math behind <c>camera_look</c> and
    /// <c>camera_orbit</c>. No UnityEngine type is used, so the same file also runs outside the editor.
    /// </summary>
    [TestFixture]
    public sealed class CameraPoseMathEditModeTests
    {
        private const float Tolerance = 0.01f;

        [TestCase(0f, 0f, 10f)]
        [TestCase(45f, 30f, 20f)]
        [TestCase(90f, 10f, 5f)]
        [TestCase(135f, -20f, 7.5f)]
        [TestCase(270f, 60f, 12f)]
        [TestCase(359f, 89f, 3f)]
        public void OrbitPosition_DescribedBack_MatchesYawPitchDistance(float yaw, float pitch, float distance)
        {
            Vector3 pivot = new(3f, 2f, -4f);

            Vector3 position = CameraPoseMath.OrbitPosition(pivot, yaw, pitch, distance);
            bool described = CameraPoseMath.TryDescribeOrbit(pivot, position, out float outYaw, out float outPitch,
                out float outDistance);

            Assert.IsTrue(described);
            Assert.AreEqual(0f, CameraPoseMath.DeltaYaw(yaw, outYaw), Tolerance, "yaw");
            Assert.AreEqual(pitch, outPitch, Tolerance, "pitch");
            Assert.AreEqual(distance, outDistance, Tolerance, "distance");
        }

        [Test]
        public void OrbitPosition_PositivePitch_IsAboveThePivot_AndYawZeroIsOnTheMinusZSide()
        {
            Vector3 pivot = Vector3.Zero;

            Vector3 above = CameraPoseMath.OrbitPosition(pivot, 0f, 30f, 10f);
            Vector3 level = CameraPoseMath.OrbitPosition(pivot, 0f, 0f, 10f);
            Vector3 east = CameraPoseMath.OrbitPosition(pivot, 90f, 0f, 10f);

            Assert.Greater(above.Y, 0f, "a positive pitch looks down from above");
            Assert.AreEqual(-10f, level.Z, Tolerance, "yaw 0 looks along +Z, so the camera sits on -Z");
            Assert.AreEqual(0f, level.Y, Tolerance);
            Assert.AreEqual(-10f, east.X, Tolerance, "yaw 90 looks along +X, so the camera sits on -X");
        }

        [Test]
        public void Forward_OfAnOrbit_PointsAtThePivot()
        {
            Vector3 pivot = new(1f, 5f, 2f);
            Vector3 position = CameraPoseMath.OrbitPosition(pivot, 200f, 40f, 9f);

            Vector3 forward = CameraPoseMath.Forward(40f, 200f);
            Vector3 toPivot = Vector3.Normalize(pivot - position);

            Assert.AreEqual(1f, Vector3.Dot(forward, toPivot), 1e-4f);
        }

        [Test]
        public void TryAim_LooksAtThePoint()
        {
            Vector3 from = new(0f, 10f, -10f);
            Vector3 to = Vector3.Zero;

            bool aimed = CameraPoseMath.TryAim(from, to, out float pitch, out float yaw);

            Assert.IsTrue(aimed);
            Assert.AreEqual(45f, pitch, Tolerance, "looking down at 45 degrees");
            Assert.AreEqual(0f, CameraPoseMath.DeltaYaw(0f, yaw), Tolerance, "facing +Z");
            Assert.AreEqual(1f, Vector3.Dot(CameraPoseMath.Forward(pitch, yaw), Vector3.Normalize(to - from)), 1e-4f);
        }

        [Test]
        public void TryAim_SamePoint_HasNoDirection()
        {
            Vector3 point = new(2f, 2f, 2f);

            Assert.IsFalse(CameraPoseMath.TryAim(point, point, out float pitch, out float yaw));
            Assert.AreEqual(0f, pitch);
            Assert.AreEqual(0f, yaw);
        }

        [Test]
        public void TryDescribeOrbit_CameraOnThePivot_ReportsFalseWithZeroDistance()
        {
            Vector3 pivot = new(1f, 1f, 1f);

            bool described = CameraPoseMath.TryDescribeOrbit(pivot, pivot, out _, out _, out float distance);

            Assert.IsFalse(described);
            Assert.AreEqual(0f, distance, Tolerance);
        }

        [TestCase(0f, 0f)]
        [TestCase(360f, 0f)]
        [TestCase(-90f, 270f)]
        [TestCase(450f, 90f)]
        [TestCase(-720f, 0f)]
        [TestCase(float.NaN, 0f)]
        public void NormalizeYaw_WrapsIntoZeroTo360(float input, float expected)
        {
            float normalized = CameraPoseMath.NormalizeYaw(input);

            Assert.AreEqual(expected, normalized, Tolerance);
            Assert.GreaterOrEqual(normalized, 0f);
            Assert.Less(normalized, 360f);
        }

        [TestCase(350f, 10f, 20f)]
        [TestCase(10f, 350f, -20f)]
        [TestCase(0f, 180f, 180f)]
        public void DeltaYaw_IsTheShortestSignedTurn(float a, float b, float expected)
        {
            Assert.AreEqual(expected, CameraPoseMath.DeltaYaw(a, b), Tolerance);
        }
    }
}
