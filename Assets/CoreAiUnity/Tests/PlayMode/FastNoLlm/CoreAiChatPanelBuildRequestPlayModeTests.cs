using System.Collections;
using CoreAI.Ai;
using CoreAI.Authority;
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
            public AiTaskRequest InvokeBuildAiTaskRequest(string userText, string roleId)
            {
                return BuildAiTaskRequest(userText, roleId);
            }
        }

        private sealed class PanelWithAllowedTools : CoreAiChatPanel
        {
            protected override AiTaskRequest BuildAiTaskRequest(string userText, string roleId)
            {
                AiTaskRequest r = base.BuildAiTaskRequest(userText, roleId);
                r.AllowedToolNames = new[] { "custom_tool_a" };
                return r;
            }

            public AiTaskRequest InvokeBuildAiTaskRequest(string userText, string roleId)
            {
                return BuildAiTaskRequest(userText, roleId);
            }
        }

        [UnityTest]
        [Timeout(60000)]
        public IEnumerator BuildAiTaskRequest_ZeroConfiguration_InPlayMode_UsesCompositionDefaultAndMapsRequest()
        {
            GameObject go = new("ChatPanel_PlayMode_BuildRequest_Default");
            go.SetActive(false);

            PanelForTesting panel = go.AddComponent<PanelForTesting>();
            yield return null;

            // WHY: keeping the object inactive avoids UI bindings; request construction has no UI dependency.

            AiTaskRequest req = panel.InvokeBuildAiTaskRequest(" hello ", "Merchant");
            AiTaskRequest nextReq = panel.InvokeBuildAiTaskRequest("again", "BlueprintBot");

            Assert.AreEqual("Merchant", req.RoleId);
            Assert.AreEqual(" hello ", req.Hint);
            Assert.AreEqual("Chat", req.SourceTag);
            Assert.AreEqual(LlmToolChoiceMode.Auto, req.ForcedToolMode);
            Assert.IsNull(req.AllowedToolNames);
            Assert.IsTrue(req.ActorContext.HasValue);
            Assert.IsTrue(nextReq.ActorContext.HasValue);
            ActorContext actor = req.ActorContext.Value;
            ActorContext nextActor = nextReq.ActorContext.Value;
            Assert.AreEqual(LocalActorIdentityProvider.DefaultActorId, actor.ActorId);
            Assert.IsTrue(actor.Grants.IsUnrestricted);
            Assert.AreEqual(actor.SessionId, req.CancellationScope);
            Assert.AreEqual(actor.SessionId, nextActor.SessionId);

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
