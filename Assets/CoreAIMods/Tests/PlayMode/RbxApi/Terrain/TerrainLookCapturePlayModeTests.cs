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

        /// <summary>Rows of the grazing sea shot that hold only water: the horizon sits near row 175.</summary>
        private const int SeaRows = 165;

        /// <summary>The Grass roughness scale before the fix, kept as the glossy reference.</summary>
        private const float GlossyGrassRoughnessScale = 0.78f;

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
            RbxTerrainPresenter presenter = _host.AddComponent<RbxTerrainPresenter>();
            // WHY the plain face rules: the block comparison below sets a LeafyGrass side face against a Grass
            // side face to check the two materials' albedo, which the grass-block rule would draw as earth.
            presenter.RendererFactory = (terrain, parent) => new RbxTerrainRenderer(terrain, parent,
                mesher: new BlockyTerrainChunkMesher(TerrainFaceMaterialRules.None));
            presenter.Bind(_terrain);

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
                                  ", leaves roughness scale " + leaves.GetFloat("_RoughnessScale") +
                                  ", leaves material colour " + leaves.GetColor("_MaterialColor") + ", water bump " +
                                  water.GetFloat("_BumpStrength") + ", water smoothness " + water.GetFloat("_Smoothness"));

            // (a) the field against a low sun in front, as shipped and with the roughness scale it had before.
            RenderSettings.sun.transform.rotation = Quaternion.Euler(15f, 170f, 0f);
            Field("shipped", out float nearShimmer, out float farShimmer, out float nearDetail, out float farDetail);
            float shippedScale = grass.GetFloat("_RoughnessScale");
            grass.SetFloat("_RoughnessScale", GlossyGrassRoughnessScale);
            Field("glossy", out float nearShimmerGlossy, out float farShimmerGlossy, out float nearDetailGlossy,
                out float farDetailGlossy);
            grass.SetFloat("_RoughnessScale", shippedScale);
            TestContext.WriteLine("[TerrainLook] field shimmer near " + nearShimmer + " (glossy " + nearShimmerGlossy +
                                  "), far " + farShimmer + " (glossy " + farShimmerGlossy + "); high-frequency near " +
                                  nearDetail + " (glossy " + nearDetailGlossy + "), far " + farDetail + " (glossy " +
                                  farDetailGlossy + ")");

            // (b) the blocks with the sun behind the camera, so the faces that look at it are sunlit.
            RenderSettings.sun.transform.rotation = Quaternion.Euler(25f, 15f, 0f);
            Aim(new RbxVector3(0f, 6f, 8f), new RbxVector3(0f, 2f, -40f));
            RectInt leafFace = ProjectedRect(new RbxVector3(-15f, 1.5f, -19.9f), new RbxVector3(-9f, 6.5f, -19.9f));
            RectInt grassFace = ProjectedRect(new RbxVector3(9f, 1.5f, -19.9f), new RbxVector3(15f, 6.5f, -19.9f));
            Color[] blocks = Shoot("terrain_blocks_sunlit.png");
            RenderSettings.sun.transform.rotation = Quaternion.Euler(25f, 195f, 0f);
            Color[] shaded = Shoot("terrain_blocks_shaded.png");
            float sunlitLeaves = Luminance(Mean(blocks, leafFace));
            float sunlitGrass = Luminance(Mean(blocks, grassFace));
            float shadedLeaves = Luminance(Mean(shaded, leafFace));
            float shadedGrass = Luminance(Mean(shaded, grassFace));
            TestContext.WriteLine("[TerrainLook] sunlit faces: leaves " + sunlitLeaves + " grass " + sunlitGrass +
                                  " | shaded faces: leaves " + shadedLeaves + " grass " + shadedGrass + " | crops " +
                                  leafFace + " " + grassFace);

            // (c) the sea over a sand floor from a grazing angle; then the sky's ground hemisphere turned magenta,
            // which only a reflection ray pointing below the horizon can pick up.
            RenderSettings.sun.transform.rotation = Quaternion.Euler(18f, 120f, 0f);
            Aim(new RbxVector3(0f, 2f, -230f), new RbxVector3(0f, 0f, -450f));
            RectInt sea = new(0, 0, ShotWidth, SeaRows);
            RectInt farSea = new(0, SeaRows - 30, ShotWidth, 30);
            Color[] seaShot = Shoot("terrain_sea_grazing.png");
            float bump = water.GetFloat("_BumpStrength");
            water.SetFloat("_BumpStrength", 0f);
            Color[] flatSea = Shoot("terrain_sea_grazing_flat.png");
            water.SetFloat("_BumpStrength", bump);
            Color groundColor = profile.SkyGroundColor;
            profile.SkyGroundColor = new Color(1f, 0f, 1f, 1f);
            _applier.Apply(profile, GraphicsOptionsResolver.Resolve(GraphicsOptions.Default, GraphicsQualityPreset.High),
                LookMode.Apply);
            for (int frame = 0; frame < 4; frame++)
            {
                yield return null;
            }

            Color[] magentaGround = Shoot("terrain_sea_grazing_magenta_ground.png");
            profile.SkyGroundColor = groundColor;
            RbxWaterTime.ClearOverride();
            float notBlue = Share(seaShot, sea, IsDullGreyOrBrown);
            float notBlueFar = Share(seaShot, farSea, IsDullGreyOrBrown);
            float magenta = Share(magentaGround, sea, pixel => pixel.r - pixel.g > 0.1f);
            TestContext.WriteLine("[TerrainLook] sea grey-or-brown share " + notBlue + " (far band " + notBlueFar +
                                  "), mean " + Mean(seaShot, sea) + " | flat water mean " + Mean(flatSea, sea) +
                                  " | magenta share with a magenta sky ground " + magenta);

            Assert.Less(nearShimmer, nearShimmerGlossy * 0.6f,
                "grass as shipped glitters less when the camera moves than the glossy grass it replaced");
            Assert.Less(nearShimmer, 0.015f, "a near lawn under a low sun barely changes when the camera moves");
            // WHY these ratios: before the LeafyGrass gain they were 0.74 sunlit and 0.62 in shade.
            Assert.Greater(sunlitLeaves, sunlitGrass * 0.85f, "sunlit LeafyGrass is about as bright as the lawn");
            Assert.Greater(shadedLeaves, shadedGrass * 0.75f, "LeafyGrass in shade is not black next to the grass");
            Assert.Less(magenta, 0.005f, "no wave reflects the sky's ground hemisphere");
            Assert.Less(notBlue, 0.01f, "a sea seen from a grazing angle stays blue, without grey or brown blotches");
        }

        /// <summary>
        /// The lawn of (a) drawn with this project's own texture catalog, the git-ignored override a developer
        /// imports locally, so a machine whose override still carries the old glossy tuning is caught. Ignored
        /// where the project has no override catalog.
        /// </summary>
        [UnityTest]
        public IEnumerator GrassField_WithTheProjectCatalog_DoesNotGlitterUnderALowSun()
        {
            if (Resources.Load<RbxMaterialTextureCatalog>("CoreAIRbxTextureCatalogOverride") == null)
            {
                Assert.Ignore("This project has no override texture catalog.");
            }

            UsePackagedCatalogOnly(false);
            _terrain.FillBlock(RbxCFrame.FromPosition(0f, -2f, -100f), new RbxVector3(400f, 4f, 240f), Slot("Grass"));
            CoreAiLookProfile profile = CoreAiLookProfile.CreateDefault();
            _owned.Add(profile);
            _applier = CoreAiLookApplierRegistry.CreateDefault();
            _applier.Apply(profile, GraphicsOptionsResolver.Resolve(GraphicsOptions.Default, GraphicsQualityPreset.High),
                LookMode.Apply);
            for (int frame = 0; frame < 600 && !HasMaterial("Grass"); frame++)
            {
                yield return null;
            }

            for (int frame = 0; frame < 30; frame++)
            {
                yield return null;
            }

            Material grass = FindMaterial("Grass");
            RenderSettings.sun.transform.rotation = Quaternion.Euler(15f, 170f, 0f);
            Field("project", out float nearShimmer, out float farShimmer, out float nearDetail, out float farDetail);
            TestContext.WriteLine("[TerrainLook] project catalog grass: roughness scale " +
                                  grass.GetFloat("_RoughnessScale") + ", normal strength " + grass.GetFloat("_BumpScale") +
                                  ", colour map " + (grass.GetTexture("_BaseMap") != null
                                      ? grass.GetTexture("_BaseMap").name + " " + grass.GetTexture("_BaseMap").width
                                      : "none") + "; shimmer near " + nearShimmer + ", far " + farShimmer +
                                  "; high-frequency near " + nearDetail + ", far " + farDetail);
            Assert.Less(nearShimmer, 0.015f, "a near lawn under a low sun barely changes when the camera moves");
        }

        /// <summary>
        /// The sea of (c) under an open sky, with the packaged catalog and with this project's own, each shot with
        /// the sky exposure the Terrain draws with and with it switched off: an open sea is fully exposed, so it
        /// mirrors the sky either way, stays blue at a grazing angle, and the two shots agree.
        /// </summary>
        [UnityTest]
        public IEnumerator AnOpenSea_ReflectsTheSky_WithEitherCatalog_WithAndWithoutTheSkyExposure()
        {
            _terrain.FillBlock(RbxCFrame.FromPosition(0f, -2f, -340f), new RbxVector3(400f, 4f, 240f),
                TerrainMaterials.Water);
            _terrain.FillBlock(RbxCFrame.FromPosition(0f, -6f, -340f), new RbxVector3(400f, 4f, 240f), Slot("Sand"));
            CoreAiLookProfile profile = CoreAiLookProfile.CreateDefault();
            _owned.Add(profile);
            _applier = CoreAiLookApplierRegistry.CreateDefault();
            _applier.Apply(profile, GraphicsOptionsResolver.Resolve(GraphicsOptions.Default, GraphicsQualityPreset.High),
                LookMode.Apply);
            RbxWaterTime.SetOverride(0f);
            RenderSettings.sun.transform.rotation = Quaternion.Euler(18f, 120f, 0f);
            bool hasOverride = Resources.Load<RbxMaterialTextureCatalog>("CoreAIRbxTextureCatalogOverride") != null;
            List<string> lines = new();
            foreach (bool packaged in new[] { true, false })
            {
                if (!packaged && !hasOverride)
                {
                    lines.Add("project catalog: none in this project");
                    continue;
                }

                UsePackagedCatalogOnly(packaged);
                RbxTerrainPresenter presenter = _host.GetComponent<RbxTerrainPresenter>();
                presenter.Bind(null);
                presenter.Bind(_terrain);
                for (int frame = 0; frame < 600 && !HasMaterial("Water"); frame++)
                {
                    yield return null;
                }

                // WHY thirty frames, as in the field test: the look's sky reflection probe renders over several
                // frames, and a sea shot before it has finished mirrors an unfinished probe.
                for (int frame = 0; frame < 30; frame++)
                {
                    yield return null;
                }

                string label = packaged ? "packaged" : "project";
                Material water = FindMaterial("Water");
                Assert.AreEqual(1f, water.GetFloat(RbxTerrainRenderer.SkyExposureSourceProperty),
                    "the sea draws with the Terrain's exposure on");
                foreach (Color32 vertex in WaterMeshColours())
                {
                    Assert.AreEqual(255, vertex.a, "every vertex of a sea under the open sky is fully exposed");
                }

                Aim(new RbxVector3(0f, 2f, -230f), new RbxVector3(0f, 0f, -450f));
                RectInt sea = new(0, 0, ShotWidth, SeaRows);
                Color[] withExposure = Shoot("terrain_sea_" + label + "_exposure_on.png");
                water.SetFloat(RbxTerrainRenderer.SkyExposureSourceProperty, 0f);
                Color[] withoutExposure = Shoot("terrain_sea_" + label + "_exposure_off.png");
                water.SetFloat(RbxTerrainRenderer.SkyExposureSourceProperty, 1f);
                Color on = Mean(withExposure, sea);
                Color off = Mean(withoutExposure, sea);
                float dull = Share(withExposure, sea, IsDullGreyOrBrown);
                lines.Add(label + ": sea mean with the exposure " + on + ", without " + off + ", grey-or-brown share "
                          + dull);
                Assert.Greater(Luminance(on), 0.25f, label + ": the open sea mirrors the bright sky");
                Assert.Less(dull, 0.01f, label + ": the open sea stays blue at a grazing angle");
                Assert.Less(Mathf.Abs(Luminance(on) - Luminance(off)), 0.01f,
                    label + ": a fully exposed sea looks the same with the exposure term and without it");
            }

            RbxWaterTime.ClearOverride();
            TestContext.WriteLine("[TerrainLook] " + string.Join(" | ", lines));
        }

        private IEnumerable<Color32> WaterMeshColours()
        {
            foreach (MeshFilter filter in _host.GetComponentsInChildren<MeshFilter>())
            {
                if (!filter.gameObject.name.StartsWith("Water", System.StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (Color32 colour in filter.sharedMesh.colors32)
                {
                    yield return colour;
                }
            }
        }

        [UnityTest]
        public IEnumerator AChunkOfThreeMaterials_DrawsEachWithItsOwn_WithTheProjectCatalog()
        {
            UsePackagedCatalogOnly(false);
            yield return ChunkMaterialsAgainstSingleMaterialChunks("project");
        }

        [UnityTest]
        public IEnumerator AChunkOfThreeMaterials_DrawsEachWithItsOwn_WithThePackagedCatalog()
        {
            yield return ChunkMaterialsAgainstSingleMaterialChunks("packaged");
        }

        /// <summary>
        /// Lays WoodPlanks, Grass and Sand side by side in one chunk and the same three strips each alone in
        /// a chunk of its own, then compares every strip of the shared chunk with its single-material twin
        /// from straight above.
        /// </summary>
        private IEnumerator ChunkMaterialsAgainstSingleMaterialChunks(string label)
        {
            string[] names = { "WoodPlanks", "Grass", "Sand" };
            float[] sharedLeft = { 4f, 24f, 44f };
            float[] aloneLeft = { 84f, 148f, 212f };
            for (int index = 0; index < names.Length; index++)
            {
                _terrain.FillBlock(RbxCFrame.FromPosition(sharedLeft[index] + 8f, 2f, 32f),
                    new RbxVector3(16f, 4f, 56f), Slot(names[index]));
                _terrain.FillBlock(RbxCFrame.FromPosition(aloneLeft[index] + 8f, 2f, 32f),
                    new RbxVector3(16f, 4f, 56f), Slot(names[index]));
            }

            for (int frame = 0; frame < 600 && CountChunkRenderers() < 4; frame++)
            {
                yield return null;
            }

            yield return null;
            RenderSettings.sun.transform.rotation = Quaternion.Euler(60f, 30f, 0f);
            _camera.orthographic = true;
            _camera.orthographicSize = (float)(256d / _camera.aspect * RbxSpace.MetersPerStud * 0.5d);
            Aim(new RbxVector3(128f, 60f, 32f), new RbxVector3(128f, 0f, 32.001f));
            _camera.transform.rotation = Quaternion.LookRotation(Vector3.down, Vector3.forward);
            Color[] shot = Shoot("terrain_chunk_materials_" + label + ".png");
            List<string> lines = new();
            float[] distances = new float[names.Length];
            for (int index = 0; index < names.Length; index++)
            {
                Color shared = Mean(shot, StripCrop(sharedLeft[index]));
                Color alone = Mean(shot, StripCrop(aloneLeft[index]));
                distances[index] = Mathf.Abs(shared.r - alone.r) + Mathf.Abs(shared.g - alone.g) +
                                   Mathf.Abs(shared.b - alone.b);
                lines.Add(names[index] + " shared " + shared + " alone " + alone + " distance " + distances[index]);
            }

            TestContext.WriteLine("[TerrainLook] chunk materials (" + label + "): " + string.Join(" | ", lines));
            for (int index = 0; index < names.Length; index++)
            {
                Assert.Less(distances[index], 0.08f, names[index] + " in a chunk shared with other materials must " +
                                                     "draw like it does alone (" + label + " catalog)");
            }

            Color woodAlone = Mean(shot, StripCrop(aloneLeft[0]));
            Color grassAlone = Mean(shot, StripCrop(aloneLeft[1]));
            Assert.Greater(Mathf.Abs(woodAlone.r - grassAlone.r) + Mathf.Abs(woodAlone.g - grassAlone.g) +
                           Mathf.Abs(woodAlone.b - grassAlone.b), 0.1f, "WoodPlanks and Grass are told apart");
        }

        private RectInt StripCrop(float leftStuds)
        {
            return ProjectedRect(new RbxVector3(leftStuds + 3f, 4f, 10f), new RbxVector3(leftStuds + 13f, 4f, 54f));
        }

        private int CountChunkRenderers()
        {
            int count = 0;
            foreach (MeshRenderer chunk in _host.GetComponentsInChildren<MeshRenderer>())
            {
                if (chunk.enabled && chunk.sharedMaterials.Length > 0)
                {
                    count++;
                }
            }

            return count;
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

        /// <summary>A pixel that is neither blue nor bright: the brown-grey blotches of a sea mirroring the sky's ground.</summary>
        private static bool IsDullGreyOrBrown(Color pixel)
        {
            return pixel.b - pixel.r < 0.08f && Luminance(pixel) < 0.5f;
        }

        private static float Share(Color[] shot, RectInt crop, System.Func<Color, bool> predicate)
        {
            int hits = 0;
            int count = 0;
            for (int y = crop.yMin; y < crop.yMax; y++)
            {
                for (int x = crop.xMin; x < crop.xMax; x++)
                {
                    count++;
                    if (predicate(shot[y * ShotWidth + x]))
                    {
                        hits++;
                    }
                }
            }

            return count == 0 ? 0f : (float)hits / count;
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
