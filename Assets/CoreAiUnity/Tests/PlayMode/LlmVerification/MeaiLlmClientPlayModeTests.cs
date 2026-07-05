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
#if !COREAI_NO_LLM && !UNITY_WEBGL
    public sealed class MeaiLlmClientPlayModeTests
    {
        /// <summary>
        /// : MeaiLlmClient.CreateHttp       .
        /// </summary>
        [UnityTest]
        [Timeout(300000)]
        public IEnumerator MeaiLlmClient_CreateHttp_ShouldCreateAndConnect()
        {
            //    CoreAISettingsAsset
            CoreAISettingsAsset settings = CoreAISettingsAsset.Instance;
            if (settings == null)
            {
                Assert.Ignore("CoreAISettingsAsset not found in Resources");
            }

            //   HTTP   
            if (settings.BackendType != LlmBackendType.OpenAiHttp && settings.BackendType != LlmBackendType.Auto)
            {
                Assert.Ignore("Backend is not HTTP. Current: " + settings.BackendType);
            }

            Debug.Log("[MeaiLlmClient.HTTP] Creating HTTP client...");
            IGameLogger logger = GameLoggerUnscopedFallback.Instance;
            InMemoryStore store = new();

            MeaiLlmClient client = MeaiLlmClient.CreateHttp(settings, logger, store);
            Assert.IsNotNull(client, "MeaiLlmClient.CreateHttp should not return null");

            Debug.Log("[MeaiLlmClient.HTTP] Client created, sending request...");
            LogAssert.ignoreFailingMessages = true;

            LlmCompletionRequest request = new()
            {
                AgentRoleId = "TestAgent",
                SystemPrompt = "You are a test agent. Respond with 'OK'.",
                UserPayload = "Say OK"
            };

            Task<LlmCompletionResult> task = client.CompleteAsync(request);
            yield return PlayModeTestAwait.WaitTask(task, 120f, "MeaiLlmClient HTTP request");

            LlmCompletionResult result = ((Task<LlmCompletionResult>)task).Result;
            Assert.IsTrue(result.Ok, $"HTTP request failed: {result?.Error}");
            Assert.IsFalse(string.IsNullOrWhiteSpace(result.Content), "HTTP response content should not be empty");
            Debug.Log(
                $"[MeaiLlmClient.HTTP] Success: {result.Content?.Substring(0, Mathf.Min(100, result.Content.Length))}");
        }

        /// <summary>
        /// : Factory methods should throw on null arguments.
        /// </summary>
        [Test]
        public void MeaiLlmClient_NullArguments_ShouldThrow()
        {
            IGameLogger logger = GameLoggerUnscopedFallback.Instance;

            Assert.Throws<ArgumentNullException>(() =>
                MeaiLlmClient.CreateHttp((IOpenAiHttpSettings)null,
                    ScriptableObject.CreateInstance<CoreAISettingsAsset>(), logger));
        }
    }
#endif
}