using System;
using System.Collections.Generic;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Infrastructure.Logging;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Scripts;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.ScriptsLua
{
    /// <summary>
    /// MVP4 step C1-3 through the production mod stack: a mod that declares a context or a <c>parent:</c> gets a durable,
    /// world-owned script instance (rule R2) that exists before its main chunk runs, is its <c>script</c>, survives a
    /// clean reload and an unload, is released by a forget and undone by a failed first load; a mod that declares
    /// nothing keeps the synthetic legacy placement (rule R1). The first four tests pin the order the C1 plan calls most
    /// likely to go wrong. Every rule has its negative twin.
    /// </summary>
    [TestFixture]
    public sealed class ScriptPlacementLuaEditModeTests
    {
        private SynchronizationContext _savedContext;
        private World _world;

        [SetUp]
        public void SetUp()
        {
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            _world = new World();
        }

        [TearDown]
        public void TearDown()
        {
            _world?.Dispose();
            _world = null;
            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        private const string ServerHeader = "-- @coreai context: server\n";
        private const string ClientHeader = "-- @coreai context: client\n";

        private RbxInstance Service(string className)
        {
            return _world.Roblox.Game.FindFirstChildOfClass(className);
        }

        private RbxBaseScript BoundScript(string modId)
        {
            Assert.IsTrue(_world.Roblox.ScriptInstances.TryGetBoundScript(modId, out RbxLuaSourceContainer script),
                "mod '" + modId + "' has a bound instance");
            return (RbxBaseScript)script;
        }

        private bool IsBound(string modId)
        {
            return _world.Roblox.ScriptInstances.TryGetBoundScript(modId, out _);
        }

        private InstanceRecord Record(RbxInstance instance)
        {
            Assert.IsTrue(_world.Roblox.Registry.TryGetRecord(instance.Id, out InstanceRecord record));
            return record;
        }

        private int CountNamed(RbxInstance parent, string name)
        {
            int count = 0;
            foreach (RbxInstance child in parent.GetChildren())
            {
                if (child.Name == name)
                {
                    count++;
                }
            }

            return count;
        }

        // ---- the order the plan pins first ---------------------------------------------------

        [Test]
        public void C1_DeclaredLoad_ScriptIsTheBoundInstance_PlacedBeforeTheChunkRan()
        {
            _world.Load("door", ServerHeader + @"
store_set('view', script.ClassName .. '|' .. script.Name .. '|' .. script.Parent.ClassName .. '|'
  .. tostring(game:GetService('ServerScriptService'):FindFirstChild('door') == script))");

            Assert.AreEqual("Script|door|ServerScriptService|true", _world.Get("door", "view"),
                "the main chunk's script is the durable instance, directly in ServerScriptService");
            RbxBaseScript script = BoundScript("door");
            InstanceRecord record = Record(script);
            Assert.IsNull(record.OwnerModId, "world-owned, so never a startup object and never torn down");
            Assert.IsFalse(record.IsRuntimeInfrastructure, "saved with the world");
            Assert.AreEqual(OriginTag.FromMod("door"), record.OriginTag);
            Assert.AreEqual("door", script.SourceId);
            Assert.AreEqual(RunContextValues.Server, script.RunContext);
            Assert.AreEqual(1, CountNamed(Service("ServerScriptService"), "door"),
                "no synthetic folder was built next to it");
        }

        [Test]
        public void C1_R1_Negative_ALegacyLoad_BuildsTheSyntheticPlacementAndBindsNothing()
        {
            _world.Load("legacy", @"store_set('view', script.Parent.ClassName .. '|' .. script.Parent.Name)");

            Assert.AreEqual("Folder|legacy", _world.Get("legacy", "view"));
            Assert.IsFalse(IsBound("legacy"), "the synthetic script is runtime infrastructure, not a binding");
            RbxInstance folder = Service("ServerScriptService").FindFirstChild("legacy");
            Assert.IsTrue(Record(folder).IsRuntimeInfrastructure);
        }

        [Test]
        public void C1_CleanReload_KeepsTheBoundScript_AndRebuildsOnlyTheStartupObjects()
        {
            const string source = ServerHeader + @"
local built = Instance.new('Folder')
built.Name = 'Built'
built.Parent = workspace
local runs = (script:GetAttribute('Runs') or 0) + 1
script:SetAttribute('Runs', runs)
store_set('runs', tostring(runs))";
            _world.Load("arena", source);
            RbxBaseScript first = BoundScript("arena");

            ModReloadReport report = _world.Stack.Runtime.ReloadMod("arena", source, ModReloadMode.CleanStartupObjects);

            Assert.AreEqual("2", _world.Get("arena", "runs"), "the new run read the attribute the old run wrote");
            Assert.AreSame(first, BoundScript("arena"), "a clean reload never takes the durable script with it");
            Assert.IsFalse(first.IsDestroyed);
            Assert.AreEqual(1, report.CleanedObjects, "only the startup Folder of the previous run was cleaned");
            Assert.AreEqual(1, CountNamed(Service("Workspace"), "Built"));
        }

        [Test]
        public void C1_FailedFirstLoad_DestroysTheInstanceItCreated()
        {
            Assert.Catch(() => _world.Load("broken", ServerHeader + "-- @coreai category: Games/Arena\n" + @"
store_set('saw', script.Parent.Name .. '|' .. script.Parent.Parent.Name)
error('boom')"));

            Assert.AreEqual("Arena|Games", _world.Get("broken", "saw"), "the chunk ran inside its placement");
            Assert.IsFalse(IsBound("broken"));
            Assert.IsNull(Service("ServerScriptService").FindFirstChild("Games"),
                "the folders the failed load made are gone with its script");
            Assert.IsFalse(_world.Stack.Runtime.IsLoaded("broken"));
        }

        [Test]
        public void C1_FailedReload_KeepsTheInstance_WhereItWasAndWithItsRunContext()
        {
            _world.Load("door", ServerHeader + "store_set('run', 'server')");
            RbxBaseScript script = BoundScript("door");

            Assert.Catch(() => _world.Stack.Runtime.ReloadMod("door", ClientHeader + @"
store_set('saw', script.Parent.Name)
error('boom')"));

            Assert.AreEqual("ReplicatedStorage", _world.Get("door", "saw"),
                "the new chunk ran at the client placement it asked for");
            Assert.AreSame(script, BoundScript("door"), "a failed reload keeps the instance");
            Assert.AreSame(Service("ServerScriptService"), script.Parent, "and puts it back where it was");
            Assert.AreEqual(RunContextValues.Server, script.RunContext);
            Assert.IsTrue(_world.Stack.Runtime.IsLoaded("door"), "the old run stays loaded");
        }

        // ---- unload, load again, forget ------------------------------------------------------

        [Test]
        public void C1_Unload_KeepsADeclaredInstance()
        {
            _world.Load("door", ServerHeader + "local p = Instance.new('Part', script) p.Name = 'Hinge'");
            RbxBaseScript script = BoundScript("door");

            Assert.IsTrue(_world.Stack.Runtime.UnloadMod("door"));

            Assert.IsFalse(script.IsDestroyed, "unloading keeps the durable instance (C1 question 4)");
            Assert.IsTrue(IsBound("door"));
            Assert.IsNull(script.FindFirstChild("Hinge"), "what the mod made inside it goes with the mod");
        }

        [Test]
        public void C1_Unload_Negative_DestroysTheLegacySynthetic()
        {
            _world.Load("legacy", "store_set('ok', 'yes')");
            RbxInstance folder = Service("ServerScriptService").FindFirstChild("legacy");
            Assert.IsNotNull(folder);

            Assert.IsTrue(_world.Stack.Runtime.UnloadMod("legacy"));

            Assert.IsTrue(folder.IsDestroyed, "the synthetic placement is the mod's runtime infrastructure, as today");
        }

        [Test]
        public void C1_LoadAfterUnload_ReusesTheKeptInstance()
        {
            _world.Load("door", ServerHeader + "store_set('ok', 'yes')");
            RbxBaseScript script = BoundScript("door");
            _world.Stack.Runtime.UnloadMod("door");

            _world.Load("door", ServerHeader + "store_set('ok', 'again')");

            Assert.AreSame(script, BoundScript("door"));
        }

        [Test]
        public void C1_LoadOfADetachedSource_RecreatesItsInstance()
        {
            _world.Load("door", ServerHeader + "store_set('ok', 'yes')");
            RbxBaseScript first = BoundScript("door");
            _world.Stack.Runtime.UnloadMod("door");
            first.Destroy();
            Assert.IsFalse(IsBound("door"), "a source whose instance was destroyed is detached");

            _world.Load("door", ServerHeader + "store_set('ok', 'again')");

            RbxBaseScript second = BoundScript("door");
            Assert.AreNotSame(first, second);
            Assert.AreSame(Service("ServerScriptService"), second.Parent);
        }

        [Test]
        public void C1_Forget_DestroysADefaultPlacedChildlessInstance()
        {
            _world.Load("door", ServerHeader + "-- @coreai category: Games\nstore_set('ok', 'yes')");
            RbxBaseScript script = BoundScript("door");

            Assert.IsTrue(_world.Stack.Runtime.ForgetMod("door"));

            Assert.IsTrue(script.IsDestroyed, "C1 question 3: a default-placed, empty instance goes with its source");
            Assert.IsFalse(IsBound("door"));
        }

        [Test]
        public void C1_Forget_Negative_KeepsAMovedInstanceUnbound()
        {
            _world.Load("door", ServerHeader + "store_set('ok', 'yes')");
            RbxBaseScript script = BoundScript("door");
            RbxInstance part = _world.Roblox.Registry.Create("Part");
            part.Parent = Service("Workspace");
            script.Parent = part;

            Assert.IsTrue(_world.Stack.Runtime.ForgetMod("door"));

            Assert.IsFalse(script.IsDestroyed, "a moved instance is the world's now; only its binding ends");
            Assert.IsFalse(script.IsBound);
            Assert.AreSame(part, script.Parent);
        }

        // ---- quota ---------------------------------------------------------------------------

        [Test]
        public void C1_Quota_ABoundScriptIsNotCharged()
        {
            _world.Dispose();
            _world = new World(maxInstancesPerActor: 1);

            _world.Stack.Runtime.LoadModForActor("door", ServerHeader + @"
Instance.new('Part')
store_set('part', 'made')", "alice");

            Assert.AreEqual("made", _world.Get("door", "part"),
                "the actor's one instance is its part; the script it runs from costs nothing (rule R2)");
            Assert.AreEqual("alice", Record(BoundScript("door")).OwnerActorId);
        }

        [Test]
        public void C1_Quota_Negative_AnInstanceNewScriptIsCharged()
        {
            _world.Dispose();
            _world = new World(maxInstancesPerActor: 1);

            _world.Stack.Runtime.LoadModForActor("door", ServerHeader + @"
Instance.new('Script')
local ok, err = pcall(function() Instance.new('Part') end)
store_set('part', tostring(ok) .. '|' .. tostring(err))", "alice");

            StringAssert.StartsWith("false|", _world.Get("door", "part"));
            StringAssert.Contains("BUDGET_EXCEEDED", _world.Get("door", "part"),
                "a script the mod makes is an ordinary instance and takes the quota");
        }

        [Test]
        public void C1_Quota_ABoundScriptTheWorldAlreadyHolds_IsNotChargedWhenTheRuntimeAttaches()
        {
            _world.Dispose();
            _world = new World(maxInstancesPerActor: 1, seed: registry =>
            {
                RbxBaseScript restored = (RbxBaseScript)registry.Create("Script", null, OriginTag.FromMod("door"),
                    ownerActorId: "alice");
                restored.SourceId = "door";
            });

            _world.Stack.Runtime.LoadModForActor("other", "Instance.new('Part') store_set('part', 'made')", "alice");

            Assert.AreEqual("made", _world.Get("other", "part"));
        }

        [Test]
        public void C1_Quota_Negative_AnUnboundScriptTheWorldAlreadyHolds_IsCharged()
        {
            _world.Dispose();
            _world = new World(maxInstancesPerActor: 1, seed: registry =>
                registry.Create("Script", null, OriginTag.FromMod("door"), ownerActorId: "alice"));

            Exception failure = Assert.Catch(() =>
                _world.Stack.Runtime.LoadModForActor("other", "Instance.new('Part')", "alice"));

            StringAssert.Contains("registered instances quota", failure.Message);
        }

        // ---- the stored context (SC-5) -------------------------------------------------------

        [Test]
        public void SC_5_ARestoreStartsAHeaderlessSourceWithItsStoredContext()
        {
            _world.Dispose();
            _world = new World(sources: new MemorySourceStore().With("hud", "store_set('parent', script.Parent.Name)",
                "client"));

            Assert.AreEqual(1, _world.Stack.Runtime.RehydrateFromStore(LuaCapabilities.All));

            Assert.AreEqual("ReplicatedStorage", _world.Get("hud", "parent"));
            Assert.AreEqual(RunContextValues.Client, BoundScript("hud").RunContext);
        }

        [Test]
        public void SC_5_Negative_ARestoreOfAStoredLegacySource_KeepsTheSyntheticPlacement()
        {
            _world.Dispose();
            _world = new World(sources: new MemorySourceStore().With("hud", "store_set('parent', script.Parent.Name)",
                ""));

            Assert.AreEqual(1, _world.Stack.Runtime.RehydrateFromStore(LuaCapabilities.All));

            Assert.AreEqual("hud", _world.Get("hud", "parent"), "rule R1: the synthetic folder named after the mod");
            Assert.IsFalse(IsBound("hud"));
        }

        // ---- header refusals and placements --------------------------------------------------

        [Test]
        public void C1_Load_UnknownContextValue_FailsWithBadArgument_AndCreatesNothing()
        {
            int before = _world.Roblox.Registry.GetLiveInstances().Count;

            Exception failure = Assert.Catch(() =>
                _world.Load("typo", "-- @coreai context: clinet\nstore_set('ran', 'yes')"));

            StringAssert.Contains("BAD_ARGUMENT: @coreai context 'clinet' is not server, client or shared",
                failure.Message);
            Assert.AreEqual("", _world.Get("typo", "ran"), "the chunk never ran");
            Assert.AreEqual(before, _world.Roblox.Registry.GetLiveInstances().Count);
            Assert.IsFalse(_world.Stack.Runtime.IsLoaded("typo"));
        }

        [Test]
        public void C1_Load_DeclaredClient_CreatesAClientScriptInReplicatedStorage()
        {
            _world.Load("hud", ClientHeader + "store_set('parent', script.Parent.Name)");

            Assert.AreEqual("ReplicatedStorage", _world.Get("hud", "parent"));
            Assert.AreEqual(RunContextValues.Client, BoundScript("hud").RunContext);
        }

        [Test]
        public void C1_ClientScript_InSinglePlayer_RunsOnceOnTheOneMachine()
        {
            Assert.AreEqual(CoreAI.Mods.Rbx.Instances.Networking.RbxNetworkTopology.Solo,
                _world.Roblox.NetworkBridge.Topology, "precondition: a world without a network is single-player");

            _world.Load("hud", ClientHeader + @"
local runs = (tonumber(store_get('runs')) or 0) + 1
store_set('runs', tostring(runs))");
            _world.Roblox.Scheduler.Advance(0d);
            _world.Roblox.Scheduler.Advance(0d);

            Assert.AreEqual("1", _world.Get("hud", "runs"),
                "one machine is both sides, so the client script runs there once, like every other mod");
        }

        [Test]
        public void C1_6_Load_SharedContext_PlacesAModuleScript_AndNeverRunsIt()
        {
            _world.Load("tools", "-- @coreai context: shared\nstore_set('ran', 'yes')");

            Assert.AreEqual("", _world.Get("tools", "ran"), "a module is never run as a mod");
            Assert.IsTrue(IsBound("tools"), "step C1-6 registers it on a ModuleScript (ScriptRunGateLuaEditModeTests)");
        }

        [Test]
        public void C1_Load_UnresolvedParent_FailsNamingTheSegment()
        {
            Exception failure = Assert.Catch(() =>
                _world.Load("door", "-- @coreai parent: Workspace/Gate\nstore_set('ran', 'yes')"));

            StringAssert.Contains("BAD_ARGUMENT", failure.Message);
            StringAssert.Contains("has no child named 'Gate'", failure.Message);
            Assert.AreEqual("", _world.Get("door", "ran"));
        }

        [Test]
        public void C1_Load_ParentPath_PlacesTheScriptThere()
        {
            RbxInstance gate = _world.Roblox.Registry.Create("Part");
            gate.Name = "Gate";
            gate.Parent = Service("Workspace");

            _world.Load("door", "-- @coreai parent: Workspace/Gate\n"
                                + "store_set('parent', tostring(script.Parent == workspace.Gate))");

            Assert.AreEqual("true", _world.Get("door", "parent"), "script.Parent of a Script in a Part is the part");
            Assert.AreEqual(RunContextValues.Legacy, BoundScript("door").RunContext,
                "a parent: alone declares where, not which side");
        }

        [Test]
        public void C1_Load_CategoryFolders_AreTheDefaultPlacementPath()
        {
            _world.Load("door", ServerHeader + "-- @coreai category: Games/Arena\nstore_set('ok', 'yes')");

            RbxBaseScript script = BoundScript("door");
            Assert.AreEqual("Arena", script.Parent.Name);
            Assert.AreEqual("Games", script.Parent.Parent.Name);
            Assert.IsNull(Record(script.Parent).OwnerModId, "the folders are world-owned and saved with the script");
        }

        // ---- reloads ---------------------------------------------------------------------------

        [Test]
        public void C1_Reload_ServerToClient_KeepsTheInstance_MovesItAndSetsItsRunContext()
        {
            _world.Load("door", ServerHeader + "store_set('ok', 'yes')");
            RbxBaseScript script = BoundScript("door");

            _world.Stack.Runtime.ReloadMod("door", ClientHeader + "store_set('parent', script.Parent.Name)");

            Assert.AreSame(script, BoundScript("door"));
            Assert.AreEqual(RunContextValues.Client, script.RunContext);
            Assert.AreEqual("ReplicatedStorage", _world.Get("door", "parent"));
        }

        [Test]
        public void C1_Reload_Negative_AScriptSomeoneMoved_StaysWhereItIs()
        {
            _world.Load("door", ServerHeader + "store_set('ok', 'yes')");
            RbxBaseScript script = BoundScript("door");
            RbxInstance part = _world.Roblox.Registry.Create("Part");
            part.Parent = Service("Workspace");
            script.Parent = part;

            _world.Stack.Runtime.ReloadMod("door", ClientHeader + "store_set('ok', 'again')");

            Assert.AreSame(part, script.Parent);
            Assert.AreEqual(RunContextValues.Client, script.RunContext);
        }

        [Test]
        public void C1_Reload_WithoutAContextKey_KeepsTheContextOfTheBoundInstance()
        {
            _world.Load("door", ClientHeader + "store_set('ok', 'yes')");
            RbxBaseScript script = BoundScript("door");

            _world.Stack.Runtime.ReloadMod("door", "store_set('parent', script.Parent.Name)");

            Assert.AreEqual("ReplicatedStorage", _world.Get("door", "parent"),
                "SC-5: without a header context the bound instance decides, so the script stays a client script");
            Assert.AreEqual(RunContextValues.Client, script.RunContext);
            Assert.IsNull(Service("ServerScriptService").FindFirstChild("door"), "no synthetic placement appears");
        }

        [Test]
        public void C1_6_Reload_ToShared_ReplacesTheScriptWithAModuleScript_AndRunsNothing()
        {
            _world.Load("door", ServerHeader + "store_set('run', 'first')");
            RbxBaseScript script = BoundScript("door");

            _world.Stack.Runtime.ReloadMod("door", "-- @coreai context: shared\nstore_set('run', 'module')");

            Assert.AreEqual("first", _world.Get("door", "run"), "the module's chunk never ran");
            Assert.IsTrue(script.IsDestroyed, "SC-4: the class changed, so a ModuleScript took the Script's place");
            Assert.IsTrue(_world.Roblox.ScriptInstances.TryGetBoundScript("door", out RbxLuaSourceContainer module));
            Assert.AreEqual(ScriptClassNames.ModuleScript, module.ClassName);
        }

        [TestCase(ModReloadMode.KeepObjects)]
        [TestCase(ModReloadMode.CleanStartupObjects)]
        public void C1_Reload_FromLegacyToDeclared_HandsTheSyntheticFolderToTheBoundScript(ModReloadMode mode)
        {
            _world.Load("door", @"
local made = Instance.new('Folder')
made.Name = 'Startup'
made.Parent = script.Parent");
            RbxInstance legacyFolder = Service("ServerScriptService").FindFirstChild("door");
            RbxInstance startup = legacyFolder.FindFirstChild("Startup");
            RbxInstance players = _world.Roblox.Registry.Create("Part");
            players.Name = "PlayersBuild";
            players.Parent = legacyFolder;

            ModReloadReport report = _world.Stack.Runtime.ReloadMod("door",
                ServerHeader + "store_set('parent', script.Parent.Name .. '|' .. script.ClassName)", mode);

            RbxBaseScript script = BoundScript("door");
            Assert.AreEqual("ServerScriptService|Script", _world.Get("door", "parent"),
                "the new run's script is the durable instance");
            Assert.IsTrue(legacyFolder.IsDestroyed, "the synthetic placement is retired once the reload succeeded");
            Assert.AreSame(script, players.Parent, "what someone else put in the folder moves into the script");
            if (mode == ModReloadMode.KeepObjects)
            {
                Assert.AreSame(script, startup.Parent, "keep mode keeps the mod's objects, now inside its script");
                Assert.AreEqual(1, report.KeptObjects);
            }
            else
            {
                Assert.IsTrue(startup.IsDestroyed, "a clean reload removes the previous run's startup objects");
            }
        }

        [Test]
        public void C1_Reload_FromLegacyToDeclared_ThatFails_KeepsTheSyntheticPlacement()
        {
            _world.Load("door", "store_set('run', 'legacy')");
            RbxInstance legacyFolder = Service("ServerScriptService").FindFirstChild("door");

            Assert.Catch(() => _world.Stack.Runtime.ReloadMod("door", ServerHeader + "error('boom')"));

            Assert.IsFalse(legacyFolder.IsDestroyed);
            Assert.IsFalse(IsBound("door"), "the durable instance the failed reload made is gone again");
            Assert.AreEqual(1, CountNamed(Service("ServerScriptService"), "door"));
        }

        [Test]
        public void C1_Revert_ToARevisionWithAnotherContext_ReappliesIt()
        {
            _world.Load("door", ServerHeader + "store_set('run', '0')");
            RbxBaseScript script = BoundScript("door");
            _world.Stack.Runtime.ReloadMod("door", ClientHeader + "store_set('run', '1')");
            Assert.AreEqual(RunContextValues.Client, script.RunContext);

            Assert.IsTrue(_world.Stack.Runtime.TryRevertMod("door", 0, out string _));

            Assert.AreEqual("0", _world.Get("door", "run"));
            Assert.AreSame(script, BoundScript("door"));
            Assert.AreEqual(RunContextValues.Server, script.RunContext, "the reverted header's context comes back");
            Assert.AreSame(Service("ServerScriptService"), script.Parent);
        }

        [Test]
        public void C1_Retitle_MovesADefaultPlacedScriptToItsNewFolder()
        {
            const string source = "--[[@coreai\nid: door\ncontext: server\ncategory: Old\n]]\nstore_set('ok', 'yes')\n";
            LocalActorIdentityProvider identity = new("placement-tests");
            ActorContext actor = identity.GetActorContext(BuiltInAgentRoleIds.Programmer);
            _world.Stack.Runtime.LoadMod(actor, "door", source);
            RbxBaseScript script = BoundScript("door");
            Assert.AreEqual("Old", script.Parent.Name);

            Assert.IsTrue(_world.Stack.Runtime.TryRetitleModSource(actor, "door", source.Replace("category: Old",
                "category: New")));

            Assert.AreEqual("New", script.Parent.Name);
            Assert.AreSame(Service("ServerScriptService"), script.Parent.Parent);
        }

        // ---- C1-5: what a listing reports -----------------------------------------------------

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

        [Test]
        public void C1_5_ListMods_ReportsADeclaredModsContextScriptAndRunState()
        {
            _world.Load("door", ServerHeader + "-- @coreai category: Games\nstore_set('ok', 'yes')");

            LuaModInfo door = Listed("door");

            Assert.AreEqual("server", door.Context);
            Assert.AreEqual("Script", door.ScriptClassName);
            Assert.AreEqual("ServerScriptService.Games.door", door.ScriptPath);
            Assert.AreEqual("running", door.RunState);
        }

        [Test]
        public void C1_5_ListMods_Negative_ALegacyModReportsItsSyntheticScript()
        {
            _world.Load("legacy", "store_set('ok', 'yes')");

            LuaModInfo legacy = Listed("legacy");

            Assert.AreEqual("legacy", legacy.Context);
            Assert.AreEqual("Script", legacy.ScriptClassName);
            Assert.AreEqual("ServerScriptService.legacy.legacy", legacy.ScriptPath);
            Assert.AreEqual("running", legacy.RunState);
        }

        [Test]
        public void C1_5_ListMods_AQuarantinedModReportsQuarantined()
        {
            _world.Dispose();
            _world = new World(maxErrorsBeforeQuarantine: 1);
            _world.Load("door", ServerHeader + "hooks_on('tick', function() error('boom') end)");

            _world.Stack.Runtime.Tick(0.1d);

            Assert.AreEqual("quarantined", Listed("door").RunState);
        }

        // ---- harness ---------------------------------------------------------------------------

        private sealed class World : IDisposable
        {
            public World(int maxInstancesPerActor = LuaCsModRuntime.DefaultMaxRegisteredInstancesPerActor,
                Action<InstanceRegistry> seed = null, ILuaModSourceStore sources = null,
                int maxErrorsBeforeQuarantine = LuaCsModRuntime.DefaultMaxErrorsBeforeQuarantine)
            {
                Roblox = new LuaCsRbxApiBindings();
                seed?.Invoke(Roblox.Registry);
                Store = new MemoryStore();
                Stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
                {
                    Logger = new SilentGameLogger(),
                    ModStore = Store,
                    ModSourceStore = sources,
                    RbxApi = Roblox,
                    LuaScriptVersions = new MemoryLuaScriptVersionStore(),
                    MaxRegisteredInstancesPerActor = maxInstancesPerActor,
                    MaxErrorsBeforeQuarantine = maxErrorsBeforeQuarantine
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

        private sealed class MemorySourceStore : ILuaModSourceStore
        {
            private readonly Dictionary<string, (string Source, LuaModManifest Manifest)> _entries =
                new(StringComparer.Ordinal);

            public MemorySourceStore With(string id, string source, string context)
            {
                _entries[id] = (source, new LuaModManifest
                {
                    Id = id, Active = true, Context = context, Capabilities = LuaCapabilities.All.ToString()
                });
                return this;
            }

            public void Save(string id, string source, LuaModManifest manifest)
            {
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
