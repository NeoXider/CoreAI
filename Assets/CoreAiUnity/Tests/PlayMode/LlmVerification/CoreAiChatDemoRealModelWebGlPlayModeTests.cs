#if COREAI_LLM
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Chat;
using CoreAI.Infrastructure.Llm;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace CoreAI.Tests.PlayMode
{
    public sealed class CoreAiChatDemoRealModelWebGlPlayModeTests
    {
        [UnityTearDown]
        public IEnumerator UnloadLoadedScenes()
        {
            // Single-mode scene loads otherwise persist past this test and leak their scope into the
            // rest of the PlayMode run.
            yield return PlayModeSceneSandbox.UnloadToEmptyScene();
        }

        private const string LogPrefix = "[CoreAI.Tests.ChatSceneRealModel]";
        private const string SceneName = "CoreAiChatDemo";
        private const string RoleId = "SmartChat";

        [UnityTest]
        [Category("RealLlm")]
        [Category("WebGL")]
        [Timeout(600000)]
        public IEnumerator CoreAiChatDemo_RealModel_StreamsStopAndRecovers()
        {
            yield return SceneManager.LoadSceneAsync(SceneName, LoadSceneMode.Single);
            yield return null;
            yield return null;

            CoreAiChatPanel panel = Object.FindFirstObjectByType<CoreAiChatPanel>();
            Assert.NotNull(panel, $"{LogPrefix} {SceneName} does not contain CoreAiChatPanel.");

            panel.SetCollapsed(false, false);
            yield return WaitForChatService(panel, 10f);

            CoreAiChatService chatService = panel.ChatService;
            if (chatService == null)
            {
                Assert.Ignore($"{LogPrefix} CoreAiChatService is not available in {SceneName}.");
            }

            CoreAISettingsAsset settings = CoreAISettingsAsset.Instance;
            if (settings == null)
            {
                Assert.Ignore($"{LogPrefix} CoreAISettingsAsset is not available in Resources.");
            }

            if (settings.ExecutionMode == LlmExecutionMode.Offline || settings.BackendType == LlmBackendType.Offline)
            {
                Assert.Ignore($"{LogPrefix} CoreAISettingsAsset is configured for Offline mode.");
            }

            if (!chatService.IsStreamingEnabled(RoleId, true))
            {
                Assert.Ignore(
                    $"{LogPrefix} Streaming is disabled for {RoleId}. " +
                    $"WebGlNativeStreaming={settings.WebGlNativeStreaming}, EnableStreaming={settings.EnableStreaming}.");
            }

            Debug.Log(
                $"{LogPrefix} Starting real-model scene test. " +
                $"ExecutionMode={settings.ExecutionMode}, BackendType={settings.BackendType}, " +
                $"Model={settings.ModelName}, WebGlNativeStreaming={settings.WebGlNativeStreaming}");

            CoreAiChatExternalSubmitOptions options = new() { AppendUserMessageToChat = true };

            Task<string> firstTask = Submit(panel,
                "Give a short response about streaming chat.",
                options);
            StreamingTextProbe firstProbe = new();
            yield return WaitForVisibleStreamingText(firstTask, panel, firstProbe, 90f, "first real-model stream");
            Debug.Log($"{LogPrefix} First visible stream: '{TrimForLog(firstProbe.Text)}'");
            yield return WaitTask(firstTask, 120f, "first real-model response");
            Assert.IsFalse(string.IsNullOrWhiteSpace(firstTask.Result), $"{LogPrefix} First response is empty.");
            Debug.Log($"{LogPrefix} First final response: '{TrimForLog(firstTask.Result)}'");

            Task<string> stopTask = Submit(panel,
                "Write a long numbered list from 1 to 80. Keep each line short, but do not stop early.",
                options);
            StreamingTextProbe stopProbe = new();
            yield return WaitForVisibleStreamingText(stopTask, panel, stopProbe, 240f, "cancellable real-model stream");
            if (stopTask.IsCompleted)
            {
                Assert.Ignore(
                    $"{LogPrefix} Real model completed before Stop could cancel it; use a slower model or rerun.");
            }

            Label stoppedLabel = stopProbe.Label;
            string textAtStop = stoppedLabel?.text ?? string.Empty;
            Debug.Log($"{LogPrefix} Stop before text: '{TrimForLog(textAtStop)}'");
            panel.StopAgent();
            yield return WaitTask(stopTask, 120f, "stopped real-model response");
            Assert.IsNull(stopTask.Result, $"{LogPrefix} Stop should cancel the active turn and return null.");
            yield return WaitUntil(() => !panel.IsBusy, 10f, "chat panel unlock after Stop");
            yield return null;
            yield return null;
            Assert.AreEqual(textAtStop, stoppedLabel?.text ?? string.Empty,
                $"{LogPrefix} Streaming text changed after Stop.");
            Debug.Log($"{LogPrefix} Stop cancelled streaming and left chat usable.");

            Task<string> thirdTask = Submit(panel,
                "Give a short response showing chat still works.",
                options);
            yield return WaitTask(thirdTask, 120f, "third real-model response");
            Assert.IsFalse(string.IsNullOrWhiteSpace(thirdTask.Result), $"{LogPrefix} Third response is empty.");
            Assert.IsFalse(panel.IsBusy, $"{LogPrefix} Chat panel stayed busy after third response.");
            Debug.Log($"{LogPrefix} Third final response after Stop: '{TrimForLog(thirdTask.Result)}'");
        }

        private static Task<string> Submit(
            CoreAiChatPanel panel,
            string message,
            CoreAiChatExternalSubmitOptions options)
        {
            return SubmitInner(panel, message, options);

            static async Task<string> SubmitInner(
                CoreAiChatPanel panel,
                string message,
                CoreAiChatExternalSubmitOptions options)
            {
                return await panel.SubmitMessageFromExternalAsync(message, options);
            }
        }

        private static IEnumerator WaitForChatService(CoreAiChatPanel panel, float timeoutSeconds)
        {
            float started = Time.realtimeSinceStartup;
            while (panel.ChatService == null && Time.realtimeSinceStartup - started <= timeoutSeconds)
            {
                yield return null;
            }
        }

        private static IEnumerator WaitForVisibleStreamingText(
            Task<string> task,
            CoreAiChatPanel panel,
            StreamingTextProbe probe,
            float timeoutSeconds,
            string operationName)
        {
            float started = Time.realtimeSinceStartup;
            while (!task.IsCompleted)
            {
                Label label = GetStreamingLabel(panel);
                string text = label?.text ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    probe.Label = label;
                    probe.Text = text;
                    yield break;
                }

                if (Time.realtimeSinceStartup - started > timeoutSeconds)
                {
                    StopActiveTurn(panel, task, operationName);
                    yield return null;
                    Assert.Fail(
                        $"{LogPrefix} Timeout waiting for visible streaming text: {operationName}. " +
                        $"TaskStatus={task.Status}; visibleLabels='{DescribeVisibleLabels(panel)}'. " +
                        "If TaskStatus is still Running and visible labels are empty, the backend may be streaming only reasoning_content " +
                        "or taking too long before the first visible content token. Try Reasoning Mode = Disabled for hybrid-thinking models.");
                }

                yield return null;
            }

            Assert.Fail(
                $"{LogPrefix} {operationName} completed before any streaming text was visible. " +
                $"TaskStatus={task.Status}; visibleLabels='{DescribeVisibleLabels(panel)}'. " +
                $"FinalResult='{DescribeCompletedTaskResult(task)}'. " +
                "If FinalResult is <empty>, the backend likely exhausted generation in reasoning_content and produced no visible assistant content. " +
                "If the task returned non-empty text, the model may have completed between frames before the transient streaming label was observed. " +
                "If the task returned an error, the backend likely failed before the first SSE chunk (for example connection refused/CORS).");
        }

        private static void StopActiveTurn(CoreAiChatPanel panel, Task<string> task, string operationName)
        {
            if (panel == null || task == null || task.IsCompleted || !panel.IsBusy)
            {
                return;
            }

            Debug.LogWarning($"{LogPrefix} Stopping active turn after streaming wait timeout: {operationName}.");
            panel.StopAgent();
        }

        private static IEnumerator WaitTask(Task task, float timeoutSeconds, string operationName)
        {
            float started = Time.realtimeSinceStartup;
            while (!task.IsCompleted)
            {
                if (Time.realtimeSinceStartup - started > timeoutSeconds)
                {
                    Assert.Fail($"{LogPrefix} Timeout waiting for {operationName} after {timeoutSeconds:0.#}s.");
                }

                yield return null;
            }

            if (task.IsCanceled)
            {
                Assert.Fail($"{LogPrefix} Task was canceled: {operationName}.");
            }

            if (task.IsFaulted)
            {
                Assert.Fail(
                    $"{LogPrefix} Task faulted during {operationName}: {task.Exception?.GetBaseException().Message}");
            }
        }

        private static IEnumerator WaitUntil(Func<bool> predicate, float timeoutSeconds, string operationName)
        {
            float started = Time.realtimeSinceStartup;
            while (!predicate())
            {
                if (Time.realtimeSinceStartup - started > timeoutSeconds)
                {
                    Assert.Fail($"{LogPrefix} Timeout waiting for {operationName} after {timeoutSeconds:0.#}s.");
                }

                yield return null;
            }
        }

        private static Label GetStreamingLabel(CoreAiChatPanel panel)
        {
            FieldInfo field = typeof(CoreAiChatPanel).GetField(
                "_streamingLabel",
                BindingFlags.Instance | BindingFlags.NonPublic);
            return field?.GetValue(panel) as Label;
        }

        private static VisualElement GetRoot(CoreAiChatPanel panel)
        {
            FieldInfo field = typeof(CoreAiChatPanel).GetField(
                "Root",
                BindingFlags.Instance | BindingFlags.NonPublic);
            return field?.GetValue(panel) as VisualElement;
        }

        private static string DescribeVisibleLabels(CoreAiChatPanel panel)
        {
            VisualElement root = GetRoot(panel);
            if (root == null)
            {
                return "<no root>";
            }

            List<Label> labels = new();
            root.Query<Label>().ToList(labels);
            List<string> texts = new();
            foreach (Label label in labels)
            {
                string text = label?.text;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    texts.Add(TrimForLog(text));
                }
            }

            return texts.Count == 0 ? "<no labels>" : string.Join(" | ", texts);
        }

        private static string DescribeCompletedTaskResult(Task<string> task)
        {
            if (task == null)
            {
                return "<null task>";
            }

            if (!task.IsCompleted)
            {
                return "<not completed>";
            }

            if (task.IsCanceled)
            {
                return "<canceled>";
            }

            if (task.IsFaulted)
            {
                return $"<faulted: {task.Exception?.GetBaseException().Message}>";
            }

            string result = task.Result;
            return string.IsNullOrWhiteSpace(result) ? "<empty>" : TrimForLog(result);
        }

        private static string TrimForLog(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            const int max = 160;
            string normalized = text.Replace("\r", " ").Replace("\n", " ");
            return normalized.Length <= max ? normalized : normalized.Substring(0, max) + "...";
        }

        private sealed class StreamingTextProbe
        {
            public Label Label;
            public string Text = string.Empty;
        }
    }
}
#endif
