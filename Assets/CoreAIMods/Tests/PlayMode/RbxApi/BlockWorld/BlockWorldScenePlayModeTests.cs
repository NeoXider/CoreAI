#if UNITY_EDITOR && COREAI_HAS_INPUT_SYSTEM
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Ai.Npc;
using CoreAI.Authority;
using CoreAI.ChatOverlay;
using CoreAI.Composition;
using CoreAI.Infrastructure;
using CoreAI.Infrastructure.Llm;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Binding.Terrain;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using CoreAI.Mods.Rbx.Instances.Terrain;
using CoreAI.Mods.Rbx.Spatial;
using CoreAI.Mods.WorldPackages;
using CoreAI.RuntimeUi;
using Microsoft.Extensions.AI;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using VContainer;
using VContainer.Unity;
using Object = UnityEngine.Object;

namespace CoreAI.Tests.PlayMode.RbxApi.BlockWorld
{
    /// <summary>
    /// P6, PlayMode (FastNoLlm): the authored Block World scene (<c>Assets/CoreAI.Demos/BlockWorld/BlockWorld.unity</c>)
    /// opened as a player opens it, with virtual Input System devices, a fresh mods store per test and the shared
    /// settings forced Offline, so no provider is called. Each test checks what a player notices and fails once,
    /// listing every finding: one player standing on generated ground, the HUD from the real UI host, place and break
    /// at the crosshair with its outline (twin: beyond reach), noon against midnight on a ground crop, a torch on a
    /// wall crop (twin: removed), a scripted model building a watchtower at the crosshair and <c>/undo</c> restoring
    /// the terrain, a villager named in the chat answering, and what the AI features say with no model. Frames are
    /// written to <c>artifacts/blockworld/</c>.
    /// </summary>
    /// <remarks>
    /// WHY a fixed frame time: the world streams and the clock runs per frame of game time, and a loaded batchmode
    /// Editor runs far below 60 frames a second; a fixed step keeps the frame counts below meaningful.
    /// </remarks>
    [TestFixture]
    [Category("InputDevice")]
    public sealed class BlockWorldScenePlayModeTests : InputTestFixture
    {
        private const string ScenePath = "Assets/CoreAI.Demos/BlockWorld/BlockWorld.unity";
        private const string AimModId = "test_blockworld_aim";
        private const float Cell = 4f;
        private const int ShotWidth = 960;
        private const int ShotHeight = 540;

        private static readonly string[] BlockWorldMods =
        {
            "blockworld_world", "blockworld_build", "blockworld_hud", "blockworld_daynight", "blockworld_torches",
            "blockworld_villagers", "blockworld_autopilot", "blockworld_survival"
        };

        private const string AimSource = @"
local camera = workspace.CurrentCamera
local applied = nil
game:GetService('RunService').Heartbeat:Connect(function()
  local from = workspace:GetAttribute('TestAimFrom')
  local at = workspace:GetAttribute('TestAimAt')
  if from and at then
    local key = tostring(from) .. tostring(at)
    if key ~= applied then
      applied = key
      camera.CameraType = Enum.CameraType.Scriptable
      camera.CFrame = CFrame.lookAt(from, at)
    end
  end
end)";

        /// <summary>
        /// The watchtower recipe of the skill text (stage 1's <c>WatchtowerExample</c>), as a model would send it;
        /// <c>{here}</c> is the attribute the model was told holds "here", filled in by the scripted model.
        /// </summary>
        private const string WatchtowerCode = @"
local terrain = workspace.Terrain
local base = workspace:GetAttribute('{here}')
if typeof(base) ~= 'Vector3' then error('point the crosshair at a block first') end
local function fill(x0, y0, z0, x1, y1, z1, material)
  local min = terrain:CellCornerToWorld(x0, y0, z0)
  local max = terrain:CellCornerToWorld(x1 + 1, y1 + 1, z1 + 1)
  terrain:FillRegion(Region3.new(min, max), 4, material)
end
local x, y, z = base.X, base.Y, base.Z
fill(x - 1, y, z - 1, x + 1, y + 7, z + 1, Enum.Material.Cobblestone)
fill(x, y + 1, z, x, y + 7, z, Enum.Material.Air)
fill(x - 2, y + 8, z - 2, x + 2, y + 8, z + 2, Enum.Material.WoodPlanks)
for _, corner in ipairs({ { -2, -2 }, { 2, -2 }, { -2, 2 }, { 2, 2 } }) do
  fill(x + corner[1], y + 9, z + corner[2], x + corner[1], y + 9, z + corner[2], Enum.Material.WoodPlanks)
end
return 'watchtower built'";

        private readonly List<string> _findings = new();
        private readonly List<string> _problems = new();
        private readonly List<string> _errors = new();
        private Keyboard _keyboard;
        private Mouse _mouse;
        private CoreAiPlayWorldBootstrap _bootstrap;
        private IDisposable _storageOverride;
        private IDisposable _installers;
        private string _storageRoot;
        private string _outputFolder;
        private float _savedCaptureDeltaTime;
        private CoreAISettingsAsset _settings;
        private string _settingsSnapshot;
        private Application.LogCallback _capture;
        private Scene _previousActive;
        private Scene _blockWorld;
        private RenderSettingsSnapshot _renderBefore;

        /// <summary>The RenderSettings of the active scene, taken before a test and put back after it.</summary>
        private struct RenderSettingsSnapshot
        {
            public Material Skybox;
            public Light Sun;
            public AmbientMode AmbientMode;
            public Color AmbientSky;
            public Color AmbientEquator;
            public Color AmbientGround;
            public float AmbientIntensity;
            public DefaultReflectionMode ReflectionMode;
            public float ReflectionIntensity;
            public bool Fog;
            public Color FogColor;
            public FogMode FogMode;
            public float FogStart;
            public float FogEnd;
            public float FogDensity;

            public static RenderSettingsSnapshot Take()
            {
                return new RenderSettingsSnapshot
                {
                    Skybox = RenderSettings.skybox,
                    Sun = RenderSettings.sun,
                    AmbientMode = RenderSettings.ambientMode,
                    AmbientSky = RenderSettings.ambientSkyColor,
                    AmbientEquator = RenderSettings.ambientEquatorColor,
                    AmbientGround = RenderSettings.ambientGroundColor,
                    AmbientIntensity = RenderSettings.ambientIntensity,
                    ReflectionMode = RenderSettings.defaultReflectionMode,
                    ReflectionIntensity = RenderSettings.reflectionIntensity,
                    Fog = RenderSettings.fog,
                    FogColor = RenderSettings.fogColor,
                    FogMode = RenderSettings.fogMode,
                    FogStart = RenderSettings.fogStartDistance,
                    FogEnd = RenderSettings.fogEndDistance,
                    FogDensity = RenderSettings.fogDensity
                };
            }

            public void Restore()
            {
                RenderSettings.skybox = Skybox;
                RenderSettings.sun = Sun;
                RenderSettings.ambientMode = AmbientMode;
                RenderSettings.ambientSkyColor = AmbientSky;
                RenderSettings.ambientEquatorColor = AmbientEquator;
                RenderSettings.ambientGroundColor = AmbientGround;
                RenderSettings.ambientIntensity = AmbientIntensity;
                RenderSettings.defaultReflectionMode = ReflectionMode;
                RenderSettings.reflectionIntensity = ReflectionIntensity;
                RenderSettings.fog = Fog;
                RenderSettings.fogColor = FogColor;
                RenderSettings.fogMode = FogMode;
                RenderSettings.fogStartDistance = FogStart;
                RenderSettings.fogEndDistance = FogEnd;
                RenderSettings.fogDensity = FogDensity;
            }
        }

        public override void Setup()
        {
            base.Setup();
            _findings.Clear();
            _problems.Clear();
            _errors.Clear();
            _savedCaptureDeltaTime = Time.captureDeltaTime;
            Time.captureDeltaTime = 1f / 60f;
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Directory.GetCurrentDirectory();
            _outputFolder = Path.Combine(projectRoot, "artifacts", "blockworld");
            Directory.CreateDirectory(_outputFolder);
            // WHY a short root: the store nests the store id and the mod id under it, and Windows refuses long paths.
            _storageRoot = Path.Combine(Path.GetTempPath(), "cai-bw", Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_storageRoot);
            _storageOverride = CoreAiStorageRoot.Override(_storageRoot);

            // WHY: the scene uses the shared Resources/CoreAISettings asset; without this the run inherits whatever
            // provider the developer selected last. The exact serialized state is restored in the tear down.
            _settings = CoreAISettingsAsset.Instance;
            Assert.IsNotNull(_settings, "the shared Resources/CoreAISettings asset exists");
            _settingsSnapshot = EditorJsonUtility.ToJson(_settings);
            _settings.ConfigureOffline();
            EditorUtility.ClearDirty(_settings);

            _capture = (condition, stackTrace, type) =>
            {
                if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                {
                    _errors.Add("[" + type + "] " + condition);
                }
            };
            Application.logMessageReceived += _capture;
            _previousActive = SceneManager.GetActiveScene();
            _renderBefore = RenderSettingsSnapshot.Take();
        }

        [UnityTearDown]
        public IEnumerator CloseScene()
        {
            Application.logMessageReceived -= _capture;
            _installers?.Dispose();
            _installers = null;
            // WHY switched off before the switch: nothing of the play world may write the RenderSettings of the scene
            // that is active again; then the test runner's own scene, with its sky, is active as before the test.
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
            _renderBefore.Restore();
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

            if (Directory.Exists(_storageRoot)
                && (File.GetAttributes(_storageRoot) & FileAttributes.ReparsePoint) == 0)
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
                "the fixture leaves the scene that was active before it active, not a scene of its own");
            Assert.AreSame(_renderBefore.Skybox, RenderSettings.skybox, "with the sky it had, for the tests after it");
        }

        // ---- the tests ------------------------------------------------------------------------------------------

        [UnityTest]
        public IEnumerator BlockWorldScene_OpensAndPlays_WithNoModel()
        {
            yield return OpenScene(null);
            CheckComposition();
            CheckOnePlayerAndTheFolder();
            yield return WaitUntilStanding("first_frame.png");

            yield return CheckHud();
            yield return CheckPlaceAndBreak();
            yield return CheckBeyondReach();
            yield return CheckNoModelAnswers();
            Report();
        }

        [UnityTest]
        public IEnumerator BlockWorldScene_MidnightDarkensTheGround_AndATorchLightsTheWall()
        {
            yield return OpenScene(null);
            yield return WaitUntilStanding();
            yield return CheckDayAndNightAndTorch();
            Report();
        }

        [UnityTest]
        public IEnumerator BlockWorldScene_AScriptedModelBuildsAWatchtowerHere_AndUndoRestoresTheTerrain()
        {
            ScriptedBlockWorldModel model = new() { ExecuteLuaCode = WatchtowerCode };
            yield return OpenScene(model);
            yield return WaitUntilStanding();
            yield return WaitForStreamingToSettle();
            yield return CheckWatchtowerAndUndo(model);
            Report();
        }

        [UnityTest]
        public IEnumerator BlockWorldScene_AVillagerNamedInTheChat_AnswersThroughAScriptedModel()
        {
            ScriptedBlockWorldModel model = new();
            yield return OpenScene(model);
            yield return WaitUntilStanding();
            yield return CheckVillagerAnswers(model);
            Report();
        }

        [UnityTest]
        public IEnumerator BlockWorldScene_InSurvival_ALongFallHurts_AndALethalOneRespawnsThePlayer()
        {
            yield return OpenScene(null);
            yield return WaitUntilStanding();
            yield return CheckSurvivalFalls();
            Report();
        }

        [UnityTest]
        [Timeout(900000)]
        public IEnumerator BlockWorldScene_ATorchLightsACaveAtNoon()
        {
            yield return OpenScene(null);
            yield return WaitUntilStanding();
            yield return WaitForStreamingToSettle();
            yield return CheckCaveAtNoon(false);
            Report();
        }

        [UnityTest]
        [Timeout(900000)]
        public IEnumerator BlockWorldScene_ACaveIsDarkAtNoon()
        {
            yield return OpenScene(null);
            yield return WaitUntilStanding();
            yield return WaitForStreamingToSettle();
            yield return CheckCaveAtNoon(true);
            Report();
        }

        [UnityTest]
        public IEnumerator BlockWorldScene_TheCharacters_InTheShoulderView_IdleAndWalking_AndTimeTypedInTheChat()
        {
            yield return OpenScene(null);
            for (int frame = 0; frame < 1800 && !Equals(Workspace().GetAttribute("BlockWorldOpened"), true); frame++)
            {
                yield return null;
            }

            yield return Frames(180);
            yield return CheckCharacterLooks();
            yield return CheckTimeTypedInTheChat();
            Report();
        }

        [UnityTest]
        [Timeout(900000)]
        public IEnumerator BlockWorldScene_TheVillagers_StayOnTheLand_ForTwoMinutes()
        {
            yield return OpenScene(null);
            yield return WaitUntilStanding();
            yield return WaitForStreamingToSettle();
            yield return CheckVillagersOnTheLand();
            Report();
        }

        [UnityTest]
        [Timeout(900000)]
        public IEnumerator BlockWorldScene_TheWorld_FromAHilltop_AWood_AndACaveMouth()
        {
            yield return OpenScene(null);
            yield return WaitUntilStanding();
            yield return WaitForStreamingToSettle(scanLeaves: true);
            yield return CheckWorldViews();
            Report();
        }

        // ---- the world: a hilltop, a wood, a cave mouth ----------------------------------------------------------------

