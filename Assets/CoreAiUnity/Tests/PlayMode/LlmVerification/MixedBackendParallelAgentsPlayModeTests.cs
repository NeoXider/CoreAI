#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL
using System.Collections;
using System.Threading.Tasks;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using CoreAI.Messaging;
using CoreAI.Session;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// Drives two agents CONCURRENTLY against two DIFFERENT live backends in the same play session:
    /// one turn goes through the native LLMUnity host (local GGUF, llama.cpp), the other through the
    /// OpenAI-compatible HTTP transport (LM Studio). Proves the native and HTTP paths run in parallel
    /// without interfering (independent clients, memory stores, orchestrators). Skips gracefully when
    /// either backend is unavailable, so it is safe in headless/offline CI.
    /// </summary>
    public sealed class MixedBackendParallelAgentsPlayModeTests
    {
        private CoreAISettingsAsset _httpSettings;

        private sealed class NullSink : IAiGameCommandSink
        {
            public void Publish(ApplyAiGameCommand command)
            {
            }
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_httpSettings != null)
            {
                Object.DestroyImmediate(_httpSettings);
                _httpSettings = null;
            }

            yield return null;
        }

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator TwoAgents_LlmUnityAndLmStudioHttp_RunConcurrently()
        {
            // --- Backend A: local GGUF via the native LLMUnity host ---
            yield return SharedLlmUnity.EnsureInitialized();
            if (!SharedLlmUnity.IsReady)
            {
                Assert.Ignore($"LLMUnity host not ready: {SharedLlmUnity.Error}");
            }

            // --- Backend B: OpenAI-compatible HTTP (LM Studio) ---
            PlayModeOpenAiTestConfig.ResolvedConfig http = PlayModeOpenAiTestConfig.Resolve(null);
            if (!http.IsComplete)
            {
                Assert.Ignore(PlayModeOpenAiTestConfig.BuildIgnoreReason(http));
            }

            IGameLogger logger = GameLoggerUnscopedFallback.Instance;
            LogAssert.ignoreFailingMessages = true;

            // LLMUnity client + its own orchestrator/store (uses the LlmUnity-configured Instance settings).
            InMemoryStore localStore = new();
            ILlmClient localClient = SharedLlmUnity.CreateClientWithMemoryStore(localStore);
            Assert.IsNotNull(localClient, "LLMUnity client must be created from the shared native host.");
            AiOrchestrator localAgent = BuildOrchestrator(localClient, localStore, CoreAISettingsAsset.Instance);

            // HTTP client + its own orchestrator/store, configured explicitly for the LM Studio endpoint.
            _httpSettings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            _httpSettings.ConfigureClientOwnedApi(http.BaseUrl, http.ApiKey, http.Model, timeoutSeconds: 120);
            InMemoryStore httpStore = new();
            ILlmClient httpClient = MeaiLlmClient.CreateHttp(_httpSettings, logger, httpStore);
            AiOrchestrator httpAgent = BuildOrchestrator(httpClient, httpStore, _httpSettings);

            // --- Fire BOTH agent turns without awaiting, so they overlap on the wire. ---
            Task<string> localTask = localAgent.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.SmartChat,
                Hint = "Reply with exactly one word: alpha",
                MaxOutputTokens = 128000
            });
            Task<string> httpTask = httpAgent.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.SmartChat,
                Hint = "Reply with exactly one word: beta",
                MaxOutputTokens = 128000
            });

            yield return PlayModeTestAwait.WaitTask(
                Task.WhenAll(localTask, httpTask), 240f, "mixed-backend parallel agents");

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

        private static AiOrchestrator BuildOrchestrator(
            ILlmClient client, IAgentMemoryStore store, CoreAISettingsAsset settings)
        {
            AiPromptComposer composer = new(
                new BuiltInDefaultAgentSystemPromptProvider(),
                new NoAgentUserPromptTemplateProvider(),
                new NullLuaScriptVersionStore());

            AgentMemoryPolicy policy = new();
            TestAgentPolicyDefaults.ApplyToolsAndChatWithMemory(policy, BuiltInAgentRoleIds.SmartChat);

            CoreAISettingsAsset effective = settings != null
                ? settings
                : ScriptableObject.CreateInstance<CoreAISettingsAsset>();

            return new AiOrchestrator(
                new SoloAuthorityHost(),
                client,
                new NullSink(),
                new SessionTelemetryCollector(),
                composer,
                store,
                policy,
                new NoOpRoleStructuredResponsePolicy(),
                new NullAiOrchestrationMetrics(),
                effective,
                new LocalActorIdentityProvider("mixed-backend-parallel-test"));
        }
    }
}
#endif
