using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using CoreAI.Look;
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
    /// A grass hillside of one-cell terraces and a three-cell cliff under the default look, drawn three ways in one
    /// run: every face grass (the plain rules, the look before), grass blocks without the band, and grass blocks
    /// with it (the default). Frames from the side and from above, sunlit and shaded, go to
    /// <c>artifacts/grassblock/</c>; the crops are the riser of a terrace (its band and the face below it) and a
    /// terrace top: the riser turns to earth below a green band, the tops stay grass.
    /// </summary>
    public sealed class TerrainGrassBlockCapturePlayModeTests
    {
        private const int ShotWidth = 640;
        private const int ShotHeight = 360;

        private readonly List<Object> _owned = new();
        private readonly StringBuilder _report = new();
        private RbxTerrain _terrain;
        private RbxTerrainPresenter _presenter;
        private Camera _camera;
        private Light _sun;
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
            GameObject host = new("Grass block host");
            _owned.Add(host);
            _presenter = host.AddComponent<RbxTerrainPresenter>();
            GameObject cameraObject = new("Grass block camera");
            _owned.Add(cameraObject);
            _camera = cameraObject.AddComponent<Camera>();
            _camera.fieldOfView = 55f;
            _camera.nearClipPlane = 0.1f;
            _camera.farClipPlane = 1000f;
            _camera.clearFlags = CameraClearFlags.Skybox;
            _camera.allowHDR = true;
            _camera.aspect = (float)ShotWidth / ShotHeight;
            GameObject sunObject = new("Grass block sun");
            _owned.Add(sunObject);
            _sun = sunObject.AddComponent<Light>();
            _sun.type = LightType.Directional;
            _sun.intensity = 1.2f;
            _sun.shadows = LightShadows.Soft;
            RenderSettings.sun = _sun;
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Directory.GetCurrentDirectory();
            _outputFolder = Path.Combine(projectRoot, "artifacts", "grassblock");
            Directory.CreateDirectory(_outputFolder);
        }

        [TearDown]
        public void DestroyWorld()
        {
            _presenter?.Bind(null);
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
        public IEnumerator AGrassHillside_ShowsEarthOnItsRisers_UnderAGreenBand_AndGrassOnTop()
        {
            byte grass = Slot("Grass");
            byte ground = Slot("Ground");
            Fill(-24, -4, -8, 64, 4, 16, ground);
            for (int step = 0; step < 6; step++)
            {
                Fill(step * 4, 0, -8, 4, step + 1, 16, ground);
                Fill(step * 4, step, -8, 4, 1, 16, grass);
            }

            Fill(-24, -1, -8, 24, 1, 16, grass);
            Fill(-12, 0, -8, 4, 3, 16, ground);
            Fill(-12, 3, -8, 4, 1, 16, grass);
            CoreAiLookProfile profile = CoreAiLookProfile.CreateDefault();
            _owned.Add(profile);
            _applier = CoreAiLookApplierRegistry.CreateDefault();
            _applier.Apply(profile, GraphicsOptionsResolver.Resolve(GraphicsOptions.Default, GraphicsQualityPreset.High),
                LookMode.Apply);

            string[] labels = { "all_grass", "no_band", "band" };
            TerrainFaceMaterialRules[] rules =
            {
                TerrainFaceMaterialRules.None,
                TerrainFaceMaterialRules.None.With(grass, new TerrainFaceMaterialRule(grass, ground, ground, 0)),
                TerrainFaceMaterialRules.BlockWorld
            };
            float[,] riserGreen = new float[3, 2];
            float[,] bandGreen = new float[3, 2];
            float[,] topGreen = new float[3, 2];
            for (int look = 0; look < 3; look++)
            {
                TerrainFaceMaterialRules chosen = rules[look];
                _presenter.Bind(null);
                _presenter.RendererFactory = (terrain, parent) =>
                    new RbxTerrainRenderer(terrain, parent, mesher: new BlockyTerrainChunkMesher(chosen));
                _presenter.Bind(_terrain);
                while (_presenter.Renderer.PendingChunkCount > 0)
                {
                    _presenter.Pump();
                }

                yield return null;
                for (int light = 0; light < 2; light++)
                {
                    bool sunlit = light == 0;
                    _sun.transform.rotation = sunlit ? Quaternion.Euler(35f, 100f, 0f) : Quaternion.Euler(35f, -80f, 0f);
                    Aim(new RbxVector3(-6f, 14f, 30f), new RbxVector3(34f, 6f, 0f));
                    string name = labels[look] + (sunlit ? "_sunlit" : "_shaded");
                    Color[] side = Shoot(name + "_side.png");
                    // The riser of terrace 2 faces -X at x = 8 cells, one cell tall from y = 2 to 3 cells.
                    riserGreen[look, light] = Green(side, Rect(new RbxVector3(32f, 8.4f, 6f), new RbxVector3(32f, 10.8f, -6f)));
                    bandGreen[look, light] = Green(side, Rect(new RbxVector3(32f, 11.5f, 6f), new RbxVector3(32f, 11.9f, -6f)));
                    topGreen[look, light] = Green(side, Rect(new RbxVector3(34f, 12f, 6f), new RbxVector3(46f, 12f, -6f)));
                    Aim(new RbxVector3(-10f, 44f, 26f), new RbxVector3(16f, 4f, 0f));
                    Shoot(name + "_above.png");
                    _report.AppendLine(name + ": riser below the band green " + F(riserGreen[look, light]) + ", band "
                                       + F(bandGreen[look, light]) + ", terrace top " + F(topGreen[look, light]));
                }
            }

            File.WriteAllText(Path.Combine(_outputFolder, "grassblock_numbers.txt"), _report.ToString());
            TestContext.WriteLine(_report.ToString());
            for (int light = 0; light < 2; light++)
            {
                Assert.Less(riserGreen[2, light], riserGreen[0, light] * 0.3f, "the riser was grass and is earth now");
                Assert.Greater(bandGreen[2, light], riserGreen[2, light] + (light == 0 ? 0.02f : 0.005f),
                    "the band is greener than the earth below it");
                Assert.Less(Mathf.Abs(topGreen[2, light] - topGreen[0, light]), 0.01f, "the terrace tops stay grass");
                Assert.Less(Mathf.Abs(riserGreen[1, light] - riserGreen[2, light]), 0.01f, "the band changes only the band");
            }
        }

        private void Fill(int x, int y, int z, int sizeX, int sizeY, int sizeZ, byte material)
        {
            _terrain.Store.FillBox(new TerrainCellBox(x, y, z, sizeX, sizeY, sizeZ), material, TerrainOccupancy.Full);
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

        private RectInt Rect(RbxVector3 firstStuds, RbxVector3 secondStuds)
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
                linear.ReadPixels(new UnityEngine.Rect(0, 0, ShotWidth, ShotHeight), 0, 0);
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

        /// <summary>How green a crop reads: the green channel above the mean of red and blue, averaged.</summary>
        private static float Green(Color[] shot, RectInt crop)
        {
            double sum = 0d;
            int count = 0;
            for (int y = crop.yMin; y < crop.yMax; y++)
            {
                for (int x = crop.xMin; x < crop.xMax; x++)
                {
                    Color pixel = shot[y * ShotWidth + x];
                    sum += pixel.g - (pixel.r + pixel.b) * 0.5f;
                    count++;
                }
            }

            return count == 0 ? 0f : (float)(sum / count);
        }

        private static string F(float value)
        {
            return value.ToString("0.0000", CultureInfo.InvariantCulture);
        }

        private static void UsePackagedCatalogOnly(bool enabled)
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            typeof(RbxTextureMaterialProvider).GetProperty("IgnoreProjectOverrideForTests", flags)?.SetValue(null, enabled);
            typeof(RbxTextureMaterialProvider).GetMethod("ResetSharedCacheForTests", flags)?.Invoke(null, null);
        }
    }
}
