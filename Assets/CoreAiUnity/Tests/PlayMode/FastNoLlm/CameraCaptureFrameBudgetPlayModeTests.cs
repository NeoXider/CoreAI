using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using CoreAI.Vision;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// Timing evidence for the P16 readback (DoD: "with async readback on, a capture adds no frame over the budget").
    /// Captures a busy scene repeatedly through <see cref="AsyncGpuCameraFrameReadback"/> and then through
    /// <see cref="SyncCameraFrameReadback"/>, logs the main-thread cost of each capture and the longest frame seen
    /// while capturing, and fails when an async capture blocks the main thread longer than the budget.
    /// <para>
    /// Explicit because timing depends on the machine: run it by name on a GPU machine, and once in a built player
    /// (Test Runner: Run in player) for the recorded run. The budget is <c>COREAI_CAMERA_CAPTURE_BUDGET_MS</c>
    /// (default 16.7 ms, one frame at 60 fps); the capture size is <c>COREAI_CAMERA_CAPTURE_SIZE</c> (default 512).
    /// </para>
    /// </summary>
    [Explicit("Machine-dependent timing; run by name on a GPU machine and in a built player.")]
    public sealed class CameraCaptureFrameBudgetPlayModeTests
    {
        private const int Captures = 12;
        private const int WarmupFrames = 10;
        private readonly List<GameObject> _created = new();

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in _created)
            {
                if (go != null)
                {
                    UnityEngine.Object.Destroy(go);
                }
            }

            _created.Clear();
        }

        [UnityTest]
        public IEnumerator AsyncCapture_StaysUnderTheFrameBudget_WhileTheSyncPathIsMeasuredForComparison()
        {
            return UniTask.ToCoroutine(async () =>
            {
                if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
                {
                    Assert.Ignore("No graphics device.");
                }

                if (!SystemInfo.supportsAsyncGPUReadback)
                {
                    Assert.Ignore("This device has no AsyncGPUReadback; only the sync path exists here.");
                }

                double budgetMs = ReadDouble("COREAI_CAMERA_CAPTURE_BUDGET_MS", 1000.0 / 60.0);
                int size = (int)ReadDouble("COREAI_CAMERA_CAPTURE_SIZE", AgentCameraService.DefaultMaxSize);
                Camera camera = BuildBusyScene();
                int width = size;
                int height = Math.Max(1, size * 9 / 16);

                for (int i = 0; i < WarmupFrames; i++)
                {
                    await UniTask.Yield(PlayerLoopTiming.Update);
                }

                Measurement asyncPath = await MeasureAsync(new AsyncGpuCameraFrameReadback(), camera, width, height);
                Measurement syncPath = await MeasureAsync(new SyncCameraFrameReadback(), camera, width, height);

                Debug.Log(string.Format(CultureInfo.InvariantCulture,
                    "[CameraCaptureFrameBudget] {0}x{1} on {2} ({3}): async main-thread max {4:0.00} ms / avg {5:0.00} " +
                    "ms, longest frame {6:0.00} ms, readback waited {7} frames; sync main-thread max {8:0.00} ms / " +
                    "avg {9:0.00} ms, longest frame {10:0.00} ms; budget {11:0.00} ms.",
                    width, height, SystemInfo.graphicsDeviceType, Application.isEditor ? "editor" : "player",
                    asyncPath.MaxMainThreadMs, asyncPath.AverageMainThreadMs, asyncPath.LongestFrameMs,
                    asyncPath.FramesWaited,
                    syncPath.MaxMainThreadMs, syncPath.AverageMainThreadMs, syncPath.LongestFrameMs, budgetMs));

                Assert.AreEqual(Captures, asyncPath.AsyncCaptures, "every capture took the async path on this device");
                Assert.LessOrEqual(asyncPath.MaxMainThreadMs, budgetMs,
                    "an async capture must not block the main thread for more than one frame budget");
            });
        }

        private Camera BuildBusyScene()
        {
            GameObject cameraObject = new("FrameBudgetCamera");
            _created.Add(cameraObject);
            Camera camera = cameraObject.AddComponent<Camera>();
            camera.transform.SetPositionAndRotation(new Vector3(0f, 12f, -24f), Quaternion.Euler(25f, 0f, 0f));
            GameObject lightObject = new("FrameBudgetLight");
            _created.Add(lightObject);
            Light light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.shadows = LightShadows.Soft;
            lightObject.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            for (int x = -10; x <= 10; x++)
            {
                for (int z = -10; z <= 10; z++)
                {
                    GameObject block = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    block.transform.position = new Vector3(x * 1.2f, (x + z) % 3, z * 1.2f);
                    _created.Add(block);
                }
            }

            return camera;
        }

        private static async UniTask<Measurement> MeasureAsync(ICameraFrameReadback readback, Camera camera,
            int width, int height)
        {
            Measurement measurement = new();
            for (int i = 0; i < Captures; i++)
            {
                float frameBefore = Time.unscaledDeltaTime;
                CameraFrameReadbackResult frame = await readback.CaptureAsync(camera, width, height,
                    CaptureImageFormat.Jpeg, 75, CancellationToken.None);
                await UniTask.Yield(PlayerLoopTiming.Update);
                measurement.Add(frame, Math.Max(frameBefore, Time.unscaledDeltaTime) * 1000.0);
            }

            return measurement;
        }

        private static double ReadDouble(string name, double fallback)
        {
            string raw = Environment.GetEnvironmentVariable(name);
            return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) &&
                   value > 0
                ? value
                : fallback;
        }

        private sealed class Measurement
        {
            private double _sum;
            private int _count;

            public double MaxMainThreadMs { get; private set; }
            public double LongestFrameMs { get; private set; }
            public int FramesWaited { get; private set; }
            public int AsyncCaptures { get; private set; }
            public double AverageMainThreadMs => _count == 0 ? 0 : _sum / _count;

            public void Add(CameraFrameReadbackResult frame, double frameMs)
            {
                _sum += frame.MainThreadMilliseconds;
                _count++;
                MaxMainThreadMs = Math.Max(MaxMainThreadMs, frame.MainThreadMilliseconds);
                LongestFrameMs = Math.Max(LongestFrameMs, frameMs);
                FramesWaited = Math.Max(FramesWaited, frame.FramesWaited);
                if (frame.Mode == CameraReadbackMode.AsyncGpu)
                {
                    AsyncCaptures++;
                }
            }
        }
    }
}