        private IEnumerator CheckWorldViews()
        {
            RbxTerrain terrain = Terrain();
            RbxVector3 spawn = (RbxVector3)Workspace().GetAttribute("BlockWorldSpawn");
            int sx = Mathf.FloorToInt(spawn.X / Cell);
            int sz = Mathf.FloorToInt(spawn.Z / Cell);
            TerrainMaterials.TryGetSlot("Mud", out byte trunkSlot);
            TerrainMaterials.TryGetSlot("LeafyGrass", out byte leafSlot);
            Vector3Int peak = new(sx, -16, sz);
            // WHY per world column: the generator offers one tree place per 3 x 3 cells inside each 16 x 16 column
            // (16 places), so a wood is a column whose places mostly grew a tree.
            Dictionary<(int X, int Z), int> trunksByColumn = new();
            for (int x = sx - 40; x <= sx + 40; x++)
            {
                for (int z = sz - 40; z <= sz + 40; z++)
                {
                    int ground = GroundTop(terrain, x, z, trunkSlot, leafSlot);
                    if (ground > peak.y)
                    {
                        peak = new Vector3Int(x, ground, z);
                    }

                    if (ground < 15 && terrain.Store.GetMaterial(x, ground + 1, z) == trunkSlot)
                    {
                        (int X, int Z) column = (Mathf.FloorToInt(x / 16f), Mathf.FloorToInt(z / 16f));
                        trunksByColumn[column] = trunksByColumn.TryGetValue(column, out int count) ? count + 1 : 1;
                    }
                }
            }

            yield return SetClock(12d);
            RbxVector3 peakTop = new((peak.x + 0.5f) * Cell, (peak.y + 1) * Cell, (peak.z + 0.5f) * Cell);
            RbxVector3 toSpawn = new RbxVector3(spawn.X - peakTop.X, 0f, spawn.Z - peakTop.Z).Unit;
            yield return Aim(peakTop + new RbxVector3(0f, 7f, 0f) - toSpawn * 4f,
                peakTop + toSpawn * 60f + new RbxVector3(0f, -18f, 0f));
            yield return Frames(20);
            yield return ShootWithUi("world_hilltop.png");
            // WHY the same view at dusk and at night: the fog takes the sky's colour, and a far view shows it best.
            foreach ((double hours, string shot) in new[] { (18.6d, "world_hilltop_dusk.png"), (0d, "world_hilltop_night.png") })
            {
                yield return SetClock(hours);
                yield return ShootWithUi(shot);
            }

            yield return SetClock(10.5d);

            (int X, int Z) densest = (Mathf.FloorToInt(sx / 16f), Mathf.FloorToInt(sz / 16f));
            int most = 0;
            foreach (KeyValuePair<(int X, int Z), int> pair in trunksByColumn)
            {
                if (pair.Value > most)
                {
                    densest = pair.Key;
                    most = pair.Value;
                }
            }

            int woodX = densest.X * 16 + 8;
            int woodZ = densest.Z * 16 + 8;
            int woodGround = GroundTop(terrain, woodX, woodZ, trunkSlot, leafSlot);
            RbxVector3 woodCentre = new((woodX + 0.5f) * Cell, (woodGround + 1) * Cell, (woodZ + 0.5f) * Cell);
            // WHY from its edge at eye height: from above a wood is a roof of leaves; from beside it, trunks show.
            RbxVector3 towardSpawn = new RbxVector3(spawn.X - woodCentre.X, 0f, spawn.Z - woodCentre.Z);
            towardSpawn = towardSpawn.Magnitude > 0.01f ? towardSpawn.Unit : new RbxVector3(1f, 0f, 0f);
            RbxVector3 edge = woodCentre + towardSpawn * 40f;
            int edgeGround = GroundTop(terrain, Mathf.FloorToInt(edge.X / Cell), Mathf.FloorToInt(edge.Z / Cell), trunkSlot,
                leafSlot);
            RbxVector3 woodLook = woodCentre + new RbxVector3(0f, 6f, 0f);
            RbxVector3 woodCamera = new(edge.X, (edgeGround + 1) * Cell + 7f, edge.Z);
            yield return Aim(ClearView(woodLook, woodCamera), woodLook);
            yield return Frames(20);
            yield return ShootWithUi("world_wood.png");

            // A tree close up: a trunk round the wood with six studs of open air on one side at the height of its
            // bark, seen level from four studs, so the bark fills the middle and the crown the top.
            bool hasTree = false;
            RbxVector3 treeCamera = default;
            RbxVector3 treeLook = default;
            for (int x = densest.X * 16 - 16; x < densest.X * 16 + 32 && !hasTree; x++)
            {
                for (int z = densest.Z * 16 - 16; z < densest.Z * 16 + 32 && !hasTree; z++)
                {
                    int treeGround = GroundTop(terrain, x, z, trunkSlot, leafSlot);
                    if (treeGround >= 14 || terrain.Store.GetMaterial(x, treeGround + 1, z) != trunkSlot)
                    {
                        continue;
                    }

                    RbxVector3 bark = new((x + 0.5f) * Cell, (treeGround + 1) * Cell + 5f, (z + 0.5f) * Cell);
                    foreach (RbxVector3 side in new[] { new RbxVector3(1f, 0f, 0f), new RbxVector3(-1f, 0f, 0f),
                                 new RbxVector3(0f, 0f, 1f), new RbxVector3(0f, 0f, -1f) })
                    {
                        // WHY from the trunk's face: the trunk's own cell is solid for its first two studs.
                        if (ClearLength(terrain, bark + side * 2.1f, side, 6f) >= 6f)
                        {
                            treeCamera = bark + side * 6f;
                            treeLook = bark + new RbxVector3(0f, 1f, 0f);
                            hasTree = true;
                            break;
                        }
                    }
                }
            }

            if (hasTree)
            {
                yield return Aim(treeCamera, treeLook);
                yield return Frames(10);
                yield return ShootWithUi("world_tree_close.png");
            }

            Expect(hasTree, "a tree near the wood can be seen up close from open ground");

            Vector3Int mouth = default;
            Vector3Int outside = default;
            bool found = false;
            for (int radius = 2; radius <= 48 && !found; radius++)
            {
                for (int dx = -radius; dx <= radius && !found; dx++)
                {
                    foreach (int dz in new[] { -radius, radius })
                    {
                        if (TryFindHillsideMouth(terrain, sx + dx, sz + dz, trunkSlot, leafSlot, out mouth, out outside))
                        {
                            found = true;
                            break;
                        }
                    }
                }
            }

            object torches = null;
            if (found)
            {
                RbxVector3 floor = new((mouth.x + 0.5f) * Cell, mouth.y * Cell, (mouth.z + 0.5f) * Cell);
                RbxVector3 outward = new(outside.x - mouth.x, 0f, outside.z - mouth.z);
                RbxVector3 stand = floor + outward * (Cell * 3f) + new RbxVector3(0f, 3.2f, 0f);
                RunAsPlayer("test_mouth_stand", "game:GetService('Players'):GetPlayers()[1].Character:PivotTo(CFrame.new("
                                                + Lua(stand.X) + ", " + Lua(stand.Y + 4f) + ", " + Lua(stand.Z) + "))");
                yield return Frames(60);
                RbxVector3 into = floor - outward * Cell + new RbxVector3(0f, 3f, 0f);
                RbxVector3 camera = ClearView(into, floor + outward * (Cell * 4.5f) + new RbxVector3(0f, 7f, 0f));
                yield return Aim(camera, into);
                yield return Frames(10);
                yield return ShootWithUi("world_cave_mouth.png");
                // WHY from inside the mouth: outside, the hillside drops below the mouth's floor, and from down there
                // the lip hides the floor a torch goes on.
                RbxVector3 inside = floor + new RbxVector3(0f, 3.2f, 0f);
                RunAsPlayer("test_mouth_inside", "game:GetService('Players'):GetPlayers()[1].Character:PivotTo(CFrame.new("
                                                 + Lua(inside.X) + ", " + Lua(inside.Y + 1f) + ", " + Lua(inside.Z) + "))");
                yield return Frames(30);
                RbxVector3 eye = RootPosition(Humanoid()) + new RbxVector3(0f, 1.5f, 0f);
                yield return Tap(_keyboard.digit9Key);
                yield return Aim(eye, floor - outward * (Cell * 1.5f));
                yield return Click(_mouse.rightButton);
                torches = Workspace().GetAttribute("BlockWorldTorches");
                yield return Tap(_keyboard.digit1Key);
                RunAsPlayer("test_mouth_out", "game:GetService('Players'):GetPlayers()[1].Character:PivotTo(CFrame.new("
                                              + Lua(stand.X) + ", " + Lua(stand.Y + 4f) + ", " + Lua(stand.Z) + "))");
                yield return Frames(30);
                yield return SetClock(19.5d);
                yield return Aim(camera, into);
                yield return Frames(10);
                yield return ShootWithUi("world_cave_mouth_torch_dusk.png");
            }

            Record("world: peak " + peak + " (" + (peak.y - GroundTop(terrain, sx, sz, trunkSlot, leafSlot))
                   + " rows above the spawn), densest wood " + most + " trunks in a 16 x 16 column at (" + woodX + ", " + woodZ
                   + "), cave mouth " + (found ? mouth + " opening toward " + outside : "none") + ", torches " + torches
                   + "; fog " + FogLine());
            Expect(peak.y >= 11, "a high peak within forty cells of the spawn");
            Expect(most >= 10, "a wood within forty cells of the spawn: a column with ten or more of its sixteen trees");
            Expect(found, "a cave mouth opens onto a hillside within 48 cells of the spawn");
            Expect(!found || Equals(torches, 1d) || Equals(torches, 1), "a torch goes on the floor inside the cave mouth");
        }

        /// <summary>
        /// A camera spot that sees <paramref name="at"/> with no block between: <paramref name="wanted"/> when its
        /// line is clear with a stud to spare, else the first clear turn of it round the target, else the turn that
        /// reaches farthest, stopped short of its block.
        /// </summary>
        /// <remarks>WHY: woods and hills now stand round the villagers, and a fixed offset often lands in a crown.</remarks>
        private RbxVector3 ClearView(RbxVector3 at, RbxVector3 wanted, RbxInstance subject = null)
        {
            RbxTerrain terrain = Terrain();
            RbxVector3 offset = wanted - at;
            float length = offset.Magnitude;
            RbxVector3 best = wanted;
            float bestClear = -1f;
            foreach (float degrees in new[] { 0f, 45f, -45f, 90f, -90f, 135f, -135f, 180f })
            {
                float radians = degrees * Mathf.Deg2Rad;
                float cos = Mathf.Cos(radians);
                float sin = Mathf.Sin(radians);
                RbxVector3 way = new RbxVector3(offset.X * cos - offset.Z * sin, offset.Y, offset.X * sin + offset.Z * cos)
                                 / length;
                float clear = ClearLength(terrain, at, way, length + 1f);
                if (clear >= length + 1f && !PartBetween(at, at + way * (length + 1f), subject))
                {
                    return at + way * length;
                }

                if (clear > bestClear)
                {
                    bestClear = clear;
                    best = at + way * Mathf.Max(clear - 1f, 0.5f);
                }
            }

            return best;
        }

        private int _probes;

        /// <summary>
        /// True when a Part (a villager, the player, a torch) lies on the segment, Terrain and the
        /// <paramref name="subject"/> Model left out.
        /// </summary>
        /// <remarks>WHY: frame 8 once had another villager's arm flat across the camera; Terrain alone misses that.</remarks>
        private bool PartBetween(RbxVector3 from, RbxVector3 to, RbxInstance subject)
        {
            string probeId = "test_part_between_" + ++_probes;
            RbxVector3 along = to - from;
            Workspace().SetAttribute("TestSubject", subject != null ? subject.Name : "");
            RunAsPlayer(probeId, "local params = RaycastParams.new() "
                                 + "params.FilterType = Enum.RaycastFilterType.Exclude "
                                 + "local leaveOut = { workspace.Terrain } "
                                 + "local subject = workspace:FindFirstChild(workspace:GetAttribute('TestSubject') or '') "
                                 + "if subject then table.insert(leaveOut, subject) end "
                                 + "params.FilterDescendantsInstances = leaveOut "
                                 + "workspace:SetAttribute('TestPartBetween', workspace:Raycast(Vector3.new(" + Lua(from.X) + ", "
                                 + Lua(from.Y) + ", " + Lua(from.Z) + "), Vector3.new(" + Lua(along.X) + ", " + Lua(along.Y) + ", "
                                 + Lua(along.Z) + "), params) ~= nil)");
            Session().CurrentConcreteRuntime.UnloadMod(PlayerActor(), probeId);
            return Equals(Workspace().GetAttribute("TestPartBetween"), true);
        }

        private static float ClearLength(RbxTerrain terrain, RbxVector3 from, RbxVector3 way, float length)
        {
            for (float travelled = 0f; travelled < length; travelled += 0.25f)
            {
                RbxVector3 point = from + way * travelled;
                if (Solid(terrain, Mathf.FloorToInt(point.X / Cell), Mathf.FloorToInt(point.Y / Cell),
                        Mathf.FloorToInt(point.Z / Cell)))
                {
                    return travelled;
                }
            }

            return length;
        }

        /// <summary>
        /// The ground crop with the sky's light at 0.6 and 1.6 times what the day-night mod set, then the mod's value
        /// back: the record of how the ground answers the light, to choose the light from.
        /// </summary>
        /// <remarks>WHY direct writes hold: the clock stands, and the mod writes the light only when the daylight changes.</remarks>
        private IEnumerator SkyTrials(RbxLighting lighting, RectInt ground, string label, StringBuilder record)
        {
            RbxColor3 set = lighting.OutdoorAmbient;
            foreach (float scale in new[] { 0.6f, 1.6f })
            {
                lighting.SetOutdoorAmbient(new RbxColor3(set.R * scale, set.G * scale, set.B * scale));
                yield return Frames(5);
                record.Append(label).Append(" x").Append(scale.ToString("0.0")).Append(' ')
                    .Append(Luminance(Shoot("_" + label + "_sky_x" + scale.ToString("0.0") + ".png"), ground).ToString("0.000"))
                    .Append("; ");
            }

            lighting.SetOutdoorAmbient(set);
            yield return Frames(5);
        }

        /// <summary>Sets the clock directly, as the AI or a script would, so no notice covers a shot.</summary>
        private IEnumerator SetClock(double hours)
        {
            ((RbxLighting)CurrentWorld().Game.FindFirstChildOfClass("Lighting")).SetClockTime(hours);
            // WHY this many: the day-night mod adopts a clock set elsewhere on its next 0.1 s step, then lights the sky.
            yield return Frames(40);
        }

        private string FogLine()
        {
            RbxLighting lighting = (RbxLighting)CurrentWorld().Game.FindFirstChildOfClass("Lighting");
            return lighting.FogStart.ToString("0") + " to " + lighting.FogEnd.ToString("0") + " studs (land reach "
                   + Workspace().GetAttribute("BlockWorldViewStuds") + "), colour " + lighting.FogColor;
        }

        private static int GroundTop(RbxTerrain terrain, int x, int z, byte trunk, byte leaves)
        {
            for (int y = 15; y > -16; y--)
            {
                byte material = terrain.Store.GetMaterial(x, y, z);
                if (material != TerrainMaterials.Air && material != TerrainMaterials.Water && material != trunk
                    && material != leaves)
                {
                    return y;
                }
            }

            return -16;
        }

