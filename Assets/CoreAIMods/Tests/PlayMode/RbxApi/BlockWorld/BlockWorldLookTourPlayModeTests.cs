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
        private float _firstLawnGreen = float.NaN;
        private readonly List<float> _edgeGaps = new();
        private readonly List<(string Clock, Color Sky)> _duskSkies = new();
        private readonly List<(string Part, Color Torso)> _torsos = new();
        private readonly List<(string File, Color Bark)> _barkCrops = new();
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
            // The scene as a player first sees it, before the tour changes anything: the lawn next to the spawn.
            yield return SetClock(12d);
            Vector3Int lawnCell = FindNearest(terrain, spawnX + 3, spawnZ + 3, "Grass", 12);
            if (lawnCell.x != int.MinValue)
            {
                RbxVector3 lawnTop = new((lawnCell.x + 0.5f) * Cell, (lawnCell.y + 1f) * Cell, (lawnCell.z + 0.5f) * Cell);
                Color[] initial = ShootAt(camera, lawnTop + new RbxVector3(0f, 12f, -5f), lawnTop, "initial_lawn.png");
                Color lawn = Mean(initial, new RectInt(ShotWidth / 2 - 40, ShotHeight / 2 - 40, 80, 80));
                Record("first scene lawn beside the spawn, cell " + lawnCell + ": " + Describe(lawn) + ", green margin "
                       + F(lawn.g - (lawn.r + lawn.b) * 0.5f));
                _firstLawnGreen = lawn.g - (lawn.r + lawn.b) * 0.5f;
            }

            yield return FogEdge(camera, terrain, spawnX, spawnZ);
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

                // WHY 10:30: the owner judges the bark under a crown at mid-morning.
                yield return SetClock(10.5d);
                Color[] treeClose = ShootTreeClose(camera, terrain, spawnX, spawnZ, look + "_tree_close.png");
                yield return SetClock(TourClock);
                if (treeClose != null)
                {
                    shots["tree_close"] = treeClose;
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
                if (painted)
                {
                    ProbeCharacters(camera, look, (presenter.Renderer as RbxTerrainRenderer)?.SkyExposure);
                }
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

            Assert.Greater(_firstLawnGreen, 0.05f, "the lawn beside the spawn is green in the first scene of the session");
            for (int index = 0; index < _edgeGaps.Count; index++)
            {
                Assert.LessOrEqual(_edgeGaps[index], 0.03f, "fog edge shot " + index + ": the world's edge cannot be found");
            }

            foreach ((string file, Color bark) in _barkCrops)
            {
                if (!file.StartsWith("blocks", StringComparison.Ordinal))
                {
                    continue;
                }

                float luminance = 0.2126f * bark.r + 0.7152f * bark.g + 0.0722f * bark.b;
                Assert.GreaterOrEqual(luminance, 0.06f, file + ": the trunk under its crown reads as wood, not a hole");
                Assert.Greater(bark.r, bark.b + 0.02f, file + ": the trunk is brown (red over blue)");
                Assert.GreaterOrEqual(bark.r, bark.g, file + ": the trunk is brown (red over green)");
            }

            foreach ((string clock, Color sky) in _duskSkies)
            {
                Assert.GreaterOrEqual(sky.b, sky.g - 0.005f, "the sky at " + clock + " is blue or violet, never olive");
            }

            Assert.Greater(greenAfter, greenBefore, "the painted lawn is greener than the photographed one");
            Assert.Greater(leavesDarkAfter, leavesDarkBefore + 0.05f, "a leaf block shows dark gaps between its leaves");
            Assert.Less(floorGapAfter, 0.1f, "below the horizon the sky continues the haze");
            Assert.Greater(floorGapBefore, floorGapAfter + 0.05f, "where the fixed ground colour drew a darker floor");
            Assert.Less(partChange, 0.03f, "a Cobblestone Part looks the same in both looks");
        }

        /// <summary>
        /// From the highest hill near the spawn, a level shot at the horizon and one pitched down, at noon, dusk and
        /// midnight: crop A is fully fogged land (a ray from the pixel hits Terrain past FogEnd), crop B the background
        /// beside it (rays that hit nothing, in the same rows); the per-channel gap is recorded.
        /// </summary>
        private IEnumerator FogEdge(Camera camera, RbxTerrain terrain, int spawnX, int spawnZ)
        {
            int bestX = spawnX;
            int bestZ = spawnZ;
            int bestTop = ColumnTop(terrain, spawnX, spawnZ);
            for (int dx = -12; dx <= 12; dx++)
            {
                for (int dz = -12; dz <= 12; dz++)
                {
                    int top = ColumnTop(terrain, spawnX + dx, spawnZ + dz);
                    if (top > bestTop)
                    {
                        bestTop = top;
                        bestX = spawnX + dx;
                        bestZ = spawnZ + dz;
                    }
                }
            }

            RbxLighting lighting = (RbxLighting)CurrentWorld().Game.FindFirstChildOfClass("Lighting");
            RbxVector3 eye = new((bestX + 0.5f) * Cell, (bestTop + 3f) * Cell, (bestZ + 0.5f) * Cell);
            RbxVector3 outward = new(bestX - spawnX + 0.01f, 0f, bestZ - spawnZ + 0.01f);
            float length = Mathf.Sqrt(outward.X * outward.X + outward.Z * outward.Z);
            outward = new RbxVector3(outward.X / length, 0f, outward.Z / length);
            foreach ((string clock, double hours) in new[] { ("noon", 12d), ("dusk", 18.6d), ("midnight", 0d) })
            {
                yield return SetClock(hours);
                yield return null;
                float fogEnd = RbxSpace.LengthToUnity(lighting.FogEnd);
                foreach ((string view, float drop) in new[] { ("level", 0f), ("down", 0.45f) })
                {
                    RbxVector3 at = eye + new RbxVector3(outward.X * 100f, -drop * 100f, outward.Z * 100f);
                    string name = "fogedge_" + clock + "_" + view;
                    Color[] shot = ShootAt(camera, eye, at, name + ".png");
                    Vector3 from = RbxSpace.ToUnity(eye);
                    Quaternion rotation = Quaternion.LookRotation(RbxSpace.ToUnity(at) - from);
                    MeasureEdge(camera, from, rotation, shot, fogEnd, name);
                }
            }

            // The sky from late afternoon to nightfall, level at the horizon: the top quarter of each shot is sky.
            foreach ((string clock, double hours) in new[] { ("1730", 17.5d), ("1800", 18d), ("1836", 18.6d), ("1915", 19.25d) })
            {
                yield return SetClock(hours);
                yield return null;
                RbxVector3 at = eye + new RbxVector3(outward.X * 100f, 0f, outward.Z * 100f);
                Color[] shot = ShootAt(camera, eye, at, "dusk_sky_" + clock + ".png");
                if (clock == "1730")
                {
                    ProbeCrowns(camera, terrain, eye, at, shot, "dusk_sky_1730");
                }
                Color sky = Mean(shot, new RectInt(0, ShotHeight * 3 / 4, ShotWidth, ShotHeight / 4));
                _duskSkies.Add((clock, sky));
                Record("dusk sky " + clock + ", top quarter of a level shot: " + Describe(sky) + ", blue minus green "
                       + F(sky.b - sky.g));
            }

            yield return SetClock(TourClock);
        }

        private void MeasureEdge(Camera camera, Vector3 from, Quaternion rotation, Color[] shot, float fogEnd, string name)
        {
            Vector3 savedPosition = camera.transform.position;
            Quaternion savedRotation = camera.transform.rotation;
            float savedAspect = camera.aspect;
            camera.transform.SetPositionAndRotation(from, rotation);
            camera.aspect = (float)ShotWidth / ShotHeight;
            double[] fogged = new double[3];
            double[] background = new double[3];
            int foggedCount = 0;
            int minRow = ShotHeight;
            int maxRow = -1;
            bool[] open = new bool[ShotWidth * ShotHeight];
            for (int y = 2; y < ShotHeight; y += 4)
            {
                for (int x = 2; x < ShotWidth; x += 4)
                {
                    Ray ray = camera.ViewportPointToRay(new Vector3((x + 0.5f) / ShotWidth, (y + 0.5f) / ShotHeight, 0f));
                    if (!Physics.Raycast(ray, out RaycastHit hit, camera.farClipPlane))
                    {
                        open[y * ShotWidth + x] = true;
                        continue;
                    }

                    // WHY view depth: the lit shaders fog by the depth along the view axis, not the ray length.
                    if (Vector3.Dot(hit.point - from, rotation * Vector3.forward) < fogEnd * 1.02f)
                    {
                        continue;
                    }

                    Color pixel = shot[y * ShotWidth + x];
                    fogged[0] += pixel.r;
                    fogged[1] += pixel.g;
                    fogged[2] += pixel.b;
                    foggedCount++;
                    minRow = Mathf.Min(minRow, y);
                    maxRow = Mathf.Max(maxRow, y);
                }
            }

            int backgroundCount = 0;
            for (int y = Mathf.Max(2, minRow - 24); y <= Mathf.Min(ShotHeight - 1, maxRow + 24); y += 4)
            {
                for (int x = 2; x < ShotWidth; x += 4)
                {
                    if (!open[y * ShotWidth + x])
                    {
                        continue;
                    }

                    Color pixel = shot[y * ShotWidth + x];
                    background[0] += pixel.r;
                    background[1] += pixel.g;
                    background[2] += pixel.b;
                    backgroundCount++;
                }
            }

            camera.transform.SetPositionAndRotation(savedPosition, savedRotation);
            camera.aspect = savedAspect;
            if (foggedCount == 0 || backgroundCount == 0)
            {
                Record(name + ": no fully fogged land (" + foggedCount + ") or no background (" + backgroundCount
                       + ") in view; fog end " + F(fogEnd) + " m");
                return;
            }

            float gap = 0f;
            StringBuilder line = new();
            for (int channel = 0; channel < 3; channel++)
            {
                float a = (float)(fogged[channel] / foggedCount);
                float b = (float)(background[channel] / backgroundCount);
                gap = Mathf.Max(gap, Mathf.Abs(a - b));
                line.Append(F(a)).Append('/').Append(F(b)).Append(' ');
            }

            _edgeGaps.Add(gap);
            Record(name + ": fogged land A vs background B per channel " + line + "-> gap " + F(gap) + " (" + foggedCount
                   + " / " + backgroundCount + " samples, fog end " + F(fogEnd) + " m)");
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

        private static byte SlotOf(string name)
        {
            TerrainMaterials.TryGetSlot(name, out byte slot);
            return slot;
        }

        /// <summary>
        /// One tree as a player standing beside it sees it: the nearest tree with a trunk of at least three cells and
        /// a crown at least three studs over the head, shot from eye height about 3.5 studs from the bark, pitched up
        /// so the trunk and the underside of the crown are both in frame, from a side where nothing stands between
        /// the eye and either of them. Null when no tree near the spawn allows that.
        /// </summary>
        private Color[] ShootTreeClose(Camera camera, RbxTerrain terrain, int spawnX, int spawnZ, string file)
        {
            byte mud = SlotOf("Mud");
            byte leaves = SlotOf("LeafyGrass");
            for (int ring = 1; ring <= 48; ring++)
            {
                for (int dx = -ring; dx <= ring; dx++)
                {
                    for (int dz = -ring; dz <= ring; dz++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != ring)
                        {
                            continue;
                        }

                        int x = spawnX + dx;
                        int z = spawnZ + dz;
                        int top = int.MinValue;
                        for (int y = 40; y > -16; y--)
                        {
                            byte here = terrain.Store.GetMaterial(x, y, z);
                            if (here == mud)
                            {
                                top = y;
                                break;
                            }

                            if (here != TerrainMaterials.Air && here != leaves)
                            {
                                break;
                            }
                        }

                        if (top == int.MinValue)
                        {
                            continue;
                        }

                        int foot = top;
                        while (foot > top - 12 && terrain.Store.GetMaterial(x, foot - 1, z) == mud)
                        {
                            foot--;
                        }

                        // WHY three cells: a shorter trunk stands inside its own crown at eye height.
                        if (top - foot + 1 < 3)
                        {
                            continue;
                        }

                        int crownCell = top + 1;
                        for (int y = foot; y <= top + 1; y++)
                        {
                            if (terrain.Store.GetMaterial(x - 1, y, z) == leaves || terrain.Store.GetMaterial(x + 1, y, z) == leaves
                                || terrain.Store.GetMaterial(x, y, z - 1) == leaves || terrain.Store.GetMaterial(x, y, z + 1) == leaves)
                            {
                                crownCell = y;
                                break;
                            }
                        }

                        float groundY = foot * Cell;
                        float undersideY = crownCell * Cell;
                        float eyeY = groundY + 5f;
                        if (undersideY < eyeY + 3f)
                        {
                            continue;
                        }

                        float centreX = (x + 0.5f) * Cell;
                        float centreZ = (z + 0.5f) * Cell;
                        float away = Cell * 0.5f + 3.5f;
                        for (int turn = 0; turn < 8; turn++)
                        {
                            // WHY starting towards the spawn: the open lawn the player comes from is the likeliest
                            // clear spot.
                            float angle = Mathf.Atan2(spawnZ - z, spawnX - x) + turn * Mathf.PI / 4f;
                            float dirX = Mathf.Cos(angle);
                            float dirZ = Mathf.Sin(angle);
                            RbxVector3 eye = new(centreX + dirX * away, eyeY, centreZ + dirZ * away);
                            RbxVector3 bark = new(centreX, eyeY, centreZ);
                            RbxVector3 underside = new(centreX + dirX * (Cell * 0.5f + 0.5f), undersideY - 0.2f,
                                centreZ + dirZ * (Cell * 0.5f + 0.5f));
                            if (!InAir(terrain, eye) || !ClearSight(terrain, eye, bark, x, z)
                                                     || !ClearSight(terrain, eye, underside, x, z))
                            {
                                continue;
                            }

                            RbxVector3 at = new(centreX, (eyeY + undersideY) * 0.5f + 1f, centreZ);
                            Record("tree close: trunk (" + x + ", " + z + ") " + (top - foot + 1) + " cells from y "
                                   + F(groundY) + ", crown underside y " + F(undersideY) + ", eye " + F(eye.X) + ", "
                                   + F(eye.Y) + ", " + F(eye.Z) + " studs");
                            Color[] shot = ShootAt(camera, eye, at, file);
                            Color trunkCrop = Mean(shot, new RectInt(ShotWidth / 2 - 16, ShotHeight / 2 - 16, 32, 32));
                            float luminance = 0.2126f * trunkCrop.r + 0.7152f * trunkCrop.g + 0.0722f * trunkCrop.b;
                            _barkCrops.Add((file, trunkCrop));
                            Record("tree close: the trunk in the middle of the frame " + Describe(trunkCrop) + ", luminance "
                                   + F(luminance));
                            return shot;
                        }
                    }
                }
            }

            Record("tree close: no tree near the spawn has a trunk of three cells with a clear view of it");
            return null;
        }

        private static bool InAir(RbxTerrain terrain, RbxVector3 point)
        {
            return terrain.Store.GetMaterial(Mathf.FloorToInt(point.X / Cell), Mathf.FloorToInt(point.Y / Cell),
                Mathf.FloorToInt(point.Z / Cell)) == TerrainMaterials.Air;
        }

        /// <summary>True when every cell from <paramref name="from"/> to <paramref name="to"/> is air until the trunk's column.</summary>
        private static bool ClearSight(RbxTerrain terrain, RbxVector3 from, RbxVector3 to, int trunkX, int trunkZ)
        {
            float dx = to.X - from.X;
            float dy = to.Y - from.Y;
            float dz = to.Z - from.Z;
            int steps = Mathf.CeilToInt(Mathf.Sqrt(dx * dx + dy * dy + dz * dz) / 0.25f);
            for (int step = 0; step <= steps; step++)
            {
                float t = step / (float)steps;
                RbxVector3 point = new(from.X + dx * t, from.Y + dy * t, from.Z + dz * t);
                if (Mathf.FloorToInt(point.X / Cell) == trunkX && Mathf.FloorToInt(point.Z / Cell) == trunkZ)
                {
                    return true;
                }

                if (!InAir(terrain, point))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Every character in the scene (a model holding a torso part): how its torso is drawn (renderer, material,
        /// sky exposure, LocalTransparencyModifier) and its torso's mean colour shot from behind and above, as a
        /// third-person camera sees it.
        /// </summary>
        private void ProbeCharacters(Camera camera, string look, TerrainSkyExposure exposure)
        {
            Dictionary<string, float> modifiers = new();
            RbxInstance workspace = CurrentWorld()?.Game?.FindFirstChildOfClass("Workspace");
            if (workspace != null)
            {
                foreach (RbxInstance instance in workspace.GetDescendants())
                {
                    if (instance is RbxBasePart part && (part.Name == "UpperTorso" || part.Name == "Torso"))
                    {
                        modifiers[(part.Parent?.Name ?? "?") + "/" + part.Name] = part.LocalTransparencyModifier;
                    }
                }
            }

            int index = 0;
            foreach (Renderer renderer in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                string partName = renderer.gameObject.name;
                if (partName != "UpperTorso" && partName != "Torso")
                {
                    continue;
                }

                string model = renderer.transform.parent != null ? renderer.transform.parent.name : "?";
                Material material = renderer.sharedMaterial;
                MaterialPropertyBlock block = new();
                renderer.GetPropertyBlock(block);
                string colour = material != null && material.HasProperty("_BaseColor")
                    ? Describe(material.GetColor("_BaseColor")) : "-";
                modifiers.TryGetValue(model + "/" + partName, out float modifier);
                Vector3 centre = renderer.bounds.center;
                Vector3 back = -renderer.transform.forward;
                back.y = 0f;
                back = back.sqrMagnitude > 1e-6f ? back.normalized : Vector3.back;
                Vector3 from = centre + back * RbxSpace.LengthToUnity(10f) + Vector3.up * RbxSpace.LengthToUnity(3f);
                Quaternion rotation = Quaternion.LookRotation(centre - from);
                Color[] shot = Shoot(camera, from, rotation, look + "_third_person_" + index + ".png");
                Vector3 savedPosition = camera.transform.position;
                Quaternion savedRotation = camera.transform.rotation;
                float savedAspect = camera.aspect;
                camera.transform.SetPositionAndRotation(from, rotation);
                camera.aspect = (float)ShotWidth / ShotHeight;
                Bounds bounds = renderer.bounds;
                Vector3 low = camera.WorldToViewportPoint(bounds.center - bounds.extents * 0.4f);
                Vector3 high = camera.WorldToViewportPoint(bounds.center + bounds.extents * 0.4f);
                camera.transform.SetPositionAndRotation(savedPosition, savedRotation);
                camera.aspect = savedAspect;
                int x0 = Mathf.Clamp(Mathf.RoundToInt(Mathf.Min(low.x, high.x) * ShotWidth), 0, ShotWidth - 2);
                int x1 = Mathf.Clamp(Mathf.RoundToInt(Mathf.Max(low.x, high.x) * ShotWidth), x0 + 1, ShotWidth - 1);
                int y0 = Mathf.Clamp(Mathf.RoundToInt(Mathf.Min(low.y, high.y) * ShotHeight), 0, ShotHeight - 2);
                int y1 = Mathf.Clamp(Mathf.RoundToInt(Mathf.Max(low.y, high.y) * ShotHeight), y0 + 1, ShotHeight - 1);
                Color torso = Mean(shot, new RectInt(x0, y0, x1 - x0, y1 - y0));
                Vector3 ahead = renderer.transform.forward;
                ahead.y = 0f;
                ahead = ahead.sqrMagnitude > 1e-6f ? ahead.normalized : Vector3.forward;
                Vector3 front = centre + ahead * RbxSpace.LengthToUnity(8f) + Vector3.up * RbxSpace.LengthToUnity(1f);
                Color[] facing = Shoot(camera, front, Quaternion.LookRotation(centre - front),
                    look + "_facing_" + index + ".png");
                Color face = Mean(facing, new RectInt(ShotWidth / 2 - 12, ShotHeight / 2 - 12, 24, 24));
                RbxVector3 studs = RbxSpace.FromUnity(centre);
                int cellX = Mathf.FloorToInt(studs.X / Cell);
                int cellY = Mathf.FloorToInt(studs.Y / Cell);
                int cellZ = Mathf.FloorToInt(studs.Z / Cell);
                string written = block.HasFloat("_RbxSkyExposure") ? F(block.GetFloat("_RbxSkyExposure")) : "not written (1)";
                string sampled = exposure != null ? F(exposure.SampleExposure(cellX, cellY, cellZ)) : "-";
                Record(look + ": character " + index + " sky exposure written " + written + ", sampled now at cell ("
                       + cellX + ", " + cellY + ", " + cellZ + ") " + sampled + "; facing it from 8 studs, middle crop "
                       + Describe(face) + "; ambient mode " + RenderSettings.ambientMode + ", sky "
                       + Describe(RenderSettings.ambientSkyColor) + ", intensity " + F(RenderSettings.ambientIntensity));
                Record(look + ": character " + index + " '" + model + "/" + partName + "' at " + centre + ", enabled "
                       + renderer.enabled + ", shadows " + renderer.shadowCastingMode + ", material "
                       + (material != null ? material.name + " (" + material.shader.name + ", queue "
                                             + material.renderQueue + ")" : "none") + ", base colour " + colour
                       + ", block sky exposure " + F(block.GetFloat("_RbxSkyExposure")) + ", LocalTransparencyModifier "
                       + F(modifier) + "; third-person torso crop " + new RectInt(x0, y0, x1 - x0, y1 - y0) + " mean "
                       + Describe(torso));
                _torsos.Add((model + "/" + partName, torso));
                index++;
            }

            if (index == 0)
            {
                Record(look + ": no character torso in the scene");
            }
        }

        /// <summary>
        /// Every crown-sized leaf blob (eight cells or more) within 40 cells of the eye: how many nothing holds up
        /// (no Mud touching it, no solid cell under it) and, for those and for a few held by a trunk, the cells,
        /// whether the column under them has been generated, and what the shot shows at the crown and where the trunk
        /// stands or would stand.
        /// </summary>
        private void ProbeCrowns(Camera camera, RbxTerrain terrain, RbxVector3 eye, RbxVector3 at, Color[] shot,
            string name)
        {
            const int Radius = 40;
            byte leaves = SlotOf("LeafyGrass");
            byte mud = SlotOf("Mud");
            int eyeX = Mathf.FloorToInt(eye.X / Cell);
            int eyeZ = Mathf.FloorToInt(eye.Z / Cell);
            Vector3 from = RbxSpace.ToUnity(eye);
            Quaternion rotation = Quaternion.LookRotation(RbxSpace.ToUnity(at) - from);
            Vector3 savedPosition = camera.transform.position;
            Quaternion savedRotation = camera.transform.rotation;
            float savedAspect = camera.aspect;
            camera.transform.SetPositionAndRotation(from, rotation);
            camera.aspect = (float)ShotWidth / ShotHeight;
            Vector3Int[] sides =
            {
                new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0), new(0, 0, 1), new(0, 0, -1)
            };
            HashSet<Vector3Int> seen = new();
            int blobs = 0;
            int floating = 0;
            int floatingShown = 0;
            int heldShown = 0;
            for (int x = eyeX - Radius; x <= eyeX + Radius; x++)
            {
                for (int z = eyeZ - Radius; z <= eyeZ + Radius; z++)
                {
                    for (int y = -16; y <= 40; y++)
                    {
                        Vector3Int start = new(x, y, z);
                        if (terrain.Store.GetMaterial(x, y, z) != leaves || !seen.Add(start))
                        {
                            continue;
                        }

                        List<Vector3Int> blob = new();
                        Stack<Vector3Int> stack = new();
                        stack.Push(start);
                        bool held = false;
                        Vector3Int trunk = new(int.MinValue, 0, 0);
                        while (stack.Count > 0)
                        {
                            Vector3Int cell = stack.Pop();
                            blob.Add(cell);
                            foreach (Vector3Int side in sides)
                            {
                                Vector3Int next = cell + side;
                                byte material = terrain.Store.GetMaterial(next.x, next.y, next.z);
                                if (material == leaves)
                                {
                                    if (Math.Abs(next.x - eyeX) <= Radius + 8 && Math.Abs(next.z - eyeZ) <= Radius + 8
                                                                              && seen.Add(next))
                                    {
                                        stack.Push(next);
                                    }
                                }
                                else if (material == mud)
                                {
                                    held = true;
                                    if (trunk.x == int.MinValue || next.y < trunk.y)
                                    {
                                        trunk = next;
                                    }
                                }
                                else if (side.y == -1 && material != TerrainMaterials.Air && material != TerrainMaterials.Water)
                                {
                                    held = true;
                                }
                            }
                        }

                        if (blob.Count < 8)
                        {
                            continue;
                        }

                        blobs++;
                        if (!held)
                        {
                            floating++;
                        }

                        Vector3Int min = blob[0];
                        Vector3Int max = blob[0];
                        foreach (Vector3Int cell in blob)
                        {
                            min = Vector3Int.Min(min, cell);
                            max = Vector3Int.Max(max, cell);
                        }

                        int midX = (min.x + max.x) / 2;
                        int midZ = (min.z + max.z) / 2;
                        RbxVector3 crownCentre = new((midX + 0.5f) * Cell, (min.y + max.y + 1) * 0.5f * Cell, (midZ + 0.5f) * Cell);
                        string crownPixel = SampleShot(camera, shot, crownCentre);
                        if (!held && floatingShown < 8)
                        {
                            floatingShown++;
                            int ground = int.MinValue;
                            for (int below = min.y - 1; below >= -16; below--)
                            {
                                byte material = terrain.Store.GetMaterial(midX, below, midZ);
                                if (material != TerrainMaterials.Air && material != TerrainMaterials.Water)
                                {
                                    ground = below;
                                    break;
                                }
                            }

                            string column = ground == int.MinValue
                                ? "the column under it is empty down to y -16 (not generated)"
                                : "ground under it at y " + ground + " ("
                                  + TerrainMaterials.GetName(terrain.Store.GetMaterial(midX, ground, midZ)) + "), "
                                  + (min.y - ground - 1) + " empty cells between, no Mud in the column";
                            RbxVector3 trunkPoint = new((midX + 0.5f) * Cell, (min.y - 0.5f) * Cell, (midZ + 0.5f) * Cell);
                            Record(name + ": crown held by nothing, cells x " + min.x + ".." + max.x + ", y " + min.y + ".."
                                   + max.y + ", z " + min.z + ".." + max.z + " (" + blob.Count + "), " + column
                                   + "; shot at the crown " + crownPixel + "; just under it " + SampleShot(camera, shot, trunkPoint));
                        }
                        else if (held && trunk.x != int.MinValue && heldShown < 4 && !crownPixel.StartsWith("off"))
                        {
                            heldShown++;
                            RbxVector3 bark = new((trunk.x + 0.5f) * Cell, (trunk.y + 0.5f) * Cell, (trunk.z + 0.5f) * Cell);
                            Record(name + ": crown on a Mud trunk at cell " + trunk + ", crown cells x " + min.x + ".."
                                   + max.x + ", y " + min.y + ".." + max.y + ", z " + min.z + ".." + max.z
                                   + "; shot at the crown " + crownPixel + "; at the top trunk cell "
                                   + SampleShot(camera, shot, bark));
                        }
                    }
                }
            }

            camera.transform.SetPositionAndRotation(savedPosition, savedRotation);
            camera.aspect = savedAspect;
            Record(name + ": " + blobs + " crown-sized leaf blobs within " + Radius + " cells of the eye, " + floating
                   + " held by nothing; fog colour " + Describe(RenderSettings.fogColor));
        }

        /// <summary>The shot's pixel where <paramref name="point"/> projects, what a ray towards it hits first, and its depth.</summary>
        private static string SampleShot(Camera camera, Color[] shot, RbxVector3 point)
        {
            Vector3 world = RbxSpace.ToUnity(point);
            Vector3 viewport = camera.WorldToViewportPoint(world);
            if (viewport.z <= 0f || viewport.x < 0f || viewport.x >= 1f || viewport.y < 0f || viewport.y >= 1f)
            {
                return "off screen";
            }

            int px = Mathf.FloorToInt(viewport.x * ShotWidth);
            int py = Mathf.FloorToInt(viewport.y * ShotHeight);
            Vector3 origin = camera.transform.position;
            string hit = Physics.Raycast(origin, world - origin, out RaycastHit first, camera.farClipPlane)
                ? first.collider.name + " at " + F(first.distance) + " m"
                : "nothing";
            return "pixel (" + px + ", " + py + " from the bottom) " + Describe(shot[py * ShotWidth + px]) + ", point "
                   + F(viewport.z) + " m deep, the ray hits " + hit;
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

                            // WHY on through the crown for Mud: a trunk stands under its leaves.
                            bool seeThrough = materialName == "Mud" && here == SlotOf("LeafyGrass");
                            if (here != TerrainMaterials.Air && here != TerrainMaterials.Water && !seeThrough)
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
