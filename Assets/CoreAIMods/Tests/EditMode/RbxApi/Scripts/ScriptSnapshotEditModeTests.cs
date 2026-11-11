using System;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Scripts;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Scripts
{
    /// <summary>
    /// MVP4 step B2, engine-free: the tree serializer carries the state of a script instance (its binding, and on a
    /// BaseScript Enabled and RunContext) in <see cref="ScriptSnapshot"/>, puts it back on restore, keeps a script node
    /// without the block valid as an unbound script with the class defaults, and refuses every payload the SC-4 table
    /// or the class shape forbids. Every rule has its negative twin.
    /// </summary>
    [TestFixture]
    public sealed class ScriptSnapshotEditModeTests
    {
        private InstanceRegistry _registry;
        private RbxDataModel _game;

        [SetUp]
        public void SetUp()
        {
            _registry = new InstanceRegistry();
            _game = DataModelBootstrap.CreateGame(_registry);
            RbxBaseScript server = NewScript("Script", "Server", _game.GetService("ServerScriptService"));
            server.SourceId = "server-mod";
            server.Enabled = false;
            server.RunContext = RunContextValues.Server;
            RbxBaseScript local = NewScript("LocalScript", "Local", _game.GetService("ReplicatedStorage"));
            RbxLuaSourceContainer module = (RbxLuaSourceContainer)_registry.Create("ModuleScript");
            module.Name = "Module";
            module.SourceId = "module-mod";
            module.Parent = local;
        }

        private RbxBaseScript NewScript(string className, string name, RbxInstance parent)
        {
            RbxBaseScript script = (RbxBaseScript)_registry.Create(className);
            script.Name = name;
            script.Parent = parent;
            return script;
        }

        private static InstanceSnapshot NodeNamed(InstanceTreeSnapshot snapshot, string name)
        {
            foreach (InstanceSnapshot node in snapshot.Instances)
            {
                if (string.Equals(node.Name, name, StringComparison.Ordinal))
                {
                    return node;
                }
            }

            Assert.Fail("the snapshot has no node named '" + name + "'");
            return null;
        }

        private static RbxInstance RestoredNamed(RbxInstance root, string name)
        {
            RbxInstance found = root.FindFirstChild(name, true);
            Assert.IsNotNull(found, "the restored tree has no node named '" + name + "'");
            return found;
        }

        private void AssertRefused(Action<InstanceSnapshot> mutate, string nodeName, string expected)
        {
            InstanceTreeSnapshot snapshot = InstanceTreeSerializer.Capture(_game);
            mutate(NodeNamed(snapshot, nodeName));
            RbxError error = Assert.Throws<RbxError>(() => InstanceTreeSerializer.Validate(snapshot, new InstanceRegistry()));
            StringAssert.Contains(expected, error.Message);
        }

        [Test]
        public void B2_Capture_CarriesTheBindingEnabledAndRunContext_OfEveryScriptClass()
        {
            InstanceTreeSnapshot snapshot = InstanceTreeSerializer.Capture(_game);

            ScriptSnapshot server = NodeNamed(snapshot, "Server").Script;
            Assert.AreEqual("server-mod", server.SourceId);
            Assert.AreEqual(false, server.Enabled);
            Assert.AreEqual(RunContextValues.Server, server.RunContext);
            ScriptSnapshot local = NodeNamed(snapshot, "Local").Script;
            Assert.AreEqual(string.Empty, local.SourceId, "an unbound script captures an empty binding");
            Assert.AreEqual(true, local.Enabled);
            Assert.AreEqual(RunContextValues.Legacy, local.RunContext);
            ScriptSnapshot module = NodeNamed(snapshot, "Module").Script;
            Assert.AreEqual("module-mod", module.SourceId);
            Assert.IsNull(module.Enabled, "a ModuleScript has no Enabled (R1.1)");
            Assert.IsNull(module.RunContext, "nor a RunContext");
            Assert.IsNull(NodeNamed(snapshot, "Workspace").Script, "twin: no other class carries script state");
        }

        [Test]
        public void B2_RestoreThenCapture_IsIdentical_AndKeepsTheRevisions()
        {
            InstanceTreeSnapshot first = InstanceTreeSerializer.Capture(_game);
            InstanceRegistry target = new();

            RbxInstance root = InstanceTreeSerializer.Restore(first, target);
            InstanceTreeSnapshot second = InstanceTreeSerializer.Capture(root);

            RbxBaseScript server = (RbxBaseScript)RestoredNamed(root, "Server");
            Assert.AreEqual("server-mod", server.SourceId);
            Assert.IsFalse(server.Enabled);
            Assert.AreEqual(RunContextValues.Server, server.RunContext);
            Assert.AreEqual("module-mod", ((RbxLuaSourceContainer)RestoredNamed(root, "Module")).SourceId);
            Assert.AreEqual(first.Instances.Count, second.Instances.Count);
            for (int index = 0; index < first.Instances.Count; index++)
            {
                InstanceSnapshot expected = first.Instances[index];
                InstanceSnapshot actual = second.Instances[index];
                Assert.AreEqual(expected.Id, actual.Id);
                Assert.AreEqual(expected.Revision, actual.Revision, expected.Name + " keeps its captured revision");
                Assert.AreEqual(expected.Script == null, actual.Script == null, expected.Name);
                if (expected.Script != null)
                {
                    Assert.AreEqual(expected.Script.SourceId, actual.Script.SourceId, expected.Name);
                    Assert.AreEqual(expected.Script.Enabled, actual.Script.Enabled, expected.Name);
                    Assert.AreEqual(expected.Script.RunContext, actual.Script.RunContext, expected.Name);
                }
            }
        }

        [Test]
        public void B2_ScriptNodeWithoutTheBlock_RestoresUnbound_WithTheClassDefaults()
        {
            InstanceTreeSnapshot snapshot = InstanceTreeSerializer.Capture(_game);
            NodeNamed(snapshot, "Server").Script = null;
            NodeNamed(snapshot, "Module").Script = null;

            RbxInstance root = InstanceTreeSerializer.Restore(snapshot, new InstanceRegistry());

            RbxBaseScript server = (RbxBaseScript)RestoredNamed(root, "Server");
            Assert.IsFalse(server.IsBound, "a node without the block is an unbound script");
            Assert.IsTrue(server.Enabled);
            Assert.AreEqual(RunContextValues.Legacy, server.RunContext);
            Assert.IsFalse(((RbxLuaSourceContainer)RestoredNamed(root, "Module")).IsBound);
        }

        [Test]
        public void B2_ScriptBlockOnAClassThatIsNoScript_IsRefused()
        {
            AssertRefused(node => node.Script = new ScriptSnapshot { SourceId = "x", Enabled = true, RunContext = 0 },
                "Workspace", "mismatched Script state");
        }

        [Test]
        public void B2_PluginRunContext_IsRefused()
        {
            AssertRefused(node => node.Script.RunContext = RunContextValues.Plugin, "Server", "RunContext Plugin");
        }

        [Test]
        public void B2_UndefinedRunContext_IsRefused()
        {
            AssertRefused(node => node.Script.RunContext = 7, "Server", "is not an Enum.RunContext value");
        }

        [Test]
        public void R1_8_LocalScriptWithAServerRunContext_IsRefused()
        {
            AssertRefused(node => node.Script.RunContext = RunContextValues.Server, "Local",
                "RunContext cannot be used from a LocalScript");
        }

        [Test]
        public void R1_8_LocalScriptWithTheLegacyRunContext_IsAccepted()
        {
            InstanceTreeSnapshot snapshot = InstanceTreeSerializer.Capture(_game);
            Assert.AreEqual(RunContextValues.Legacy, NodeNamed(snapshot, "Local").Script.RunContext);

            Assert.DoesNotThrow(() => InstanceTreeSerializer.Validate(snapshot, new InstanceRegistry()));
        }

        [Test]
        public void B2_ModuleScriptWithEnabledOrRunContext_IsRefused()
        {
            AssertRefused(node => node.Script.Enabled = true, "Module", "carries Enabled or RunContext");
            AssertRefused(node => node.Script.RunContext = RunContextValues.Legacy, "Module",
                "carries Enabled or RunContext");
        }

        [Test]
        public void B2_BaseScriptWithoutEnabledOrRunContext_IsRefused()
        {
            AssertRefused(node => node.Script.Enabled = null, "Server", "is missing Enabled or RunContext");
            AssertRefused(node => node.Script.RunContext = null, "Local", "is missing Enabled or RunContext");
        }

        [Test]
        public void B2_NilSourceId_IsRefused_WhileAnEmptyOneIsUnbound()
        {
            AssertRefused(node => node.Script.SourceId = null, "Server", "has a nil SourceId");

            InstanceTreeSnapshot snapshot = InstanceTreeSerializer.Capture(_game);
            NodeNamed(snapshot, "Server").Script.SourceId = string.Empty;
            RbxInstance root = InstanceTreeSerializer.Restore(snapshot, new InstanceRegistry());
            Assert.IsFalse(((RbxBaseScript)RestoredNamed(root, "Server")).IsBound);
        }
    }
}
