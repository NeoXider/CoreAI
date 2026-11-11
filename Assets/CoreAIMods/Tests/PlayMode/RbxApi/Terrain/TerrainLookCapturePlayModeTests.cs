using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using CoreAI.Look;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Binding.Terrain;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Terrain;
using CoreAI.Mods.Rbx.Rendering;
using CoreAI.Mods.Rbx.Spatial;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode.RbxApi
{
    /// <summary>
    /// A voxel Terrain scene under the default look and a low sun: a grass field running to the horizon, a sea
    /// behind it, and LeafyGrass and Grass blocks side by side. The frames go to <c>artifacts/terrain/</c>, and
    /// the measurements are masked crops: the far grass and the near water seen twice with the camera moved by a
    /// fraction of a pixel (what a viewer sees as shimmer and glitter), the sunlit faces of a LeafyGrass block
    /// against a Grass block, and the colour of the sea at a grazing angle.
    /// </summary>
    public sealed class TerrainLookCapturePlayModeTests
    {
        private const int ShotWidth = 640;
        private const int ShotHeight = 360;

        private readonly List<Object> _owned = new();
        private GameObject _host;
        private RbxTerrain _terrain;
        private Camera _camera;
        private ICoreAiLookApplier _applier;
        private Light _savedSun;
        private string _outputFolder;

        [SetUp]
        public void CreateWorld()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null || GraphicsSettings.currentRenderPipeline == null
                || !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.DefaultHDR))
            {
                Assert.Ignore("These captures need a graphics device, a render pipeline and an HDR target.");
            }

            _savedSun = RenderSettings.sun;
            UsePackagedCatalogOnly(true);
            RbxSpace.Configure(RbxSpace.DefaultMetersPerStud);
            ClassCatalog catalog = ClassCatalog.CreateMvp1();
            if (!catalog.TryGet(RbxTerrain.ClassNameValue, out ClassDescriptor _))
            {
                catalog.Register(RbxTerrain.CreateDescriptor());
            }

            InstanceRegistry registry = new(catalog);
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            _terrain = RbxTerrainBootstrap.EnsureTerrain(registry, game.FindFirstChildOfClass("Workspace"),
                TerrainLimits.Desktop);
            _host = new GameObject("Terrain look host");
            _owned.Add(_host);
            RbxTerrainPresenter.Attach(_host, game);

            GameObject cameraObject = new("Terrain look camera");
            _owned.Add(cameraObject);
            _camera = cameraObject.AddComponent<Camera>();
            _camera.fieldOfView = 60f;
            _camera.nearClipPlane = 0.1f;
            _camera.farClipPlane = 1000f;
            _camera.clearFlags = CameraClearFlags.Skybox;
            _camera.allowHDR = true;
            _camera.aspect = (float)ShotWidth / ShotHeight;

            GameObject sunObject = new("Terrain look sun");
            _owned.Add(sunObject);
            Light sun = sunObject.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.2f;
            sun.shadows = LightShadows.Soft;
            sunObject.transform.rotation = Quaternion.Euler(18f, 120f, 0f);
            RenderSettings.sun = sun;

            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Directory.GetCurrentDirectory();
            _outputFolder = Path.Combine(projectRoot, "artifacts", "terrain");
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
            RenderSettings.sun = _savedSun;
            UsePackagedCatalogOnly(false);
        }

        [UnityTest]
        public IEnumerator GrassFieldSeaAndLeaves_UnderTheDefaultLook()
        {
            _terrain.FillBlock(RbxCFrame.FromPosition(0f, -2f, -100f), new RbxVector3(400f, 4f, 240f), Slot("Grass"));
            _terrain.FillBlock(RbxCFrame.FromPosition(0f, -2f, -340f), new RbxVector3(400f, 4f, 240f),
                TerrainMaterials.Water);
            _terrain.FillBlock(RbxCFrame.FromPosition(0f, -6f, -340f), new RbxVector3(400f, 4f, 240f), Slot("Sand"));
            _terrain.FillBlock(RbxCFrame.FromPosition(-12f, 4f, -24f), new RbxVector3(8f, 8f, 8f), Slot("LeafyGrass"));
            _terrain.FillBlock(RbxCFrame.FromPosition(12f, 4f, -24f), new RbxVector3(8f, 8f, 8f), Slot("Grass"));
            CoreAiLookProfile profile = CoreAiLookProfile.CreateDefault();
            _owned.Add(profile);
            _applier = CoreAiLookApplierRegistry.CreateDefault();
            _applier.Apply(profile, GraphicsOptionsResolver.Resolve(GraphicsOptions.Default, GraphicsQualityPreset.High),
                LookMode.Apply);
            RbxWaterTime.SetOverride(0f);
            for (int frame = 0; frame < 600 && !(HasMaterial("LeafyGrass") && HasMaterial("Water")
                                                  && HasMaterial("Sand")); frame++)
            {
                yield return null;
            }

            for (int frame = 0; frame < 30; frame++)
            {
                yield return null;
            }

            Material grass = FindMaterial("Grass");
            Material leaves = FindMaterial("LeafyGrass");
            Material water = FindMaterial("Water");
            TestContext.WriteLine("[TerrainLook] grass roughness scale " + grass.GetFloat("_RoughnessScale") +
                                  ", leaves material colour " + leaves.GetColor("_MaterialColor") + ", water bump " +
                                  water.GetFloat("_BumpStrength") + ", water smoothness " + water.GetFloat("_Smoothness"));

            // (a) the field against a low sun in front: glints and shimmer.
            RenderSettings.sun.transform.rotation = Quaternion.Euler(15f, 170f, 0f);
            Field("default", out float nearShimmer, out float farShimmer, out float nearDetail, out float farDetail);
            float roughnessScale = grass.GetFloat("_RoughnessScale");
            grass.SetFloat("_RoughnessScale", 3.2f);
            Field("rougher", out float nearShimmerRough, out float farShimmerRough, out float nearDetailRough,
                out float farDetailRough);
            grass.SetFloat("_RoughnessScale", roughnessScale);
            TestContext.WriteLine("[TerrainLook] field shimmer near " + nearShimmer + " -> " + nearShimmerRough + ", far " +
                                  farShimmer + " -> " + farShimmerRough + "; high-frequency near " + nearDetail + " -> " +
                                  nearDetailRough + ", far " + farDetail + " -> " + farDetailRough);

            // (b) the blocks with the sun behind the camera, so the faces that look at it are sunlit.
            RenderSettings.sun.transform.rotation = Quaternion.Euler(25f, 15f, 0f);
            Aim(new RbxVector3(0f, 6f, 8f), new RbxVector3(0f, 2f, -40f));
            RectInt leafFace = ProjectedRect(new RbxVector3(-15f, 1.5f, -19.9f), new RbxVector3(-9f, 6.5f, -19.9f));
            RectInt grassFace = ProjectedRect(new RbxVector3(9f, 1.5f, -19.9f), new RbxVector3(15f, 6.5f, -19.9f));
            Color[] blocks = Shoot("terrain_blocks_default.png");
            Color leafColor = leaves.GetColor("_MaterialColor");
            leaves.SetColor("_MaterialColor", leafColor * 1.6f);
            Color[] blocksBright = Shoot("terrain_blocks_leaves_x1.6.png");
            leaves.SetColor("_MaterialColor", leafColor);
            TestContext.WriteLine("[TerrainLook] sunlit faces: leaves " + Luminance(Mean(blocks, leafFace)) + " grass " +
                                  Luminance(Mean(blocks, grassFace)) + " | leaves x1.6 " +
                                  Luminance(Mean(blocksBright, leafFace)) + " | crops " + leafFace + " " + grassFace);
            RenderSettings.sun.transform.rotation = Quaternion.Euler(25f, 195f, 0f);
            Color[] shaded = Shoot("terrain_blocks_shaded.png");
            TestContext.WriteLine("[TerrainLook] shaded faces: leaves " + Luminance(Mean(shaded, leafFace)) + " grass " +
                                  Luminance(Mean(shaded, grassFace)));

            // (c) the sea over a sand floor from a grazing angle, with and without the wave normals.
            RenderSettings.sun.transform.rotation = Quaternion.Euler(18f, 120f, 0f);
            Aim(new RbxVector3(0f, 2f, -230f), new RbxVector3(0f, 0f, -450f));
            RectInt farSea = new(0, ShotHeight / 2 - 40, ShotWidth, 30);
            RectInt nearSea = new(0, 0, ShotWidth, 60);
            Color[] sea = Shoot("terrain_sea_grazing.png");
            float bump = water.GetFloat("_BumpStrength");
            water.SetFloat("_BumpStrength", 0f);
            Color[] flatSea = Shoot("terrain_sea_grazing_flat.png");
            water.SetFloat("_BumpStrength", bump);
            TestContext.WriteLine("[TerrainLook] sea far band " + Mean(sea, farSea) + " near band " + Mean(sea, nearSea) +
                                  " | flat water far " + Mean(flatSea, farSea) + " near " + Mean(flatSea, nearSea));
            RbxWaterTime.ClearOverride();
            Assert.Greater(Luminance(Mean(sea, nearSea)), 0.02f, "the sea rendered");
        }

        private void Field(string label, out float nearShimmer, out float farShimmer, out float nearDetail,
            out float farDetail)
        {
            Aim(new RbxVector3(0f, 6f, 8f), new RbxVector3(0f, 0f, -150f));
            RectInt far = ProjectedRect(new RbxVector3(-60f, 0f, -120f), new RbxVector3(60f, 0f, -200f));
            RectInt near = ProjectedRect(new RbxVector3(-10f, 0f, -2f), new RbxVector3(10f, 0f, -12f));
            Color[] first = Shoot("terrain_field_" + label + ".png");
            Aim(new RbxVector3(0.02f, 6f, 8f), new RbxVector3(0.02f, 0f, -150f));
            Color[] shifted = Shoot("terrain_field_" + label + "_shifted.png");
            nearShimmer = MeanAbsoluteDifference(first, shifted, near);
            farShimmer = MeanAbsoluteDifference(first, shifted, far);
            nearDetail = HighFrequency(first, near);
            farDetail = HighFrequency(first, far);
        }

        private bool HasMaterial(string name)
        {
            foreach (Renderer chunk in _host.GetComponentsInChildren<Renderer>())
            {
                foreach (Material material in chunk.sharedMaterials)
                {
                    if (material != null && material.name.EndsWith("_" + name))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private Material FindMaterial(string name)
        {
            foreach (Renderer chunk in _host.GetComponentsInChildren<Renderer>())
            {
                foreach (Material material in chunk.sharedMaterials)
                {
                    if (material != null && material.name.EndsWith("_" + name))
                    {
                        return material;
                    }
                }
            }

            Assert.Fail("no terrain chunk draws " + name);
            return null;
        }

        /// <summary>Mean absolute difference between each pixel's luminance and its 3x3 neighbourhood's.</summary>
        private static float HighFrequency(Color[] shot, RectInt crop)
        {
            double sum = 0d;
            int count = 0;
            for (int y = Mathf.Max(crop.yMin, 1); y < Mathf.Min(crop.yMax, ShotHeight - 1); y++)
            {
                for (int x = Mathf.Max(crop.xMin, 1); x < Mathf.Min(crop.xMax, ShotWidth - 1); x++)
                {
                    float local = 0f;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            local += Luminance(shot[(y + dy) * ShotWidth + x + dx]);
                        }
                    }

                    sum += Mathf.Abs(Luminance(shot[y * ShotWidth + x]) - local / 9f);
                    count++;
                }
            }

            return count == 0 ? 0f : (float)(sum / count);
        }

        private static byte Slot(string name)
        {
            Assert.IsTrue(TerrainMaterials.TryGetSlot(name, out byte slot), name + " is a terrain material");
            return slot;
        }

        private void Aim(RbxVector3 positionStuds, RbxVector3 targetStuds)
        {
            Vector3 position = RbxSpace.ToUnity(positionStuds);
            Vector3 target = RbxSpace.ToUnity(targetStuds);
            _camera.transform.position = position;
            _camera.transform.rotation = Quaternion.LookRotation(target - position);
        }

        private RectInt ProjectedRect(RbxVector3 firstStuds, RbxVector3 secondStuds)
        {
            Vector3 first = _camera.WorldToViewportPoint(RbxSpace.ToUnity(firstStuds));
            Vector3 second = _camera.WorldToViewportPoint(RbxSpace.ToUnity(secondStuds));
            int xMin = Mathf.Clamp(Mathf.RoundToInt(Mathf.Min(first.x, second.x) * ShotWidth), 0, ShotWidth - 1);
            int xMax = Mathf.Clamp(Mathf.RoundToInt(Mathf.Max(first.x, second.x) * ShotWidth), xMin + 1, ShotWidth);
            int yMin = Mathf.Clamp(Mathf.RoundToInt(Mathf.Min(first.y, second.y) * ShotHeight), 0, ShotHeight - 1);
            int yMax = Mathf.Clamp(Mathf.RoundToInt(Mathf.Max(first.y, second.y) * ShotHeight), yMin + 1, ShotHeight);
            return new RectInt(xMin, yMin, xMax - xMin, yMax - yMin);
        }

        private Color[] Shoot(string fileName)
        {
            RenderTexture target = new(ShotWidth, ShotHeight, 24, RenderTextureFormat.DefaultHDR);
            Texture2D linear = new(ShotWidth, ShotHeight, TextureFormat.RGBAFloat, false, true);
            Texture2D png = new(ShotWidth, ShotHeight, TextureFormat.RGBA32, false);
            RenderTexture previousActive = RenderTexture.active;
            try
            {
                RenderPipeline.SubmitRenderRequest(_camera, new RenderPipeline.StandardRequest { destination = target });
                RenderTexture.active = target;
                linear.ReadPixels(new Rect(0, 0, ShotWidth, ShotHeight), 0, 0);
                linear.Apply();
                Color[] pixels = linear.GetPixels();
                for (int index = 0; index < pixels.Length; index++)
                {
                    Color pixel = pixels[index];
                    pixels[index] = new Color(Mathf.Clamp01(pixel.r), Mathf.Clamp01(pixel.g), Mathf.Clamp01(pixel.b), 1f).gamma;
                }

                png.SetPixels(pixels);
                png.Apply();
                File.WriteAllBytes(Path.Combine(_outputFolder, fileName), png.EncodeToPNG());
                return pixels;
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
                    sum += Mathf.Abs(Luminance(first[index]) - Luminance(second[index]));
                    count++;
                }
            }

            return count == 0 ? 0f : (float)(sum / count);
        }

        private static Color Mean(Color[] shot, RectInt crop)
        {
            double r = 0d;
            double g = 0d;
            double b = 0d;
            int count = 0;
            for (int y = crop.yMin; y < crop.yMax; y++)
            {
                for (int x = crop.xMin; x < crop.xMax; x++)
                {
                    Color pixel = shot[y * ShotWidth + x];
                    r += pixel.r;
                    g += pixel.g;
                    b += pixel.b;
                    count++;
                }
            }

            return count == 0 ? Color.black : new Color((float)(r / count), (float)(g / count), (float)(b / count));
        }

        private static float Luminance(Color color)
        {
            return 0.2126f * color.r + 0.7152f * color.g + 0.0722f * color.b;
        }

        private static void UsePackagedCatalogOnly(bool enabled)
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            typeof(RbxTextureMaterialProvider).GetProperty("IgnoreProjectOverrideForTests", flags)?.SetValue(null, enabled);
            typeof(RbxTextureMaterialProvider).GetMethod("ResetSharedCacheForTests", flags)?.Invoke(null, null);
        }
    }
}
