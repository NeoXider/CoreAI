using System;
using System.Collections;
using System.Collections.Generic;
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
        /// <summary>
        /// : MeaiLlmClient.CreateHttp       .
        /// </summary>
        [UnityTest]
        [Timeout(300000)]
        public IEnumerator MeaiLlmClient_CreateHttp_ShouldCreateAndConnect()
        {
            PlayModeOpenAiTestConfig.ResolvedConfig config = PlayModeOpenAiTestConfig.Resolve();
            if (!config.IsComplete)
            {
                Assert.Ignore(PlayModeOpenAiTestConfig.BuildIgnoreReason(config));
            }

            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            settings.ConfigureHttpApi(config.BaseUrl, config.ApiKey, config.Model, 0.2f, 120);
            try
            {
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

                Task<LlmCompletionResult> task = client.CompleteAsync(request);
                yield return PlayModeTestAwait.WaitTask(task, 120f, "MeaiLlmClient HTTP request");

                LlmCompletionResult result = task.Result;
                Assert.IsTrue(result.Ok, $"HTTP request failed: {result.Error}");
                Assert.IsFalse(string.IsNullOrWhiteSpace(result.Content),
                    "HTTP response content should not be empty");
            }
            finally
            {
                LogAssert.ignoreFailingMessages = false;
                UnityEngine.Object.DestroyImmediate(settings);
            }
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
