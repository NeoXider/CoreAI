using System;
using System.Collections.Generic;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.Logging;
using CoreAI.Ai.LuaCs;
using CoreAI.Infrastructure.Logging;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.ScriptsLua
{
    /// <summary>
    /// MVP4 step C4 through the production mod stack: <c>require</c> (rulebook R3.1 to R3.8, decisions SC-10 to SC-12,
    /// SC-18, SC-19). A <c>context: shared</c> source is a ModuleScript that runs once per requiring script, inline on the
    /// thread that first requires it; a cycle raises CYCLIC_REQUIRE (DEV-1); a failed module stays failed for that script
    /// until its source changes; a module reload restarts the scripts that required it. Every rule has its negative twin.
    /// </summary>
    [TestFixture]
    public sealed class RequireLuaEditModeTests
    {
        private const string ServerHeader = "-- @coreai context: server\n";
        private const string ClientHeader = "-- @coreai context: client\n";
        private const string SharedHeader = "-- @coreai context: shared\n";
        private const string CountRuns = "script:SetAttribute('runs', (script:GetAttribute('runs') or 0) + 1)\n";
        private const string Util = "game:GetService('ReplicatedStorage').util";

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

        private RbxInstance Module(string name)
        {
            return _world.Roblox.Game.FindFirstChildOfClass("ReplicatedStorage").FindFirstChild(name);
        }

        private int ModuleRuns(string name)
        {
            object runs = Module(name).GetAttribute("runs");
            return runs == null ? 0 : Convert.ToInt32(runs, System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string Probe(string expression)
        {
            return "local ok, value = pcall(function() return " + expression + " end)\n"
                   + "store_set('probe', tostring(ok) .. '|' .. tostring(value))\n";
        }

        // ---- R3.1: exactly one non-nil value ------------------------------------------------------

        [Test]
        public void R3_1_ModuleMustReturnExactlyOneValue()
        {
            _world.Load("util", SharedHeader + "return 1, 2");
            _world.Load("pair", SharedHeader + "local x = 1");

            _world.Load("door", ServerHeader + Probe("require(" + Util + ")")
                                + "local ok2, err2 = pcall(function() return require(game.ReplicatedStorage.pair) end)\n"
                                + "store_set('none', tostring(err2))");

            StringAssert.StartsWith("false|", _world.Get("door", "probe"));
            StringAssert.Contains("BAD_ARGUMENT: module 'ReplicatedStorage.util' returned 2 values",
                _world.Get("door", "probe"));
            StringAssert.Contains("returned 0 values", _world.Get("door", "none"));
        }

        [Test]
        public void R3_1_ModuleReturningNil_IsRefused()
        {
            _world.Load("util", SharedHeader + "return nil");

            _world.Load("door", ServerHeader + Probe("require(" + Util + ")"));

            StringAssert.Contains("module 'ReplicatedStorage.util' returned nil", _world.Get("door", "probe"));
        }

        [Test]
        public void R3_1_Negative_AModuleReturningAFunction_IsAccepted()
        {
            _world.Load("util", SharedHeader + "return function(a) return a * 2 end");

            _world.Load("door", ServerHeader + Probe("require(" + Util + ")(21)"));

            Assert.AreEqual("true|42", _world.Get("door", "probe"));
        }

        // ---- R3.2, R3.3: one run per script, the same value -------------------------------------

        [Test]
        public void R3_2_RequireExecutesOnce()
        {
            _world.Load("util", SharedHeader + CountRuns + "return {}");

            _world.Load("door", ServerHeader + "local a = require(" + Util + ")\nlocal b = require(" + Util + ")\n"
                                + "store_set('same', tostring(rawequal(a, b)))");

            Assert.AreEqual("true", _world.Get("door", "same"));
            Assert.AreEqual(1, ModuleRuns("util"));
        }

        [Test]
        public void R3_2_AfterModuleReload_DependentRunsItAgain()
        {
            _world.Load("util", SharedHeader + "return { v = 'first' }");
            _world.Load("door", ServerHeader + "store_set('v', require(" + Util + ").v)");
            Assert.AreEqual("first", _world.Get("door", "v"));

            ModReloadReport report = _world.Stack.Runtime.ReloadMod("util", SharedHeader + "return { v = 'second' }",
                ModReloadMode.CleanStartupObjects);

            Assert.AreEqual("second", _world.Get("door", "v"), "SC-18: the dependent restarted and ran the new module");
            CollectionAssert.AreEqual(new[] { "door" }, report.RestartedDependents);
            CollectionAssert.IsEmpty(report.FailedDependents);
            StringAssert.Contains("restarted the scripts that require it: door", report.Describe());
        }

        [Test]
        public void R3_3_RequiredTableIsSharedWithinTheScript()
        {
            _world.Load("util", SharedHeader + "return { count = 0 }");

            _world.Load("door", ServerHeader + "local a = require(" + Util + ")\na.count = a.count + 1\n"
                                + "task.spawn(function() local b = require(" + Util + ")\n"
                                + "store_set('seen', tostring(b.count)) end)");

            Assert.AreEqual("1", _world.Get("door", "seen"), "another thread of the same script sees the same table");
        }

        [Test]
        public void DEV126_TwoScriptsGetTwoCopiesAndOneNote()
        {
            _world.Load("util", SharedHeader + CountRuns + "return { count = 0 }");
            const string body = "local m = require(" + Util + ")\nm.count = m.count + 1\n"
                                + "store_set('count', tostring(m.count))";

            _world.Load("door", ServerHeader + body);
            _world.Load("gate", ServerHeader + body);
            _world.Load("lamp", ServerHeader + body);

            Assert.AreEqual(3, ModuleRuns("util"), "each script runs the module in its own environment (SC-11)");
            Assert.AreEqual("1", _world.Get("gate", "count"), "the second script does not see the first one's table");
            int notes = 0;
            foreach (string line in _world.Log)
            {
                if (line.Contains("now runs separately in scripts 'door' and 'gate'"))
                {
                    notes++;
                }
            }

            Assert.AreEqual(1, notes, "one note per module, on the second script that loads it");
        }

        [Test]
        public void R3_4_ServerAndClientRequireRunTheModuleTwice()
        {
            _world.ConnectLocalPlayer();
            _world.Load("util", SharedHeader + CountRuns + "return { side = game:GetService('RunService'):IsClient() }");

            _world.Load("door", ServerHeader + "store_set('client', tostring(require(" + Util + ").side))");
            _world.Load("hud", ClientHeader + "script:SetAttribute('client', tostring(require(" + Util + ").side))");

            Assert.AreEqual(2, ModuleRuns("util"));
            Assert.AreEqual("false", _world.Get("door", "client"), "a module takes its requirer's side");
            Assert.AreEqual("true", _world.Seen("hud", "client"));
            foreach (string line in _world.Log)
            {
                StringAssert.DoesNotContain("now runs separately", line, "two sides never share a module in Roblox either");
            }
        }

        // ---- R3.5, R3.6: arguments and paths -----------------------------------------------------

        [Test]
        public void R3_5_RequireByStringPaths()
        {
            _world.Load("util", SharedHeader + "-- @coreai category: Kit\nreturn { name = 'util', helper = require('./helper') }");
            _world.Load("helper", SharedHeader + "-- @coreai category: Kit\nreturn { inner = require('@self/inner') }");
            _world.Load("inner", SharedHeader + "-- @coreai parent: ReplicatedStorage/Kit/helper\nreturn 'inner'");

            _world.Load("door", ServerHeader + "-- @coreai parent: ReplicatedStorage/Kit\n"
                                + "local a = require('./util')\n"
                                + "local b = require('../Kit/util')\n"
                                + "local c = require('@game/ReplicatedStorage/Kit/util')\n"
                                + "local d = require('./../Kit/util')\n"
                                + "store_set('paths', tostring(rawequal(a, b) and rawequal(b, c) and rawequal(c, d)) .. '|' "
                                + ".. a.helper.inner)");

            Assert.AreEqual("true|inner", _world.Get("door", "paths"),
                "./, ../, @game/ and .. reach one module; a module resolves ./ and @self/ from itself");
        }

        [Test]
        public void R3_5_RequireByAssetId_IsRefusedLoudly()
        {
            _world.Load("door", ServerHeader + Probe("require(12345)"));

            StringAssert.Contains("BAD_ARGUMENT: require(assetId) loads a published model, which CoreAI does not have",
                _world.Get("door", "probe"));
        }

        [Test]
        public void R3_5_RequireOfANonModule_NamesWhatItGot()
        {
            _world.Load("door", ServerHeader + Probe("require(workspace)") + "local ok, e = pcall(require, {})\n"
                                + "store_set('table', tostring(e))");

            StringAssert.Contains("'Workspace' is a Workspace, not a ModuleScript", _world.Get("door", "probe"));
            StringAssert.Contains("require expects a ModuleScript or a path string, got table", _world.Get("door", "table"));
        }

        [Test]
        public void R3_6_RequireMissingModuleFailsImmediately_AtTheRequiringLine()
        {
            _world.Load("door", ServerHeader + "local before = 1\n" + Probe("require('./Nope')"));

            string probe = _world.Get("door", "probe");
            StringAssert.Contains("finds no child named 'Nope' (require does not wait for a module to appear)", probe);
            StringAssert.Contains("[mod:door script:main.lua line:3]", probe,
                "the requiring line, never a line of the require bridge (SC-19)");
        }

        [Test]
        public void R3_6_RequireAfterWaitForChildSucceeds()
        {
            _world.Load("door", ServerHeader
                                + "local m = require(game:GetService('ReplicatedStorage'):WaitForChild('util'))\n"
                                + "store_set('v', m.v)");
            Assert.AreEqual("", _world.Get("door", "v"));

            _world.Load("util", SharedHeader + "return { v = 'arrived' }");
            _world.Roblox.Scheduler.Advance(0.1d);

            Assert.AreEqual("arrived", _world.Get("door", "v"));
        }

        // ---- DEV-1, SC-12: cycles and concurrent loads --------------------------------------------

        [Test]
        public void DEV1_CyclicRequireRaisesError()
        {
            _world.Load("a", SharedHeader + "return { b = require(game.ReplicatedStorage.b) }");
            _world.Load("b", SharedHeader + "return { a = require(game.ReplicatedStorage.a) }");

            _world.Load("door", ServerHeader + Probe("require(game.ReplicatedStorage.a)"));

            string probe = _world.Get("door", "probe");
            StringAssert.StartsWith("false|", probe);
            StringAssert.Contains("CYCLIC_REQUIRE: Requested module was required recursively: "
                                  + "ReplicatedStorage.a -> ReplicatedStorage.b -> ReplicatedStorage.a", probe);
            StringAssert.Contains("fix:", probe);
        }

        [Test]
        public void DEV1_ConcurrentRequireOfALoadingModuleWaits()
        {
            _world.Load("slow", SharedHeader + CountRuns + "task.wait(0.1)\nreturn { n = 1 }");

            _world.Load("door", ServerHeader + "local first\n"
                                + "task.defer(function() local m = require(game.ReplicatedStorage.slow)\n"
                                + "store_set('second', tostring(rawequal(m, first))) end)\n"
                                + "first = require(game.ReplicatedStorage.slow)\nstore_set('first', tostring(first.n))");
            Assert.AreEqual("", _world.Get("door", "second"), "the deferred thread waits for the loading thread");

            _world.Roblox.Scheduler.Advance(0.2d);

            Assert.AreEqual("1", _world.Get("door", "first"));
            Assert.AreEqual("true", _world.Get("door", "second"), "the waiting thread gets the loader's value");
            Assert.AreEqual(1, ModuleRuns("slow"));
        }

        // ---- R3.8: failures -------------------------------------------------------------------------

        [Test]
        public void R3_8_ModuleErrorPropagatesWithTheModulesLine()
        {
            _world.Load("util", SharedHeader + "local x = 1\nerror('boom')\nreturn {}");

            _world.Load("door", ServerHeader + Probe("require(" + Util + ")"));

            StringAssert.Contains("[string \"mod:util\"]:3: boom", _world.Get("door", "probe"),
                "the module's own chunk and line (SC-19)");
        }

        [Test]
        public void R3_8_SecondRequireReRaisesTheFirstError()
        {
            _world.Load("util", SharedHeader + CountRuns + "error('boom')");

            _world.Load("door", ServerHeader + "pcall(require, " + Util + ")\n" + Probe("require(" + Util + ")"));

            string probe = _world.Get("door", "probe");
            StringAssert.Contains("BAD_ARGUMENT: Requested module experienced an error while loading: "
                                  + "ReplicatedStorage.util: [string \"mod:util\"]:3: boom", probe);
            Assert.AreEqual(1, ModuleRuns("util"), "a failed module stays failed for the script; it does not run again");
        }

        [Test]
        public void Mvp4_ModuleErrors_ChargeTheRequirersStreak_AndAreLoggedInBothBuffers()
        {
            _world.Load("util", SharedHeader + "error('boom')");

            _world.Load("door", ServerHeader + "task.spawn(function() require(" + Util + ") end)");

            IReadOnlyList<LuaModHandlerError> errors = _world.Stack.Runtime.GetRecentHandlerErrors("door");
            Assert.AreEqual(1, errors.Count, "charged once, to the requiring script");
            StringAssert.Contains("boom", errors[0].Error);
            IReadOnlyList<LuaLogEntry> moduleLog = _world.Logs.Query(new LuaLogQuery { ModId = "util" });
            bool logged = false;
            foreach (LuaLogEntry entry in moduleLog)
            {
                logged |= entry.Level == LuaLogLevel.RuntimeError
                          && entry.Message.Contains("module failed while script 'door' required it")
                          && entry.Message.Contains("boom");
            }

            Assert.IsTrue(logged, "the module's own log names the failure and who required it");
            CollectionAssert.IsEmpty(_world.Stack.Runtime.GetRecentHandlerErrors("util"),
                "a module has no run of its own to charge");
        }

        // ---- the body: yields, script, globals ------------------------------------------------------

        [Test]
        public void Require_ModuleYieldingAtTopLevel_RequirerWaits()
        {
            _world.Load("util", SharedHeader + "task.wait(0.1)\nreturn { v = 'late' }");

            _world.Load("door", ServerHeader + "store_set('v', require(" + Util + ").v)");
            Assert.AreEqual("", _world.Get("door", "v"));

            _world.Roblox.Scheduler.Advance(0.2d);

            Assert.AreEqual("late", _world.Get("door", "v"));
        }

        [Test]
        public void Require_SynchronousModule_DoesNotYieldTheRequirer()
        {
            _world.Load("util", SharedHeader + "return { v = 'now' }");

            _world.Load("door", ServerHeader + "store_set('v', require(" + Util + ").v)");

            Assert.AreEqual("now", _world.Get("door", "v"), "set while the chunk loaded, before any frame");
        }

        [Test]
        public void Require_ModuleScriptGlobalIsTheModule()
        {
            _world.Load("util", SharedHeader + "return script");

            _world.Load("door", ServerHeader + "local m = require(" + Util + ")\n"
                                + "store_set('script', m.ClassName .. '|' .. tostring(m == " + Util + ") .. '|' "
                                + ".. tostring(m == script))");

            Assert.AreEqual("ModuleScript|true|false", _world.Get("door", "script"));
        }

        [Test]
        public void Require_ModuleGlobalsDoNotLeakIntoTheRequirer()
        {
            _world.Load("util", SharedHeader + "leaked = 5\nreturn { read = function() return shared_value end }");

            _world.Load("door", ServerHeader + "shared_value = 'requirer'\nlocal m = require(" + Util + ")\n"
                                + "store_set('globals', tostring(leaked) .. '|' .. tostring(m.read()))");

            Assert.AreEqual("nil|requirer", _world.Get("door", "globals"),
                "module globals stay in the module; the module still reads the script's globals");
        }

        // ---- SC-10: trust and location --------------------------------------------------------------

        [Test]
        public void Require_ForeignActorsModuleFromHostScript_IsNotAuthority()
        {
            _world.Stack.Runtime.LoadModForActor("util", SharedHeader + "return {}", "alice", LuaCapabilities.All,
                false);

            _world.Load("door", ServerHeader + Probe("require(" + Util + ")"));

            StringAssert.Contains("NOT_AUTHORITY: module 'util' belongs to actor 'alice'", _world.Get("door", "probe"));
        }

        [Test]
        public void Require_HostModuleFromRestrictedScript_Works()
        {
            _world.Load("util", SharedHeader + "return { v = 'host' }");

            _world.Stack.Runtime.LoadModForActor("door", ServerHeader + Probe("require(" + Util + ").v"), "alice",
                LuaCapabilities.All, false);

            Assert.AreEqual("true|host", _world.Get("door", "probe"));
        }

        [Test]
        public void Require_ServerOnlyModuleFromClientScript_IsContextViolation()
        {
            _world.ConnectLocalPlayer();
            _world.Load("secret", SharedHeader + "-- @coreai parent: ServerStorage\nreturn {}");

            _world.Load("hud", ClientHeader + "local ok, err = pcall(require, '@game/ServerStorage/secret')\n"
                               + "script:SetAttribute('probe', tostring(ok) .. '|' .. tostring(err))");
            _world.Load("door", ServerHeader + Probe("require('@game/ServerStorage/secret') ~= nil"));

            StringAssert.Contains("CONTEXT_VIOLATION: module 'ServerStorage.secret' is in ServerStorage",
                _world.Seen("hud", "probe"));
            Assert.AreEqual("true|true", _world.Get("door", "probe"), "a server script may require it");
        }

        [Test]
        public void Require_ReplicatedModuleFromClientScript_Works()
        {
            _world.ConnectLocalPlayer();
            _world.Load("util", SharedHeader + "return { v = 'shared' }");

            _world.Load("hud", ClientHeader + "script:SetAttribute('v', require(" + Util + ").v)");

            Assert.AreEqual("shared", _world.Seen("hud", "v"));
        }

        // ---- SC-17: unbound ModuleScripts ------------------------------------------------------------

        [Test]
        public void Clone_OfModuleScript_IsUnbound_RequireFailsNamed()
        {
            _world.Load("util", SharedHeader + "return {}");

            _world.Load("door", ServerHeader + "local copy = " + Util + ":Clone()\ncopy.Name = 'copy'\n"
                                + "copy.Parent = workspace\n" + Probe("require(workspace.copy)")
                                + "local ok, err = pcall(require, Instance.new('ModuleScript'))\n"
                                + "store_set('new', tostring(err))");

            StringAssert.Contains("BAD_ARGUMENT: ModuleScript 'Workspace.copy' has no source", _world.Get("door", "probe"));
            StringAssert.Contains("has no source", _world.Get("door", "new"));
        }

        [Test]
        public void Require_OfAnUnloadedModule_IsRefusedNamingIt()
        {
            _world.Load("util", SharedHeader + "return {}");
            Assert.IsTrue(_world.Stack.Runtime.UnloadMod("util"));

            _world.Load("door", ServerHeader + Probe("require(" + Util + ")"));

            StringAssert.Contains("ModuleScript 'ReplicatedStorage.util' is unloaded", _world.Get("door", "probe"));
        }

        // ---- SC-19: error attribution ----------------------------------------------------------------

        [Test]
        public void ErrorAttribution_ModuleErrorNamesTheModuleChunk()
        {
            // WHY not "return Instance.new(...)": that is a tail call, which replaces the module's frame (DEV-111), and
            // the error then belongs to the next mod frame, the requirer's.
            _world.Load("util", SharedHeader + "local x = 1\nlocal part = Instance.new('NoSuchClass')\nreturn part");

            _world.Load("door", ServerHeader + Probe("require(" + Util + ")"));

            StringAssert.Contains("[mod:util script:main.lua line:3]", _world.Get("door", "probe"));
        }

        [Test]
        public void ErrorAttribution_MainChunkErrorNamesTheScript()
        {
            _world.Load("door", ServerHeader + "local x = 1\n" + Probe("Instance.new('NoSuchClass')"));

            StringAssert.Contains("[mod:door script:main.lua line:3]", _world.Get("door", "probe"));
        }

        // ---- SC-18: dependents ------------------------------------------------------------------------

        [Test]
        public void Mvp4_ModuleReload_RestartsDependentsInOrder_ServerSideFirst()
        {
            _world.ConnectLocalPlayer();
            _world.Load("util", SharedHeader + "return {}");
            const string body = "require(" + Util + ")\n" + CountRuns;
            _world.Load("hud", ClientHeader + body);
            _world.Load("door", ServerHeader + body);
            _world.Load("lamp", ServerHeader + CountRuns);

            ModReloadReport report = _world.Stack.Runtime.ReloadMod("util", SharedHeader + "return { v = 2 }",
                ModReloadMode.KeepObjects);

            CollectionAssert.AreEqual(new[] { "door", "hud" }, report.RestartedDependents,
                "the server side restarts before the client side (R2.2)");
            Assert.AreEqual("2", _world.Seen("door", "runs"));
            Assert.AreEqual("2", _world.Seen("hud", "runs"));
            Assert.AreEqual("1", _world.Seen("lamp", "runs"), "a script that never required the module keeps its run");
        }

        [Test]
        public void Mvp4_ModuleUnload_RestartsNobody()
        {
            _world.Load("util", SharedHeader + "return {}");
            _world.Load("door", ServerHeader + "require(" + Util + ")\n" + CountRuns);

            Assert.IsTrue(_world.Stack.Runtime.UnloadMod("util"));

            Assert.AreEqual("1", _world.Seen("door", "runs"), "nothing new to run, so the script keeps its copy");
        }

        [Test]
        public void Mvp4_ModuleReload_ADependentThatFailsToRestart_KeepsItsOldRun()
        {
            _world.Load("util", SharedHeader + "return { ok = true }");
            _world.Load("door", ServerHeader + "assert(require(" + Util + ").ok)\n" + CountRuns);

            ModReloadReport report = _world.Stack.Runtime.ReloadMod("util", SharedHeader + "return { ok = false }",
                ModReloadMode.KeepObjects);

            CollectionAssert.AreEqual(new[] { "door" }, report.FailedDependents);
            Assert.AreEqual("1", _world.Seen("door", "runs"));
            Assert.IsTrue(_world.Stack.Runtime.IsLoaded("door"));
        }

        // ---- the console ------------------------------------------------------------------------------

        [Test]
        public void Require_FromTheConsole_RunsTheModuleInTheConsolesOwnEnvironment()
        {
            _world.Load("util", SharedHeader + CountRuns + "return { v = 'console' }");

            LuaTool.LuaResult result = _world.Stack.ToolExecutor
                .ExecuteAsync("return require(" + Util + ").v", CancellationToken.None).GetAwaiter().GetResult();

            Assert.IsTrue(result.Success, result.Error);
            StringAssert.Contains("console", result.Output);
            Assert.AreEqual(1, ModuleRuns("util"));
        }

        [Test]
        public void Require_FromTheConsole_OfAYieldingModule_IsContextViolation()
        {
            _world.Load("util", SharedHeader + "task.wait(0.1)\nreturn {}");

            LuaTool.LuaResult result = _world.Stack.ToolExecutor
                .ExecuteAsync("return require(" + Util + ")", CancellationToken.None).GetAwaiter().GetResult();

            Assert.IsFalse(result.Success);
            StringAssert.Contains("CONTEXT_VIOLATION", result.Error);
        }

        // ---- harness ---------------------------------------------------------------------------------

        private sealed class World : IDisposable
        {
            public World(RbxNetworkTopology topology)
            {
                Roblox = new LuaCsRbxApiBindings(log: Log.Add, networkBridge: new TopologyBridge(topology));
                Store = new MemoryStore();
                Stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
                {
                    Logger = new SilentGameLogger(),
                    ModStore = Store,
                    RbxApi = Roblox,
                    LogService = Logs,
                    OneOffCapabilities = LuaCapabilities.All,
                    LuaScriptVersions = new MemoryLuaScriptVersionStore()
                });
            }

            public LuaCsRbxApiBindings Roblox { get; }

            public List<string> Log { get; } = new();

            public LuaLogService Logs { get; } = new();

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
