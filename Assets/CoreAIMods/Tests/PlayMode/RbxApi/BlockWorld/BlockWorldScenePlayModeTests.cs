#if UNITY_EDITOR && COREAI_HAS_INPUT_SYSTEM
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
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
            "blockworld_villagers", "blockworld_autopilot"
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

        /// <summary>The watchtower recipe of the skill text (stage 1's <c>WatchtowerExample</c>), as a model would send it.</summary>
        private const string WatchtowerCode = @"
local terrain = workspace.Terrain
local base = workspace:GetAttribute('CrosshairPlaceCell')
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
        }

        [UnityTearDown]
        public IEnumerator CloseScene()
        {
            Application.logMessageReceived -= _capture;
            _installers?.Dispose();
            _installers = null;
            Scene empty = SceneManager.CreateScene("BlockWorldTearDown" + Guid.NewGuid().ToString("N"));
            SceneManager.SetActiveScene(empty);
            for (int index = SceneManager.sceneCount - 1; index >= 0; index--)
            {
                Scene scene = SceneManager.GetSceneAt(index);
                if (scene.path == ScenePath)
                {
                    yield return SceneManager.UnloadSceneAsync(scene);
                }
            }

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

            Scene scene = EditorSceneManager.LoadSceneInPlayMode(ScenePath, new LoadSceneParameters(LoadSceneMode.Single));
            Assert.IsTrue(scene.IsValid(), "the Block World scene loads: " + ScenePath);
            yield return null;
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
            }

            RbxVector3 groundTop = new(root.X + 6f, (below + 1) * Cell, root.Z + 0.5f);
            yield return Aim(groundTop + new RbxVector3(0f, 14f, -6f), groundTop);
            Color[] look = Shoot("_ground_check.png");
            RectInt crop = ProjectedRect(groundTop + new RbxVector3(-1.5f, 0f, -1.5f), groundTop + new RbxVector3(1.5f, 0f, 1.5f));
            Color mean = Mean(look, crop);
            Record("ground colour at the spawn: crop " + crop + " mean " + mean);
            Expect(mean.g > mean.r && mean.g > mean.b, "the grass at the spawn is drawn green (the Grass material)");
            Record("standing: root " + root + ", state " + humanoid.GetState() + ", still " + still
                   + " frames, cell under the feet (" + cellX + ", " + below + ", " + cellZ + ") material slot " + ground);
            Expect(still >= 30, "the character comes to rest");
            Expect(ground != TerrainMaterials.Air, "the character stands on generated ground, not on air");
        }

        private IEnumerator WaitForStreamingToSettle()
        {
            double last = -1d;
            int stable = 0;
            for (int frame = 0; frame < 6000 && stable < 120; frame++)
            {
                yield return null;
                double columns = Convert.ToDouble(Workspace().GetAttribute("BlockWorldColumns") ?? 0d);
                stable = Math.Abs(columns - last) < 0.5d ? stable + 1 : 0;
                last = columns;
            }

            Record("streaming settled at " + last + " columns (stable for " + stable + " frames)");
        }

        // ---- HUD ---------------------------------------------------------------------------------------------------

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
            Shoot("building.png");
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
            yield return Aim(miraAt - toward * 6f + new RbxVector3(0f, 0.5f, 0f), miraAt);
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
            Workspace().SetAttribute("BlockWorldTime", "midnight");
            yield return Frames(10);
            Color[] midnight = Shoot("midnight.png");
            Quaternion midnightSun = sun != null ? sun.transform.rotation : Quaternion.identity;
            float groundNoon = Luminance(noon, ground);
            float groundMidnight = Luminance(midnight, ground);

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
            RectInt wall = ProjectedRect(face + new RbxVector3(-10f, -3f, -0.1f), face + new RbxVector3(-3f, 5f, -0.1f));
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

            Record("day and night: ground crop " + ground + " noon " + groundNoon.ToString("0.000") + ", midnight "
                   + groundMidnight.ToString("0.000") + "; sun " + (sun != null ? sun.name : "none") + " turned "
                   + Quaternion.Angle(noonSun, midnightSun).ToString("0.0") + " degrees; wall crop " + wall + " torch on "
                   + wallOn.ToString("0.000") + " (torches " + torches + "), removed " + wallOff.ToString("0.000")
                   + " (torches " + torchesAfter + ")");
            Expect(sun != null && Quaternion.Angle(noonSun, midnightSun) > 30f, "the day cycle turns the scene's one sun");
            Expect(groundMidnight < groundNoon * 0.5f, "midnight darkens the ground crop to under half of noon");
            Expect(Equals(torches, 1d) || Equals(torches, 1), "slot 9 and a right click put one torch on the wall");
            Expect(Equals(torchesAfter, 0d) || Equals(torchesAfter, 0), "a left click on the torch takes it back");
            Expect(wallOn > wallOff + 0.02f, "the torch lights the wall crop at night, and removing it darkens it");
        }

        private IEnumerator NightWithTorches(int sx, int sz, int row)
        {
            RbxVector3 eye = RootPosition(Humanoid()) + new RbxVector3(0f, 1.5f, 0f);
            int placed = 0;
            foreach (Vector2Int offset in new[] { new Vector2Int(2, -3), new Vector2Int(-2, -3), new Vector2Int(3, -1),
                         new Vector2Int(-3, -1) })
            {
                int cx = sx + offset.x;
                int cz = sz + offset.y;
                int top = row;
                while (top > row - 6 && Terrain().Store.GetMaterial(cx, top, cz) == TerrainMaterials.Air)
                {
                    top--;
                }

                RbxVector3 floor = new((cx + 0.5f) * Cell, (top + 1f) * Cell, (cz + 0.5f) * Cell);
                yield return Aim(eye + new RbxVector3(0f, 2f, 0f), floor);
                object before = Workspace().GetAttribute("BlockWorldTorches");
                yield return Click(_mouse.rightButton);
                placed += Equals(Workspace().GetAttribute("BlockWorldTorches"), before) ? 0 : 1;
            }

            RbxVector3 middle = new((sx + 0.5f) * Cell, row * Cell, (sz - 2f) * Cell);
            yield return Aim(middle + new RbxVector3(0f, 14f, -26f), middle);
            yield return Frames(10);
            yield return ShootWithUi("night_torches.png");
            Record("night view: " + placed + " torches put on the ground, " + Workspace().GetAttribute("BlockWorldTorches")
                   + " lit");
            Expect(placed == 4, "four right clicks with the torch in hand put four torches on the ground");
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

            Record("watchtower: base " + baseCell + ", model saw the crosshair " + model.SawCrosshairAttribute
                   + ", execute_lua result '" + model.LastToolResult + "', reply '" + reply + "'; cells changed "
                   + changed + " (outside the 5 x 10 x 5 box: " + outside + "), tower present " + tower
                   + "; /undo answered '" + undoReply + "'; world instances changed between the build and /undo: "
                   + changedSince + "; terrain golden " + goldenBefore + " -> " + goldenAfter + "; /redo answered '"
                   + redoReply + "', tower back " + redone);
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
            object agent = tobin?.GetAttribute("NpcAgent");
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

                NpcPosition? playerAt = listenerRelay.Current;
                atSend = "Tobin " + tobinAt + ", listener " + playerAt + " after " + frame + " frames";
                if (tobinAt.HasValue && playerAt.HasValue && tobinAt.Value.DistanceTo(playerAt.Value) <= 40d)
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
                yield return Aim(at + new RbxVector3(-7f, 3f, -7f), at + new RbxVector3(0f, 1.5f, 0f));
                yield return Frames(3);
            }

            yield return ShootWithUi("villager_talking.png");
            Record("villager: Tobin's agent " + agent + "; the model was asked as " + model.NpcPersonaSeen
                   + "; chat '" + lines + "'" + probe + "; BlockWorldVillagerReply '" + reply + "'; bubble '" + bubble
                   + "'");
            Expect(probe.Length == 0, "the chat line that names Tobin reaches Tobin through the chat's hearing");
            Expect(agent != null, "Tobin has an AIService agent (AI from Lua is on in this scene)");
            Expect(model.NpcPersonaSeen.Contains("Tobin"), "the turn reached the model with Tobin's persona");
            Expect(lines.Contains("Tobin: ") && lines.Contains(ScriptedBlockWorldModel.TobinReply),
                "Tobin answers in the chat by name");
            Expect(bubble.StartsWith(ScriptedBlockWorldModel.TobinReply, StringComparison.Ordinal),
                "Tobin's answer shows in a bubble over his head");
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
            yield return Frames(4);
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
        /// The camera frame with the UI Toolkit panels (HUD, chat overlay, bubbles) composited over it: every panel is
        /// pointed at one transparent target for a frame, then put back.
        /// </summary>
        private IEnumerator ShootWithUi(string fileName)
        {
            Color[] scene = Shoot("_scene_" + fileName);
            RenderTexture ui = new(ShotWidth, ShotHeight, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            ui.Create();
            RenderTexture previousActive = RenderTexture.active;
            RenderTexture.active = ui;
            GL.Clear(true, true, Color.clear);
            RenderTexture.active = previousActive;
            List<(PanelSettings Panel, RenderTexture Target)> panels = new();
            foreach (UIDocument document in Object.FindObjectsByType<UIDocument>(FindObjectsSortMode.None))
            {
                PanelSettings panel = document.panelSettings;
                if (panel != null && document.isActiveAndEnabled && !panels.Exists(entry => entry.Panel == panel))
                {
                    panels.Add((panel, panel.targetTexture));
                    panel.targetTexture = ui;
                }
            }

            yield return null;
            yield return null;
            Texture2D read = new(ShotWidth, ShotHeight, TextureFormat.RGBA32, false);
            RenderTexture.active = ui;
            read.ReadPixels(new Rect(0, 0, ShotWidth, ShotHeight), 0, 0);
            read.Apply();
            RenderTexture.active = previousActive;
            foreach ((PanelSettings panel, RenderTexture target) in panels)
            {
                panel.targetTexture = target;
            }

            Color[] overlay = read.GetPixels();
            Color[] composed = new Color[scene.Length];
            for (int index = 0; index < scene.Length; index++)
            {
                Color top = overlay[index];
                Color under = scene[index];
                composed[index] = new Color(top.r + under.r * (1f - top.a), top.g + under.g * (1f - top.a),
                    top.b + under.b * (1f - top.a), 1f);
            }

            WritePng(fileName, composed);
            Object.Destroy(read);
            ui.Release();
            Object.Destroy(ui);
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
                    SawCrosshairAttribute = text.Contains("CrosshairPlaceCell");
                    ToolCalls++;
                    object result = await function.CreateAIFunction().InvokeAsync(new AIFunctionArguments(
                        new Dictionary<string, object> { ["code"] = ExecuteLuaCode }), cancellationToken);
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
