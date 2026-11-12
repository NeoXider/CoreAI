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
            _world.ConnectLocalPlayer();

            _world.Load("door", ServerHeader + "store_set('ran', 'yes')");
            _world.Load("hud", ClientHeader + "script:SetAttribute('ran', 'yes')");

            Assert.AreEqual("", _world.Get("door", "ran"));
            Assert.AreEqual("not-on-this-side", Listed("door").RunState);
            Assert.AreEqual("yes", _world.Seen("hud", "ran"));
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

        // ---- C2: a running script follows its instance --------------------------------------------

        private const string MakesAPartAndCounts = ServerHeader + @"
local runs = (tonumber(store_get('runs')) or 0) + 1
store_set('runs', tostring(runs))
local made = Instance.new('Part')
made.Name = 'Made' .. runs
made.Parent = workspace
" + CountTicks;

        private void SetEnabledFromLua(string modId, bool enabled)
        {
            _world.Load("switch-" + modId + "-" + enabled.ToString().ToLowerInvariant(),
                "game:GetService('ServerScriptService'):FindFirstChild('" + modId + "').Enabled = "
                + enabled.ToString().ToLowerInvariant());
        }

        [Test]
        public void C2_Lua_ReadsEnabledRunContextAndTheDeprecatedDisabled()
        {
            _world.Load("door", ServerHeader + @"
store_set('read', tostring(script.Enabled) .. '|' .. tostring(script.Disabled) .. '|'
  .. tostring(script.RunContext == Enum.RunContext.Server) .. '|' .. tostring(script.RunContext))
local fired = false
script:GetPropertyChangedSignal('Enabled'):Connect(function() fired = true end)
store_set('signal', 'connected')");

            Assert.AreEqual("true|false|true|Enum.RunContext.Server", _world.Get("door", "read"));
            Assert.AreEqual("connected", _world.Get("door", "signal"),
                "Enabled is a bound property, so GetPropertyChangedSignal accepts it");
        }

        [Test]
        public void R1_10_EnabledFalseStopsTheRun_KeepsWhatItMade_AndTrueStartsAFreshRunKeepingObjects()
        {
            _world.Load("door", MakesAPartAndCounts);
            _world.Stack.Runtime.Tick(0.1d);
            Assert.AreEqual("1", _world.Get("door", "ticks"));

            SetEnabledFromLua("door", false);
            _world.Stack.Runtime.Tick(0.1d);

            Assert.AreEqual("1", _world.Get("door", "ticks"), "the stop comes first in the frame, before any handler");
            Assert.AreEqual("disabled", Listed("door").RunState);
            Assert.IsNotNull(Service("Workspace").FindFirstChild("Made1"), "instances the run made stay (R1.10)");
            Assert.IsTrue(_world.Stack.Runtime.IsLoaded("door"), "a disabled script stays loaded");

            SetEnabledFromLua("door", true);
            _world.Stack.Runtime.Tick(0.1d);

            Assert.AreEqual("2", _world.Get("door", "runs"), "enabling starts the main chunk afresh");
            Assert.IsNotNull(Service("Workspace").FindFirstChild("Made1"), "the fresh run keeps the old objects");
            Assert.IsNotNull(Service("Workspace").FindFirstChild("Made2"));
            Assert.AreEqual("running", Listed("door").RunState);
            Assert.AreEqual("2", _world.Get("door", "ticks"),
                "the start comes first in the frame, so the fresh run's handlers already run in it");
        }

        [Test]
        public void R1_10_AScriptThatDisablesItself_StopsByTheNextFrame()
        {
            _world.Load("door", ServerHeader + @"
hooks_on('tick', function()
  store_set('ticks', tostring((tonumber(store_get('ticks')) or 0) + 1))
  script.Enabled = false
end)");

            _world.Stack.Runtime.Tick(0.1d);
            _world.Stack.Runtime.Tick(0.1d);

            Assert.AreEqual("1", _world.Get("door", "ticks"));
            Assert.AreEqual("disabled", Listed("door").RunState);
        }

        [Test]
        public void SC_3_EnabledDoesNotClearQuarantine_AndAReloadByHandDoes()
        {
            _world.Dispose();
            _world = new World(RbxNetworkTopology.Solo, maxErrorsBeforeQuarantine: 1);
            const string source = ServerHeader + "hooks_on('tick', function() error('boom') end)";
            _world.Load("door", source);
            _world.Stack.Runtime.Tick(0.1d);
            Assert.AreEqual("quarantined", Listed("door").RunState);
            RbxBaseScript script = (RbxBaseScript)BoundScript("door");

            script.Enabled = false;
            _world.Stack.Runtime.Tick(0.1d);
            script.Enabled = true;
            _world.Stack.Runtime.Tick(0.1d);

            Assert.AreEqual("quarantined", Listed("door").RunState,
                "toggling Enabled never restarts a quarantined script, so it cannot reset its own streak");
            _world.Stack.Runtime.ReloadMod("door", ServerHeader + "store_set('ran', 'yes')");
            Assert.AreEqual("running", Listed("door").RunState, "twin: a reload by hand clears the quarantine");
            Assert.AreEqual("yes", _world.Get("door", "ran"));
        }

        [Test]
        public void R1_12_DetachToNilKeepsARunningScript()
        {
            _world.Load("door", ServerHeader + CountTicks);
            BoundScript("door").Parent = null;

            _world.Stack.Runtime.Tick(0.1d);
            _world.Stack.Runtime.Tick(0.1d);

            Assert.AreEqual("2", _world.Get("door", "ticks"), "a running script parented to nil keeps running (R1.12)");
            Assert.AreEqual("running", Listed("door").RunState);
        }

        [Test]
        public void R1_12_MoveIntoServerStorageStopsIt_AndMovingItBackStartsItAgain()
        {
            _world.Load("door", MakesAPartAndCounts);
            RbxLuaSourceContainer script = BoundScript("door");

            script.Parent = Service("ServerStorage");
            _world.Stack.Runtime.Tick(0.1d);

            Assert.AreEqual("", _world.Get("door", "ticks"), "stopped before its first frame");
            Assert.AreEqual("container-does-not-run", Listed("door").RunState);

            script.Parent = Service("ServerScriptService");
            _world.Stack.Runtime.Tick(0.1d);

            Assert.AreEqual("2", _world.Get("door", "runs"));
            Assert.AreEqual("running", Listed("door").RunState);
        }

        [Test]
        public void R1_11_DestroyingTheScriptsParentStopsIt_AndTheSourceListsDetached()
        {
            RbxInstance gate = Part("Gate", Service("Workspace"));
            _world.Load("door", ServerHeader + "-- @coreai parent: Workspace/Gate\n" + CountTicks);
            _world.Stack.Runtime.Tick(0.1d);

            gate.Destroy();
            _world.Stack.Runtime.Tick(0.1d);

            Assert.AreEqual("1", _world.Get("door", "ticks"), "a destroyed script stops (R1.11)");
            Assert.AreEqual("detached", Listed("door").RunState);
            Assert.IsTrue(_world.Stack.Runtime.IsLoaded("door"), "its source stays, with its versions");
        }

        [Test]
        public void R1_11_Negative_AReloadOfTheDetachedSource_PlacesANewInstanceAndRunsIt()
        {
            RbxInstance gate = Part("Gate", Service("Workspace"));
            _world.Load("door", ServerHeader + CountTicks);
            BoundScript("door").Destroy();
            _world.Stack.Runtime.Tick(0.1d);
            Assert.AreEqual("detached", Listed("door").RunState);

            _world.Stack.Runtime.ReloadMod("door", ServerHeader + "store_set('ran', script.Parent.ClassName)");

            Assert.AreEqual("ServerScriptService", _world.Get("door", "ran"));
            Assert.AreEqual("running", Listed("door").RunState);
            Assert.IsFalse(gate.IsDestroyed);
        }

        [Test]
        public void SC_3_AnEnabledToggle_WritesNoStoreEntryAndNoRevision()
        {
            CountingSourceStore sources = new();
            _world.Dispose();
            _world = new World(RbxNetworkTopology.Solo, sources: sources);
            _world.Stack.Runtime.LoadMod("door", MakesAPartAndCounts, LuaCapabilities.All, true);
            int saves = sources.Saves;
            Assert.Greater(saves, 0, "precondition: this composition persists mods");
            int revisions = _world.Stack.Runtime.ListModVersions("door").Count;

            RbxBaseScript script = (RbxBaseScript)BoundScript("door");
            script.Enabled = false;
            _world.Stack.Runtime.Tick(0.1d);
            script.Enabled = true;
            _world.Stack.Runtime.Tick(0.1d);

            Assert.AreEqual("2", _world.Get("door", "runs"), "precondition: the toggle restarted the script");
            Assert.AreEqual(saves, sources.Saves, "Enabled is tree state; it never writes the store (SC-3)");
            Assert.AreEqual(revisions, _world.Stack.Runtime.ListModVersions("door").Count);
        }

        // ---- C2: Source, RunContext writes and clones from a mod ----------------------------------

        [Test]
        public void SC_2_SourceRead_FromAHostMod_ReturnsTheCurrentRevision()
        {
            _world.Load("door", ServerHeader + "local marker = 'first'");
            _world.Stack.Runtime.ReloadMod("door", ServerHeader + "local marker = 'second'");

            _world.Load("reader", "store_set('source', game:GetService('ServerScriptService').door.Source)");

            StringAssert.Contains("'second'", _world.Get("reader", "source"), "Source is a view over the live revision");
        }

        [Test]
        public void SC_2_SourceRead_FromARestrictedMod_IsNotAuthority()
        {
            _world.Load("door", ServerHeader + "local marker = 'first'");

            _world.Stack.Runtime.LoadModForActor("reader", @"
local ok, err = pcall(function() return game:GetService('ServerScriptService').door.Source end)
store_set('read', tostring(ok) .. '|' .. tostring(err))", "alice", LuaCapabilities.All, false);

            StringAssert.StartsWith("false|", _world.Get("reader", "read"));
            StringAssert.Contains("NOT_AUTHORITY", _world.Get("reader", "read"),
                "Roblox gives Source to plugins and Open Cloud only, so an actor's script never reads another's code");
        }

        [Test]
        public void SC_2_SourceWrite_FromAHostMod_IsNotAuthority_AndChangesNothing()
        {
            _world.Load("door", ServerHeader + "local marker = 'first'");

            _world.Load("writer", @"
local ok, err = pcall(function() game:GetService('ServerScriptService').door.Source = 'print(1)' end)
store_set('write', tostring(ok) .. '|' .. tostring(err))");

            StringAssert.StartsWith("false|", _world.Get("writer", "write"));
            StringAssert.Contains("NOT_AUTHORITY", _world.Get("writer", "write"));
            StringAssert.Contains("only the console", _world.Get("writer", "write"));
            Assert.IsTrue(_world.Stack.Runtime.TryGetModSource("door", out string source));
            StringAssert.Contains("'first'", source);
        }

        [Test]
        public void SC_4_RunContextWrite_FromAMod_IsNotAuthority()
        {
            _world.Load("door", ServerHeader + "local marker = 'first'");

            _world.Load("writer", @"
local ok, err = pcall(function()
  game:GetService('ServerScriptService').door.RunContext = Enum.RunContext.Client
end)
store_set('write', tostring(ok) .. '|' .. tostring(err))");

            StringAssert.Contains("NOT_AUTHORITY", _world.Get("writer", "write"));
            Assert.AreEqual(RunContextValues.Server, ((RbxBaseScript)BoundScript("door")).RunContext);
        }

        [Test]
        public void SC_17_ACloneOfARunningScript_IsUnbound_RunsNothing_AndIsNotedOncePerMod()
        {
            RbxInstance gate = Part("Gate", Service("Workspace"));
            _world.Load("door", ServerHeader + "-- @coreai parent: Workspace/Gate\n" + CountTicks);

            _world.Load("cloner", @"
local first = workspace.Gate:Clone()
first.Parent = workspace
local second = workspace.Gate:Clone()
second.Parent = workspace
store_set('bound', tostring(first:FindFirstChild('door') ~= nil))");
            _world.Stack.Runtime.Tick(0.1d);

            Assert.AreEqual("true", _world.Get("cloner", "bound"), "the copy holds a copy of the script");
            Assert.AreEqual("1", _world.Get("door", "ticks"), "only the original runs");
            int notes = 0;
            foreach (string line in _world.Log)
            {
                if (line.Contains("a cloned Script does not run yet"))
                {
                    notes++;
                }
            }

            Assert.AreEqual(1, notes, "the cloning mod is told once (SC-17)");
            Assert.IsTrue(_world.Roblox.ScriptInstances.TryGetBoundScript("door", out RbxLuaSourceContainer bound));
            Assert.AreSame(gate, bound.Parent, "the binding stays on the original");
        }

        // ---- C3: contexts in the bindings ---------------------------------------------------------

        private const string MakeRemote = @"
local remote = Instance.new('RemoteEvent')
remote.Name = 'Ping'
remote.Parent = game:GetService('ReplicatedStorage')";

        private const string FireAllClients = @"
local ok, err = pcall(function() game:GetService('ReplicatedStorage').Ping:FireAllClients('hi') end)
script:SetAttribute('fire', tostring(ok) .. '|' .. tostring(err))";

        [Test]
        public void SC_13_DoD1_FireAllClientsFromAClientScript_IsContextViolationNamingTheRule()
        {
            _world.ConnectLocalPlayer();
            _world.Load("setup", MakeRemote);

            _world.Load("hud", ClientHeader + FireAllClients);

            string fire = _world.Seen("hud", "fire");
            StringAssert.StartsWith("false|", fire);
            StringAssert.Contains("CONTEXT_VIOLATION", fire);
            StringAssert.Contains("RemoteEvent.FireAllClients is server-only", fire);
            StringAssert.Contains("this script runs as client", fire);
            StringAssert.Contains("fix:", fire);
        }

        [Test]
        public void SC_13_DoD1_Negative_FireAllClientsFromAServerScript_Passes()
        {
            _world.Load("setup", MakeRemote);

            _world.Load("door", ServerHeader + FireAllClients);

            Assert.AreEqual("true|nil", _world.Seen("door", "fire"));
        }

        [Test]
        public void SC_14_ARestrictedAuthorsServerScript_RunsAsTheServerOnAHost()
        {
            UseTopology(RbxNetworkTopology.Host);
            _world.Load("setup", MakeRemote);

            _world.Stack.Runtime.LoadModForActor("door", ServerHeader + FireAllClients, "alice",
                LuaCapabilities.All, false);

            Assert.AreEqual("true|nil", _world.Seen("door", "fire"),
                "the rung's bug: a restricted actor's server script used to run as a client");
        }

        [Test]
        public void SC_8_Negative_ARestrictedAuthorsLegacyMod_KeepsTodaysSide_AndNotAuthority()
        {
            UseTopology(RbxNetworkTopology.Host);
            _world.Load("setup", MakeRemote);

            _world.Stack.Runtime.LoadModForActor("legacy", FireAllClients, "alice", LuaCapabilities.All, false);

            StringAssert.Contains("NOT_AUTHORITY", _world.Seen("legacy", "fire"),
                "a legacy mod's side still comes from its actor's grant");
        }

        [Test]
        public void SC_14_RunService_AnswersPerScriptInOneSoloProcess_AndALegacyModFromTheTopology()
        {
            _world.ConnectLocalPlayer();
            const string probe = @"
local rs = game:GetService('RunService')
script:SetAttribute('side', tostring(rs:IsServer()) .. '|' .. tostring(rs:IsClient()))";

            _world.Load("door", ServerHeader + probe);
            _world.Load("hud", ClientHeader + probe);
            _world.Load("legacy", probe);

            Assert.AreEqual("true|false", _world.Seen("door", "side"));
            Assert.AreEqual("false|true", _world.Seen("hud", "side"));
            Assert.AreEqual("true|false", _world.Seen("legacy", "side"), "solo answers as the server, as before");
        }

        [Test]
        public void SC_14_LocalPlayer_IsNilOnTheServer_AndThisScreensPlayerOnTheClient()
        {
            RbxPlayer local = _world.ConnectLocalPlayer();
            const string probe = @"
local p = game:GetService('Players').LocalPlayer
script:SetAttribute('local', p and p.Name or 'nil')";
            int players = _world.Roblox.Players.GetPlayers().Count;

            _world.Load("door", ServerHeader + probe);
            _world.Load("hud", ClientHeader + probe);

            Assert.AreEqual("nil", _world.Seen("door", "local"), "Roblox parity: a server script has no LocalPlayer");
            Assert.AreEqual(local.Name, _world.Seen("hud", "local"));
            Assert.AreEqual(players, _world.Roblox.Players.GetPlayers().Count,
                "the host's client script is this screen's player; it mints none (SC-15)");
        }

        [Test]
        public void SC_13_UserInputServiceFromAServerScript_IsContextViolation_AndFromAClientScriptWorks()
        {
            _world.ConnectLocalPlayer();
            const string probe = @"
local ok, err = pcall(function() return game:GetService('UserInputService'):IsKeyDown(Enum.KeyCode.W) end)
script:SetAttribute('input', tostring(ok) .. '|' .. tostring(err))";

            _world.Load("door", ServerHeader + probe);
            _world.Load("hud", ClientHeader + probe);

            StringAssert.Contains("CONTEXT_VIOLATION: UserInputService.IsKeyDown is client-only", _world.Seen("door", "input"));
            StringAssert.StartsWith("true|", _world.Seen("hud", "input"));
        }

        [Test]
        public void SC_13_ServerStorageFromAClientScript_IsContextViolation_AndFromAServerScriptWorks()
        {
            _world.ConnectLocalPlayer();
            const string probe = @"
local ok, err = pcall(function() return game:GetService('ServerStorage') end)
local ok2, err2 = pcall(function() return game.ServerStorage end)
script:SetAttribute('storage', tostring(ok) .. '|' .. tostring(ok2) .. '|' .. tostring(err))";

            _world.Load("door", ServerHeader + probe);
            _world.Load("hud", ClientHeader + probe);

            StringAssert.StartsWith("true|true|", _world.Seen("door", "storage"));
            StringAssert.StartsWith("false|false|", _world.Seen("hud", "storage"));
            StringAssert.Contains("ServerStorage is server-only", _world.Seen("hud", "storage"));
        }

        [Test]
        public void SC_13_DoD2_ModsCallAcrossTheBoundary_IsContextViolation_AndOnOneSideWorks()
        {
            _world.ConnectLocalPlayer();
            _world.Load("bank", ServerHeader + "mods_export('balance', function() return 7 end)");
            const string call = @"
local ok, result = pcall(function() return mods_call('bank', 'balance') end)
script:SetAttribute('call', tostring(ok) .. '|' .. tostring(result))";

            _world.Load("hud", ClientHeader + call);
            _world.Load("shop", ServerHeader + call);

            StringAssert.Contains("CONTEXT_VIOLATION", _world.Seen("hud", "call"));
            StringAssert.Contains("crosses the client-server boundary", _world.Seen("hud", "call"));
            Assert.AreEqual("true|7", _world.Seen("shop", "call"));
        }

        [Test]
        public void SC_13_StoreSetFromAClientScript_IsContextViolation_AndFromAServerScriptWorks()
        {
            _world.ConnectLocalPlayer();

            _world.Load("hud", ClientHeader + @"
local ok, err = pcall(function() store_set('k', 'v') end)
script:SetAttribute('store', tostring(ok) .. '|' .. tostring(err))");
            _world.Load("door", ServerHeader + "store_set('k', 'v')");

            StringAssert.Contains("CONTEXT_VIOLATION: store_set is server-only", _world.Seen("hud", "store"));
            Assert.AreEqual("v", _world.Get("door", "k"));
        }

        [Test]
        public void SC_9_6_AClientScriptWaitsForTheLocalPlayer_AndStartsWhenThePlayerExists()
        {
            _world.Load("hud", ClientHeader + "script:SetAttribute('ran', 'yes')");

            Assert.AreEqual("", _world.Seen("hud", "ran"), "no player at this screen yet");
            Assert.AreEqual("waiting-for-local-player", Listed("hud").RunState);

            _world.ConnectLocalPlayer();
            _world.Stack.Runtime.Tick(0.1d);

            Assert.AreEqual("yes", _world.Seen("hud", "ran"), "the player's arrival starts the waiting client script");
            Assert.AreEqual("running", Listed("hud").RunState);
        }

        [Test]
        public void SC_15_AReplicaMintsNoPlayer_AndAnAuthoritativeWorldDoes()
        {
            ActorContext host = CoreAI.Composition.CoreServicesInstaller.DefaultLocalHostIdentityProvider
                .GetActorContext(BuiltInAgentRoleIds.Programmer);
            using LuaCsRbxApiBindings replica = new(new InstanceRegistry(authority: RegistryAuthority.Replica));

            Assert.Throws<InvalidOperationException>(() => replica.ConnectActor(host),
                "a replica admits players only through replication");
            Assert.IsEmpty(replica.Players.GetPlayers());
            Assert.IsNotNull(_world.Roblox.ConnectActor(host), "twin: the authoritative world admits it");
        }

        [Test]
        public void C3_ARestrictedActorsSourceLoadedWithoutARun_IsUnloadedWhenThatActorDisconnects()
        {
            UseTopology(RbxNetworkTopology.DedicatedServer);
            _world.Stack.Runtime.LoadModForActor("hud", ClientHeader + "local value = 1", "alice",
                LuaCapabilities.All, false);
            Assert.AreEqual("not-on-this-side", Listed("hud").RunState);

            _world.Roblox.DisconnectActor(LuaCsRbxApiBindings.ModActorLedger.CreateAttributedContext("alice",
                _world.Roblox.Registry.WorldId));
            _world.Stack.Runtime.Tick(0.1d);

            Assert.IsFalse(_world.Stack.Runtime.IsLoaded("hud"),
                "a source registered without a run belongs to its actor like a running mod (M2-24)");
        }

        // ---- harness ---------------------------------------------------------------------------

        private sealed class World : IDisposable
        {
            public World(RbxNetworkTopology topology,
                int maxErrorsBeforeQuarantine = LuaCsModRuntime.DefaultMaxErrorsBeforeQuarantine,
                ILuaModSourceStore sources = null)
            {
                Roblox = new LuaCsRbxApiBindings(log: Log.Add, networkBridge: new TopologyBridge(topology));
                Store = new MemoryStore();
                Stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
                {
                    Logger = new SilentGameLogger(),
                    ModStore = Store,
                    ModSourceStore = sources,
                    RbxApi = Roblox,
                    LuaScriptVersions = new MemoryLuaScriptVersionStore(),
                    MaxErrorsBeforeQuarantine = maxErrorsBeforeQuarantine
                });
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

        private sealed class CountingSourceStore : ILuaModSourceStore
        {
            private readonly Dictionary<string, (string Source, LuaModManifest Manifest)> _entries =
                new(StringComparer.Ordinal);

            public int Saves { get; private set; }

            public void Save(string id, string source, LuaModManifest manifest)
            {
                Saves++;
                _entries[id] = (source, manifest);
            }

            public bool TryLoad(string id, out string source, out LuaModManifest manifest)
            {
                if (_entries.TryGetValue(id, out (string Source, LuaModManifest Manifest) entry))
                {
                    source = entry.Source;
                    manifest = entry.Manifest;
                    return true;
                }

                source = null;
                manifest = null;
                return false;
            }

            public IReadOnlyList<LuaModManifest> List()
            {
                List<LuaModManifest> manifests = new();
                foreach ((string Source, LuaModManifest Manifest) entry in _entries.Values)
                {
                    manifests.Add(entry.Manifest);
                }

                return manifests;
            }

            public void SetActive(string id, bool active)
            {
                Saves++;
                if (_entries.TryGetValue(id, out (string Source, LuaModManifest Manifest) entry))
                {
                    entry.Manifest.Active = active;
                }
            }

            public void Delete(string id)
            {
                _entries.Remove(id);
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
