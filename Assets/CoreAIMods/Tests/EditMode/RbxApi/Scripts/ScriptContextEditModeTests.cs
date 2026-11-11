using System;
using CoreAI.Mods.Rbx.Instances.Networking;
using CoreAI.Mods.Rbx.Instances.Scripts;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Scripts
{
    /// <summary>
    /// The declared context of the <c>@coreai</c> header, the effective context of an instance (MVP4 design note SC-4,
    /// rulebook R1.4-R1.8) and which side a context runs on per topology (SC-9 item 5).
    /// </summary>
    [TestFixture]
    public sealed class ScriptContextEditModeTests
    {
        [TestCase("server", ScriptContext.Server)]
        [TestCase("SERVER", ScriptContext.Server)]
        [TestCase(" Client ", ScriptContext.Client)]
        [TestCase("shared", ScriptContext.Shared)]
        [TestCase("Shared", ScriptContext.Shared)]
        public void ScriptContext_ParsesServerClientSharedCaseInsensitively(string text, ScriptContext expected)
        {
            Assert.IsTrue(ScriptContexts.TryParseDeclared(text, out ScriptContext context, out string error));
            Assert.AreEqual(expected, context);
            Assert.IsNull(error);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void ScriptContext_AbsentValue_IsLegacy(string text)
        {
            Assert.IsTrue(ScriptContexts.TryParseDeclared(text, out ScriptContext context, out string error));
            Assert.AreEqual(ScriptContext.Legacy, context);
            Assert.IsNull(error);
        }

        [TestCase("clinet")]
        [TestCase("legacy")]
        [TestCase("both")]
        public void ScriptContext_UnknownValue_ReturnsNamedError(string text)
        {
            Assert.IsFalse(ScriptContexts.TryParseDeclared(text, out ScriptContext context, out string error));
            Assert.AreEqual(ScriptContext.Legacy, context);
            Assert.AreEqual("@coreai context '" + text + "' is not server, client or shared", error);
        }

        [Test]
        public void DeclaredNames_RoundTripAndLegacyIsEmpty()
        {
            foreach (ScriptContext context in (ScriptContext[])Enum.GetValues(typeof(ScriptContext)))
            {
                string name = ScriptContexts.ToDeclaredName(context);
                Assert.IsTrue(ScriptContexts.TryParseDeclared(name, out ScriptContext parsed, out string _));
                Assert.AreEqual(context, parsed);
            }

            Assert.AreEqual(string.Empty, ScriptContexts.ToDeclaredName(ScriptContext.Legacy));
            Assert.AreEqual("legacy", ScriptContexts.ToDisplayName(ScriptContext.Legacy));
        }

        [TestCase("ModuleScript", 0, ScriptContext.Legacy, ScriptContext.Shared)]
        [TestCase("ModuleScript", 0, ScriptContext.Shared, ScriptContext.Shared)]
        [TestCase("LocalScript", 0, ScriptContext.Legacy, ScriptContext.Client)]
        [TestCase("LocalScript", 0, ScriptContext.Client, ScriptContext.Client)]
        [TestCase("Script", 1, ScriptContext.Server, ScriptContext.Server)]
        [TestCase("Script", 2, ScriptContext.Client, ScriptContext.Client)]
        [TestCase("Script", 0, ScriptContext.Server, ScriptContext.Server)]
        [TestCase("Script", 0, ScriptContext.Legacy, ScriptContext.Legacy)]
        public void Effective_FollowsTheSc4Table(string className, int runContext, ScriptContext declared,
            ScriptContext expected)
        {
            Assert.IsTrue(ScriptContexts.TryGetEffective(className, runContext, declared,
                out ScriptContext effective, out string error), error);
            Assert.AreEqual(expected, effective);
        }

        [TestCase(1, ScriptContext.Server)]
        [TestCase(2, ScriptContext.Client)]
        public void Effective_UndeclaredScript_TakesTheInstancesRunContext(int runContext, ScriptContext expected)
        {
            Assert.IsTrue(ScriptContexts.TryGetEffective("Script", runContext, ScriptContext.Legacy,
                out ScriptContext effective, out string _));
            Assert.AreEqual(expected, effective);
        }

        [TestCase("Script", 1, ScriptContext.Client, "runs as server, but its source declares context: client")]
        [TestCase("Script", 2, ScriptContext.Server, "runs as client, but its source declares context: server")]
        [TestCase("Script", 0, ScriptContext.Client, "runs as legacy, but its source declares context: client")]
        [TestCase("Script", 1, ScriptContext.Shared, "context: shared makes a ModuleScript")]
        [TestCase("LocalScript", 0, ScriptContext.Server, "always runs on the client")]
        [TestCase("ModuleScript", 0, ScriptContext.Client, "cannot declare context: client")]
        public void Effective_DeclarationDisagreeingWithInstance_IsRefused(string className, int runContext,
            ScriptContext declared, string expectedFragment)
        {
            Assert.IsFalse(ScriptContexts.TryGetEffective(className, runContext, declared,
                out ScriptContext _, out string error));
            StringAssert.Contains(expectedFragment, error);
        }

        [TestCase(1)]
        [TestCase(2)]
        public void R1_8_RunContextOnLocalScript_IsRefused(int runContext)
        {
            Assert.IsFalse(ScriptContexts.TryGetEffective("LocalScript", runContext, ScriptContext.Legacy,
                out ScriptContext _, out string error));
            StringAssert.StartsWith("RunContext cannot be used from a LocalScript", error);
        }

        [Test]
        public void R1_8_LegacyRunContextOnLocalScript_IsAccepted()
        {
            Assert.IsTrue(ScriptContexts.TryGetEffective("LocalScript", RunContextValues.Legacy,
                ScriptContext.Legacy, out ScriptContext effective, out string error));
            Assert.AreEqual(ScriptContext.Client, effective);
            Assert.IsNull(error);
        }

        [Test]
        public void Effective_PluginRunContext_IsRefused()
        {
            Assert.IsFalse(ScriptContexts.TryGetEffective("Script", RunContextValues.Plugin, ScriptContext.Legacy,
                out ScriptContext _, out string error));
            StringAssert.Contains("CoreAI does not have", error);
        }

        [TestCase(-1)]
        [TestCase(4)]
        public void Effective_UndefinedRunContext_IsRefused(int runContext)
        {
            Assert.IsFalse(ScriptContexts.TryGetEffective("Script", runContext, ScriptContext.Legacy,
                out ScriptContext _, out string error));
            StringAssert.Contains("is not an Enum.RunContext value", error);
        }

        [TestCase("Part")]
        [TestCase("script")]
        [TestCase(null)]
        public void Effective_NonScriptClass_IsRefused(string className)
        {
            Assert.IsFalse(ScriptContexts.TryGetEffective(className, RunContextValues.Legacy, ScriptContext.Legacy,
                out ScriptContext _, out string error));
            StringAssert.Contains("is not a script class", error);
        }

        [TestCase(RbxNetworkTopology.Solo)]
        [TestCase(RbxNetworkTopology.Host)]
        [TestCase(RbxNetworkTopology.DedicatedServer)]
        public void ContextMatrix_ServerSideRunsOnSoloHostAndDedicated(RbxNetworkTopology topology)
        {
            Assert.IsTrue(ScriptContexts.IsSideAvailable(ScriptContext.Server, topology));
            Assert.IsTrue(ScriptContexts.IsSideAvailable(ScriptContext.Legacy, topology));
        }

        [Test]
        public void ContextMatrix_ServerSideNeverRunsOnPureClient()
        {
            Assert.IsFalse(ScriptContexts.IsSideAvailable(ScriptContext.Server, RbxNetworkTopology.Client));
            Assert.IsFalse(ScriptContexts.IsSideAvailable(ScriptContext.Legacy, RbxNetworkTopology.Client));
        }

        [TestCase(RbxNetworkTopology.Solo)]
        [TestCase(RbxNetworkTopology.Host)]
        [TestCase(RbxNetworkTopology.Client)]
        public void ContextMatrix_ClientSideRunsOnSoloHostAndClient(RbxNetworkTopology topology)
        {
            Assert.IsTrue(ScriptContexts.IsSideAvailable(ScriptContext.Client, topology));
        }

        [Test]
        public void ContextMatrix_ClientSideNeverRunsOnDedicatedServer()
        {
            Assert.IsFalse(ScriptContexts.IsSideAvailable(ScriptContext.Client, RbxNetworkTopology.DedicatedServer));
        }

        [Test]
        public void SideOfAModule_IsACallerBug()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => ScriptContexts.RunsOnServerSide(ScriptContext.Shared));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                ScriptContexts.IsSideAvailable(ScriptContext.Shared, RbxNetworkTopology.Solo));
        }

        [Test]
        public void SameSide_LegacyAndServerShareTheServerSide()
        {
            Assert.IsTrue(ScriptContexts.SameSide(ScriptContext.Legacy, ScriptContext.Server));
            Assert.IsTrue(ScriptContexts.SameSide(ScriptContext.Client, ScriptContext.Client));
        }

        [Test]
        public void SameSide_ClientAndServerDiffer()
        {
            Assert.IsFalse(ScriptContexts.SameSide(ScriptContext.Client, ScriptContext.Server));
            Assert.IsFalse(ScriptContexts.SameSide(ScriptContext.Legacy, ScriptContext.Client));
        }

        [Test]
        public void NewInstanceShape_FollowsTheDeclaredContext()
        {
            Assert.AreEqual("Script", ScriptContexts.ClassNameFor(ScriptContext.Server));
            Assert.AreEqual("Script", ScriptContexts.ClassNameFor(ScriptContext.Client));
            Assert.AreEqual("Script", ScriptContexts.ClassNameFor(ScriptContext.Legacy));
            Assert.AreEqual("ModuleScript", ScriptContexts.ClassNameFor(ScriptContext.Shared));
            Assert.AreEqual(RunContextValues.Server, ScriptContexts.RunContextFor(ScriptContext.Server));
            Assert.AreEqual(RunContextValues.Client, ScriptContexts.RunContextFor(ScriptContext.Client));
            Assert.AreEqual(RunContextValues.Legacy, ScriptContexts.RunContextFor(ScriptContext.Legacy));
        }

        [Test]
        public void NewInstanceShape_ModuleHasNoRunContext()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => ScriptContexts.RunContextFor(ScriptContext.Shared));
        }

        [Test]
        public void RunContextValues_MatchTheMirrorEnum()
        {
            Assert.AreEqual("Legacy", RunContextValues.ToName(0));
            Assert.AreEqual("Server", RunContextValues.ToName(1));
            Assert.AreEqual("Client", RunContextValues.ToName(2));
            Assert.AreEqual("Plugin", RunContextValues.ToName(3));
            Assert.IsFalse(RunContextValues.IsDefined(4));
        }
    }
}
