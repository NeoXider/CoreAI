using System.Collections;
using System.Reflection;
using System.Threading;
using CoreAI.Chat;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    public sealed class CoreAiChatPanelStopPlayModeTests
    {
        [UnityTest]
        [Timeout(120000)]
        public IEnumerator StopAgent_WhenStreamingRequestActive_CancelsCtsAndUnlocksUiState()
        {
            GameObject go = new("CoreAiChatPanel_StopAgent_PlayMode_Test");
            go.SetActive(false);

            CoreAiChatPanel panel = go.AddComponent<CoreAiChatPanel>();
            CancellationTokenSource rootCts = new();
            CancellationTokenSource activeRequestCts = new();

            SetPrivateField(panel, "_cts", rootCts);
            SetPrivateField(panel, "_isSending", true);
            SetPrivateField(panel, "_isStreaming", true);
            SetPrivateField(panel, "_activeRequestCts", activeRequestCts);

            yield return null;

            panel.StopAgent();

            yield return null;

            Assert.IsTrue(activeRequestCts.IsCancellationRequested, "Active streaming/request CTS should be cancelled.");
            Assert.IsTrue(rootCts.IsCancellationRequested, "Root CTS should be cancelled and replaced by StopAgent().");
            Assert.IsFalse(GetPrivateField<bool>(panel, "_isSending"), "Chat panel should no longer be sending.");
            Assert.IsFalse(GetPrivateField<bool>(panel, "_isStreaming"), "Chat panel should no longer be streaming.");
            Assert.IsNotNull(GetPrivateField<CancellationTokenSource>(panel, "_cts"), "Root CTS should be recreated for future sends.");

            Object.DestroyImmediate(go);
        }

        private static void SetPrivateField<T>(CoreAiChatPanel panel, string fieldName, T value)
        {
            FieldInfo field = typeof(CoreAiChatPanel).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, $"Private field not found: {fieldName}");
            field.SetValue(panel, value);
        }

        private static T GetPrivateField<T>(CoreAiChatPanel panel, string fieldName)
        {
            FieldInfo field = typeof(CoreAiChatPanel).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, $"Private field not found: {fieldName}");
            return (T)field.GetValue(panel);
        }
    }
}
