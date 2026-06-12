using System;
using CoreAI.Ai;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode coverage for the strongly-typed <see cref="RoleId"/> wrapper around agent role strings.
    /// </summary>
    public sealed class RoleIdEditModeTests
    {
        [Test]
        public void BuiltInStatics_MatchBuiltInAgentRoleIds()
        {
            Assert.AreEqual(BuiltInAgentRoleIds.Creator, RoleId.Creator.Value);
            Assert.AreEqual(BuiltInAgentRoleIds.Analyzer, RoleId.Analyzer.Value);
            Assert.AreEqual(BuiltInAgentRoleIds.Programmer, RoleId.Programmer.Value);
            Assert.AreEqual(BuiltInAgentRoleIds.AiNpc, RoleId.AiNpc.Value);
            Assert.AreEqual(BuiltInAgentRoleIds.CoreMechanic, RoleId.CoreMechanic.Value);
            Assert.AreEqual(BuiltInAgentRoleIds.PlainChat, RoleId.PlainChat.Value);
            Assert.AreEqual(BuiltInAgentRoleIds.SmartChat, RoleId.SmartChat.Value);
            Assert.AreEqual(BuiltInAgentRoleIds.Merchant, RoleId.Merchant.Value);
        }

        [Test]
        public void ImplicitConversions_RoundTripThroughString()
        {
            RoleId fromString = "Blacksmith";
            string backToString = fromString;

            Assert.AreEqual("Blacksmith", backToString);
            Assert.IsFalse(fromString.IsEmpty);
            Assert.IsFalse(fromString.IsBuiltIn);
            Assert.IsTrue(RoleId.SmartChat.IsBuiltIn);
        }

        [Test]
        public void Equality_IsOrdinalOnValue()
        {
            Assert.AreEqual(new RoleId("SmartChat"), RoleId.SmartChat);
            Assert.IsTrue(new RoleId("A") != new RoleId("a"));
            Assert.AreEqual(RoleId.Merchant.GetHashCode(), new RoleId("Merchant").GetHashCode());
        }

        [Test]
        public void DefaultAndNullString_AreEmptyAndDoNotThrow()
        {
            RoleId fromNull = (string)null;
            RoleId fromBlank = "   ";

            Assert.IsTrue(default(RoleId).IsEmpty);
            Assert.IsTrue(fromNull.IsEmpty);
            Assert.IsTrue(fromBlank.IsEmpty);
            Assert.AreEqual(string.Empty, default(RoleId).Value);
            Assert.AreEqual(string.Empty, default(RoleId).ToString());
        }

        [Test]
        public void Constructor_RejectsEmpty()
        {
            Assert.Throws<ArgumentException>(() => _ = new RoleId(null));
            Assert.Throws<ArgumentException>(() => _ = new RoleId("  "));
        }

        [Test]
        public void RoleId_FlowsIntoStringBasedApis()
        {
            // The whole point: existing string-based APIs accept RoleId without overloads.
            AgentBuilder builder = new(RoleId.Merchant);
            Assert.IsNotNull(builder);

            AiTaskRequest request = new() { RoleId = RoleId.SmartChat };
            Assert.AreEqual("SmartChat", request.RoleId);
        }
    }
}