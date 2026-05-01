using System.Collections;
using CoreAI.Ai;
using CoreAI.Chat;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// PlayMode companions to <c>CoreAiChatPanelBuildRequestEditModeTests</c> (EditMode assembly) —
    /// verifies <see cref="CoreAiChatPanel.BuildAiTaskRequest"/> under a Unity player tick
    /// (lifecycle differs from EditMode; no LLM required).
    /// </summary>
    public sealed class CoreAiChatPanelBuildRequestPlayModeTests
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

        [UnityTest]
        [Timeout(60000)]
        public IEnumerator BuildAiTaskRequest_Default_InPlayMode_MapsHintRoleAndChatSourceTag()
        {
            GameObject go = new("ChatPanel_PlayMode_BuildRequest_Default");
            go.SetActive(false);

            PanelForTesting panel = go.AddComponent<PanelForTesting>();
            yield return null;

            // Keep the GameObject inactive: OnEnable() requires UITK UIDocument/visual tree bindings;
            // BuildAiTaskRequest does not depend on UI (same pattern as CoreAiChatPanelStopPlayModeTests).

            AiTaskRequest req = panel.InvokeBuildAiTaskRequest(" hello ", "Merchant");

            Assert.AreEqual("Merchant", req.RoleId);
            Assert.AreEqual(" hello ", req.Hint);
            Assert.AreEqual("Chat", req.SourceTag);
            Assert.AreEqual(LlmToolChoiceMode.Auto, req.ForcedToolMode);
            Assert.IsNull(req.AllowedToolNames);

            Object.DestroyImmediate(go);
        }

        [UnityTest]
        [Timeout(60000)]
        public IEnumerator BuildAiTaskRequest_SubclassInPlayMode_PreservesAllowedToolNames()
        {
            GameObject go = new("ChatPanel_PlayMode_BuildRequest_Override");
            go.SetActive(false);

            PanelWithAllowedTools panel = go.AddComponent<PanelWithAllowedTools>();
            yield return null;

            AiTaskRequest req = panel.InvokeBuildAiTaskRequest("go", "BlueprintBot");

            Assert.AreEqual("BlueprintBot", req.RoleId);
            Assert.AreEqual("go", req.Hint);
            Assert.IsNotNull(req.AllowedToolNames);
            Assert.AreEqual(1, req.AllowedToolNames!.Length);
            Assert.AreEqual("custom_tool_a", req.AllowedToolNames[0]);

            Object.DestroyImmediate(go);
        }
    }
}
