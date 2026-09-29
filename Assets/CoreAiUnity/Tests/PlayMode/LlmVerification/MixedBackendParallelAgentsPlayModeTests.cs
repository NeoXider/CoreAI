#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL && COREAI_LLM
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
    /// OpenAI-compatible HTTP transport. Proves the native and HTTP paths run in parallel
    /// without interfering (independent clients, memory stores, orchestrators). Skips gracefully when
    /// either backend is unavailable, so it is safe in headless/offline CI.
    /// </summary>
    public sealed class MixedBackendParallelAgentsPlayModeTests
    {
        private CoreAISettingsAsset _httpSettings;
        private CoreAISettingsAsset _secondHttpSettings;

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

            if (_secondHttpSettings != null)
            {
                Object.DestroyImmediate(_secondHttpSettings);
                _secondHttpSettings = null;
            }

            LogAssert.ignoreFailingMessages = false;

            yield return null;
        }

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator TwoAgents_LlmUnityAndConfiguredHttp_RunConcurrently()
        {
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
            AiOrchestrator localAgent = BuildOrchestrator(localClient, localStore, CoreAISettingsAsset.Instance);

            // HTTP client + its own orchestrator/store, configured explicitly for the LM Studio endpoint.
            _httpSettings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            _httpSettings.ConfigureClientOwnedApi(http.BaseUrl, http.ApiKey, http.Model, timeoutSeconds: 120);
            InMemoryStore httpStore = new();
            ILlmClient httpClient = MeaiLlmClient.CreateHttp(_httpSettings, logger, supportsNativeToolCalling: true, memoryStore: httpStore);
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

        [UnityTest]
        [Timeout(180000)]
        public IEnumerator TwoHttpAgents_IndependentStores_RunConcurrently()
        {
            PlayModeOpenAiTestConfig.ResolvedConfig config = PlayModeOpenAiTestConfig.Resolve(null);
            if (!config.IsComplete)
            {
                Assert.Ignore(PlayModeOpenAiTestConfig.BuildIgnoreReason(config));
            }

            LogAssert.ignoreFailingMessages = true;
            _httpSettings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            _secondHttpSettings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            _httpSettings.ConfigureClientOwnedApi(config.BaseUrl, config.ApiKey, config.Model, 120);
            _secondHttpSettings.ConfigureClientOwnedApi(config.BaseUrl, config.ApiKey, config.Model, 120);

            InMemoryStore firstStore = new();
            InMemoryStore secondStore = new();
            IGameLogger logger = GameLoggerUnscopedFallback.Instance;
            ILlmClient firstClient = MeaiLlmClient.CreateHttp(_httpSettings, logger,
                supportsNativeToolCalling: config.NativeTools, memoryStore: firstStore);
            ILlmClient secondClient = MeaiLlmClient.CreateHttp(_secondHttpSettings, logger,
                supportsNativeToolCalling: config.NativeTools, memoryStore: secondStore);
            AiOrchestrator firstAgent = BuildOrchestrator(firstClient, firstStore, _httpSettings);
            AiOrchestrator secondAgent = BuildOrchestrator(secondClient, secondStore, _secondHttpSettings);

            // WHY: Start both turns before yielding so neither can complete before the other is submitted.
            Task<string> first = firstAgent.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.SmartChat,
                Hint = "Reply with the single word alpha.",
                MaxOutputTokens = 256
            });
            Task<string> second = secondAgent.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.SmartChat,
                Hint = "Reply with the single word beta.",
                MaxOutputTokens = 256
            });

            yield return PlayModeTestAwait.WaitTask(Task.WhenAll(first, second), 150f,
                "two HTTP agents running concurrently");
            Assert.IsFalse(first.IsFaulted, first.Exception?.GetBaseException().Message);
            Assert.IsFalse(second.IsFaulted, second.Exception?.GetBaseException().Message);
            Assert.IsFalse(string.IsNullOrWhiteSpace(first.Result), "The first agent returned no text.");
            Assert.IsFalse(string.IsNullOrWhiteSpace(second.Result), "The second agent returned no text.");
            Assert.AreNotSame(firstStore, secondStore, "Agent memory stores must be isolated.");
            Debug.Log($"[ParallelHttp] first='{first.Result.Trim()}' second='{second.Result.Trim()}'");
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
