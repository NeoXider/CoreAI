using System;
using System.Collections.Generic;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Infrastructure.Logging;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.LuaBindings
{
    /// <summary>
    /// P1 stage C through the real mod runtime with headless seams: the <c>Ray</c> datatype, the camera
    /// queries and lens properties, the default camera of <c>CameraType.Custom</c> (orbit, wheel zoom into
    /// first person, <c>LockFirstPerson</c>, the collision pull-in, the replaceable module), the player's
    /// camera settings, the thumbsticks and the text-focus signals. Every behaviour has a negative twin.
    /// </summary>
    [TestFixture]
    public sealed class P1CameraLuaEditModeTests
    {
        private const double FrameSeconds = 1d / 60d;

        private SynchronizationContext _savedContext;

        [SetUp]
        public void DetachSynchronizationContext()
        {
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void RestoreSynchronizationContext()
        {
            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        private sealed class MemoryStore : ILuaModStore
        {
            private readonly Dictionary<(string ModId, string Key), string> _values = new();

            public string Get(string modId, string key)
            {
                return _values.TryGetValue((modId, key), out string value) ? value : "";
            }

            public void Set(string modId, string key, string value)
            {
                if (value == null)
                {
                    _values.Remove((modId, key));
                    return;
                }

                _values[(modId, key)] = value;
            }

            public void Clear(string modId)
            {
            }
        }

        private sealed class SilentLogger : IGameLogger
        {
            public void LogDebug(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogInfo(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogWarning(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogError(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }
        }

        private sealed class World
        {
            public InMemoryCameraRig Rig;
            public InMemoryInputSource Source;
            public InMemoryCursorPort Cursor;
            public InputFocusState Focus;
            public LuaCsRbxApiBindings Roblox;
            public LuaCsModStack Stack;
            public MemoryStore Store;

            public void Frames(int count)
            {
                for (int index = 0; index < count; index++)
                {
                    Roblox.Scheduler.Advance(FrameSeconds);
                }
            }

            public void Load(string modId, string code)
            {
                Stack.Runtime.LoadMod(modId, code);
            }

            public string Get(string modId, string key)
            {
                return Store.Get(modId, key);
            }

            public RbxVector3 RootPosition()
            {
                RbxPlayer player = Roblox.Players.GetSoleListedPlayer();
                RbxInstance root = player.Character.FindFirstChild(RbxCharacterFactory.RootPartName);
                return Roblox.PartSink.GetLivePositionStuds(root.Id);
            }
        }

        private static World BuildWorld(bool withPlayer, IRbxCameraRig rig = null)
        {
            World world = new()
            {
                Rig = rig as InMemoryCameraRig ?? (rig == null ? new InMemoryCameraRig() : null),
                Focus = new InputFocusState(),
                Cursor = new InMemoryCursorPort(),
                Store = new MemoryStore()
            };
            world.Source = new InMemoryInputSource { FocusState = world.Focus, CursorPort = world.Cursor };
            world.Roblox = new LuaCsRbxApiBindings(cameraRig: rig ?? world.Rig, inputSource: world.Source);
            world.Stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new SilentLogger(),
                ModStore = world.Store,
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All,
                RbxApi = world.Roblox
            });
            if (withPlayer)
            {
                world.Roblox.Players.EnsureActor(world.Roblox.Registry, "player-1");
                world.Frames(3);
            }

            return world;
        }

        // ---- Ray ------------------------------------------------------------------------------

        [Test]
        public void Lua_Ray_NewReadsItsMembers_AndTypeofIsRay()
        {
            World world = BuildWorld(false);
            world.Load("m", @"
                local ray = Ray.new(Vector3.new(0, 0, 0), Vector3.new(0, 0, -10))
                store_set('type', typeof(ray))
                store_set('text', tostring(ray))
                store_set('unit', tostring(ray.Unit.Direction))
                store_set('closest', tostring(Ray.new(Vector3.zero, Vector3.xAxis):ClosestPoint(Vector3.new(3, 4, 0))))
                store_set('distance', tostring(Ray.new(Vector3.zero, Vector3.xAxis):Distance(Vector3.new(3, 4, 0))))
                store_set('equal', tostring(ray == Ray.new(Vector3.zero, Vector3.new(0, 0, -10))))
                store_set('origin', tostring(Ray.new().Origin))");

            Assert.AreEqual("Ray", world.Get("m", "type"));
            Assert.AreEqual("{0, 0, 0}, {0, 0, -10}", world.Get("m", "text"));
            Assert.AreEqual("0, 0, -1", world.Get("m", "unit"));
            Assert.AreEqual("3, 0, 0", world.Get("m", "closest"));
            Assert.AreEqual("4", world.Get("m", "distance"));
            Assert.AreEqual("true", world.Get("m", "equal"));
            Assert.AreEqual("0, 0, 0", world.Get("m", "origin"), "omitted arguments are zero vectors");
        }

        [Test]
        public void Lua_Ray_IsImmutable_AndRefusesANonVector_NegativeTwin()
        {
            World world = BuildWorld(false);
            world.Load("m", @"
                local ok, err = pcall(function() Ray.new(Vector3.zero, Vector3.xAxis).Origin = Vector3.one end)
                store_set('write', tostring(ok) .. '|' .. tostring(err))
                local badOk, badErr = pcall(function() return Ray.new(1, 2) end)
                store_set('bad', tostring(badOk) .. '|' .. tostring(badErr))");

            StringAssert.StartsWith("false|", world.Get("m", "write"));
            StringAssert.Contains("Ray values are immutable", world.Get("m", "write"));
            StringAssert.StartsWith("false|", world.Get("m", "bad"));
            StringAssert.Contains("Ray.new expects a Vector3 at argument 1", world.Get("m", "bad"));
        }

        // ---- camera queries ---------------------------------------------------------------------

        [Test]
        public void Lua_ViewportPointToRay_AtTheViewportCentre_MatchesTheLookVector()
        {
            World world = BuildWorld(false);
            world.Load("m", @"
                local cam = workspace.CurrentCamera
                cam.CameraType = Enum.CameraType.Scriptable
                cam.CFrame = CFrame.new(4, 10, -3) * CFrame.fromEulerAnglesYXZ(-0.3, 0.9, 0)
                local centre = cam.ViewportSize / 2
                local function gap(ray)
                    return (ray.Direction - cam.CFrame.LookVector).Magnitude
                end
                store_set('viewport', tostring(gap(cam:ViewportPointToRay(centre.X, centre.Y)) < 1e-4))
                store_set('screen', tostring(gap(cam:ScreenPointToRay(centre.X, centre.Y, 0)) < 1e-4))
                local deep = cam:ViewportPointToRay(centre.X, centre.Y, 10)
                store_set('depth', tostring((deep.Origin - (cam.CFrame.Position + cam.CFrame.LookVector * 10)).Magnitude < 1e-3))
                store_set('corner', tostring(gap(cam:ViewportPointToRay(0, 0)) > 0.1))");

            Assert.AreEqual("true", world.Get("m", "viewport"), "the DoD's 1e-4 bound");
            Assert.AreEqual("true", world.Get("m", "screen"), "no top bar, so the screen equals the viewport");
            Assert.AreEqual("true", world.Get("m", "depth"));
            Assert.AreEqual("true", world.Get("m", "corner"), "a corner is not the centre");
        }

        [Test]
        public void Lua_WorldToViewportPoint_ReturnsThePointAndOnScreen_AndBehindIsOffScreen()
        {
            World world = BuildWorld(false);
            world.Rig.ViewportSize = new RbxVector2(800f, 600f);
            world.Load("m", @"
                local cam = workspace.CurrentCamera
                cam.CameraType = Enum.CameraType.Scriptable
                cam.CFrame = CFrame.new(0, 0, 0)
                local ahead, aheadOn = cam:WorldToViewportPoint(Vector3.new(0, 0, -10))
                local behind, behindOn = cam:WorldToScreenPoint(Vector3.new(0, 0, 10))
                store_set('ahead', string.format('%.1f,%.1f,%.1f,%s', ahead.X, ahead.Y, ahead.Z, tostring(aheadOn)))
                store_set('behind', tostring(behindOn) .. ',' .. tostring(behind.Z < 0))");

            Assert.AreEqual("400.0,300.0,10.0,true", world.Get("m", "ahead"));
            Assert.AreEqual("false,true", world.Get("m", "behind"));
        }

        [Test]
        public void Lua_FieldOfView_DefaultsTo70_ClampsTo1And120_AndReachesTheRig()
        {
            World world = BuildWorld(false);
            world.Load("m", @"
                local cam = workspace.CurrentCamera
                local changes = 0
                cam:GetPropertyChangedSignal('FieldOfView'):Connect(function() changes = changes + 1; store_set('changes', tostring(changes)) end)
                store_set('default', tostring(cam.FieldOfView))
                cam.FieldOfView = 45
                store_set('set', tostring(cam.FieldOfView))
                cam.FieldOfView = 500
                store_set('high', tostring(cam.FieldOfView))
                cam.FieldOfView = 0
                store_set('low', tostring(cam.FieldOfView))
                local ok, err = pcall(function() cam.FieldOfView = 'wide' end)
                store_set('bad', tostring(ok) .. '|' .. tostring(err))");
            world.Frames(1);

            Assert.AreEqual("70", world.Get("m", "default"));
            Assert.AreEqual("45", world.Get("m", "set"));
            Assert.AreEqual("120", world.Get("m", "high"));
            Assert.AreEqual("1", world.Get("m", "low"));
            Assert.AreEqual(1d, world.Rig.FieldOfView);
            Assert.AreEqual("3", world.Get("m", "changes"), "each real change fires once");
            StringAssert.StartsWith("false|", world.Get("m", "bad"));
            StringAssert.Contains("Camera.FieldOfView expects a number", world.Get("m", "bad"));
        }

        [Test]
        public void Lua_ViewportSize_IsReadOnly()
        {
            World world = BuildWorld(false);
            world.Rig.ViewportSize = new RbxVector2(640f, 480f);
            world.Load("m", @"
                local cam = workspace.CurrentCamera
                store_set('size', tostring(cam.ViewportSize))
                local ok, err = pcall(function() cam.ViewportSize = Vector2.new(1, 1) end)
                store_set('write', tostring(ok) .. '|' .. tostring(err))");

            Assert.AreEqual("640, 480", world.Get("m", "size"));
            StringAssert.Contains("Unable to assign property ViewportSize. Property is read only",
                world.Get("m", "write"));
        }

        /// <summary>A host rig written before the lens members: pose only.</summary>
        private sealed class PoseOnlyRig : IRbxCameraRig
        {
            private RbxCFrame _cframe = RbxCFrame.Identity;

            public RbxCFrame GetCFrame()
            {
                return _cframe;
            }

            public void SetCFrame(in RbxCFrame cframe)
            {
                _cframe = cframe;
            }

            public bool Follow(InstanceId id)
            {
                return true;
            }

            public void StopFollowing()
            {
            }
        }

        [Test]
        public void Lua_FieldOfView_OnAHostRigWithoutALens_ReadsTheDefault_AndRefusesAWriteLoudly()
        {
            World world = BuildWorld(false, new PoseOnlyRig());
            world.Load("m", @"
                local cam = workspace.CurrentCamera
                store_set('read', tostring(cam.FieldOfView))
                store_set('size', tostring(cam.ViewportSize))
                local ok, err = pcall(function() cam.FieldOfView = 50 end)
                store_set('write', tostring(ok) .. '|' .. tostring(err))");

            Assert.AreEqual("70", world.Get("m", "read"));
            Assert.AreEqual("1, 1", world.Get("m", "size"), "Roblox's unset viewport");
            StringAssert.StartsWith("false|", world.Get("m", "write"));
            StringAssert.Contains("NOT_IMPLEMENTED", world.Get("m", "write"));
            StringAssert.Contains("IRbxCameraRig.TrySetFieldOfView", world.Get("m", "write"));
        }

        // ---- the default camera -----------------------------------------------------------------

        [Test]
        public void CustomCamera_OrbitsTheLocalCharacter_AndCameraSubjectReadsItsHumanoid()
        {
            World world = BuildWorld(true);
            world.Frames(1);
            world.Load("m", @"
                local cam = workspace.CurrentCamera
                local humanoid = game:GetService('Players'):GetPlayers()[1].Character:FindFirstChildOfClass('Humanoid')
                store_set('subject', tostring(cam.CameraSubject == humanoid))");

            RbxVector3 focus = world.RootPosition() + LuaCsRbxApiBindings.RigCameraSubjectOffset;
            RbxCFrame camera = world.Rig.GetCFrame();
            Assert.AreEqual("true", world.Get("m", "subject"));
            Assert.AreEqual(12.5f, (focus - camera.Position).Magnitude, 1e-3f, "the default zoom");
            Assert.Less((camera.Position + camera.LookVector * 12.5f - focus).Magnitude, 1e-3f,
                "the camera looks at the head");
        }

        [Test]
        public void ScriptableCamera_IsLeftWhereTheScriptPutIt_NegativeTwin()
        {
            World world = BuildWorld(true);
            world.Load("m", @"
                local cam = workspace.CurrentCamera
                cam.CameraType = Enum.CameraType.Scriptable
                cam.CFrame = CFrame.new(100, 50, 100)");
            world.Frames(5);

            Assert.AreEqual(100f, world.Rig.GetCFrame().Position.X, 1e-4f);
            Assert.AreEqual(50f, world.Rig.GetCFrame().Position.Y, 1e-4f);
        }

        [Test]
        public void CustomCamera_WithNoLocalCharacter_LeavesTheCameraAlone()
        {
            World world = BuildWorld(false);
            world.Load("m", "workspace.CurrentCamera.CFrame = CFrame.new(7, 8, 9)");
            world.Frames(3);

            Assert.AreEqual(7f, world.Rig.GetCFrame().Position.X, 1e-4f, "nothing to orbit");
        }

        [Test]
        public void WheelZoom_ReachesFirstPerson_AndLocksThePointer_ThenZoomsBackOut()
        {
            World world = BuildWorld(true);
            for (int notch = 0; notch < 8; notch++)
            {
                world.Source.ScrollWheel(120f);
                world.Frames(1);
            }

            RbxVector3 focus = world.RootPosition() + LuaCsRbxApiBindings.RigCameraSubjectOffset;
            Assert.AreEqual(0.5f, (focus - world.Rig.GetCFrame().Position).Magnitude, 1e-3f, "first person");
            Assert.AreEqual(RbxMouseLock.LockCenter, world.Cursor.Applied);
            Assert.AreEqual(RbxMouseLock.LockCenter, world.Roblox.UserInputService.RequestedMouseLock);

            world.Source.ScrollWheel(-120f);
            world.Frames(1);
            Assert.Greater((focus - world.Rig.GetCFrame().Position).Magnitude, 1f, "back to third person");
            Assert.AreEqual(RbxMouseLock.Default, world.Cursor.Applied, "the camera frees the pointer it locked");
        }

        [Test]
        public void WheelZoom_WhileTheChatHoldsTheInput_DoesNotZoom_NegativeTwin()
        {
            World world = BuildWorld(true);
            world.Focus.SetCaptured("chat", true);
            for (int notch = 0; notch < 8; notch++)
            {
                world.Source.ScrollWheel(120f);
                world.Frames(1);
            }

            RbxVector3 focus = world.RootPosition() + LuaCsRbxApiBindings.RigCameraSubjectOffset;
            Assert.AreEqual(12.5f, (focus - world.Rig.GetCFrame().Position).Magnitude, 1e-3f);
        }

        [Test]
        public void PlayerCameraMode_LockFirstPerson_FromLua_HoldsFirstPerson()
        {
            World world = BuildWorld(true);
            world.Load("m", @"
                local player = game:GetService('Players'):GetPlayers()[1]
                store_set('defaults', tostring(player.CameraMode == Enum.CameraMode.Classic) .. ','
                    .. tostring(player.CameraMaxZoomDistance) .. ',' .. tostring(player.CameraMinZoomDistance))
                player.CameraMode = Enum.CameraMode.LockFirstPerson");
            world.Frames(1);

            RbxVector3 focus = world.RootPosition() + LuaCsRbxApiBindings.RigCameraSubjectOffset;
            Assert.AreEqual("true,400,0.5", world.Get("m", "defaults"));
            Assert.AreEqual(0.5f, (focus - world.Rig.GetCFrame().Position).Magnitude, 1e-3f);
            Assert.AreEqual(RbxMouseLock.LockCenter, world.Cursor.Applied);
        }

        [Test]
        public void PlayerMaxZoomDistance_FromLua_ClampsTheZoomOut()
        {
            World world = BuildWorld(true);
            world.Load("m", "game:GetService('Players'):GetPlayers()[1].CameraMaxZoomDistance = 20");
            for (int notch = 0; notch < 20; notch++)
            {
                world.Source.ScrollWheel(-1f);
                world.Frames(1);
            }

            RbxVector3 focus = world.RootPosition() + LuaCsRbxApiBindings.RigCameraSubjectOffset;
            Assert.AreEqual(20f, (focus - world.Rig.GetCFrame().Position).Magnitude, 1e-3f);
        }

        /// <summary>A camera behaviour supplied by a host: it pins the camera to one pose.</summary>
        private sealed class FixedCameraModule : IRbxCameraModule
        {
            public int Updates;
            public int Resets;
            public RbxVector3 LastFocus;

            public RbxCameraModuleOutput Update(in RbxCameraModuleInput input)
            {
                Updates++;
                LastFocus = input.Focus;
                return new RbxCameraModuleOutput
                {
                    HasCFrame = true,
                    CFrame = RbxCFrame.FromPosition(new RbxVector3(1f, 2f, 3f))
                };
            }

            public void Reset()
            {
                Resets++;
            }
        }

        [Test]
        public void CameraModule_ReplacedByAHostModule_DrivesTheCameraInstead()
        {
            World world = BuildWorld(true);
            FixedCameraModule fake = new();
            world.Roblox.CameraModule = fake;
            world.Frames(2);

            Assert.AreEqual(2, fake.Updates);
            Assert.AreEqual(3f, world.Rig.GetCFrame().Position.Z, 1e-4f);
            Assert.AreEqual(world.RootPosition().Y + 1.5f, fake.LastFocus.Y, 1e-3f, "the module gets the head focus");

            world.Load("m", "workspace.CurrentCamera.CameraType = Enum.CameraType.Scriptable");
            world.Frames(1);
            Assert.AreEqual(2, fake.Updates, "Scriptable hands the camera to scripts");
            Assert.AreEqual(1, fake.Resets);
        }

        [Test]
        public void CameraModule_SetToNull_LeavesTheCameraToTheHost()
        {
            World world = BuildWorld(true);
            world.Roblox.CameraModule = null;
            world.Rig.SetCFrame(RbxCFrame.FromPosition(new RbxVector3(9f, 9f, 9f)));
            int writes = world.Rig.SetCount;
            world.Frames(3);

            Assert.AreEqual(writes, world.Rig.SetCount, "no default camera, no writes");
        }

        private sealed class ThrowingCameraModule : IRbxCameraModule
        {
            public RbxCameraModuleOutput Update(in RbxCameraModuleInput input)
            {
                throw new InvalidOperationException("host camera bug");
            }

            public void Reset()
            {
            }
        }

        [Test]
        public void CameraModule_ThatThrows_IsContainedAndReportedOnce()
        {
            World world = BuildWorld(true);
            List<string> diagnostics = new();
            world.Roblox.Registry.Diagnostics = diagnostics.Add;
            world.Roblox.CameraModule = new ThrowingCameraModule();

            Assert.DoesNotThrow(() => world.Frames(3));
            Assert.AreEqual(1, diagnostics.FindAll(line => line.Contains("IRbxCameraModule.Update threw")).Count);
        }

        /// <summary>A physics port whose rays meet the listed parts at the listed distances, first eligible wins.</summary>
        private sealed class ListedHitsPort : IRbxPhysicsPort
        {
            public readonly List<(InstanceId Id, double Distance)> Hits = new();
            public int Casts;

            public bool TryRaycast(RbxVector3 originStuds, RbxVector3 directionStuds, bool respectCanCollide,
                Func<InstanceId, bool> isEligible, out RbxPhysicsRaycastHit hit)
            {
                Casts++;
                foreach ((InstanceId id, double distance) in Hits)
                {
                    if (isEligible(id))
                    {
                        hit = new RbxPhysicsRaycastHit(id, originStuds, RbxVector3.YAxis, default, distance);
                        return true;
                    }
                }

                hit = default;
                return false;
            }

            public void SetGravity(double studsPerSecondSquared)
            {
            }

            public event Action<InstanceId, InstanceId> ContactBegan
            {
                add { }
                remove { }
            }

            public event Action<InstanceId, InstanceId> ContactEnded
            {
                add { }
                remove { }
            }
        }

        [Test]
        public void PullIn_AWallBetweenCharacterAndCamera_PullsTheCameraIn_ThroughItsOwnBodyAndGlass()
        {
            World world = BuildWorld(true);
            world.Load("m", @"
                local glass = Instance.new('Part')
                glass.Name = 'Glass'
                glass.Transparency = 0.97
                glass.Parent = workspace
                local wall = Instance.new('Part')
                wall.Name = 'Wall'
                wall.Parent = workspace");
            RbxInstance workspace = world.Roblox.Game.FindFirstChildOfClass("Workspace");
            RbxInstance root = world.Roblox.Players.GetSoleListedPlayer().Character
                .FindFirstChild(RbxCharacterFactory.RootPartName);
            ListedHitsPort port = new();
            port.Hits.Add((root.Id, 0.5d));
            port.Hits.Add((workspace.FindFirstChild("Glass").Id, 2d));
            port.Hits.Add((workspace.FindFirstChild("Wall").Id, 5d));
            world.Roblox.WorldPhysics.AttachPort(port);
            world.Frames(1);

            RbxVector3 focus = world.RootPosition() + LuaCsRbxApiBindings.RigCameraSubjectOffset;
            Assert.Greater(port.Casts, 0);
            Assert.AreEqual(4.75f, (focus - world.Rig.GetCFrame().Position).Magnitude, 1e-3f,
                "the wall at 5 studs, less the clearance; the root and the glass do not occlude");

            port.Hits.Clear();
            world.Frames(1);
            Assert.AreEqual(12.5f, (focus - world.Rig.GetCFrame().Position).Magnitude, 1e-3f,
                "the wall is gone and the chosen zoom comes back");
        }

        // ---- input additions --------------------------------------------------------------------

        [Test]
        public void Thumbstick_FiresInputChanged_WithThePositionAndKeyCode()
        {
            World world = BuildWorld(false);
            world.Load("m", @"
                UserInputService.InputChanged:Connect(function(input, gameProcessed)
                    if input.KeyCode == Enum.KeyCode.Thumbstick1 then
                        store_set('stick', string.format('%.2f,%.2f,%s,%s', input.Position.X, input.Position.Y,
                            input.UserInputType.Name, tostring(gameProcessed)))
                    end
                end)");

            world.Source.SetThumbstick(1, 0.5f, -0.25f);
            world.Frames(1);

            Assert.AreEqual("0.50,-0.25,Gamepad1,false", world.Get("m", "stick"));
        }

        [Test]
        public void TextBoxFocusSignals_FireWhenAHostFieldTakesAndReturnsTheKeyboard()
        {
            World world = BuildWorld(false);
            world.Load("m", @"
                local log = ''
                UserInputService.TextBoxFocused:Connect(function(box) log = log .. 'in:' .. tostring(box) .. ';' store_set('log', log) end)
                UserInputService.TextBoxFocusReleased:Connect(function(box) log = log .. 'out:' .. tostring(box) .. ';' store_set('log', log) end)
                store_set('focused', tostring(UserInputService:GetFocusedTextBox()))");

            world.Frames(1);
            world.Focus.SetCaptured("chat", true);
            world.Frames(2);
            world.Focus.SetCaptured("chat", false);
            world.Frames(1);

            Assert.AreEqual("in:nil;out:nil;", world.Get("m", "log"), "one fire per edge, no fire while held");
            Assert.AreEqual("nil", world.Get("m", "focused"));
        }

        [Test]
        public void TextBoxFocusSignals_DoNotFireWithoutAFocusChange_NegativeTwin()
        {
            World world = BuildWorld(false);
            world.Load("m", @"
                UserInputService.TextBoxFocused:Connect(function() store_set('fired', 'yes') end)");
            world.Frames(5);

            Assert.AreEqual("", world.Get("m", "fired"));
        }
    }
}
