using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Chat;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// PlayMode coverage for <see cref="CoreAiChatPanel"/> non-streaming path (WebGL-stable default):
    /// <see cref="CoreAiChatPanel.SubmitMessageFromExternalAsync"/> must complete, fire
    /// <see cref="CoreAiChatPanel.OnAiResponseCompleted"/> and clear <c>_isSending</c> without a bound UIDocument
    /// (typing/message UI is skipped when elements are null; response callbacks still run).
    /// </summary>
    public sealed class CoreAiChatPanelNonStreamingPlayModeTests
    {
        private sealed class PanelHarness : CoreAiChatPanel
        {
            public void AssignTest(CoreAiChatConfig cfg, CoreAiChatService svc)
            {
                config = cfg;
                ChatService = svc;
            }
        }

        private sealed class StubOrchestrator : IAiOrchestrationService
        {
            private readonly string _text;

            public StubOrchestrator(string text)
            {
                _text = text;
            }

            public Task<string> RunTaskAsync(AiTaskRequest request, CancellationToken ct = default)
            {
                return Task.FromResult(_text ?? "");
            }

            public async IAsyncEnumerable<LlmStreamChunk> RunStreamingAsync(
                AiTaskRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken ct = default)
            {
                yield return new LlmStreamChunk { Text = _text ?? "" };
                yield return new LlmStreamChunk { IsDone = true };
                await Task.CompletedTask;
            }

            public void CancelTasks(string scopeId)
            {
            }
        }

        private static void SetPrivateField<T>(CoreAiChatPanel panel, string fieldName, T value)
        {
            FieldInfo field =
                typeof(CoreAiChatPanel).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, $"Field {fieldName}");
            field.SetValue(panel, value);
        }

        private static T GetPrivateField<T>(CoreAiChatPanel panel, string fieldName)
        {
            FieldInfo field =
                typeof(CoreAiChatPanel).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, $"Field {fieldName}");
            return (T)field.GetValue(panel);
        }

        private static CoreAiChatConfig CreateChatConfig(bool streaming)
        {
            CoreAiChatConfig cfg = ScriptableObject.CreateInstance<CoreAiChatConfig>();
            FieldInfo f =
                typeof(CoreAiChatConfig).GetField("_enableStreaming", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(f);
            f.SetValue(cfg, streaming);
            return cfg;
        }

        [UnityTest]
        [Timeout(120000)]
        public IEnumerator SubmitMessageFromExternal_NonStreaming_CompletesAndInvokesOnAiResponseCompleted()
        {
            GameObject go = new("CoreAiChatPanel_NonStreaming_Test");
            go.SetActive(false);

            PanelHarness panel = go.AddComponent<PanelHarness>();
            CoreAiChatConfig cfg = CreateChatConfig(false);
            StubOrchestrator orchestrator = new("Hello from stub LLM");
            CoreAiChatService svc = new(orchestrator);
            panel.AssignTest(cfg, svc);
            SetPrivateField(panel, "_cts", new CancellationTokenSource());

            string captured = null;
            panel.OnAiResponseCompleted += reply => captured = reply;

            Task<string?> work = panel.SubmitMessageFromExternalAsync(
                "ping",
                new CoreAiChatExternalSubmitOptions { AppendUserMessageToChat = false },
                CancellationToken.None);

            while (!work.IsCompleted)
            {
                yield return null;
            }

            Assert.IsFalse(work.IsFaulted, work.Exception?.ToString());
            Assert.AreEqual("Hello from stub LLM", work.Result);
            Assert.AreEqual("Hello from stub LLM", captured);
            Assert.IsFalse(GetPrivateField<bool>(panel, "_isSending"), "_isSending must be false after turn.");

            Object.DestroyImmediate(cfg);
            Object.DestroyImmediate(go);
        }

        private sealed class PanelEmptyFormat : CoreAiChatPanel
        {
            public void AssignTest(CoreAiChatConfig cfg, CoreAiChatService svc)
            {
                config = cfg;
                ChatService = svc;
            }

            protected override string FormatResponseText(string rawText)
            {
                return string.Empty;
            }
        }

        [UnityTest]
        [Timeout(120000)]
        public IEnumerator SubmitMessageFromExternal_NonStreaming_EmptyFormat_YieldsNullAndClearsSending()
        {
            GameObject go = new("CoreAiChatPanel_NonStreaming_EmptyFmt_Test");
            go.SetActive(false);

            PanelEmptyFormat panel = go.AddComponent<PanelEmptyFormat>();
            CoreAiChatConfig cfg = CreateChatConfig(false);
            StubOrchestrator orchestrator = new("ignored body");
            CoreAiChatService svc = new(orchestrator);
            panel.AssignTest(cfg, svc);
            SetPrivateField(panel, "_cts", new CancellationTokenSource());

            int completionCalls = 0;
            panel.OnAiResponseCompleted += _ => completionCalls++;

            Task<string?> work = panel.SubmitMessageFromExternalAsync(
                "ping",
                new CoreAiChatExternalSubmitOptions { AppendUserMessageToChat = false },
                CancellationToken.None);

            while (!work.IsCompleted)
            {
                yield return null;
            }

            Assert.IsFalse(work.IsFaulted, work.Exception?.ToString());
            Assert.IsNull(work.Result, "Empty FormatResponseText → null return path.");
            Assert.AreEqual(0, completionCalls, "OnAiResponseCompleted must not fire when formatted text is empty.");
            Assert.IsFalse(GetPrivateField<bool>(panel, "_isSending"));

            Object.DestroyImmediate(cfg);
            Object.DestroyImmediate(go);
        }
    }
}