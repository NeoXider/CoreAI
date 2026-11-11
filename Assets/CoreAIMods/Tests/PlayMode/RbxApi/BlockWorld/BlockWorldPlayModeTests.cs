#if COREAI_HAS_INPUT_SYSTEM
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Ai.Logging;
using CoreAI.Authority;
using CoreAI.Infrastructure.Logging;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Binding.Terrain;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Terrain;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode.RbxApi.BlockWorld
{
    /// <summary>
    /// P6, PlayMode (FastNoLlm): the Block World's mods in a running player loop on the real world host - Terrain
    /// chunks drawn and collided by the presenter, rays resolved against chunk colliders, the character on the
    /// physics motor and real mouse input through the Input System. The spawn area is drawn and holds the character,
    /// a click breaks and places at the outlined cell in the frame after it, a click beyond the reach changes nothing,
    /// and the streaming frames are timed for the P19 record. The scene-level twins (HUD on the runtime UI host,
    /// lighting crops, "build here" with a scripted model and /undo) run against the authored scene.
    /// </summary>
    [TestFixture]
    [Category("InputDevice")]
    public sealed class BlockWorldPlayModeTests : InputTestFixture
    {
        private const string ActorId = "p6-local-player";

        private static readonly string[] Mods =
        {
            "blockworld_world", "blockworld_build", "blockworld_daynight", "blockworld_torches"
        };

        private readonly List<GameObject> _created = new();
        private RbxWorldHost _host;
        private LuaCsRbxApiBindings _roblox;
        private LuaCsModStack _stack;
        private Mouse _mouse;
        private Keyboard _keyboard;
        private List<string> _problems;
        private RbxTerrain _terrain;

        private sealed class MemoryStore : ILuaModStore
        {
            private readonly Dictionary<string, string> _values = new();

            public string Get(string modId, string key)
            {
                return _values.TryGetValue(modId + "/" + key, out string value) ? value : "";
            }

            public void Set(string modId, string key, string value)
            {
                _values[modId + "/" + key] = value;
            }

            public void Clear(string modId)
            {
            }
        }

        private sealed class ProblemLogger : IGameLogger
        {
            private readonly List<string> _problems;

            public ProblemLogger(List<string> problems)
            {
                _problems = problems;
            }

            public void LogDebug(GameLogFeature feature, string message, Object context = null)
            {
            }

            public void LogInfo(GameLogFeature feature, string message, Object context = null)
            {
            }

            public void LogWarning(GameLogFeature feature, string message, Object context = null)
            {
                // WHY not a problem: host warnings describe this fixture's own composition (no Full-tier deny
                // list, no world host scope); the mods' own warnings arrive through the mod log below.
            }

            public void LogError(GameLogFeature feature, string message, Object context = null)
            {
                _problems.Add("error: " + message);
            }
        }

        public override void TearDown()
        {
            try
            {
                foreach (GameObject created in _created)
                {
                    if (created != null)
                    {
                        Object.Destroy(created);
                    }
                }

                _created.Clear();
            }
            finally
            {
                base.TearDown();
            }
        }

        private IEnumerator BuildWorld()
        {
            InputSystem.settings.updateMode = InputSettings.UpdateMode.ProcessEventsManually;
            _problems = new List<string>();
            ProblemLogger logger = new(_problems);
            _keyboard = InputSystem.AddDevice<Keyboard>();
            _mouse = InputSystem.AddDevice<Mouse>();
            _keyboard.MakeCurrent();
            _mouse.MakeCurrent();

            GameObject cameraObject = new("P6Camera");
            _created.Add(cameraObject);
            cameraObject.AddComponent<Camera>();
            GameObject hostObject = new("P6WorldHost");
            _created.Add(hostObject);
            _host = hostObject.AddComponent<RbxWorldHost>();
            _host.Initialize();
            yield return null;

            UnityCameraRig rig = new(cameraObject.transform, _host.Binder);
            UnityNewInputSource source = new(_host.InputFocus, new InMemoryCursorPort());
            _roblox = new LuaCsRbxApiBindings(_host.Registry, _host.Game, log: line => _problems.Add(line),
                partSink: _host.Binder, cameraRig: rig, inputSource: source);
            LuaLogService modLog = new(mirrorLogger: logger);
            modLog.EntryAppended += entry =>
            {
                if (entry.Level != LuaLogLevel.Print)
                {
                    _problems.Add(entry.ModId + " " + entry.Level + ": " + entry.Message);
                }
            };
            _roblox.WorldPhysics.AttachPort(_host.PhysicsPort);
            InstanceGameObjectBinder binder = _host.Binder;
            _roblox.AttachCharacterMotorFactory(humanoid =>
            {
                RbxInstance root = humanoid.RootPart;
                return root != null && binder.TryGetBoundObject(root.Id, out GameObject body)
                       && body.TryGetComponent(out Rigidbody rigidbody)
                    ? new UnityRbxCharacterMotor(rigidbody)
                    : null;
            });
            _stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = logger,
                LogService = modLog,
                ModStore = new MemoryStore(),
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All,
                RbxApi = _roblox
            });
            _terrain = RbxTerrainBootstrap.Find(_host.Game.FindFirstChildOfClass("Workspace"));
            Assert.IsNotNull(_terrain, "the world has a Terrain");

            _roblox.LocalPlayerActorId = ActorId;
            _roblox.Players.EnsureActor(_roblox.Registry, ActorId);
            foreach (string mod in Mods)
            {
                Load(ActorId, mod, Resource("BlockWorld/" + mod));
            }

            Load(ActorId, "coreai_player_controls", Resource("coreai_player_controls"));
            // WHY the player's actor: a mod loaded by another trusted actor joins that actor as a second player.
            Load(ActorId, "p6_aim", @"
                local camera = workspace.CurrentCamera
                game:GetService('RunService').Heartbeat:Connect(function()
                  local from = workspace:GetAttribute('TestAimFrom')
                  local at = workspace:GetAttribute('TestAimAt')
                  if from and at then
                    camera.CameraType = Enum.CameraType.Scriptable
                    camera.CFrame = CFrame.lookAt(from, at)
                  end
                end)");
            yield return Frames(120);
            Assert.IsNotNull(Humanoid(), "the local character spawned");
            // WHY: a new player is held at the spawn until the world has built the first view.
            for (int frame = 0; frame < 1200 && !Equals(Attribute("BlockWorldOpened"), true); frame++)
            {
                yield return Frames(1);
            }

            Assert.AreEqual(true, Attribute("BlockWorldOpened"), "the world lets the player go");
            yield return Frames(30);
        }

        private static string Resource(string path)
        {
            TextAsset asset = Resources.Load<TextAsset>("CoreAIMods/" + path);
            Assert.IsNotNull(asset, "Resources/CoreAIMods/" + path + ".lua ships with the mods package");
            return asset.text;
        }

        private void Load(string actorId, string id, string code)
        {
            ActorContext loader = new LocalActorIdentityProvider(actorId).GetActorContext(null);
            _stack.Runtime.LoadMod(loader, id, code, LuaCapabilities.All, false);
        }

        private IEnumerator Frames(int count)
        {
            for (int frame = 0; frame < count; frame++)
            {
                yield return new WaitForFixedUpdate();
                InputSystem.Update();
                _roblox.WorldPhysics.BeginPhysicsStep();
                _roblox.StepCharacterMotors(Time.fixedDeltaTime);
                _host.PhysicsPort.ApplyGravity();
                _roblox.Scheduler.Advance(Time.fixedDeltaTime);
            }
        }

        private RbxInstance Workspace => _host.Game.FindFirstChildOfClass("Workspace");

        private object Attribute(string name)
        {
            return Workspace.GetAttribute(name);
        }

        private RbxHumanoid Humanoid()
        {
            _roblox.Players.TryGetByActorId(ActorId, out CoreAI.Mods.Rbx.Instances.Networking.RbxPlayer player);
            return player?.Character?.FindFirstChildOfClass("Humanoid") as RbxHumanoid;
        }

        private RbxVector3 RootPosition()
        {
            return _roblox.PartSink.GetLivePositionStuds(Humanoid().RootPart.Id);
        }

        private IEnumerator Aim(RbxVector3 from, RbxVector3 at)
        {
            Workspace.SetAttribute("TestAimFrom", from);
            Workspace.SetAttribute("TestAimAt", at);
            yield return Frames(3);
        }

        private IEnumerator ClickOnce(ButtonControl button)
        {
            InputSystem.Update();
            Press(button);
            yield return Frames(1);
            InputSystem.Update();
            Release(button);
            yield return Frames(1);
        }

        private string Problems()
        {
            return _problems.Count == 0 ? "none" : string.Join(" | ", _problems);
        }

        [UnityTest]
        public IEnumerator P6_PlayMode_TheSpawnArea_IsDrawnAndCollides_AndTheCharacterStandsOnIt()
        {
            yield return BuildWorld();
            yield return Frames(120);

            RbxTerrainRenderer renderer = (RbxTerrainRenderer)_host.TerrainPresenter.Renderer;
            Assert.Greater(renderer.ChunkObjectCount, 4, "the spawn columns are drawn");
            RbxVector3 spawn = (RbxVector3)Attribute("BlockWorldSpawn");
            RbxVector3 root = RootPosition();
            TestContext.WriteLine("[P6] spawn " + spawn + ", root " + root + ", problems: " + Problems());
            Assert.Greater(root.Y, spawn.Y - 6f, "the character stands on the ground, it did not fall through");
            CollectionAssert.IsEmpty(_problems, Problems());
        }

        [UnityTest]
        public IEnumerator P6_PlayMode_ClicksBreakAndPlace_AtTheOutlinedCell_InTheFrameAfterTheClick()
        {
            yield return BuildWorld();
            RbxVector3 eye = RootPosition() + new RbxVector3(0f, 1.5f, 0f);
            // WHY the cell next to the one under the feet: breaking that one drops the character into the hole,
            // and a block is never placed where the character stands.
            yield return Aim(eye, eye + new RbxVector3(8f, -6f, 3f));
            Assert.AreEqual(true, Attribute("CrosshairHasTarget"), "the ground next to the character: " + Problems());
            RbxVector3 cell = (RbxVector3)Attribute("CrosshairCell");

            yield return ClickOnce(_mouse.leftButton);

            Assert.AreEqual(TerrainMaterials.Air, _terrain.Store.GetMaterial((int)cell.X, (int)cell.Y, (int)cell.Z),
                "the outlined cell was broken");
            yield return Frames(2);
            Assert.AreEqual(true, Attribute("CrosshairHasTarget"), "the cell behind it is outlined now");
            RbxVector3 place = (RbxVector3)Attribute("CrosshairPlaceCell");
            RbxVector3 behind = (RbxVector3)Attribute("CrosshairCell");
            RbxVector3 normal = (RbxVector3)Attribute("CrosshairNormal");
            byte placeBefore = _terrain.Store.GetMaterial((int)place.X, (int)place.Y, (int)place.Z);
            Workspace.SetAttribute("BlockWorldSelected", "Brick");

            yield return ClickOnce(_mouse.rightButton);

            TerrainMaterials.TryGetSlot("Brick", out byte brick);
            Assert.AreEqual(brick, _terrain.Store.GetMaterial((int)place.X, (int)place.Y, (int)place.Z),
                "the brick sits on the outlined face: broken " + cell + ", now outlined " + behind + " normal " + normal
                + ", place cell " + place + " held slot " + placeBefore + " before the click; " + Problems());
            CollectionAssert.IsEmpty(_problems, Problems());
        }

        [UnityTest]
        public IEnumerator P6_PlayMode_AClickBeyondTheReach_ChangesNothing_NegativeTwin()
        {
            yield return BuildWorld();
            // WHY a pillar 32 studs from the eyes: the reach is measured from the character's eyes, not from the
            // camera, so a block straight under a raised camera is still within reach.
            RbxVector3 eye = RootPosition() + new RbxVector3(0f, 1.5f, 0f);
            int px = (int)System.Math.Floor(eye.X / 4f) + 8;
            int py = (int)System.Math.Floor(eye.Y / 4f) + 3;
            int pz = (int)System.Math.Floor(eye.Z / 4f);
            TerrainMaterials.TryGetSlot("Concrete", out byte concrete);
            for (int y = py - 1; y <= py + 1; y++)
            {
                _terrain.Store.SetCell(px, y, pz, concrete, 255);
            }

            yield return Frames(10);
            yield return Aim(eye, new RbxVector3((px + 0.5f) * 4f, (py + 0.5f) * 4f, (pz + 0.5f) * 4f));
            Assert.AreEqual(false, Attribute("CrosshairHasTarget"), "a block 32 studs from the eyes is out of reach");
            // WHY counters and not the store revision: the world keeps streaming columns in while the test runs.
            object placed = Attribute("BlockWorldPlaced");
            object broken = Attribute("BlockWorldBroken");

            yield return ClickOnce(_mouse.leftButton);
            yield return ClickOnce(_mouse.rightButton);

            Assert.AreEqual(placed, Attribute("BlockWorldPlaced"), "nothing was placed beyond the reach");
            Assert.AreEqual(broken, Attribute("BlockWorldBroken"), "nothing was broken beyond the reach");
        }

        [UnityTest]
        public IEnumerator P6_PlayMode_StreamingFrames_AreTimedForTheFrameBudgetRecord()
        {
            yield return BuildWorld();
            List<double> milliseconds = new();
            Stopwatch clock = new();
            for (int frame = 0; frame < 600; frame++)
            {
                clock.Restart();
                yield return Frames(1);
                clock.Stop();
                milliseconds.Add(clock.Elapsed.TotalMilliseconds);
            }

            milliseconds.Sort();
            double median = milliseconds[milliseconds.Count / 2];
            double p95 = milliseconds[(int)(milliseconds.Count * 0.95)];
            TestContext.WriteLine("[P6] 600 streaming frames: median " + median.ToString("0.00") + " ms, p95 "
                                  + p95.ToString("0.00") + " ms, columns " + Attribute("BlockWorldColumns"));
            Assert.Greater(System.Convert.ToDouble(Attribute("BlockWorldColumns")), 10d, "the world streamed in");
            CollectionAssert.IsEmpty(_problems, Problems());
        }
    }
}
#endif
