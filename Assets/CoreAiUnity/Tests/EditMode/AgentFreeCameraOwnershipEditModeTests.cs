using System.Collections.Generic;
using CoreAI.Vision;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Ownership rules of the free agent camera in <see cref="AgentCameraService"/>: an agent that moves "its own"
    /// camera and has none gets a new, disabled camera created on demand (never the player's camera), while the
    /// player's camera is refused and keeps its pose. Each rule is pinned with its negative twin.
    /// </summary>
    [TestFixture]
    public sealed class AgentFreeCameraOwnershipEditModeTests
    {
        private const string Role = "Programmer";
        private readonly List<GameObject> _created = new();
        private readonly List<AgentCameraService> _services = new();

        [TearDown]
        public void TearDown()
        {
            foreach (AgentCameraService service in _services)
            {
                service.Dispose();
            }

            foreach (GameObject go in _created)
            {
                if (go != null)
                {
                    Object.DestroyImmediate(go);
                }
            }

            _services.Clear();
            _created.Clear();
        }

        private AgentCameraService NewService(AgentCameraOptions options = null)
        {
            AgentCameraService service = new(options ?? AgentCameraOptions.CreateDefault(),
                new FakeCameraFrameReadback(), null, () => 0.0);
            _services.Add(service);
            return service;
        }

        private Camera NewPlayerCamera()
        {
            GameObject go = new("PlayerCamera") { tag = "MainCamera" };
            _created.Add(go);
            go.transform.SetPositionAndRotation(new Vector3(1f, 2f, 3f), Quaternion.Euler(10f, 20f, 0f));
            return go.AddComponent<Camera>();
        }

        private Camera NewMarkedCamera(string name, string role, bool allowMove)
        {
            GameObject go = new(name);
            _created.Add(go);
            Camera camera = go.AddComponent<Camera>();
            go.AddComponent<CoreAiAgentCamera>().SetConfigurationForTests(role, allowMove);
            return camera;
        }

        [Test]
        public void EmptyName_NoMarkedCamera_CreatesAFreeCamera_ThatIsNotThePlayerCamera()
        {
            Camera player = NewPlayerCamera();
            AgentCameraService service = NewService();
            Camera main = Camera.main;

            bool ok = service.TryResolveMovableCamera("", Role, out Camera free, out CameraMoveDenial denial);

            Assert.IsTrue(ok, denial.Message);
            Assert.IsNotNull(free);
            Assert.AreNotSame(player, free, "the free camera is a new camera, never the player's");
            Assert.AreNotSame(Camera.main, free, "the free camera never becomes Camera.main");
            Assert.IsFalse(free.enabled, "disabled, so it never draws to the screen");
            Assert.IsTrue(free.CompareTag("Untagged"));
            Assert.IsTrue(service.IsFreeCamera(free));
            CoreAiAgentCamera marker = free.GetComponent<CoreAiAgentCamera>();
            Assert.IsNotNull(marker);
            Assert.IsTrue(marker.IsMovableBy(Role));
            if (main != null)
            {
                Assert.Less(Vector3.Distance(main.transform.position, free.transform.position), 0.001f,
                    "the free camera starts at the main camera's pose");
                // WHY: Quaternion.Angle reports 0 for anything under about 0.16 degrees, so it cannot hold 0.01.
                CameraAimAssert.SameDirection(free.transform.forward, main.transform.forward, 0.01,
                    "the free camera starts with the main camera's heading");
                CameraAimAssert.SameDirection(free.transform.up, main.transform.up, 0.01,
                    "the free camera starts with the main camera's roll");
            }
        }

        [Test]
        public void MainName_ThePlayerCameraIsRefused_AndNoFreeCameraIsCreated()
        {
            NewPlayerCamera();
            AgentCameraService service = NewService();
            Camera main = Camera.main;
            Assert.IsNotNull(main, "the fixture's player camera is tagged MainCamera");
            Vector3 before = main.transform.position;

            bool ok = service.TryResolveMovableCamera("main", Role, out Camera camera, out CameraMoveDenial denial);

            Assert.IsFalse(ok, "the player's camera must never be movable");
            Assert.IsNull(camera);
            Assert.AreEqual(CameraMoveDenialReason.NotMarked, denial.Reason);
            StringAssert.Contains("'agent'", denial.Message, "the refusal names the next step");
            Assert.IsFalse(service.TryGetFreeCamera(Role, false, out _), "refusing main creates nothing");
            Assert.AreEqual(before, main.transform.position);
        }

        [Test]
        public void FreeCameraSwitchedOff_EmptyName_FallsBackToTheRefusal()
        {
            NewPlayerCamera();
            AgentCameraOptions options = AgentCameraOptions.CreateDefault();
            options.AllowFreeCamera = false;
            AgentCameraService service = NewService(options);

            bool ok = service.TryResolveMovableCamera("", Role, out Camera camera, out CameraMoveDenial denial);

            Assert.IsFalse(ok);
            Assert.IsNull(camera);
            Assert.AreEqual(CameraMoveDenialReason.NotMarked, denial.Reason);
            Assert.IsFalse(service.TryGetFreeCamera(Role, true, out _), "no free camera when the game turned it off");
        }

        [Test]
        public void FreeCameraSwitchedOff_FreeName_IsRefusedAsDisabled()
        {
            AgentCameraOptions options = AgentCameraOptions.CreateDefault();
            options.AllowFreeCamera = false;
            AgentCameraService service = NewService(options);

            bool ok = service.TryResolveMovableCamera("free", Role, out _, out CameraMoveDenial denial);

            Assert.IsFalse(ok);
            Assert.AreEqual(CameraMoveDenialReason.FreeCameraDisabled, denial.Reason);
        }

        [Test]
        public void FreeCamera_IsPerRole_AndAnotherRoleCannotMoveIt()
        {
            AgentCameraService service = NewService();
            Assert.IsTrue(service.TryResolveMovableCamera("", "Programmer", out Camera programmerCam, out _));
            Assert.IsTrue(service.TryResolveMovableCamera("", "Creator", out Camera creatorCam, out _));

            bool ok = service.TryResolveMovableCamera(programmerCam.name, "Creator", out _,
                out CameraMoveDenial denial);

            Assert.AreNotSame(programmerCam, creatorCam, "each role gets its own free camera");
            Assert.IsFalse(ok, "one role may not take another role's free camera");
            Assert.AreEqual(CameraMoveDenialReason.WrongRole, denial.Reason);
        }

        [Test]
        public void SecondRequest_ReusesTheSameFreeCamera()
        {
            AgentCameraService service = NewService();

            Assert.IsTrue(service.TryResolveMovableCamera("", Role, out Camera first, out _));
            Assert.IsTrue(service.TryResolveMovableCamera("agent", Role, out Camera second, out _));
            Assert.IsTrue(service.TryResolveMovableCamera("free", Role, out Camera third, out _));

            Assert.AreSame(first, second);
            Assert.AreSame(first, third);
        }

        [Test]
        public void DesignerMarkedCamera_WinsOverTheFreeCamera()
        {
            Camera drone = NewMarkedCamera("DroneCam", Role, true);
            AgentCameraService service = NewService();

            bool ok = service.TryResolveMovableCamera("", Role, out Camera camera, out _);

            Assert.IsTrue(ok);
            Assert.AreSame(drone, camera);
            Assert.IsFalse(service.TryGetFreeCamera(Role, false, out _), "a marked camera needs no free camera");
        }

        [Test]
        public void CaptureOnlyMarkedCamera_EmptyNameIsRefused_FreeNameMovesAFreeCamera()
        {
            Camera captureOnly = NewMarkedCamera("FixedCam", Role, false);
            AgentCameraService service = NewService();

            bool emptyOk = service.TryResolveMovableCamera("", Role, out _, out CameraMoveDenial denial);
            bool freeOk = service.TryResolveMovableCamera("free", Role, out Camera free, out _);

            Assert.IsFalse(emptyOk, "the designer made the agent's camera capture-only");
            Assert.AreEqual(CameraMoveDenialReason.MovementDisabled, denial.Reason);
            StringAssert.Contains("'free'", denial.Message);
            Assert.IsTrue(freeOk);
            Assert.AreNotSame(captureOnly, free);
        }

        [Test]
        public void CaptureResolution_EmptyName_UsesTheMainCameraUntilTheAgentHasAFreeCamera()
        {
            NewPlayerCamera();
            AgentCameraService service = NewService();
            Camera main = Camera.main;

            Camera before = service.ResolveCaptureCamera("", Role, out bool beforeIsMain);
            Assert.IsTrue(service.TryResolveMovableCamera("", Role, out Camera free, out _));
            Camera after = service.ResolveCaptureCamera("", Role, out bool afterIsMain);

            Assert.AreSame(main, before, "capturing alone never creates a camera");
            Assert.IsTrue(beforeIsMain);
            Assert.AreSame(free, after, "once the agent moved its own camera, an empty name captures it");
            Assert.IsFalse(afterIsMain);
        }

        [Test]
        public void CaptureResolution_AgentName_CreatesTheFreeCamera()
        {
            NewPlayerCamera();
            AgentCameraService service = NewService();

            Camera agent = service.ResolveCaptureCamera("agent", Role, out bool isMain);

            Assert.IsNotNull(agent);
            Assert.IsTrue(service.IsFreeCamera(agent));
            Assert.IsFalse(isMain);
        }

        [Test]
        public void CaptureResolution_MainName_NeverResolvesTheFreeCamera()
        {
            NewPlayerCamera();
            AgentCameraService service = NewService();
            Assert.IsTrue(service.TryResolveMovableCamera("", Role, out Camera free, out _));

            Camera resolved = service.ResolveCaptureCamera("main", Role, out bool isMain);

            Assert.AreNotSame(free, resolved);
            Assert.IsTrue(isMain);
        }

        [Test]
        public void ListCameras_ReportsTheFreeCameraAsFreeAndMovable()
        {
            NewPlayerCamera();
            AgentCameraService service = NewService();
            Assert.IsTrue(service.TryResolveMovableCamera("", Role, out Camera free, out _));

            IReadOnlyList<AgentCameraInfo> cameras = service.ListCameras(Role);

            AgentCameraInfo freeInfo = null;
            AgentCameraInfo playerInfo = null;
            foreach (AgentCameraInfo info in cameras)
            {
                if (info.Name == free.name)
                {
                    freeInfo = info;
                }
                else if (info.Name == "PlayerCamera")
                {
                    playerInfo = info;
                }
            }

            Assert.IsNotNull(freeInfo, "the free camera is listed");
            Assert.IsTrue(freeInfo.IsFree);
            Assert.IsTrue(freeInfo.Movable);
            Assert.IsNotNull(playerInfo);
            Assert.IsFalse(playerInfo.IsFree);
            Assert.IsFalse(playerInfo.Movable);
        }

        [Test]
        public void Dispose_DestroysTheFreeCameras()
        {
            AgentCameraService service = new(AgentCameraOptions.CreateDefault(), new FakeCameraFrameReadback(), null,
                () => 0.0);
            Assert.IsTrue(service.TryResolveMovableCamera("", Role, out Camera free, out _));

            service.Dispose();

            Assert.IsTrue(free == null, "the free camera's GameObject is destroyed with the service");
            Assert.IsFalse(service.TryGetFreeCamera(Role, false, out _));
        }
    }
}