        /// <summary>A two-high opening under a roof at (x, z) whose neighbour toward the open side lies below it.</summary>
        private static bool TryFindHillsideMouth(RbxTerrain terrain, int x, int z, byte trunk, byte leaves,
            out Vector3Int mouth, out Vector3Int outside)
        {
            mouth = default;
            outside = default;
            int top = GroundTop(terrain, x, z, trunk, leaves);
            for (int y = top - 3; y >= top - 6 && y > -15; y--)
            {
                if (Solid(terrain, x, y - 1, z) && !Solid(terrain, x, y, z) && !Solid(terrain, x, y + 1, z)
                    && Solid(terrain, x, y + 2, z))
                {
                    foreach (Vector3Int step in new[] { Vector3Int.right, Vector3Int.left, new Vector3Int(0, 0, 1), new Vector3Int(0, 0, -1) })
                    {
                        if (GroundTop(terrain, x + step.x, z + step.z, trunk, leaves) < y)
                        {
                            mouth = new Vector3Int(x, y, z);
                            outside = new Vector3Int(x + step.x, y, z + step.z);
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        // ---- the villagers stay on the land ----------------------------------------------------------------------

        private IEnumerator CheckVillagersOnTheLand()
        {
            string[] names = { "Mira", "Tobin", "Elda" };
            TerrainMaterials.TryGetSlot("Mud", out byte trunkSlot);
            TerrainMaterials.TryGetSlot("LeafyGrass", out byte leafSlot);
            float[] falling = new float[names.Length];
            float[] longestFall = new float[names.Length];
            StringBuilder seen = new();
            bool allOnTop = true;
            float start = Time.time;
            int nextCheck = 0;
            float[] checks = { 0f, 30f, 120f };
            while (nextCheck < checks.Length)
            {
                float elapsed = Time.time - start;
                for (int index = 0; index < names.Length; index++)
                {
                    RbxInstance model = Workspace().FindFirstChild(names[index]);
                    RbxHumanoid humanoid = model?.FindFirstChildOfClass("Humanoid") as RbxHumanoid;
                    if (humanoid != null && humanoid.GetState() == RbxHumanoidState.Freefall)
                    {
                        falling[index] += Time.deltaTime;
                        longestFall[index] = Mathf.Max(longestFall[index], falling[index]);
                    }
                    else
                    {
                        falling[index] = 0f;
                    }
                }

                if (elapsed >= checks[nextCheck])
                {
                    seen.Append("at ").Append(checks[nextCheck].ToString("0")).Append(" s:");
                    foreach (string name in names)
                    {
                        RbxInstance model = Workspace().FindFirstChild(name);
                        RbxInstance root = model?.FindFirstChild("HumanoidRootPart");
                        if (root == null)
                        {
                            seen.Append(' ').Append(name).Append(" missing;");
                            allOnTop = false;
                            continue;
                        }

                        RbxVector3 at = CurrentWorld().PartSink.GetLivePositionStuds(root.Id);
                        int top = GroundTop(Terrain(), Mathf.FloorToInt(at.X / Cell), Mathf.FloorToInt(at.Z / Cell), trunkSlot,
                            leafSlot);
                        float surface = (top + 1) * Cell;
                        bool onTop = at.Y - 1f >= surface - 0.5f;
                        allOnTop &= onTop;
                        seen.Append(' ').Append(name).Append(" root ").Append(at).Append(" over ground ").Append(surface)
                            .Append(onTop ? "" : " (UNDER IT)").Append(", rescues ").Append(model.GetAttribute("NpcRescues") ?? 0)
                            .Append(';');
                    }

                    seen.Append(" | ");
                    nextCheck++;
                }

                yield return null;
            }

            // The three villagers at the end of the two minutes, seen together from above their middle.
            RbxVector3 middle = new(0f, 0f, 0f);
            int counted = 0;
            foreach (string name in names)
            {
                RbxInstance root = Workspace().FindFirstChild(name)?.FindFirstChild("HumanoidRootPart");
                if (root != null)
                {
                    middle += CurrentWorld().PartSink.GetLivePositionStuds(root.Id);
                    counted++;
                }
            }

            if (counted > 0)
            {
                middle = middle / counted + new RbxVector3(0f, 2f, 0f);
                yield return Aim(ClearView(middle, middle + new RbxVector3(-20f, 14f, -20f)), middle);
                yield return Frames(5);
                yield return ShootWithUi("villagers_on_the_land.png");
            }

            Record("villagers on the land: " + seen + "longest fall " + string.Join(", ", longestFall.Select(value => value.ToString("0.0")))
                   + " s");
            Expect(allOnTop, "every villager stands on top of the land of its column at 0, 30 and 120 s");
            Expect(longestFall.All(value => value <= 2f), "no villager is in free fall for more than two seconds");
        }

        // ---- how the characters look -------------------------------------------------------------------------------

        private IEnumerator CheckCharacterLooks()
        {
            yield return SetClock(10d);
            yield return Tap(_keyboard.vKey);
            yield return Frames(45);
            yield return ShootWithUi("look_player_shoulder.png");
            // WHY the torso's own colour: a look tour once drew the player's character pure black.
            RbxInstance torso = Humanoid()?.Parent?.FindFirstChild("Torso");
            Color torsoColour = Color.black;
            if (torso != null)
            {
                RbxVector3 torsoAt = CurrentWorld().PartSink.GetLivePositionStuds(torso.Id);
                RectInt torsoCrop = ProjectedRect(torsoAt + new RbxVector3(-0.5f, -0.5f, 0f), torsoAt + new RbxVector3(0.5f, 0.5f, 0f));
                torsoColour = Mean(Shoot("_player_torso.png"), torsoCrop);
                Record("the player in third person at 10:00: torso crop " + torsoCrop + " mean " + torsoColour);
            }

            Expect(torso != null && 0.2126f * torsoColour.r + 0.7152f * torsoColour.g + 0.0722f * torsoColour.b > 0.03f,
                "the player's character is drawn in its colours in third person, not black");
            yield return Tap(_keyboard.vKey);
            yield return Frames(10);

            StringBuilder seen = new();
            foreach (string name in new[] { "Mira", "Tobin" })
            {
                RunAsPlayer("test_look_" + name, "local root = workspace." + name + ".HumanoidRootPart "
                                                 + "workspace:SetAttribute('TestLookAt', root.Position) "
                                                 + "workspace:SetAttribute('TestLookAlong', root.CFrame.LookVector)");
                RbxVector3 at = (RbxVector3)Workspace().GetAttribute("TestLookAt");
                RbxVector3 along = (RbxVector3)Workspace().GetAttribute("TestLookAlong");
                RbxVector3 face = at + new RbxVector3(0f, 2.5f, 0f);
                yield return Aim(ClearView(face, at + along * 7f + new RbxVector3(0f, 3f, 0f), Workspace().FindFirstChild(name)), face);
                yield return Frames(5);
                yield return ShootWithUi("look_" + name.ToLowerInvariant() + "_idle.png");
                seen.Append(name).Append(" at ").Append(at).Append(" facing ").Append(along).Append("; ");
            }

            // WHY the brain off first: Tobin's NPC brain drives his Humanoid itself every tick.
            RunAsPlayer("test_stop_tobin", "workspace.Tobin:SetAttribute('NpcBrain', nil)");
            yield return Frames(10);
            // WHY a clear heading: on hilly ground the way Tobin faces can end at a step a block high, which no
            // Humanoid climbs; the first of eight headings with ten studs of open, level ground is walked.
            RunAsPlayer("test_walk_tobin", "local tobin = workspace.Tobin "
                                           + "local root = tobin.HumanoidRootPart "
                                           + "local params = RaycastParams.new() "
                                           + "params.FilterType = Enum.RaycastFilterType.Exclude "
                                           + "params.FilterDescendantsInstances = { tobin } "
                                           + "local heading = nil "
                                           + "for step = 0, 7 do "
                                           + "local angle = step * math.pi / 4 "
                                           + "local way = Vector3.new(math.cos(angle), 0, math.sin(angle)) "
                                           + "if not workspace:Raycast(root.Position, way * 10, params) "
                                           + "and workspace:Raycast(root.Position + way * 10, Vector3.new(0, -4, 0), params) then "
                                           + "heading = way break end end "
                                           + "workspace:SetAttribute('TestWalkClear', heading ~= nil) "
                                           + "workspace:SetAttribute('TestWalkHeld', root.Anchored) "
                                           + "heading = heading or root.CFrame.LookVector "
                                           + "tobin.Humanoid:MoveTo(root.Position + heading * 12) "
                                           + "workspace:SetAttribute('TestWalkFrom', root.Position)");
            RbxVector3 from = (RbxVector3)Workspace().GetAttribute("TestWalkFrom");
            object clear = Workspace().GetAttribute("TestWalkClear");
            object held = Workspace().GetAttribute("TestWalkHeld");
            yield return Frames(24);
            RbxInstance tobinRoot = Workspace().FindFirstChild("Tobin")?.FindFirstChild("HumanoidRootPart");
            RbxVector3 now = tobinRoot != null ? CurrentWorld().PartSink.GetLivePositionStuds(tobinRoot.Id) : from;
            RbxVector3 side = new RbxVector3(now.Z - from.Z, 0f, from.X - now.X);
            side = side.Magnitude > 0.01f ? side.Unit : new RbxVector3(1f, 0f, 0f);
            RbxVector3 body = now + new RbxVector3(0f, 2f, 0f);
            yield return Aim(ClearView(body, now + side * 8f + new RbxVector3(0f, 2.5f, 0f), Workspace().FindFirstChild("Tobin")), body);
            yield return ShootWithUi("look_tobin_walking.png");
            Record("looks: " + seen + "Tobin walked from " + from + " to " + now + " ("
                   + (now - from).Magnitude.ToString("0.0") + " studs in 24 frames; a clear heading " + clear
                   + ", held " + held + ")");
            Expect((now - from).Magnitude > 1f, "a villager told to MoveTo walks there");
        }

        // ---- /time typed in the chat --------------------------------------------------------------------------------

        private IEnumerator CheckTimeTypedInTheChat()
        {
            ChatOverlayController overlay = Overlay();
            int start = overlay.Feed.Lines.Count;
            overlay.Submit("/time midnight");
            yield return Frames(20);
            RbxLighting lighting = (RbxLighting)CurrentWorld().Game.FindFirstChildOfClass("Lighting");
            string lines = Lines(overlay, start);
            yield return ShootWithUi("time_midnight_typed.png");
            Record("/time midnight typed: clock " + lighting.ClockTime.ToString("0.00") + ", chat '" + lines + "'");
            Expect(lighting.ClockTime < 0.2f || lighting.ClockTime > 23.8f, "/time midnight typed in the chat sets the clock");
        }

        // ---- opening the scene ------------------------------------------------------------------------------------

        private IEnumerator OpenScene(ILlmClient model)
        {
            // WHY here and not in Setup: the flag set in a set-up method does not carry into the test's coroutine.
            LogAssert.ignoreFailingMessages = true;
            _keyboard = InputSystem.AddDevice<Keyboard>();
            _mouse = InputSystem.AddDevice<Mouse>();
            _keyboard.MakeCurrent();
            _mouse.MakeCurrent();
            if (model != null)
            {
                _installers = LifetimeScope.Enqueue(builder =>
                {
                    if (builder.ApplicationOrigin is CoreAILifetimeScope)
                    {
                        builder.RegisterInstance<ILlmClient>(model);
                    }
                });
            }

            HashSet<GameObject> before = new();
            if (_previousActive.IsValid() && _previousActive.isLoaded)
            {
                before.UnionWith(_previousActive.GetRootGameObjects());
            }

            Scene scene = EditorSceneManager.LoadSceneInPlayMode(ScenePath, new LoadSceneParameters(LoadSceneMode.Additive));
            Assert.IsTrue(scene.IsValid(), "the Block World scene loads: " + ScenePath);
            yield return null;
            // WHY: loaded beside the test runner's scene, which is active again after the test. The play world composes
            // in Awake, before the new scene can be made active, so what it created at the root of the scene active
            // then moves into the new scene; from here on that is the active scene, as a player's scene is.
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
            _installers?.Dispose();
            _installers = null;

            _bootstrap = Object.FindFirstObjectByType<CoreAiPlayWorldBootstrap>();
            Assert.IsNotNull(_bootstrap, "the scene carries the play world bootstrap");
            Assert.IsTrue(_bootstrap.IsComposed, "the play world composes: " + _bootstrap.Failure);
            RbxHumanoid humanoid = null;
            for (int frame = 0; frame < 900 && (humanoid == null || !IsReady()); frame++)
            {
                yield return null;
                humanoid = CurrentWorld() != null ? CoreAiLocalPlayer.FindHumanoid(CurrentWorld()) : null;
            }

            Assert.IsNotNull(humanoid, "the local character spawns within 900 frames; errors: " + Errors());
            Assert.IsTrue(IsReady(), "the world mod reports BlockWorldReady within 900 frames; errors: " + Errors());
            Record("opened: spawn " + Workspace().GetAttribute("BlockWorldSpawn") + " facing "
                   + Workspace().GetAttribute("BlockWorldSpawnLook") + ", columns "
                   + Workspace().GetAttribute("BlockWorldColumns") + " after load, clock " + ClockTime());
        }

        private bool IsReady()
        {
            RbxInstance workspace = CurrentWorld()?.Game?.FindFirstChildOfClass("Workspace");
            return workspace != null && Equals(workspace.GetAttribute("BlockWorldReady"), true);
        }

        private void CheckComposition()
        {
            int previews = 0;
            foreach (Transform transform in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include,
                         FindObjectsSortMode.None))
            {
                previews += transform.name.StartsWith("Block World Preview", StringComparison.Ordinal) ? 1 : 0;
            }

            List<Light> suns = new();
            foreach (Light light in Object.FindObjectsByType<Light>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (light.type == LightType.Directional)
                {
                    suns.Add(light);
                }
            }

            bool baseplate = Workspace().FindFirstChild("Baseplate") != null;
            Record("composition: preview objects left " + previews + ", directional lights " + suns.Count
                   + " (RenderSettings.sun " + (RenderSettings.sun != null ? RenderSettings.sun.name : "none")
                   + "), baseplate " + baseplate + ", AllowAgentsFromLua " + _bootstrap.Composition.ModsScope.AllowAgentsFromLua);
            Expect(previews == 0, "the baked editor preview is gone at Play (the mods draw the world)");
            Expect(suns.Count == 1 && suns[0] == RenderSettings.sun,
                "exactly one sun, and it is RenderSettings.sun, the light the day cycle turns");
            Expect(!baseplate, "no baseplate: the Terrain is the ground");
            Expect(_bootstrap.Composition.ModsScope.AllowAgentsFromLua, "this scene turns AI from Lua on");
        }

        private void CheckOnePlayerAndTheFolder()
        {
            LuaCsRbxApiBindings world = CurrentWorld();
            StringBuilder players = new();
            int characters = 0;
            foreach (RbxPlayer player in world.Players.GetPlayers())
            {
                characters += player.Character != null ? 1 : 0;
                players.Append(player.Name).Append(" (actor ").Append(player.NetworkActorId).Append(") ");
            }

            ActorContext actor = PlayerActor();
            HashSet<string> loaded = new();
            StringBuilder owners = new();
            foreach (LuaModInfo mod in Session().CurrentConcreteRuntime.ListMods(actor))
            {
                loaded.Add(mod.Id);
                owners.Append(mod.Id).Append(" by ").Append(mod.OwnerActorId).Append("; ");
            }

            List<string> missing = new();
            bool ownedByPlayer = true;
            foreach (LuaModInfo mod in Session().CurrentConcreteRuntime.ListMods(actor))
            {
                // WHY an empty owner counts: a mod the store rehydrates without a recorded owner runs as the host,
                // which in a solo world is the same screen as the local player and joins no second player.
                if (Array.IndexOf(BlockWorldMods, mod.Id) >= 0 && mod.OwnerActorId.Length > 0
                    && mod.OwnerActorId != actor.ActorId)
                {
                    ownedByPlayer = false;
                }
            }

            foreach (string id in BlockWorldMods)
            {
                if (!loaded.Contains(id))
                {
                    missing.Add(id);
                }
            }

            Record("players: " + players + "| player actor " + actor.ActorId + " | mods: " + owners);
            Expect(world.Players.GetPlayers().Count == 1 && characters == 1, "exactly one player with one character");
            Expect(missing.Count == 0, "the Samples/Block World folder is on: missing " + string.Join(", ", missing));
            Expect(ownedByPlayer, "the Block World mods run as the host of this screen or as the local player, no other actor");
        }

        private IEnumerator WaitUntilStanding(string firstFrameShot = null)
        {
            // WHY first: a new player is held behind the HUD's veil until the world has built the first view.
            bool veilSeen = false;
            int openedAfter = 0;
            float gameStart = Time.time;
            float wallStart = Time.realtimeSinceStartup;
            for (; openedAfter < 1800 && !Equals(Workspace().GetAttribute("BlockWorldOpened"), true); openedAfter++)
            {
                if (firstFrameShot != null && !veilSeen && Equals(Workspace().GetAttribute("BlockWorldOpened"), false))
                {
                    veilSeen = true;
                    yield return ShootWithUi("veil.png");
                }

                yield return null;
            }

            Record("first view built after " + openedAfter + " frames, " + (Time.time - gameStart).ToString("0.00")
                   + " s of game time, " + (Time.realtimeSinceStartup - wallStart).ToString("0.00") + " s of wall time"
                   + " (veil captured " + veilSeen + "), columns " + Workspace().GetAttribute("BlockWorldColumns"));
            Expect(Equals(Workspace().GetAttribute("BlockWorldOpened"), true), "the world lets the new player go");
            RbxHumanoid humanoid = Humanoid();
            RbxVector3 previous = RootPosition(humanoid);
            int still = 0;
            for (int frame = 0; frame < 900 && still < 30; frame++)
            {
                yield return null;
                RbxVector3 now = RootPosition(humanoid);
                still = Mathf.Abs(now.Y - previous.Y) < 0.01f ? still + 1 : 0;
                previous = now;
            }

            RbxVector3 root = RootPosition(humanoid);
            RbxTerrain terrain = Terrain();
            int cellX = Mathf.FloorToInt(root.X / Cell);
            int cellZ = Mathf.FloorToInt(root.Z / Cell);
            int below = Mathf.FloorToInt((root.Y - 3.5f) / Cell);
            byte ground = terrain.Store.GetMaterial(cellX, below, cellZ);
            if (firstFrameShot != null)
            {
                // WHY before any aim: this is the view the default camera gives a new player.
                yield return ShootWithUi(firstFrameShot);
                Record("first frame at clock " + ClockTime());
                RunAsPlayer("test_first_person", "local c = game:GetService('Players'):GetPlayers()[1].Character "
                                                 + "workspace:SetAttribute('TestHeadHidden', c.Head.LocalTransparencyModifier) "
                                                 + "workspace:SetAttribute('TestEyeY', workspace.CurrentCamera.CFrame.Position.Y)");
                double hidden = Convert.ToDouble(Workspace().GetAttribute("TestHeadHidden") ?? 0d);
                double eyeAboveFloor = Convert.ToDouble(Workspace().GetAttribute("TestEyeY") ?? 0d) - (below + 1) * Cell;
                Record("first person: the head's LocalTransparencyModifier " + hidden.ToString("0.00") + ", the eye "
                       + eyeAboveFloor.ToString("0.00") + " studs above the floor");
                Expect(hidden > 0.99d, "in first person the player's own body is not drawn");
                Expect(eyeAboveFloor > 4f && eyeAboveFloor < 5f, "the first-person eye is at head height (about 4.5 studs)");
            }

            yield return CheckGrassIsGreen(root);
            Record("standing: root " + root + ", state " + humanoid.GetState() + ", still " + still
                   + " frames, cell under the feet (" + cellX + ", " + below + ", " + cellZ + ") material slot " + ground);
            Expect(still >= 30, "the character comes to rest");
            Expect(ground != TerrainMaterials.Air, "the character stands on generated ground, not on air");
        }

        /// <summary>
        /// The grass cell beside the spawn, at noon and proven sunlit (no ray toward the sun from its corners or
        /// centre hits anything), reads green by a margin over red; the same cell turned to Ground reads brown, and
        /// is turned back.
        /// </summary>
        /// <remarks>
        /// WHY noon and the sunlight proof: under the morning sun the cell can lie in a hill's or the character's long
        /// shadow, where the grass's mean colour reads olive-brown and a plain "green wins" check fails by luck.
        /// </remarks>
        private IEnumerator CheckGrassIsGreen(RbxVector3 root)
        {
            RbxTerrain terrain = Terrain();
            TerrainMaterials.TryGetSlot("Grass", out byte grassSlot);
            // WHY the clock itself and not BlockWorldTime: the attribute makes the clock mod post a notice, which
            // the HUD would still show when a test looks at it.
            RbxLighting lighting = (RbxLighting)CurrentWorld().Game.FindFirstChildOfClass("Lighting");
            double clockBefore = lighting.ClockTime;
            lighting.SetClockTime(12d);
            yield return Frames(3);
            // WHY a search: woods and hills now stand around the spawn, so the cell beside it may lie in a shadow; the
            // first grass cell two to eight cells away that nothing hides from the sun is measured.
            int rootX = Mathf.FloorToInt(root.X / Cell);
            int rootZ = Mathf.FloorToInt(root.Z / Cell);
            StringBuilder candidates = new();
            List<Vector3Int> cells = new();
            for (int ring = 2; ring <= 8 && cells.Count < 40; ring++)
            {
                for (int dx = -ring; dx <= ring && cells.Count < 40; dx++)
                {
                    foreach (int dz in new[] { -ring, ring })
                    {
                        int x = rootX + dx;
                        int z = rootZ + dz;
                        int rowTop = 15;
                        while (rowTop > -16 && terrain.Store.GetMaterial(x, rowTop, z) == TerrainMaterials.Air)
                        {
                            rowTop--;
                        }

                        if (terrain.Store.GetMaterial(x, rowTop, z) == grassSlot)
                        {
                            cells.Add(new Vector3Int(x, rowTop, z));
                            candidates.Append("Vector3.new(").Append(Lua((x + 0.5f) * Cell)).Append(", ")
                                .Append(Lua((rowTop + 1) * Cell + 0.2f)).Append(", ").Append(Lua((z + 0.5f) * Cell)).Append("), ");
                        }
                    }
                }
            }

            RunAsPlayer("test_grass_sunlit", "local toSun = game:GetService('Lighting'):GetSunDirection() * 300 "
                                             + "workspace:SetAttribute('TestGrassPick', 0) "
                                             + "for index, centre in ipairs({ " + candidates + "}) do "
                                             + "local lit = true "
                                             + "for _, d in ipairs({ Vector3.new(0, 0, 0), Vector3.new(-1.4, 0, -1.4), "
                                             + "Vector3.new(1.4, 0, -1.4), Vector3.new(-1.4, 0, 1.4), Vector3.new(1.4, 0, 1.4) }) do "
                                             + "if workspace:Raycast(centre + d, toSun) then lit = false end end "
                                             + "if lit then workspace:SetAttribute('TestGrassPick', index) break end end");
            int pick = Convert.ToInt32(Workspace().GetAttribute("TestGrassPick") ?? 0);
            bool sunlit = pick > 0;
            Vector3Int chosen = sunlit ? cells[pick - 1] : (cells.Count > 0 ? cells[0] : new Vector3Int(rootX + 2, 0, rootZ));
            int cellX = chosen.x;
            int cellZ = chosen.z;
            int top = chosen.y;
            bool isGrass = terrain.Store.GetMaterial(cellX, top, cellZ) == grassSlot;
            RbxVector3 centre = new((cellX + 0.5f) * Cell, (top + 1) * Cell, (cellZ + 0.5f) * Cell);
            yield return Aim(centre + new RbxVector3(0f, 14f, -6f), centre);
            RbxTerrainRenderer renderer = Object.FindAnyObjectByType<RbxTerrainPresenter>()?.Renderer as RbxTerrainRenderer;
            TerrainChunkCoord grassChunk = TerrainChunkCoord.FromCell(cellX, top, cellZ);
            int pendingFrames = 0;
            while (renderer != null && renderer.IsGeometryPending(grassChunk) && pendingFrames < 300)
            {
                pendingFrames++;
                yield return null;
            }

            string chunkMaterials = ChunkMaterialNames(renderer, grassChunk);
            RectInt crop = ProjectedRect(centre + new RbxVector3(-1.5f, 0f, -1.5f), centre + new RbxVector3(1.5f, 0f, 1.5f));
            Color grass = Mean(Shoot("_ground_check.png"), crop);

            string cell = "Region3.new(Vector3.new(" + Lua(cellX * Cell) + ", " + Lua(top * Cell) + ", " + Lua(cellZ * Cell)
                          + "), Vector3.new(" + Lua((cellX + 1) * Cell) + ", " + Lua((top + 1) * Cell) + ", "
                          + Lua((cellZ + 1) * Cell) + "))";
            RunAsPlayer("test_grass_to_dirt",
                "workspace.Terrain:ReplaceMaterial(" + cell + ", 4, Enum.Material.Grass, Enum.Material.Ground)");
            yield return Frames(10);
            Color dirt = Mean(Shoot("_ground_check_dirt.png"), crop);
            RunAsPlayer("test_dirt_to_grass",
                "workspace.Terrain:ReplaceMaterial(" + cell + ", 4, Enum.Material.Ground, Enum.Material.Grass)");
            bool restored = terrain.Store.GetMaterial(cellX, top, cellZ) == grassSlot;
            lighting.SetClockTime(clockBefore);
            yield return Frames(3);

            Record("grass beside the spawn: cell (" + cellX + ", " + top + ", " + cellZ + ") grass " + isGrass + ", sunlit at noon "
                   + sunlit + "; chunk " + grassChunk + " meshed after " + pendingFrames + " frame(s), drawn with " + chunkMaterials
                   + "; crop " + crop + " mean " + grass + "; as Ground " + dirt + "; restored " + restored
                   + ", clock back to " + clockBefore.ToString("0.00"));
            Expect(isGrass, "the ground beside the spawn is the Grass material");
            Expect(sunlit, "a grass cell near the spawn lies in sunlight at noon (nothing between it and the sun)");
            // WHY no hue here: the grass colour belongs to the look package and is retuned there; what this scene owns
            // is that its top face is the Grass material, drawn clearly unlike the Ground under it.
            float apart = Mathf.Abs(grass.r - dirt.r) + Mathf.Abs(grass.g - dirt.g) + Mathf.Abs(grass.b - dirt.b);
            Expect(apart > 0.1f, "the sunlit Grass top is drawn clearly unlike the same cell as Ground (" + apart.ToString("0.000")
                                 + " apart)");
            Expect(restored, "the cell is grass again");
        }

        /// <summary>
        /// What the camera shows at a viewport point: the first Roblox hit along the ray (instance, material, cell) and
        /// the nearest three Unity renderers whose bounds the ray enters (object, its root, material, distance).
        /// </summary>
        private string WhatIsAt(float viewportX, float viewportY)
        {
            Camera camera = _bootstrap.Composition.Camera;
            if (camera == null)
            {
                return "(no camera)";
            }

            Ray ray = camera.ViewportPointToRay(new Vector3(viewportX, viewportY, 0f));
            RbxVector3 origin = RbxSpace.FromUnity(ray.origin);
            RbxVector3 along = (RbxSpace.FromUnity(ray.origin + ray.direction) - origin).Unit * 400f;
            string probeId = "test_what_is_at_" + ++_probes;
            RunAsPlayer(probeId, "local hit = workspace:Raycast(Vector3.new(" + Lua(origin.X) + ", " + Lua(origin.Y)
                                           + ", " + Lua(origin.Z) + "), Vector3.new(" + Lua(along.X) + ", " + Lua(along.Y)
                                           + ", " + Lua(along.Z) + ")) "
                                           + "workspace:SetAttribute('TestWhatIsAt', hit and (hit.Instance:GetFullName() .. ' ' "
                                           + ".. hit.Material.Name .. ' at ' .. tostring(hit.Position) .. ', cell ' .. "
                                           + "tostring(workspace.Terrain:WorldToCell(hit.Position - hit.Normal * 0.1)) "
                                           + ".. ', ' .. math.floor(hit.Distance) .. ' studs') or 'nothing within 400 studs')");
            Session().CurrentConcreteRuntime.UnloadMod(PlayerActor(), probeId);
            List<(float Distance, string What)> renderers = new();
            foreach (Renderer renderer in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Exclude))
            {
                if (renderer.enabled && renderer.bounds.IntersectRay(ray, out float distance))
                {
                    renderers.Add((distance, renderer.name + " (root " + renderer.transform.root.name + ", "
                                             + (renderer.sharedMaterial != null ? renderer.sharedMaterial.name : "no material")
                                             + ") at " + distance.ToString("0.0")));
                }
            }

            renderers.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            List<string> nearest = new();
            for (int index = 0; index < renderers.Count && index < 3; index++)
            {
                nearest.Add(renderers[index].What);
            }

            return "(" + viewportX + ", " + viewportY + ") Roblox: " + Workspace().GetAttribute("TestWhatIsAt")
                   + "; renderers: " + string.Join("; ", nearest);
        }

        /// <summary>The materials (and their base textures) a terrain chunk is drawn with, for the grass record.</summary>
        private static string ChunkMaterialNames(RbxTerrainRenderer renderer, TerrainChunkCoord coord)
        {
            if (renderer == null || !renderer.TryGetChunkObject(coord, out GameObject chunk) || chunk == null)
            {
                return "(no chunk)";
            }

            MeshRenderer meshRenderer = chunk.GetComponent<MeshRenderer>();
            if (meshRenderer == null)
            {
                return "(no renderer)";
            }

            List<string> names = new();
            foreach (Material material in meshRenderer.sharedMaterials)
            {
                Texture baseMap = material != null && material.HasProperty("_BaseMap") ? material.GetTexture("_BaseMap") : null;
                names.Add(material == null ? "null" : material.name + "/" + (baseMap != null ? baseMap.name : "-"));
            }

            return "[" + string.Join(", ", names) + "]";
        }

        private IEnumerator WaitForStreamingToSettle(bool scanLeaves = false)
        {
            bool scannedMidway = false;
            double last = -1d;
            int stable = 0;
            int frames = 0;
            float stableSince = Time.realtimeSinceStartup;
            for (; frames < 6000 && stable < 120; frames++)
            {
                yield return null;
                double columns = Convert.ToDouble(Workspace().GetAttribute("BlockWorldColumns") ?? 0d);
                if (Math.Abs(columns - last) < 0.5d)
                {
                    stable++;
                }
                else
                {
                    stable = 0;
                    stableSince = Time.realtimeSinceStartup;
                }

                last = columns;
                if (scanLeaves && !scannedMidway && columns >= 20d)
                {
                    scannedMidway = true;
                    LeafScan midway = ScanLeaves(Terrain());
                    Record("leaves while streaming (" + columns + " columns): " + midway);
                }
            }

            if (scanLeaves)
            {
                LeafScan whole = ScanLeaves(Terrain());
                Record("leaves with the radius full (" + last + " columns): " + whole);
                Expect(whole.Inside == 0, "no leaf hangs without a trunk inside the streamed land: " + whole);
            }

            // WHY the stable frames' wall time: the land is whole and the player stands, so it is the standing frame.
            Record("streaming settled at " + last + " columns after " + (frames - stable) + " frames (stable for "
                   + stable + " frames, " + ((Time.realtimeSinceStartup - stableSince) * 1000f / Math.Max(stable, 1))
                   .ToString("0.0") + " ms of wall time per standing frame)");
        }

        // ---- HUD ---------------------------------------------------------------------------------------------------

        private readonly struct LeafScan
        {
            public LeafScan(int leaves, int inside, int edge, int outside, string example)
            {
                Leaves = leaves;
                Inside = inside;
                Edge = edge;
                Outside = outside;
                Example = example;
            }

            public int Leaves { get; }

            public int Inside { get; }

            public int Edge { get; }

            public int Outside { get; }

            public string Example { get; }

            public override string ToString()
            {
                return Leaves + " leaf cells; not held by a rooted trunk: " + Inside + " inside, " + Edge
                       + " in a column at the edge of the made land, " + Outside + " in columns not made yet"
                       + (Example.Length > 0 ? " (" + Example + ")" : "");
            }
        }

        /// <summary>
        /// Leaf cells of the live Terrain that no rooted trunk holds (six neighbours, through leaves and trunk cells),
        /// within five columns of the spawn's column, split by where their column stands: made with every neighbour
        /// made, made at the edge of the made land, or not made yet (no bedrock).
        /// </summary>
        private LeafScan ScanLeaves(RbxTerrain terrain)
        {
            TerrainMaterials.TryGetSlot("Mud", out byte trunk);
            TerrainMaterials.TryGetSlot("LeafyGrass", out byte leaves);
            TerrainMaterials.TryGetSlot("Basalt", out byte bedrock);
            RbxVector3 spawn = (RbxVector3)Workspace().GetAttribute("BlockWorldSpawn");
            int centreX = Mathf.FloorToInt(spawn.X / Cell / 16f);
            int centreZ = Mathf.FloorToInt(spawn.Z / Cell / 16f);
            int min = (Math.Min(centreX, centreZ) - 5) * 16;
            int max = (Math.Max(centreX, centreZ) + 6) * 16 - 1;
            HashSet<(int, int, int)> leafCells = new();
            HashSet<(int, int, int)> held = new();
            Queue<(int X, int Y, int Z)> open = new();
            for (int x = min; x <= max; x++)
            {
                for (int z = min; z <= max; z++)
                {
                    for (int y = -15; y <= 15; y++)
                    {
                        byte material = terrain.Store.GetMaterial(x, y, z);
                        if (material == leaves)
                        {
                            leafCells.Add((x, y, z));
                        }
                        else if (material == trunk && terrain.Store.GetMaterial(x, y - 1, z) != trunk)
                        {
                            byte under = terrain.Store.GetMaterial(x, y - 1, z);
                            if (under != TerrainMaterials.Air && under != TerrainMaterials.Water && under != leaves)
                            {
                                held.Add((x, y, z));
                                open.Enqueue((x, y, z));
                            }
                        }
                    }
                }
            }

            (int, int, int)[] steps = { (1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1) };
            while (open.Count > 0)
            {
                (int x, int y, int z) = open.Dequeue();
                foreach ((int dx, int dy, int dz) in steps)
                {
                    (int, int, int) next = (x + dx, y + dy, z + dz);
                    if (held.Contains(next))
                    {
                        continue;
                    }

                    byte material = terrain.Store.GetMaterial(next.Item1, next.Item2, next.Item3);
                    if (material == leaves || material == trunk)
                    {
                        held.Add(next);
                        open.Enqueue(next);
                    }
                }
            }

            bool Made(int atX, int atZ)
            {
                return terrain.Store.GetMaterial(atX * 16 + 8, -16, atZ * 16 + 8) == bedrock;
            }

            int inside = 0;
            int edge = 0;
            int outside = 0;
            string example = "";
            foreach ((int x, int y, int z) in leafCells)
            {
                if (held.Contains((x, y, z)))
                {
                    continue;
                }

                int columnX = Mathf.FloorToInt(x / 16f);
                int columnZ = Mathf.FloorToInt(z / 16f);
                if (!Made(columnX, columnZ))
                {
                    outside++;
                }
                else if (!Made(columnX + 1, columnZ) || !Made(columnX - 1, columnZ) || !Made(columnX, columnZ + 1)
                         || !Made(columnX, columnZ - 1))
                {
                    edge++;
                }
                else
                {
                    inside++;
                }

                example = example.Length == 0 ? "e.g. (" + x + ", " + y + ", " + z + ")" : example;
            }

            return new LeafScan(leafCells.Count, inside, edge, outside, example);
        }

        private IEnumerator CheckHud()
        {
            IRuntimeUiHost host = _bootstrap.Composition.CoreScope.Container.Resolve<IRuntimeUiHost>();
            RuntimeUiScreen hud = null;
            foreach (RuntimeUiScreen screen in host.Screens)
            {
                if (!screen.IsDestroyed && screen.Root.Q("hotbar") != null)
                {
                    hud = screen;
                }
            }

            Expect(host.IsEnabled, "the runtime UI host is on");
            Expect(hud != null, "the HUD screen exists on the real runtime UI host");
            if (hud == null)
            {
                yield break;
            }

            VisualElement hotbar = hud.Root.Q("hotbar");
            VisualElement notice = hud.Root.Q("notice");
            VisualElement banner = hud.Root.Q("autopilot");
            Rect bounds = hotbar.worldBound;
            Rect panel = hud.Root.panel != null ? hud.Root.panel.visualTree.worldBound : Rect.zero;
            Record("hud: screen '" + hud.Id + "' visible " + hud.IsVisible + ", diagnostics " + hud.Diagnostics.Count
                   + ", hotbar " + bounds + " in panel " + panel + ", notice display " + notice.resolvedStyle.display
                   + ", autopilot banner display " + banner.resolvedStyle.display);
            Expect(hud.IsVisible && hud.Root.panel != null, "the HUD is attached and visible");
            Expect(hud.Diagnostics.Count == 0, "the HUD's UXML and USS load with no diagnostics");
            Expect(bounds.width > 100f && bounds.height > 20f && panel.Contains(bounds.center),
                "the hotbar is laid out on screen");
            Expect(notice.resolvedStyle.display == DisplayStyle.None, "an empty notice is hidden");
            Expect(banner.resolvedStyle.display == DisplayStyle.None, "the autopilot banner is hidden while it is off");
            Expect(hud.Root.Q("slot1").ClassListContains("is-selected"), "slot 1 starts selected");

            yield return Tap(_keyboard.digit3Key);
            bool third = hud.Root.Q("slot3").ClassListContains("is-selected")
                         && !hud.Root.Q("slot1").ClassListContains("is-selected");
            object afterKey = Workspace().GetAttribute("BlockWorldSelected");
            Set(_mouse.scroll, new Vector2(0f, 120f));
            yield return Frames(3);
            Set(_mouse.scroll, Vector2.zero);
            yield return Frames(2);
            object afterWheel = Workspace().GetAttribute("BlockWorldSelected");
            Record("hotbar: key 3 -> " + afterKey + " (slot 3 selected " + third + "), wheel up -> " + afterWheel);
            Expect(third && Equals(afterKey, "Rock"), "key 3 picks slot 3, Stone");
            Expect(Equals(afterWheel, "Ground"), "the wheel moves the hotbar one slot left");
            yield return Tap(_keyboard.digit1Key);
            yield return ShootWithUi("hud.png");
        }

        // ---- place and break -----------------------------------------------------------------------------------------

        private IEnumerator CheckPlaceAndBreak()
        {
            RbxVector3 eye = RootPosition(Humanoid()) + new RbxVector3(0f, 1.5f, 0f);
            yield return Aim(eye, eye + new RbxVector3(8f, -6f, 3f));
            bool targeted = Equals(Workspace().GetAttribute("CrosshairHasTarget"), true);
            Expect(targeted, "the ground next to the character is under the crosshair");
            if (!targeted)
            {
                yield break;
            }

            RbxVector3 cell = (RbxVector3)Workspace().GetAttribute("CrosshairCell");
            RbxVector3 outline = OutlineCenter();
            RbxVector3 cellCenter = (cell + new RbxVector3(0.5f, 0.5f, 0.5f)) * Cell;
            RbxTerrain terrain = Terrain();
            byte before = terrain.Store.GetMaterial((int)cell.X, (int)cell.Y, (int)cell.Z);
            yield return Click(_mouse.leftButton);
            byte broken = Terrain().Store.GetMaterial((int)cell.X, (int)cell.Y, (int)cell.Z);
            yield return Frames(3);
            RbxVector3 nextCell = (RbxVector3)Workspace().GetAttribute("CrosshairCell");
            RbxVector3 nextOutline = OutlineCenter();
            RbxVector3 place = (RbxVector3)Workspace().GetAttribute("CrosshairPlaceCell");
            yield return Tap(_keyboard.digit6Key);
            yield return Click(_mouse.rightButton);
            TerrainMaterials.TryGetSlot("Brick", out byte brick);
            byte placed = Terrain().Store.GetMaterial((int)place.X, (int)place.Y, (int)place.Z);
            Record("build: target " + cell + " (slot " + before + "), outline centre " + outline + " vs cell centre "
                   + cellCenter + "; after left click slot " + broken + ", next target " + nextCell + " outline "
                   + nextOutline + "; right click with Brick put slot " + placed + " at " + place
                   + "; placed " + Workspace().GetAttribute("BlockWorldPlaced") + ", broken "
                   + Workspace().GetAttribute("BlockWorldBroken"));
            Expect((outline - cellCenter).Magnitude < 0.2f, "the outline surrounds the cell under the crosshair");
            Expect(before != TerrainMaterials.Air && broken == TerrainMaterials.Air,
                "a left click breaks the outlined cell in the frame after the click");
            Expect(!nextCell.Equals(cell) && (nextOutline - (nextCell + new RbxVector3(0.5f, 0.5f, 0.5f)) * Cell).Magnitude < 0.2f,
                "the outline follows to the cell behind the broken one");
            Expect(placed == brick, "a right click places the hotbar block on the outlined face");
            yield return Tap(_keyboard.digit1Key);
            // WHY a step back: from the eye the new brick fills the view; from behind the player it reads as built.
            RbxVector3 brickCentre = (place + new RbxVector3(0.5f, 0.5f, 0.5f)) * Cell;
            RbxVector3 back = new(eye.X - brickCentre.X, 0f, eye.Z - brickCentre.Z);
            back = back.Magnitude > 0.01f ? back.Unit : new RbxVector3(-1f, 0f, 0f);
            yield return Aim(ClearView(brickCentre, brickCentre + back * 12f + new RbxVector3(0f, 7f, 0f)), brickCentre);
            yield return Frames(3);
            yield return ShootWithUi("building.png");
        }

        private IEnumerator CheckBeyondReach()
        {
            RbxVector3 eye = RootPosition(Humanoid()) + new RbxVector3(0f, 1.5f, 0f);
            int px = Mathf.FloorToInt(eye.X / Cell) + 8;
            int py = Mathf.FloorToInt(eye.Y / Cell) + 3;
            int pz = Mathf.FloorToInt(eye.Z / Cell);
            RunAsPlayer("test_blockworld_pillar", @"
                local terrain = workspace.Terrain
                local min = terrain:CellCornerToWorld(" + px + ", " + (py - 1) + ", " + pz + @")
                local max = terrain:CellCornerToWorld(" + (px + 1) + ", " + (py + 2) + ", " + (pz + 1) + @")
                terrain:FillRegion(Region3.new(min, max), 4, Enum.Material.Concrete)");
            RbxVector3 pillar = new((px + 0.5f) * Cell, (py + 0.5f) * Cell, (pz + 0.5f) * Cell);
            yield return Aim(eye, pillar);
            TerrainMaterials.TryGetSlot("Concrete", out byte concrete);
            bool target = Equals(Workspace().GetAttribute("CrosshairHasTarget"), true);
            object placedBefore = Workspace().GetAttribute("BlockWorldPlaced");
            object brokenBefore = Workspace().GetAttribute("BlockWorldBroken");
            yield return Click(_mouse.leftButton);
            yield return Click(_mouse.rightButton);
            object placedAfter = Workspace().GetAttribute("BlockWorldPlaced");
            object brokenAfter = Workspace().GetAttribute("BlockWorldBroken");
            bool pillarStands = Terrain().Store.GetMaterial(px, py, pz) == concrete;
            Record("beyond reach: pillar at " + pillar + ", " + (pillar - eye).Magnitude.ToString("0.0")
                   + " studs from the eyes; outlined cell " + Workspace().GetAttribute("CrosshairCell") + ", target " + target + ", placed " + placedBefore + " -> " + placedAfter
                   + ", broken " + brokenBefore + " -> " + brokenAfter + ", pillar still stands " + pillarStands);
            Expect(!target, "a block 32 studs away is beyond the reach of five blocks: nothing is outlined");
            Expect(Equals(placedBefore, placedAfter) && Equals(brokenBefore, brokenAfter) && pillarStands,
                "clicks beyond the reach place and break nothing");
        }

        // ---- no model ------------------------------------------------------------------------------------------------

        private IEnumerator CheckNoModelAnswers()
        {
            ChatOverlayController overlay = Overlay();
            int start = overlay.Feed.Lines.Count;
            overlay.Submit("build a watchtower here");
            yield return WaitForLine(overlay, start, line => line.Kind != ChatFeedLineKind.Player, 900);
            string chat = Lines(overlay, start);

            RbxInstance mira = Workspace().FindFirstChild("Mira");
            RbxVector3 eye = RootPosition(Humanoid()) + new RbxVector3(0f, 1.5f, 0f);
            RbxVector3 miraAt = mira?.FindFirstChild("HumanoidRootPart") != null
                ? CurrentWorld().PartSink.GetLivePositionStuds(mira.FindFirstChild("HumanoidRootPart").Id)
                : eye;
            RbxVector3 toward = (miraAt - eye).Unit;
            // WHY from above: on hilly ground a point six studs before Mira at her height can lie inside a slope.
            yield return Aim(miraAt - toward * 5f + new RbxVector3(0f, 4f, 0f), miraAt + new RbxVector3(0f, 2f, 0f));
            object looked = Workspace().GetAttribute("CrosshairModelName");
            object lookedPart = Workspace().GetAttribute("CrosshairPartName");
            int villagerStart = overlay.Feed.Lines.Count;
            yield return Tap(_keyboard.eKey);
            yield return WaitForLine(overlay, villagerStart, line => line.Speaker == "Mira", 900);
            string villager = Lines(overlay, villagerStart);
            NpcSpeechBubbleBoard board = _bootstrap.Composition.CoreScope.Container.Resolve<NpcSpeechBubbleBoard>();
            StringBuilder bubbles = new();
            foreach (NpcSpeechBubble bubble in board.Bubbles)
            {
                bubbles.Append(bubble.NpcName).Append(": ").Append(bubble.Text).Append(bubble.IsVisible ? "" : " (hidden)")
                    .Append(" | ");
            }

            yield return ShootWithUi("villager_no_model.png");
            RbxVector3 spawnAt = (RbxVector3)Workspace().GetAttribute("BlockWorldSpawn");
            StringBuilder standing = new();
            bool allStand = true;
            foreach (string name in new[] { "Mira", "Tobin", "Elda" })
            {
                RbxInstance root = Workspace().FindFirstChild(name)?.FindFirstChild("HumanoidRootPart");
                RbxVector3 at = root != null ? CurrentWorld().PartSink.GetLivePositionStuds(root.Id) : default;
                allStand &= root != null && Mathf.Abs(at.Y - spawnAt.Y) < 24f;
                standing.Append(name).Append(' ').Append(at).Append("; ");
            }

            Record("villagers stand at " + standing);
            Expect(allStand, "the three villagers stand on the ground near the spawn, none fell through the world");
            overlay.Submit("/autopilot on");
            yield return Frames(30);
            object autopilot = Players()[0].GetAttribute("Autopilot");
            object banner = Workspace().GetAttribute("BlockWorldAutopilot");
            overlay.Submit("/autopilot off");
            yield return Frames(10);

            Record("no model: chat answered '" + chat + "'; Mira at " + miraAt + ", looking at '" + looked + "' (part '"
                   + lookedPart + "'), E answered '" + villager
                   + "'; bubbles " + bubbles + "; /autopilot on -> Autopilot " + autopilot + ", banner '" + banner + "'");
            Expect(chat.Length > 0, "a chat request with no model is answered");
            Expect(MentionsMissingModel(chat) && !chat.Contains("```"),
                "the chat says in a sentence that no model is set up, not with a code stub");
            Expect(Equals(looked, "Mira"), "the crosshair names the villager looked at");
            Expect(MentionsMissingModel(villager), "a villager with no model says that no model answers");
            Expect(!villager.Contains("Offline mode"), "in character: the villager never speaks the offline system line");
            Expect(Equals(autopilot, true) && banner is string text && text.Length > 0,
                "/autopilot on hands over the character with no model (the fixed rule) and the banner says so");
        }

        private static bool MentionsMissingModel(string text)
        {
            string lower = (text ?? "").ToLowerInvariant();
            return lower.Contains("not connected") || lower.Contains("model") || lower.Contains("llm");
        }

        // ---- day, night and the torch --------------------------------------------------------------------------------

        private IEnumerator CheckDayAndNightAndTorch()
        {
            RbxVector3 spawn = (RbxVector3)Workspace().GetAttribute("BlockWorldSpawn");
            int sx = Mathf.FloorToInt(spawn.X / Cell);
            int sz = Mathf.FloorToInt(spawn.Z / Cell);
            int row = Mathf.FloorToInt((spawn.Y - 4f) / Cell);
            Light sun = RenderSettings.sun;
            Workspace().SetAttribute("BlockWorldTime", "noon");
            yield return Frames(2);
            Workspace().SetAttribute("BlockWorldTime", "stop");

            RbxVector3 groundAt = new((sx + 0.5f) * Cell, row * Cell, (sz - 4.5f) * Cell);
            yield return Aim(groundAt + new RbxVector3(0f, 26f, 14f), groundAt);
            yield return Frames(10);
            Quaternion noonSun = sun != null ? sun.transform.rotation : Quaternion.identity;
            Color[] noon = Shoot("noon.png");
            RectInt ground = ProjectedRect(groundAt + new RbxVector3(-4f, 0f, -4f), groundAt + new RbxVector3(4f, 0f, 4f));
            RbxLighting lighting = (RbxLighting)CurrentWorld().Game.FindFirstChildOfClass("Lighting");

            // WHY 18:36: the sun is down, so the sky's light is all the land gets, and the land must stay readable.
            Workspace().SetAttribute("BlockWorldTime", "18:36");
            yield return Frames(10);
            float groundDusk = Luminance(Shoot("dusk.png"), ground);
            StringBuilder trials = new();
            yield return SkyTrials(lighting, ground, "dusk", trials);
            // WHY a level view from above the hills: the band just under its horizon lies past the fog's end, so it is
            // the dusk haze as drawn, which must not outshine the land it covers.
            RbxVector3 high = groundAt + new RbxVector3(0f, 60f, 0f);
            yield return Aim(high, high + new RbxVector3(400f, 0f, 0f));
            yield return Frames(5);
            RectInt band = new(ShotWidth / 10, Mathf.RoundToInt(ShotHeight * 0.43f), ShotWidth * 8 / 10,
                Mathf.RoundToInt(ShotHeight * 0.05f));
            float hazeDusk = Luminance(Shoot("dusk_horizon.png"), band);
            yield return Aim(groundAt + new RbxVector3(0f, 26f, 14f), groundAt);

            Workspace().SetAttribute("BlockWorldTime", "midnight");
            yield return Frames(10);
            Color[] midnight = Shoot("midnight.png");
            Quaternion midnightSun = sun != null ? sun.transform.rotation : Quaternion.identity;
            float groundNoon = Luminance(noon, ground);
            float groundMidnight = Luminance(midnight, ground);
            yield return SkyTrials(lighting, ground, "midnight", trials);

            // The wall: a test fixture written as the player (cells in front of the spawn), lit by a torch placed
            // through the real crosshair and hotbar.
            int wallZ = sz + 3;
            RunAsPlayer("test_blockworld_wall", @"
                local terrain = workspace.Terrain
                local min = terrain:CellCornerToWorld(" + (sx - 3) + ", " + (row + 1) + ", " + wallZ + @")
                local max = terrain:CellCornerToWorld(" + (sx + 4) + ", " + (row + 5) + ", " + (wallZ + 1) + @")
                terrain:FillRegion(Region3.new(min, max), 4, Enum.Material.Concrete)");
            yield return Frames(5);
            RbxVector3 face = new((sx + 0.5f) * Cell, (row + 2.5f) * Cell, wallZ * Cell);
            RbxVector3 camera = face + new RbxVector3(0f, 2f, -10f);
            yield return Aim(camera, face);
            yield return Tap(_keyboard.digit9Key);
            yield return Click(_mouse.rightButton);
            yield return Frames(10);
            object torches = Workspace().GetAttribute("BlockWorldTorches");
            Color[] torchOn = Shoot("night_torch_on.png");
            // WHY 1.5 to 5 studs beside the torch: where its light falls on the wall, outside the flame's own glow.
            RectInt wall = ProjectedRect(face + new RbxVector3(-5f, -3f, -0.1f), face + new RbxVector3(-1.5f, 4f, -0.1f));
            float wallOn = Luminance(torchOn, wall);

            RbxInstance folder = Workspace().FindFirstChild("BlockWorldTorches");
            RbxInstance flame = null;
            foreach (RbxInstance child in folder != null ? folder.GetChildren() : new List<RbxInstance>())
            {
                if (child.Name.StartsWith("TorchFlame_", StringComparison.Ordinal))
                {
                    flame = child;
                }
            }

            if (flame != null)
            {
                RbxVector3 flameAt = CurrentWorld().PartSink.GetLivePositionStuds(flame.Id);
                yield return Aim(camera, flameAt);
                object part = Workspace().GetAttribute("CrosshairPartName");
                yield return Click(_mouse.leftButton);
                Record("torch: aimed at the flame " + flameAt + ", crosshair part '" + part + "'");
            }

            yield return Aim(camera, face);
            yield return Frames(10);
            object torchesAfter = Workspace().GetAttribute("BlockWorldTorches");
            Color[] torchOff = Shoot("night_torch_off.png");
            float wallOff = Luminance(torchOff, wall);
            yield return NightWithTorches(sx, sz, row);
            yield return Tap(_keyboard.digit1Key);

            Record("dusk and night: ground at 18:36 " + groundDusk.ToString("0.000") + ", the haze under the horizon "
                   + hazeDusk.ToString("0.000") + "; sky light trials " + trials);
            Record("day and night: ground crop " + ground + " noon " + groundNoon.ToString("0.000") + ", midnight "
                   + groundMidnight.ToString("0.000") + "; sun " + (sun != null ? sun.name : "none") + " turned "
                   + Quaternion.Angle(noonSun, midnightSun).ToString("0.0") + " degrees; wall crop " + wall + " torch on "
                   + wallOn.ToString("0.000") + " (torches " + torches + "), removed " + wallOff.ToString("0.000")
                   + " (torches " + torchesAfter + ")");
            Expect(sun != null && Quaternion.Angle(noonSun, midnightSun) > 30f, "the day cycle turns the scene's one sun");
            Expect(groundMidnight < groundNoon * 0.5f, "midnight darkens the ground crop to under half of noon");
            Expect(groundDusk >= 0.15f && groundDusk <= 0.25f, "at 18:36, after sunset, the ground reads dim and warm (0.15-0.25)");
            Expect(hazeDusk <= groundDusk * 1.5f, "the dusk haze is no brighter than one and a half times the land it covers");
            Expect(groundMidnight >= 0.04f && groundMidnight <= 0.07f,
                "at midnight the open ground by the player reads in dim moonlight (0.04-0.07), not black");
            Expect(Equals(torches, 1d) || Equals(torches, 1), "slot 9 and a right click put one torch on the wall");
            Expect(Equals(torchesAfter, 0d) || Equals(torchesAfter, 0), "a left click on the torch takes it back");
            Expect(wallOn > wallOff + 0.02f, "the torch lights the wall crop at night, and removing it darkens it");
        }

        private bool HasGroundUnder(int sx, int sz, int row, Vector2Int[] offsets)
        {
            foreach (Vector2Int offset in offsets)
            {
                bool ground = false;
                for (int y = row; y > row - 6 && !ground; y--)
                {
                    ground = Terrain().Store.GetMaterial(sx + offset.x, y, sz + offset.y) != TerrainMaterials.Air;
                }

                if (!ground)
                {
                    return false;
                }
            }

            return true;
        }

        private IEnumerator NightWithTorches(int sx, int sz, int row)
        {
            RbxVector3 eye = RootPosition(Humanoid()) + new RbxVector3(0f, 1.5f, 0f);
            Vector2Int[] offsets = { new(2, -3), new(-2, -3), new(3, -1), new(-3, -1) };
            // WHY wait: these cells lie behind the first view, and the world streams what the camera faces first.
            for (int frame = 0; frame < 1800 && !HasGroundUnder(sx, sz, row, offsets); frame++)
            {
                yield return null;
            }

            // WHY open ground near the spawn's level, nearest first, and a look before each click: on hilly ground
            // with woods a cell can be a step up, a trunk or hidden behind a crown or a villager, and the click then
            // targets that instead; a cell the crosshair does not outline is skipped for the next one.
            TerrainMaterials.TryGetSlot("Mud", out byte trunkSlot);
            TerrainMaterials.TryGetSlot("LeafyGrass", out byte leafSlot);
            List<Vector3Int> open = new();
            for (int dx = -4; dx <= 4; dx++)
            {
                for (int dz = -4; dz <= 4; dz++)
                {
                    int cx = sx + dx;
                    int cz = sz + dz;
                    int ground = GroundTop(Terrain(), cx, cz, trunkSlot, leafSlot);
                    if (Math.Max(Math.Abs(dx), Math.Abs(dz)) >= 2 && Math.Abs(ground - (row - 1)) <= 1
                        && Terrain().Store.GetMaterial(cx, ground + 1, cz) == TerrainMaterials.Air
                        && Terrain().Store.GetMaterial(cx, ground + 2, cz) == TerrainMaterials.Air)
                    {
                        open.Add(new Vector3Int(cx, ground, cz));
                    }
                }
            }

            open.Sort((a, b) => ((a.x - sx) * (a.x - sx) + (a.z - sz) * (a.z - sz))
                .CompareTo((b.x - sx) * (b.x - sx) + (b.z - sz) * (b.z - sz)));
            int placed = 0;
            int skipped = 0;
            foreach (Vector3Int spot in open)
            {
                if (placed == 4)
                {
                    break;
                }

                int cx = spot.x;
                int cz = spot.z;
                int top = spot.y;

                RbxVector3 floor = new((cx + 0.5f) * Cell, (top + 1f) * Cell, (cz + 0.5f) * Cell);
                yield return Aim(eye + new RbxVector3(0f, 2f, 0f), floor);
                object target = Workspace().GetAttribute("CrosshairPlaceCell");
                if (!(target is RbxVector3 cell) || (int)cell.X != cx || (int)cell.Y != top + 1 || (int)cell.Z != cz)
                {
                    skipped++;
                    continue;
                }

                object before = Workspace().GetAttribute("BlockWorldTorches");
                yield return Click(_mouse.rightButton);
                placed += Equals(Workspace().GetAttribute("BlockWorldTorches"), before) ? 0 : 1;
            }

            RbxVector3 middle = new((sx + 0.5f) * Cell, row * Cell, (sz - 2f) * Cell);
            yield return Aim(ClearView(middle, middle + new RbxVector3(0f, 14f, -26f)), middle);
            yield return Frames(10);
            yield return ShootWithUi("night_torches.png");
            Record("night view: " + placed + " torches put on the ground (" + skipped + " of " + open.Count
                   + " open cells hidden from the eye), " + Workspace().GetAttribute("BlockWorldTorches") + " lit");
            Expect(placed == 4, "four right clicks with the torch in hand put four torches on the ground");
        }

        // ---- survival ----------------------------------------------------------------------------------------------

        private IEnumerator CheckSurvivalFalls()
        {
            Workspace().SetAttribute("BlockWorldCreative", false);
            yield return Frames(5);
            RbxHumanoid humanoid = Humanoid();
            RbxVector3 start = RootPosition(humanoid);
            RunAsPlayer("test_survival_drop", "local c = game:GetService('Players'):GetPlayers()[1].Character "
                                              + "c:PivotTo(CFrame.new(c.HumanoidRootPart.Position + Vector3.new(0, 40, 0)))");
            yield return WaitUntilLanded(humanoid);
            double afterFall = humanoid.Health;
            object hudHealth = Workspace().GetAttribute("BlockWorldHealth");
            object notice = Workspace().GetAttribute("BlockWorldNotice");
            yield return ShootWithUi("survival_after_fall.png");

            RunAsPlayer("test_survival_lethal", "local c = game:GetService('Players'):GetPlayers()[1].Character "
                                                + "c:PivotTo(CFrame.new(c.HumanoidRootPart.Position + Vector3.new(0, 90, 0)))");
            yield return WaitUntilLanded(humanoid);
            double afterLethal = humanoid.Health;
            object deaths = Workspace().GetAttribute("BlockWorldDeaths");
            object deathNotice = Workspace().GetAttribute("BlockWorldNotice");
            RbxHumanoid next = null;
            for (int frame = 0; frame < 600 && (next == null || next == humanoid); frame++)
            {
                yield return null;
                next = Humanoid();
            }

            yield return Frames(30);
            RbxVector3 spawn = (RbxVector3)Workspace().GetAttribute("BlockWorldSpawn");
            RbxVector3 back = next != null ? RootPosition(next) : default;
            Record("survival: start " + start + "; a 40-stud drop left health " + afterFall.ToString("0.0") + " (HUD "
                   + hudHealth + ", notice '" + notice + "'); a 90-stud drop left " + afterLethal.ToString("0.0")
                   + ", deaths " + deaths + ", notice '" + deathNotice + "'; respawned " + (next != null && next != humanoid)
                   + " at " + back + " (spawn " + spawn + "), health " + (next != null ? next.Health.ToString("0") : "?"));
            Expect(afterFall < 100d && afterFall > 0d, "a fall of ten blocks in survival costs health and does not kill");
            Expect(Equals(hudHealth, afterFall) || System.Math.Abs(System.Convert.ToDouble(hudHealth ?? -1d) - afterFall) < 1d,
                "the HUD shows the health that is left");
            Expect(afterLethal <= 0d && Equals(System.Convert.ToDouble(deaths ?? 0d), 1d), "a fall of 22 blocks kills, once");
            Expect(next != null && next != humanoid && next.Health >= 100d
                   && (new RbxVector3(back.X, 0f, back.Z) - new RbxVector3(spawn.X, 0f, spawn.Z)).Magnitude < 8f,
                "the player comes back at the spawn with full health");
        }

        private static IEnumerator WaitUntilLanded(RbxHumanoid humanoid)
        {
            bool airborne = false;
            for (int frame = 0; frame < 900; frame++)
            {
                yield return null;
                RbxHumanoidState state = humanoid.GetState();
                airborne |= state == RbxHumanoidState.Freefall;
                if (airborne && (state == RbxHumanoidState.Running || state == RbxHumanoidState.Landed
                                 || state == RbxHumanoidState.Dead || humanoid.Health <= 0d))
                {
                    break;
                }
            }

            yield return Frames(10);
        }

        // ---- a cave at noon --------------------------------------------------------------------------------------------

        private IEnumerator CheckCaveAtNoon(bool expectDark)
        {
            Workspace().SetAttribute("BlockWorldTime", "noon");
            yield return Frames(2);
            Workspace().SetAttribute("BlockWorldTime", "stop");
            RbxVector3 spawn = (RbxVector3)Workspace().GetAttribute("BlockWorldSpawn");
            int sx = Mathf.FloorToInt(spawn.X / Cell);
            int sz = Mathf.FloorToInt(spawn.Z / Cell);
            RbxTerrain terrain = Terrain();
            Vector3Int found = default;
            Vector3Int along = default;
            bool any = false;
            for (int radius = 6; radius <= 40 && !any; radius++)
            {
                for (int dx = -radius; dx <= radius && !any; dx++)
                {
                    foreach (int dz in new[] { -radius, radius })
                    {
                        if (TryFindTunnel(terrain, sx + dx, sz + dz, out found, out along))
                        {
                            any = true;
                            break;
                        }
                    }
                }
            }

            Expect(any, "a tunnel two cells tall under at least three cells of rock lies within 40 cells of the spawn");
            if (!any)
            {
                yield break;
            }

            RbxVector3 floorTop = new((found.x + 0.5f) * Cell, found.y * Cell, (found.z + 0.5f) * Cell);
            RunAsPlayer("test_cave_stand", "game:GetService('Players'):GetPlayers()[1].Character:PivotTo(CFrame.new("
                                           + Lua(floorTop.X) + ", " + Lua(floorTop.Y + 3.2f) + ", " + Lua(floorTop.Z)
                                           + "))");
            yield return Frames(30);
            RbxVector3 eye = floorTop + new RbxVector3(0f, 6f, 0f);
            RbxVector3 ahead = floorTop + new RbxVector3(along.x * 3f * Cell, 2f, along.z * 3f * Cell);
            yield return Aim(eye, ahead);
            yield return Frames(30);
            Color[] dark = Shoot("cave_noon_no_torch.png");
            RbxVector3 side = new(along.z * 1.5f, 0f, -along.x * 1.5f);
            RbxVector3 nearFloor = floorTop + new RbxVector3(along.x * Cell * 1.2f, 0f, along.z * Cell * 1.2f);
            RbxVector3 farFloor = floorTop + new RbxVector3(along.x * Cell * 2.4f, 0f, along.z * Cell * 2.4f);
            // WHY the middle of the view: the tunnel's walls ahead are what a torch lights; the floor near the camera
            // lies in the shadow of its own step.
            RectInt crop = new(ShotWidth / 4, ShotHeight / 4, ShotWidth / 2, ShotHeight / 2);
            float darkLuminance = Luminance(dark, crop);

            // WHY the same view at midnight: the sky does not reach a cave, so its floor at noon is as dark as at night.
            Workspace().SetAttribute("BlockWorldTime", "midnight");
            yield return Frames(3);
            Workspace().SetAttribute("BlockWorldTime", "stop");
            yield return Frames(5);
            float midnightLuminance = Luminance(Shoot("cave_midnight_no_torch.png"), crop);
            Workspace().SetAttribute("BlockWorldTime", "noon");
            yield return Frames(3);
            Workspace().SetAttribute("BlockWorldTime", "stop");
            yield return Frames(5);

            // WHY trial values: Lighting.Ambient is chosen on these shots (pitch black against walls barely seen); a
            // Heartbeat connected after the day-night mod's holds each one for its shot, and unloading drops it.
            StringBuilder trials = new();
            RbxLighting caveLighting = (RbxLighting)CurrentWorld().Game.FindFirstChildOfClass("Lighting");
            RbxColor3 modAmbient = caveLighting.Ambient;
            foreach ((int r, int g, int b) in new[] { (34, 40, 74), (56, 60, 80), (80, 84, 104) })
            {
                string trialId = "test_cave_ambient_" + r;
                RunAsPlayer(trialId, "local Lighting = game:GetService('Lighting') "
                                     + "game:GetService('RunService').Heartbeat:Connect(function() "
                                     + "Lighting.Ambient = Color3.fromRGB(" + r + ", " + g + ", " + b + ") end)");
                yield return Frames(5);
                float trial = Luminance(Shoot("cave_noon_ambient_" + r + "_" + g + "_" + b + ".png"), crop);
                Session().CurrentConcreteRuntime.UnloadMod(PlayerActor(), trialId);
                trials.Append(r).Append(',').Append(g).Append(',').Append(b).Append(" -> ").Append(trial.ToString("0.000"))
                    .Append("; ");
            }

            // WHY put back here: the day-night mod writes Ambient only when the daylight changes, and the clock stands.
            caveLighting.SetAmbient(modAmbient);
            yield return Frames(5);

            // WHY the torch at the camera's own cell: its light reaches the floor crop ahead, its flame stays out of it.
            RbxVector3 torchFloor = floorTop - new RbxVector3(along.x * 1.2f, 0f, along.z * 1.2f);
            yield return Tap(_keyboard.digit9Key);
            yield return Aim(eye, torchFloor);
            yield return Click(_mouse.rightButton);
            object torches = Workspace().GetAttribute("BlockWorldTorches");
            yield return Aim(eye, ahead);
            yield return Frames(10);
            Color[] lit = Shoot("cave_noon_torch.png");
            float litLuminance = Luminance(lit, crop);
            yield return Tap(_keyboard.digit1Key);

            Record("cave: tunnel cell " + found + " heading " + along + "; floor crop " + crop + " at noon without a "
                   + "torch " + darkLuminance.ToString("0.000") + ", at midnight " + midnightLuminance.ToString("0.000")
                   + ", at noon with one (torches " + torches + ") " + litLuminance.ToString("0.000")
                   + "; Ambient trials without a torch: " + trials);
            if (expectDark)
            {
                Expect(darkLuminance <= midnightLuminance + 0.015f,
                    "the cave floor at noon is as dark as at midnight: the sky does not reach it");
            }

            Expect(litLuminance > darkLuminance + 0.02f && litLuminance > darkLuminance * 2f,
                "a torch clearly lights the cave");
        }

        /// <summary>
        /// A floor cell (x, y, z) whose two cells above are empty, with at least three solid cells of roof above them,
        /// and a neighbour that continues the tunnel along x or z.
        /// </summary>
        private static bool TryFindTunnel(RbxTerrain terrain, int x, int z, out Vector3Int floor, out Vector3Int along)
        {
            floor = default;
            along = default;
            for (int y = 8; y > -14; y--)
            {
                if (!Solid(terrain, x, y, z) || Solid(terrain, x, y + 1, z) || Solid(terrain, x, y + 2, z)
                    || !Solid(terrain, x, y + 3, z) || !Solid(terrain, x, y + 4, z) || !Solid(terrain, x, y + 5, z))
                {
                    continue;
                }

                foreach (Vector3Int step in new[] { Vector3Int.right, Vector3Int.left, new Vector3Int(0, 0, 1), new Vector3Int(0, 0, -1) })
                {
                    if (Solid(terrain, x + step.x, y, z + step.z) && !Solid(terrain, x + step.x, y + 1, z + step.z)
                        && !Solid(terrain, x + step.x, y + 2, z + step.z))
                    {
                        floor = new Vector3Int(x, y + 1, z);
                        along = step;
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>A number as Lua source, whatever the machine's culture.</summary>
        private static string Lua(float value)
        {
            return value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static bool Solid(RbxTerrain terrain, int x, int y, int z)
        {
            byte material = terrain.Store.GetMaterial(x, y, z);
            return material != TerrainMaterials.Air && material != TerrainMaterials.Water;
        }

        // ---- build here with a scripted model, then /undo -------------------------------------------------------------

        private IEnumerator CheckWatchtowerAndUndo(ScriptedBlockWorldModel model)
        {
            RbxVector3 eye = RootPosition(Humanoid()) + new RbxVector3(0f, 1.5f, 0f);
            yield return Aim(eye, eye + new RbxVector3(0f, -6f, 9f));
            bool targeted = Equals(Workspace().GetAttribute("CrosshairHasTarget"), true);
            Expect(targeted, "the ground ahead is under the crosshair");
            if (!targeted)
            {
                yield break;
            }

            RbxVector3 baseCell = (RbxVector3)Workspace().GetAttribute("CrosshairPlaceCell");
            int x = (int)baseCell.X;
            int y = (int)baseCell.Y;
            int z = (int)baseCell.Z;
            Dictionary<long, (byte, byte)> before = ReadBox(Terrain(), x - 6, y - 3, z - 6, x + 6, y + 13, z + 6);
            string goldenBefore = TerrainHash(Terrain());

            ChatOverlayController overlay = Overlay();
            int start = overlay.Feed.Lines.Count;
            overlay.Submit("build a watchtower here");
            yield return WaitForLine(overlay, start,
                line => line.Kind == ChatFeedLineKind.Assistant && !line.IsStreaming && line.Text.Length > 0, 1200);
            yield return Frames(10);
            string reply = Lines(overlay, start);
            Dictionary<long, (byte, byte)> after = ReadBox(Terrain(), x - 6, y - 3, z - 6, x + 6, y + 13, z + 6);
            int changed = 0;
            int outside = 0;
            foreach (KeyValuePair<long, (byte, byte)> cell in before)
            {
                if (after[cell.Key] != cell.Value)
                {
                    changed++;
                    Decode(cell.Key, out int cx, out int cy, out int cz);
                    bool inside = cx >= x - 2 && cx <= x + 2 && cy >= y && cy <= y + 9 && cz >= z - 2 && cz <= z + 2;
                    outside += inside ? 0 : 1;
                }
            }

            TerrainMaterials.TryGetSlot("Cobblestone", out byte cobblestone);
            TerrainMaterials.TryGetSlot("WoodPlanks", out byte planks);
            bool tower = Terrain().Store.GetMaterial(x - 1, y, z - 1) == cobblestone
                         && Terrain().Store.GetMaterial(x, y + 8, z) == planks;
            // WHY /undo before any new aim: a script that moves the camera edits the world, and /undo refuses
            // after any edit that followed the AI's change (the documented rule of the undo history).
            Dictionary<ulong, long> revisions = Revisions();
            int undoStart = overlay.Feed.Lines.Count;
            overlay.Submit("/undo");
            yield return WaitForLine(overlay, undoStart, line => line.Kind != ChatFeedLineKind.Player, 900);
            yield return Frames(30);
            string undoReply = Lines(overlay, undoStart);
            string changedSince = ChangedSince(revisions);
            string goldenAfter = TerrainHash(Terrain());

            int redoStart = overlay.Feed.Lines.Count;
            overlay.Submit("/redo");
            yield return WaitForLine(overlay, redoStart, line => line.Kind != ChatFeedLineKind.Player, 900);
            yield return Frames(30);
            string redoReply = Lines(overlay, redoStart);
            bool redone = Terrain().Store.GetMaterial(x - 1, y, z - 1) == cobblestone
                          && Terrain().Store.GetMaterial(x, y + 8, z) == planks;
            yield return Aim(eye + new RbxVector3(-26f, 22f, -14f), new RbxVector3((x + 0.5f) * Cell, (y + 4f) * Cell, (z + 0.5f) * Cell));
            yield return Frames(5);
            yield return ShootWithUi("watchtower.png");

            Record("watchtower: base " + baseCell + ", the request told the model 'here' is '" + model.HereAttribute
                   + "', execute_lua result '" + model.LastToolResult + "', reply '" + reply + "'; cells changed "
                   + changed + " (outside the 5 x 10 x 5 box: " + outside + "), tower present " + tower
                   + "; /undo answered '" + undoReply + "'; world instances changed between the build and /undo: "
                   + changedSince + "; terrain golden " + goldenBefore + " -> " + goldenAfter + "; /redo answered '"
                   + redoReply + "', tower back " + redone);
            Expect(model.SawCrosshairAttribute && model.HereAttribute == "CrosshairPlaceCell",
                "the turn told the model, in its world state, that \"here\" is the attribute CrosshairPlaceCell");
            Expect(model.ToolCalls == 1, "the scripted model called execute_lua once");
            Expect(tower && changed > 50, "the watchtower stands at the crosshair");
            Expect(outside == 0, "every cell the build wrote is inside the box at the crosshair");
            Expect(goldenBefore == goldenAfter, "/undo restores the terrain byte-identically");
            Expect(redone, "/redo puts the watchtower back");
        }

        /// <summary>Revisions of the instances a world package keeps (not mod-owned, not runtime infrastructure).</summary>
        private Dictionary<ulong, long> Revisions()
        {
            Dictionary<ulong, long> revisions = new();
            InstanceRegistry registry = CurrentWorld().Registry;
            foreach (RbxInstance instance in registry.GetLiveInstances())
            {
                if (instance != null && registry.TryGetRecord(instance.Id, out InstanceRecord record)
                    && string.IsNullOrEmpty(record.OwnerModId) && !record.IsRuntimeInfrastructure)
                {
                    revisions[instance.Id.Value] = record.Revision;
                }
            }

            return revisions;
        }

        private string ChangedSince(Dictionary<ulong, long> before)
        {
            List<string> changed = new();
            InstanceRegistry registry = CurrentWorld().Registry;
            foreach (RbxInstance instance in registry.GetLiveInstances())
            {
                if (instance == null || !registry.TryGetRecord(instance.Id, out InstanceRecord record)
                    || !string.IsNullOrEmpty(record.OwnerModId) || record.IsRuntimeInfrastructure)
                {
                    continue;
                }

                if (!before.TryGetValue(instance.Id.Value, out long revision) || revision != record.Revision)
                {
                    changed.Add(instance.GetFullName() + " (" + instance.ClassName + ")");
                }
            }

            return changed.Count == 0 ? "none" : string.Join(", ", changed);
        }

        private static Dictionary<long, (byte, byte)> ReadBox(RbxTerrain terrain, int x0, int y0, int z0, int x1, int y1,
            int z1)
        {
            Dictionary<long, (byte, byte)> cells = new();
            for (int x = x0; x <= x1; x++)
            {
                for (int y = y0; y <= y1; y++)
                {
                    for (int z = z0; z <= z1; z++)
                    {
                        terrain.Store.GetCell(x, y, z, out byte material, out byte occupancy);
                        cells[Encode(x, y, z)] = (material, occupancy);
                    }
                }
            }

            return cells;
        }

        private static long Encode(int x, int y, int z)
        {
            return ((long)(x + 100000) << 40) | ((long)(y + 100000) << 20) | (long)(z + 100000);
        }

        private static void Decode(long key, out int x, out int y, out int z)
        {
            x = (int)(key >> 40) - 100000;
            y = (int)((key >> 20) & 0xFFFFF) - 100000;
            z = (int)(key & 0xFFFFF) - 100000;
        }

        /// <summary>FNV-1a over every cell of every stored chunk, in chunk-key order.</summary>
        private static string TerrainHash(RbxTerrain terrain)
        {
            List<TerrainChunkCoord> coords = new();
            terrain.Store.CopyChunkCoords(coords);
            coords.Sort((a, b) => a.Key.CompareTo(b.Key));
            uint hash = 2166136261;
            int size = TerrainChunkCoord.Size;
            foreach (TerrainChunkCoord coord in coords)
            {
                for (int x = 0; x < size; x++)
                {
                    for (int y = 0; y < size; y++)
                    {
                        for (int z = 0; z < size; z++)
                        {
                            terrain.Store.GetCell(coord.X * size + x, coord.Y * size + y, coord.Z * size + z,
                                out byte material, out byte occupancy);
                            if (occupancy == 0)
                            {
                                continue;
                            }

                            hash = (hash ^ material) * 16777619;
                            hash = (hash ^ occupancy) * 16777619;
                            hash = (hash ^ (uint)(x | (y << 5) | (z << 10))) * 16777619;
                            hash = (hash ^ (uint)coord.Key) * 16777619;
                        }
                    }
                }
            }

            return hash.ToString("x8") + "/" + coords.Count + " chunks";
        }

        // ---- a villager answers ------------------------------------------------------------------------------------

        private IEnumerator CheckVillagerAnswers(ScriptedBlockWorldModel model)
        {
            ChatOverlayController overlay = Overlay();
            RbxInstance tobin = Workspace().FindFirstChild("Tobin");
            object agent = null;
            INpcDirector npcs = _bootstrap.Composition.CoreScope.Container.Resolve<INpcDirector>();
            StringBuilder heardBy = new();
            Action<NpcAddressed> addressed = e => heardBy.Append("addressed ").Append(e.NpcName).Append("; ");
            Action<NpcLineIgnored> ignored = e => heardBy.Append("ignored (").Append(e.Reason).Append(", '")
                .Append(e.NpcId).Append("'); ");
            NpcListenerPositionRelay listenerRelay = _bootstrap.Composition.CoreScope.Container
                .Resolve<NpcListenerPositionRelay>();
            string atSend = "";
            for (int frame = 0; frame < 900; frame++)
            {
                NpcPosition? tobinAt = null;
                foreach (NpcAgent listed in npcs.Agents)
                {
                    if (listed.Name == "Tobin")
                    {
                        tobinAt = listed.Position;
                    }
                }

                // WHY wait for the brain: the mod gives it once Tobin stands on his finished column, which can come
                // a few columns after the spawn's; until then his agent sits at the origin.
                NpcPosition? playerAt = listenerRelay.Current;
                agent = tobin?.GetAttribute("NpcAgent");
                atSend = "Tobin " + tobinAt + ", listener " + playerAt + ", brain " + (agent != null) + " after " + frame
                         + " frames";
                if (agent != null && tobinAt.HasValue && playerAt.HasValue
                    && tobinAt.Value.DistanceTo(playerAt.Value) <= 40d)
                {
                    break;
                }

                yield return null;
            }

            Record("before the chat line: " + atSend);
            npcs.NpcAddressed += addressed;
            npcs.LineIgnored += ignored;
            int start = overlay.Feed.Lines.Count;
            overlay.Submit("Tobin, what should I build next?");
            yield return WaitForLine(overlay, start,
                line => line.Speaker == "Tobin" && !line.IsStreaming && line.Text.Length > 0, 1200);
            npcs.NpcAddressed -= addressed;
            npcs.LineIgnored -= ignored;
            string lines = Lines(overlay, start);
            Record("the chat line was heard as: " + (heardBy.Length == 0 ? "nothing (no NPC event)" : heardBy.ToString()));
            string probe = "";
            if (!lines.Contains("Tobin: "))
            {
                INpcDirector director = _bootstrap.Composition.CoreScope.Container.Resolve<INpcDirector>();
                NpcListenerPositionRelay listener = _bootstrap.Composition.CoreScope.Container
                    .Resolve<NpcListenerPositionRelay>();
                NpcHearResult heard = director.Hear(NpcHeardLine.Chat("player", "Player1",
                    "Tobin, what should I build next?", listener.Current));
                StringBuilder agents = new();
                foreach (NpcAgent listed in director.Agents)
                {
                    agents.Append(listed.Id).Append(" at ").Append(listed.Position).Append("; ");
                }

                probe = "; a direct Hear of the same line: addressed " + heard.Decision.IsAddressed + " ("
                        + heard.Decision.Reason + ", ignored " + heard.Decision.IgnoreReason + ", npc '"
                        + heard.Decision.NpcId + "'), listener at " + listener.Current + ", agents " + agents;
                yield return WaitForLine(overlay, start,
                    line => line.Speaker == "Tobin" && !line.IsStreaming && line.Text.Length > 0, 600);
                lines = Lines(overlay, start);
            }

            object reply = Workspace().GetAttribute("BlockWorldVillagerReply");
            NpcSpeechBubbleBoard board = _bootstrap.Composition.CoreScope.Container.Resolve<NpcSpeechBubbleBoard>();
            string bubble = "";
            foreach (NpcSpeechBubble candidate in board.Bubbles)
            {
                if (candidate.NpcName == "Tobin")
                {
                    bubble = candidate.Text + (candidate.IsVisible ? "" : " (hidden)");
                }
            }

            if (tobin?.FindFirstChild("HumanoidRootPart") != null)
            {
                RbxVector3 at = CurrentWorld().PartSink.GetLivePositionStuds(tobin.FindFirstChild("HumanoidRootPart").Id);
                RbxVector3 face = at + new RbxVector3(0f, 1.5f, 0f);
                yield return Aim(ClearView(face, at + new RbxVector3(-7f, 3f, -7f), tobin), face);
                yield return Frames(3);
            }

            yield return ShootWithUi("villager_talking.png");
            Record("what the villager frame shows at its right edge: " + WhatIsAt(0.9f, 0.45f) + " | " + WhatIsAt(0.95f, 0.25f));
            int drawn = CountShownBubbles("Tobin");
            Record("villager: Tobin's agent " + agent + "; the model was asked as " + model.NpcPersonaSeen
                   + "; chat '" + lines + "'" + probe + "; BlockWorldVillagerReply '" + reply + "'; bubble '" + bubble
                   + "', drawn " + drawn + " time(s)");
            Expect(probe.Length == 0, "the chat line that names Tobin reaches Tobin through the chat's hearing");
            Expect(agent != null, "Tobin has an AIService agent (AI from Lua is on in this scene)");
            Expect(model.NpcPersonaSeen.Contains("Tobin"), "the turn reached the model with Tobin's persona");
            Expect(lines.Contains("Tobin: ") && lines.Contains(ScriptedBlockWorldModel.TobinReply),
                "Tobin answers in the chat by name");
            Expect(bubble.StartsWith(ScriptedBlockWorldModel.TobinReply, StringComparison.Ordinal),
                "Tobin's answer shows in a bubble over his head");
            Expect(drawn == 1, "the bubble document draws Tobin's answer once");
        }

        /// <summary>Bubble elements of <paramref name="npcName"/> the NPC speech document shows now.</summary>
        private static int CountShownBubbles(string npcName)
        {
            int shown = 0;
            foreach (UIDocument document in Object.FindObjectsByType<UIDocument>(FindObjectsSortMode.None))
            {
                if (document.gameObject.name != NpcSpeechBubbleSurface.GameObjectName || document.rootVisualElement == null)
                {
                    continue;
                }

                foreach (VisualElement element in document.rootVisualElement.Query(className: NpcSpeechBubbleView.BubbleClass)
                             .ToList())
                {
                    Label name = element.Q<Label>(className: NpcSpeechBubbleView.NameClass);
                    if (element.style.display.value != DisplayStyle.None && name != null && name.text == npcName)
                    {
                        shown++;
                    }
                }
            }

            return shown;
        }

        // ---- helpers -------------------------------------------------------------------------------------------------

        private string ClockTime()
        {
            RbxInstance lighting = CurrentWorld().Game.FindFirstChildOfClass("Lighting");
            return lighting is RbxLighting clock ? clock.ClockTime.ToString("0.00") : "?";
        }

        private RbxWorldRuntimeSessionController Session()
        {
            return _bootstrap.Composition.ModsScope.Container.Resolve<RbxWorldRuntimeSessionController>();
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

        private RbxTerrain Terrain()
        {
            return RbxTerrainBootstrap.Find(Workspace());
        }

        private IReadOnlyList<RbxPlayer> Players()
        {
            return CurrentWorld().Players.GetPlayers();
        }

        private RbxHumanoid Humanoid()
        {
            return CoreAiLocalPlayer.FindHumanoid(CurrentWorld());
        }

        private RbxVector3 RootPosition(RbxHumanoid humanoid)
        {
            return CurrentWorld().PartSink.GetLivePositionStuds(humanoid.RootPart.Id);
        }

        private ActorContext PlayerActor()
        {
            return _bootstrap.Composition.ModsScope.Container.Resolve<IActorIdentityProvider>()
                .GetActorContext(BuiltInAgentRoleIds.Programmer);
        }

        private ChatOverlayController Overlay()
        {
            return _bootstrap.Composition.CoreScope.Container.Resolve<ChatOverlayController>();
        }

        private void RunAsPlayer(string id, string code)
        {
            Session().CurrentConcreteRuntime.LoadMod(PlayerActor(), id, code, LuaCapabilities.All, false);
        }

        private IEnumerator Aim(RbxVector3 from, RbxVector3 at)
        {
            if (!Session().CurrentConcreteRuntime.IsLoaded(PlayerActor(), AimModId))
            {
                RunAsPlayer(AimModId, AimSource);
            }

            Workspace().SetAttribute("TestAimFrom", from);
            Workspace().SetAttribute("TestAimAt", at);
            // WHY wait for the pose and not a fixed count: a shot taken before the camera moved measures the old view.
            Camera camera = _bootstrap.Composition.Camera;
            Vector3 wantedAt = RbxSpace.ToUnity(from);
            Vector3 wantedForward = (RbxSpace.ToUnity(at) - wantedAt).normalized;
            for (int frame = 0; frame < 120 && camera != null; frame++)
            {
                yield return null;
                if ((camera.transform.position - wantedAt).magnitude < 0.01f
                    && Vector3.Angle(camera.transform.forward, wantedForward) < 0.5f)
                {
                    break;
                }
            }

            yield return Frames(2);
        }

        private RbxVector3 OutlineCenter()
        {
            RbxInstance outline = Workspace().FindFirstChild("BlockWorldOutline");
            if (outline == null)
            {
                return new RbxVector3(float.NaN, float.NaN, float.NaN);
            }

            RbxVector3 sum = new(0f, 0f, 0f);
            int count = 0;
            foreach (RbxInstance edge in outline.GetChildren())
            {
                sum += CurrentWorld().PartSink.GetLivePositionStuds(edge.Id);
                count++;
            }

            return count == 0 ? sum : sum / count;
        }

        private IEnumerator Tap(ButtonControl button)
        {
            yield return null;
            Press(button);
            yield return Frames(2);
            Release(button);
            yield return Frames(2);
        }

        private IEnumerator Click(ButtonControl button)
        {
            Set(_mouse.position, new Vector2(Screen.width * 0.5f, Screen.height * 0.5f));
            yield return Tap(button);
        }

        private static IEnumerator Frames(int count)
        {
            for (int frame = 0; frame < count; frame++)
            {
                yield return null;
            }
        }

        private static IEnumerator WaitForLine(ChatOverlayController overlay, int start, Func<ChatFeedLine, bool> match,
            int maxFrames)
        {
            for (int frame = 0; frame < maxFrames; frame++)
            {
                for (int index = start; index < overlay.Feed.Lines.Count; index++)
                {
                    if (match(overlay.Feed.Lines[index]))
                    {
                        yield break;
                    }
                }

                yield return null;
            }
        }

        private static string Lines(ChatOverlayController overlay, int start)
        {
            StringBuilder text = new();
            for (int index = start; index < overlay.Feed.Lines.Count; index++)
            {
                ChatFeedLine line = overlay.Feed.Lines[index];
                if (line.Kind == ChatFeedLineKind.Player)
                {
                    continue;
                }

                text.Append(line.Speaker).Append(": ").Append(line.Text).Append(" | ");
            }

            return text.ToString();
        }

        /// <summary>Renders the composed camera into a half-float target and returns the frame as a display shows it.</summary>
        private Color[] Shoot(string fileName)
        {
            Camera camera = _bootstrap.Composition.Camera;
            RenderTexture target = new(ShotWidth, ShotHeight, 24, RenderTextureFormat.DefaultHDR);
            Texture2D linear = new(ShotWidth, ShotHeight, TextureFormat.RGBAFloat, false, true);
            RenderTexture previousActive = RenderTexture.active;
            try
            {
                RenderPipeline.StandardRequest request = new() { destination = target };
                RenderPipeline.SubmitRenderRequest(camera, request);
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

                WritePng(fileName, pixels);
                return pixels;
            }
            finally
            {
                RenderTexture.active = previousActive;
                Object.Destroy(target);
                Object.Destroy(linear);
            }
        }

        /// <summary>
        /// The camera frame with the UI Toolkit panels (HUD, chat overlay, bubbles) composited over it in sorting
        /// order: every panel draws into a transparent target of its own for two frames, then is put back.
        /// </summary>
        /// <remarks>
        /// WHY a cleared texture per panel: a panel redrawn into its target keeps what an earlier frame drew unless it
        /// clears, and a world-anchored element (an NPC bubble) moves when the panel's target changes size, so one
        /// shared, uncleared texture showed such an element twice.
        /// </remarks>
        private IEnumerator ShootWithUi(string fileName)
        {
            Color[] scene = Shoot("_scene_" + fileName);
            List<(PanelSettings Panel, RenderTexture Previous, bool Clears, Color ClearValue, RenderTexture Target)>
                panels = new();
            foreach (UIDocument document in Object.FindObjectsByType<UIDocument>(FindObjectsSortMode.None))
            {
                PanelSettings panel = document.panelSettings;
                if (panel == null || !document.isActiveAndEnabled || panels.Exists(entry => entry.Panel == panel))
                {
                    continue;
                }

                RenderTexture target = new(ShotWidth, ShotHeight, 24, RenderTextureFormat.ARGB32,
                    RenderTextureReadWrite.sRGB);
                target.Create();
                panels.Add((panel, panel.targetTexture, panel.clearColor, panel.colorClearValue, target));
                panel.clearColor = true;
                panel.colorClearValue = Color.clear;
                panel.targetTexture = target;
            }

            panels.Sort((first, second) => first.Panel.sortingOrder.CompareTo(second.Panel.sortingOrder));
            yield return null;
            yield return null;
            Color[] composed = (Color[])scene.Clone();
            Texture2D read = new(ShotWidth, ShotHeight, TextureFormat.RGBA32, false);
            RenderTexture previousActive = RenderTexture.active;
            foreach ((PanelSettings panel, RenderTexture previous, bool clears, Color clearValue, RenderTexture target)
                     in panels)
            {
                RenderTexture.active = target;
                read.ReadPixels(new Rect(0, 0, ShotWidth, ShotHeight), 0, 0);
                read.Apply();
                Color[] overlay = read.GetPixels();
                for (int index = 0; index < composed.Length; index++)
                {
                    Color top = overlay[index];
                    Color under = composed[index];
                    composed[index] = new Color(top.r + under.r * (1f - top.a), top.g + under.g * (1f - top.a),
                        top.b + under.b * (1f - top.a), 1f);
                }

                panel.targetTexture = previous;
                panel.clearColor = clears;
                panel.colorClearValue = clearValue;
                target.Release();
                Object.Destroy(target);
            }

            RenderTexture.active = previousActive;
            WritePng(fileName, composed);
            Object.Destroy(read);
            Record("ui shot " + fileName + ": " + panels.Count + " panels composited");
        }

        private void WritePng(string fileName, Color[] pixels)
        {
            Texture2D png = new(ShotWidth, ShotHeight, TextureFormat.RGBA32, false);
            try
            {
                png.SetPixels(pixels);
                png.Apply();
                File.WriteAllBytes(Path.Combine(_outputFolder, fileName), png.EncodeToPNG());
            }
            finally
            {
                Object.Destroy(png);
            }
        }

        /// <summary>The screen rectangle spanned by two opposite corners given in studs.</summary>
        private RectInt ProjectedRect(RbxVector3 firstStuds, RbxVector3 secondStuds)
        {
            Camera camera = _bootstrap.Composition.Camera;
            Vector3 first = camera.WorldToViewportPoint(RbxSpace.ToUnity(firstStuds));
            Vector3 second = camera.WorldToViewportPoint(RbxSpace.ToUnity(secondStuds));
            int xMin = Mathf.Clamp(Mathf.RoundToInt(Mathf.Min(first.x, second.x) * ShotWidth), 0, ShotWidth - 1);
            int xMax = Mathf.Clamp(Mathf.RoundToInt(Mathf.Max(first.x, second.x) * ShotWidth), xMin + 1, ShotWidth);
            int yMin = Mathf.Clamp(Mathf.RoundToInt(Mathf.Min(first.y, second.y) * ShotHeight), 0, ShotHeight - 1);
            int yMax = Mathf.Clamp(Mathf.RoundToInt(Mathf.Max(first.y, second.y) * ShotHeight), yMin + 1, ShotHeight);
            return new RectInt(xMin, yMin, xMax - xMin, yMax - yMin);
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

        private static float Luminance(Color[] shot, RectInt crop)
        {
            double sum = 0d;
            int count = 0;
            for (int y = crop.yMin; y < crop.yMax; y++)
            {
                for (int x = crop.xMin; x < crop.xMax; x++)
                {
                    Color pixel = shot[y * ShotWidth + x];
                    sum += 0.2126f * pixel.r + 0.7152f * pixel.g + 0.0722f * pixel.b;
                    count++;
                }
            }

            return count == 0 ? 0f : (float)(sum / count);
        }

        private string Errors()
        {
            return _errors.Count == 0 ? "none" : string.Join(" | ", _errors);
        }

        private void Record(string finding)
        {
            _findings.Add(finding);
            TestContext.WriteLine("[BlockWorldScene] " + finding);
        }

        private void Expect(bool holds, string what)
        {
            if (!holds)
            {
                _problems.Add(what);
            }
        }

        private void Report()
        {
            Record("errors logged: " + Errors());
            Assert.IsEmpty(_problems, "a player would notice: " + string.Join("; ", _problems) + "\nfindings: "
                                      + string.Join("\n", _findings));
        }

        /// <summary>
        /// The model behind both scripted tests: a Programmer turn that offers <c>execute_lua</c> runs the configured
        /// code through the request's own tool, as a provider's tool loop would; an NPC turn with Tobin's persona
        /// answers with <see cref="TobinReply"/>; anything else answers a fixed line.
        /// </summary>
        private sealed class ScriptedBlockWorldModel : ILlmClient
        {
            public const string TobinReply = "Build a watchtower on the hill by the sea, three by three, eight high.";

            public string ExecuteLuaCode { get; set; }

            public int ToolCalls { get; private set; }

            public string LastToolResult { get; private set; } = "";

            public bool SawCrosshairAttribute { get; private set; }

            /// <summary>The attribute the request named as "here"; empty when it named none.</summary>
            public string HereAttribute { get; private set; } = "";

            public string NpcPersonaSeen { get; private set; } = "";

            public async Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request,
                CancellationToken cancellationToken = default)
            {
                string text = (request?.SystemPrompt ?? "") + "\n" + (request?.UserPayload ?? "");
                IList<Microsoft.Extensions.AI.ChatMessage> history = request?.ChatHistory
                                                                    ?? new List<Microsoft.Extensions.AI.ChatMessage>();
                foreach (Microsoft.Extensions.AI.ChatMessage message in history)
                {
                    text += "\n" + message.Text;
                }

                if (string.Equals(request?.AgentRoleId, BuiltInAgentRoleIds.AiNpc, StringComparison.OrdinalIgnoreCase)
                    && text.Contains("Tobin"))
                {
                    NpcPersonaSeen += "Tobin (" + (text.Contains("village builder") ? "persona" : "name only") + ");";
                    return new LlmCompletionResult { Ok = true, Content = TobinReply, Model = "scripted" };
                }

                ILlmTool lua = Find(request, "execute_lua");
                if (lua is IAIFunctionLlmTool function && !string.IsNullOrEmpty(ExecuteLuaCode)
                    && text.Contains("watchtower"))
                {
                    // WHY read from the request: a model that never saw the demo knows "here" only from what it
                    // is given, so the attribute name comes from the prompt, not from this test.
                    System.Text.RegularExpressions.Match told = System.Text.RegularExpressions.Regex.Match(text,
                        "\"Here\" is the workspace attribute (\\w+)");
                    SawCrosshairAttribute = told.Success;
                    HereAttribute = told.Success ? told.Groups[1].Value : "";
                    if (!told.Success)
                    {
                        return new LlmCompletionResult
                        {
                            Ok = true, Content = "Where is 'here' in this world? Nothing told me.", Model = "scripted"
                        };
                    }

                    ToolCalls++;
                    object result = await function.CreateAIFunction().InvokeAsync(new AIFunctionArguments(
                        new Dictionary<string, object> { ["code"] = ExecuteLuaCode.Replace("{here}", HereAttribute) }),
                        cancellationToken);
                    LastToolResult = result?.ToString() ?? "";
                    return new LlmCompletionResult
                    {
                        Ok = true, Content = "I built a watchtower where you are looking.", Model = "scripted"
                    };
                }

                return new LlmCompletionResult { Ok = true, Content = "scripted answer", Model = "scripted" };
            }

            public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(LlmCompletionRequest request,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                LlmCompletionResult result = await CompleteAsync(request, cancellationToken);
                yield return new LlmStreamChunk { Text = result.Content, Model = "scripted" };
                yield return new LlmStreamChunk { IsDone = true, Model = "scripted" };
            }

            private static ILlmTool Find(LlmCompletionRequest request, string name)
            {
                foreach (ILlmTool tool in request?.Tools ?? Array.Empty<ILlmTool>())
                {
                    if (tool.Name == name)
                    {
                        return tool;
                    }
                }

                return null;
            }
        }
    }
}
#endif
