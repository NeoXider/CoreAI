using System;
using System.Collections.Generic;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Infrastructure.Logging;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using CoreAI.Mods.Rbx.Instances.Scripts;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.ScriptsLua
{
    /// <summary>
    /// The acceptance of MVP4 (roadmap MVP4 DoD; design note step C6), through the production mod stack: a server-only
    /// API from a client script raises CONTEXT_VIOLATION and the same call from a server script passes (DoD 1); one solo
    /// process runs server, client and shared code that talk through loopback remotes only (DoD 2); a Script inside a
    /// Part has that part as <c>script.Parent</c> and its <c>Touched</c> works (DoD 3). DoD 4 (an old package without
    /// contexts loads as legacy scripts) is <c>Mvp4_PackageV1WithoutContexts_LoadsAsLegacyScripts_GoldenTree</c> in
    /// <see cref="ScriptSessionEditModeTests"/>. Also the SC-13 rows step C6 binds: <c>logic_define</c>/<c>logic_reset</c>,
    /// <c>events_emit</c> across sides, the finders on <c>game</c>, and the hook that adds a package's rows.
    /// </summary>
    [TestFixture]
    public sealed class Mvp4ScriptContextsAcceptanceEditModeTests
    {
        private const string ServerHeader = "-- @coreai context: server\n";
        private const string ClientHeader = "-- @coreai context: client\n";
        private const string SharedHeader = "-- @coreai context: shared\n";

        private const string MakeRemote = @"
local remote = Instance.new('RemoteEvent')
remote.Name = 'Ping'
remote.Parent = game:GetService('ReplicatedStorage')";

        private const string FireAllClients = @"
local ok, err = pcall(function() game:GetService('ReplicatedStorage').Ping:FireAllClients('hi') end)
script:SetAttribute('fire', tostring(ok) .. '|' .. tostring(err))";

        private SynchronizationContext _savedContext;
        private World _world;

        [SetUp]
        public void SetUp()
        {
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            _world = new World(new TopologyBridge(RbxNetworkTopology.Solo));
        }

        [TearDown]
        public void TearDown()
        {
            _world?.Dispose();
            _world = null;
            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        private void Frames(int count)
        {
            for (int index = 0; index < count; index++)
            {
                _world.Roblox.Scheduler.Advance(0.05d);
                _world.Stack.Runtime.Tick(0.05d);
            }
        }

        // ---- DoD 1 -----------------------------------------------------------------------------------

        [Test]
        public void Mvp4_DoD1_ServerOnlyApiFromAClientScript_IsContextViolationNamingTheRule()
        {
            _world.ConnectLocalPlayer();
            _world.Load("setup", MakeRemote);

            _world.Load("hud", ClientHeader + FireAllClients);

            string fire = _world.Seen("hud", "fire");
            StringAssert.Contains("CONTEXT_VIOLATION: RemoteEvent.FireAllClients is server-only", fire);
            StringAssert.Contains("this script runs as client | fix:", fire);
        }

        [Test]
        public void Mvp4_DoD1_Negative_TheSameCallFromAServerScript_Passes()
        {
            _world.Load("setup", MakeRemote);

            _world.Load("door", ServerHeader + FireAllClients);

            Assert.AreEqual("true|nil", _world.Seen("door", "fire"));
        }

        // ---- DoD 2 -----------------------------------------------------------------------------------

        [Test]
        public void Mvp4_Solo_ServerClientShared_TalkThroughLoopbackRemotesOnly()
        {
            _world.Dispose();
            _world = new World(null);
            ActorContext player = LuaCsRbxApiBindings.ModActorLedger.CreateAttributedContext("player-1",
                _world.Roblox.Registry.WorldId);
            _world.Roblox.ConnectActor(player);
            _world.Load("proto", SharedHeader + "return { greet = function(name) return 'hi ' .. name end }");
            _world.Load("server", ServerHeader + MakeRemote + @"
local proto = require(game:GetService('ReplicatedStorage').proto)
remote.OnServerEvent:Connect(function(sender, name)
  store_set('from', sender.Name)
  remote:FireClient(sender, proto.greet(name))
end)");

            _world.Load("client", ClientHeader + @"
local proto = require(game:GetService('ReplicatedStorage').proto)
local ping = game:GetService('ReplicatedStorage'):WaitForChild('Ping')
ping.OnClientEvent:Connect(function(reply) script:SetAttribute('reply', reply .. '|' .. proto.greet('back')) end)
ping:FireServer('client')");
            Frames(6);

            Assert.IsFalse(string.IsNullOrEmpty(_world.Get("server", "from")), "the server heard the client's remote");
            Assert.AreEqual("hi client|hi back", _world.Seen("client", "reply"),
                "the client heard the server's answer; each side ran the shared module itself");
        }

        [Test]
        public void Mvp4_Solo_DirectModsCallFromClientToServer_IsContextViolation()
        {
            _world.ConnectLocalPlayer();
            _world.Load("bank", ServerHeader + "mods_export('balance', function() return 7 end)");

            _world.Load("hud", ClientHeader + @"
local ok, err = pcall(function() return mods_call('bank', 'balance') end)
script:SetAttribute('call', tostring(ok) .. '|' .. tostring(err))");

            StringAssert.Contains("CONTEXT_VIOLATION", _world.Seen("hud", "call"));
            StringAssert.Contains("crosses the client-server boundary", _world.Seen("hud", "call"));
        }

        // ---- DoD 3 -----------------------------------------------------------------------------------

        [Test]
        public void Mvp4_ScriptInsidePart_ParentIsPart_TouchedFires()
        {
            RbxInstance workspace = _world.Roblox.Registry.WorldRoot;
            RbxBasePart pad = (RbxBasePart)_world.Roblox.Registry.Create("Part");
            pad.Name = "Pad";
            pad.Parent = workspace;
            RbxInstance ball = _world.Roblox.Registry.Create("Part");
            ball.Name = "Ball";
            ball.Parent = workspace;

            _world.Load("pad", ServerHeader + "-- @coreai parent: Workspace/Pad\n"
                               + "script:SetAttribute('parent', script.Parent.Name)\n"
                               + "script.Parent.Touched:Connect(function(hit) script:SetAttribute('hit', hit.Name) end)");
            pad.FireTouched(ball);
            Frames(2);

            Assert.AreEqual("Pad", _world.Seen("pad", "parent"));
            Assert.AreEqual("Ball", _world.Seen("pad", "hit"));
        }

        [Test]
        public void Mvp4_ScriptInServerScriptService_ParentIsTheService()
        {
            _world.Load("door", ServerHeader + "script:SetAttribute('parent', script.Parent.ClassName)");

            Assert.AreEqual("ServerScriptService", _world.Seen("door", "parent"));
        }

        // ---- SC-13 rows bound in C6 ------------------------------------------------------------------

        [Test]
        public void SC_13_LogicDefineFromAClientScript_IsContextViolation_AndFromAServerScriptWorks()
        {
            _world.ConnectLocalPlayer();

            _world.Load("hud", ClientHeader + @"
local ok, err = pcall(logic_define, 'damage', function() return 1 end)
local ok2, err2 = pcall(logic_reset, 'damage')
script:SetAttribute('define', tostring(ok) .. '|' .. tostring(ok2) .. '|' .. tostring(err))");
            _world.Load("door", ServerHeader
                                + "local ok, err = pcall(logic_define, 'armor', function() return 2 end)\n"
                                + "store_set('define', tostring(err))");

            StringAssert.StartsWith("false|false|", _world.Seen("hud", "define"));
            StringAssert.Contains("CONTEXT_VIOLATION: logic_define is server-only", _world.Seen("hud", "define"));
            StringAssert.DoesNotContain("CONTEXT_VIOLATION", _world.Get("door", "define"),
                "a server script reaches the slots; this game declares no 'armor' slot");
            StringAssert.Contains("not declared", _world.Get("door", "define"));
        }

        [Test]
        public void SC_13_EventsEmitFromADeclaredScript_ReachesOnlyRunsOnItsSide()
        {
            _world.ConnectLocalPlayer();
            _world.Load("door", ServerHeader + "hooks_on('ping', function(_, p) store_set('got', p) end)");
            _world.Load("old", "hooks_on('ping', function(_, p) store_set('got', p) end)");
            _world.Load("hud", ClientHeader + "hooks_on('ping', function(_, p) script:SetAttribute('got', p) end)");

            _world.Load("radio", ServerHeader + "events_emit('ping', 'server')");
            Frames(2);

            Assert.AreEqual("server", _world.Get("door", "got"));
            Assert.AreEqual("server", _world.Get("old", "got"), "a legacy run is on the server side");
            Assert.AreEqual("", _world.Seen("hud", "got"), "no event crosses the client-server boundary");
        }

        [Test]
        public void SC_13_EventsEmitFromALegacyMod_StillReachesEveryRun()
        {
            _world.ConnectLocalPlayer();
            _world.Load("hud", ClientHeader + "hooks_on('ping', function(_, p) script:SetAttribute('got', p) end)");

            _world.Load("radio", "events_emit('ping', 'legacy')");
            Frames(2);

            Assert.AreEqual("legacy", _world.Seen("hud", "got"), "a legacy mod keeps every pre-MVP4 rule (SC-8)");
        }

        [Test]
        public void SC_13_FindersOnGameReachingAServerContainer_FromAClientScript_AreContextViolation()
        {
            _world.ConnectLocalPlayer();
            const string probe = @"
local a = pcall(function() return game:FindFirstChild('ServerStorage') end)
local b = pcall(function() return game:FindFirstChildOfClass('ServerScriptService') end)
local c, err = pcall(function() return game:WaitForChild('ServerStorage') end)
local d = pcall(function() return game:FindFirstChild('ReplicatedStorage') end)
script:SetAttribute('find', tostring(a) .. '|' .. tostring(b) .. '|' .. tostring(c) .. '|' .. tostring(d) .. '|' .. tostring(err))";

            _world.Load("hud", ClientHeader + probe);
            _world.Load("door", ServerHeader + probe);

            StringAssert.StartsWith("false|false|false|true|", _world.Seen("hud", "find"));
            StringAssert.Contains("CONTEXT_VIOLATION: ServerStorage is server-only", _world.Seen("hud", "find"));
            StringAssert.StartsWith("true|true|true|true|", _world.Seen("door", "find"));
        }

        [Test]
        public void SC_13_APackagesRow_AddedBeforeTheFirstScript_IsChecked_AndAfterItIsRefused()
        {
            using LuaCsRbxApiBindings roblox = new();
            roblox.AddContextRule(new ScriptContextRule("UxmlService", ScriptSide.Client, "P18: screens are per client",
                "draw the screen from a client script"));

            Assert.IsNotNull(roblox.ContextRules.Check("UxmlService.CreateScreen", ScriptContext.Server));
            Assert.IsNull(roblox.ContextRules.Check("UxmlService.CreateScreen", ScriptContext.Client));
            Assert.Throws<InvalidOperationException>(() => _world.Roblox.AddContextRule(new ScriptContextRule(
                    "UxmlService", ScriptSide.Client, "P18", "draw the screen from a client script")),
                "a world whose scripts registered has a frozen table");
        }

        // ---- harness ---------------------------------------------------------------------------------

        private sealed class World : IDisposable
        {
            public World(INetworkBridge bridge)
            {
                Roblox = bridge == null
                    ? new LuaCsRbxApiBindings(log: Log.Add)
                    : new LuaCsRbxApiBindings(log: Log.Add, networkBridge: bridge);
                Store = new MemoryStore();
                Stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
                {
                    Logger = new SilentGameLogger(),
                    ModStore = Store,
                    RbxApi = Roblox,
                    LuaScriptVersions = new MemoryLuaScriptVersionStore()
                });
                Stack.Runtime.LoadMod("bootstrap", "local x = 1", LuaCapabilities.All, false);
            }

            public LuaCsRbxApiBindings Roblox { get; }

            public List<string> Log { get; } = new();

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

            /// <summary>An attribute the mod's script set on itself: how a client script reports (SC-13).</summary>
            public string Seen(string modId, string key)
            {
                return Roblox.TryGetExecutingScript(modId, out RbxInstance script)
                    ? Convert.ToString(script.GetAttribute(key), System.Globalization.CultureInfo.InvariantCulture)
                      ?? ""
                    : "";
            }

            /// <summary>Admits this screen's player, which a client script waits for (SC-9 item 6).</summary>
            public RbxPlayer ConnectLocalPlayer()
            {
                return Roblox.Players.EnsureActor(Roblox.Registry, "screen-player");
            }

            public void Dispose()
            {
                Roblox.Dispose();
            }
        }

        /// <summary>A loopback bridge that reports another topology; nothing it is given is delivered.</summary>
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
