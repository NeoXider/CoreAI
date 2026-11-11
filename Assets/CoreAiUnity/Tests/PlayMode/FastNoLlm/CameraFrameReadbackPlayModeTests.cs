using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Vision;
using Cysharp.Threading.Tasks;
using Microsoft.Extensions.AI;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// The two readback paths on a real GPU: <see cref="AsyncGpuCameraFrameReadback"/> and
    /// <see cref="SyncCameraFrameReadback"/> each produce a decodable JPEG/PNG of the requested size, they agree on
    /// orientation and colour (a frame whose top half is blue and bottom half red decodes the same way through both),
    /// and the <c>camera</c> tool orbits its free camera and captures it end to end. Self-skips without a graphics
    /// device, and the async cases skip where the platform has no async readback.
    /// </summary>
    public sealed class CameraFrameReadbackPlayModeTests
    {
        private readonly List<Object> _created = new();

        [TearDown]
        public void TearDown()
        {
            foreach (Object created in _created)
            {
                if (created != null)
                {
                    Object.Destroy(created);
                }
            }

            _created.Clear();
        }

        private static void IgnoreWithoutAGraphicsDevice()
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
            {
                Assert.Ignore("No graphics device (batchmode -nographics): a RenderTexture cannot be created.");
            }
        }

        private static void IgnoreWithoutAsyncReadback()
        {
            if (!SystemInfo.supportsAsyncGPUReadback)
            {
                Assert.Ignore("This device has no AsyncGPUReadback; the sync path covers it.");
            }
        }

        private Camera NewSolidCamera(string name, Color background)
        {
            GameObject go = new(name);
            _created.Add(go);
            Camera camera = go.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = background;
            return camera;
        }

        /// <summary>An orthographic camera that sees a red background with an unlit blue quad over its top half.</summary>
        private Camera NewTopBlueBottomRedScene()
        {
            Camera camera = NewSolidCamera("ReadbackOrientationCam", Color.red);
            camera.orthographic = true;
            camera.orthographicSize = 1f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 10f;
            camera.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

            GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            _created.Add(quad);
            quad.transform.position = new Vector3(0f, 0.5f, 2f);
            quad.transform.localScale = new Vector3(20f, 1f, 1f);
            Shader unlit = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            if (unlit != null)
            {
                Material material = new(unlit) { color = Color.blue };
                _created.Add(material);
                quad.GetComponent<MeshRenderer>().sharedMaterial = material;
            }

            return camera;
        }

        private Texture2D Decode(byte[] bytes)
        {
            Texture2D texture = new(2, 2);
            _created.Add(texture);
            Assert.IsTrue(texture.LoadImage(bytes), "the frame must decode");
            return texture;
        }

        private static Color MeanOfRows(Texture2D texture, int fromRow, int toRow)
        {
            Color sum = Color.clear;
            int count = 0;
            for (int y = fromRow; y < toRow; y++)
            {
                for (int x = 0; x < texture.width; x++)
                {
                    sum += texture.GetPixel(x, y);
                    count++;
                }
            }

            return count == 0 ? Color.clear : sum / count;
        }

        private static void AssertJpeg(byte[] bytes)
        {
            Assert.IsNotNull(bytes);
            Assert.Greater(bytes.Length, 2);
            Assert.AreEqual(0xFF, bytes[0]);
            Assert.AreEqual(0xD8, bytes[1]);
        }

        [UnityTest]
        public IEnumerator AsyncReadback_ProducesAValidJpegOfTheRequestedSize()
        {
            return UniTask.ToCoroutine(async () =>
            {
                IgnoreWithoutAGraphicsDevice();
                IgnoreWithoutAsyncReadback();
                Camera camera = NewSolidCamera("AsyncReadbackCam", Color.magenta);
                AsyncGpuCameraFrameReadback readback = new();

                CameraFrameReadbackResult frame = await readback.CaptureAsync(camera, 128, 72,
                    CaptureImageFormat.Jpeg, 75, CancellationToken.None);

                Assert.AreEqual(CameraReadbackMode.AsyncGpu, frame.Mode, "this device supports async readback");
                AssertJpeg(frame.Bytes);
                Texture2D decoded = Decode(frame.Bytes);
                Assert.AreEqual(128, decoded.width);
                Assert.AreEqual(72, decoded.height);
            });
        }

        [UnityTest]
        public IEnumerator AsyncReadback_Png_IsOpaque()
        {
            return UniTask.ToCoroutine(async () =>
            {
                IgnoreWithoutAGraphicsDevice();
                IgnoreWithoutAsyncReadback();
                Camera camera = NewSolidCamera("AsyncPngCam", new Color(0.2f, 0.4f, 0.6f, 0f));
                AsyncGpuCameraFrameReadback readback = new();

                CameraFrameReadbackResult frame = await readback.CaptureAsync(camera, 32, 32,
                    CaptureImageFormat.Png, 75, CancellationToken.None);

                Assert.AreEqual(0x89, frame.Bytes[0]);
                Assert.AreEqual(0x50, frame.Bytes[1]);
                Texture2D decoded = Decode(frame.Bytes);
                Assert.AreEqual(1f, decoded.GetPixel(16, 16).a, 0.01f,
                    "a camera frame is opaque even when the clear colour has no alpha");
            });
        }

        [UnityTest]
        public IEnumerator SyncReadback_ProducesAValidJpegOfTheRequestedSize()
        {
            return UniTask.ToCoroutine(async () =>
            {
                IgnoreWithoutAGraphicsDevice();
                Camera camera = NewSolidCamera("SyncReadbackCam", Color.cyan);
                SyncCameraFrameReadback readback = new();

                CameraFrameReadbackResult frame = await readback.CaptureAsync(camera, 96, 64,
                    CaptureImageFormat.Jpeg, 75, CancellationToken.None);

                Assert.AreEqual(CameraReadbackMode.Sync, frame.Mode);
                AssertJpeg(frame.Bytes);
                Texture2D decoded = Decode(frame.Bytes);
                Assert.AreEqual(96, decoded.width);
                Assert.AreEqual(64, decoded.height);
            });
        }

        [UnityTest]
        public IEnumerator AsyncAndSync_AgreeOnOrientationAndColour()
        {
            return UniTask.ToCoroutine(async () =>
            {
                IgnoreWithoutAGraphicsDevice();
                IgnoreWithoutAsyncReadback();
                Camera camera = NewTopBlueBottomRedScene();

                CameraFrameReadbackResult fromSync = await new SyncCameraFrameReadback().CaptureAsync(camera, 64, 64,
                    CaptureImageFormat.Jpeg, 90, CancellationToken.None);
                CameraFrameReadbackResult fromAsync = await new AsyncGpuCameraFrameReadback().CaptureAsync(camera, 64,
                    64, CaptureImageFormat.Jpeg, 90, CancellationToken.None);

                Assert.AreEqual(CameraReadbackMode.AsyncGpu, fromAsync.Mode);
                Texture2D syncImage = Decode(fromSync.Bytes);
                Texture2D asyncImage = Decode(fromAsync.Bytes);
                // WHY: Texture2D row 0 is the bottom of the image, so rows 48..64 are the top quarter.
                Color syncTop = MeanOfRows(syncImage, 48, 64);
                Color syncBottom = MeanOfRows(syncImage, 0, 16);
                Color asyncTop = MeanOfRows(asyncImage, 48, 64);
                Color asyncBottom = MeanOfRows(asyncImage, 0, 16);

                Assert.Greater(syncBottom.r, syncBottom.b, $"the sync bottom is the red background: {syncBottom}");
                Assert.Greater(syncTop.b, syncTop.r, $"the sync top is the blue quad: {syncTop}");
                Assert.Greater(asyncBottom.r, asyncBottom.b, $"the async frame is not upside down: {asyncBottom}");
                Assert.Greater(asyncTop.b, asyncTop.r, $"the async frame is not upside down: {asyncTop}");
                foreach ((Color a, Color s, string region) in new[]
                         {
                             (asyncTop, syncTop, "top"), (asyncBottom, syncBottom, "bottom")
                         })
                {
                    Assert.AreEqual(s.r, a.r, 0.08f, $"{region} red: same colour space through both paths");
                    Assert.AreEqual(s.g, a.g, 0.08f, $"{region} green");
                    Assert.AreEqual(s.b, a.b, 0.08f, $"{region} blue");
                }
            });
        }

        [UnityTest]
        public IEnumerator CameraTool_OrbitsTheFreeCamera_ThenCapturesIt_PlayerCameraUntouched()
        {
            return UniTask.ToCoroutine(async () =>
            {
                IgnoreWithoutAGraphicsDevice();
                GameObject player = new("ReadbackPlayerCamera") { tag = "MainCamera" };
                _created.Add(player);
                player.AddComponent<Camera>();
                player.transform.SetPositionAndRotation(new Vector3(0f, 2f, -8f), Quaternion.identity);
                Vector3 playerPosition = player.transform.position;
                GameObject build = GameObject.CreatePrimitive(PrimitiveType.Cube);
                build.name = "ReadbackBlockBuild";
                _created.Add(build);
                AgentCameraOptions options = AgentCameraOptions.CreateDefault();
                options.MinCaptureIntervalSeconds = 0;
                AgentCameraService service = new(options);
                CameraLlmTool tool = new(service, "Programmer");
                try
                {
                    AIFunction orbit = tool.CreateAIFunctions().Single(f => f.Name == "camera_orbit");
                    AIFunction capture = tool.CreateAIFunctions().Single(f => f.Name == "camera_capture");

                    object orbitResult = await orbit.InvokeAsync(new AIFunctionArguments(
                        new Dictionary<string, object>
                        {
                            ["target"] = build.name, ["yaw"] = 45.0, ["pitch"] = 30.0, ["distance"] = 6.0
                        }), CancellationToken.None);
                    await UniTask.SwitchToMainThread();
                    JObject orbitJson = JObject.Parse(orbitResult.ToString());
                    Assert.IsTrue((bool)orbitJson["ok"], orbitJson.ToString());
                    Assert.AreEqual(45f, (float)orbitJson["yaw"], 0.01f);
                    Assert.AreEqual(30f, (float)orbitJson["pitch"], 0.01f);
                    Assert.AreEqual(6f, (float)orbitJson["distance"], 0.01f);

                    object captureResult = await capture.InvokeAsync(new AIFunctionArguments(
                        new Dictionary<string, object> { ["maxSize"] = 256 }), CancellationToken.None);
                    await UniTask.SwitchToMainThread();

                    LlmToolImageResult image = captureResult as LlmToolImageResult;
                    Assert.IsNotNull(image, captureResult?.ToString());
                    byte[] jpeg = image.Images[0].Memory.ToArray();
                    AssertJpeg(jpeg);
                    JObject summary = JObject.Parse(image.Text);
                    Assert.IsTrue(service.TryGetFreeCamera("Programmer", false, out Camera free));
                    Assert.AreEqual(free.name, (string)summary["camera"], "an empty name captures the orbited camera");
                    Assert.IsFalse((bool)summary["isMain"]);
                    Assert.AreEqual(playerPosition, player.transform.position, "the player's camera never moves");
                }
                finally
                {
                    service.Dispose();
                }
            });
        }
    }
}
