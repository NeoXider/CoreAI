using System.Collections;
using System.Collections.Generic;
using System.IO;
using CoreAI.Look;
using CoreAI.Look.Urp;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.TestTools;
using VContainer;
using VContainer.Unity;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// Rendered proof that the default look reaches the frame: with post-processing on, an HDR surface
    /// (what Neon renders as) blooms into a halo; with it off, the same ring of pixels stays dark. The
    /// metric is the mean luminance of a ring masked around the sphere's silhouette, never a whole-frame
    /// mean. Every frame is also written to <c>artifacts/look/</c> for a person to look at.
    /// </summary>
    /// <remarks>
    /// WHY an HDR target: URP gives a camera that renders into a texture an intermediate colour buffer in
    /// that texture's own format. An 8-bit target clamps the 6.0 sphere to 1.0 before bloom runs, so only
    /// the soft knee below the threshold (about 6% of a clamped pixel) can bloom; on screen the buffer is
    /// HDR, and the half-float target here is that screen path.
    /// </remarks>
    public sealed class UrpLookBloomPlayModeTests
    {
        private const int Size = 128;
        private const float RingInnerPixels = 38f;
        private const float RingOuterPixels = 56f;
        private const float CoreRadiusPixels = 12f;
        private const float SphereBrightness = 6f;

        private readonly List<Object> _created = new();
        private readonly List<LifetimeScope> _scopes = new();
        private string _outputFolder;

        [SetUp]
        public void SetUp()
        {
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName
                                 ?? Directory.GetCurrentDirectory();
            _outputFolder = Path.Combine(projectRoot, "artifacts", "look");
            Directory.CreateDirectory(_outputFolder);
        }

        [TearDown]
        public void TearDown()
        {
            // WHY disposed now and not left to Destroy: a destroyed scope reverts its look only at the end
            // of the frame, and until then its applier still writes its options into every camera that
            // begins rendering, including the next test's.
            foreach (LifetimeScope scope in _scopes)
            {
                if (scope != null)
                {
                    scope.Dispose();
                }
            }

            _scopes.Clear();
            foreach (Object created in _created)
            {
                if (created != null)
                {
                    Object.Destroy(created);
                }
            }

            _created.Clear();
        }

        [Test]
        public void PostProcessingSwitch_DecidesWhetherAnHdrSurfaceBlooms()
        {
            Shader unlit = RequireRendering();
            Camera camera = BuildRig(unlit);
            RenderTexture target = CreateHdrTarget();
            CoreAiLookProfile profile = CoreAiLookProfile.CreateDefault();
            profile.RealtimeReflectionProbe = false;
            _created.Add(profile);
            UrpLookApplier applier = new();
            try
            {
                applier.Apply(profile, Options(true), LookMode.Apply);
                Assert.IsTrue(camera.GetUniversalAdditionalCameraData().renderPostProcessing,
                    "the look switches post-processing on for the game camera");
                Texture2D withPostImage = Render(camera, target, "bloom_applier_post_on.png");

                applier.Apply(profile, Options(false), LookMode.Apply);
                Assert.IsFalse(camera.GetUniversalAdditionalCameraData().renderPostProcessing,
                    "the option switches post-processing off for the same camera");
                Texture2D withoutPostImage = Render(camera, target, "bloom_applier_post_off.png");

                AssertBloomOnlyWithPost(withPostImage, withoutPostImage);
            }
            finally
            {
                applier.Revert();
            }
        }

        [UnityTest]
        public IEnumerator DefaultLookOfAScope_ReachesTheFrame_AndTheGraphicsOptionTurnsPostOff()
        {
            Shader unlit = RequireRendering();
            Camera camera = BuildRig(unlit);
            RenderTexture target = CreateHdrTarget();
            CoreAiLookProfile profile = CoreAiLookProfile.CreateDefault();
            profile.RealtimeReflectionProbe = false;
            _created.Add(profile);
            CoreAiLookSettings settings = new()
            {
                Profile = profile,
                // WHY no preset: the platform default preset must carry post-processing on by itself; fog
                // and sky are off only so the ring around the sphere has a black background.
                StoreOverride = new InMemoryGraphicsOptionsStore(new GraphicsOptions(fog: false, sky: false))
            };
            LifetimeScope scope = LifetimeScope.Create(builder => builder.RegisterCoreAiLook(settings),
                "Look Bloom Scope");
            _scopes.Add(scope);
            _created.Add(scope.gameObject);
            yield return null;
            yield return null;

            ICoreAiLookApplier applier = scope.Container.Resolve<ICoreAiLookApplier>();
            Assert.IsInstanceOf<UrpLookApplier>(applier, "with URP active the scope applies the URP look");
            Volume volume = ((UrpLookApplier)applier).Volume;
            Assert.IsNotNull(volume, "the URP look builds its global Volume");
            Assert.IsTrue(volume.sharedProfile.TryGet(out Bloom bloom) && bloom.active && bloom.intensity.value > 0f,
                "the default Volume carries an active bloom");
            Assert.IsTrue(volume.sharedProfile.TryGet(out Tonemapping tonemapping) &&
                          tonemapping.mode.value != TonemappingMode.None, "the default Volume tonemaps");
            UniversalAdditionalCameraData data = camera.GetComponent<UniversalAdditionalCameraData>();
            Assert.IsNotNull(data, "the look gives the scene camera URP camera data");
            Assert.IsTrue(data.renderPostProcessing, "the platform default preset renders post-processing");
            Assert.AreNotEqual(AntialiasingMode.None, data.antialiasing, "the default preset anti-aliases");
            Texture2D withPostImage = Render(camera, target, "bloom_scope_post_on.png");

            IGraphicsOptionsService options = scope.Container.Resolve<IGraphicsOptionsService>();
            options.SetOptions(options.Options.WithPostProcessing(false));
            Assert.IsFalse(data.renderPostProcessing, "the Graphics page option switches post-processing off");
            Texture2D withoutPostImage = Render(camera, target, "bloom_scope_post_off.png");

            AssertBloomOnlyWithPost(withPostImage, withoutPostImage);
        }

        private static Shader RequireRendering()
        {
            if (!(GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset))
            {
                Assert.Ignore("URP is not the active render pipeline.");
            }

            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                Assert.Ignore("Rendering needs a graphics device.");
            }

            if (!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.DefaultHDR))
            {
                Assert.Ignore("This device has no HDR render texture format.");
            }

            Shader unlit = Shader.Find("Universal Render Pipeline/Unlit");
            if (unlit == null)
            {
                Assert.Ignore("The URP Unlit shader is not available.");
            }

            return unlit;
        }

        private static void AssertBloomOnlyWithPost(Texture2D withPostImage, Texture2D withoutPostImage)
        {
            float withCore = DiscLuminance(withPostImage, 0f, CoreRadiusPixels);
            float withoutCore = DiscLuminance(withoutPostImage, 0f, CoreRadiusPixels);
            float withRing = DiscLuminance(withPostImage, RingInnerPixels, RingOuterPixels);
            float withoutRing = DiscLuminance(withoutPostImage, RingInnerPixels, RingOuterPixels);
            string numbers = "core with " + withCore + ", core without " + withoutCore + ", ring with " + withRing +
                             ", ring without " + withoutRing;
            TestContext.WriteLine("[Look] " + numbers);
            Assert.Greater(withoutCore, 1f, "without post-processing the sphere keeps its HDR value (" +
                                            numbers + ")");
            Assert.Greater(withCore, 0.5f, "with post-processing the tonemapped sphere is still bright (" +
                                           numbers + ")");
            Assert.Less(withCore, withoutCore, "tonemapping compresses the HDR sphere (" + numbers + ")");
            Assert.Greater(withRing, withoutRing + 0.01f,
                "bloom must light the ring around the HDR sphere only while post-processing is on (" +
                numbers + ")");
        }

        private static ResolvedGraphicsOptions Options(bool postProcessing)
        {
            return GraphicsOptionsResolver.Resolve(
                new GraphicsOptions(GraphicsQualityPreset.High, postProcessing, fog: false, sky: false),
                GraphicsQualityPreset.High);
        }

        private RenderTexture CreateHdrTarget()
        {
            RenderTexture target = new(Size, Size, 24, RenderTextureFormat.DefaultHDR);
            _created.Add(target);
            return target;
        }

        private Camera BuildRig(Shader unlit)
        {
            GameObject cameraObject = new("Look Bloom Camera");
            _created.Add(cameraObject);
            cameraObject.transform.position = new Vector3(0f, 0f, -4f);
            Camera camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.fieldOfView = 30f;
            camera.allowHDR = true;

            GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _created.Add(sphere);
            Material material = new(unlit);
            _created.Add(material);
            material.SetColor("_BaseColor", new Color(SphereBrightness, SphereBrightness, SphereBrightness, 1f));
            sphere.GetComponent<Renderer>().sharedMaterial = material;
            return camera;
        }

        private Texture2D Render(Camera camera, RenderTexture target, string fileName)
        {
            RenderPipeline.StandardRequest request = new() { destination = target };
            RenderTexture previous = RenderTexture.active;
            if (RenderPipeline.SupportsRenderRequest(camera, request))
            {
                RenderPipeline.SubmitRenderRequest(camera, request);
            }
            else
            {
                camera.targetTexture = target;
                camera.Render();
                camera.targetTexture = null;
            }

            Texture2D image = new(Size, Size, TextureFormat.RGBAFloat, false, true);
            _created.Add(image);
            RenderTexture.active = target;
            image.ReadPixels(new Rect(0, 0, Size, Size), 0, 0);
            image.Apply();
            RenderTexture.active = previous;
            WritePng(image, fileName);
            return image;
        }

        private void WritePng(Texture2D linearImage, string fileName)
        {
            Texture2D png = new(Size, Size, TextureFormat.RGBA32, false);
            try
            {
                Color[] pixels = linearImage.GetPixels();
                for (int index = 0; index < pixels.Length; index++)
                {
                    Color linear = pixels[index];
                    pixels[index] = new Color(Mathf.Clamp01(linear.r), Mathf.Clamp01(linear.g),
                        Mathf.Clamp01(linear.b), 1f).gamma;
                }

                png.SetPixels(pixels);
                png.Apply();
                File.WriteAllBytes(Path.Combine(_outputFolder, fileName), png.EncodeToPNG());
            }
            finally
            {
                Object.Destroy(png);
            }
        }

        /// <summary>Mean linear luminance of the annulus between the two radii around the frame centre.</summary>
        private static float DiscLuminance(Texture2D image, float innerRadius, float outerRadius)
        {
            float centre = (Size - 1) * 0.5f;
            float sum = 0f;
            int count = 0;
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    float dx = x - centre;
                    float dy = y - centre;
                    float distance = Mathf.Sqrt(dx * dx + dy * dy);
                    if (distance < innerRadius || distance > outerRadius)
                    {
                        continue;
                    }

                    Color pixel = image.GetPixel(x, y);
                    sum += 0.2126f * pixel.r + 0.7152f * pixel.g + 0.0722f * pixel.b;
                    count++;
                }
            }

            return count == 0 ? 0f : sum / count;
        }
    }
}
