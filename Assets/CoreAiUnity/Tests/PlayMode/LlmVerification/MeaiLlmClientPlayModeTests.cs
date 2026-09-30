using System;
using System.Collections;
using System.Collections.Generic;
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
    /// PlayMode   MeaiLlmClient   MEAI .
    ///     (HTTP  LLMUnity)    pipeline.
    /// </summary>
#if COREAI_LLM && !UNITY_WEBGL
    public sealed class MeaiLlmClientPlayModeTests
    {
        // WHY: one 120 s HTTP request + the 20 s LiveTestRequestScope reserve = 140 s; the existing 300 s budget
        // is kept as margin for a cold remote endpoint.
        private const int TestTimeoutMs = 300000;

        private LiveTestRequestScope _requests;
        private CoreAISettingsAsset _settings;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _requests = new LiveTestRequestScope(TestTimeoutMs);
            yield break;
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

            if (_settings != null)
            {
                UnityEngine.Object.DestroyImmediate(_settings);
                _settings = null;
            }

            LogAssert.ignoreFailingMessages = false;
        }

        /// <summary>
        /// : MeaiLlmClient.CreateHttp       .
        /// </summary>
        [UnityTest]
        [Timeout(TestTimeoutMs)]
        public IEnumerator MeaiLlmClient_CreateHttp_ShouldCreateAndConnect()
        {
            PlayModeOpenAiTestConfig.ResolvedConfig config = PlayModeOpenAiTestConfig.Resolve();
            if (!config.IsComplete)
            {
                Assert.Ignore(PlayModeOpenAiTestConfig.BuildIgnoreReason(config));
            }

            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            // WHY: destroyed in [UnityTearDown] after the request drain; the client reads it until the turn ends.
            _settings = settings;
            settings.ConfigureHttpApi(config.BaseUrl, config.ApiKey, config.Model, 0.2f, 120);
            IGameLogger logger = GameLoggerUnscopedFallback.Instance;
            InMemoryStore store = new();
            MeaiLlmClient client = MeaiLlmClient.CreateHttp(settings, logger,
                supportsNativeToolCalling: config.NativeTools, memoryStore: store);
            Assert.IsNotNull(client, "MeaiLlmClient.CreateHttp should not return null");
            LogAssert.ignoreFailingMessages = true;

            LlmCompletionRequest request = new()
            {
                AgentRoleId = "TestAgent",
                SystemPrompt = "You are a test agent. Answer briefly.",
                UserPayload = "Say OK"
            };

            CancellationTokenSource cts = _requests.CreateCancellation();
            Task<LlmCompletionResult> task = _requests.Track(client.CompleteAsync(request, cts.Token));
            yield return PlayModeTestAwait.WaitTask(task, _requests.Cap(120f), "MeaiLlmClient HTTP request", cts);

            LlmCompletionResult result = task.Result;
            Assert.IsTrue(result.Ok, $"HTTP request failed: {result.Error}");
            Assert.IsFalse(string.IsNullOrWhiteSpace(result.Content),
                "HTTP response content should not be empty");
        }

        /// <summary>
        /// : Factory methods should throw on null arguments.
        /// </summary>
        [Test]
        public void MeaiLlmClient_NullArguments_ShouldThrow()
        {
            IGameLogger logger = GameLoggerUnscopedFallback.Instance;
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            try
            {
                Assert.Throws<ArgumentNullException>(() =>
                    MeaiLlmClient.CreateHttp((IOpenAiHttpSettings)null,
                        settings,
                        logger,
                        supportsNativeToolCalling: true));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }
    }
#endif
}
