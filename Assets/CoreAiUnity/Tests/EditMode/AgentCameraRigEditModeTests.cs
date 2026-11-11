using System.Collections.Generic;
using CoreAI.Vision;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// <see cref="AgentCameraRig"/> on real transforms: orbit poses match the requested yaw, pitch and distance within
    /// 0.01, look aims at objects and points, the field of view is clamped to <see cref="AgentCameraOptions"/>, and
    /// every refusal leaves the camera exactly where it was.
    /// </summary>
    [TestFixture]
    public sealed class AgentCameraRigEditModeTests
    {
        private const float Tolerance = 0.01f;
        private readonly List<GameObject> _created = new();

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in _created)
            {
                if (go != null)
                {
                    Object.DestroyImmediate(go);
                }
            }

            _created.Clear();
        }

        private Camera NewCamera(Vector3 position)
        {
            GameObject go = new("RigTestCamera");
            _created.Add(go);
            go.transform.position = position;
            return go.AddComponent<Camera>();
        }

        private static void AssertOrbitPose(Camera camera, Vector3 pivot, float yaw, float pitch, float distance)
        {
            Transform t = camera.transform;
            Vector3 offset = t.position - pivot;
            Assert.AreEqual(distance, offset.magnitude, Tolerance, "distance from the pivot");
            CameraAimAssert.LooksAt(t, pivot, Tolerance, "the camera looks at the pivot");
            Assert.AreEqual(0f, Mathf.DeltaAngle(yaw, t.eulerAngles.y), Tolerance, "yaw");
            Assert.AreEqual(0f, Mathf.DeltaAngle(pitch, t.eulerAngles.x), Tolerance, "pitch");
            Assert.AreEqual(0f, Mathf.DeltaAngle(0f, t.eulerAngles.z), Tolerance, "no roll");
        }

        [Test]
        public void AimCheck_ResolvesAnglesBelowWhatVector3AngleCanReport()
        {
            const double tiny = 0.005;
            double radians = tiny * System.Math.PI / 180.0;
            Vector3 forward = new(0f, 0f, 1f);
            Vector3 nudged = new((float)System.Math.Sin(radians), 0f, (float)System.Math.Cos(radians));

            Assert.AreEqual(tiny, CameraAimAssert.AngleDegrees(forward, nudged), 1e-4,
                "a 0.005 degree aim error is measured, not rounded to 0 or 0.0198");
            Assert.AreEqual(0.0, CameraAimAssert.AngleDegrees(forward, forward * 6f), 1e-9);
            Assert.AreEqual(90.0, CameraAimAssert.AngleDegrees(forward, Vector3.up), 1e-9);
            Assert.AreEqual(180.0, CameraAimAssert.AngleDegrees(forward, -forward), 1e-9);
            Assert.IsNaN(CameraAimAssert.AngleDegrees(forward, Vector3.zero), "no direction never passes a bound");
        }

        [TestCase(0f, 0f, 10f)]
        [TestCase(45f, 30f, 20f)]
        [TestCase(135f, -20f, 6f)]
        [TestCase(300f, 75f, 4f)]
        public void Orbit_AroundAPoint_PoseMatchesTheRequest(float yaw, float pitch, float distance)
        {
            Camera camera = NewCamera(new Vector3(9f, 9f, 9f));
            Vector3 pivot = new(2f, 1f, -3f);
            AgentCameraRig rig = new();

            CameraRigResult result = rig.Orbit(camera, new CameraOrbitRequest
            {
                TargetX = pivot.x, TargetY = pivot.y, TargetZ = pivot.z, Yaw = yaw, Pitch = pitch, Distance = distance
            });

            Assert.IsTrue(result.Ok, result.Message);
            Assert.IsTrue(result.IsOrbit);
            Assert.AreEqual(0f, CameraPoseMath.DeltaYaw(yaw, result.Yaw), Tolerance);
            Assert.AreEqual(pitch, result.Pitch, Tolerance);
            Assert.AreEqual(distance, result.Distance, Tolerance);
            AssertOrbitPose(camera, pivot, yaw, pitch, distance);
        }

        [Test]
        public void Orbit_AroundAnObject_CirclesItsRenderedCentre()
        {
            GameObject build = new("BlockBuild");
            _created.Add(build);
            GameObject block = GameObject.CreatePrimitive(PrimitiveType.Cube);
            block.transform.SetParent(build.transform, false);
            block.transform.localPosition = new Vector3(5f, 1f, 0f);
            Camera camera = NewCamera(new Vector3(0f, 5f, -10f));
            AgentCameraRig rig = new();

            CameraRigResult result = rig.Orbit(camera,
                new CameraOrbitRequest { Target = "BlockBuild", Yaw = 90f, Pitch = 30f, Distance = 8f });

            Assert.IsTrue(result.Ok, result.Message);
            Assert.AreEqual("BlockBuild", result.TargetName);
            Assert.Less(Vector3.Distance(new Vector3(5f, 1f, 0f), result.TargetPoint), 0.001f,
                "the pivot is the centre of what the object renders, not its empty root");
            AssertOrbitPose(camera, result.TargetPoint, 90f, 30f, 8f);
        }

        [Test]
        public void Orbit_OmittedValues_KeepTheCurrentAngleAndDistance()
        {
            Vector3 start = new(0f, 10f, -10f);
            Camera camera = NewCamera(start);
            AgentCameraRig rig = new();

            CameraRigResult result = rig.Orbit(camera, new CameraOrbitRequest { TargetX = 0f, TargetY = 0f, TargetZ = 0f });

            Assert.IsTrue(result.Ok, result.Message);
            Assert.AreEqual(0f, CameraPoseMath.DeltaYaw(0f, result.Yaw), Tolerance);
            Assert.AreEqual(45f, result.Pitch, Tolerance);
            Assert.AreEqual(start.magnitude, result.Distance, Tolerance);
            Assert.Less(Vector3.Distance(start, camera.transform.position), 0.001f, "only the aim changed");
        }

        [Test]
        public void Orbit_WithoutATarget_KeepsCirclingTheLastPivot()
        {
            Camera camera = NewCamera(new Vector3(0f, 0f, -5f));
            Vector3 pivot = new(1f, 0f, 1f);
            AgentCameraRig rig = new();
            Assert.IsTrue(rig.Orbit(camera, new CameraOrbitRequest
            {
                TargetX = pivot.x, TargetY = pivot.y, TargetZ = pivot.z, Yaw = 0f, Pitch = 10f, Distance = 6f
            }).Ok);

            CameraRigResult second = rig.Orbit(camera, new CameraOrbitRequest { Yaw = 90f });

            Assert.IsTrue(second.Ok, second.Message);
            AssertOrbitPose(camera, pivot, 90f, 10f, 6f);
        }

        [Test]
        public void Orbit_ManyStepsWithoutATarget_NeverDriftOffThePivot()
        {
            Camera camera = NewCamera(new Vector3(0f, 0f, -5f));
            Vector3 pivot = new(1f, 0f, 1f);
            AgentCameraRig rig = new();
            Assert.IsTrue(rig.Orbit(camera, new CameraOrbitRequest
            {
                TargetX = pivot.x, TargetY = pivot.y, TargetZ = pivot.z, Yaw = 0f, Pitch = 10f, Distance = 6f
            }).Ok);

            // WHY: pitch and distance are re-measured from the camera position on every step, so a rounding error
            // that fed back into the next step would accumulate here; the pivot itself is kept, never re-derived.
            for (int step = 1; step <= 200; step++)
            {
                float yaw = step * 37 % 360;
                CameraRigResult result = rig.Orbit(camera, new CameraOrbitRequest { Yaw = yaw });

                Assert.IsTrue(result.Ok, result.Message);
                Assert.AreEqual(pivot, result.TargetPoint, $"step {step} keeps the remembered pivot");
                AssertOrbitPose(camera, pivot, yaw, 10f, 6f);
            }
        }

        [Test]
        public void Orbit_AfterALookAtAPoint_CirclesThatPoint()
        {
            Camera camera = NewCamera(new Vector3(0f, 0f, -5f));
            Vector3 point = new(2f, 0f, 2f);
            AgentCameraRig rig = new();
            Assert.IsTrue(rig.Look(camera, new CameraLookRequest
            {
                TargetX = point.x, TargetY = point.y, TargetZ = point.z
            }).Ok);

            CameraRigResult orbit = rig.Orbit(camera, new CameraOrbitRequest { Yaw = 180f, Pitch = 15f, Distance = 4f });

            Assert.IsTrue(orbit.Ok, orbit.Message);
            AssertOrbitPose(camera, point, 180f, 15f, 4f);
        }

        [Test]
        public void Orbit_WithoutAnyTargetEver_IsRefused_AndTheCameraStays()
        {
            Camera camera = NewCamera(new Vector3(3f, 4f, 5f));
            Quaternion rotation = camera.transform.rotation;
            AgentCameraRig rig = new();

            CameraRigResult result = rig.Orbit(camera, new CameraOrbitRequest { Yaw = 90f });

            Assert.IsFalse(result.Ok);
            Assert.AreEqual("no_target", result.ErrorCode);
            StringAssert.Contains("target", result.Message);
            Assert.AreEqual(new Vector3(3f, 4f, 5f), camera.transform.position);
            Assert.AreEqual(rotation, camera.transform.rotation);
        }

        [TestCase(120f, 30f, 89f, 30f)]
        [TestCase(-120f, 5000f, -89f, 1000f)]
        [TestCase(20f, 0f, 20f, 0.5f)]
        public void Orbit_ClampsPitchAndDistance(float pitch, float distance, float expectedPitch,
            float expectedDistance)
        {
            Camera camera = NewCamera(new Vector3(0f, 0f, -5f));
            AgentCameraRig rig = new();

            CameraRigResult result = rig.Orbit(camera, new CameraOrbitRequest
            {
                TargetX = 0f, TargetY = 0f, TargetZ = 0f, Yaw = 0f, Pitch = pitch, Distance = distance
            });

            Assert.IsTrue(result.Ok, result.Message);
            Assert.AreEqual(expectedPitch, result.Pitch, Tolerance);
            Assert.AreEqual(expectedDistance, result.Distance, Tolerance);
        }

        [Test]
        public void Orbit_PartialPoint_IsRefused()
        {
            Camera camera = NewCamera(new Vector3(1f, 1f, 1f));
            AgentCameraRig rig = new();

            CameraRigResult result = rig.Orbit(camera, new CameraOrbitRequest { TargetX = 0f, TargetZ = 0f });

            Assert.IsFalse(result.Ok);
            Assert.AreEqual("invalid_target", result.ErrorCode);
            Assert.AreEqual(new Vector3(1f, 1f, 1f), camera.transform.position);
        }

        [Test]
        public void Orbit_MissingObject_IsRefused_AndTheCameraStays()
        {
            Camera camera = NewCamera(new Vector3(1f, 2f, 3f));
            AgentCameraRig rig = new();

            CameraRigResult result = rig.Orbit(camera, new CameraOrbitRequest { Target = "NoSuchObject_P16", Yaw = 10f });

            Assert.IsFalse(result.Ok);
            Assert.AreEqual("target_not_found", result.ErrorCode);
            Assert.AreEqual(new Vector3(1f, 2f, 3f), camera.transform.position);
        }

        [Test]
        public void Orbit_UsesTheInjectedTargetResolver()
        {
            FixedCameraTargetResolver targets = new(new CameraTarget("Castle", new Vector3(7f, 0f, 7f), 2f));
            AgentCameraRig rig = new(AgentCameraOptions.CreateDefault(), targets);
            Camera camera = NewCamera(new Vector3(0f, 0f, 0f));

            CameraRigResult result = rig.Orbit(camera,
                new CameraOrbitRequest { Target = "Castle", Yaw = 180f, Pitch = 0f, Distance = 3f });

            Assert.IsTrue(result.Ok, result.Message);
            CollectionAssert.AreEqual(new[] { "Castle" }, targets.Requested);
            AssertOrbitPose(camera, new Vector3(7f, 0f, 7f), 180f, 0f, 3f);
        }

        [Test]
        public void Look_PointTarget_AimsAtThePointFromTheNewPosition()
        {
            Camera camera = NewCamera(Vector3.zero);
            AgentCameraRig rig = new();

            CameraRigResult result = rig.Look(camera, new CameraLookRequest
            {
                PositionX = 0f, PositionY = 10f, PositionZ = -10f, TargetX = 0f, TargetY = 0f, TargetZ = 0f
            });

            Assert.IsTrue(result.Ok, result.Message);
            Assert.IsTrue(result.HasTarget);
            Assert.AreEqual(new Vector3(0f, 10f, -10f), camera.transform.position);
            CameraAimAssert.LooksAt(camera.transform, Vector3.zero, Tolerance);
        }

        [Test]
        public void Look_PartialPoint_IsRefused_AndTheCameraStays()
        {
            Camera camera = NewCamera(new Vector3(4f, 4f, 4f));
            AgentCameraRig rig = new();

            CameraRigResult result = rig.Look(camera, new CameraLookRequest { PositionX = 1f, TargetY = 0f });

            Assert.IsFalse(result.Ok);
            Assert.AreEqual("invalid_target", result.ErrorCode);
            Assert.AreEqual(new Vector3(4f, 4f, 4f), camera.transform.position);
        }

        [Test]
        public void Look_TargetAtTheCameraPosition_IsRefused()
        {
            Camera camera = NewCamera(new Vector3(2f, 2f, 2f));
            AgentCameraRig rig = new();

            CameraRigResult result = rig.Look(camera,
                new CameraLookRequest { TargetX = 2f, TargetY = 2f, TargetZ = 2f });

            Assert.IsFalse(result.Ok);
            Assert.AreEqual("invalid_target", result.ErrorCode);
        }

        [Test]
        public void Look_ObjectTarget_WinsOverThePoint()
        {
            GameObject target = new("RigLookTarget");
            _created.Add(target);
            target.transform.position = new Vector3(10f, 0f, 0f);
            Camera camera = NewCamera(Vector3.zero);
            AgentCameraRig rig = new();

            CameraRigResult result = rig.Look(camera, new CameraLookRequest
            {
                LookAt = "RigLookTarget", TargetX = -10f, TargetY = 0f, TargetZ = 0f
            });

            Assert.IsTrue(result.Ok, result.Message);
            Assert.AreEqual("RigLookTarget", result.TargetName);
            CameraAimAssert.SameDirection(camera.transform.forward, Vector3.right, Tolerance,
                "the camera looks at the object, not the point");
        }

        [TestCase(5f, 10f)]
        [TestCase(60f, 60f)]
        [TestCase(170f, 120f)]
        public void Look_FieldOfView_IsClampedToTheDefaultRange(float requested, float expected)
        {
            Camera camera = NewCamera(Vector3.zero);
            AgentCameraRig rig = new();

            CameraRigResult result = rig.Look(camera, new CameraLookRequest { FieldOfView = requested });

            Assert.IsTrue(result.Ok, result.Message);
            Assert.AreEqual(expected, camera.fieldOfView, Tolerance);
        }

        [Test]
        public void Look_FieldOfView_FollowsTheConfiguredRange()
        {
            AgentCameraOptions options = AgentCameraOptions.CreateDefault();
            options.MinFieldOfView = 30f;
            options.MaxFieldOfView = 50f;
            Camera camera = NewCamera(Vector3.zero);
            AgentCameraRig rig = new(options, null);

            Assert.IsTrue(rig.Look(camera, new CameraLookRequest { FieldOfView = 90f }).Ok);
            Assert.AreEqual(50f, camera.fieldOfView, Tolerance);
            Assert.IsTrue(rig.Orbit(camera, new CameraOrbitRequest
            {
                TargetX = 0f, TargetY = 0f, TargetZ = 5f, FieldOfView = 10f
            }).Ok);
            Assert.AreEqual(30f, camera.fieldOfView, Tolerance);
        }

        [Test]
        public void Look_NothingToChange_IsRefused()
        {
            Camera camera = NewCamera(Vector3.one);
            AgentCameraRig rig = new();

            CameraRigResult result = rig.Look(camera, new CameraLookRequest());

            Assert.IsFalse(result.Ok);
            Assert.AreEqual("no_change", result.ErrorCode);
        }
    }
}
