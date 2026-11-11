using System;
using System.Collections.Generic;
using System.IO;
using CoreAI.Look;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Spatial;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace CoreAI.Tests.EditMode.RbxApi.Binding
{
    /// <summary>
    /// The Unity half of P9's day-cycle slice and PointLight: the scene sink (sun direction, intensity,
    /// trilight ambient, revert), the light backend (a point light under the part's GameObject), the world
    /// host wiring, <see cref="RbxWorldHost.SetCamera"/>, and two masked-crop captures (noon against midnight
    /// on the ground, a torch on against off on a wall at night). The captures need a graphics device and
    /// URP and report Ignored without them; their PNGs land in TestResults/CoreAI-P9Lighting.
    /// </summary>
    [TestFixture]
    public sealed class P9UnityLightingEditModeTests
    {
        private const int Resolution = 128;

        private GameObject _sunGo;
        private Light _sun;
        private GameObject _hostGo;
        private RbxWorldHost _host;
        private Light _previousSun;
        private AmbientMode _previousAmbientMode;
        private Color _previousSky;
        private UnityRbxLightingSink _sink;
        private readonly List<Light> _silencedLights = new();

        [SetUp]
        public void SetUp()
        {
            RbxSpace.ResetForTests();
            _previousSun = RenderSettings.sun;
            _previousAmbientMode = RenderSettings.ambientMode;
            _previousSky = RenderSettings.ambientSkyColor;
            _sunGo = new GameObject("P9 Sun");
            _sun = _sunGo.AddComponent<Light>();
            _sun.type = LightType.Directional;
            _sun.intensity = 0.25f;
            _sunGo.transform.rotation = Quaternion.Euler(10f, 20f, 0f);
        }

        [TearDown]
        public void TearDown()
        {
            _sink?.Revert();
            _sink = null;
            foreach (Light light in _silencedLights)
            {
                if (light != null)
                {
                    light.enabled = true;
                }
            }

            _silencedLights.Clear();
            if (_hostGo != null)
            {
                Object.DestroyImmediate(_hostGo);
            }

            Object.DestroyImmediate(_sunGo);
            RenderSettings.sun = _previousSun;
            RenderSettings.ambientMode = _previousAmbientMode;
            RenderSettings.ambientSkyColor = _previousSky;
            RbxSpace.ResetForTests();
        }

        /// <summary>
        /// A sink with its own ambient ownership record, reverted in teardown. WHY: a sink claims the ambient
        /// for the default look on its first apply; a sink on the shared record that is never reverted left
        /// it claimed for every later test of the run.
        /// </summary>
        private UnityRbxLightingSink NewSink()
        {
            _sink = new UnityRbxLightingSink(_sun, new LookAmbientOwnership());
            return _sink;
        }

        private static RbxLightingState State(float clockTime, float brightness = 2f)
        {
            return new RbxLightingState(clockTime, 41.7f, brightness, new RbxColor3(0.1f, 0.1f, 0.1f),
                new RbxColor3(0.6f, 0.6f, 0.6f), RbxSunModel.SunDirection(clockTime, 41.7d));
        }

        // ---- the sink ------------------------------------------------------------------------------

        [Test]
        public void Sink_AtNoon_PointsTheSunAlongGetSunDirection_WithBrightnessAsIntensity()
        {
            UnityRbxLightingSink sink = NewSink();
            RbxLightingState noon = State(12f);
            sink.Apply(in noon);

            Vector3 towardSun = RbxSpace.DirectionToUnity(noon.SunDirection).normalized;
            Assert.Greater(Vector3.Dot(-_sun.transform.forward, towardSun), 0.9999f,
                "the light travels from the sun towards the scene");
            Assert.AreEqual(2f, _sun.intensity, 1e-5f);
            Assert.IsTrue(_sun.enabled);
            Assert.AreSame(_sun, RenderSettings.sun, "the sky follows the driven sun");
            Assert.AreEqual(AmbientMode.Trilight, RenderSettings.ambientMode);
            Assert.AreEqual(0.6f, RenderSettings.ambientSkyColor.r, 1e-5f);
            Assert.AreEqual(0.1f, RenderSettings.ambientGroundColor.r, 1e-5f, "Ambient is the ground term (P9-a)");
        }

        [Test]
        public void Sink_AtMidnight_SwitchesTheSunOff_AndDimsTheOutdoorAmbient_NegativeTwin()
        {
            UnityRbxLightingSink sink = NewSink();
            RbxLightingState midnight = State(0f);
            sink.Apply(in midnight);

            Assert.AreEqual(0f, _sun.intensity);
            Assert.IsFalse(_sun.enabled, "no shadow pass for a sun below the horizon");
            Assert.AreEqual(0.6f * RbxLightingMapping.NightOutdoorAmbientFloor, RenderSettings.ambientSkyColor.r, 1e-5f);
        }

        [Test]
        public void Sink_Revert_RestoresWhatTheFirstApplyFound()
        {
            Quaternion rotation = _sunGo.transform.rotation;
            RenderSettings.ambientMode = AmbientMode.Flat;
            UnityRbxLightingSink sink = NewSink();
            RbxLightingState noon = State(12f);
            sink.Apply(in noon);
            RbxLightingState evening = State(18.5f);
            sink.Apply(in evening);
            sink.Revert();

            Assert.AreEqual(0.25f, _sun.intensity);
            Assert.IsTrue(_sun.enabled);
            Assert.AreEqual(rotation, _sunGo.transform.rotation);
            Assert.AreEqual(AmbientMode.Flat, RenderSettings.ambientMode);
        }

        // ---- the backend -----------------------------------------------------------------------------

        [Test]
        public void Backend_DrawsAPointLightUnderThePartsGameObject_AndReleasesIt()
        {
            GameObject part = new("Torch part");
            try
            {
                InstanceId partId = new(7UL);
                InstanceId lightId = new(8UL);
                UnityRbxLightBackend backend = new(id => id == partId ? part : null);
                backend.Apply(lightId, partId,
                    new RbxLightRenderState(3f, new RbxColor3(1f, 0.6f, 0.2f), 16f, true, true));

                Light light = backend.GetLight(lightId);
                Assert.IsNotNull(light);
                Assert.AreSame(part.transform, light.transform.parent);
                Assert.AreEqual(Vector3.zero, light.transform.localPosition);
                Assert.AreEqual(LightType.Point, light.type);
                Assert.AreEqual(RbxSpace.LengthToUnity(16f), light.range, 1e-5f, "Range converts from studs");
                Assert.AreEqual(3f, light.intensity, 1e-5f);
                Assert.AreEqual(LightShadows.Soft, light.shadows);
                Assert.IsTrue(light.enabled);
                Assert.IsTrue(backend.Holds(lightId));

                backend.Apply(lightId, partId,
                    new RbxLightRenderState(3f, new RbxColor3(1f, 0.6f, 0.2f), 16f, false, false));
                Assert.IsFalse(light.enabled, "a light off by script or budget is drawn off");

                backend.Release(lightId);
                Assert.IsFalse(backend.Holds(lightId));
                Assert.AreEqual(0, part.transform.childCount);
            }
            finally
            {
                Object.DestroyImmediate(part);
            }
        }

        // ---- the world host --------------------------------------------------------------------------

        private RbxWorldHost NewHost(Camera camera = null)
        {
            RenderSettings.sun = _sun;
            _hostGo = new GameObject("P9 RbxWorldHost");
            _host = _hostGo.AddComponent<RbxWorldHost>();
            if (camera != null)
            {
                _host.SetCamera(camera);
            }

            _host.Initialize();
            return _host;
        }

        private RbxInstance NewPart(string name, RbxVector3 size, RbxVector3 position)
        {
            RbxInstance part = _host.Registry.Create("Part");
            part.Name = name;
            part.Parent = _host.Registry.WorldRoot;
            _host.Binder.SetAnchored(part.Id, true);
            _host.Binder.SetSize(part.Id, size);
            _host.Binder.SetPosition(part.Id, position);
            return part;
        }

        [Test]
        public void Host_LeavesTheSceneLightingAlone_UntilAScriptDrivesIt()
        {
            NewHost();
            RbxLighting lighting = (RbxLighting)_host.Game.GetService("Lighting");
            Assert.AreSame(_host.LightingSink, lighting.Sink);
            Assert.AreEqual(0.25f, _sun.intensity, "an undriven world keeps the authored sun");

            lighting.SetClockTime(12d);
            Assert.AreEqual(1f, _sun.intensity, 1e-5f, "Brightness 1 at noon");
            lighting.SetClockTime(0d);
            Assert.IsFalse(_sun.enabled);
        }

        [Test]
        public void Host_DrawsAPointLightOnAWorkspacePart_AndSwitchesItOff()
        {
            NewHost();
            RbxInstance part = NewPart("Torch", new RbxVector3(1f, 1f, 1f), new RbxVector3(0f, 2f, 0f));
            RbxPointLight light = (RbxPointLight)_host.Registry.Create("PointLight");
            light.Parent = part;
            _host.Lights.Tick(new RbxVector3(0f, 0f, 0f));

            Assert.IsTrue(_host.Binder.TryGetBoundObject(part.Id, out GameObject partGo));
            Light drawn = _host.LightBackend.GetLight(light.Id);
            Assert.IsNotNull(drawn);
            Assert.AreSame(partGo.transform, drawn.transform.parent);
            Assert.IsTrue(drawn.enabled);

            light.SetEnabled(false);
            _host.Lights.Tick(new RbxVector3(0f, 0f, 0f));
            Assert.IsFalse(drawn.enabled);

            light.Destroy();
            Assert.IsNull(_host.LightBackend.GetLight(light.Id), "a destroyed light is released at once");
        }

        // ---- SetCamera --------------------------------------------------------------------------------

        [Test]
        public void SetCamera_BeforeInitialize_DrivesThatCamera_NotCameraMain()
        {
            GameObject mainGo = new("P9 Main Camera") { tag = "MainCamera" };
            GameObject otherGo = new("P9 Assigned Camera");
            try
            {
                Camera main = mainGo.AddComponent<Camera>();
                Camera assigned = otherGo.AddComponent<Camera>();
                Vector3 mainBefore = mainGo.transform.position;
                NewHost(assigned);

                Assert.AreSame(assigned, _host.SceneCamera);
                _host.CameraRig.SetCFrame(RbxCFrame.FromPosition(new RbxVector3(10f, 20f, 30f)));
                Assert.AreEqual(RbxSpace.ToUnity(new RbxVector3(10f, 20f, 30f)), otherGo.transform.position);
                Assert.AreEqual(mainBefore, mainGo.transform.position, "Camera.main is not the one driven");
                Assert.AreNotSame(main, _host.SceneCamera);
            }
            finally
            {
                Object.DestroyImmediate(mainGo);
                Object.DestroyImmediate(otherGo);
            }
        }

        [Test]
        public void SetCamera_AfterInitialize_RefusesAnotherCamera_AndAcceptsTheSameOne_NegativeTwin()
        {
            GameObject firstGo = new("P9 First Camera");
            GameObject secondGo = new("P9 Second Camera");
            try
            {
                Camera first = firstGo.AddComponent<Camera>();
                Camera second = secondGo.AddComponent<Camera>();
                NewHost(first);

                Assert.DoesNotThrow(() => _host.SetCamera(first), "the camera it already drives is a no-op");
                InvalidOperationException refused = Assert.Throws<InvalidOperationException>(
                    () => _host.SetCamera(second));
                StringAssert.Contains("before Initialize", refused.Message);
                Assert.AreSame(first, _host.SceneCamera, "a refused call changes nothing");
            }
            finally
            {
                Object.DestroyImmediate(firstGo);
                Object.DestroyImmediate(secondGo);
            }
        }

        // ---- captures --------------------------------------------------------------------------------

        private static void RequireRendering()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                Assert.Ignore("The lighting captures need a graphics device; -nographics is not a pass.");
            }

            if (GraphicsSettings.currentRenderPipeline == null
                || GraphicsSettings.currentRenderPipeline.GetType().Name != "UniversalRenderPipelineAsset")
            {
                Assert.Ignore("The lighting captures need an active Universal Render Pipeline asset.");
            }
        }

        /// <summary>
        /// Switches off every other enabled light for a capture and restores them in teardown. WHY: the Editor's
        /// test scene comes with Unity's default "Directional Light" at intensity 1, which the day cycle does not
        /// drive; it kept the midnight ground crop at 0.46 against 0.02 without it, and lit the torch wall at
        /// night to 0.40.
        /// </summary>
        private void IsolateTheDrivenSun()
        {
            foreach (Light light in Object.FindObjectsByType<Light>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (light != _sun && light.enabled && light.type == LightType.Directional)
                {
                    light.enabled = false;
                    _silencedLights.Add(light);
                }
            }
        }

        private static Camera NewCaptureCamera(Vector3 position, Quaternion rotation)
        {
            GameObject cameraGo = new("P9 Capture Camera");
            Camera camera = cameraGo.AddComponent<Camera>();
            camera.transform.SetPositionAndRotation(position, rotation);
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.fieldOfView = 50f;
            camera.enabled = false;
            return camera;
        }

        /// <summary>Mean luminance of the centre quarter of the frame, which the test's object fills.</summary>
        private static float CaptureCropLuminance(Camera camera, string fileName)
        {
            RenderTexture target = new(Resolution, Resolution, 24, RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.sRGB) { antiAliasing = 1 };
            Texture2D image = new(Resolution, Resolution, TextureFormat.RGBA32, false);
            RenderTexture previousActive = RenderTexture.active;
            try
            {
                RenderPipeline.StandardRequest request = new() { destination = target };
                if (!RenderPipeline.SupportsRenderRequest(camera, request))
                {
                    Assert.Ignore("The active pipeline cannot execute the required SRP render request.");
                }

                camera.targetTexture = target;
                RenderPipeline.SubmitRenderRequest(camera, request);
                RenderTexture.active = target;
                image.ReadPixels(new Rect(0, 0, Resolution, Resolution), 0, 0);
                image.Apply();
                string directory = Path.Combine(Path.GetFullPath(Path.Combine(Application.dataPath, "..")),
                    "TestResults", "CoreAI-P9Lighting");
                Directory.CreateDirectory(directory);
                File.WriteAllBytes(Path.Combine(directory, fileName), image.EncodeToPNG());

                Color[] pixels = image.GetPixels();
                double sum = 0d;
                int count = 0;
                int from = Resolution / 4;
                int to = Resolution * 3 / 4;
                for (int y = from; y < to; y++)
                {
                    for (int x = from; x < to; x++)
                    {
                        Color pixel = pixels[y * Resolution + x];
                        sum += 0.2126d * pixel.r + 0.7152d * pixel.g + 0.0722d * pixel.b;
                        count++;
                    }
                }

                return (float)(sum / count);
            }
            finally
            {
                camera.targetTexture = null;
                RenderTexture.active = previousActive;
                Object.DestroyImmediate(image);
                target.Release();
                Object.DestroyImmediate(target);
            }
        }

        [Test]
        [Category("LightingRender")]
        public void Capture_NoonToMidnight_DarkensTheMaskedGroundCrop()
        {
            RequireRendering();
            NewHost();
            IsolateTheDrivenSun();
            NewPart("Ground", new RbxVector3(400f, 1f, 400f), new RbxVector3(0f, 0f, 0f));
            Camera camera = NewCaptureCamera(RbxSpace.ToUnity(new RbxVector3(0f, 40f, 0f)),
                Quaternion.Euler(90f, 0f, 0f));
            try
            {
                RbxLighting lighting = (RbxLighting)_host.Game.GetService("Lighting");
                lighting.SetClockTime(12d);
                float noon = CaptureCropLuminance(camera, "ground-noon.png");
                lighting.SetClockTime(0d);
                float midnight = CaptureCropLuminance(camera, "ground-midnight.png");
                TestContext.WriteLine("ground crop luminance: noon " + noon + ", midnight " + midnight);

                Assert.Greater(noon, midnight * 2f, "midnight must darken the ground crop");
            }
            finally
            {
                Object.DestroyImmediate(camera.gameObject);
            }
        }

        [Test]
        [Category("LightingRender")]
        public void Capture_ATorchAtNight_LightsTheMaskedWallCrop_AndRemovingItDarkensIt()
        {
            RequireRendering();
            NewHost();
            IsolateTheDrivenSun();
            NewPart("Wall", new RbxVector3(60f, 60f, 1f), new RbxVector3(0f, 0f, -10f));
            RbxInstance torch = NewPart("Torch", new RbxVector3(0.2f, 0.2f, 0.2f), new RbxVector3(0f, 0f, -6f));
            RbxPointLight light = (RbxPointLight)_host.Registry.Create("PointLight");
            light.SetRange(20d);
            light.SetBrightness(4d);
            light.Parent = torch;
            Camera camera = NewCaptureCamera(RbxSpace.ToUnity(new RbxVector3(0f, 0f, 14f)), Quaternion.identity);
            camera.transform.rotation = Quaternion.LookRotation(RbxSpace.DirectionToUnity(new RbxVector3(0f, 0f, -1f)));
            try
            {
                RbxLighting lighting = (RbxLighting)_host.Game.GetService("Lighting");
                lighting.SetClockTime(0d);
                lighting.SetOutdoorAmbient(new RbxColor3(0.05f, 0.05f, 0.08f));
                lighting.SetAmbient(new RbxColor3(0f, 0f, 0f));
                _host.Lights.Tick(new RbxVector3(0f, 0f, 14f));
                float on = CaptureCropLuminance(camera, "wall-torch-on.png");
                light.Destroy();
                _host.Lights.Tick(new RbxVector3(0f, 0f, 14f));
                float off = CaptureCropLuminance(camera, "wall-torch-off.png");
                TestContext.WriteLine("wall crop luminance: torch on " + on + ", torch removed " + off);

                Assert.Greater(on, off * 2f, "the torch must light the wall crop at night");
            }
            finally
            {
                Object.DestroyImmediate(camera.gameObject);
            }
        }
    }
}
