using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Logging;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Scripts;
using CoreAI.Mods.WorldPackages;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.ScriptsLua
{
    /// <summary>
    /// MVP4 step C1-4 through the production session controller: a world package's format decides how its mods start
    /// (a version 1 world starts every active mod, a version 2 world leaves a declared source without its instance
    /// detached, rule R3), a declared mod's saved instance is the one it runs from after a load, a version 1 world
    /// with a declared mod migrates to its placement, and a gated load of a declared mod undoes together with its
    /// source without making later undos refuse. Every rule has its negative twin.
    /// </summary>
    [TestFixture]
    public sealed class ScriptSessionEditModeTests
    {
        private const string ServerHeader = "-- @coreai context: server\n";
        private const string BuildTower =
            "local p = Instance.new('Part') p.Name = 'Tower' p.Anchored = true p.Parent = workspace return true";

        private SynchronizationContext _savedContext;

        [SetUp]
        public void SetUp()
        {
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void TearDown()
        {
            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        private static ActorContext Actor(LocalActorIdentityProvider identity)
        {
            return identity.GetActorContext(BuiltInAgentRoleIds.Programmer);
        }

        private static void Load(P3HeadlessSession session, ActorContext actor, string id, string source)
        {
            session.Controller.Runtime.LoadMod(actor, id, source, P3Fixtures.SessionCapabilities);
        }

        private static bool TryBound(P3HeadlessSession session, string id, out RbxLuaSourceContainer script)
        {
            return session.Controller.CurrentRbxApi.ScriptInstances.TryGetBoundScript(id, out script);
        }

        private static int CountNamed(RbxInstance parent, string name)
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

        private static List<string> Shape(RbxWorldPackagePayload payload)
        {
            List<string> shape = new();
            foreach (InstanceSnapshot node in payload.Tree.Instances)
            {
                shape.Add(node.Id + "|" + node.ParentId + "|" + node.ClassName + "|" + node.Name);
            }

            return shape;
        }

        private static RbxInstance ServerScripts(P3HeadlessSession session)
        {
            return session.Controller.CurrentRbxApi.Game.FindFirstChildOfClass("ServerScriptService");
        }

        private static string RunStateOf(P3HeadlessSession session, ActorContext actor, string id)
        {
            foreach (LuaModInfo info in session.Controller.Runtime.ListMods(actor))
            {
                if (info.Id == id)
                {
                    return info.RunState;
                }
            }

            return "not listed";
        }

        // ---- R3: the package's format decides how its mods start ------------------------------

        [Test]
        public async Task Mvp4_PackageV1WithoutContexts_LoadsAsLegacyScripts_GoldenTree()
        {
            using P3HeadlessSession session = new(new P3MemoryPackageStore(), new P3MemorySourceStore());
            ActorContext actor = Actor(new LocalActorIdentityProvider("legacy-author"));
            Load(session, actor, "alpha", "local f = Instance.new('Folder') f.Name = 'AlphaStart' f.Parent = workspace");
            Load(session, actor, "beta", "local f = Instance.new('Folder') f.Name = 'BetaStart' f.Parent = workspace");
            RbxWorldPackagePayload saved = session.Controller.CaptureCurrent();
            Assert.AreEqual(1, saved.FormatVersion);

            RbxWorldLoadResult loaded = await session.Controller.LoadConfirmedAsync(saved);

            Assert.IsTrue(loaded.Success, loaded.Error);
            Assert.AreEqual(2, loaded.ActiveModsStarted);
            CollectionAssert.IsEmpty(loaded.NotStartedMods);
            Assert.IsNotNull(ServerScripts(session).FindFirstChild("alpha"), "the synthetic legacy placement, as today");
            Assert.IsFalse(TryBound(session, "alpha", out _));
            Assert.AreEqual(1, CountNamed(session.Controller.CurrentRbxApi.Registry.WorldRoot, "AlphaStart"));
            RbxWorldPackagePayload resaved = session.Controller.CaptureCurrent();
            Assert.AreEqual(1, resaved.FormatVersion);
            CollectionAssert.AreEqual(Shape(saved), Shape(resaved),
                "the loaded world saves back the same instances, and no synthetic script among them");
            CollectionAssert.IsEmpty(session.Diagnostics);
        }

        [Test]
        public async Task Mvp4_PackageV2_SourceWithoutBoundInstance_IsDetached()
        {
            using P3HeadlessSession session = new(new P3MemoryPackageStore(), new P3MemorySourceStore());
            ActorContext actor = Actor(new LocalActorIdentityProvider("door-author"));
            Load(session, actor, "alpha", "local f = Instance.new('Folder') f.Name = 'AlphaStart' f.Parent = workspace");
            Load(session, actor, "door", ServerHeader + "local f = Instance.new('Folder') f.Name = 'DoorStart' f.Parent = workspace");
            Assert.IsTrue(TryBound(session, "door", out RbxLuaSourceContainer script));
            script.Destroy();
            RbxWorldPackagePayload saved = session.Controller.CaptureCurrent();
            Assert.AreEqual(2, saved.FormatVersion, "a declared mod makes the world version 2");

            RbxWorldLoadResult loaded = await session.Controller.LoadConfirmedAsync(saved);

            Assert.IsTrue(loaded.Success, loaded.Error);
            Assert.AreEqual(1, loaded.ActiveModsStarted, "only the legacy mod starts");
            CollectionAssert.AreEqual(new[] { "door (detached)" }, loaded.NotStartedMods);
            Assert.IsTrue(session.Controller.Runtime.IsLoaded(actor, "door"),
                "step C1-6: a detached source is registered without a run, so it is listed and can be reloaded");
            Assert.AreEqual("detached", RunStateOf(session, actor, "door"));
            Assert.IsFalse(TryBound(session, "door", out _));
            Assert.IsTrue(session.Controller.Runtime.IsLoaded(actor, "alpha"),
                "twin: a legacy source without an instance starts at the synthetic placement (R3)");
            Assert.AreEqual("running", RunStateOf(session, actor, "alpha"));
            Assert.AreEqual(0, CountNamed(session.Controller.CurrentRbxApi.Registry.WorldRoot, "DoorStart"));
            Assert.IsTrue(session.Controller.SourceStore.TryLoad("door", out _, out LuaModManifest manifest),
                "a detached source stays in the world with its versions");
            Assert.IsTrue(manifest.Active);
            StringAssert.Contains("door (detached)", string.Join("|", session.Diagnostics));
        }

        [Test]
        public async Task Mvp4_PackageV2_ABoundSource_StartsFromItsSavedInstance()
        {
            using P3HeadlessSession session = new(new P3MemoryPackageStore(), new P3MemorySourceStore());
            ActorContext actor = Actor(new LocalActorIdentityProvider("door-author"));
            Load(session, actor, "door", ServerHeader + "local f = Instance.new('Folder') f.Name = 'DoorStart' f.Parent = workspace");
            Assert.IsTrue(TryBound(session, "door", out RbxLuaSourceContainer saved));
            InstanceId savedId = saved.Id;
            RbxWorldPackagePayload payload = session.Controller.CaptureCurrent();

            RbxWorldLoadResult loaded = await session.Controller.LoadConfirmedAsync(payload);

            Assert.IsTrue(loaded.Success, loaded.Error);
            Assert.AreEqual(1, loaded.ActiveModsStarted);
            Assert.IsTrue(TryBound(session, "door", out RbxLuaSourceContainer restored));
            Assert.AreEqual(savedId, restored.Id, "the mod runs from the instance the world saved");
            Assert.AreEqual(1, CountNamed(ServerScripts(session), "door"), "and no second one is made");
            Assert.AreEqual(1, CountNamed(session.Controller.CurrentRbxApi.Registry.WorldRoot, "DoorStart"));
        }

        [Test]
        public async Task Mvp4_PackageV1WithADeclaredMod_MigratesToItsPlacement_ReusingTheSavedNode()
        {
            using P3HeadlessSession session = new(new P3MemoryPackageStore(), new P3MemorySourceStore());
            ActorContext actor = Actor(new LocalActorIdentityProvider("door-author"));
            Load(session, actor, "door", ServerHeader + "local f = Instance.new('Folder') f.Name = 'DoorStart' f.Parent = workspace");
            Assert.IsTrue(TryBound(session, "door", out RbxLuaSourceContainer saved));
            LuaCsRbxApiBindings api = session.Controller.CurrentRbxApi;
            RbxWorldPackagePayload version1 = RbxWorldPackageSerializer.Capture(
                new RbxWorldPackageCaptureContext(api.Registry, api.Game, api.PartSink, P3Fixtures.NewSettings(),
                    api.CameraRig, session.Controller.SourceStore, P3Fixtures.CapturedAtUtc),
                RbxWorldPackageSerializer.LegacyScriptPlacementFormatVersion);

            RbxWorldLoadResult loaded = await session.Controller.LoadConfirmedAsync(version1);

            Assert.IsTrue(loaded.Success, loaded.Error);
            Assert.AreEqual(1, loaded.ActiveModsStarted, "in version 1 every active mod starts (C1 question 8)");
            Assert.IsTrue(TryBound(session, "door", out RbxLuaSourceContainer migrated));
            Assert.AreEqual(saved.Id, migrated.Id, "the unbound node version 1 kept is bound again");
            Assert.AreEqual(1, CountNamed(ServerScripts(session), "door"));
            Assert.AreEqual(RunContextValues.Server, ((RbxBaseScript)migrated).RunContext);
        }

        // ---- C1-6: sources registered without a run ---------------------------------------------

        [Test]
        public async Task C1_6_PackageV2_AModuleAndADisabledScript_AreRegisteredOnTheirSavedInstances_NotStarted()
        {
            using P3HeadlessSession session = new(new P3MemoryPackageStore(), new P3MemorySourceStore());
            ActorContext actor = Actor(new LocalActorIdentityProvider("kit-author"));
            Load(session, actor, "tools", "-- @coreai context: shared\nreturn {}");
            Load(session, actor, "door", ServerHeader + "local f = Instance.new('Folder') f.Name = 'DoorStart' f.Parent = workspace");
            Assert.IsTrue(TryBound(session, "tools", out RbxLuaSourceContainer module));
            Assert.IsTrue(TryBound(session, "door", out RbxLuaSourceContainer door));
            ((RbxBaseScript)door).Enabled = false;
            RbxWorldPackagePayload saved = session.Controller.CaptureCurrent();

            RbxWorldLoadResult loaded = await session.Controller.LoadConfirmedAsync(saved);

            Assert.IsTrue(loaded.Success, loaded.Error);
            Assert.AreEqual(0, loaded.ActiveModsStarted);
            CollectionAssert.AreEquivalent(new[] { "tools (module)", "door (disabled)" }, loaded.NotStartedMods);
            Assert.AreEqual("module", RunStateOf(session, actor, "tools"));
            Assert.AreEqual("disabled", RunStateOf(session, actor, "door"));
            Assert.IsTrue(TryBound(session, "tools", out RbxLuaSourceContainer restoredModule));
            Assert.AreEqual(module.Id, restoredModule.Id, "the module is registered on the instance the world saved");
            Assert.AreEqual(0, CountNamed(session.Controller.CurrentRbxApi.Registry.WorldRoot, "DoorStart"),
                "the disabled script's main chunk did not run, so nothing rebuilt its startup folder");
        }

        [Test]
        public async Task C1_6_PackageV2_Negative_TheEnabledScriptBesideAModule_Starts()
        {
            using P3HeadlessSession session = new(new P3MemoryPackageStore(), new P3MemorySourceStore());
            ActorContext actor = Actor(new LocalActorIdentityProvider("kit-author"));
            Load(session, actor, "tools", "-- @coreai context: shared\nreturn {}");
            Load(session, actor, "door", ServerHeader + "local value = 1");
            RbxWorldPackagePayload saved = session.Controller.CaptureCurrent();

            RbxWorldLoadResult loaded = await session.Controller.LoadConfirmedAsync(saved);

            Assert.IsTrue(loaded.Success, loaded.Error);
            Assert.AreEqual(1, loaded.ActiveModsStarted);
            CollectionAssert.AreEqual(new[] { "tools (module)" }, loaded.NotStartedMods);
            Assert.AreEqual("running", RunStateOf(session, actor, "door"));
        }

        [Test]
        public async Task C1_6_ReloadOfADetachedSource_PlacesANewInstance_AndRunsIt()
        {
            using P3HeadlessSession session = new(new P3MemoryPackageStore(), new P3MemorySourceStore());
            ActorContext actor = Actor(new LocalActorIdentityProvider("door-author"));
            const string source = ServerHeader + "local f = Instance.new('Folder') f.Name = 'DoorStart' f.Parent = workspace";
            Load(session, actor, "door", source);
            Assert.IsTrue(TryBound(session, "door", out RbxLuaSourceContainer script));
            script.Destroy();
            RbxWorldLoadResult loaded = await session.Controller.LoadConfirmedAsync(session.Controller.CaptureCurrent());
            Assert.IsTrue(loaded.Success, loaded.Error);
            Assert.AreEqual("detached", RunStateOf(session, actor, "door"));

            session.Controller.Runtime.ReloadMod(actor, "door", source);

            Assert.AreEqual("running", RunStateOf(session, actor, "door"));
            Assert.IsTrue(TryBound(session, "door", out RbxLuaSourceContainer placed));
            Assert.AreSame(ServerScripts(session), placed.Parent, "a load of a detached source re-creates its instance");
            Assert.AreEqual(1, CountNamed(session.Controller.CurrentRbxApi.Registry.WorldRoot, "DoorStart"));
        }

        // ---- C2: Source and RunContext from the console (execute_lua) -------------------------------

        private static ActorContext Host()
        {
            return CoreAI.Composition.CoreServicesInstaller.DefaultLocalHostIdentityProvider
                .GetActorContext(BuiltInAgentRoleIds.Programmer);
        }

        [Test]
        public async Task SC_2_SourceRead_FromTheElevatedConsole_ReturnsTheCurrentRevision()
        {
            using P3HeadlessSession session = new(new P3MemoryPackageStore(), new P3MemorySourceStore());
            ActorContext host = Host();
            Load(session, host, "door", ServerHeader + "local marker = 'v1'");

            LuaTool.LuaResult read = await session.ExecuteAsAsync(host,
                "print(game:GetService('ServerScriptService').door.Source)");

            Assert.IsTrue(read.Success, read.Error);
            StringAssert.Contains("local marker = 'v1'", read.Printed);
        }

        [Test]
        public async Task SC_2_SourceRead_FromARestrictedConsole_IsNotAuthority()
        {
            using P3HeadlessSession session = new(new P3MemoryPackageStore(), new P3MemorySourceStore());
            Load(session, Host(), "door", ServerHeader + "local marker = 'v1'");
            ActorContext visitor = Actor(new LocalActorIdentityProvider("visitor"));

            LuaTool.LuaResult read = await session.ExecuteAsAsync(visitor,
                "print(game:GetService('ServerScriptService').door.Source)");

            Assert.IsFalse(read.Success);
            StringAssert.Contains("NOT_AUTHORITY", read.Error);
        }

        [Test]
        public async Task SC_2_SourceWrite_FromTheConsole_ReloadsTheBoundScript_AsANewRevision()
        {
            using P3HeadlessSession session = new(new P3MemoryPackageStore(), new P3MemorySourceStore());
            ActorContext host = Host();
            Load(session, host, "door", ServerHeader + "local marker = 'v1'");
            Assert.IsTrue(TryBound(session, "door", out RbxLuaSourceContainer script));

            LuaTool.LuaResult written = await session.ExecuteAsAsync(host,
                "game:GetService('ServerScriptService').door.Source = "
                + "'-- @coreai context: server\\nlocal f = Instance.new(\"Folder\") f.Name = \"Rewritten\" f.Parent = workspace'");

            Assert.IsTrue(written.Success, written.Error);
            Assert.IsNotNull(session.Controller.CurrentRbxApi.Registry.WorldRoot.FindFirstChild("Rewritten"),
                "the write is a reload: the new text ran");
            Assert.IsTrue(TryBound(session, "door", out RbxLuaSourceContainer after));
            Assert.AreSame(script, after, "the instance is the same; only its source changed");
            Assert.IsTrue(session.Controller.SourceStore.TryLoad("door", out string stored, out _));
            StringAssert.Contains("Rewritten", stored);
            await session.Gate.WaitForPendingBackupAsync();
        }

        [Test]
        public async Task SC_4_RunContextWrite_FromTheConsole_RestartsUnderTheNewContext_AndTheManifestFollows()
        {
            using P3HeadlessSession session = new(new P3MemoryPackageStore(), new P3MemorySourceStore());
            ActorContext host = Host();
            Assert.IsTrue((await session.ExecuteAsAsync(host,
                "local g = Instance.new('Part') g.Name = 'Gate' g.Anchored = true g.Parent = workspace")).Success);
            Load(session, host, "door",
                "-- @coreai parent: Workspace/Gate\nscript:SetAttribute('ctx', tostring(script.RunContext))");
            Assert.IsTrue(TryBound(session, "door", out RbxLuaSourceContainer script));
            LuaCsRbxApiBindings api = session.Controller.CurrentRbxApi;
            api.Players.EnsureActor(api.Registry, "screen-player");

            LuaTool.LuaResult written = await session.ExecuteAsAsync(host,
                "workspace.Gate.door.RunContext = Enum.RunContext.Client");

            Assert.IsTrue(written.Success, written.Error);
            Assert.AreEqual(RunContextValues.Client, ((RbxBaseScript)script).RunContext);
            Assert.AreEqual("running", RunStateOf(session, host, "door"), "a Client script runs in Workspace");
            Assert.AreEqual("Enum.RunContext.Client", script.GetAttribute("ctx"), "the restart ran under the new context");
            Assert.IsTrue(session.Controller.SourceStore.TryLoad("door", out _, out LuaModManifest manifest));
            Assert.AreEqual("client", manifest.Context, "the stored manifest agrees with the instance");
            RbxWorldPackagePayload saved = session.Controller.CaptureCurrent();
            Assert.AreEqual(2, saved.FormatVersion, "and the world still saves");
            await session.Gate.WaitForPendingBackupAsync();
        }

        [Test]
        public async Task SC_4_RunContextWrite_OnAScriptWhoseHeaderDeclaresAContext_IsRefused()
        {
            using P3HeadlessSession session = new(new P3MemoryPackageStore(), new P3MemorySourceStore());
            ActorContext host = Host();
            Load(session, host, "door", ServerHeader + "local marker = 'v1'");

            LuaTool.LuaResult written = await session.ExecuteAsAsync(host,
                "game:GetService('ServerScriptService').door.RunContext = Enum.RunContext.Client");

            Assert.IsFalse(written.Success);
            StringAssert.Contains("set by its @coreai header", written.Error);
            Assert.IsTrue(TryBound(session, "door", out RbxLuaSourceContainer script));
            Assert.AreEqual(RunContextValues.Server, ((RbxBaseScript)script).RunContext);
        }

        // ---- C1-5: manage_mods list -------------------------------------------------------------

        [Test]
        public async Task C1_5_ManageModsList_ReportsContextScriptPathAndRunState_ForDeclaredAndLegacyMods()
        {
            using P3HeadlessSession session = new(new P3MemoryPackageStore(), new P3MemorySourceStore());
            LocalActorIdentityProvider identity = new("lister");
            ActorContext actor = Actor(identity);
            Load(session, actor, "door", ServerHeader + "local value = 1");
            Load(session, actor, "legacy", "local value = 1");
            LuaModsLlmTool tool = new(session.Controller.Runtime, new CoreAISettingsOptions(), NullLog.Instance,
                P3Fixtures.SessionCapabilities, true, identity, BuiltInAgentRoleIds.Programmer, session.Gate);

            JObject listed = JObject.Parse(await tool.ExecuteAsync("list"));

            Assert.IsTrue(listed.Value<bool>("success"), listed.ToString());
            Dictionary<string, JObject> byId = new();
            foreach (JToken item in (JArray)listed["data"])
            {
                byId[(string)item["id"]] = (JObject)item;
            }

            Assert.AreEqual("server", (string)byId["door"]["context"]);
            Assert.AreEqual("ServerScriptService.door", (string)byId["door"]["script_path"]);
            Assert.AreEqual("running", (string)byId["door"]["run_state"]);
            Assert.AreEqual("legacy", (string)byId["legacy"]["context"], "twin: a mod that declares nothing is legacy");
            Assert.AreEqual("ServerScriptService.legacy.legacy", (string)byId["legacy"]["script_path"]);
            Assert.AreEqual("running", (string)byId["legacy"]["run_state"]);
        }

        // ---- undo -------------------------------------------------------------------------------

        [Test]
        public async Task Undo_AGatedLoadOfADeclaredMod_TakesItsScriptInstanceBackWithItsSource()
        {
            using P3HeadlessSession session = new(new P3MemoryPackageStore(), new P3MemorySourceStore());
            LocalActorIdentityProvider identity = new("mod-builder");
            ActorContext builder = Actor(identity);
            LuaModsLlmTool tool = new(session.Controller.Runtime, new CoreAISettingsOptions(), NullLog.Instance,
                P3Fixtures.SessionCapabilities, true, identity, BuiltInAgentRoleIds.Programmer, session.Gate);

            JObject loaded = JObject.Parse(await tool.ExecuteAsync("load", "door", ServerHeader + "local value = 1"));
            Assert.IsTrue(loaded.Value<bool>("success"), loaded.ToString());
            Assert.IsTrue(TryBound(session, "door", out _));

            RbxWorldUndoResult undone = await session.Undo.UndoAsync(builder);

            Assert.IsTrue(undone.Success, undone.Message);
            Assert.IsFalse(session.Controller.SourceStore.TryLoad("door", out _, out _));
            Assert.IsFalse(session.Controller.Runtime.IsLoaded(builder, "door"));
            Assert.IsFalse(TryBound(session, "door", out _), "the script instance goes with the load it came with");
            Assert.IsNull(ServerScripts(session).FindFirstChild("door"));
            await session.Gate.WaitForPendingBackupAsync();
        }

        [Test]
        public async Task Undo_AfterADeclaredLoad_LaterChangesStillUndoOneByOne()
        {
            using P3HeadlessSession session = new(new P3MemoryPackageStore(), new P3MemorySourceStore());
            LocalActorIdentityProvider identity = new("mod-builder");
            ActorContext builder = Actor(identity);
            LuaModsLlmTool tool = new(session.Controller.Runtime, new CoreAISettingsOptions(), NullLog.Instance,
                P3Fixtures.SessionCapabilities, true, identity, BuiltInAgentRoleIds.Programmer, session.Gate);
            JObject loaded = JObject.Parse(await tool.ExecuteAsync("load", "door", ServerHeader + "local value = 1"));
            Assert.IsTrue(loaded.Value<bool>("success"), loaded.ToString());
            Assert.IsTrue(TryBound(session, "door", out RbxLuaSourceContainer script));
            InstanceId scriptId = script.Id;
            Assert.IsTrue((await session.ExecuteAsAsync(builder, BuildTower)).Success);
            Assert.IsTrue(session.Undo.GetStatus(builder.ActorId).CanUndo, session.Undo.GetStatus(builder.ActorId).Reason);

            RbxWorldUndoResult towerUndone = await session.Undo.UndoAsync(builder);

            Assert.IsTrue(towerUndone.Success, towerUndone.Message);
            Assert.IsNull(session.Controller.CurrentRbxApi.Registry.WorldRoot.FindFirstChild("Tower"));
            Assert.IsTrue(session.Controller.Runtime.IsLoaded(builder, "door"), "the declared mod is part of that world");
            Assert.IsTrue(TryBound(session, "door", out RbxLuaSourceContainer restored));
            Assert.AreEqual(scriptId, restored.Id);
            RbxWorldUndoStatus status = session.Undo.GetStatus(builder.ActorId);
            Assert.IsTrue(status.CanUndo, "the restored script instance does not refuse the next undo: " + status.Reason);

            RbxWorldUndoResult loadUndone = await session.Undo.UndoAsync(builder);

            Assert.IsTrue(loadUndone.Success, loadUndone.Message);
            Assert.IsFalse(session.Controller.Runtime.IsLoaded(builder, "door"));
            Assert.IsFalse(TryBound(session, "door", out _));
            await session.Gate.WaitForPendingBackupAsync();
        }
    }
}
