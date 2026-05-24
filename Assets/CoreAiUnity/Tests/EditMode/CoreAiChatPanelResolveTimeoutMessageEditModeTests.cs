using CoreAI.Chat;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>EditMode coverage for <see cref="CoreAiChatPanel.ResolveTimeoutMessage"/> hook.</summary>
    public sealed class CoreAiChatPanelResolveTimeoutMessageEditModeTests
    {
        private class PanelProbe : CoreAiChatPanel
        {
            public string Call(bool stopByUser)
            {
                return ResolveTimeoutMessage(stopByUser);
            }
        }

        private sealed class PanelSuppressTimeout : PanelProbe
        {
            protected override string ResolveTimeoutMessage(bool stopRequestedByUser)
            {
                return stopRequestedByUser ? base.ResolveTimeoutMessage(true) : null;
            }
        }

        [Test]
        public void ResolveTimeoutMessage_Default_TimeoutBranch_UsesConfigOrFallback()
        {
            GameObject go = new();
            PanelProbe panel = go.AddComponent<PanelProbe>();
            Assert.AreEqual("Timeout.", panel.Call(false));
            Object.DestroyImmediate(go);
        }

        [Test]
        public void ResolveTimeoutMessage_OverrideCanReturnNullForTimeoutBranch()
        {
            GameObject go = new();
            PanelSuppressTimeout panel = go.AddComponent<PanelSuppressTimeout>();
            Assert.IsNull(panel.Call(false));
            Assert.IsFalse(string.IsNullOrEmpty(panel.Call(true)));
            Object.DestroyImmediate(go);
        }
    }
}