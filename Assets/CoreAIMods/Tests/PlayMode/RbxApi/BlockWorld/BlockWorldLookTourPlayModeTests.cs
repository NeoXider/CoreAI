#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Composition;
using CoreAI.Infrastructure;
using CoreAI.Infrastructure.Llm;
using CoreAI.Look;
using CoreAI.Mods.Rbx.Binding.Terrain;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Terrain;
using CoreAI.Mods.Rbx.Spatial;
using CoreAI.Mods.WorldPackages;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using VContainer;
using Object = UnityEngine.Object;

namespace CoreAI.Tests.PlayMode.RbxApi.BlockWorld
{
    /// <summary>
    /// The Block World scene as a player opens it, photographed for the look: the first view, a wall of each hotbar
    /// block at 2, 10 and 40 studs (front and back, one of them sunlit), the nearest tree, the nearest shore, an
    /// overview of the hills and the horizon at 10:00, each drawn twice in one run, once with the material catalog's
    /// photographed sets (the look before block tiles) and once with the painted blocks, plus a sky-only frame of the
    /// horizon with the haze-following ground on and off. Frames and numbers go to <c>artifacts/blocklook/</c>; the
    /// asserts are the claims the owner can see: the lawn is greener, the leaves have dark gaps, the sky below the
    /// horizon continues the haze (twin: the old brown floor), and a Cobblestone Part looks the same in both looks.
    /// </summary>
    /// <remarks>
    /// WHY the composed camera posed by hand and rendered on request: the frame then has the scene's own
    /// post-processing and exposure, while the camera mods never see the pose; the pose is put back after each shot.
    /// </remarks>
    [TestFixture]
    public sealed class BlockWorldLookTourPlayModeTests
    {
        private const string ScenePath = "Assets/CoreAI.Demos/BlockWorld/BlockWorld.unity";
        private const float Cell = 4f;
        private const int ShotWidth = 960;
        private const int ShotHeight = 540;
        private const double TourClock = 10d;

        private static readonly string[] HotbarBlocks =
            { "Grass", "Ground", "Rock", "Cobblestone", "WoodPlanks", "Brick", "Sand", "LeafyGrass" };

        private readonly StringBuilder _report = new();
        private CoreAiPlayWorldBootstrap _bootstrap;
        private IDisposable _storageOverride;
        private string _storageRoot;
        private string _outputFolder;
        private float _savedCaptureDeltaTime;
        private CoreAISettingsAsset _settings;
        private string _settingsSnapshot;
        private Scene _previousActive;
        private Scene _blockWorld;
        private Material _savedSkybox;
        private Light _savedSun;
        private bool _savedFog;
        private Color _savedFogColor;

