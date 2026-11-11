using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Scripts;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Scripts
{
    /// <summary>
    /// The engine-free half of <c>require</c> (MVP4 design note SC-10, SC-12): author trust, the server-only location
    /// rule, require-by-string parsing and non-blocking resolution (R3.5, R3.6), and the stable error texts (R3.1, R3.8,
    /// DEV-1).
    /// </summary>
    [TestFixture]
    public sealed class ModuleRequirePolicyEditModeTests
    {
        private sealed class Node
        {
            public readonly string Name;
            public readonly Node Parent;
            public readonly List<Node> Children = new List<Node>();

            public Node(string name, Node parent)
            {
                Name = name;
                Parent = parent;
                parent?.Children.Add(this);
            }

            public Node FindFirstChild(string name)
            {
                foreach (Node child in Children)
                {
                    if (child.Name == name)
                    {
                        return child;
                    }
                }

                return null;
            }
        }

        private static RequirePathResolution<Node> Resolve(string text, Node script, Node game)
        {
            Assert.IsTrue(RequirePath.TryParse(text, out RequirePath path, out string error), error);
            return path.Resolve(script, game, node => node.Parent, (node, name) => node.FindFirstChild(name));
        }

        [Test]
        public void Require_ForeignActorsModuleFromHostScript_IsNotAuthority()
        {
            RbxError error = ModuleRequirePolicy.CheckTrust(null, "alice", "shared_utils");
            Assert.IsNotNull(error);
            Assert.AreEqual(RbxErrorCode.NotAuthority, error.Code);
            Assert.AreEqual(
                "module 'shared_utils' belongs to actor 'alice'; a script of actor 'host' cannot run its code",
                error.RawMessage);
        }

        [TestCase(null)]
        [TestCase("")]
        public void Require_HostModuleFromRestrictedScript_Works(string moduleOwner)
        {
            Assert.IsNull(ModuleRequirePolicy.CheckTrust("alice", moduleOwner, "shared_utils"));
        }

        [Test]
        public void Require_SameActorsModule_Works()
        {
            Assert.IsNull(ModuleRequirePolicy.CheckTrust("alice", "alice", "alice_utils"));
            Assert.IsNull(ModuleRequirePolicy.CheckTrust(null, null, "host_utils"));
        }

        [Test]
        public void Require_AnotherRestrictedActorsModule_IsNotAuthority()
        {
            Assert.IsNotNull(ModuleRequirePolicy.CheckTrust("bob", "alice", "alice_utils"));
        }

        [TestCase(ScriptContainer.ServerStorage)]
        [TestCase(ScriptContainer.ServerScriptService)]
        public void Require_ServerOnlyModuleFromClientScript_IsContextViolation(ScriptContainer container)
        {
            RbxError error = ModuleRequirePolicy.CheckLocation(ScriptContext.Client, container, "ServerStorage.Secrets");
            Assert.IsNotNull(error);
            Assert.AreEqual(RbxErrorCode.ContextViolation, error.Code);
            StringAssert.Contains("a client never receives", error.RawMessage);
        }

        [TestCase(ScriptContext.Client, ScriptContainer.ReplicatedStorage)]
        [TestCase(ScriptContext.Client, ScriptContainer.OutsideDataModel)]
        [TestCase(ScriptContext.Server, ScriptContainer.ServerStorage)]
        [TestCase(ScriptContext.Legacy, ScriptContainer.ServerStorage)]
        public void Require_ModuleTheRequirersSideReceives_Works(ScriptContext requirer, ScriptContainer container)
        {
            Assert.IsNull(ModuleRequirePolicy.CheckLocation(requirer, container, "Module"));
        }

        [Test]
        public void R2_1_ModuleScriptIsRequirableFromBothSides()
        {
            Assert.IsNull(ModuleRequirePolicy.CheckLocation(ScriptContext.Server, ScriptContainer.ReplicatedStorage,
                "ReplicatedStorage.Shared"));
            Assert.IsNull(ModuleRequirePolicy.CheckLocation(ScriptContext.Client, ScriptContainer.ReplicatedStorage,
                "ReplicatedStorage.Shared"));
        }

        [Test]
        public void Require_LocationOfAModuleRequirer_IsACallerBug()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                ModuleRequirePolicy.CheckLocation(ScriptContext.Shared, ScriptContainer.ReplicatedStorage, "M"));
        }

        [Test]
        public void DEV1_CycleError_CarriesTheGuideWordingAndPath()
        {
            RbxError error = ModuleRequirePolicy.CycleError(new string[]
                { "ReplicatedStorage.A", "ReplicatedStorage.B", "ReplicatedStorage.A" });
            Assert.AreEqual(RbxErrorCode.CyclicRequire, error.Code);
            Assert.AreEqual(
                "CYCLIC_REQUIRE: Requested module was required recursively: ReplicatedStorage.A -> ReplicatedStorage.B"
                + " -> ReplicatedStorage.A | fix: move what both need into a third module that neither requires, or "
                + "require one of them inside a function",
                error.Message);
            Assert.Throws<ArgumentException>(() => ModuleRequirePolicy.CycleError(Array.Empty<string>()));
        }

        [Test]
        public void R3_8_PreviouslyFailedError_ReRaisesTheFirstError()
        {
            RbxError error = ModuleRequirePolicy.PreviouslyFailedError("ReplicatedStorage.A", "line 3: boom");
            Assert.AreEqual(RbxErrorCode.BadArgument, error.Code);
            Assert.AreEqual("Requested module experienced an error while loading: ReplicatedStorage.A: line 3: boom",
                error.RawMessage);
        }

        [TestCase(0)]
        [TestCase(2)]
        public void R3_1_ModuleMustReturnExactlyOneValue(int count)
        {
            RbxError error = ModuleRequirePolicy.ReturnCountError("ReplicatedStorage.A", count);
            StringAssert.Contains("returned " + count + " values", error.RawMessage);
            Assert.Throws<ArgumentOutOfRangeException>(() => ModuleRequirePolicy.ReturnCountError("A", 1));
        }

        [Test]
        public void R3_1_ModuleReturningNil_IsRefused()
        {
            StringAssert.Contains("returned nil", ModuleRequirePolicy.ReturnedNilError("A").RawMessage);
        }

        [Test]
        public void R3_5_RequireByAssetId_IsRefusedLoudly()
        {
            RbxError error = ModuleRequirePolicy.AssetIdError();
            Assert.AreEqual("BAD_ARGUMENT: require(assetId) loads a published model, which CoreAI does not have | fix: "
                            + "put the module in the world and require the instance", error.Message);
        }

        [Test]
        public void R3_5_RequireByStringPaths_ParseToAnchorAndSteps()
        {
            Assert.IsTrue(RequirePath.TryParse("./Sibling", out RequirePath sibling, out string _));
            Assert.AreEqual(RequirePathAnchor.Script, sibling.Anchor);
            Assert.AreEqual(2, sibling.Steps.Count);
            Assert.IsTrue(sibling.Steps[0].IsParent);
            Assert.AreEqual("Sibling", sibling.Steps[1].ChildName);

            Assert.IsTrue(RequirePath.TryParse("../../Up", out RequirePath twoUp, out string _));
            Assert.AreEqual(4, twoUp.Steps.Count, "../ is two parents; the extra .. is one more");

            Assert.IsTrue(RequirePath.TryParse("@self/Child", out RequirePath self, out string _));
            Assert.AreEqual(1, self.Steps.Count);

            Assert.IsTrue(RequirePath.TryParse("@game/ReplicatedStorage/Shared/M", out RequirePath game, out string _));
            Assert.AreEqual(RequirePathAnchor.Game, game.Anchor);
            Assert.AreEqual(3, game.Steps.Count);
        }

        [Test]
        public void R3_5_RequireByStringPaths_ResolveAsTheMirrorDescribes()
        {
            Node gameNode = new Node("game", null);
            Node replicated = new Node("ReplicatedStorage", gameNode);
            Node shared = new Node("Shared", replicated);
            Node module = new Node("MyModule", shared);
            Node folder = new Node("Client", replicated);
            Node script = new Node("Main", folder);
            Node child = new Node("Child", script);
            Node sibling = new Node("Sibling", folder);

            Assert.AreSame(sibling, Resolve("./Sibling", script, gameNode).Node);
            Assert.AreSame(shared, Resolve("../Shared", script, gameNode).Node);
            Assert.AreSame(module, Resolve("../../ReplicatedStorage/Shared/MyModule", script, gameNode).Node);
            Assert.AreSame(child, Resolve("@self/Child", script, gameNode).Node);
            Assert.AreSame(module, Resolve("@game/ReplicatedStorage/Shared/MyModule", script, gameNode).Node);
            Assert.AreSame(sibling, Resolve("./Sibling/../Sibling", script, gameNode).Node);
        }

        [TestCase("MyModule", "has no prefix")]
        [TestCase("/abs", "has no prefix")]
        [TestCase("@pkg/Thing", "alias '@pkg' does not exist")]
        [TestCase("@self", "alias '@self' does not exist")]
        [TestCase("./", "names no module")]
        [TestCase("./a//b", "empty component")]
        [TestCase("./a/./b", "'.' component")]
        [TestCase("", "is empty")]
        public void R3_5_MalformedPath_IsRefusedWithAReason(string text, string expected)
        {
            Assert.IsFalse(RequirePath.TryParse(text, out RequirePath path, out string error));
            Assert.IsNull(path);
            StringAssert.Contains(expected, error);
        }

        [Test]
        public void R3_6_RequireMissingModuleFailsImmediately()
        {
            Node gameNode = new Node("game", null);
            Node replicated = new Node("ReplicatedStorage", gameNode);
            Node script = new Node("Main", replicated);
            RequirePathResolution<Node> missing = Resolve("./Later", script, gameNode);
            Assert.IsFalse(missing.Found);
            StringAssert.Contains("finds no child named 'Later'", missing.Error);
            StringAssert.Contains("does not wait", missing.Error);
        }

        [Test]
        public void R3_6_RequireAfterTheModuleExists_Resolves()
        {
            Node gameNode = new Node("game", null);
            Node replicated = new Node("ReplicatedStorage", gameNode);
            Node script = new Node("Main", replicated);
            Node later = new Node("Later", replicated);
            RequirePathResolution<Node> found = Resolve("./Later", script, gameNode);
            Assert.IsTrue(found.Found);
            Assert.AreSame(later, found.Node);
        }

        [Test]
        public void R3_5_PathAboveTheRoot_IsReported()
        {
            Node script = new Node("Main", null);
            RequirePathResolution<Node> above = Resolve("./Sibling", script, null);
            Assert.IsFalse(above.Found);
            StringAssert.Contains("goes above the top of the tree", above.Error);
            RequirePathResolution<Node> noGame = Resolve("@game/Workspace", script, null);
            StringAssert.Contains("starts at game", noGame.Error);
        }

        [Test]
        public void Require_OtherErrors_UseBadArgument()
        {
            RbxError[] errors =
            {
                ModuleRequirePolicy.ArgumentTypeError("number"),
                ModuleRequirePolicy.NotAModuleScriptError("./Part", "Part"),
                ModuleRequirePolicy.NoSourceError("ReplicatedStorage.Clone"),
                ModuleRequirePolicy.UnloadedError("ReplicatedStorage.Off"),
                ModuleRequirePolicy.PathError("require path './x' finds no child named 'x'")
            };
            foreach (RbxError error in errors)
            {
                Assert.AreEqual(RbxErrorCode.BadArgument, error.Code, error.Message);
                Assert.IsFalse(string.IsNullOrEmpty(error.Fix), error.Message);
            }
        }
    }
}
