using CoreAI.Ai;
using CoreAI.Chat;
using CoreAI.Hub.UI;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// The default Hub chat can build: with no chat configuration wired (the shipped Hub prefab wires none) it
    /// answers as the Programmer role, which carries the Lua and world tools. A wired configuration keeps its own
    /// role, and the standalone chat default (<see cref="CoreAiChatOptions.DefaultRoleId"/>) stays SmartChat, which
    /// the panel and config tests pin.
    /// </summary>
    [TestFixture]
    public sealed class HubChatPageDefaultRoleEditModeTests
    {
        [Test]
        public void NoChatConfig_UsesProgrammer_AndShowsToolCalls()
        {
            HubChatPage page = new();

            CoreAiChatOptions options = HubChatPage.BuildRuntimeOptions(null, page.DefaultRoleId);

            Assert.AreEqual(BuiltInAgentRoleIds.Programmer, page.DefaultRoleId);
            Assert.AreEqual(BuiltInAgentRoleIds.Programmer, options.RoleId);
            Assert.IsTrue(options.ShowToolCallsInChat);
        }

        [Test]
        public void WiredChatConfig_KeepsItsOwnRole()
        {
            CoreAiChatConfig config = ScriptableObject.CreateInstance<CoreAiChatConfig>();
            try
            {
                CoreAiChatOptions options = HubChatPage.BuildRuntimeOptions(config, BuiltInAgentRoleIds.Programmer);

                Assert.AreEqual(config.RoleId, options.RoleId, "An explicit host choice wins over the Hub default.");
                Assert.AreEqual(BuiltInAgentRoleIds.SmartChat, options.RoleId);
                Assert.IsTrue(options.ShowToolCallsInChat);
            }
            finally
            {
                Object.DestroyImmediate(config);
            }
        }

        [Test]
        public void HostCanReplaceTheHubDefault()
        {
            HubChatPage page = new() { DefaultRoleId = " AINpc " };

            Assert.AreEqual("AINpc", HubChatPage.BuildRuntimeOptions(null, page.DefaultRoleId).RoleId);
        }

        [Test]
        public void StandaloneChatDefault_StaysSmartChat()
        {
            Assert.AreEqual(BuiltInAgentRoleIds.SmartChat, CoreAiChatOptions.DefaultRoleId);
            Assert.AreEqual(BuiltInAgentRoleIds.SmartChat, CoreAiChatOptions.CreateDefault().RoleId);
        }
    }
}
