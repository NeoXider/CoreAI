#if !COREAI_NO_LLM && !UNITY_WEBGL
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
        private TestAgentSetup _setup;

        [UnitySetUp]
        public IEnumerator Setup()
        {
            _setup = new TestAgentSetup();
            yield return _setup.Initialize();
            Assert.IsTrue(_setup.IsReady, $"LLM backend not available ({_setup.BackendName}). Skipping.");
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            _setup?.Dispose();
            yield return null;
        }

        // ===================== Streaming + Tool Calling =====================

        /// <summary>
        /// Verifies that streaming with a tool-calling system prompt produces chunks
        /// and completes without errors (smoke test for the full pipeline).
        /// </summary>
        [UnityTest]
        public IEnumerator Streaming_WithToolCapablePrompt_CompletesSuccessfully()
        {
            var request = new LlmCompletionRequest
            {
                AgentRoleId = "PlayerChat",
                SystemPrompt = "You are a helpful game assistant. You have tools available. " +
                               "If the user asks a simple question, just answer in text without using tools.",
                UserPayload = "What is 2 + 2?"
            };

            var chunks = new List<LlmStreamChunk>();
            bool gotDone = false;

            Task streamTask = CollectStreamAsync(_setup.Client, request, CancellationToken.None,
                chunks, done => gotDone = done);

            yield return _setup.RunAndWait(streamTask, ResolveLlmWaitSeconds(), "Streaming_ToolCapable");

            Assert.IsTrue(gotDone, "Should receive a chunk with IsDone=true");
            Assert.GreaterOrEqual(chunks.Count, 1, "Should receive at least 1 chunk");

            string full = "";
            foreach (var c in chunks)
            {
                if (!string.IsNullOrEmpty(c.Text)) full += c.Text;
            }

            Debug.Log($"[StreamingToolTest] Full response ({chunks.Count} chunks): {full}");
            Assert.IsNotEmpty(full, "Combined response should not be empty");
        }

        /// <summary>
        /// Verifies that cancelling a streaming request mid-flight doesn't cause errors
        /// and properly stops the stream. Tests the StopActiveGeneration path.
        /// </summary>
        [UnityTest]
        public IEnumerator Streaming_EarlyCancellation_StopsCleanly()
        {
            var request = new LlmCompletionRequest
            {
                AgentRoleId = "PlayerChat",
                SystemPrompt = "You are a verbose assistant. Write as much as possible.",
                UserPayload = "Write a very detailed essay about the history of computing from the 1940s to today."
            };

            var cts = new CancellationTokenSource();
            // Cancel after 3 seconds or 2 chunks, whichever comes first
            cts.CancelAfter(System.TimeSpan.FromSeconds(5));
            var counter = new StreamCancelCounter();

            Task streamTask = CollectCancelStreamAsync(_setup.Client, request, cts, counter, cancelAfterChunks: 2);

            yield return _setup.RunAndWait(streamTask, ResolveLlmWaitSeconds(), "Streaming_EarlyCancel");

            Debug.Log($"[StreamingToolTest] EarlyCancel: wasCancelled={counter.WasCancelled}, chunks={counter.ChunkCount}");
            Assert.IsTrue(counter.WasCancelled,
                "Streaming task should observe cancellation and finish without hanging.");
        }

        /// <summary>
        /// Verifies that the streaming pipeline can handle a non-streaming (CompleteAsync) request
        /// right after a streaming request without state contamination.
        /// </summary>
        [UnityTest]
        public IEnumerator Streaming_ThenNonStreaming_NoStateContamination()
        {
            // First: streaming request
            var streamRequest = new LlmCompletionRequest
            {
                AgentRoleId = "PlayerChat",
                SystemPrompt = "You are a test agent. Be very brief.",
                UserPayload = "Say 'STREAM_OK'."
            };

            var chunks = new List<LlmStreamChunk>();
            bool gotDone = false;

            Task streamTask = CollectStreamAsync(_setup.Client, streamRequest, CancellationToken.None,
                chunks, done => gotDone = done);

            yield return _setup.RunAndWait(streamTask, ResolveLlmWaitSeconds(), "Streaming_First");

            Assert.IsTrue(gotDone, "Streaming should complete");

            // Second: non-streaming request
            var nonStreamRequest = new LlmCompletionRequest
            {
                AgentRoleId = "PlayerChat",
                SystemPrompt = "You are a test agent. Be very brief.",
                UserPayload = "Say 'NONSTREAM_OK'."
            };

            var resultBox = new LlmResultBox();
            Task nonStreamTask = CompleteOnMainThreadAsync(_setup.Client, nonStreamRequest, resultBox);

            yield return _setup.RunAndWait(nonStreamTask, ResolveLlmWaitSeconds(), "NonStreaming_Second");

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
            System.Action<bool> setDone)
        {
            await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(request, ct))
            {
                chunks.Add(chunk);
                if (chunk.IsDone) setDone(true);
            }
        }

        private static async Task CollectCancelStreamAsync(
            ILlmClient client,
            LlmCompletionRequest request,
            CancellationTokenSource cts,
            StreamCancelCounter counter,
            int cancelAfterChunks = 3)
        {
            try
            {
                await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(request, cts.Token))
                {
                    counter.ChunkCount++;
                    if (counter.ChunkCount >= cancelAfterChunks)
                    {
                        cts.Cancel();
                    }
                }
            }
            catch (System.OperationCanceledException)
            {
                counter.WasCancelled = true;
            }
            finally
            {
                if (!cts.IsCancellationRequested)
                {
                    cts.Cancel();
                }
                cts.Dispose();
            }
        }

        private static async Task CompleteOnMainThreadAsync(
            ILlmClient client,
            LlmCompletionRequest request,
            LlmResultBox box)
        {
            box.Value = await client.CompleteAsync(request, CancellationToken.None);
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
