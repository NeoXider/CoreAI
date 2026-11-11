using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
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
    /// Rendered proof for Materials v2, stage 1 (roadmap P8). Every check compares an object-masked crop
    /// (the part's projected renderer bounds, shrunk away from its edges) between two frames that differ in
    /// exactly one thing, never a whole-frame mean. Each frame is also written to
    /// <c>artifacts/materials/v2/</c> for a person to look at.
    /// </summary>
    public sealed class RbxMaterialsV2CapturePlayModeTests
    {
        private const int ShotWidth = 640;
        private const int ShotHeight = 360;
        private const float CropInset = 0.2f;

        private readonly List<Object> _ownedObjects = new();
        private GameObject _root;
        private InstanceGameObjectBinder _binder;
        private InstanceRegistry _registry;
        private Camera _camera;
        private Light _sun;
        private string _outputFolder;
        private AmbientMode _savedAmbientMode;
        private Color _savedAmbientLight;
        private float _savedReflectionIntensity;
        private Light _savedSun;

        [SetUp]
        public void CreateWorld()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null ||
                GraphicsSettings.currentRenderPipeline == null)
            {
                Assert.Ignore("These captures need a graphics device and the URP pipeline.");
            }

            _savedAmbientMode = RenderSettings.ambientMode;
            _savedAmbientLight = RenderSettings.ambientLight;
            _savedReflectionIntensity = RenderSettings.reflectionIntensity;
            _savedSun = RenderSettings.sun;
            UsePackagedCatalogOnly(true);
            RbxSpace.Configure(RbxSpace.DefaultMetersPerStud);
            _root = new GameObject("CoreAI_MaterialsV2World");
            _binder = new InstanceGameObjectBinder(_root.transform, null);
            _registry = new InstanceRegistry(null, _binder,
                worldInstanceAdapter: new WorldInstanceAdapter(_binder));
            DataModelBootstrap.CreateGame(_registry);

            GameObject cameraObject = new("MaterialsV2Camera");
            _ownedObjects.Add(cameraObject);
            _camera = cameraObject.AddComponent<Camera>();
            _camera.fieldOfView = 40f;
            _camera.nearClipPlane = 0.1f;
            _camera.farClipPlane = 500f;
            _camera.aspect = (float)ShotWidth / ShotHeight;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = new Color(0.35f, 0.45f, 0.6f);

            GameObject sunObject = new("MaterialsV2Sun");
            _ownedObjects.Add(sunObject);
            _sun = sunObject.AddComponent<Light>();
            _sun.type = LightType.Directional;
            _sun.intensity = 1.2f;
            _sun.shadows = LightShadows.Soft;
            sunObject.transform.rotation = Quaternion.Euler(40f, 210f, 0f);
            RenderSettings.sun = _sun;

            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName
                                 ?? Directory.GetCurrentDirectory();
            _outputFolder = Path.Combine(projectRoot, "artifacts", "materials", "v2");
            Directory.CreateDirectory(_outputFolder);
        }

        [TearDown]
        public void DestroyWorld()
        {
            RbxWaterTime.ClearOverride();
            foreach (Object owned in _ownedObjects)
            {
                if (owned != null)
                {
                    Object.Destroy(owned);
                }
            }

            _ownedObjects.Clear();
            if (_root != null)
            {
                Object.Destroy(_root);
            }

            RenderSettings.ambientMode = _savedAmbientMode;
            RenderSettings.ambientLight = _savedAmbientLight;
            RenderSettings.reflectionIntensity = _savedReflectionIntensity;
            RenderSettings.sun = _savedSun;
            UsePackagedCatalogOnly(false);
        }

        [UnityTest]
        public IEnumerator Water_MovesBetweenTwoPinnedTimes_AndAPlasticSlabDoesNot()
        {
            RbxInstance water = CreatePart("Water", 2048, new RbxVector3(24f, 1f, 24f),
                RbxCFrame.FromPosition(-14f, 0f, 0f));
            RbxInstance plastic = CreatePart("Plastic", 256, new RbxVector3(24f, 1f, 24f),
                RbxCFrame.FromPosition(14f, 0f, 0f));
            AimCamera(new RbxVector3(0f, 28f, -34f), RbxVector3.Zero);
            yield return null;

            RbxWaterTime.SetOverride(0f);
            Color32[] before = Shoot("water_t0.png");
            RbxWaterTime.SetOverride(1f);
            Color32[] after = Shoot("water_t1.png");

            float waterChange = MeanAbsoluteDifference(before, after, Crop(water));
            float plasticChange = MeanAbsoluteDifference(before, after, Crop(plastic));
            Assert.Greater(waterChange, 0.004f, "the water must move between t=0 s and t=1 s");
            Assert.Less(plasticChange, 0.001f, "negative twin: a Plastic slab in the same frames is still");
        }

        [UnityTest]
        public IEnumerator WaterAndGlacier_NoLongerShareOneLook()
        {
            RbxInstance water = CreatePart("Water", 2048, new RbxVector3(16f, 1f, 16f),
                RbxCFrame.FromPosition(-9f, 0f, 0f));
            RbxInstance glacier = CreatePart("Glacier", 1552, new RbxVector3(16f, 1f, 16f),
                RbxCFrame.FromPosition(9f, 0f, 0f));
            AimCamera(new RbxVector3(0f, 24f, -28f), RbxVector3.Zero);
            yield return null;

            RbxWaterTime.SetOverride(0f);
            Color32[] shot = Shoot("water_vs_glacier.png");

            Color waterMean = MeanColor(shot, Crop(water));
            Color glacierMean = MeanColor(shot, Crop(glacier));
            float distance = Mathf.Abs(waterMean.r - glacierMean.r) + Mathf.Abs(waterMean.g - glacierMean.g)
                             + Mathf.Abs(waterMean.b - glacierMean.b);
            Assert.Greater(distance, 0.05f, "before v2, Water rendered with Glacier's ice-fracture mode");
        }

        [UnityTest]
        public IEnumerator CrackedLava_GlowsWithoutLight_AndBasaltGoesDark()
        {
            RbxInstance lava = CreatePart("CrackedLava", 804, new RbxVector3(10f, 10f, 1f),
                RbxCFrame.FromPosition(-6f, 5f, 0f));
            RbxInstance basalt = CreatePart("Basalt", 788, new RbxVector3(10f, 10f, 1f),
                RbxCFrame.FromPosition(6f, 5f, 0f));
            AimCamera(new RbxVector3(0f, 5f, -20f), new RbxVector3(0f, 5f, 0f));
            _sun.intensity = 0f;
            _camera.backgroundColor = Color.black;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = Color.black;
            // WHY: "no light at all" includes the sky's specular reflection. The scene keeps the default
            // skybox as its reflection source, and at intensity 1 it lit unlit Basalt to 0.063 in the crop
            // (0.006 linear); at 0 the same crop reads exactly 0 and the lava still glows at 0.17.
            RenderSettings.reflectionIntensity = 0f;
            yield return null;

            Color32[] shot = Shoot("lava_dark.png");

            float lavaLuminance = Luminance(MeanColor(shot, Crop(lava)));
            float basaltLuminance = Luminance(MeanColor(shot, Crop(basalt)));
            Assert.Greater(lavaLuminance, 0.08f, "CrackedLava must glow with no light at all");
            Assert.Less(basaltLuminance, 0.03f, "negative twin: unlit Basalt is dark");
        }

        [UnityTest]
        public IEnumerator HalfTransparentBrick_ShowsWhatIsBehindIt_AndOpaqueBrickDoesNot()
        {
            RbxInstance panel = CreatePart("Brick", 848, new RbxVector3(10f, 10f, 0.5f),
                RbxCFrame.FromPosition(0f, 5f, 0f));
            RbxInstance behind = CreatePart("Neon", 288, new RbxVector3(6f, 6f, 0.5f),
                RbxCFrame.FromPosition(0f, 5f, 4f), new RbxColor3(1f, 0.1f, 0.1f));
            AimCamera(new RbxVector3(0f, 5f, -16f), new RbxVector3(0f, 5f, 0f));
            yield return null;

            _binder.SetTransparency(panel.Id, 0.5f);
            Color32[] halfWith = Shoot("brick_half_with.png");
            _binder.SetTransparency(behind.Id, 1f);
            Color32[] halfWithout = Shoot("brick_half_without.png");
            _binder.SetTransparency(behind.Id, 0f);
            _binder.SetTransparency(panel.Id, 0f);
            Color32[] opaqueWith = Shoot("brick_opaque_with.png");
            _binder.SetTransparency(behind.Id, 1f);
            Color32[] opaqueWithout = Shoot("brick_opaque_without.png");

            RectInt crop = Crop(panel);
            Assert.Greater(MeanAbsoluteDifference(halfWith, halfWithout, crop), 0.03f,
                "at Transparency 0.5 the red panel behind must show through");
            Assert.Less(MeanAbsoluteDifference(opaqueWith, opaqueWithout, crop), 0.002f,
                "negative twin: an opaque brick hides it");
        }

        [UnityTest]
        public IEnumerator Glass_BendsTheSceneBehindIt_OnTheSceneTextureTier()
        {
            if (!RbxSceneTextures.Available)
            {
                Assert.Ignore("This platform renders Glass without scene textures.");
            }

            for (int index = 0; index < 8; index++)
            {
                RbxColor3 stripe = index % 2 == 0 ? new RbxColor3(1f, 1f, 1f) : new RbxColor3(0.05f, 0.05f, 0.05f);
                CreatePart("Neon", 288, new RbxVector3(1f, 12f, 0.5f),
                    RbxCFrame.FromPosition(-3.5f + index, 6f, 6f), stripe);
            }

            RbxInstance glass = CreatePart("Glass", 1568, new RbxVector3(6f, 6f, 6f),
                RbxCFrame.FromPosition(0f, 6f, 0f));
            _binder.SetShape(glass.Id, RbxPartShape.Ball);
            AimCamera(new RbxVector3(0f, 6f, -14f), new RbxVector3(0f, 6f, 0f));
            yield return null;

            Material glassMaterial = RendererOf(glass).sharedMaterial;
            Color32[] refracted = Shoot("glass_refraction_on.png");
            glassMaterial.SetFloat("_SceneRefraction", 0f);
            Color32[] blended = Shoot("glass_refraction_off.png");
            glassMaterial.SetFloat("_SceneRefraction", 1f);

            Assert.Greater(MeanAbsoluteDifference(refracted, blended, Crop(glass)), 0.02f,
                "refraction must bend the stripes behind the ball");
        }

        [UnityTest]
        public IEnumerator PlasticSurfaceDetail_ChangesTheShading_UnderAGrazingLight()
        {
            RbxInstance plastic = CreatePart("Plastic", 256, new RbxVector3(20f, 1f, 20f),
                RbxCFrame.FromPosition(0f, 0f, 0f));
            _sun.transform.rotation = Quaternion.Euler(12f, 180f, 0f);
            // WHY this pose: it is the one the first captures used (Unity (0, 4, -6) looking at (0, 0, 3)),
            // now written in studs; the grazing sun comes towards the camera, which is where the detail
            // reads. Seen from the other side, 16 studs back, the same detail moved the crop by 0.0019.
            AimCamera(new RbxVector3(0f, 14.29f, 21.43f), new RbxVector3(0f, 0f, -10.71f));
            yield return null;

            Material plasticMaterial = RendererOf(plastic).sharedMaterial;
            float detail = plasticMaterial.GetFloat("_SurfaceDetail");
            Assert.Greater(detail, 0f, "Plastic ships with surface detail");
            Color32[] withDetail = Shoot("plastic_detail_on.png");
            plasticMaterial.SetFloat("_SurfaceDetail", 0f);
            Color32[] withoutDetail = Shoot("plastic_detail_off.png");
            plasticMaterial.SetFloat("_SurfaceDetail", detail);

            Assert.Greater(MeanAbsoluteDifference(withDetail, withoutDetail, Crop(plastic)), 0.002f,
                "the surface detail must change the lit Plastic, not leave the frame byte-identical");
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

        private Renderer RendererOf(RbxInstance part)
        {
            Assert.IsTrue(_binder.TryGetBoundObject(part.Id, out GameObject bound));
            Renderer renderer = bound.GetComponentInChildren<Renderer>();
            Assert.IsNotNull(renderer, part.Name + " has no renderer");
            return renderer;
        }

        /// <summary>
        /// Places the camera in studs, the unit every part of these scenes is authored in. WHY through
        /// <see cref="RbxSpace"/>: a raw Unity position mixed metres with studs and mirrored Z, which put the
        /// "behind" Neon panel in front of the brick and the stripes in front of the Glass ball.
        /// </summary>
        private void AimCamera(RbxVector3 positionStuds, RbxVector3 targetStuds)
        {
            Vector3 position = RbxSpace.ToUnity(positionStuds);
            Vector3 target = RbxSpace.ToUnity(targetStuds);
            _camera.transform.position = position;
            _camera.transform.rotation = Quaternion.LookRotation(target - position);
        }

        /// <summary>The part's projected renderer bounds in shot pixels, shrunk away from its edges.</summary>
        private RectInt Crop(RbxInstance part)
        {
            Bounds bounds = RendererOf(part).bounds;
            Vector2 min = new(float.MaxValue, float.MaxValue);
            Vector2 max = new(float.MinValue, float.MinValue);
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 point = bounds.center + Vector3.Scale(bounds.extents, new Vector3(
                    (corner & 1) == 0 ? -1f : 1f, (corner & 2) == 0 ? -1f : 1f, (corner & 4) == 0 ? -1f : 1f));
                Vector3 viewport = _camera.WorldToViewportPoint(point);
                Assert.Greater(viewport.z, _camera.nearClipPlane, part.Name +
                    " reaches behind the camera, where its projected crop is meaningless; frame the whole part");
                min = Vector2.Min(min, new Vector2(viewport.x, viewport.y));
                max = Vector2.Max(max, new Vector2(viewport.x, viewport.y));
            }

            Vector2 inset = (max - min) * CropInset;
            min = Vector2.Max(min + inset, Vector2.zero);
            max = Vector2.Min(max - inset, Vector2.one);
            int x = Mathf.RoundToInt(min.x * ShotWidth);
            int y = Mathf.RoundToInt(min.y * ShotHeight);
            int width = Mathf.Max(1, Mathf.RoundToInt((max.x - min.x) * ShotWidth));
            int height = Mathf.Max(1, Mathf.RoundToInt((max.y - min.y) * ShotHeight));
            return new RectInt(x, y, Mathf.Min(width, ShotWidth - x), Mathf.Min(height, ShotHeight - y));
        }

        private Color32[] Shoot(string fileName)
        {
            RenderTexture target = new(ShotWidth, ShotHeight, 24, RenderTextureFormat.ARGB32);
            Texture2D image = new(ShotWidth, ShotHeight, TextureFormat.RGBA32, false);
            RenderTexture previousActive = RenderTexture.active;
            try
            {
                // WHY: Camera.Render() bypasses the URP light loop; the SRP request is the supported path.
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
                image.ReadPixels(new Rect(0, 0, ShotWidth, ShotHeight), 0, 0);
                image.Apply();
                File.WriteAllBytes(Path.Combine(_outputFolder, fileName), image.EncodeToPNG());
                return image.GetPixels32();
            }
            finally
            {
                RenderTexture.active = previousActive;
                Object.Destroy(target);
                Object.Destroy(image);
            }
        }

        private static float MeanAbsoluteDifference(Color32[] first, Color32[] second, RectInt crop)
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

            return count == 0 ? 0f : (float)(sum / (count * 3d * 255d));
        }

        private static Color MeanColor(Color32[] shot, RectInt crop)
        {
            double r = 0d;
            double g = 0d;
            double b = 0d;
            int count = 0;
            for (int y = crop.yMin; y < crop.yMax; y++)
            {
                for (int x = crop.xMin; x < crop.xMax; x++)
                {
                    Color32 pixel = shot[y * ShotWidth + x];
                    r += pixel.r;
                    g += pixel.g;
                    b += pixel.b;
                    count++;
                }
            }

            return count == 0
                ? Color.black
                : new Color((float)(r / count / 255d), (float)(g / count / 255d), (float)(b / count / 255d));
        }

        private static float Luminance(Color color)
        {
            return 0.2126f * color.r + 0.7152f * color.g + 0.0722f * color.b;
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
