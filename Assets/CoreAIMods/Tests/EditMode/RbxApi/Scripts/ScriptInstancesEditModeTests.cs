using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Scripts;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Scripts
{
    /// <summary>
    /// MVP4 step B1, engine-free: the script instance classes the catalog builds, their mirror defaults, the
    /// SC-4 table enforced on <c>RunContext</c>, clones that are never bound, the <c>Enum.RunContext</c> items and
    /// the member stubs that keep the Lua surface unchanged until step C2. Every rule has its negative twin.
    /// </summary>
    [TestFixture]
    public sealed class ScriptInstancesEditModeTests
    {
        private InstanceRegistry _registry;

        [SetUp]
        public void SetUp()
        {
            _registry = new InstanceRegistry();
        }

        private long RevisionOf(RbxInstance instance)
        {
            Assert.IsTrue(_registry.TryGetRecord(instance.Id, out InstanceRecord record));
            return record.Revision;
        }

        [TestCase("Script")]
        [TestCase("LocalScript")]
        public void B1_ScriptAndLocalScript_AreCreatableBaseScripts_WithTheMirrorDefaults(string className)
        {
            RbxInstance instance = _registry.Create(className);

            RbxBaseScript script = instance as RbxBaseScript;
            Assert.IsNotNull(script, className + " is built as an RbxBaseScript");
            Assert.AreEqual(className, script.ClassName);
            Assert.IsTrue(script.IsA("BaseScript"));
            Assert.IsTrue(script.IsA("LuaSourceContainer"));
            Assert.IsTrue(script.Enabled, "Script.yaml: a new script is enabled");
            Assert.IsFalse(script.Disabled);
            Assert.AreEqual(RunContextValues.Legacy, script.RunContext);
            Assert.AreEqual(string.Empty, script.SourceId);
            Assert.IsFalse(script.IsBound, "an Instance.new script names no store entry");
        }

        [Test]
        public void B1_ModuleScript_IsACreatableSourceContainer_WithoutBaseScriptMembers()
        {
            RbxInstance instance = _registry.Create("ModuleScript");

            Assert.IsInstanceOf<RbxLuaSourceContainer>(instance);
            Assert.IsNotInstanceOf<RbxBaseScript>(instance, "ModuleScript has no BaseScript ancestor (R1.1)");
            Assert.IsTrue(instance.IsA("LuaSourceContainer"));
            Assert.IsFalse(instance.IsA("BaseScript"));
        }

        [TestCase("LuaSourceContainer")]
        [TestCase("BaseScript")]
        public void B1_Negative_TheAbstractScriptClasses_StayNotCreatable(string className)
        {
            ClassCatalog catalog = ClassCatalog.CreateMvp1();
            Assert.IsTrue(catalog.TryGet(className, out ClassDescriptor descriptor));
            Assert.IsTrue(descriptor.IsAbstract, className + " is NotCreatable in the mirror");
            Assert.IsFalse(descriptor.IsCreatable);
            Assert.IsTrue(catalog.TryGet("Script", out ClassDescriptor script));
            Assert.IsTrue(script.IsCreatable, "twin: the concrete class is creatable");
        }

        [Test]
        public void B1_EnabledAndRunContextWrites_AdvanceTheRevision_AndDisabledIsTheInverse()
        {
            RbxBaseScript script = (RbxBaseScript)_registry.Create("Script");
            long before = RevisionOf(script);

            script.Enabled = false;
            Assert.IsTrue(script.Disabled);
            long afterEnabled = RevisionOf(script);
            Assert.Greater(afterEnabled, before);

            script.RunContext = RunContextValues.Server;
            Assert.AreEqual(RunContextValues.Server, script.RunContext);
            Assert.Greater(RevisionOf(script), afterEnabled);

            script.Disabled = false;
            Assert.IsTrue(script.Enabled);
        }

        [Test]
        public void B1_Negative_WritingTheSameValue_IsNoChange()
        {
            RbxBaseScript script = (RbxBaseScript)_registry.Create("Script");
            long before = RevisionOf(script);

            script.Enabled = true;
            script.RunContext = RunContextValues.Legacy;

            Assert.AreEqual(before, RevisionOf(script));
        }

        [TestCase("LocalScript", 1, "RunContext cannot be used from a LocalScript")]
        [TestCase("LocalScript", 2, "RunContext cannot be used from a LocalScript")]
        [TestCase("Script", 3, "Studio plugin")]
        [TestCase("Script", 7, "is not an Enum.RunContext value")]
        public void R1_8_RunContextTheTableRefuses_RaisesBadArgument_AndChangesNothing(string className,
            int runContext, string fragment)
        {
            RbxBaseScript script = (RbxBaseScript)_registry.Create(className);
            long before = RevisionOf(script);

            RbxError error = Assert.Throws<RbxError>(() => script.RunContext = runContext);

            Assert.AreEqual(RbxErrorCode.BadArgument, error.Code);
            StringAssert.Contains(fragment, error.Message);
            Assert.AreEqual(RunContextValues.Legacy, script.RunContext);
            Assert.AreEqual(before, RevisionOf(script));
        }

        [Test]
        public void R1_8_Twin_ALegacyRunContextOnALocalScript_AndAClientOneOnAScript_AreAccepted()
        {
            RbxBaseScript local = (RbxBaseScript)_registry.Create("LocalScript");
            RbxBaseScript script = (RbxBaseScript)_registry.Create("Script");

            local.RunContext = RunContextValues.Legacy;
            script.RunContext = RunContextValues.Client;

            Assert.AreEqual(RunContextValues.Legacy, local.RunContext);
            Assert.AreEqual(RunContextValues.Client, script.RunContext);
        }

        [Test]
        public void SC_17_AClone_CopiesEnabledAndRunContext_ButIsNeverBound()
        {
            RbxBaseScript script = (RbxBaseScript)_registry.Create("Script");
            script.SourceId = "bound-mod";
            script.Enabled = false;
            script.RunContext = RunContextValues.Server;

            RbxBaseScript copy = (RbxBaseScript)script.Clone();

            Assert.IsFalse(copy.Enabled);
            Assert.AreEqual(RunContextValues.Server, copy.RunContext);
            Assert.AreEqual(string.Empty, copy.SourceId, "a clone of a bound script is unbound (SC-17)");
            Assert.AreEqual("bound-mod", script.SourceId, "twin: the original keeps its binding");
        }

        [Test]
        public void B1_TryRestoreState_PutsSavedStateBack_WithoutCountingAChange()
        {
            RbxBaseScript script = (RbxBaseScript)_registry.Create("Script");
            long before = RevisionOf(script);

            Assert.IsTrue(script.TryRestoreState(false, RunContextValues.Client, out string error), error);

            Assert.IsFalse(script.Enabled);
            Assert.AreEqual(RunContextValues.Client, script.RunContext);
            Assert.AreEqual(before, RevisionOf(script), "a restore is not a change");
        }

        [Test]
        public void B1_Negative_TryRestoreState_RefusesWhatTheTableRefuses_AndKeepsTheState()
        {
            RbxBaseScript local = (RbxBaseScript)_registry.Create("LocalScript");

            Assert.IsFalse(local.TryRestoreState(false, RunContextValues.Server, out string error));

            StringAssert.StartsWith("RunContext cannot be used from a LocalScript", error);
            Assert.IsTrue(local.Enabled);
            Assert.AreEqual(RunContextValues.Legacy, local.RunContext);
        }

        [Test]
        public void SC_4_EnumRunContext_HasTheMirrorItems()
        {
            RbxEnum runContext = RbxEnumRegistry.CreateWithBuiltins().Get("RunContext");

            Assert.AreEqual(RunContextValues.Legacy, runContext["Legacy"].Value);
            Assert.AreEqual(RunContextValues.Server, runContext["Server"].Value);
            Assert.AreEqual(RunContextValues.Client, runContext["Client"].Value);
            Assert.AreEqual(RunContextValues.Plugin, runContext["Plugin"].Value);
            Assert.IsFalse(runContext.TryGetItem("Studio", out RbxEnumItem _), "twin: no item beyond the mirror's");
        }

        [TestCase("Script", "Enabled", RbxKnownUnimplementedMemberStatus.Planned)]
        [TestCase("LocalScript", "RunContext", RbxKnownUnimplementedMemberStatus.Planned)]
        [TestCase("Script", "Source", RbxKnownUnimplementedMemberStatus.Planned)]
        [TestCase("ModuleScript", "Source", RbxKnownUnimplementedMemberStatus.Planned)]
        [TestCase("Script", "LinkedSource", RbxKnownUnimplementedMemberStatus.Unsupported)]
        public void B1_TheScriptMembers_StayLoudStubsForLua_UntilStepC2(string className, string member,
            RbxKnownUnimplementedMemberStatus status)
        {
            ClassCatalog catalog = ClassCatalog.CreateMvp1();

            Assert.IsTrue(catalog.TryGetKnownUnimplementedMember(className, member,
                RbxKnownUnimplementedMemberAccess.Read, out string _, out RbxKnownUnimplementedMemberDescriptor stub));
            Assert.AreEqual(status, stub.Status);
            Assert.IsFalse(catalog.TryGetKnownUnimplementedMember("ModuleScript", "Enabled",
                RbxKnownUnimplementedMemberAccess.Read, out string _, out RbxKnownUnimplementedMemberDescriptor _),
                "twin: ModuleScript has no Enabled at all");
        }
    }
}
