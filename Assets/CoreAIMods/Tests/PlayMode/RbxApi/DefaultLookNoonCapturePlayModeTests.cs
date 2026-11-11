using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using CoreAI.Look;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Rendering;
using CoreAI.Mods.Rbx.Spatial;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode.RbxApi
{
    /// <summary>
    /// A noon Rbx scene (grass, brick, wood, metal, Glass, water and a Neon bar under a high sun) captured
    /// with the default look applied and without it, into <c>artifacts/look/</c>, for a person to compare.
    /// The applier is the one a <c>CoreAILifetimeScope</c> creates (<see cref="CoreAiLookApplierRegistry"/>),
    /// and the frames are rendered into a half-float target, the format of the screen's intermediate
    /// buffer, then clamped to 8 bits the way a display shows them. The checks are masked crops: the
    /// look's sky replaces the scene's default sky, and the brick wall right beside the Neon bar is lit by
    /// bloom only with the look on, measured against the same wall a little further from the bar so that
    /// tonemapping, which changes both bands alike, cancels out. The look's fog must stay faint at this
    /// distance: the brick wall keeps at least half of its texture contrast (luminance spread) with the
    /// look on. Before the Rbx shaders took their fog factor from the vertex stage, the default fog washed every lit
    /// part into one flat grey and this crop's spread fell to nearly zero.
    /// </summary>
    public sealed class DefaultLookNoonCapturePlayModeTests
    {
        private const int ShotWidth = 640;
        private const int ShotHeight = 360;
        private const int HaloInnerPixels = 4;
        private const int HaloOuterPixels = 14;
        private const int ReferenceInnerPixels = 30;
        private const int ReferenceOuterPixels = 40;

        private readonly List<Object> _owned = new();
        private GameObject _root;
        private InstanceGameObjectBinder _binder;
        private InstanceRegistry _registry;
        private Camera _camera;
        private string _outputFolder;
        private ICoreAiLookApplier _applier;
        private Light _savedSun;

        [SetUp]
        public void CreateWorld()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null ||
                GraphicsSettings.currentRenderPipeline == null)
            {
                Assert.Ignore("These captures need a graphics device and a render pipeline.");
            }

            if (!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.DefaultHDR))
            {
                Assert.Ignore("This device has no HDR render texture format.");
            }

            _savedSun = RenderSettings.sun;
            UsePackagedCatalogOnly(true);
            RbxSpace.Configure(RbxSpace.DefaultMetersPerStud);
            _root = new GameObject("CoreAI_NoonLookWorld");
            _binder = new InstanceGameObjectBinder(_root.transform, null);
            _registry = new InstanceRegistry(null, _binder,
                worldInstanceAdapter: new WorldInstanceAdapter(_binder));
            DataModelBootstrap.CreateGame(_registry);

            GameObject cameraObject = new("NoonLookCamera");
            _owned.Add(cameraObject);
            _camera = cameraObject.AddComponent<Camera>();
            _camera.fieldOfView = 50f;
            _camera.nearClipPlane = 0.1f;
            _camera.farClipPlane = 1000f;
            _camera.aspect = (float)ShotWidth / ShotHeight;
            _camera.clearFlags = CameraClearFlags.Skybox;
            _camera.allowHDR = true;

            GameObject sunObject = new("NoonLookSun");
            _owned.Add(sunObject);
            Light sun = sunObject.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.3f;
            sun.shadows = LightShadows.Soft;
            sunObject.transform.rotation = Quaternion.Euler(62f, 35f, 0f);
            RenderSettings.sun = sun;

            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName
                                 ?? Directory.GetCurrentDirectory();
            _outputFolder = Path.Combine(projectRoot, "artifacts", "look");
            Directory.CreateDirectory(_outputFolder);
        }

        [TearDown]
        public void DestroyWorld()
        {
            _applier?.Revert();
            _applier = null;
            foreach (Object owned in _owned)
            {
                if (owned != null)
                {
                    Object.Destroy(owned);
                }
            }

            _owned.Clear();
            if (_root != null)
            {
                Object.Destroy(_root);
            }

            RenderSettings.sun = _savedSun;
            UsePackagedCatalogOnly(false);
        }

        [UnityTest]
        public IEnumerator NoonScene_WithAndWithoutTheDefaultLook()
        {
            CreatePart("Grass", 1280, new RbxVector3(160f, 1f, 160f), RbxCFrame.FromPosition(0f, -0.5f, 30f));
            CreatePart("Brick", 848, new RbxVector3(40f, 10f, 1f), RbxCFrame.FromPosition(0f, 5f, 14f));
            CreatePart("Wood", 512, new RbxVector3(4f, 4f, 4f), RbxCFrame.FromPosition(-13f, 2f, 2f));
            CreatePart("Glass", 1568, new RbxVector3(4f, 6f, 4f), RbxCFrame.FromPosition(-7f, 3f, 2f));
            RbxInstance metal = CreatePart("Metal", 1088, new RbxVector3(5f, 5f, 5f),
                RbxCFrame.FromPosition(8f, 2.5f, 4f));
            _binder.SetShape(metal.Id, RbxPartShape.Ball);
            CreatePart("Water", 2048, new RbxVector3(14f, 1f, 10f), RbxCFrame.FromPosition(13f, 0.05f, -6f));
            RbxInstance neon = CreatePart("Neon", 288, new RbxVector3(1.5f, 8f, 1.5f),
                RbxCFrame.FromPosition(0f, 4f, 8f), new RbxColor3(0.1f, 0.85f, 1f));
            AimCamera(new RbxVector3(0f, 9f, -20f), new RbxVector3(0f, 4f, 6f));
            RbxWaterTime.SetOverride(0f);
            yield return null;

            Color[] withoutLook = Shoot("noon_look_off.png");

            CoreAiLookProfile profile = CoreAiLookProfile.CreateDefault();
            _owned.Add(profile);
            _applier = CoreAiLookApplierRegistry.CreateDefault();
            _applier.Apply(profile, GraphicsOptionsResolver.Resolve(GraphicsOptions.Default,
                GraphicsQualityPreset.High), LookMode.Apply);
            yield return null;
            Color[] withLook = Shoot("noon_look_on.png");
            RbxWaterTime.ClearOverride();

            RectInt wall = ProjectedRect(new RbxVector3(-18f, 9.5f, 13.4f), new RbxVector3(-4f, 6.5f, 13.4f));
            float wallSpreadOn = LuminanceSpread(withLook, wall);
            float wallSpreadOff = LuminanceSpread(withoutLook, wall);
            RectInt sky = new(0, ShotHeight - 40, ShotWidth, 40);
            float skyChange = MeanAbsoluteDifference(withLook, withoutLook, sky);
            RectInt neonBox = ProjectedBox(neon);
            float haloOn = BandLuminance(withLook, neonBox, HaloInnerPixels, HaloOuterPixels) -
                           BandLuminance(withLook, neonBox, ReferenceInnerPixels, ReferenceOuterPixels);
            float haloOff = BandLuminance(withoutLook, neonBox, HaloInnerPixels, HaloOuterPixels) -
                            BandLuminance(withoutLook, neonBox, ReferenceInnerPixels, ReferenceOuterPixels);
            TestContext.WriteLine("[NoonLook] sky change " + skyChange + ", Neon halo over the wall with the look " +
                                  haloOn + ", without " + haloOff + ", Neon box " + neonBox +
                                  ", brick spread with the look " + wallSpreadOn + ", without " + wallSpreadOff +
                                  ", wall crop " + wall);
            Assert.Greater(wallSpreadOn, wallSpreadOff * 0.5f,
                "the look's fog must not wash the brick wall ten metres away into a flat colour");
            Assert.Greater(skyChange, 0.02f, "the look's procedural sky replaces the scene's default sky");
            Assert.Greater(haloOn, haloOff + 0.01f, "the Neon bar blooms over the wall only with the look on");
        }

        private RbxInstance CreatePart(string material, int value, RbxVector3 size, RbxCFrame cframe,
            RbxColor3? color = null)
        {
            RbxInstance part = _registry.Create("Part");
            part.Name = material;
            part.Parent = _registry.WorldRoot;
            _binder.SetAnchored(part.Id, true);
            _binder.SetSize(part.Id, size);
            _binder.SetCFrame(part.Id, cframe);
            _binder.SetMaterial(part.Id, new RbxMaterialId(material, value));
            if (color.HasValue)
            {
                _binder.SetColor(part.Id, color.Value);
            }

            return part;
        }

        private void AimCamera(RbxVector3 positionStuds, RbxVector3 targetStuds)
        {
            Vector3 position = RbxSpace.ToUnity(positionStuds);
            Vector3 target = RbxSpace.ToUnity(targetStuds);
            _camera.transform.position = position;
            _camera.transform.rotation = Quaternion.LookRotation(target - position);
        }

        /// <summary>The screen rectangle spanned by two opposite corners given in studs.</summary>
        private RectInt ProjectedRect(RbxVector3 firstStuds, RbxVector3 secondStuds)
        {
            Vector3 first = _camera.WorldToViewportPoint(RbxSpace.ToUnity(firstStuds));
            Vector3 second = _camera.WorldToViewportPoint(RbxSpace.ToUnity(secondStuds));
            int xMin = Mathf.Clamp(Mathf.RoundToInt(Mathf.Min(first.x, second.x) * ShotWidth), 0, ShotWidth - 1);
            int xMax = Mathf.Clamp(Mathf.RoundToInt(Mathf.Max(first.x, second.x) * ShotWidth), xMin + 1, ShotWidth);
            int yMin = Mathf.Clamp(Mathf.RoundToInt(Mathf.Min(first.y, second.y) * ShotHeight), 0, ShotHeight - 1);
            int yMax = Mathf.Clamp(Mathf.RoundToInt(Mathf.Max(first.y, second.y) * ShotHeight), yMin + 1,
                ShotHeight);
            return new RectInt(xMin, yMin, xMax - xMin, yMax - yMin);
        }

        /// <summary>Standard deviation of the luminance inside the crop.</summary>
        private static float LuminanceSpread(Color[] shot, RectInt crop)
        {
            double sum = 0d;
            double sumOfSquares = 0d;
            int count = 0;
            for (int y = crop.yMin; y < crop.yMax; y++)
            {
                for (int x = crop.xMin; x < crop.xMax; x++)
                {
                    Color pixel = shot[y * ShotWidth + x];
                    double luminance = 0.2126d * pixel.r + 0.7152d * pixel.g + 0.0722d * pixel.b;
                    sum += luminance;
                    sumOfSquares += luminance * luminance;
                    count++;
                }
            }

            if (count == 0)
            {
                return 0f;
            }

            double mean = sum / count;
            return (float)System.Math.Sqrt(System.Math.Max(0d, sumOfSquares / count - mean * mean));
        }

        /// <summary>The part's projected renderer bounds in shot pixels.</summary>
        private RectInt ProjectedBox(RbxInstance part)
        {
            Assert.IsTrue(_binder.TryGetBoundObject(part.Id, out GameObject bound));
            Bounds bounds = bound.GetComponentInChildren<Renderer>().bounds;
            Vector2 min = new(float.MaxValue, float.MaxValue);
            Vector2 max = new(float.MinValue, float.MinValue);
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 point = bounds.center + Vector3.Scale(bounds.extents, new Vector3(
                    (corner & 1) == 0 ? -1f : 1f, (corner & 2) == 0 ? -1f : 1f, (corner & 4) == 0 ? -1f : 1f));
                Vector3 viewport = _camera.WorldToViewportPoint(point);
                min = Vector2.Min(min, new Vector2(viewport.x * ShotWidth, viewport.y * ShotHeight));
                max = Vector2.Max(max, new Vector2(viewport.x * ShotWidth, viewport.y * ShotHeight));
            }

            int x = Mathf.Clamp(Mathf.FloorToInt(min.x), 0, ShotWidth - 1);
            int y = Mathf.Clamp(Mathf.FloorToInt(min.y), 0, ShotHeight - 1);
            int xMax = Mathf.Clamp(Mathf.CeilToInt(max.x), x + 1, ShotWidth);
            int yMax = Mathf.Clamp(Mathf.CeilToInt(max.y), y + 1, ShotHeight);
            return new RectInt(x, y, xMax - x, yMax - y);
        }

        /// <summary>
        /// Mean luminance of the bands left and right of the bar, between the inner and outer distances
        /// from its projected box; the bands stay at the bar's height so they cover the brick wall behind
        /// it, never the sky.
        /// </summary>
        private static float BandLuminance(Color[] shot, RectInt box, int inner, int outer)
        {
            double sum = 0d;
            int count = 0;
            for (int y = box.yMin; y < box.yMax; y++)
            {
                for (int offset = inner; offset < outer; offset++)
                {
                    foreach (int x in new[] { box.xMin - offset, box.xMax - 1 + offset })
                    {
                        if (x < 0 || x >= ShotWidth)
                        {
                            continue;
                        }

                        Color pixel = shot[y * ShotWidth + x];
                        sum += 0.2126f * pixel.r + 0.7152f * pixel.g + 0.0722f * pixel.b;
                        count++;
                    }
                }
            }

            return count == 0 ? 0f : (float)(sum / count);
        }

        /// <summary>Renders into a half-float target and returns the frame clamped to the display range.</summary>
        private Color[] Shoot(string fileName)
        {
            RenderTexture target = new(ShotWidth, ShotHeight, 24, RenderTextureFormat.DefaultHDR);
            Texture2D linear = new(ShotWidth, ShotHeight, TextureFormat.RGBAFloat, false, true);
            Texture2D png = new(ShotWidth, ShotHeight, TextureFormat.RGBA32, false);
            RenderTexture previousActive = RenderTexture.active;
            try
            {
                RenderPipeline.StandardRequest request = new() { destination = target };
                if (RenderPipeline.SupportsRenderRequest(_camera, request))
                {
                    RenderPipeline.SubmitRenderRequest(_camera, request);
                }
                else
                {
                    _camera.targetTexture = target;
                    _camera.Render();
                    _camera.targetTexture = null;
                }

                RenderTexture.active = target;
                linear.ReadPixels(new Rect(0, 0, ShotWidth, ShotHeight), 0, 0);
                linear.Apply();
                Color[] pixels = linear.GetPixels();
                Color[] display = new Color[pixels.Length];
                for (int index = 0; index < pixels.Length; index++)
                {
                    Color pixel = pixels[index];
                    display[index] = new Color(Mathf.Clamp01(pixel.r), Mathf.Clamp01(pixel.g),
                        Mathf.Clamp01(pixel.b), 1f).gamma;
                }

                png.SetPixels(display);
                png.Apply();
                File.WriteAllBytes(Path.Combine(_outputFolder, fileName), png.EncodeToPNG());
                return display;
            }
            finally
            {
                RenderTexture.active = previousActive;
                Object.Destroy(target);
                Object.Destroy(linear);
                Object.Destroy(png);
            }
        }

        private static float MeanAbsoluteDifference(Color[] first, Color[] second, RectInt crop)
        {
            double sum = 0d;
            int count = 0;
            for (int y = crop.yMin; y < crop.yMax; y++)
            {
                for (int x = crop.xMin; x < crop.xMax; x++)
                {
                    int index = y * ShotWidth + x;
                    sum += Mathf.Abs(first[index].r - second[index].r) + Mathf.Abs(first[index].g - second[index].g)
                           + Mathf.Abs(first[index].b - second[index].b);
                    count++;
                }
            }

            return count == 0 ? 0f : (float)(sum / (count * 3d));
        }

        private static void UsePackagedCatalogOnly(bool enabled)
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            PropertyInfo ignoreOverride = typeof(RbxTextureMaterialProvider).GetProperty(
                "IgnoreProjectOverrideForTests", flags);
            MethodInfo resetCache = typeof(RbxTextureMaterialProvider).GetMethod(
                "ResetSharedCacheForTests", flags);
            Assert.IsNotNull(ignoreOverride, "RbxTextureMaterialProvider lost its override test seam");
            Assert.IsNotNull(resetCache, "RbxTextureMaterialProvider lost its cache reset test seam");
            ignoreOverride.SetValue(null, enabled);
            resetCache.Invoke(null, null);
        }
    }
}
