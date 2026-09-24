using System.Collections.Generic;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Infrastructure.Logging;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.LuaBindings
{
    /// <summary>
    /// Proof of the ClickDetector wiring through the REAL mod runtime for the parts that do not need
    /// a live scene (Physics/Camera raycasting is a Play Mode check — the pick pump itself is verified
    /// there): Instance.new("ClickDetector") is creatable and parents under a Part, MouseClick is a
    /// dispatch-enabled signal a mod connects and that fires its handler when the host fire path runs,
    /// MaxActivationDistance round-trips, and a mod's MouseClick connection is dropped on unload.
    /// </summary>
    [TestFixture]
    public sealed class RbxClickDetectorLuaBindingsEditModeTests
    {
        private SynchronizationContext _savedContext;

        /// <summary>Same sync-over-async hazard as the sibling RunService fixture: detach Unity's
        /// SynchronizationContext so VM continuations complete on the thread pool.</summary>
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
                List<(string ModId, string Key)> keys = new();
                foreach ((string storedModId, string key) in _values.Keys)
                {
                    if (storedModId == modId)
                    {
                        keys.Add((storedModId, key));
                    }
                }

                foreach ((string ModId, string Key) key in keys)
                {
                    _values.Remove(key);
                }
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

        private static LuaCsModStack BuildStack(LuaCsRbxApiBindings roblox, MemoryStore store)
        {
            return LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new FakeGameLogger(),
                ModStore = store,
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All,
                RbxApi = roblox
            });
        }

        /// <summary>Same wiring as the CoreAiModsInstaller / the connection-teardown fixture: a shared
        /// ledger plus the ModTearingDown sweep that disconnects a mod's connections on unload.</summary>
        private static LuaCsModStack BuildWiredStack(out LuaCsRbxApiBindings roblox, MemoryStore store)
        {
            ModConnectionRegistry connections = new();
            roblox = new LuaCsRbxApiBindings(connections: connections);
            LuaCsModStack stack = BuildStack(roblox, store);
            stack.Runtime.ModTearingDown += (modId, reason) => connections.DisconnectOwnedBy(
                modId, reason == LuaModTeardownReason.Reload);
            return stack;
        }

        // WHY: the pick pump resolves the clicked part's ClickDetector child from C#; the tests fire
        // that same signal directly (the raycast that selects it is a Play Mode concern).
        private static RbxClickDetector FindClickDetector(LuaCsRbxApiBindings roblox)
        {
            foreach (RbxInstance descendant in roblox.Game.GetDescendants())
            {
                if (descendant is RbxClickDetector detector)
                {
                    return detector;
                }
            }

            return null;
        }

        [Test]
        public void Lua_ClickDetector_IsCreatable_AndParentsUnderPart()
        {
            LuaCsRbxApiBindings roblox = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(roblox, store);

            stack.Runtime.LoadMod("m", @"
                local part = Instance.new('Part')
                part.Parent = workspace
                local cd = Instance.new('ClickDetector')
                cd.Parent = part
                store_set('class', cd.ClassName)
                store_set('is_instance', tostring(cd:IsA('Instance')))
                store_set('parented', tostring(cd.Parent == part))
                store_set('found', tostring(part:FindFirstChildOfClass('ClickDetector') == cd))");

            Assert.AreEqual("ClickDetector", store.Get("m", "class"));
            Assert.AreEqual("true", store.Get("m", "is_instance"));
            Assert.AreEqual("true", store.Get("m", "parented"), "the ClickDetector parents under the Part");
            Assert.AreEqual("true", store.Get("m", "found"),
                "the Part exposes its ClickDetector via FindFirstChildOfClass");
            Assert.IsNotNull(FindClickDetector(roblox), "the ClickDetector materialized in the world");
        }

        [Test]
        public void Lua_ClickDetector_MaxActivationDistance_DefaultsTo32_AndRoundTrips()
        {
            LuaCsRbxApiBindings roblox = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(roblox, store);

            stack.Runtime.LoadMod("m", @"
                local cd = Instance.new('ClickDetector')
                store_set('default', tostring(cd.MaxActivationDistance))
                cd.MaxActivationDistance = 12
                store_set('after', tostring(cd.MaxActivationDistance))");

            Assert.AreEqual("32", store.Get("m", "default"), "Roblox default MaxActivationDistance is 32");
            Assert.AreEqual("12", store.Get("m", "after"), "MaxActivationDistance round-trips through Lua");
        }

        [Test]
        public void Lua_ClickDetector_MouseClick_ConnectHandler_FiresOnHostFire()
        {
            LuaCsRbxApiBindings roblox = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(roblox, store);

            stack.Runtime.LoadMod("m", @"
                local part = Instance.new('Part')
                part.Parent = workspace
                local cd = Instance.new('ClickDetector')
                cd.Parent = part
                local n = 0
                cd.MouseClick:Connect(function()
                    n = n + 1
                    store_set('clicks', tostring(n))
                end)");

            RbxClickDetector detector = FindClickDetector(roblox);
            Assert.IsNotNull(detector);
            Assert.IsTrue(detector.MouseClick.HasConnections, "the mod connected a MouseClick handler");

            // WHY: drive the same fire path the pick pump uses when the part is clicked; the handler
            // must run once per fire.
            detector.MouseClick.Fire();
            roblox.Scheduler.Advance(0d);
            Assert.AreEqual("1", store.Get("m", "clicks"), "MouseClick handler runs when the signal fires");

            detector.MouseClick.Fire();
            roblox.Scheduler.Advance(0d);
            Assert.AreEqual("2", store.Get("m", "clicks"), "each MouseClick fire invokes the handler once");
        }

        [Test]
        public void ClickDetector_MouseClick_Connection_IsDropped_OnModUnload()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildWiredStack(out LuaCsRbxApiBindings roblox, store);

            stack.Runtime.LoadMod("m", @"
                local part = Instance.new('Part')
                part.Parent = workspace
                local cd = Instance.new('ClickDetector')
                cd.Parent = part
                cd.MouseClick:Connect(function() end)");

            RbxClickDetector detector = FindClickDetector(roblox);
            Assert.IsNotNull(detector);
            Assert.IsTrue(detector.MouseClick.HasConnections,
                "the mod's MouseClick connection is live while loaded");

            Assert.IsTrue(stack.Runtime.UnloadMod("m"), "the mod unloads");

            // WHY: the ModTearingDown sweep disconnects the mod's MouseClick connection, exactly like
            // a RunService.Heartbeat connection, so a clicked part never fires a torn-down mod's handler.
            Assert.IsFalse(detector.MouseClick.HasConnections,
                "unloading the mod disconnects its MouseClick connection");
        }

        /// <summary>A pick source that reports whatever part and camera distance the test sets.</summary>
        private sealed class ScriptedPickSource : IClickPickSource
        {
            public InstanceId Hit { get; set; } = InstanceId.None;

            public double CameraDistanceStuds { get; set; }

            public bool TryPick(RbxVector2 screenPositionTopLeft, out InstanceId hitId,
                out double distanceStuds)
            {
                hitId = Hit;
                distanceStuds = CameraDistanceStuds;
                return Hit != InstanceId.None;
            }
        }

        private sealed class ClickWorld
        {
            public ClickWorld(bool withCharacter)
            {
                Pick = new ScriptedPickSource();
                Input = new InMemoryInputSource();
                Roblox = new LuaCsRbxApiBindings(pickSource: Pick, inputSource: Input,
                    defaultCharacterAutoLoads: false);
                Store = new MemoryStore();
                Stack = BuildStack(Roblox, Store);
                Player = Roblox.ConnectActor(new LocalActorIdentityProvider("clicker")
                    .GetActorContext(BuiltInAgentRoleIds.Programmer));
                if (!withCharacter)
                {
                    return;
                }

                RbxInstance character = RbxCharacterFactory.Load(
                    Roblox.Registry, Roblox.Registry.WorldRoot, Player);
                RbxInstance root = character.FindFirstChild(RbxCharacterFactory.RootPartName);
                Roblox.PartSink.SetPosition(root.Id, RbxVector3.Zero);
            }

            public ScriptedPickSource Pick { get; }

            public InMemoryInputSource Input { get; }

            public LuaCsRbxApiBindings Roblox { get; }

            public MemoryStore Store { get; }

            public LuaCsModStack Stack { get; }

            public RbxPlayer Player { get; }

            public RbxInstance Find(string name)
            {
                foreach (RbxInstance descendant in Roblox.Registry.WorldRoot.GetDescendants())
                {
                    if (descendant.Name == name)
                    {
                        return descendant;
                    }
                }

                return null;
            }

            /// <summary>One full press and release of the left button over <paramref name="part"/>.</summary>
            public void Click(RbxInstance part, double cameraDistanceStuds)
            {
                Pick.Hit = part.Id;
                Pick.CameraDistanceStuds = cameraDistanceStuds;
                Input.SetMouseButton(0, true);
                Roblox.PumpPreRender(0f);
                Input.SetMouseButton(0, false);
                Roblox.PumpPreRender(0f);
                Roblox.Scheduler.Advance(0d);
            }
        }

        private const string DoorModSource = @"
            local door = Instance.new('Model')
            door.Name = 'Door'
            door.Parent = workspace
            local panel = Instance.new('Part')
            panel.Name = 'Panel'
            panel.Size = Vector3.new(1, 1, 1)
            panel.Position = Vector3.new(20, 0, 0)
            panel.Parent = door
            local detector = Instance.new('ClickDetector')
            detector.Parent = door
            local clicks = 0
            detector.MouseClick:Connect(function(player)
                clicks = clicks + 1
                store_set('clicks', tostring(clicks))
                store_set('who', typeof(player) .. ':' .. tostring(player and player.Name))
            end)";

        [Test]
        public void Lua_ClickDetector_MouseClick_PassesThePlayerWhoClicked_FromAModelLevelDetector()
        {
            ClickWorld world = new(withCharacter: true);
            world.Stack.Runtime.LoadMod("door", DoorModSource);

            world.Click(world.Find("Panel"), 40d);

            Assert.AreEqual("1", world.Store.Get("door", "clicks"),
                "a detector parented to the part's Model fires for a click on the part");
            Assert.AreEqual("Instance:" + world.Player.Name, world.Store.Get("door", "who"),
                "MouseClick passes the Player who clicked");
        }

        [Test]
        public void Lua_ClickDetector_Distance_IsMeasuredFromTheCharacter_NotTheCamera()
        {
            ClickWorld world = new(withCharacter: true);
            world.Stack.Runtime.LoadMod("door", DoorModSource);
            RbxInstance panel = world.Find("Panel");

            world.Click(panel, 40d);
            Assert.AreEqual("1", world.Store.Get("door", "clicks"),
                "19.5 studs from the character but 40 from the camera is within the default 32");

            world.Roblox.PartSink.SetPosition(panel.Id, new RbxVector3(40f, 0f, 0f));
            world.Click(panel, 5d);
            Assert.AreEqual("1", world.Store.Get("door", "clicks"),
                "39.5 studs from the character stays out of range even with the camera 5 studs away");
        }

        [Test]
        public void Lua_ClickDetector_FolderLevelDetectorFires_AndTheDeepestDetectorWins()
        {
            ClickWorld world = new(withCharacter: true);
            world.Stack.Runtime.LoadMod("m", @"
                local folder = Instance.new('Folder')
                folder.Parent = workspace
                local button = Instance.new('Part')
                button.Name = 'Button'
                button.Position = Vector3.new(3, 0, 0)
                button.Parent = folder
                local folderDetector = Instance.new('ClickDetector')
                folderDetector.Parent = folder
                folderDetector.MouseClick:Connect(function() store_set('folder', 'fired') end)
                local lever = Instance.new('Part')
                lever.Name = 'Lever'
                lever.Position = Vector3.new(0, 0, 3)
                lever.Parent = folder
                local leverDetector = Instance.new('ClickDetector')
                leverDetector.Parent = lever
                leverDetector.MouseClick:Connect(function() store_set('lever', 'fired') end)");

            world.Click(world.Find("Button"), 10d);
            Assert.AreEqual("fired", world.Store.Get("m", "folder"), "a Folder-level detector fires");

            world.Store.Set("m", "folder", null);
            world.Click(world.Find("Lever"), 10d);
            Assert.AreEqual("fired", world.Store.Get("m", "lever"), "the part's own detector fires");
            Assert.AreEqual("", world.Store.Get("m", "folder"),
                "only the deepest detector fires, never its ancestor's as well");
        }

        [Test]
        public void Negative_Lua_ClickDetector_ParentedToWorkspace_DoesNotClaimEveryClick()
        {
            ClickWorld world = new(withCharacter: true);
            world.Stack.Runtime.LoadMod("m", @"
                local part = Instance.new('Part')
                part.Name = 'Loose'
                part.Parent = workspace
                local detector = Instance.new('ClickDetector')
                detector.Parent = workspace
                detector.MouseClick:Connect(function() store_set('fired', 'yes') end)");

            world.Click(world.Find("Loose"), 5d);

            Assert.AreEqual("", world.Store.Get("m", "fired"));
        }

        [Test]
        public void Lua_ClickDetector_WithoutACharacter_FallsBackToTheCameraDistance()
        {
            ClickWorld world = new(withCharacter: false);
            world.Stack.Runtime.LoadMod("door", DoorModSource);
            RbxInstance panel = world.Find("Panel");

            world.Click(panel, 40d);
            Assert.AreEqual("", world.Store.Get("door", "clicks"), "40 camera studs is out of range");

            world.Click(panel, 10d);
            Assert.AreEqual("1", world.Store.Get("door", "clicks"));
            Assert.AreEqual("Instance:" + world.Player.Name, world.Store.Get("door", "who"),
                "the only connected player is the one who clicked");
        }

        [Test]
        public void ClickDetector_Destroy_DisconnectsItsMouseClickHandlers()
        {
            LuaCsRbxApiBindings roblox = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(roblox, store);
            stack.Runtime.LoadMod("m", @"
                local part = Instance.new('Part')
                part.Parent = workspace
                local detector = Instance.new('ClickDetector')
                detector.Parent = part
                detector.MouseClick:Connect(function() store_set('clicked', 'yes') end)");
            RbxClickDetector detector = FindClickDetector(roblox);
            Assert.IsTrue(detector.MouseClick.HasConnections);

            detector.Destroy();

            // WHY: Destroy disconnects exactly the signals the instance holds in its signal table; a
            // field-initialised MouseClick kept delivering to a destroyed detector's handlers.
            Assert.IsFalse(detector.MouseClick.HasConnections,
                "destroying the detector disconnects its MouseClick handlers");
            detector.MouseClick.Fire();
            roblox.Scheduler.Advance(0d);
            Assert.AreEqual("", store.Get("m", "clicked"));
        }
    }
}