        [SetUp]
        public void SetUp()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null || GraphicsSettings.currentRenderPipeline == null
                || !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.DefaultHDR))
            {
                Assert.Ignore("These captures need a graphics device, a render pipeline and an HDR target.");
            }

            _report.Clear();
            _savedCaptureDeltaTime = Time.captureDeltaTime;
            Time.captureDeltaTime = 1f / 60f;
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Directory.GetCurrentDirectory();
            _outputFolder = Path.Combine(projectRoot, "artifacts", "blocklook");
            Directory.CreateDirectory(_outputFolder);
            // WHY a short root: the store nests the store id and the mod id under it, and Windows refuses long paths.
            _storageRoot = Path.Combine(Path.GetTempPath(), "cai-bl", Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_storageRoot);
            _storageOverride = CoreAiStorageRoot.Override(_storageRoot);
            _settings = CoreAISettingsAsset.Instance;
            Assert.IsNotNull(_settings, "the shared Resources/CoreAISettings asset exists");
            _settingsSnapshot = EditorJsonUtility.ToJson(_settings);
            _settings.ConfigureOffline();
            EditorUtility.ClearDirty(_settings);
            _previousActive = SceneManager.GetActiveScene();
            _savedSkybox = RenderSettings.skybox;
            _savedSun = RenderSettings.sun;
            _savedFog = RenderSettings.fog;
            _savedFogColor = RenderSettings.fogColor;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_blockWorld.IsValid() && _blockWorld.isLoaded)
            {
                foreach (GameObject root in _blockWorld.GetRootGameObjects())
                {
                    root.SetActive(false);
                }

                if (_previousActive.IsValid() && _previousActive.isLoaded)
                {
                    SceneManager.SetActiveScene(_previousActive);
                }

                yield return SceneManager.UnloadSceneAsync(_blockWorld);
            }

            _blockWorld = default;
            RenderSettings.skybox = _savedSkybox;
            RenderSettings.sun = _savedSun;
            RenderSettings.fog = _savedFog;
            RenderSettings.fogColor = _savedFogColor;
            for (int frame = 0; frame < 5; frame++)
            {
                yield return null;
            }

            Time.captureDeltaTime = _savedCaptureDeltaTime;
            _storageOverride?.Dispose();
            _storageOverride = null;
            if (_settings != null && _settingsSnapshot != null)
            {
                EditorJsonUtility.FromJsonOverwrite(_settingsSnapshot, _settings);
                EditorUtility.ClearDirty(_settings);
            }

            if (Directory.Exists(_storageRoot) && (File.GetAttributes(_storageRoot) & FileAttributes.ReparsePoint) == 0)
            {
                try
                {
                    Directory.Delete(_storageRoot, true);
                }
                catch (IOException)
                {
                }
            }

            Assert.AreEqual(_previousActive, SceneManager.GetActiveScene(),
                "the tour leaves the scene that was active before it active");
        }

        [UnityTest]
        [Timeout(1200000)]
        public IEnumerator BlockWorld_PhotographedWithPhotoSetsAndWithPaintedBlocks()
        {
            yield return OpenScene();
            RbxTerrain terrain = RbxTerrainBootstrap.Find(Workspace());
            Assert.IsNotNull(terrain, "the world has a Terrain");
            RbxTerrainPresenter presenter = Object.FindAnyObjectByType<RbxTerrainPresenter>();
            Assert.IsNotNull(presenter, "the scene draws the Terrain through a presenter");
            Camera camera = _bootstrap.Composition.Camera;
            Assert.IsNotNull(camera, "the play world has a camera");
            Pose firstView = new(camera.transform.position, camera.transform.rotation);

            RbxVector3 spawn = SpawnPosition();
            int spawnX = Mathf.FloorToInt(spawn.X / Cell);
            int spawnZ = Mathf.FloorToInt(spawn.Z / Cell);
            int floor = ColumnTop(terrain, spawnX, spawnZ + 10);
            int wallX0 = spawnX - 16;
            int wallZ = spawnZ + 10;
            BuildWalls(terrain, wallX0, floor, wallZ);
            RunAsPlayer("test_look_part", "local p = Instance.new('Part') p.Name = 'LookTourCobble' "
                                          + "p.Material = Enum.Material.Cobblestone p.Size = Vector3.new(4, 4, 4) "
                                          + "p.Anchored = true p.Position = Vector3.new("
                                          + Lua((wallX0 + 34.5f) * Cell) + ", " + Lua((floor + 1.5f) * Cell) + ", "
                                          + Lua((wallZ + 0.5f) * Cell) + ") p.Parent = workspace");
            Vector3Int tree = FindNearest(terrain, spawnX, spawnZ, "LeafyGrass", 48);
            Vector3Int shore = FindNearest(terrain, spawnX, spawnZ, "Water", 64);
            Record("spawn " + spawn + ", floor row " + floor + ", walls from cell x " + wallX0 + " at z " + wallZ
                   + ", tree " + tree + ", shore " + shore);

            Dictionary<string, Color[]> photo = new();
            Dictionary<string, Color[]> blocks = new();
            CoreAiLookProfile profile = LookProfile();
            for (int pass = 0; pass < 2; pass++)
            {
                bool painted = pass == 1;
                string look = painted ? "blocks" : "photo";
                TerrainBlockLook blockLook = painted ? TerrainBlockLook.Blocks : TerrainBlockLook.PhotoSets;
                presenter.Bind(null);
                presenter.RendererFactory = (bound, parent) => new RbxTerrainRenderer(bound, parent, blockLook: blockLook);
                presenter.Bind(terrain);
                // WHY all at once: the rebuilt chunks carry the colliders the character stands on.
                while (presenter.Renderer.PendingChunkCount > 0)
                {
                    presenter.Renderer.ProcessDirty(int.MaxValue, double.MaxValue);
                }

                if (profile != null)
                {
                    profile.SkyGroundFollowsHaze = painted;
                    profile.FogFollowsHaze = painted;
                }

                yield return SetClock(TourClock);
                Dictionary<string, Color[]> shots = painted ? blocks : photo;
                Record(look + ": materials of the chunk under the walls: " + ChunkMaterials(presenter, wallX0, floor, wallZ));
                RbxVector3 wallCentre = new((wallX0 + 16f) * Cell, (floor + 2.5f) * Cell, wallZ * Cell);
                RbxVector3 lawn = new((wallX0 + 16f) * Cell, (floor + 1f) * Cell, (wallZ - 8f) * Cell);
                shots["lawn"] = ShootAt(camera, lawn + new RbxVector3(0f, 10f, -8f), lawn, look + "_lawn.png");
                RbxVector3 part = new((wallX0 + 34.5f) * Cell, (floor + 1.5f) * Cell, (wallZ + 0.5f) * Cell);
                shots["part"] = ShootAt(camera, part + new RbxVector3(0f, 1f, -9f), part, look + "_part.png");
                foreach ((string name, float distance, float lift) in new[]
                         {
                             ("walls_10_front", 10f, 2f), ("walls_10_back", -11f, 2f), ("walls_40", 40f, 14f)
                         })
                {
                    RbxVector3 from = wallCentre + new RbxVector3(0f, lift, distance < 0f ? distance - 4f : -distance);
                    shots[name] = ShootAt(camera, from, wallCentre, look + "_" + name + ".png");
                }

                for (int index = 0; index < HotbarBlocks.Length; index++)
                {
                    RbxVector3 face = new((wallX0 + 1.5f + index * 4f) * Cell, (floor + 2.5f) * Cell, wallZ * Cell);
                    shots["wall_2_" + HotbarBlocks[index]] = ShootAt(camera, face + new RbxVector3(0f, 0.5f, -2f - 6f),
                        face, look + "_wall_2_" + HotbarBlocks[index] + ".png");
                }

                if (tree.x != int.MinValue)
                {
                    RbxVector3 canopy = new((tree.x + 0.5f) * Cell, (tree.y + 0.5f) * Cell, (tree.z + 0.5f) * Cell);
                    shots["tree"] = ShootAt(camera, canopy + new RbxVector3(-14f, 4f, -14f), canopy, look + "_tree.png");
                }

                if (shore.x != int.MinValue)
                {
                    RbxVector3 water = new((shore.x + 0.5f) * Cell, (shore.y + 1f) * Cell, (shore.z + 0.5f) * Cell);
                    shots["shore"] = ShootAt(camera, water + new RbxVector3(-28f, 22f, -28f), water, look + "_shore.png");
                }

                RbxVector3 high = new(spawn.X, (floor + 12f) * Cell, spawn.Z);
                shots["hills"] = ShootAt(camera, high, high + new RbxVector3(80f, -40f, 40f), look + "_hills.png");
                shots["horizon"] = ShootAt(camera, high, high + new RbxVector3(100f, -4f, 0f), look + "_horizon.png");
                int savedMask = camera.cullingMask;
                camera.cullingMask = 0;
                shots["sky_only"] = ShootAt(camera, high, high + new RbxVector3(100f, -4f, 0f), look + "_sky_only.png");
                camera.cullingMask = savedMask;
                yield return SetClock(17.5d);
                shots["dusk_first_view"] = Shoot(camera, firstView.position, firstView.rotation,
                    look + "_dusk_first_view.png");
                yield return SetClock(TourClock);
                // WHY last in the pass: every chunk the view needs has been drawn for a while by now.
                shots["first_view"] = Shoot(camera, firstView.position, firstView.rotation, look + "_first_view.png");
                Record(look + ": materials of the chunk under the walls at the end: "
                       + ChunkMaterials(presenter, wallX0, floor, wallZ));
            }

            if (profile != null)
            {
                profile.SkyGroundFollowsHaze = true;
                profile.FogFollowsHaze = true;
            }

            presenter.Bind(null);
            presenter.RendererFactory = null;
            presenter.Bind(terrain);
            while (presenter.Renderer.PendingChunkCount > 0)
            {
                presenter.Renderer.ProcessDirty(int.MaxValue, double.MaxValue);
            }

            Measure(photo, blocks, camera, wallX0, floor, wallZ, tree);
        }

        private void Measure(Dictionary<string, Color[]> photo, Dictionary<string, Color[]> blocks, Camera camera,
            int wallX0, int floor, int wallZ, Vector3Int tree)
        {
            // The masked crops are taken from the 2-stud shots, where the block face fills the middle of the frame.
            RectInt middle = new(ShotWidth / 2 - 120, ShotHeight / 2 - 120, 240, 240);
            Dictionary<string, float> numbers = new();
            foreach (string block in HotbarBlocks)
            {
                Color before = Mean(photo["wall_2_" + block], middle);
                Color after = Mean(blocks["wall_2_" + block], middle);
                float structureBefore = BlockStructure(photo["wall_2_" + block], middle);
                float structureAfter = BlockStructure(blocks["wall_2_" + block], middle);
                Record(block + " at 2 studs: photo " + Describe(before) + " structure " + F(structureBefore)
                       + " | blocks " + Describe(after) + " structure " + F(structureAfter));
                numbers[block + "_saturation_before"] = Saturation(before);
                numbers[block + "_saturation_after"] = Saturation(after);
            }

            float leavesDarkBefore = DarkShare(photo["wall_2_LeafyGrass"], middle);
            float leavesDarkAfter = DarkShare(blocks["wall_2_LeafyGrass"], middle);
            Record("leaves: share of dark gaps photo " + F(leavesDarkBefore) + ", blocks " + F(leavesDarkAfter));
            Color grassBefore = Mean(photo["lawn"], middle);
            Color grassAfter = Mean(blocks["lawn"], middle);
            float greenBefore = grassBefore.g - (grassBefore.r + grassBefore.b) * 0.5f;
            float greenAfter = grassAfter.g - (grassAfter.r + grassAfter.b) * 0.5f;

            // The sky-only frame: the band just below the horizon against the band just above it.
            int horizonRow = HorizonRow(camera);
            RectInt above = new(0, Mathf.Clamp(horizonRow + 8, 0, ShotHeight - 12), ShotWidth, 10);
            RectInt below = new(0, Mathf.Clamp(horizonRow - 40, 0, ShotHeight - 12), ShotWidth, 10);
            float floorGapBefore = Distance(Mean(photo["sky_only"], above), Mean(photo["sky_only"], below));
            float floorGapAfter = Distance(Mean(blocks["sky_only"], above), Mean(blocks["sky_only"], below));
            Record("sky below the horizon (row " + horizonRow + "): photo look above " + Describe(Mean(photo["sky_only"], above))
                   + " below " + Describe(Mean(photo["sky_only"], below)) + " gap " + F(floorGapBefore) + " | haze look above "
                   + Describe(Mean(blocks["sky_only"], above)) + " below " + Describe(Mean(blocks["sky_only"], below))
                   + " gap " + F(floorGapAfter));

            // The Part's own frame: its 4-stud face fills the middle of it.
            RectInt partCrop = new(ShotWidth / 2 - 60, ShotHeight / 2 - 60, 120, 120);
            float partChange = Distance(Mean(photo["part"], partCrop), Mean(blocks["part"], partCrop));
            Record("lawn green margin photo " + F(greenBefore) + ", blocks " + F(greenAfter) + "; Cobblestone Part crop "
                   + partCrop + " changed by " + F(partChange) + " between the looks");
            File.WriteAllText(Path.Combine(_outputFolder, "blocklook_numbers.txt"), _report.ToString());
            TestContext.WriteLine(_report.ToString());

            Assert.Greater(greenAfter, greenBefore, "the painted lawn is greener than the photographed one");
            Assert.Greater(leavesDarkAfter, leavesDarkBefore + 0.05f, "a leaf block shows dark gaps between its leaves");
            Assert.Less(floorGapAfter, 0.1f, "below the horizon the sky continues the haze");
            Assert.Greater(floorGapBefore, floorGapAfter + 0.05f, "where the fixed ground colour drew a darker floor");
            Assert.Less(partChange, 0.03f, "a Cobblestone Part looks the same in both looks");
        }

        // ---- the world --------------------------------------------------------------------------------------------

        private void BuildWalls(RbxTerrain terrain, int x0, int floor, int z)
        {
            TerrainMaterials.TryGetSlot("Grass", out byte grass);
            TerrainMaterials.TryGetSlot("Ground", out byte ground);
            terrain.Store.FillBox(new TerrainCellBox(x0 - 2, floor - 3, z - 12, 40, 3, 24), ground, TerrainOccupancy.Full);
            terrain.Store.FillBox(new TerrainCellBox(x0 - 2, floor, z - 12, 40, 1, 24), grass, TerrainOccupancy.Full);
            terrain.Store.FillBox(new TerrainCellBox(x0 - 2, floor + 1, z - 12, 40, 14, 24), TerrainMaterials.Air, 0);
            for (int index = 0; index < HotbarBlocks.Length; index++)
            {
                TerrainMaterials.TryGetSlot(HotbarBlocks[index], out byte material);
                terrain.Store.FillBox(new TerrainCellBox(x0 + index * 4, floor + 1, z, 3, 3, 1), material,
                    TerrainOccupancy.Full);
            }
        }

        private static int ColumnTop(RbxTerrain terrain, int x, int z)
        {
            for (int y = 40; y > -16; y--)
            {
                if (terrain.Store.GetMaterial(x, y, z) != TerrainMaterials.Air
                    && terrain.Store.GetMaterial(x, y, z) != TerrainMaterials.Water)
                {
                    return y;
                }
            }

            return 0;
        }

        private static Vector3Int FindNearest(RbxTerrain terrain, int x, int z, string materialName, int radius)
        {
            TerrainMaterials.TryGetSlot(materialName, out byte material);
            for (int ring = 1; ring <= radius; ring++)
            {
                for (int dx = -ring; dx <= ring; dx++)
                {
                    for (int dz = -ring; dz <= ring; dz++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != ring)
                        {
                            continue;
                        }

                        for (int y = 40; y > -16; y--)
                        {
                            byte here = terrain.Store.GetMaterial(x + dx, y, z + dz);
                            if (here == material)
                            {
                                return new Vector3Int(x + dx, y, z + dz);
                            }

                            if (here != TerrainMaterials.Air && material != TerrainMaterials.Water)
                            {
                                break;
                            }

                            if (here != TerrainMaterials.Air && here != TerrainMaterials.Water)
                            {
                                break;
                            }
                        }
                    }
                }
            }

            return new Vector3Int(int.MinValue, 0, 0);
        }

        private IEnumerator OpenScene()
        {
            LogAssert.ignoreFailingMessages = true;
            HashSet<GameObject> before = new();
            if (_previousActive.IsValid() && _previousActive.isLoaded)
            {
                before.UnionWith(_previousActive.GetRootGameObjects());
            }

            Scene scene = EditorSceneManager.LoadSceneInPlayMode(ScenePath, new LoadSceneParameters(LoadSceneMode.Additive));
            Assert.IsTrue(scene.IsValid(), "the Block World scene loads: " + ScenePath);
            yield return null;
            if (_previousActive.IsValid() && _previousActive.isLoaded)
            {
                foreach (GameObject root in _previousActive.GetRootGameObjects())
                {
                    if (!before.Contains(root))
                    {
                        SceneManager.MoveGameObjectToScene(root, scene);
                    }
                }
            }

            SceneManager.SetActiveScene(scene);
            _blockWorld = scene;
            yield return null;
            _bootstrap = Object.FindAnyObjectByType<CoreAiPlayWorldBootstrap>();
            Assert.IsNotNull(_bootstrap, "the scene carries the play world bootstrap");
            Assert.IsTrue(_bootstrap.IsComposed, "the play world composes: " + _bootstrap.Failure);
            for (int frame = 0; frame < 900 && !Flag("BlockWorldReady"); frame++)
            {
                yield return null;
            }

            for (int frame = 0; frame < 1800 && !Flag("BlockWorldOpened"); frame++)
            {
                yield return null;
            }

            Assert.IsTrue(Flag("BlockWorldOpened"), "the world opens its first view");
            double last = -1d;
            int stable = 0;
            for (int frame = 0; frame < 6000 && stable < 120; frame++)
            {
                yield return null;
                double columns = Convert.ToDouble(Workspace().GetAttribute("BlockWorldColumns") ?? 0d);
                stable = Math.Abs(columns - last) < 0.5d ? stable + 1 : 0;
                last = columns;
            }

            Record("opened, streaming settled at " + last + " columns");
        }

        private bool Flag(string attribute)
        {
            RbxInstance workspace = CurrentWorld()?.Game?.FindFirstChildOfClass("Workspace");
            return workspace != null && Equals(workspace.GetAttribute(attribute), true);
        }

        private LuaCsRbxApiBindings CurrentWorld()
        {
            IObjectResolver container = _bootstrap?.Composition?.ModsScope?.Container;
            return container?.Resolve<RbxWorldRuntimeSessionController>().CurrentRbxApi;
        }

        private RbxInstance Workspace()
        {
            return CurrentWorld().Game.FindFirstChildOfClass("Workspace");
        }

        private RbxVector3 SpawnPosition()
        {
            RbxHumanoid humanoid = CoreAiLocalPlayer.FindHumanoid(CurrentWorld());
            return humanoid != null
                ? CurrentWorld().PartSink.GetLivePositionStuds(humanoid.RootPart.Id)
                : new RbxVector3(0f, 20f, 0f);
        }

        private void RunAsPlayer(string id, string code)
        {
            ActorContext actor = _bootstrap.Composition.ModsScope.Container.Resolve<IActorIdentityProvider>()
                .GetActorContext(BuiltInAgentRoleIds.Programmer);
            _bootstrap.Composition.ModsScope.Container.Resolve<RbxWorldRuntimeSessionController>().CurrentConcreteRuntime
                .LoadMod(actor, id, code, LuaCapabilities.All, false);
        }

        private IEnumerator SetClock(double clock)
        {
            RbxLighting lighting = (RbxLighting)CurrentWorld().Game.FindFirstChildOfClass("Lighting");
            for (int frame = 0; frame < 6; frame++)
            {
                lighting.SetClockTime(clock + frame * 0.001d);
                yield return null;
            }
        }

        private static CoreAiLookProfile LookProfile()
        {
            CoreAiLookProfile found = null;
            foreach (CoreAiLookProfile profile in Resources.FindObjectsOfTypeAll<CoreAiLookProfile>())
            {
                found ??= profile;
            }

            return found;
        }

        // ---- capture ----------------------------------------------------------------------------------------------

        private Color[] ShootAt(Camera camera, RbxVector3 fromStuds, RbxVector3 atStuds, string fileName)
        {
            Vector3 from = RbxSpace.ToUnity(fromStuds);
            Vector3 at = RbxSpace.ToUnity(atStuds);
            return Shoot(camera, from, Quaternion.LookRotation(at - from), fileName);
        }

        private Color[] Shoot(Camera camera, Vector3 position, Quaternion rotation, string fileName)
        {
            Vector3 savedPosition = camera.transform.position;
            Quaternion savedRotation = camera.transform.rotation;
            RenderTexture target = new(ShotWidth, ShotHeight, 24, RenderTextureFormat.DefaultHDR);
            Texture2D linear = new(ShotWidth, ShotHeight, TextureFormat.RGBAFloat, false, true);
            Texture2D png = new(ShotWidth, ShotHeight, TextureFormat.RGBA32, false);
            RenderTexture previousActive = RenderTexture.active;
            float savedAspect = camera.aspect;
            try
            {
                camera.transform.SetPositionAndRotation(position, rotation);
                camera.aspect = (float)ShotWidth / ShotHeight;
                RenderPipeline.SubmitRenderRequest(camera, new RenderPipeline.StandardRequest { destination = target });
                RenderTexture.active = target;
                linear.ReadPixels(new Rect(0, 0, ShotWidth, ShotHeight), 0, 0);
                linear.Apply();
                Color[] pixels = linear.GetPixels();
                for (int index = 0; index < pixels.Length; index++)
                {
                    Color pixel = pixels[index];
                    pixels[index] = new Color(Mathf.Clamp01(pixel.r), Mathf.Clamp01(pixel.g), Mathf.Clamp01(pixel.b), 1f)
                        .gamma;
                }

                png.SetPixels(pixels);
                png.Apply();
                File.WriteAllBytes(Path.Combine(_outputFolder, fileName), png.EncodeToPNG());
                return pixels;
            }
            finally
            {
                camera.transform.SetPositionAndRotation(savedPosition, savedRotation);
                camera.aspect = savedAspect;
                RenderTexture.active = previousActive;
                Object.Destroy(target);
                Object.Destroy(linear);
                Object.Destroy(png);
            }
        }

        private static string ChunkMaterials(RbxTerrainPresenter presenter, int x, int y, int z)
        {
            if (!(presenter.Renderer is RbxTerrainRenderer renderer)
                || !renderer.TryGetChunkObject(TerrainChunkCoord.FromCell(x + 8, y + 1, z), out GameObject chunk))
            {
                return "(no chunk)";
            }

            List<string> names = new();
            foreach (Material material in chunk.GetComponent<MeshRenderer>().sharedMaterials)
            {
                names.Add(material != null ? material.name + "/" + (material.GetTexture("_BaseMap") != null
                    ? material.GetTexture("_BaseMap").name
                    : "-") : "null");
            }

            return string.Join(", ", names);
        }

        /// <summary>The image row of the horizon in the horizon shots (looking 2.3 degrees down).</summary>
        private static int HorizonRow(Camera camera)
        {
            float pitch = Mathf.Atan2(4f, 100f) * Mathf.Rad2Deg;
            float halfHeight = camera.fieldOfView * 0.5f;
            float share = 0.5f + Mathf.Tan(pitch * Mathf.Deg2Rad) / Mathf.Tan(halfHeight * Mathf.Deg2Rad) * 0.5f;
            return Mathf.RoundToInt(share * ShotHeight);
        }

        // ---- measures ---------------------------------------------------------------------------------------------

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

            return count == 0 ? Color.black : new Color((float)(r / count), (float)(g / count), (float)(b / count), 1f);
        }

        /// <summary>Mean luminance difference between neighbouring 8-pixel cells of a crop: structure at stone scale.</summary>
        private static float BlockStructure(Color[] shot, RectInt crop)
        {
            const int step = 8;
            double sum = 0d;
            int count = 0;
            for (int y = crop.yMin; y + step < crop.yMax; y += step)
            {
                for (int x = crop.xMin; x + step < crop.xMax; x += step)
                {
                    float here = Luminance(shot[y * ShotWidth + x]);
                    sum += Mathf.Abs(here - Luminance(shot[y * ShotWidth + x + step]))
                           + Mathf.Abs(here - Luminance(shot[(y + step) * ShotWidth + x]));
                    count += 2;
                }
            }

            return count == 0 ? 0f : (float)(sum / count);
        }

        /// <summary>Share of pixels darker than half the crop's median luminance: the gaps of a leaf block.</summary>
        private static float DarkShare(Color[] shot, RectInt crop)
        {
            List<float> values = new();
            for (int y = crop.yMin; y < crop.yMax; y++)
            {
                for (int x = crop.xMin; x < crop.xMax; x++)
                {
                    values.Add(Luminance(shot[y * ShotWidth + x]));
                }
            }

            if (values.Count == 0)
            {
                return 0f;
            }

            values.Sort();
            float threshold = values[values.Count / 2] * 0.5f;
            int dark = 0;
            foreach (float value in values)
            {
                dark += value < threshold ? 1 : 0;
            }

            return dark / (float)values.Count;
        }

        private static float Luminance(Color color)
        {
            return 0.2126f * color.r + 0.7152f * color.g + 0.0722f * color.b;
        }

        private static float Saturation(Color color)
        {
            float max = Mathf.Max(color.r, Mathf.Max(color.g, color.b));
            float min = Mathf.Min(color.r, Mathf.Min(color.g, color.b));
            return max <= 0f ? 0f : (max - min) / max;
        }

        private static float Distance(Color a, Color b)
        {
            return Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b);
        }

        private static string Describe(Color color)
        {
            return "rgb(" + Mathf.RoundToInt(color.r * 255f) + ", " + Mathf.RoundToInt(color.g * 255f) + ", "
                   + Mathf.RoundToInt(color.b * 255f) + ")";
        }

        private static string F(float value)
        {
            return value.ToString("0.0000", CultureInfo.InvariantCulture);
        }

        private static string Lua(float value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private void Record(string line)
        {
            _report.AppendLine(line);
        }
    }
}
#endif
