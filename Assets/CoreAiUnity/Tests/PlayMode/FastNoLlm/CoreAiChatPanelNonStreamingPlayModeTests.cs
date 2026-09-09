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
        [UnityTest]
        public IEnumerator TypedBufferedFailure_IsAdmittedWithoutCompletionEventOrLegacyExecution()
        {
            GameObject go = new("TypedBufferedFailure");
            go.SetActive(false);
            PanelHarness panel = go.AddComponent<PanelHarness>();
            panel.SetRuntimeOptions(new CoreAiChatOptions { RoleId = "Teacher", EnableStreaming = false,
                WelcomeMessage = "", LoadPersistedChatOnStartup = false, EnableCameraTool = false });
            BufferedTypedOrchestrator provider = new();
            panel.ChatService = new CoreAiChatService(provider);
            go.SetActive(true);
            int completed = 0;
            panel.OnAiResponseCompleted += _ => completed++;
            try
            {
                Task<CoreAiChatExternalSubmitResult> task = panel.SubmitMessageFromExternalResultAsync("help",
                    new CoreAiChatExternalSubmitOptions { AppendUserMessageToChat = false });
                float deadline = Time.realtimeSinceStartup + 8;
                while (!task.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
                Assert.IsTrue(task.IsCompleted, "Buffered typed submission must finish.");
                Assert.IsFalse(task.IsFaulted);
                Assert.IsTrue(task.Result.Admitted);
                Assert.IsFalse(task.Result.Completion.Ok);
                Assert.AreEqual(LlmErrorCode.RateLimited, task.Result.Completion.ErrorCode);
                Assert.AreEqual("partial", task.Result.Completion.Content);
                Assert.AreEqual(31, task.Result.Completion.TotalTokens);
                Assert.AreEqual(1, provider.Calls);
                Assert.AreEqual(0, completed);
                Assert.IsFalse(panel.IsBusy);
                go.SetActive(false);
                Task<CoreAiChatExternalSubmitResult> inactive = panel.SubmitMessageFromExternalResultAsync("later");
                Assert.IsTrue(inactive.IsCompleted);
                Assert.IsFalse(inactive.Result.Admitted);
                Assert.AreEqual(CoreAiChatExternalSubmitRejection.Inactive, inactive.Result.Rejection);
                Assert.AreEqual(1, provider.Calls);
            }
            finally { Object.DestroyImmediate(go); }
        }

        private sealed class BufferedTypedOrchestrator : IAiOrchestrationService, IAiTaskResultService
        {
            public int Calls;
            public Task<LlmCompletionResult> RunTaskResultAsync(AiTaskRequest task, CancellationToken token = default)
            {
                Calls++;
                return Task.FromResult(new LlmCompletionResult { Ok = false, Content = "partial", Error = "limited",
                    ErrorCode = LlmErrorCode.RateLimited, HttpStatus = 429, TotalTokens = 31 });
            }
            public Task<string> RunTaskAsync(AiTaskRequest task, CancellationToken token = default) =>
                throw new System.InvalidOperationException("Legacy execution must not run.");
            public void CancelTasks(string scope) { }
        }

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
            // WHY: This no-UIDocument harness tests the turn pipeline, not OnEnable binding. Explicitly
            // model an active lifecycle so the production inactive-panel guard does not short-circuit it.
            SetPrivateField(panel, "_lifecycleActive", true);

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
            // WHY: Exercise the formatter's empty-result branch rather than the inactive-panel early return.
            SetPrivateField(panel, "_lifecycleActive", true);

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
