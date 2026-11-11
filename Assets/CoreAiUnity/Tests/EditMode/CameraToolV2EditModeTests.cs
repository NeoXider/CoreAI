using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Vision;
using Microsoft.Extensions.AI;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using WorldCameraTool = CoreAI.Infrastructure.World.CameraLlmTool;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// The <c>camera</c> tool v2 through its MEAI functions: the action set and schemas, the description limit, the
    /// unchanged capture summary shape, the deprecated <c>capture_camera</c> alias forwarding to the same readback,
    /// and the replace-with-fake seams (<see cref="ICameraFrameReadback"/>, <see cref="IAgentCameraRig"/>). Captures
    /// use <see cref="FakeCameraFrameReadback"/>, so no render pipeline is needed; the GPU paths are PlayMode tests.
    /// </summary>
    [TestFixture]
    public sealed class CameraToolV2EditModeTests
    {
        private const string Role = "Programmer";
        private const float Tolerance = 0.01f;
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
                    UnityEngine.Object.DestroyImmediate(go);
                }
            }

            _services.Clear();
            _created.Clear();
        }

        private AgentCameraService NewService(FakeCameraFrameReadback readback, AgentCameraOptions options = null)
        {
            AgentCameraService service = new(options ?? AgentCameraOptions.CreateDefault(), readback, null, () => 0.0);
            _services.Add(service);
            return service;
        }

        private Camera NewPlayerCamera()
        {
            GameObject go = new("PlayerCameraV2") { tag = "MainCamera" };
            _created.Add(go);
            go.transform.SetPositionAndRotation(new Vector3(4f, 3f, -6f), Quaternion.Euler(15f, 30f, 0f));
            return go.AddComponent<Camera>();
        }

        private Camera NewCamera(string name)
        {
            GameObject go = new(name);
            _created.Add(go);
            return go.AddComponent<Camera>();
        }

        private static AIFunction Function(IAIFunctionsLlmTool tool, string name)
        {
            AIFunction function = tool.CreateAIFunctions().SingleOrDefault(f => f.Name == name);
            Assert.IsNotNull(function, $"function '{name}' must exist");
            return function;
        }

        private static async Task<object> Invoke(IAIFunctionsLlmTool tool, string name,
            Dictionary<string, object> arguments)
        {
            return await Function(tool, name).InvokeAsync(new AIFunctionArguments(arguments), CancellationToken.None);
        }

        private static async Task<JObject> InvokeJson(IAIFunctionsLlmTool tool, string name,
            Dictionary<string, object> arguments)
        {
            object result = await Invoke(tool, name, arguments);
            return JObject.Parse(result.ToString());
        }

        [Test]
        public void CreateAIFunctions_ExposesCaptureItsAliasLookOrbitAndList()
        {
            CameraLlmTool tool = new(new AgentCameraService(), Role);

            string[] names = tool.CreateAIFunctions().Select(f => f.Name).ToArray();

            Assert.AreEqual("camera", tool.Name);
            CollectionAssert.AreEquivalent(
                new[] { "camera_capture", "screenshot", "camera_look", "camera_orbit", "camera_list" }, names);
        }

        [Test]
        public void EveryCameraToolDescription_FitsTheToolContractLimit()
        {
            int limit = AiToolContractPromptFormatter.ToolDescriptionMaxChars;
            IAIFunctionsLlmTool[] tools = { new CameraLlmTool(new AgentCameraService(), Role), new WorldCameraTool() };

            foreach (IAIFunctionsLlmTool tool in tools)
            {
                Assert.LessOrEqual(tool.Description.Length, limit, tool.Name);
                foreach (AIFunction function in tool.CreateAIFunctions())
                {
                    Assert.LessOrEqual(function.Description.Length, limit, function.Name);
                }
            }
        }

        [Test]
        public void CameraLook_Schema_KeepsTheOldParameters_AndAddsAPointTargetAndFov()
        {
            string schema = Function(new CameraLlmTool(new AgentCameraService(), Role), "camera_look")
                .JsonSchema.ToString();

            foreach (string parameter in new[]
                     {
                         "cameraName", "posX", "posY", "posZ", "rotX", "rotY", "rotZ", "lookAt", "targetX", "targetY",
                         "targetZ", "fov"
                     })
            {
                StringAssert.Contains("\"" + parameter + "\"", schema, parameter);
            }
        }

        [Test]
        public void CameraOrbit_Schema_HasTargetYawPitchDistanceAndFov()
        {
            string schema = Function(new CameraLlmTool(new AgentCameraService(), Role), "camera_orbit")
                .JsonSchema.ToString();

            foreach (string parameter in new[]
                     {
                         "cameraName", "target", "targetX", "targetY", "targetZ", "yaw", "pitch", "distance", "fov"
                     })
            {
                StringAssert.Contains("\"" + parameter + "\"", schema, parameter);
            }
        }

        [Test]
        public void CameraCapture_Schema_IsUnchanged()
        {
            CameraLlmTool tool = new(new AgentCameraService(), Role);

            foreach (string name in new[] { "camera_capture", "screenshot" })
            {
                string schema = Function(tool, name).JsonSchema.ToString();
                StringAssert.Contains("\"cameraName\"", schema, name);
                StringAssert.Contains("\"maxSize\"", schema, name);
            }
        }

        [Test]
        public void WorldCameraTool_KeepsItsNameFunctionAndParameters()
        {
            WorldCameraTool tool = new();

            AIFunction capture = Function(tool, "capture_camera");
            string schema = capture.JsonSchema.ToString();

            Assert.AreEqual("camera_tool", tool.Name);
            Assert.AreEqual(1, tool.CreateAIFunctions().Count());
            StringAssert.Contains("\"cameraName\"", schema);
            StringAssert.Contains("\"width\"", schema);
            StringAssert.Contains("\"height\"", schema);
        }

        [Test]
        public async Task CameraCapture_WithAFakeReadback_ReturnsTheImage_AndTheSameSummaryShape()
        {
            Camera camera = NewCamera("CaptureV2Cam");
            FakeCameraFrameReadback readback = new();
            CameraLlmTool tool = new(NewService(readback), Role);

            object result = await Invoke(tool, "camera_capture",
                new Dictionary<string, object> { ["cameraName"] = camera.name, ["maxSize"] = 256 });

            LlmToolImageResult image = result as LlmToolImageResult;
            Assert.IsNotNull(image, $"capture returns an image result, got: {result}");
            Assert.AreEqual(1, readback.Calls);
            Assert.AreSame(camera, readback.LastCamera);
            Assert.AreEqual(256, Mathf.Max(readback.LastWidth, readback.LastHeight), "long edge = maxSize");
            Assert.AreEqual(1, image.Images.Count);
            Assert.AreEqual("image/jpeg", image.Images[0].ResolvedMediaType);
            CollectionAssert.AreEqual(FakeCameraFrameReadback.JpegBytes, image.Images[0].Memory.ToArray());

            JObject summary = JObject.Parse(image.Text);
            foreach (string key in new[]
                     {
                         "ok", "summary", "camera", "isMain", "width", "height", "format", "sizeBytes", "pose",
                         "imageAttached"
                     })
            {
                Assert.IsNotNull(summary[key], $"summary keeps '{key}'");
            }

            Assert.IsTrue((bool)summary["ok"]);
            Assert.IsTrue((bool)summary["imageAttached"]);
            Assert.AreEqual(camera.name, (string)summary["camera"]);
            Assert.AreEqual("jpg", (string)summary["format"]);
            Assert.AreEqual(FakeCameraFrameReadback.JpegBytes.Length, (int)summary["sizeBytes"]);
            Assert.IsNotNull(summary["pose"]["position"]);
            Assert.IsNotNull(summary["pose"]["fieldOfView"]);
            StringAssert.DoesNotContain("base64", image.Text, "no image bytes inside the tool message");
        }

        [Test]
        public async Task CameraCapture_ReadbackFails_ReturnsCaptureFailed_WithoutAnImage()
        {
            Camera camera = NewCamera("CaptureFailCam");
            FakeCameraFrameReadback readback = new() { Failure = new InvalidOperationException("gpu lost") };
            CameraLlmTool tool = new(NewService(readback), Role);

            object result = await Invoke(tool, "camera_capture",
                new Dictionary<string, object> { ["cameraName"] = camera.name });

            Assert.IsNotInstanceOf<LlmToolImageResult>(result);
            JObject error = JObject.Parse(result.ToString());
            Assert.IsFalse((bool)error["ok"]);
            Assert.AreEqual("capture_failed", (string)error["error"]);
            StringAssert.Contains("gpu lost", (string)error["message"]);
        }

        [Test]
        public async Task WorldCaptureCamera_ForwardsToTheSharedReadback_AndKeepsItsSummaryShape()
        {
            Camera camera = NewCamera("WorldAliasCam");
            FakeCameraFrameReadback readback = new();
            WorldCameraTool tool = new(readback);

            object result = await Invoke(tool, "capture_camera", new Dictionary<string, object>
            {
                ["cameraName"] = camera.name, ["width"] = 2000, ["height"] = 10
            });

            LlmToolImageResult image = result as LlmToolImageResult;
            Assert.IsNotNull(image, $"capture_camera returns an image result, got: {result}");
            Assert.AreEqual(1, readback.Calls, "the alias captures through the same readback seam");
            Assert.AreSame(camera, readback.LastCamera);
            Assert.AreEqual(1024, readback.LastWidth, "width clamped to 1024");
            Assert.AreEqual(64, readback.LastHeight, "height clamped to 64");
            JObject summary = JObject.Parse(image.Text);
            Assert.IsTrue((bool)summary["success"]);
            Assert.AreEqual("1024x64", (string)summary["resolution"]);
            Assert.AreEqual(camera.name, (string)summary["camera"]);
            Assert.AreEqual("jpeg", (string)summary["format"]);
            Assert.AreEqual(FakeCameraFrameReadback.JpegBytes.Length, (int)summary["sizeBytes"]);
            Assert.IsTrue((bool)summary["imageAttached"]);
        }

        [Test]
        public async Task CameraOrbit_UsesTheInjectedRig()
        {
            FakeCameraFrameReadback readback = new();
            RecordingCameraRig rig = new();
            CameraLlmTool tool = new(NewService(readback), Role, rig);

            JObject json = await InvokeJson(tool, "camera_orbit", new Dictionary<string, object>
            {
                ["target"] = "Castle", ["yaw"] = 45.0, ["pitch"] = 20.0, ["distance"] = 12.0
            });

            Assert.IsTrue((bool)json["ok"], json.ToString());
            Assert.AreEqual(1, rig.Orbits.Count);
            Assert.AreEqual("Castle", rig.Orbits[0].Target);
            Assert.AreEqual(45f, rig.Orbits[0].Yaw.Value, Tolerance);
            Assert.AreEqual(20f, rig.Orbits[0].Pitch.Value, Tolerance);
            Assert.AreEqual(12f, rig.Orbits[0].Distance.Value, Tolerance);
            Assert.AreEqual(45f, (float)json["yaw"], Tolerance);
        }

        [Test]
        public async Task CameraLook_PassesThePointTargetAndFovToTheRig()
        {
            RecordingCameraRig rig = new();
            CameraLlmTool tool = new(NewService(new FakeCameraFrameReadback()), Role, rig);

            JObject json = await InvokeJson(tool, "camera_look", new Dictionary<string, object>
            {
                ["targetX"] = 1.0, ["targetY"] = 2.0, ["targetZ"] = 3.0, ["fov"] = 40.0
            });

            Assert.IsTrue((bool)json["ok"], json.ToString());
            Assert.AreEqual(1, rig.Looks.Count);
            Assert.AreEqual(1f, rig.Looks[0].TargetX.Value, Tolerance);
            Assert.AreEqual(2f, rig.Looks[0].TargetY.Value, Tolerance);
            Assert.AreEqual(3f, rig.Looks[0].TargetZ.Value, Tolerance);
            Assert.AreEqual(40f, rig.Looks[0].FieldOfView.Value, Tolerance);
        }

        [Test]
        public async Task CameraOrbit_ThroughTheTool_MovesTheFreeCamera_PoseWithinTolerance_PlayerCameraUntouched()
        {
            Camera player = NewPlayerCamera();
            Vector3 playerPosition = player.transform.position;
            Quaternion playerRotation = player.transform.rotation;
            AgentCameraService service = NewService(new FakeCameraFrameReadback());
            CameraLlmTool tool = new(service, Role);

            JObject json = await InvokeJson(tool, "camera_orbit", new Dictionary<string, object>
            {
                ["targetX"] = 0.0, ["targetY"] = 1.0, ["targetZ"] = 0.0, ["yaw"] = 30.0, ["pitch"] = 20.0,
                ["distance"] = 10.0
            });

            Assert.IsTrue((bool)json["ok"], json.ToString());
            Assert.AreEqual(30f, (float)json["yaw"], Tolerance);
            Assert.AreEqual(20f, (float)json["pitch"], Tolerance);
            Assert.AreEqual(10f, (float)json["distance"], Tolerance);
            Assert.IsTrue(service.TryGetFreeCamera(Role, false, out Camera free), "the orbit created the free camera");
            Assert.AreEqual(free.name, (string)json["camera"]);
            Vector3 pivot = new(0f, 1f, 0f);
            Assert.AreEqual(10f, Vector3.Distance(pivot, free.transform.position), Tolerance);
            CameraAimAssert.LooksAt(free.transform, pivot, Tolerance, "the free camera looks at the pivot");
            Assert.AreEqual(playerPosition, player.transform.position, "the player's camera never moves");
            Assert.AreEqual(playerRotation, player.transform.rotation);
        }

        [Test]
        public async Task CameraLook_OnMain_IsRefused_AndThePlayerCameraDoesNotMove()
        {
            NewPlayerCamera();
            Camera main = Camera.main;
            Assert.IsNotNull(main);
            Vector3 before = main.transform.position;
            CameraLlmTool tool = new(NewService(new FakeCameraFrameReadback()), Role);

            JObject look = await InvokeJson(tool, "camera_look", new Dictionary<string, object>
            {
                ["cameraName"] = "main", ["posX"] = 100.0
            });
            JObject orbit = await InvokeJson(tool, "camera_orbit", new Dictionary<string, object>
            {
                ["cameraName"] = "main", ["targetX"] = 0.0, ["targetY"] = 0.0, ["targetZ"] = 0.0, ["yaw"] = 90.0
            });

            Assert.IsFalse((bool)look["ok"]);
            Assert.AreEqual("camera_not_movable", (string)look["error"]);
            Assert.IsFalse((bool)orbit["ok"]);
            Assert.AreEqual("camera_not_movable", (string)orbit["error"]);
            Assert.AreEqual(before, main.transform.position, "the player's camera never moves");
        }

        [Test]
        public async Task CameraLook_EmptyName_MovesTheFreeCamera_NotThePlayerCamera()
        {
            NewPlayerCamera();
            Camera main = Camera.main;
            Vector3 before = main.transform.position;
            AgentCameraService service = NewService(new FakeCameraFrameReadback());
            CameraLlmTool tool = new(service, Role);

            JObject look = await InvokeJson(tool, "camera_look", new Dictionary<string, object>
            {
                ["posX"] = 100.0, ["posY"] = 5.0, ["posZ"] = 0.0
            });

            Assert.IsTrue((bool)look["ok"], look.ToString());
            Assert.IsTrue(service.TryGetFreeCamera(Role, false, out Camera free));
            Assert.AreEqual(new Vector3(100f, 5f, 0f), free.transform.position);
            Assert.AreEqual(before, main.transform.position);
            StringAssert.Contains("camera_capture", (string)look["summary"], "the result names the next step");
        }

        [Test]
        public async Task CameraCapture_AfterALook_CapturesTheFreeCamera()
        {
            NewPlayerCamera();
            FakeCameraFrameReadback readback = new();
            AgentCameraService service = NewService(readback);
            CameraLlmTool tool = new(service, Role);
            await InvokeJson(tool, "camera_look", new Dictionary<string, object> { ["posY"] = 20.0 });

            object result = await Invoke(tool, "camera_capture", new Dictionary<string, object>());

            Assert.IsInstanceOf<LlmToolImageResult>(result, result?.ToString());
            Assert.IsTrue(service.TryGetFreeCamera(Role, false, out Camera free));
            Assert.AreSame(free, readback.LastCamera, "an empty name captures the camera the agent just moved");
        }

        [Test]
        public async Task CameraList_ReportsTheFreeFlag()
        {
            NewPlayerCamera();
            AgentCameraService service = NewService(new FakeCameraFrameReadback());
            CameraLlmTool tool = new(service, Role);
            await InvokeJson(tool, "camera_look", new Dictionary<string, object> { ["fov"] = 45.0 });

            JObject json = await InvokeJson(tool, "camera_list", new Dictionary<string, object>());

            Assert.IsTrue((bool)json["ok"]);
            JToken free = json["cameras"].FirstOrDefault(c => (bool)c["free"]);
            Assert.IsNotNull(free, json.ToString());
            Assert.IsTrue((bool)free["movable"]);
            Assert.AreEqual(45f, (float)free["pose"]["fieldOfView"], Tolerance);
        }

        [Test]
        public async Task CameraTool_WithoutARig_SharesTheServiceRules()
        {
            AgentCameraOptions options = AgentCameraOptions.CreateDefault();
            options.MaxFieldOfView = 50f;
            AgentCameraService service = NewService(new FakeCameraFrameReadback(), options);
            CameraLlmTool tool = new(service, Role);

            JObject json = await InvokeJson(tool, "camera_look", new Dictionary<string, object> { ["fov"] = 100.0 });

            Assert.IsTrue((bool)json["ok"], json.ToString());
            Assert.IsTrue(service.TryGetFreeCamera(Role, false, out Camera free));
            Assert.AreEqual(50f, free.fieldOfView, Tolerance, "the tool used the service's rig and its options");
        }

        [Test]
        public async Task AsyncGpuReadback_WhenThePlatformCannot_UsesTheFallback()
        {
            Camera camera = NewCamera("FallbackCam");
            FakeCameraFrameReadback fallback = new() { Mode = CameraReadbackMode.Sync };
            AsyncGpuCameraFrameReadback readback = new(fallback, () => false, 5);

            CameraFrameReadbackResult frame =
                await readback.CaptureAsync(camera, 64, 48, CaptureImageFormat.Jpeg, 75, CancellationToken.None);

            Assert.AreEqual(1, fallback.Calls);
            Assert.AreSame(camera, fallback.LastCamera);
            Assert.AreEqual(64, fallback.LastWidth);
            Assert.AreEqual(48, fallback.LastHeight);
            Assert.AreEqual(CameraReadbackMode.Sync, frame.Mode);
        }

        [Test]
        public void CameraFrameReadback_Default_IsTheAsyncPath()
        {
            Assert.IsInstanceOf<AsyncGpuCameraFrameReadback>(CameraFrameReadback.CreateDefault());
        }
    }
}
