using System;
using CoreAI.Mods.Rbx.Datatypes;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Datatypes
{
    /// <summary>
    /// P1 stage C, engine-free: the <c>Ray</c> datatype and the perspective maths behind
    /// <c>Camera:ViewportPointToRay</c>, <c>ScreenPointToRay</c>, <c>WorldToViewportPoint</c> and
    /// <c>WorldToScreenPoint</c>, checked against a hand-computed projection.
    /// </summary>
    [TestFixture]
    public sealed class P1RayAndProjectionEditModeTests
    {
        private const float Tolerance = 1e-4f;

        private static readonly RbxVector2 Viewport = new(1920f, 1080f);

        private static void AssertNear(RbxVector3 expected, RbxVector3 actual, string message = null)
        {
            Assert.AreEqual(expected.X, actual.X, Tolerance, message);
            Assert.AreEqual(expected.Y, actual.Y, Tolerance, message);
            Assert.AreEqual(expected.Z, actual.Z, Tolerance, message);
        }

        private static RbxCFrame TurnedCamera()
        {
            return RbxCFrame.FromPosition(new RbxVector3(4f, 10f, -3f))
                   * RbxCFrame.FromEulerAnglesYXZ(-0.3f, 0.9f, 0f);
        }

        [Test]
        public void Ray_New_KeepsOriginAndDirection_AndUnitNormalisesOnlyTheDirection()
        {
            RbxRay ray = new(new RbxVector3(1f, 2f, 3f), new RbxVector3(0f, 0f, -5f));

            AssertNear(new RbxVector3(1f, 2f, 3f), ray.Origin);
            AssertNear(new RbxVector3(0f, 0f, -5f), ray.Direction, "the direction is never normalised behind the caller");
            AssertNear(new RbxVector3(1f, 2f, 3f), ray.Unit.Origin);
            AssertNear(new RbxVector3(0f, 0f, -1f), ray.Unit.Direction);
        }

        [Test]
        public void Ray_ClosestPoint_ProjectsOntoTheUnitRay()
        {
            RbxRay ray = new(RbxVector3.Zero, new RbxVector3(1f, 0f, 0f));

            AssertNear(new RbxVector3(3f, 0f, 0f), ray.ClosestPoint(new RbxVector3(3f, 4f, 0f)));
            Assert.AreEqual(4f, ray.Distance(new RbxVector3(3f, 4f, 0f)), Tolerance);
        }

        [Test]
        public void Ray_ClosestPoint_OfAPointBehindTheOrigin_IsTheOrigin()
        {
            RbxRay ray = new(new RbxVector3(1f, 0f, 0f), new RbxVector3(1f, 0f, 0f));

            AssertNear(new RbxVector3(1f, 0f, 0f), ray.ClosestPoint(new RbxVector3(-2f, 4f, 0f)),
                "a half-line has nothing behind its origin");
            Assert.AreEqual(5f, ray.Distance(new RbxVector3(-2f, 4f, 0f)), Tolerance);
        }

        [Test]
        public void Ray_EqualityAndToString_FollowTheEngine()
        {
            RbxRay ray = new(new RbxVector3(1f, 2f, 3f), new RbxVector3(0f, 0f, -1f));

            Assert.AreEqual("{1, 2, 3}, {0, 0, -1}", ray.ToString());
            Assert.IsTrue(ray == new RbxRay(new RbxVector3(1f, 2f, 3f), new RbxVector3(0f, 0f, -1f)));
            Assert.IsFalse(ray == new RbxRay(new RbxVector3(1f, 2f, 3f), new RbxVector3(0f, 0f, 1f)));
        }

        [Test]
        public void ViewportPointToRay_AtTheViewportCentre_MatchesTheLookVector()
        {
            RbxCFrame camera = TurnedCamera();

            RbxRay ray = RbxCameraProjection.ViewportPointToRay(camera, 70d, Viewport, 960d, 540d);

            AssertNear(camera.LookVector, ray.Direction, "the DoD's 1e-4 bound");
            AssertNear(camera.Position, ray.Origin, "depth 0 starts at the camera");
            Assert.AreEqual(1f, ray.Direction.Magnitude, Tolerance, "a unit ray");
        }

        [Test]
        public void ViewportPointToRay_TopLeftCorner_MatchesAHandComputedProjection()
        {
            // WHY (-2, 1, -1): at 90 degrees vertical tan(45) = 1, so the top-left pixel of a 2:1
            // viewport sits one unit up and two units left of the axis, one unit in front.
            RbxRay ray = RbxCameraProjection.ViewportPointToRay(RbxCFrame.Identity, 90d,
                new RbxVector2(200f, 100f), 0d, 0d);

            AssertNear(new RbxVector3(-2f, 1f, -1f).Unit, ray.Direction);
        }

        [Test]
        public void ViewportPointToRay_RightEdge_LooksRightOfTheCentre_NegativeTwinOfTheCentre()
        {
            RbxRay ray = RbxCameraProjection.ViewportPointToRay(RbxCFrame.Identity, 70d, Viewport, 1920d, 540d);

            Assert.Greater(ray.Direction.X, 0.1f, "the right edge is not the centre");
            Assert.AreEqual(0f, ray.Direction.Y, Tolerance);
        }

        [Test]
        public void ViewportPointToRay_Depth_IsMeasuredAlongTheLookAxis_AndProjectsBack()
        {
            RbxCFrame camera = TurnedCamera();

            RbxRay ray = RbxCameraProjection.ViewportPointToRay(camera, 60d, Viewport, 300d, 200d, 25d);
            RbxVector3 back = RbxCameraProjection.WorldToViewportPoint(camera, 60d, Viewport, ray.Origin,
                out bool onScreen);

            Assert.IsTrue(onScreen);
            Assert.AreEqual(300f, back.X, 1e-2f);
            Assert.AreEqual(200f, back.Y, 1e-2f);
            Assert.AreEqual(25f, back.Z, 1e-3f, "Z is the depth the ray was built at");
        }

        [Test]
        public void WorldToViewportPoint_PointAhead_IsOnScreenAtTheCentre()
        {
            RbxCFrame camera = TurnedCamera();

            RbxVector3 point = RbxCameraProjection.WorldToViewportPoint(camera, 70d, Viewport,
                camera.Position + camera.LookVector * 10f, out bool onScreen);

            Assert.IsTrue(onScreen);
            Assert.AreEqual(960f, point.X, 1e-2f);
            Assert.AreEqual(540f, point.Y, 1e-2f);
            Assert.AreEqual(10f, point.Z, 1e-3f);
        }

        [Test]
        public void WorldToViewportPoint_PointBehindTheCamera_IsNotOnScreen()
        {
            RbxCFrame camera = TurnedCamera();

            RbxVector3 point = RbxCameraProjection.WorldToViewportPoint(camera, 70d, Viewport,
                camera.Position - camera.LookVector * 10f, out bool onScreen);

            Assert.IsFalse(onScreen);
            Assert.Less(point.Z, 0f, "a negative depth says behind");
        }

        [Test]
        public void WorldToViewportPoint_PointOutsideTheFrustum_IsNotOnScreen()
        {
            RbxVector3 point = RbxCameraProjection.WorldToViewportPoint(RbxCFrame.Identity, 70d, Viewport,
                new RbxVector3(100f, 0f, -10f), out bool onScreen);

            Assert.IsFalse(onScreen);
            Assert.Greater(point.X, 1920f);
        }

        [Test]
        public void ScreenVariants_ShiftByTheGuiInset()
        {
            RbxVector2 inset = new(0f, 36f);
            RbxCFrame camera = TurnedCamera();

            RbxRay screen = RbxCameraProjection.ScreenPointToRay(camera, 70d, Viewport, inset, 960d, 504d);
            RbxVector3 screenPoint = RbxCameraProjection.WorldToScreenPoint(camera, 70d, Viewport, inset,
                camera.Position + camera.LookVector * 5f, out bool onScreen);

            AssertNear(camera.LookVector, screen.Direction, "screen y 504 is viewport y 540 under a 36 px inset");
            Assert.IsTrue(onScreen);
            Assert.AreEqual(504f, screenPoint.Y, 1e-2f);
        }

        [TestCase(70d, 70d)]
        [TestCase(0d, 1d)]
        [TestCase(-5d, 1d)]
        [TestCase(500d, 120d)]
        [TestCase(double.NaN, 70d)]
        public void ClampFieldOfView_KeepsTheMirrorRange(double requested, double expected)
        {
            Assert.AreEqual(expected, RbxCameraProjection.ClampFieldOfView(requested), 1e-9d);
        }

        [Test]
        public void SanitizeViewport_AnUnknownViewport_ReadsAsOneByOne()
        {
            RbxVector2 sanitized = RbxCameraProjection.SanitizeViewport(RbxVector2.Zero);

            Assert.AreEqual(1f, sanitized.X);
            Assert.AreEqual(1f, sanitized.Y);
            Assert.AreEqual(1920f, RbxCameraProjection.SanitizeViewport(Viewport).X, "a real viewport is kept");
        }

        [Test]
        public void ViewportPointToRay_NonFiniteDepth_StartsAtTheCamera()
        {
            RbxRay ray = RbxCameraProjection.ViewportPointToRay(RbxCFrame.Identity, 70d, Viewport, 960d, 540d,
                double.PositiveInfinity);

            AssertNear(RbxVector3.Zero, ray.Origin);
            Assert.IsFalse(float.IsNaN(ray.Direction.X) || Math.Abs(ray.Direction.Z + 1f) > Tolerance);
        }
    }
}
