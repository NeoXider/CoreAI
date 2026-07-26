using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Chat;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    public sealed class CoreAiChatPanelStopPlayModeTests
    {
        [UnityTearDown]
        public IEnumerator UnloadLoadedScenes()
        {
            // Single-mode scene loads otherwise persist past this test and leak their scope into the
            // rest of the PlayMode run.
            yield return PlayModeSceneSandbox.UnloadToEmptyScene();
        }

        private sealed class PanelHarness : CoreAiChatPanel
        {
            public void AssignTest(CoreAiChatConfig cfg, CoreAiChatService svc)
            {
                config = cfg;
                ChatService = svc;
            }
        }

        private sealed class CancelThenRecoverStreamingOrchestrator : IAiOrchestrationService
        {
            private int _streamingCalls;
            private readonly TaskCompletionSource<bool> _secondStreamStarted = new();

            public Task SecondStreamStarted => _secondStreamStarted.Task;

            public Task<string> RunTaskAsync(AiTaskRequest request, CancellationToken ct = default)
            {
                return Task.FromResult(string.Empty);
            }

            public async IAsyncEnumerable<LlmStreamChunk> RunStreamingAsync(
                AiTaskRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken ct = default)
            {
                int call = Interlocked.Increment(ref _streamingCalls);
                if (call == 1)
                {
                    yield return new LlmStreamChunk { Text = "first" };
                    yield return new LlmStreamChunk { IsDone = true };
                    yield break;
                }

                if (call == 2)
                {
                    _secondStreamStarted.TrySetResult(true);
                    while (!ct.IsCancellationRequested)
                    {
                        await Task.Yield();
                    }

                    ct.ThrowIfCancellationRequested();
                    yield break;
                }

                yield return new LlmStreamChunk { Text = "third" };
                yield return new LlmStreamChunk { IsDone = true };
            }

            public void CancelTasks(string scopeId)
            {
            }
        }

        private sealed class SceneChatStreamingProbeOrchestrator : IAiOrchestrationService
        {
            private int _streamingCalls;
            private int _secondTextChunks;
            private readonly TaskCompletionSource<bool> _secondChunkGate = new();

            public int StreamingCalls => _streamingCalls;
            public int FirstTextChunks { get; private set; }
            public int SecondTextChunks => _secondTextChunks;
            public int ThirdTextChunks { get; private set; }
            public bool SecondCancellationObserved { get; private set; }
            public Task SecondChunkGate => _secondChunkGate.Task;

            public Task<string> RunTaskAsync(AiTaskRequest request, CancellationToken ct = default)
            {
                return Task.FromResult(string.Empty);
            }

            public async IAsyncEnumerable<LlmStreamChunk> RunStreamingAsync(
                AiTaskRequest request,
                [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken ct = default)
            {
                int call = Interlocked.Increment(ref _streamingCalls);
                Debug.Log($"[CoreAI.Tests.ChatSceneStreaming] stream call {call} started; hint={request.Hint}");

                if (call == 1)
                {
                    yield return TextChunk(call, "scene-first-a ");
                    FirstTextChunks++;
                    await Task.Yield();
                    yield return TextChunk(call, "scene-first-b");
                    FirstTextChunks++;
                    yield return DoneChunk(call);
                    yield break;
                }

                if (call == 2)
                {
                    yield return TextChunk(call, "scene-stop-a ");
                    Interlocked.Increment(ref _secondTextChunks);
                    await Task.Yield();
                    yield return TextChunk(call, "scene-stop-b ");
                    Interlocked.Increment(ref _secondTextChunks);
                    _secondChunkGate.TrySetResult(true);

                    while (!ct.IsCancellationRequested)
                    {
                        await Task.Yield();
                    }

                    SecondCancellationObserved = true;
                    Debug.Log("[CoreAI.Tests.ChatSceneStreaming] stream call 2 cancellation observed");
                    ct.ThrowIfCancellationRequested();
                    yield break;
                }

                yield return TextChunk(call, "scene-third-a ");
                ThirdTextChunks++;
                await Task.Yield();
                yield return TextChunk(call, "scene-third-b");
                ThirdTextChunks++;
                yield return DoneChunk(call);
            }

            public void CancelTasks(string scopeId)
            {
                Debug.Log($"[CoreAI.Tests.ChatSceneStreaming] CancelTasks({scopeId})");
            }

            private static LlmStreamChunk TextChunk(int call, string text)
            {
                Debug.Log($"[CoreAI.Tests.ChatSceneStreaming] stream call {call} text chunk: {text}");
                return new LlmStreamChunk { Text = text };
            }

            private static LlmStreamChunk DoneChunk(int call)
            {
                Debug.Log($"[CoreAI.Tests.ChatSceneStreaming] stream call {call} done");
                return new LlmStreamChunk { IsDone = true };
            }
        }

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

            Assert.IsTrue(activeRequestCts.IsCancellationRequested,
                "Active streaming/request CTS should be cancelled.");
            Assert.IsTrue(rootCts.IsCancellationRequested, "Root CTS should be cancelled and replaced by StopAgent().");
            Assert.IsFalse(GetPrivateField<bool>(panel, "_isSending"), "Chat panel should no longer be sending.");
            Assert.IsFalse(GetPrivateField<bool>(panel, "_isStreaming"), "Chat panel should no longer be streaming.");
            Assert.IsNotNull(GetPrivateField<CancellationTokenSource>(panel, "_cts"),
                "Root CTS should be recreated for future sends.");

            Object.DestroyImmediate(go);
        }

        [UnityTest]
        [Timeout(120000)]
        public IEnumerator StopAgent_AfterCancellingStreamingRequest_AllowsNextStreamingSubmit()
        {
            GameObject go = new("CoreAiChatPanel_StopAgent_Recover_PlayMode_Test");
            go.SetActive(false);

            PanelHarness panel = go.AddComponent<PanelHarness>();
            CoreAiChatConfig cfg = CreateChatConfig(true);
            CancelThenRecoverStreamingOrchestrator orchestrator = new();
            CoreAiChatService service = new(orchestrator);
            panel.AssignTest(cfg, service);
            SetPrivateField(panel, "_cts", new CancellationTokenSource());

            int completedResponses = 0;
            panel.OnAiResponseCompleted += _ => completedResponses++;

            Task<string?> first = panel.SubmitMessageFromExternalAsync(
                "first",
                new CoreAiChatExternalSubmitOptions { AppendUserMessageToChat = false },
                CancellationToken.None);

            yield return AwaitTask(first);

            Assert.IsFalse(first.IsFaulted, first.Exception?.ToString());
            Assert.AreEqual("first", first.Result);
            Assert.AreEqual(1, completedResponses);
            Assert.IsFalse(panel.IsBusy, "Panel must be idle after first successful streaming turn.");

            Task<string?> cancelled = panel.SubmitMessageFromExternalAsync(
                "cancel this stream",
                new CoreAiChatExternalSubmitOptions { AppendUserMessageToChat = false },
                CancellationToken.None);

            yield return AwaitTask(orchestrator.SecondStreamStarted);

            Assert.IsTrue(panel.IsBusy, "Panel must be busy while the second streaming turn is in flight.");
            Assert.DoesNotThrow(panel.StopAgent, "StopAgent must not throw while cancelling active streaming.");

            yield return AwaitTask(cancelled);

            Assert.IsFalse(cancelled.IsFaulted, cancelled.Exception?.ToString());
            Assert.IsNull(cancelled.Result, "Cancelled streaming turn should return null.");
            Assert.AreEqual(1, completedResponses,
                "Stop must not fire OnAiResponseCompleted for a cancelled turn.");
            Assert.IsFalse(panel.IsBusy, "Panel must unlock after StopAgent cancellation.");

            Task<string?> third = panel.SubmitMessageFromExternalAsync(
                "third",
                new CoreAiChatExternalSubmitOptions { AppendUserMessageToChat = false },
                CancellationToken.None);

            yield return AwaitTask(third);

            Assert.IsFalse(third.IsFaulted, third.Exception?.ToString());
            Assert.AreEqual("third", third.Result);
            Assert.AreEqual(2, completedResponses);
            Assert.IsFalse(panel.IsBusy, "Panel must remain reusable after post-cancel streaming turn.");

            Object.DestroyImmediate(cfg);
            Object.DestroyImmediate(go);
        }

        [UnityTest]
        [Timeout(120000)]
        public IEnumerator CoreAiChatDemoScene_WithStubOrchestrator_StreamsStopsAndStreamsAgain()
        {
            // WHY: yield once before any skip. Assert.Ignore thrown on the very first MoveNext() of a
            // [UnityTest] wedges the PlayMode runner instead of reporting a skip, and a wedged runner
            // blocks every remaining test in the suite.
            yield return null;

            if (!IsSceneInBuildSettings("CoreAiChatDemo"))
            {
                Assert.Ignore("CoreAiChatDemo scene is not in Build Settings; skipping the scene test.");
                yield break;
            }

            SceneManager.LoadScene("CoreAiChatDemo", LoadSceneMode.Single);
            yield return null;
            yield return null;

            CoreAiChatPanel panel = null;
            for (int i = 0; i < 120 && panel == null; i++)
            {
                panel = Object.FindFirstObjectByType<CoreAiChatPanel>();
                yield return null;
            }

            Assert.IsNotNull(panel, "CoreAiChatDemo scene must contain CoreAiChatPanel.");
            panel.SetCollapsed(false, false);

            SceneChatStreamingProbeOrchestrator orchestrator = new();
            panel.ChatService = new CoreAiChatService(orchestrator);

            int completions = 0;
            string lastCompleted = null;
            panel.OnAiResponseCompleted += response =>
            {
                completions++;
                lastCompleted = response;
                Debug.Log($"[CoreAI.Tests.ChatSceneStreaming] completed response {completions}: {response}");
            };

            Debug.Log("[CoreAI.Tests.ChatSceneStreaming] first scene chat send");
            Task<string?> first = panel.SubmitMessageFromExternalAsync(
                "scene first",
                new CoreAiChatExternalSubmitOptions { AppendUserMessageToChat = true },
                CancellationToken.None);

            yield return AwaitTask(first);

            Assert.IsFalse(first.IsFaulted, first.Exception?.ToString());
            Assert.AreEqual("scene-first-a scene-first-b", first.Result);
            Assert.AreEqual(2, orchestrator.FirstTextChunks);
            Assert.AreEqual(1, completions);
            Assert.AreEqual(first.Result, lastCompleted);
            Assert.IsFalse(panel.IsBusy, "Scene chat panel must be idle after first streaming turn.");

            Debug.Log("[CoreAI.Tests.ChatSceneStreaming] second scene chat send; will stop after chunks");
            Task<string?> cancelled = panel.SubmitMessageFromExternalAsync(
                "scene stop",
                new CoreAiChatExternalSubmitOptions { AppendUserMessageToChat = true },
                CancellationToken.None);

            yield return AwaitTask(orchestrator.SecondChunkGate);

            int chunksAtStop = orchestrator.SecondTextChunks;
            Assert.GreaterOrEqual(chunksAtStop, 2, "Second streaming turn must emit visible chunks before Stop.");
            Assert.IsTrue(panel.IsBusy, "Scene chat panel must be busy before StopAgent.");

            panel.StopAgent();

            yield return AwaitTask(cancelled);

            int chunksAfterStop = orchestrator.SecondTextChunks;
            for (int i = 0; i < 10; i++)
            {
                yield return null;
            }

            Assert.IsFalse(cancelled.IsFaulted, cancelled.Exception?.ToString());
            Assert.IsNull(cancelled.Result, "Stopped scene streaming turn should not complete as assistant response.");
            Assert.IsTrue(orchestrator.SecondCancellationObserved,
                "StopAgent must cancel the active scene streaming request.");
            Assert.AreEqual(chunksAtStop, chunksAfterStop,
                "No additional chunks should be emitted after StopAgent cancellation.");
            Assert.AreEqual(1, completions, "Stopped turn must not fire completed response.");
            Assert.IsFalse(panel.IsBusy, "Scene chat panel must unlock after StopAgent.");

            Debug.Log("[CoreAI.Tests.ChatSceneStreaming] third scene chat send after stop");
            Task<string?> third = panel.SubmitMessageFromExternalAsync(
                "scene third",
                new CoreAiChatExternalSubmitOptions { AppendUserMessageToChat = true },
                CancellationToken.None);

            yield return AwaitTask(third);

            Assert.IsFalse(third.IsFaulted, third.Exception?.ToString());
            Assert.AreEqual("scene-third-a scene-third-b", third.Result);
            Assert.AreEqual(2, orchestrator.ThirdTextChunks);
            Assert.AreEqual(2, completions);
            Assert.AreEqual(third.Result, lastCompleted);
            Assert.AreEqual(3, orchestrator.StreamingCalls);
            Assert.IsFalse(panel.IsBusy, "Scene chat panel must remain reusable after stop recovery.");
        }

        /// <summary>
        /// True when a scene with this name is registered in Build Settings. Replaces
        /// <c>Application.CanStreamedLevelBeLoaded</c>, which reports <c>false</c> in the Editor even for
        /// a scene that IS registered and enabled, so the guard above skipped unconditionally.
        /// </summary>
        private static bool IsSceneInBuildSettings(string sceneName)
        {
            for (int i = 0; i < SceneManager.sceneCountInBuildSettings; i++)
            {
                string path = SceneUtility.GetScenePathByBuildIndex(i);
                if (Path.GetFileNameWithoutExtension(path) == sceneName)
                {
                    return true;
                }
            }

            return false;
        }

        private static IEnumerator AwaitTask(Task task)
        {
            const int maxFrames = 1200;
            int frames = 0;
            while (!task.IsCompleted && frames++ < maxFrames)
            {
                yield return null;
            }

            Assert.IsTrue(task.IsCompleted, "Task did not complete within the test frame budget.");
        }

        private static CoreAiChatConfig CreateChatConfig(bool streaming)
        {
            CoreAiChatConfig cfg = ScriptableObject.CreateInstance<CoreAiChatConfig>();
            FieldInfo field =
                typeof(CoreAiChatConfig).GetField("_enableStreaming", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field);
            field.SetValue(cfg, streaming);
            return cfg;
        }

        private static void SetPrivateField<T>(CoreAiChatPanel panel, string fieldName, T value)
        {
            FieldInfo field =
                typeof(CoreAiChatPanel).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, $"Private field not found: {fieldName}");
            field.SetValue(panel, value);
        }

        private static T GetPrivateField<T>(CoreAiChatPanel panel, string fieldName)
        {
            FieldInfo field =
                typeof(CoreAiChatPanel).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, $"Private field not found: {fieldName}");
            return (T)field.GetValue(panel);
        }
    }
}
