#if COREAI_LLM && !UNITY_WEBGL
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using CoreAI.Messaging;
using CoreAI.Session;
using CoreAI.Vision;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// LIVE vision round trip of the <c>camera</c> tool (P16, TODO [R8]): a real vision model orbits its free camera
    /// around a two-block build, captures it, and names the colour of each block. The colours are drawn at random
    /// per run, so only a model that really received the frame can answer; the run logs the seed and, when
    /// <c>COREAI_VISUAL_CAPTURE_DIR</c> is set, saves every frame the model saw.
    /// <para>
    /// Explicit: it needs a real vision-capable model, resolved like the rest of the LlmVerification suite
    /// (<c>COREAI_TEST_BASE_URL</c>/<c>COREAI_TEST_MODEL</c>/<c>COREAI_TEST_API_KEY</c>, the local config file or the
    /// settings asset). <c>COREAI_TEST_VISION_MODEL</c> overrides the model for this test and
    /// <c>COREAI_TEST_VISION=on</c> forces the vision gate; a model the gate calls text-only is skipped.
    /// </para>
    /// </summary>
    [TestFixture]
    [Explicit("Needs a real vision-capable model; run by name.")]
    public sealed class AgentVisionLiveRoundTripPlayModeTests
    {
        private const string RoleId = "VisionRoundTrip";
        private const string BuildName = "VisionRoundTripBuild";
        private const string EnvVisionModel = "COREAI_TEST_VISION_MODEL";
        private const string EnvVisionMode = "COREAI_TEST_VISION";
        private const string EnvCaptureDir = "COREAI_VISUAL_CAPTURE_DIR";
        private const int RequestTimeoutSeconds = 240;
        private const float WaitSeconds = 360f;
        private const int TestTimeoutMs = 480000;
        private const int AnswerMaxOutputTokens = 4096;

        private static readonly (string Word, Color Color)[] Palette =
        {
            ("red", new Color(0.9f, 0.1f, 0.1f)),
            ("green", new Color(0.1f, 0.8f, 0.1f)),
            ("blue", new Color(0.1f, 0.2f, 0.95f)),
            ("yellow", new Color(0.95f, 0.85f, 0.1f))
        };

        private readonly List<UnityEngine.Object> _created = new();
        private LiveTestRequestScope _requests;
        private PlayModeProductionLikeLlmHandle _handle;
        private AgentCameraService _service;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _requests = new LiveTestRequestScope(TestTimeoutMs);
            yield break;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            // WHY: a framework timeout skips the test body's cleanup, so cancel the abandoned request here and
            // let it unwind before the client and the scene are disposed.
            if (_requests != null)
            {
                yield return _requests.CancelAllAndDrain();
                _requests = null;
            }

            _service?.Dispose();
            _service = null;
            _handle?.Dispose();
            _handle = null;
            foreach (UnityEngine.Object created in _created)
            {
                if (created != null)
                {
                    UnityEngine.Object.Destroy(created);
                }
            }

            _created.Clear();
        }

        [UnityTest]
        [Timeout(TestTimeoutMs)]
        public IEnumerator VisionModel_OrbitsCapturesAndNamesTheBlockColours()
        {
            if (!PlayModeProductionLikeLlmFactory.TryCreate(null, 0f, RequestTimeoutSeconds, GetEnv(EnvVisionModel),
                    out PlayModeProductionLikeLlmHandle handle, out string ignore))
            {
                Assert.Ignore(ignore);
            }

            // WHY: disposed in [UnityTearDown] after the request drain, never under a still-running turn.
            _handle = handle;
            string model = handle.ResolvedConfig != null && !string.IsNullOrWhiteSpace(handle.ResolvedConfig.Model)
                ? handle.ResolvedConfig.Model
                : CoreAISettingsAsset.Instance != null ? CoreAISettingsAsset.Instance.ModelName : "";
            VisionSupportMode mode = ParseVisionMode(GetEnv(EnvVisionMode));
            if (!VisionCapability.IsEnabled(mode, model))
            {
                Assert.Ignore($"Model '{model}' is not vision-capable (mode={mode}). Set {EnvVisionModel} to a " +
                              $"vision model and/or {EnvVisionMode}=on.");
            }

            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
            {
                Assert.Ignore("No graphics device: the camera cannot render.");
            }

            if (handle.ResolvedBackend == PlayModeProductionLikeLlmBackend.LlmUnity)
            {
                yield return PlayModeProductionLikeLlmFactory.EnsureLlmUnityModelReady(handle);
            }

            int seed = Environment.TickCount & 0x7FFFFFFF;
            System.Random random = new(seed);
            int bottomIndex = random.Next(Palette.Length);
            int topIndex = (bottomIndex + 1 + random.Next(Palette.Length - 1)) % Palette.Length;
            BuildScene(Palette[bottomIndex].Color, Palette[topIndex].Color);

            RecordingReadback readback = new(CameraFrameReadback.CreateDefault());
            RecordingRig rig = new(new AgentCameraRig());
            AgentCameraOptions options = AgentCameraOptions.CreateDefault();
            options.MinCaptureIntervalSeconds = 0;
            _service = new AgentCameraService(options, readback, rig, null);
            CameraLlmTool tool = new(_service, RoleId);

            AiTaskRequest task = new()
            {
                RoleId = RoleId,
                SystemPrompt =
                    "You are an agent inside a running 3D game. You can see the scene only through your camera " +
                    "tools. Always look before you answer, and answer briefly.",
                Hint =
                    $"The scene has a build named '{BuildName}': a wide block with a smaller block on top of it. " +
                    $"Call camera_orbit with target '{BuildName}', yaw 30, pitch 25 and distance 7, then call " +
                    "camera_capture and look at the picture. Then answer exactly in the form " +
                    "'top: <colour>, bottom: <colour>' using only red, green, blue or yellow.",
                MaxOutputTokens = AnswerMaxOutputTokens
            };

            AiOrchestrator orchestrator = CreateOrchestrator(handle.Client, tool);
            StringBuilder answer = new();
            string[] error = new string[1];
            CancellationTokenSource cts = _requests.CreateCancellation();
            Task run = _requests.Track(RunStreamingAsync(orchestrator, task, answer, error, cts.Token));
            yield return PlayModeTestAwait.WaitTask(run, _requests.Cap(WaitSeconds), "VisionRoundTrip", cts);

            string text = answer.ToString();
            string saved = SaveFrames(readback.Frames, seed);
            Debug.Log($"[VisionRoundTrip] model={model} seed={seed} expected top={Palette[topIndex].Word} " +
                      $"bottom={Palette[bottomIndex].Word}; orbits={rig.Orbits}, captures={readback.Frames.Count}, " +
                      $"readback={readback.LastMode}, frames saved to: {saved}; answer: {text} (error={error[0]})");

            Assert.IsNull(error[0], $"the turn failed: {error[0]}");
            Assert.GreaterOrEqual(rig.Orbits, 1, "the model orbited its camera");
            Assert.GreaterOrEqual(readback.Frames.Count, 1, "the model captured a frame");
            byte[] last = readback.Frames[readback.Frames.Count - 1];
            Assert.AreEqual(0xFF, last[0], "the frame the model saw is a JPEG");
            Assert.AreEqual(0xD8, last[1]);
            string lower = text.ToLowerInvariant();
            StringAssert.IsMatch(@"top\W*" + Palette[topIndex].Word, lower,
                $"the model named the top block's colour ({Palette[topIndex].Word})");
            StringAssert.IsMatch(@"bottom\W*" + Palette[bottomIndex].Word, lower,
                $"the model named the bottom block's colour ({Palette[bottomIndex].Word})");
        }

        private void BuildScene(Color bottom, Color top)
        {
            GameObject cameraObject = new("VisionRoundTripPlayerCamera") { tag = "MainCamera" };
            _created.Add(cameraObject);
            Camera camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.55f, 0.55f, 0.58f);
            cameraObject.transform.SetPositionAndRotation(new Vector3(0f, 3f, -12f), Quaternion.Euler(10f, 0f, 0f));

            GameObject lightObject = new("VisionRoundTripLight");
            _created.Add(lightObject);
            Light light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.2f;
            lightObject.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            GameObject build = new(BuildName);
            _created.Add(build);
            AddBlock(build.transform, "BottomBlock", new Vector3(0f, 0.5f, 0f), new Vector3(3f, 1f, 3f), bottom);
            AddBlock(build.transform, "TopBlock", new Vector3(0f, 1.5f, 0f), new Vector3(1.5f, 1f, 1.5f), top);
        }

        private void AddBlock(Transform parent, string name, Vector3 position, Vector3 scale, Color color)
        {
            GameObject block = GameObject.CreatePrimitive(PrimitiveType.Cube);
            block.name = name;
            block.transform.SetParent(parent, false);
            block.transform.localPosition = position;
            block.transform.localScale = scale;
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            if (shader != null)
            {
                Material material = new(shader) { color = color };
                _created.Add(material);
                block.GetComponent<MeshRenderer>().sharedMaterial = material;
            }
            else
            {
                block.GetComponent<MeshRenderer>().material.color = color;
            }
        }

        private static string SaveFrames(IReadOnlyList<byte[]> frames, int seed)
        {
            string directory = GetEnv(EnvCaptureDir);
            if (directory == null || frames.Count == 0)
            {
                return "(not saved; set " + EnvCaptureDir + ")";
            }

            Directory.CreateDirectory(directory);
            for (int i = 0; i < frames.Count; i++)
            {
                File.WriteAllBytes(Path.Combine(directory, $"vision_round_trip_{seed}_{i}.jpg"), frames[i]);
            }

            return directory;
        }

        private AiOrchestrator CreateOrchestrator(ILlmClient client, ILlmTool cameraTool)
        {
            AgentMemoryPolicy policy = new();
            AgentConfig config = new AgentBuilder(RoleId)
                .WithTool(cameraTool)
                .WithMode(AgentMode.ToolsOnly)
                .WithMaxToolCallRoundtrips(8)
                .Build();
            config.ApplyToPolicy(policy);

            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            _created.Add(settings);
            AiPromptComposer composer = new(
                new BuiltInDefaultAgentSystemPromptProvider(),
                new NoAgentUserPromptTemplateProvider(),
                new NullLuaScriptVersionStore());

            return new AiOrchestrator(
                new SoloAuthorityHost(),
                client,
                new NullSink(),
                new SessionTelemetryCollector(),
                composer,
                new InMemoryStore(),
                policy,
                new NoOpRoleStructuredResponsePolicy(),
                new NullAiOrchestrationMetrics(),
                settings,
                new LocalActorIdentityProvider("vision-round-trip-live-test"));
        }

        private static async Task RunStreamingAsync(AiOrchestrator orchestrator, AiTaskRequest task,
            StringBuilder answer, string[] error, CancellationToken cancellationToken)
        {
            await foreach (LlmStreamChunk chunk in orchestrator.RunStreamingAsync(task, cancellationToken))
            {
                if (!string.IsNullOrEmpty(chunk.Error))
                {
                    error[0] = chunk.Error;
                }

                if (!string.IsNullOrEmpty(chunk.Text))
                {
                    answer.Append(chunk.Text);
                }
            }
        }

        private static VisionSupportMode ParseVisionMode(string raw)
        {
            switch (raw?.Trim().ToLowerInvariant())
            {
                case "on":
                case "true":
                case "1":
                    return VisionSupportMode.On;
                case "off":
                case "false":
                case "0":
                    return VisionSupportMode.Off;
                default:
                    return VisionSupportMode.Auto;
            }
        }

        private static string GetEnv(string name)
        {
            string value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        /// <summary>Delegates to the shipped readback and keeps every frame the model was shown.</summary>
        private sealed class RecordingReadback : ICameraFrameReadback
        {
            private readonly ICameraFrameReadback _inner;

            public RecordingReadback(ICameraFrameReadback inner)
            {
                _inner = inner;
            }

            public List<byte[]> Frames { get; } = new();

            public CameraReadbackMode LastMode { get; private set; }

            public async UniTask<CameraFrameReadbackResult> CaptureAsync(Camera camera, int width, int height,
                CaptureImageFormat format, int jpegQuality, CancellationToken cancellationToken)
            {
                CameraFrameReadbackResult frame =
                    await _inner.CaptureAsync(camera, width, height, format, jpegQuality, cancellationToken);
                Frames.Add(frame.Bytes);
                LastMode = frame.Mode;
                return frame;
            }
        }

        /// <summary>Delegates to the shipped rig and counts the orbits.</summary>
        private sealed class RecordingRig : IAgentCameraRig
        {
            private readonly IAgentCameraRig _inner;

            public RecordingRig(IAgentCameraRig inner)
            {
                _inner = inner;
            }

            public int Orbits { get; private set; }

            public CameraRigResult Look(Camera camera, CameraLookRequest request)
            {
                return _inner.Look(camera, request);
            }

            public CameraRigResult Orbit(Camera camera, CameraOrbitRequest request)
            {
                CameraRigResult result = _inner.Orbit(camera, request);
                if (result.Ok)
                {
                    Orbits++;
                }

                return result;
            }
        }

        private sealed class NullSink : IAiGameCommandSink
        {
            public void Publish(ApplyAiGameCommand command)
            {
            }
        }
    }
}
#endif
