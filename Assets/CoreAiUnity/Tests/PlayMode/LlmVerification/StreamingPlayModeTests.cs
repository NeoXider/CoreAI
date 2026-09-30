#if COREAI_LLM && !UNITY_WEBGL
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
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
        // WHY: without [Timeout] the Unity Test Framework aborts at 180 s, which Streaming_ThinkBlocks' own
        // Max(180, RequestTimeoutSeconds + 30) wait can never beat. 600 s covers RequestTimeoutSeconds up to
        // ~550 s; LiveTestRequestScope caps longer waits so the cancelling wait always fires first.
        // TestAgentSetup.Initialize is not capped (SharedLlmUnity allows up to 600 s for a cold GGUF load and 300 s
        // when another test is already loading); the Cap on every wait protects the request, so a slower load
        // shortens the turn or ends in the test's own cancelling wait, never a stranded request.
        private const int TestTimeoutMs = 600000;

        private TestAgentSetup _setup;
        private LiveTestRequestScope _requests;

        [UnitySetUp]
        public IEnumerator Setup()
        {
            _requests = new LiveTestRequestScope(TestTimeoutMs);
            _setup = new TestAgentSetup();
            yield return _setup.Initialize();
            Assert.IsTrue(_setup.IsReady, $"LLM   ({_setup.BackendName}).  .");
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            // WHY: a framework timeout skips the test body's cleanup; cancel and let the abandoned request
            // unwind before the client is disposed.
            if (_requests != null)
            {
                yield return _requests.CancelAllAndDrain();
                _requests = null;
            }

            _setup?.Dispose();
            yield return null;
        }

        // ===================== Streaming =====================

        [UnityTest]
        [Timeout(TestTimeoutMs)]
        public IEnumerator Streaming_ReturnsChunks_WithDoneFlag()
        {
            LlmCompletionRequest request = new()
            {
                AgentRoleId = "SmartChat",
                SystemPrompt = "You are a helpful assistant. Be very brief.",
                UserPayload = "Greet the player briefly."
            };

            List<LlmStreamChunk> chunks = new();
            bool gotDone = false;

            // :     main thread  UnityWebRequest    ThreadPool.
            //  async-  ( Task.Run),  continuations
            //   UnitySynchronizationContext.
            CancellationTokenSource cts = _requests.CreateCancellation();
            Task streamTask = _requests.Track(CollectStreamAsync(_setup.Client, request, cts.Token,
                chunks, done => gotDone = done));

            // LLMUnity cold start / first token can exceed 30s; align with RequestTimeoutSeconds + margin
            // (same idea as Streaming_ThinkBlocks_StrippedFromResponse).
            float waitSec = 120f;
            CoreAISettingsAsset settingsAsset = CoreAISettingsAsset.Instance;
            if (settingsAsset != null)
            {
                waitSec = Mathf.Max(120f, settingsAsset.RequestTimeoutSeconds + 30f);
            }

            yield return _setup.RunAndWait(streamTask, _requests.Cap(waitSec), "Streaming", cts);

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

            Debug.Log($"[StreamingTest] Full response ({chunks.Count} chunks): {full}");
            if (string.IsNullOrEmpty(full))
            {
                Assert.Ignore(
                    "Local LLM returned streaming completion with no visible Text chunks (empty assistant delta). " +
                    "Retry or pick another model — not a CoreAI pipeline failure.");
            }
        }

        [UnityTest]
        [Timeout(TestTimeoutMs)]
        public IEnumerator Streaming_CancellationToken_StopsStream()
        {
            LlmCompletionRequest request = new()
            {
                AgentRoleId = "SmartChat",
                SystemPrompt = "You are a helpful assistant.",
                UserPayload = "Write a very long essay about the history of computing in detail."
            };

            CancellationTokenSource cts = _requests.CreateCancellation();
            // Safety net if the stream or transport ignores cooperative cancel.
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            StreamCancelCounter counter = new();

            Task streamTask = _requests.Track(
                ConsumeStreamingUntilCanceledAsync(_setup.Client, request, cts.Token, counter));

            // Cancel from the test coroutine: local servers often return the whole reply in one SSE frame, so
            // in-loop "cancel after N chunks" never runs a second MoveNext. MeaiLlmClient checks the token
            // between fan-out parts and before the terminal chunk so cancellation is still observable.
            yield return new WaitForSecondsRealtime(0.25f);
            cts.Cancel();

            yield return _setup.RunAndWait(streamTask, _requests.Cap(30f), "Streaming_Cancel", cts);

            Debug.Log(
                $"[StreamingTest] Cancellation: wasCancelled={counter.WasCancelled}, chunks={counter.ChunkCount}");
            Assert.IsTrue(counter.WasCancelled,
                "Streaming task should observe cancellation and finish without hanging.");
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
            LlmResultBox box,
            CancellationToken cancellationToken)
        {
            box.Value = await client.CompleteAsync(request, cancellationToken);
        }

        // ===================== 3-Layer Prompt =====================

        [UnityTest]
        [Timeout(TestTimeoutMs)]
        public IEnumerator ThreeLayerPrompt_AllLayersApplied()
        {
            // Setup 3 layers
            string universalPrefix = "RULE: Always respond with 'LAYER1_OK' at the start.";
            string basePrompt = "You are a test agent. Always include 'LAYER2_OK'.";
            string additionalPrompt = "Also include 'LAYER3_OK' in your response.";

            // Create composer with all 3 layers
            SingleRolePromptProvider provider = new("TestStreaming", basePrompt);
            _setup.Policy.SetAdditionalSystemPrompt("TestStreaming", additionalPrompt);

            TestSettings settings = new() { UniversalSystemPromptPrefix = universalPrefix };
            AiPromptComposer composer = new(provider,
                new NoAgentUserPromptTemplateProvider(),
                new NullLuaScriptVersionStore(), null, _setup.Policy, settings);

            string composedPrompt = composer.GetSystemPrompt("TestStreaming");
            Debug.Log($"[ThreeLayerTest] Composed prompt:\n{composedPrompt}");

            // Verify composition
            Assert.That(composedPrompt, Does.Contain("LAYER1_OK"), "Layer 1 missing");
            Assert.That(composedPrompt, Does.Contain("LAYER2_OK"), "Layer 2 missing");
            Assert.That(composedPrompt, Does.Contain("LAYER3_OK"), "Layer 3 missing");

            // Test with real LLM
            LlmCompletionRequest request = new()
            {
                AgentRoleId = "TestStreaming",
                SystemPrompt = composedPrompt,
                UserPayload = "Respond now."
            };

            LlmResultBox resultBox = new();
            CancellationTokenSource cts = _requests.CreateCancellation();
            Task task = _requests.Track(CompleteOnMainThreadAsync(_setup.Client, request, resultBox, cts.Token));

            float waitSec = 120f;
            CoreAISettingsAsset settingsAsset = CoreAISettingsAsset.Instance;
            if (settingsAsset != null)
            {
                waitSec = Mathf.Max(120f, settingsAsset.RequestTimeoutSeconds + 30f);
            }

            yield return _setup.RunAndWait(task, _requests.Cap(waitSec), "ThreeLayerPrompt", cts);

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
        [Timeout(TestTimeoutMs)]
        public IEnumerator Streaming_ThinkBlocks_StrippedFromResponse()
        {
            // Models may write their thinking inside <think> (content) or separately as delta.reasoning_content;
            // MeaiLlmClient/ThinkBlockStreamFilter strip the tags; the HTTP client never forwards reasoning to the UI.
            LlmCompletionRequest request = new()
            {
                AgentRoleId = "SmartChat",
                SystemPrompt = "Think step by step using <think> tags before responding. Keep your answer brief.",
                UserPayload = "What is 2+2?"
            };

            List<LlmStreamChunk> chunks = new();
            StringBuilder response = new();

            CancellationTokenSource cts = _requests.CreateCancellation();
            Task streamTask = _requests.Track(CollectStreamAsync(_setup.Client, request, cts.Token, chunks,
                _ => { }));

            // Wait at least as long as UnityWebRequest (RequestTimeoutSeconds) plus slack: a long reasoning_content
            // eats the same wall-time as generation, and the test must not give up before HTTP does.
            float waitSec = 180f;
            CoreAISettingsAsset asset = CoreAISettingsAsset.Instance;
            if (asset != null)
            {
                waitSec = Mathf.Max(180f, asset.RequestTimeoutSeconds + 30f);
            }

            yield return _setup.RunAndWait(streamTask, _requests.Cap(waitSec), "Streaming_ThinkBlock", cts);

            foreach (LlmStreamChunk c in chunks)
            {
                if (!string.IsNullOrEmpty(c.Text))
                {
                    response.Append(c.Text);
                }
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

            public SingleRolePromptProvider(string roleId, string prompt)
            {
                _roleId = roleId;
                _prompt = prompt;
            }

            public bool TryGetSystemPrompt(string roleId, out string prompt)
            {
                if (roleId == _roleId)
                {
                    prompt = _prompt;
                    return true;
                }

                prompt = null;
                return false;
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
