using CoreAI.Ai;
using CoreAI.Chat;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Documents and guards that <see cref="CoreAiChatPanel"/> routing is driven by RoleId /
    /// <see cref="BuildAiTaskRequest"/> — custom roles and tool policy injections are host concerns.
    /// </summary>
    [TestFixture]
    public sealed class CoreAiChatPanelBuildRequestEditModeTests
    {
        private sealed class PanelForTesting : CoreAiChatPanel
        {
            public AiTaskRequest InvokeBuildAiTaskRequest(string userText, string roleId) =>
                BuildAiTaskRequest(userText, roleId);
        }

        private sealed class PanelWithAllowedTools : CoreAiChatPanel
        {
            protected override AiTaskRequest BuildAiTaskRequest(string userText, string roleId)
            {
                AiTaskRequest r = base.BuildAiTaskRequest(userText, roleId);
                r.AllowedToolNames = new[] { "custom_tool_a" };
                return r;
            }

            public AiTaskRequest InvokeBuildAiTaskRequest(string userText, string roleId) =>
                BuildAiTaskRequest(userText, roleId);
        }

        [Test]
        public void BuildAiTaskRequest_Default_MapsHintRoleAndChatSourceTag()
        {
            GameObject go = new("ChatPanel_BuildRequest_Default");
            try
            {
                PanelForTesting panel = go.AddComponent<PanelForTesting>();
                AiTaskRequest req = panel.InvokeBuildAiTaskRequest(" hello ", "Merchant");

                Assert.AreEqual("Merchant", req.RoleId);
                Assert.AreEqual(" hello ", req.Hint);
                Assert.AreEqual("Chat", req.SourceTag);
                Assert.AreEqual(LlmToolChoiceMode.Auto, req.ForcedToolMode);
                Assert.IsNull(req.AllowedToolNames);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void BuildAiTaskRequest_SubclassInjected_AllowedToolNames_Preserved()
        {
            GameObject go = new("ChatPanel_BuildRequest_Override");
            try
            {
                PanelWithAllowedTools panel = go.AddComponent<PanelWithAllowedTools>();
                AiTaskRequest req = panel.InvokeBuildAiTaskRequest("go", "BlueprintBot");

                Assert.AreEqual("BlueprintBot", req.RoleId);
                Assert.AreEqual("go", req.Hint);
                Assert.IsNotNull(req.AllowedToolNames);
                Assert.AreEqual(1, req.AllowedToolNames!.Length);
                Assert.AreEqual("custom_tool_a", req.AllowedToolNames[0]);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }
    }
}
