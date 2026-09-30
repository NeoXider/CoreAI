#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL && COREAI_LLM
using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// Drives two agents CONCURRENTLY against two DIFFERENT live backends in the same play session:
    /// one turn goes through the native LLMUnity host (local GGUF, llama.cpp), the other through the
    /// OpenAI-compatible HTTP transport. Proves both turns complete while running in parallel with
    /// independent clients, memory stores and orchestrators. Skips gracefully when either backend is
    /// unavailable, so it is safe in headless/offline CI. The HTTP-only concurrency and isolation checks are in
    /// <see cref="ParallelHttpAgentsPlayModeTests"/>, which needs no LLMUnity.
    /// </summary>
    public sealed class MixedBackendParallelAgentsPlayModeTests
    {
        // WHY: one 240 s concurrent turn + the 5 s cancellation grace + the 20 s LiveTestRequestScope reserve
        // = 265 s; 300 s leaves ~35 s for a warm host. SharedLlmUnity.EnsureInitialized is not capped (up to 600 s
        // for a cold GGUF load, 300 s when another test is already loading), so a cold load can use the whole
        // budget; the turn's wait is capped to what the load left over, which keeps the request out of a
        // framework abort. The HTTP-only pair lives in ParallelHttpAgentsPlayModeTests.
        private const int MixedTestTimeoutMs = 300000;

        private CoreAISettingsAsset _httpSettings;
        private LiveTestRequestScope _requests;

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            // WHY: a framework timeout skips the test body's cleanup, so cancel both abandoned turns here and
            // let them unwind before the settings they read are destroyed.
            if (_requests != null)
            {
                yield return _requests.CancelAllAndDrain();
                _requests = null;
            }

            if (_httpSettings != null)
            {
                UnityEngine.Object.DestroyImmediate(_httpSettings);
                _httpSettings = null;
            }

            LogAssert.ignoreFailingMessages = false;

            yield return null;
        }

        [UnityTest]
        [Timeout(MixedTestTimeoutMs)]
        public IEnumerator TwoAgents_LlmUnityAndConfiguredHttp_RunConcurrently()
        {
            _requests = new LiveTestRequestScope(MixedTestTimeoutMs);
            // WHY: An unavailable local GGUF logs an error before this fixture can skip it.
            LogAssert.ignoreFailingMessages = true;
            // --- Backend A: local GGUF via the native LLMUnity host ---
            yield return SharedLlmUnity.EnsureInitialized();
            if (!SharedLlmUnity.IsReady)
            {
                Assert.Ignore($"LLMUnity host not ready: {SharedLlmUnity.Error}");
            }

            // WHY: Resolve the live-test HTTP endpoint independently of the local GGUF backend.
            PlayModeOpenAiTestConfig.ResolvedConfig http = PlayModeOpenAiTestConfig.Resolve(null);
            if (!http.IsComplete)
            {
                Assert.Ignore(PlayModeOpenAiTestConfig.BuildIgnoreReason(http));
            }

            IGameLogger logger = GameLoggerUnscopedFallback.Instance;

            // LLMUnity client + its own orchestrator/store (uses the LlmUnity-configured Instance settings).
            InMemoryStore localStore = new();
            ILlmClient localClient = SharedLlmUnity.CreateClientWithMemoryStore(localStore);
            Assert.IsNotNull(localClient, "LLMUnity client must be created from the shared native host.");
            AiOrchestrator localAgent = ParallelHttpAgentsPlayModeTests.BuildOrchestrator(localClient, localStore, CoreAISettingsAsset.Instance);

            // HTTP client + its own orchestrator/store, configured explicitly for the LM Studio endpoint.
            _httpSettings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            _httpSettings.ConfigureClientOwnedApi(http.BaseUrl, http.ApiKey, http.Model, timeoutSeconds: 120);
            InMemoryStore httpStore = new();
            ILlmClient httpClient = MeaiLlmClient.CreateHttp(_httpSettings, logger, supportsNativeToolCalling: true, memoryStore: httpStore);
            AiOrchestrator httpAgent = ParallelHttpAgentsPlayModeTests.BuildOrchestrator(httpClient, httpStore, _httpSettings);

            // --- Fire BOTH agent turns without awaiting, so they overlap on the wire. ---
            CancellationTokenSource cts = _requests.CreateCancellation();
            Task<string> localTask = _requests.Track(localAgent.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.SmartChat,
                Hint = "Reply with exactly one word: alpha",
                MaxOutputTokens = 128000
            }, cts.Token));
            Task<string> httpTask = _requests.Track(httpAgent.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.SmartChat,
                Hint = "Reply with exactly one word: beta",
                MaxOutputTokens = 128000
            }, cts.Token));

            yield return PlayModeTestAwait.WaitTask(
                Task.WhenAll(localTask, httpTask), _requests.Cap(240f), "mixed-backend parallel agents", cts);

            Assert.IsTrue(localTask.IsCompleted && httpTask.IsCompleted,
                "Both concurrent agent turns must complete within the timeout.");
            Assert.IsFalse(localTask.IsFaulted,
                $"LLMUnity turn faulted: {localTask.Exception?.GetBaseException().Message}");
            Assert.IsFalse(httpTask.IsFaulted,
                $"HTTP turn faulted: {httpTask.Exception?.GetBaseException().Message}");
            Assert.IsFalse(string.IsNullOrWhiteSpace(localTask.Result),
                "LLMUnity agent produced an empty answer.");
            Assert.IsFalse(string.IsNullOrWhiteSpace(httpTask.Result),
                "HTTP agent produced an empty answer.");

            // Both backends were genuinely live and distinct: native host started, HTTP endpoint targeted.
            Assert.IsTrue(SharedLlmUnity.Llm.started,
                "LLMUnity native host must be started (local backend was live).");
            Assert.IsNotEmpty(_httpSettings.ApiBaseUrl,
                "HTTP backend must target a base URL (remote backend was live).");

            Debug.Log($"[MixedBackend] local='{localTask.Result?.Trim()}' http='{httpTask.Result?.Trim()}'");
        }
    }
}
#endif
