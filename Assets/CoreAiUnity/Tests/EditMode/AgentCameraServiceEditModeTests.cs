using System.Collections.Generic;
using CoreAI.Vision;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode coverage for <see cref="AgentCameraService"/> logic that needs no live render pipeline:
    /// the ownership/movement gate (player cameras protected by default), the per-agent capture rate
    /// limit (deterministic via an injected clock), camera listing, and look application. The render path
    /// itself is covered by <c>AgentCameraCapturePlayModeTests</c>.
    /// </summary>
    public sealed class AgentCameraServiceEditModeTests
    {
        private const string Role = "Programmer";
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

        private Camera NewCamera(string name)
        {
            GameObject go = new(name);
            _created.Add(go);
            return go.AddComponent<Camera>();
        }

        private GameObject NewGameObject(string name)
        {
            GameObject go = new(name);
            _created.Add(go);
            return go;
        }

        private static CoreAiAgentCamera Mark(Camera cam, string role, bool allowMove)
        {
            CoreAiAgentCamera marker = cam.gameObject.AddComponent<CoreAiAgentCamera>();
            marker.SetConfigurationForTests(role, allowMove);
            return marker;
        }

        [Test]
        public void TryResolveMovableCamera_UnmarkedCamera_DeniedAsNotMarked()
        {
            NewCamera("PlayerCam");
            var service = new AgentCameraService();

            bool ok = service.TryResolveMovableCamera("PlayerCam", Role, out Camera cam, out CameraMoveDenial denial);

            Assert.IsFalse(ok, "An unmarked camera must not be movable (player camera protection).");
            Assert.IsNull(cam);
            Assert.AreEqual(CameraMoveDenialReason.NotMarked, denial.Reason);
            StringAssert.Contains("CoreAiAgentCamera", denial.Message);
        }

        [Test]
        public void TryResolveMovableCamera_MarkedAndAllowMove_Permitted()
        {
            Camera drone = NewCamera("DroneCam");
            Mark(drone, role: "", allowMove: true);
            var service = new AgentCameraService();

            bool ok = service.TryResolveMovableCamera("DroneCam", Role, out Camera cam, out CameraMoveDenial denial);

            Assert.IsTrue(ok);
            Assert.AreSame(drone, cam);
            Assert.AreEqual(CameraMoveDenialReason.None, denial.Reason);
        }

        [Test]
        public void TryResolveMovableCamera_AllowMoveFalse_DeniedAsMovementDisabled()
        {
            Camera cam = NewCamera("CaptureOnlyCam");
            Mark(cam, role: "", allowMove: false);
            var service = new AgentCameraService();

            bool ok = service.TryResolveMovableCamera("CaptureOnlyCam", Role, out _, out CameraMoveDenial denial);

            Assert.IsFalse(ok);
            Assert.AreEqual(CameraMoveDenialReason.MovementDisabled, denial.Reason);
        }

        [Test]
        public void TryResolveMovableCamera_WrongRole_Denied()
        {
            Camera cam = NewCamera("AnalyzerCam");
            Mark(cam, role: "Analyzer", allowMove: true);
            var service = new AgentCameraService();

            bool ok = service.TryResolveMovableCamera("AnalyzerCam", Role, out _, out CameraMoveDenial denial);

            Assert.IsFalse(ok);
            Assert.AreEqual(CameraMoveDenialReason.WrongRole, denial.Reason);
        }

        [Test]
        public void TryReserveCapture_RateLimitsPerAgent_UsingInjectedClock()
        {
            double now = 100.0;
            var service = new AgentCameraService(() => now, minCaptureIntervalSeconds: 1.0);

            Assert.IsTrue(service.TryReserveCapture(Role, out _), "First capture is allowed.");

            bool second = service.TryReserveCapture(Role, out double retryAfter);
            Assert.IsFalse(second, "A second capture within the interval is rate limited.");
            Assert.Greater(retryAfter, 0.0);

            now += 1.5; // advance past the interval
            Assert.IsTrue(service.TryReserveCapture(Role, out _), "Capture allowed again after the interval.");
        }

        [Test]
        public void TryReserveCapture_IndependentPerRole()
        {
            double now = 0.0;
            var service = new AgentCameraService(() => now, minCaptureIntervalSeconds: 1.0);

            Assert.IsTrue(service.TryReserveCapture("Programmer", out _));
            Assert.IsTrue(service.TryReserveCapture("Analyzer", out _),
                "A different agent role has its own rate-limit budget.");
            Assert.IsFalse(service.TryReserveCapture("Programmer", out _));
        }

        [Test]
        public void ListCameras_ReportsMarkedAndMovableFlags()
        {
            NewCamera("PlainCam");
            Camera drone = NewCamera("DroneCam");
            Mark(drone, role: "", allowMove: true);
            var service = new AgentCameraService();

            IReadOnlyList<AgentCameraInfo> cameras = service.ListCameras(Role);

            AgentCameraInfo plain = Find(cameras, "PlainCam");
            AgentCameraInfo movable = Find(cameras, "DroneCam");
            Assert.IsNotNull(plain);
            Assert.IsFalse(plain.IsMarked);
            Assert.IsFalse(plain.Movable);
            Assert.IsNotNull(movable);
            Assert.IsTrue(movable.IsMarked);
            Assert.IsTrue(movable.Movable);
        }

        [Test]
        public void ResolveCaptureCamera_EmptyName_PrefersOwnMarkedCamera()
        {
            NewCamera("PlainCam");
            Camera drone = NewCamera("OwnedCam");
            Mark(drone, role: Role, allowMove: true);
            var service = new AgentCameraService();

            Camera resolved = service.ResolveCaptureCamera("", Role, out bool isMain);

            Assert.AreSame(drone, resolved, "Empty cameraName should default to the agent's own camera.");
            Assert.IsFalse(isMain);
        }

        [Test]
        public void ResolveCaptureCamera_ExplicitName_ResolvesThatCamera()
        {
            Camera target = NewCamera("SideCam");
            var service = new AgentCameraService();

            Camera resolved = service.ResolveCaptureCamera("SideCam", Role, out _);

            Assert.AreSame(target, resolved);
        }

        [Test]
        public void ResolveCaptureCamera_ExplicitInactiveName_DoesNotResolveInactiveCamera()
        {
            Camera inactive = NewCamera("InactiveCaptureCam");
            inactive.gameObject.SetActive(false);
            var service = new AgentCameraService();

            Camera resolved = service.ResolveCaptureCamera("InactiveCaptureCam", Role, out _);

            Assert.AreNotSame(inactive, resolved);
        }

        [Test]
        public void TryResolveMovableCamera_ExplicitInactiveName_DeniedAsNoCamera()
        {
            Camera inactive = NewCamera("InactiveMovableCam");
            Mark(inactive, role: "", allowMove: true);
            inactive.gameObject.SetActive(false);
            var service = new AgentCameraService();

            bool ok = service.TryResolveMovableCamera(
                "InactiveMovableCam", Role, out Camera resolved, out CameraMoveDenial denial);

            Assert.IsFalse(ok);
            Assert.IsNull(resolved);
            Assert.AreEqual(CameraMoveDenialReason.NoCamera, denial.Reason);
        }

        [Test]
        public void TryApplyLook_SetsPosition_AndRejectsNoChange()
        {
            Camera cam = NewCamera("MoveCam");
            var service = new AgentCameraService();

            bool applied = service.TryApplyLook(cam, 1f, 2f, 3f, null, null, null, null, out string err);
            Assert.IsTrue(applied);
            Assert.IsNull(err);
            Assert.AreEqual(new Vector3(1f, 2f, 3f), cam.transform.position);

            bool noChange = service.TryApplyLook(cam, null, null, null, null, null, null, null, out string err2);
            Assert.IsFalse(noChange);
            Assert.AreEqual("no_change", err2);
        }

        [Test]
        public void TryApplyLook_ActiveTarget_RotatesFromFinalPosition()
        {
            Camera cam = NewCamera("LookCamera");
            GameObject target = NewGameObject("LookTarget");
            target.transform.position = new Vector3(4f, 2f, 8f);
            var service = new AgentCameraService();

            bool applied = service.TryApplyLook(
                cam, 1f, 2f, 3f, null, null, null, target.name, out string error);

            Assert.IsTrue(applied);
            Assert.IsNull(error);
            Assert.AreEqual(new Vector3(1f, 2f, 3f), cam.transform.position);
            Vector3 direction = (target.transform.position - cam.transform.position).normalized;
            Assert.Greater(Vector3.Dot(cam.transform.forward, direction), 0.999f);
        }

        [Test]
        public void TryApplyLook_MissingTarget_RestoresOriginalPosition()
        {
            Camera cam = NewCamera("RestoreCamera");
            Vector3 originalPosition = new(9f, 8f, 7f);
            cam.transform.position = originalPosition;
            var service = new AgentCameraService();

            bool applied = service.TryApplyLook(
                cam, 1f, 2f, 3f, null, null, null, "MissingLookTarget", out string error);

            Assert.IsFalse(applied);
            Assert.AreEqual("target_not_found", error);
            Assert.AreEqual(originalPosition, cam.transform.position);
        }

        private static AgentCameraInfo Find(IReadOnlyList<AgentCameraInfo> cameras, string name)
        {
            foreach (AgentCameraInfo info in cameras)
            {
                if (info.Name == name)
                {
                    return info;
                }
            }

            return null;
        }
    }
}
