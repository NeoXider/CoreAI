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
    /// PlayMode   streaming API  3-  .
    ///   LLM  (HTTP  LLMUnity).
    /// </summary>
    public class StreamingPlayModeTests
    {
        private TestAgentSetup _setup;

        [UnitySetUp]
        public IEnumerator Setup()
        {
            _setup = new TestAgentSetup();
            yield return _setup.Initialize();
            Assert.IsTrue(_setup.IsReady, $"LLM   ({_setup.BackendName}).  .");
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            _setup?.Dispose();
            yield return null;
        }

        // ===================== Streaming =====================

        [UnityTest]
        public IEnumerator Streaming_ReturnsChunks_WithDoneFlag()
        {
            var request = new LlmCompletionRequest
            {
                AgentRoleId = "PlayerChat",
                SystemPrompt = "You are a helpful assistant. Be very brief.",
                UserPayload = "Say hello in exactly 3 words."
            };

            var chunks = new List<LlmStreamChunk>();
            bool gotDone = false;

            // :     main thread  UnityWebRequest    ThreadPool.
            //  async-  ( Task.Run),  continuations
            //   UnitySynchronizationContext.
            Task streamTask = CollectStreamAsync(_setup.Client, request, CancellationToken.None,
                chunks, done => gotDone = done);

            yield return _setup.RunAndWait(streamTask, 30f, "Streaming");

            Assert.IsTrue(gotDone, "Should receive a chunk with IsDone=true");
            Assert.GreaterOrEqual(chunks.Count, 1, "Should receive at least 1 chunk");

            string full = "";
            foreach (var c in chunks)
            {
                if (!string.IsNullOrEmpty(c.Text)) full += c.Text;
            }

            Debug.Log($"[StreamingTest] Full response ({chunks.Count} chunks): {full}");
            Assert.IsNotEmpty(full, "Combined response should not be empty");
        }

        [UnityTest]
        public IEnumerator Streaming_CancellationToken_StopsStream()
        {
            var request = new LlmCompletionRequest
            {
                AgentRoleId = "PlayerChat",
                SystemPrompt = "You are a helpful assistant.",
                UserPayload = "Write a very long essay about the history of computing in detail."
            };

            var cts = new CancellationTokenSource();
            // Fallback-cancel: если модель долго не выдает чанки, всё равно должны прервать поток.
            cts.CancelAfter(System.TimeSpan.FromSeconds(8));
            var counter = new StreamCancelCounter();

            //    3- .   main thread.
            Task streamTask = CollectCancelStreamAsync(_setup.Client, request, cts, counter);

            yield return _setup.RunAndWait(streamTask, 30f, "Streaming_Cancel");

            Debug.Log($"[StreamingTest] Cancellation: wasCancelled={counter.WasCancelled}, chunks={counter.ChunkCount}");
            Assert.IsTrue(counter.WasCancelled,
                "Streaming task should observe cancellation and finish without hanging.");
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
            StreamCancelCounter counter)
        {
            try
            {
                await foreach (LlmStreamChunk chunk in client.CompleteStreamingAsync(request, cts.Token))
                {
                    counter.ChunkCount++;
                    if (counter.ChunkCount >= 3)
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

        private sealed class StreamCancelCounter
        {
            public int ChunkCount;
            public bool WasCancelled;
        }

        private sealed class LlmResultBox
        {
            public LlmCompletionResult Value;
        }

        private static async Task CompleteOnMainThreadAsync(
            ILlmClient client,
            LlmCompletionRequest request,
            LlmResultBox box)
        {
            box.Value = await client.CompleteAsync(request, CancellationToken.None);
        }

        // ===================== 3-Layer Prompt =====================

        [UnityTest]
        public IEnumerator ThreeLayerPrompt_AllLayersApplied()
        {
            // Setup 3 layers
            string universalPrefix = "RULE: Always respond with 'LAYER1_OK' at the start.";
            string basePrompt = "You are a test agent. Always include 'LAYER2_OK'.";
            string additionalPrompt = "Also include 'LAYER3_OK' in your response.";

            // Create composer with all 3 layers
            var provider = new SingleRolePromptProvider("TestStreaming", basePrompt);
            _setup.Policy.SetAdditionalSystemPrompt("TestStreaming", additionalPrompt);

            var settings = new TestSettings { UniversalSystemPromptPrefix = universalPrefix };
            var composer = new AiPromptComposer(provider,
                new NoAgentUserPromptTemplateProvider(),
                new NullLuaScriptVersionStore(), null, _setup.Policy, settings);

            string composedPrompt = composer.GetSystemPrompt("TestStreaming");
            Debug.Log($"[ThreeLayerTest] Composed prompt:\n{composedPrompt}");

            // Verify composition
            Assert.That(composedPrompt, Does.Contain("LAYER1_OK"), "Layer 1 missing");
            Assert.That(composedPrompt, Does.Contain("LAYER2_OK"), "Layer 2 missing");
            Assert.That(composedPrompt, Does.Contain("LAYER3_OK"), "Layer 3 missing");

            // Test with real LLM
            var request = new LlmCompletionRequest
            {
                AgentRoleId = "TestStreaming",
                SystemPrompt = composedPrompt,
                UserPayload = "Respond now."
            };

            var resultBox = new LlmResultBox();
            Task task = CompleteOnMainThreadAsync(_setup.Client, request, resultBox);

            yield return _setup.RunAndWait(task, 30f, "ThreeLayerPrompt");

            LlmCompletionResult result = resultBox.Value;

            Assert.IsNotNull(result);
            Assert.IsTrue(result.Ok, $"Request failed: {result?.Error}");
            Assert.IsNotEmpty(result.Content);

            Debug.Log($"[ThreeLayerTest] LLM response: {result.Content}");
            // Note: LLM may not perfectly follow all 3 instructions,
            // but the composed prompt itself has already been verified above.
        }

        // ===================== Streaming + Think Block =====================

        [UnityTest]
        public IEnumerator Streaming_ThinkBlocks_StrippedFromResponse()
        {
            // Модели могут писать think в <think> (content) либо отдельно как delta.reasoning_content;
            // MeaiLlmClient/ThinkBlockStreamFilter убирают теги; HTTP-клиент не прокидывает reasoning в UI.
            var request = new LlmCompletionRequest
            {
                AgentRoleId = "PlayerChat",
                SystemPrompt = "Think step by step using <think> tags before responding. Keep your answer brief.",
                UserPayload = "What is 2+2?"
            };

            var chunks = new List<LlmStreamChunk>();
            var response = new System.Text.StringBuilder();

            Task streamTask = CollectStreamAsync(_setup.Client, request, CancellationToken.None, chunks,
                _ => { });

            // Ожидание не меньше UnityWebRequest (RequestTimeoutSeconds) + запас: длинный reasoning_content
            // съедает тот же wall-time, что и генерация, и тест не должен обрываться раньше HTTP.
            float waitSec = 180f;
            CoreAISettingsAsset asset = CoreAISettingsAsset.Instance;
            if (asset != null)
            {
                waitSec = Mathf.Max(180f, asset.RequestTimeoutSeconds + 30f);
            }

            yield return _setup.RunAndWait(streamTask, waitSec, "Streaming_ThinkBlock");

            foreach (var c in chunks)
            {
                if (!string.IsNullOrEmpty(c.Text)) response.Append(c.Text);
            }

            string fullResponse = response.ToString();
            Debug.Log($"[ThinkBlockTest] Response ({chunks.Count} chunks): {fullResponse}");

            Assert.That(fullResponse, Does.Not.Contain("<think>"),
                "Think blocks should be stripped from streaming output");
            Assert.That(fullResponse, Does.Not.Contain("</think>"),
                "Closing think tag should be stripped too");
        }

        // ===================== Helpers =====================

        private class SingleRolePromptProvider : IAgentSystemPromptProvider
        {
            private readonly string _roleId;
            private readonly string _prompt;
            public SingleRolePromptProvider(string roleId, string prompt) { _roleId = roleId; _prompt = prompt; }
            public bool TryGetSystemPrompt(string roleId, out string prompt)
            {
                if (roleId == _roleId) { prompt = _prompt; return true; }
                prompt = null; return false;
            }
        }

        private class TestSettings : ICoreAISettings
        {
            public string UniversalSystemPromptPrefix { get; set; } = "";
            public float Temperature { get; set; } = 0.1f;
            public int ContextWindowTokens => 8192;
            public int MaxLuaRepairRetries => 3;
            public int MaxToolCallRetries => 3;
            public bool AllowDuplicateToolCalls => false;
            public bool EnableHttpDebugLogging => false;
            public bool LogMeaiToolCallingSteps => false;
            public bool EnableMeaiDebugLogging => false;
            public float LlmRequestTimeoutSeconds => 15f;
            public int MaxLlmRequestRetries => 2;
            public bool LogTokenUsage => false;
            public bool LogLlmLatency => false;
            public bool LogLlmConnectionErrors => false;
            public bool LogToolCalls => false;
            public bool LogToolCallArguments => false;
            public bool LogToolCallResults => false;
            public bool EnableStreaming { get; set; } = true;
        }
    }
}
#endif

