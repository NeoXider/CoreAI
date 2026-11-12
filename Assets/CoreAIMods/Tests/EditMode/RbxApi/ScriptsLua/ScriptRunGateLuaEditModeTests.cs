using System;
using System.Collections.Generic;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Infrastructure.Logging;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using CoreAI.Mods.Rbx.Instances.Scripts;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.ScriptsLua
{
    /// <summary>
    /// MVP4 step C1-6 through the production mod stack: the registration/run split. A source is registered with its
    /// script instance, source and versions even when it does not run: a ModuleScript (<c>context: shared</c>), a script
    /// whose side this process does not have (a client script on a dedicated server, a server script on a pure client),
    /// a disabled script and a script in a container that runs no scripts. A legacy mod keeps today's per-process rule
    /// (SC-8). A context change that needs another class replaces the instance (SC-4). Every rule has its negative twin.
    /// </summary>
    [TestFixture]
    public sealed class ScriptRunGateLuaEditModeTests
    {
        private const string ServerHeader = "-- @coreai context: server\n";
        private const string ClientHeader = "-- @coreai context: client\n";
        private const string SharedHeader = "-- @coreai context: shared\n";
        private const string CountTicks =
            "hooks_on('tick', function() store_set('ticks', tostring((tonumber(store_get('ticks')) or 0) + 1)) end)\n";

        private SynchronizationContext _savedContext;
        private World _world;

        [SetUp]
        public void SetUp()
        {
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            _world = new World(RbxNetworkTopology.Solo);
        }

        [TearDown]
        public void TearDown()
        {
            _world?.Dispose();
            _world = null;
            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        private void UseTopology(RbxNetworkTopology topology)
        {
            _world.Dispose();
            _world = new World(topology);
        }

        private RbxInstance Service(string className)
        {
            return _world.Roblox.Game.FindFirstChildOfClass(className);
        }

        private RbxLuaSourceContainer BoundScript(string modId)
        {
            Assert.IsTrue(_world.Roblox.ScriptInstances.TryGetBoundScript(modId, out RbxLuaSourceContainer script),
                "mod '" + modId + "' has a bound instance");
            return script;
        }

        private LuaModInfo Listed(string modId)
        {
            foreach (LuaModInfo info in _world.Stack.Runtime.ListMods())
            {
                if (info.Id == modId)
                {
                    return info;
                }
            }

            Assert.Fail("mod '" + modId + "' is not listed");
            return null;
        }

        private RbxInstance Part(string name, RbxInstance parent)
        {
            RbxInstance part = _world.Roblox.Registry.Create("Part");
            part.Name = name;
            part.Parent = parent;
            return part;
        }

        // ---- a ModuleScript is registered, never run ----------------------------------------------

        [Test]
        public void C1_6_Load_SharedContext_RegistersAModuleScript_WithoutRunningIt()
        {
            _world.Load("tools", SharedHeader + "-- @coreai category: Kit\nstore_set('ran', 'yes')\nreturn {}");

            Assert.AreEqual("", _world.Get("tools", "ran"), "a module runs only when a script requires it (R2.1)");
            Assert.IsTrue(_world.Stack.Runtime.IsLoaded("tools"), "registered: listed, reloadable, unloadable");
            RbxLuaSourceContainer module = BoundScript("tools");
            Assert.AreEqual(ScriptClassNames.ModuleScript, module.ClassName);
            Assert.AreEqual("Kit", module.Parent.Name);
            Assert.AreSame(Service("ReplicatedStorage"), module.Parent.Parent);
            LuaModInfo listed = Listed("tools");
            Assert.AreEqual("shared", listed.Context);
            Assert.AreEqual("module", listed.RunState);
            Assert.AreEqual("ReplicatedStorage.Kit.tools", listed.ScriptPath);
            Assert.AreEqual(0, listed.HandlerCount);
            Assert.IsTrue(_world.Stack.Runtime.TryGetModSource("tools", out string source));
            StringAssert.Contains("context: shared", source);
        }

        [Test]
        public void C1_6_Load_Negative_AServerScriptLoadedTheSameWay_Runs()
        {
            _world.Load("door", ServerHeader + "store_set('ran', 'yes')");

            Assert.AreEqual("yes", _world.Get("door", "ran"));
            Assert.AreEqual("running", Listed("door").RunState);
        }

        [Test]
        public void C1_6_UnloadOfAModule_KeepsItsModuleScript_AndALoadRegistersItAgainOnIt()
        {
            _world.Load("tools", SharedHeader + "return {}");
            RbxLuaSourceContainer module = BoundScript("tools");

            Assert.IsTrue(_world.Stack.Runtime.UnloadMod("tools"));

            Assert.IsFalse(_world.Stack.Runtime.IsLoaded("tools"));
            Assert.IsFalse(module.IsDestroyed, "the ModuleScript is world-owned, as every bound instance is");
            _world.Load("tools", SharedHeader + "return {}");
            Assert.AreSame(module, BoundScript("tools"));
            Assert.AreEqual("module", Listed("tools").RunState);
        }

        [Test]
        public void C1_6_DestroyingARegisteredModulesInstance_ListsItDetached()
        {
            _world.Load("tools", SharedHeader + "return {}");

            BoundScript("tools").Destroy();

            Assert.AreEqual("detached", Listed("tools").RunState);
            Assert.AreEqual("", Listed("tools").ScriptPath);
        }

        // ---- SC-4: a class change replaces the instance -------------------------------------------

        [Test]
        public void SC_4_Reload_ServerToShared_StopsTheRun_AndAModuleScriptTakesTheScriptsPlaceAndChildren()
        {
            _world.Load("door", ServerHeader + CountTicks);
            RbxLuaSourceContainer script = BoundScript("door");
            RbxInstance hinge = Part("Hinge", script);
            _world.Stack.Runtime.Tick(0.1d);
            Assert.AreEqual("1", _world.Get("door", "ticks"));

            _world.Stack.Runtime.ReloadMod("door", SharedHeader + CountTicks);
            _world.Stack.Runtime.Tick(0.1d);

            Assert.AreEqual("1", _world.Get("door", "ticks"), "a ModuleScript has no run, so its old handlers are gone");
            RbxLuaSourceContainer module = BoundScript("door");
            Assert.AreEqual(ScriptClassNames.ModuleScript, module.ClassName);
            Assert.AreNotEqual(script.Id, module.Id);
            Assert.IsTrue(script.IsDestroyed, "the replaced Script leaves the world once the reload succeeded");
            Assert.AreSame(module, hinge.Parent, "children move to the replacement");
            Assert.AreSame(Service("ReplicatedStorage"), module.Parent, "a default-placed script follows its new context");
            Assert.AreEqual("module", Listed("door").RunState);
        }

        [Test]
        public void SC_4_Reload_ModuleToServer_ReplacesTheModuleScript_AndTheScriptRuns()
        {
            _world.Load("tools", SharedHeader + "return {}");
            RbxLuaSourceContainer module = BoundScript("tools");
            Part("Gear", module);

            _world.Stack.Runtime.ReloadMod("tools",
                ServerHeader + "store_set('ran', script.ClassName .. '|' .. tostring(#script:GetChildren()))");

            Assert.AreEqual("Script|1", _world.Get("tools", "ran"));
            Assert.IsTrue(module.IsDestroyed);
            Assert.AreEqual("running", Listed("tools").RunState);
        }

        [Test]
        public void SC_4_Reload_ModuleToServer_ThatFails_PutsTheModuleScriptBackWithItsChildren()
        {
            _world.Load("tools", SharedHeader + "return {}");
            RbxLuaSourceContainer module = BoundScript("tools");
            RbxInstance gear = Part("Gear", module);

            Assert.Catch(() => _world.Stack.Runtime.ReloadMod("tools", ServerHeader + "error('boom')"));

            Assert.AreSame(module, BoundScript("tools"), "a failed reload leaves the world as it was");
            Assert.IsFalse(module.IsDestroyed);
            Assert.AreSame(Service("ReplicatedStorage"), module.Parent);
            Assert.AreSame(module, gear.Parent);
            Assert.IsNull(Service("ServerScriptService").FindFirstChild("tools"), "the replacement is gone");
            Assert.AreEqual("module", Listed("tools").RunState);
        }

        // ---- SC-9 item 5: the side this process has ------------------------------------------------

        [Test]
        public void SC_9_DedicatedServer_AClientScriptIsRegisteredButNeverStarted()
        {
            UseTopology(RbxNetworkTopology.DedicatedServer);

            _world.Load("hud", ClientHeader + "store_set('ran', 'yes')");

            Assert.AreEqual("", _world.Get("hud", "ran"), "a dedicated server has no client side");
            Assert.AreEqual("not-on-this-side", Listed("hud").RunState);
            Assert.AreSame(Service("ReplicatedStorage"), BoundScript("hud").Parent,
                "its instance is placed and saved like any other, so the clients receive it");
        }

        [Test]
        public void SC_9_DedicatedServer_Negative_AServerScriptAndALegacyModStart()
        {
            UseTopology(RbxNetworkTopology.DedicatedServer);

            _world.Load("door", ServerHeader + "store_set('ran', 'yes')");
            _world.Load("legacy", "store_set('ran', 'yes')");

            Assert.AreEqual("yes", _world.Get("door", "ran"));
            Assert.AreEqual("yes", _world.Get("legacy", "ran"));
        }

        [Test]
        public void SC_9_PureClient_AServerScriptIsNotStarted_AClientScriptIs()
        {
            UseTopology(RbxNetworkTopology.Client);

            _world.Load("door", ServerHeader + "store_set('ran', 'yes')");
            _world.Load("hud", ClientHeader + "store_set('ran', 'yes')");

            Assert.AreEqual("", _world.Get("door", "ran"));
            Assert.AreEqual("not-on-this-side", Listed("door").RunState);
            Assert.AreEqual("yes", _world.Get("hud", "ran"));
        }

        [Test]
        public void SC_8_PureClient_ALegacyModStillStarts_AsBeforeContexts()
        {
            UseTopology(RbxNetworkTopology.Client);

            _world.Load("legacy", "store_set('ran', 'yes')");

            Assert.AreEqual("yes", _world.Get("legacy", "ran"),
                "a mod that declares nothing keeps today's rule on every topology; the client composition keeps it off clients");
            Assert.AreEqual("running", Listed("legacy").RunState);
        }

        // ---- SC-9 item 4: the container ------------------------------------------------------------

        [Test]
        public void SC_9_AServerScriptPlacedInServerStorage_IsNotStarted()
        {
            _world.Load("vault", ServerHeader + "-- @coreai parent: ServerStorage\nstore_set('ran', 'yes')");

            Assert.AreEqual("", _world.Get("vault", "ran"), "nothing runs in ServerStorage (R2.1)");
            Assert.AreEqual("container-does-not-run", Listed("vault").RunState);
            Assert.AreSame(Service("ServerStorage"), BoundScript("vault").Parent);
        }

        [Test]
        public void SC_9_Negative_AServerScriptPlacedInAWorkspacePart_Starts()
        {
            Part("Gate", Service("Workspace"));

            _world.Load("door", ServerHeader + "-- @coreai parent: Workspace/Gate\nstore_set('ran', script.Parent.Name)");

            Assert.AreEqual("Gate", _world.Get("door", "ran"));
        }

        [Test]
        public void R1_5_ALegacyScriptPlacedInReplicatedStorage_IsNotStarted()
        {
            _world.Load("legacy", "-- @coreai parent: ReplicatedStorage\nstore_set('ran', 'yes')");

            Assert.AreEqual("", _world.Get("legacy", "ran"), "a Legacy Script runs only in Workspace and ServerScriptService");
            Assert.AreEqual("container-does-not-run", Listed("legacy").RunState);
        }

        [Test]
        public void SC_9_AReloadThatMovesTheScriptWhereItRuns_StartsIt()
        {
            Part("Gate", Service("Workspace"));
            _world.Load("vault", ServerHeader + "-- @coreai parent: ServerStorage\nstore_set('ran', 'first')");

            _world.Stack.Runtime.ReloadMod("vault",
                ServerHeader + "-- @coreai parent: Workspace/Gate\nstore_set('ran', 'second')");

            Assert.AreEqual("second", _world.Get("vault", "ran"));
            Assert.AreEqual("running", Listed("vault").RunState);
        }

        // ---- SC-9 item 3: Enabled -------------------------------------------------------------------

        [Test]
        public void SC_3_ADisabledBoundScript_IsNotStartedByAReload_AndAnEnabledOneIs()
        {
            const string source = ServerHeader + "store_set('runs', tostring((tonumber(store_get('runs')) or 0) + 1))";
            _world.Load("door", source);
            RbxBaseScript script = (RbxBaseScript)BoundScript("door");
            script.Enabled = false;

            _world.Stack.Runtime.ReloadMod("door", source);

            Assert.AreEqual("1", _world.Get("door", "runs"));
            Assert.AreEqual("disabled", Listed("door").RunState);
            script.Enabled = true;
            _world.Stack.Runtime.ReloadMod("door", source);
            Assert.AreEqual("2", _world.Get("door", "runs"), "twin: an enabled script starts again");
        }

        // ---- harness ---------------------------------------------------------------------------

        private sealed class World : IDisposable
        {
            public World(RbxNetworkTopology topology)
            {
                Roblox = new LuaCsRbxApiBindings(networkBridge: new TopologyBridge(topology));
                Store = new MemoryStore();
                Stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
                {
                    Logger = new SilentGameLogger(),
                    ModStore = Store,
                    RbxApi = Roblox,
                    LuaScriptVersions = new MemoryLuaScriptVersionStore()
                });
            }

            public LuaCsRbxApiBindings Roblox { get; }

            public MemoryStore Store { get; }

            public LuaCsModStack Stack { get; }

            public void Load(string modId, string source)
            {
                Stack.Runtime.LoadMod(modId, source, LuaCapabilities.All, false);
            }

            public string Get(string modId, string key)
            {
                return Store.Get(modId, key);
            }

            public void Dispose()
            {
                Roblox.Dispose();
            }
        }

        /// <summary>A loopback bridge that reports another topology; nothing in these tests sends a remote.</summary>
        private sealed class TopologyBridge : INetworkBridge
        {
            private readonly List<string> _actors = new();

            public TopologyBridge(RbxNetworkTopology topology)
            {
                Topology = topology;
            }

            public RbxNetworkTopology Topology { get; }

            public IReadOnlyList<string> ActorIds => _actors;

            public event Action<RbxNetworkEventMessage> EventReceived
            {
                add { }
                remove { }
            }

            public event Action<RbxNetworkRequestMessage, RbxNetworkRequestResponder> RequestReceived
            {
                add { }
                remove { }
            }

            public event Action<RbxNetworkPeerDisconnected> PeerDisconnected
            {
                add { }
                remove { }
            }

            public int MaxPayloadBytes => 65536;

            public double ServerClockOffsetSeconds => 0d;

            public void RegisterActor(string actorId)
            {
                if (!_actors.Contains(actorId))
                {
                    _actors.Add(actorId);
                }
            }

            public void UnregisterActor(string actorId)
            {
                _actors.Remove(actorId);
            }

            public void SendEvent(RbxNetworkEventMessage message)
            {
            }

            public void SendRequest(RbxNetworkRequestMessage message, Action<RbxNetworkResponse> response)
            {
            }
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
                foreach ((string ModId, string Key) key in _values.Keys)
                {
                    if (key.ModId == modId)
                    {
                        keys.Add(key);
                    }
                }

                foreach ((string ModId, string Key) key in keys)
                {
                    _values.Remove(key);
                }
            }
        }

        private sealed class SilentGameLogger : IGameLogger
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
    }
}
