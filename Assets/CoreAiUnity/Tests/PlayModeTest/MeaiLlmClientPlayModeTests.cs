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
    /// PlayMode тест для MeaiLlmClient — единый MEAI клиент.
    /// Проверяет что оба бэкенда (HTTP и LLMUnity) работают через единый pipeline.
    /// </summary>
    public sealed class MeaiLlmClientPlayModeTests
    {
        /// <summary>
        /// Тест: MeaiLlmClient.CreateHttp — создаёт клиент и может отправить запрос.
        /// </summary>
        [UnityTest]
        [Timeout(300000)]
        public IEnumerator MeaiLlmClient_CreateHttp_ShouldCreateAndConnect()
        {
            // Читаем настройки из CoreAISettingsAsset
            CoreAISettingsAsset settings = CoreAISettingsAsset.Instance;
            if (settings == null)
            {
                Assert.Ignore("CoreAISettingsAsset not found in Resources");
            }

            // Если не HTTP режим — пропускаем
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
            LlmCompletionRequest request = new()
            {
                AgentRoleId = "TestAgent",
                SystemPrompt = "You are a test agent. Respond with 'OK'.",
                UserPayload = "Say OK"
            };

            Task<LlmCompletionResult> task = client.CompleteAsync(request);
            yield return PlayModeTestAwait.WaitTask(task, 300f, "MeaiLlmClient HTTP request");

            LlmCompletionResult result = ((System.Threading.Tasks.Task<LlmCompletionResult>)task).Result;
            if (!result.Ok)
            {
                Debug.LogWarning($"[MeaiLlmClient.HTTP] Request failed: {result.Error}");
            }
            else
            {
                Debug.Log(
                    $"[MeaiLlmClient.HTTP] Success: {result.Content?.Substring(0, Mathf.Min(100, result.Content.Length))}");
            }
        }

        /// <summary>
        /// Тест: MeaiLlmClient.CreateLlmUnity — создаёт клиент с локальной моделью.
        /// </summary>
        [UnityTest]
        [Timeout(600000)]
        public IEnumerator MeaiLlmClient_CreateLlmUnity_ShouldCreateAndConnect()
        {
#if COREAI_NO_LLM
            Assert.Ignore("COREAI_NO_LLM defined");
#else
            CoreAISettingsAsset settings = CoreAISettingsAsset.Instance;
            if (settings == null)
            {
                Assert.Ignore("CoreAISettingsAsset not found in Resources");
            }

            // Если не LLMUnity режим — пропускаем
            if (settings.BackendType != LlmBackendType.LlmUnity && settings.BackendType != LlmBackendType.Auto)
            {
                Assert.Ignore("Backend is not LLMUnity. Current: " + settings.BackendType);
            }

            Debug.Log("[MeaiLlmUnity.LLMUnity] Creating LLMUnity client...");

            // Используем настройки из CoreAISettingsAsset
            if (!PlayModeProductionLikeLlmFactory.TryCreate(
                    null, // from settings
                    0.2f,
                    300,
                    out PlayModeProductionLikeLlmHandle handle,
                    out string ignore))
            {
                Assert.Ignore("Failed to create LLM client from settings: " + ignore);
            }

            Debug.Log($"[MeaiLlmUnity] Using backend: {handle.ResolvedBackend}");

            // Только для LLMUnity — ждём готовности модели
            if (handle.ResolvedBackend == PlayModeProductionLikeLlmBackend.LlmUnity)
            {
                Debug.Log("[MeaiLlmUnity.LLMUnity] LLMUnity handle created, waiting for model...");
                yield return PlayModeProductionLikeLlmFactory.EnsureLlmUnityModelReady(handle);
            }

            IGameLogger logger = GameLoggerUnscopedFallback.Instance;
            InMemoryStore store = new();

            MeaiLlmClient client =
                MeaiLlmClient.CreateLlmUnity(handle.Client is MeaiLlmUnityClient mc ? mc.UnityAgent : null, logger,
                    store);
            Assert.IsNotNull(client, "MeaiLlmClient.CreateLlmUnity should not return null");

            Debug.Log("[MeaiLlmClient.LLMUnity] Client created, sending request...");
            LlmCompletionRequest request = new()
            {
                AgentRoleId = "TestAgent",
                SystemPrompt = "You are a test agent. Respond with 'OK'.",
                UserPayload = "Say OK"
            };

            Task<LlmCompletionResult> task = client.CompleteAsync(request);
            yield return PlayModeTestAwait.WaitTask(task, 240f, "MeaiLlmClient LLMUnity request");

            LlmCompletionResult result = ((System.Threading.Tasks.Task<LlmCompletionResult>)task).Result;
            if (!result.Ok)
            {
                Debug.LogWarning($"[MeaiLlmClient.LLMUnity] Request failed: {result.Error}");
            }
            else
            {
                Debug.Log(
                    $"[MeaiLlmClient.LLMUnity] Success: {result.Content?.Substring(0, Mathf.Min(100, result.Content.Length))}");
            }

            handle.Dispose();
#endif
        }

        /// <summary>
        /// Тест: Factory methods should throw on null arguments.
        /// </summary>
        [Test]
        public void MeaiLlmClient_NullArguments_ShouldThrow()
        {
            IGameLogger logger = GameLoggerUnscopedFallback.Instance;

            Assert.Throws<System.ArgumentNullException>(() =>
                MeaiLlmClient.CreateHttp((IOpenAiHttpSettings)null, logger));

            Assert.Throws<System.ArgumentNullException>(() =>
                MeaiLlmClient.CreateHttp((CoreAISettingsAsset)null, logger));

            Assert.Throws<System.ArgumentNullException>(() =>
                MeaiLlmClient.CreateLlmUnity(null, logger));
        }
    }
}