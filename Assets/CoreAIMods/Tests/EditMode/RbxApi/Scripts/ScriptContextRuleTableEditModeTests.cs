using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Scripts;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Scripts
{
    /// <summary>
    /// The side-only member table of MVP4 design note SC-13: its rows, the CONTEXT_VIOLATION it raises for declared
    /// contexts, its silence for legacy scripts, and the composition-time extension point packages use.
    /// </summary>
    [TestFixture]
    public sealed class ScriptContextRuleTableEditModeTests
    {
        [Test]
        public void ContextRules_EveryRowHasSideRuleAndFix()
        {
            ScriptContextRuleTable table = ScriptContextRuleTable.CreateDefault();
            Assert.Greater(table.Rules.Count, 20);
            HashSet<string> members = new HashSet<string>(StringComparer.Ordinal);
            foreach (ScriptContextRule rule in table.Rules)
            {
                Assert.IsTrue(members.Add(rule.Member), rule.Member);
                Assert.IsTrue(rule.Side == ScriptSide.Server || rule.Side == ScriptSide.Client, rule.Member);
                Assert.IsFalse(string.IsNullOrWhiteSpace(rule.RuleRef), rule.Member);
                Assert.IsFalse(string.IsNullOrWhiteSpace(rule.Fix), rule.Member);
            }
        }

        [TestCase("RemoteEvent.FireServer", ScriptSide.Client)]
        [TestCase("RemoteEvent.FireClient", ScriptSide.Server)]
        [TestCase("RemoteEvent.FireAllClients", ScriptSide.Server)]
        [TestCase("UnreliableRemoteEvent.OnServerEvent", ScriptSide.Server)]
        [TestCase("RemoteFunction.InvokeServer", ScriptSide.Client)]
        [TestCase("RemoteFunction.OnServerInvoke", ScriptSide.Server)]
        [TestCase("UserInputService", ScriptSide.Client)]
        [TestCase("RunService.RenderStepped", ScriptSide.Client)]
        [TestCase("ServerStorage", ScriptSide.Server)]
        [TestCase("HttpService.RequestAsync", ScriptSide.Server)]
        [TestCase("store_set", ScriptSide.Server)]
        [TestCase("camera_follow", ScriptSide.Client)]
        public void ContextRules_DefaultRowsMatchSc13(string member, ScriptSide side)
        {
            Assert.IsTrue(ScriptContextRuleTable.CreateDefault().TryGet(member, out ScriptContextRule rule));
            Assert.AreEqual(side, rule.Side);
        }

        [Test]
        public void ContextRules_FrozenTableRefusesAdd()
        {
            ScriptContextRuleTable table = ScriptContextRuleTable.CreateDefault();
            table.Freeze();
            table.Freeze();
            Assert.IsTrue(table.IsFrozen);
            Assert.Throws<InvalidOperationException>(() => table.Add(new ScriptContextRule(
                "TextChannel.SendAsync", ScriptSide.Client, "TextChannel.yaml", "send from a client script")));
        }

        [Test]
        public void ContextRules_PackageRowAddedBeforeFreeze_IsChecked()
        {
            ScriptContextRuleTable table = ScriptContextRuleTable.CreateDefault();
            table.Add(new ScriptContextRule("TextChannel.SendAsync", ScriptSide.Client, "P2; TextChannel.yaml",
                "send chat from a client script"));
            table.Freeze();
            RbxError error = table.Check("TextChannel.SendAsync", ScriptContext.Server);
            Assert.IsNotNull(error);
            Assert.AreEqual(RbxErrorCode.ContextViolation, error.Code);
            Assert.IsNull(table.Check("TextChannel.SendAsync", ScriptContext.Client));
        }

        [Test]
        public void ContextRules_DuplicateMemberIsRefused()
        {
            ScriptContextRuleTable table = ScriptContextRuleTable.CreateDefault();
            Assert.Throws<ArgumentException>(() => table.Add(new ScriptContextRule(
                "UserInputService", ScriptSide.Server, "elsewhere", "anything")));
        }

        [Test]
        public void ContextViolation_FireClientFromClientScript_NamesTheRule()
        {
            RbxError error = ScriptContextRuleTable.CreateDefault().Check("RemoteEvent.FireClient",
                ScriptContext.Client);
            Assert.IsNotNull(error);
            Assert.AreEqual(RbxErrorCode.ContextViolation, error.Code);
            Assert.AreEqual(
                "RemoteEvent.FireClient is server-only (§3.2; RemoteEvent.yaml) and this script runs as client",
                error.RawMessage);
            Assert.AreEqual("a client script sends to the server with FireServer", error.Fix);
        }

        [Test]
        public void ContextViolation_FireClientFromServerScript_Passes()
        {
            Assert.IsNull(ScriptContextRuleTable.CreateDefault().Check("RemoteEvent.FireClient",
                ScriptContext.Server));
        }

        [Test]
        public void ContextViolation_UserInputServiceFromServerScript_UsesTheWireFormat()
        {
            RbxError error = ScriptContextRuleTable.CreateDefault().Check("UserInputService.InputBegan",
                ScriptContext.Server);
            Assert.IsNotNull(error);
            Assert.AreEqual(
                "CONTEXT_VIOLATION: UserInputService.InputBegan is client-only (§3.2; UserInputService.yaml) and this "
                + "script runs as server | fix: move this code into a client script (context: client) and send the "
                + "result to the server with a RemoteEvent",
                error.Message);
        }

        [Test]
        public void UserInputService_FromClientScript_Passes()
        {
            ScriptContextRuleTable table = ScriptContextRuleTable.CreateDefault();
            Assert.IsNull(table.Check("UserInputService", ScriptContext.Client));
            Assert.IsNull(table.Check("UserInputService.InputBegan", ScriptContext.Client));
        }

        [Test]
        public void ContextViolation_ServerStorageFromClientScript()
        {
            RbxError error = ScriptContextRuleTable.CreateDefault().Check("ServerStorage", ScriptContext.Client);
            Assert.IsNotNull(error);
            StringAssert.Contains("ServerStorage is server-only", error.RawMessage);
        }

        [Test]
        public void ServerStorage_FromServerScript_Passes()
        {
            Assert.IsNull(ScriptContextRuleTable.CreateDefault().Check("ServerStorage", ScriptContext.Server));
        }

        [Test]
        public void Legacy_IsNeverChecked()
        {
            ScriptContextRuleTable table = ScriptContextRuleTable.CreateDefault();
            foreach (ScriptContextRule rule in table.Rules)
            {
                Assert.IsNull(table.Check(rule.Member, ScriptContext.Legacy), rule.Member);
            }
        }

        [Test]
        public void ModuleContext_IsACallerBug()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                ScriptContextRuleTable.CreateDefault().Check("UserInputService", ScriptContext.Shared));
        }

        [Test]
        public void UnknownMember_HasNoRule()
        {
            ScriptContextRuleTable table = ScriptContextRuleTable.CreateDefault();
            Assert.IsNull(table.Check("Workspace.Gravity", ScriptContext.Client));
            Assert.IsNull(table.Check("Players.LocalPlayer", ScriptContext.Server));
            Assert.IsFalse(table.TryGet(null, out ScriptContextRule _));
            Assert.IsFalse(table.TryGet(string.Empty, out ScriptContextRule _));
        }

        [Test]
        public void ExactRowBeatsTheClassRow()
        {
            ScriptContextRuleTable table = new ScriptContextRuleTable();
            table.Add(new ScriptContextRule("Widget", ScriptSide.Client, "test", "use a client script"));
            table.Add(new ScriptContextRule("Widget.Save", ScriptSide.Server, "test", "use a server script"));
            Assert.IsNull(table.Check("Widget.Save", ScriptContext.Server));
            Assert.IsNotNull(table.Check("Widget.Save", ScriptContext.Client));
            Assert.IsNotNull(table.Check("Widget.Draw", ScriptContext.Server));
            Assert.IsNull(table.Check("Widget.Draw", ScriptContext.Client));
        }

        [TestCase("")]
        [TestCase(" Widget")]
        [TestCase("Widget.")]
        [TestCase(".Draw")]
        [TestCase("A.B.C")]
        [TestCase("Widget Draw")]
        public void Rule_MalformedMember_IsRefused(string member)
        {
            Assert.Throws<ArgumentException>(() =>
                new ScriptContextRule(member, ScriptSide.Client, "test", "fix"));
        }

        [Test]
        public void Rule_WithoutReferenceOrFix_IsRefused()
        {
            Assert.Throws<ArgumentException>(() => new ScriptContextRule("Widget", ScriptSide.Client, " ", "fix"));
            Assert.Throws<ArgumentException>(() => new ScriptContextRule("Widget", ScriptSide.Client, "test", null));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new ScriptContextRule("Widget", (ScriptSide)7, "test", "fix"));
        }
    }
}
