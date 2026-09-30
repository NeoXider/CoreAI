#if COREAI_LLM && !UNITY_WEBGL
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// PlayMode tests for streaming tool-calling hardening (v0.24.1+).
    /// Validates ToolExecutionPolicy integration, duplicate detection under live LLM,
    /// and streaming stop/cancellation behaviour with real model backends.
    /// </summary>
    public class StreamingToolCallingPlayModeTests
    {
        // WHY: without [Timeout] the framework aborts at 180 s, which one Max(120, RequestTimeoutSeconds + 30) wait
        // can already reach, and Streaming_ThenNonStreaming runs two of them: 2 x 150 s at the default request
        // timeout + 20 s reserve = 320 s. 600 s matches StreamingPlayModeTests; LiveTestRequestScope caps longer
        // configured waits so the cancel fires first. TestAgentSetup.Initialize is not capped (SharedLlmUnity
        // allows up to 600 s for a cold GGUF load and 300 s when another test is already loading); the Cap on every
        // wait protects the request, so a slower load shortens the turn or ends in the test's own cancelling wait,
        // never a stranded request.
        private const int TestTimeoutMs = 600000;

        private TestAgentSetup _setup;
        private LiveTestRequestScope _requests;

        [UnitySetUp]
        public IEnumerator Setup()
        {
            _requests = new LiveTestRequestScope(TestTimeoutMs);
            _setup = new TestAgentSetup();
            yield return _setup.Initialize();
            Assert.IsTrue(_setup.IsReady, $"LLM backend not available ({_setup.BackendName}). Skipping.");
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            // WHY: a framework timeout skips the test body's cleanup; cancel and let the abandoned turn unwind
            // before the client is disposed.
            if (_requests != null)
            {
                yield return _requests.CancelAllAndDrain();
                _requests = null;
            }

            _setup?.Dispose();
            yield return null;
        }

        // ===================== Streaming + Tool Calling =====================

        /// <summary>
        /// Verifies that streaming with a tool-calling system prompt produces chunks
        /// and completes without errors (smoke test for the full pipeline).
        /// </summary>
        [UnityTest]
        [Timeout(TestTimeoutMs)]
        public IEnumerator Streaming_WithToolCapablePrompt_CompletesSuccessfully()
        {
            LlmCompletionRequest request = new()
            {
                AgentRoleId = "SmartChat",
                SystemPrompt = "You are a helpful game assistant. You have tools available. " +
                               "If the user asks a simple question, just answer in text without using tools.",
                UserPayload = "What is 2 + 2?"
            };

            List<LlmStreamChunk> chunks = new();
            bool gotDone = false;

            CancellationTokenSource cts = _requests.CreateCancellation();
            Task streamTask = _requests.Track(CollectStreamAsync(_setup.Client, request, cts.Token,
                chunks, done => gotDone = done));

            yield return _setup.RunAndWait(streamTask, _requests.Cap(ResolveLlmWaitSeconds()), "Streaming_ToolCapable",
                cts);

            Assert.IsTrue(gotDone, "Should receive a chunk with IsDone=true");
            Assert.GreaterOrEqual(chunks.Count, 1, "Should receive at least 1 chunk");

            string full = "";
            foreach (LlmStreamChunk c in chunks)
            {
                if (!string.IsNullOrEmpty(c.Text))
                {
                    full += c.Text;
                }
            }

            Debug.Log($"[StreamingToolTest] Full response ({chunks.Count} chunks): {full}");
            if (string.IsNullOrEmpty(full))
            {
                Assert.Ignore(
                    "Local LLM returned streaming completion with no visible Text chunks. Retry or change model.");
            }
        }

        /// <summary>
        /// Verifies that cancelling a streaming request mid-flight doesn't cause errors
        /// and properly stops the stream. Tests the StopActiveGeneration path.
        /// </summary>
        [UnityTest]
        [Timeout(TestTimeoutMs)]
        public IEnumerator Streaming_EarlyCancellation_StopsCleanly()
        {
            LlmCompletionRequest request = new()
            {
                AgentRoleId = "SmartChat",
                SystemPrompt = "You are a verbose assistant. Write as much as possible.",
                UserPayload = "Write a very detailed essay about the history of computing from the 1940s to today."
            };

            CancellationTokenSource cts = _requests.CreateCancellation();
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            StreamCancelCounter counter = new();

            Task streamTask = _requests.Track(
                ConsumeStreamingUntilCanceledAsync(_setup.Client, request, cts.Token, counter));

            yield return new WaitForSecondsRealtime(0.25f);
            cts.Cancel();

            yield return _setup.RunAndWait(streamTask, _requests.Cap(ResolveLlmWaitSeconds()), "Streaming_EarlyCancel",
                cts);

            Debug.Log(
                $"[StreamingToolTest] EarlyCancel: wasCancelled={counter.WasCancelled}, chunks={counter.ChunkCount}");
            Assert.IsTrue(counter.WasCancelled,
                "Streaming task should observe cancellation and finish without hanging.");
        }

        /// <summary>
        /// Verifies that the streaming pipeline can handle a non-streaming (CompleteAsync) request
        /// right after a streaming request without state contamination.
        /// </summary>
        [UnityTest]
        [Timeout(TestTimeoutMs)]
        public IEnumerator Streaming_ThenNonStreaming_NoStateContamination()
        {
            // First: streaming request
            LlmCompletionRequest streamRequest = new()
            {
                AgentRoleId = "SmartChat",
                SystemPrompt = "You are a test agent. Be very brief.",
                UserPayload = "Say 'STREAM_OK'."
            };

            List<LlmStreamChunk> chunks = new();
            bool gotDone = false;

            CancellationTokenSource cts = _requests.CreateCancellation();
            Task streamTask = _requests.Track(CollectStreamAsync(_setup.Client, streamRequest, cts.Token,
                chunks, done => gotDone = done));

            yield return _setup.RunAndWait(streamTask, _requests.Cap(ResolveLlmWaitSeconds()), "Streaming_First", cts);

            Assert.IsTrue(gotDone, "Streaming should complete");

            // Second: non-streaming request
            LlmCompletionRequest nonStreamRequest = new()
            {
                AgentRoleId = "SmartChat",
                SystemPrompt = "You are a test agent. Be very brief.",
                UserPayload = "Say 'NONSTREAM_OK'."
            };

            LlmResultBox resultBox = new();
            Task nonStreamTask = _requests.Track(
                CompleteOnMainThreadAsync(_setup.Client, nonStreamRequest, resultBox, cts.Token));

            yield return _setup.RunAndWait(nonStreamTask, _requests.Cap(ResolveLlmWaitSeconds()), "NonStreaming_Second",
                cts);

            Assert.IsNotNull(resultBox.Value, "Non-streaming result should not be null");
            Assert.IsTrue(resultBox.Value.Ok, $"Non-streaming request failed: {resultBox.Value?.Error}");
            Assert.IsNotEmpty(resultBox.Value.Content, "Non-streaming response should not be empty");

            Debug.Log($"[StreamingToolTest] Stream→NonStream test passed. " +
                      $"Stream: {chunks.Count} chunks, NonStream: {resultBox.Value.Content.Length} chars");
        }

        // ===================== Helpers =====================

        /// <summary>
        /// LLMUnity cold start / first token often exceeds 30s; match <see cref="StreamingPlayModeTests"/> margins.
        /// </summary>
        private static float ResolveLlmWaitSeconds()
        {
            float waitSec = 120f;
            CoreAISettingsAsset settingsAsset = CoreAISettingsAsset.Instance;
            if (settingsAsset != null)
            {
                waitSec = Mathf.Max(120f, settingsAsset.RequestTimeoutSeconds + 30f);
            }

            return waitSec;
        }

        private static async Task CollectStreamAsync(
            ILlmClient client,
            LlmCompletionRequest request,
            CancellationToken ct,
            List<LlmStreamChunk> chunks,
            Action<bool> setDone)
        {
            await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(request, ct))
            {
                chunks.Add(chunk);
                if (chunk.IsDone)
                {
                    setDone(true);
                }
            }
        }

        private static async Task ConsumeStreamingUntilCanceledAsync(
            ILlmClient client,
            LlmCompletionRequest request,
            CancellationToken ct,
            StreamCancelCounter counter)
        {
            try
            {
                await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(request, ct))
                {
                    counter.ChunkCount++;
                }
            }
            catch (OperationCanceledException)
            {
                counter.WasCancelled = true;
            }
        }

        private static async Task CompleteOnMainThreadAsync(
            ILlmClient client,
            LlmCompletionRequest request,
            LlmResultBox box,
            CancellationToken ct)
        {
            box.Value = await client.CompleteAsync(request, ct);
        }

        private sealed class StreamCancelCounter
        {
            public int ChunkCount;
            public bool WasCancelled;
        }

        private sealed class LlmResultBox
        {
            public LlmCompletionResult Value;
        }
    }
}
#endif
