using System;
using System.Collections.Generic;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Infrastructure.Logging;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Spatial;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode.RobloxApi.LuaBindings
{
    /// <summary>
    /// Camera control from Lua (MVP1 mini-game enabler) end-to-end through the real mod
    /// runtime: workspace.CurrentCamera resolves to the canonical Camera instance whose CFrame
    /// drives a fabricated Unity camera through <see cref="UnityCameraRig"/> (RobloxSpace
    /// transposition, D2), the camera_set_cframe/camera_follow convenience globals, and the
    /// WorldEdit gate on every camera write (reads stay open). Plus the Lua-level Part.Shape
    /// write over Enum.PartType materializing through the GameObject binder.
    /// </summary>
    [TestFixture]
    public sealed class RobloxCameraLuaBindingsEditModeTests
    {
        private const float Epsilon = 1e-4f;

        private SynchronizationContext _savedContext;
        private GameObject _root;
        private GameObject _cameraGo;

        [SetUp]
        public void SetUp()
        {
            // WHY: same sync-over-async hazard as LuaCsModRuntimeEditModeTests — detach Unity's
            // SynchronizationContext so VM continuations complete on the thread pool.
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            RobloxSpace.ResetForTests(0.28f);
            _root = new GameObject("CameraTestRoot");
            _cameraGo = new GameObject("FabricatedCamera");
            _cameraGo.AddComponent<Camera>();
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(_cameraGo);
            UnityEngine.Object.DestroyImmediate(_root);
            RobloxSpace.ResetForTests();
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

        private sealed class FakeGameLogger : IGameLogger
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
            public InstanceGameObjectBinder Binder;
            public InstanceRegistry Registry;
            public RbxDataModel Game;
            public LuaCsRobloxApiBindings Roblox;
            public UnityCameraRig Rig;
            public LuaCsModStack Stack;
        }

        /// <summary>Materialized world (binder + fabricated camera rig) behind a real mod stack.</summary>
        private World BuildWorld(LuaCapabilities caps = LuaCapabilities.All)
        {
            var world = new World();
            world.Binder = new InstanceGameObjectBinder(_root.transform);
            world.Registry = new InstanceRegistry(null, world.Binder);
            world.Game = DataModelBootstrap.CreateGame(world.Registry);
            world.Rig = new UnityCameraRig(_cameraGo.transform, world.Binder);
            world.Roblox = new LuaCsRobloxApiBindings(
                world.Registry, world.Game, partSink: world.Binder, cameraRig: world.Rig);
            world.Stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new FakeGameLogger(),
                ModStore = new MemoryStore(),
                Capabilities = caps,
                OneOffCapabilities = caps,
                RobloxApi = world.Roblox
            });
            return world;
        }

        private static Exception LoadFails(World world, string modId, string code)
        {
            return Assert.Catch(() => world.Stack.Runtime.LoadMod(modId, code));
        }

        // ---- workspace.CurrentCamera --------------------------------------------------------

        [Test]
        public void Lua_CurrentCamera_ResolvesToTheCanonicalCameraInstance()
        {
            World world = BuildWorld();
            world.Stack.Runtime.LoadMod("m", @"
                local cam = workspace.CurrentCamera
                assert(cam ~= nil, 'CurrentCamera must resolve')
                assert(cam.ClassName == 'Camera')
                assert(cam.Parent == workspace)");
            Assert.IsTrue(world.Stack.Runtime.IsLoaded("m"));
        }

        [Test]
        public void Lua_CurrentCameraCFrameWrite_MovesTheUnityCameraToTheTransposedPose()
        {
            World world = BuildWorld();
            world.Stack.Runtime.LoadMod("m",
                "workspace.CurrentCamera.CFrame = CFrame.new(10, 5, -4)");

            Vector3 position = _cameraGo.transform.position;
            Assert.AreEqual(2.8f, position.x, Epsilon);
            Assert.AreEqual(1.4f, position.y, Epsilon);
            Assert.AreEqual(1.12f, position.z, Epsilon, "mod-space z = -Unity z (D2)");
        }

        [Test]
        public void Lua_CurrentCameraCFrameRead_RoundTripsThroughRobloxSpace()
        {
            World world = BuildWorld();
            _cameraGo.transform.position = new Vector3(2.8f, 1.4f, 1.12f);
            world.Stack.Runtime.LoadMod("m", @"
                local function near(a, b) return math.abs(a - b) < 1e-3 end
                local cf = workspace.CurrentCamera.CFrame
                assert(near(cf.X, 10) and near(cf.Y, 5) and near(cf.Z, -4))");
            Assert.IsTrue(world.Stack.Runtime.IsLoaded("m"));
        }

        [Test]
        public void Lua_CameraTypeAndSubject_RoundTrip()
        {
            World world = BuildWorld();
            world.Stack.Runtime.LoadMod("m", @"
                local cam = workspace.CurrentCamera
                assert(cam.CameraType == Enum.CameraType.Custom, 'Roblox default CameraType')
                cam.CameraType = Enum.CameraType.Scriptable
                assert(cam.CameraType == Enum.CameraType.Scriptable)
                assert(cam.CameraSubject == nil)
                local p = Instance.new('Part')
                p.Parent = workspace
                cam.CameraSubject = p
                assert(cam.CameraSubject == p)");
            Assert.IsTrue(world.Stack.Runtime.IsLoaded("m"));
        }

        // ---- camera_set_cframe / camera_follow ----------------------------------------------

        [Test]
        public void Lua_CameraSetCFrame_MovesAndRotatesTheCamera()
        {
            World world = BuildWorld();
            world.Stack.Runtime.LoadMod("m",
                "camera_set_cframe(CFrame.new(10, 5, -4) * CFrame.Angles(0, math.pi / 2, 0))");

            Vector3 position = _cameraGo.transform.position;
            Assert.AreEqual(2.8f, position.x, Epsilon);
            Assert.AreEqual(1.4f, position.y, Epsilon);
            Assert.AreEqual(1.12f, position.z, Epsilon);
            // WHY: Roblox yaw +90 turns LookVector onto -X; the transposed Unity forward agrees.
            Assert.AreEqual(-1f, _cameraGo.transform.forward.x, Epsilon);
        }

        [Test]
        public void Lua_CameraFollow_TracksTheTargetAndStopsOnNil()
        {
            World world = BuildWorld();
            world.Stack.Runtime.LoadMod("m", @"
                local p = Instance.new('Part')
                p.Name = 'Hero'
                p.Parent = workspace
                p.Position = Vector3.new(10, 0, 0)
                camera_set_cframe(CFrame.new(10, 5, 10))
                camera_follow(p)");

            RbxInstance part = world.Game.FindFirstChildOfClass("Workspace").FindFirstChild("Hero");
            Assert.IsTrue(world.Binder.TryGetBoundObject(part.Id, out GameObject partGo));

            var follower = _cameraGo.GetComponent<RobloxCameraFollower>();
            Assert.IsNotNull(follower, "camera_follow must attach the follower to the camera");
            Assert.IsTrue(follower.enabled);
            Assert.AreSame(partGo.transform, follower.Target);

            // Move the part 10 studs (+X) and tick the follower: the camera keeps its offset.
            Vector3 cameraBefore = _cameraGo.transform.position;
            world.Binder.SetPosition(part.Id, new RbxVector3(20f, 0f, 0f));
            follower.Apply();
            Vector3 delta = _cameraGo.transform.position - cameraBefore;
            Assert.AreEqual(2.8f, delta.x, Epsilon, "10 studs = 2.8 m at the locked scale");
            Assert.AreEqual(0f, delta.y, Epsilon);
            Assert.AreEqual(0f, delta.z, Epsilon);

            world.Stack.Runtime.LoadMod("stop", "camera_follow(nil)");
            Assert.IsFalse(follower.enabled, "camera_follow(nil) stops the follow");
        }

        [Test]
        public void Lua_CameraFollow_TargetWithoutBackingObject_RaisesActionableError()
        {
            World world = BuildWorld();
            Exception ex = LoadFails(world, "m", @"
                local p = Instance.new('Part')
                camera_follow(p)");
            StringAssert.Contains("no backing object", ex.ToString());
            StringAssert.Contains("Workspace", ex.ToString());
        }

        // ---- WorldEdit gate (writes refused, reads open) ------------------------------------

        [Test]
        public void Lua_CameraWrites_WithoutWorldEdit_AreRefusedActionably()
        {
            LuaCapabilities readOnly =
                LuaCapabilities.Read | LuaCapabilities.Gameplay | LuaCapabilities.LogicOverride;
            World world = BuildWorld(readOnly);

            world.Stack.Runtime.LoadMod("reader", @"
                local cf = workspace.CurrentCamera.CFrame
                assert(cf ~= nil, 'camera reads stay available on Read tier')");

            Vector3 before = _cameraGo.transform.position;
            Exception cframeEx = LoadFails(world, "w1",
                "workspace.CurrentCamera.CFrame = CFrame.new(1, 2, 3)");
            StringAssert.Contains("WorldEdit", cframeEx.ToString());

            Exception helperEx = LoadFails(world, "w2", "camera_set_cframe(CFrame.new(1, 2, 3))");
            StringAssert.Contains("WorldEdit", helperEx.ToString());

            Exception followEx = LoadFails(world, "w3", "camera_follow(nil)");
            StringAssert.Contains("WorldEdit", followEx.ToString());

            Assert.AreEqual(before, _cameraGo.transform.position, "refused writes must not move the camera");
        }

        // ---- Part.Shape over Enum.PartType (Lua -> binder) ----------------------------------

        [Test]
        public void Lua_PartShape_BallAndCylinder_MaterializeAndReadBack()
        {
            World world = BuildWorld();
            world.Stack.Runtime.LoadMod("m", @"
                local ball = Instance.new('Part')
                ball.Name = 'Ball'
                ball.Shape = Enum.PartType.Ball
                ball.Parent = workspace
                assert(ball.Shape == Enum.PartType.Ball)
                local cyl = Instance.new('Part')
                cyl.Name = 'Cyl'
                cyl.Parent = workspace
                assert(cyl.Shape == Enum.PartType.Block, 'Roblox default Shape is Block')
                cyl.Shape = Enum.PartType.Cylinder
                assert(cyl.Shape == Enum.PartType.Cylinder)");

            RbxInstance workspaceInstance = world.Game.FindFirstChildOfClass("Workspace");
            RbxInstance ball = workspaceInstance.FindFirstChild("Ball");
            Assert.IsTrue(world.Binder.TryGetBoundObject(ball.Id, out GameObject ballGo));
            Assert.AreEqual("Sphere", ballGo.GetComponent<MeshFilter>().sharedMesh.name);

            RbxInstance cylinder = workspaceInstance.FindFirstChild("Cyl");
            Assert.IsTrue(world.Binder.TryGetBoundObject(cylinder.Id, out GameObject cylinderGo));
            Assert.AreEqual("Cylinder",
                cylinderGo.transform.Find("Shape").GetComponent<MeshFilter>().sharedMesh.name);
        }

        [Test]
        public void Lua_PartShape_WrongValueType_RaisesBadArgument()
        {
            World world = BuildWorld();
            Exception ex = LoadFails(world, "m",
                "Instance.new('Part').Shape = Enum.Material.Wood");
            StringAssert.Contains("BAD_ARGUMENT", ex.ToString());
            StringAssert.Contains("Enum.PartType", ex.ToString());
        }
    }
}
