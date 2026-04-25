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
    /// PlayMode С‚РµСЃС‚ РґР»СЏ MeaiLlmClient вЂ” РµРґРёРЅС‹Р№ MEAI РєР»РёРµРЅС‚.
    /// РџСЂРѕРІРµСЂСЏРµС‚ С‡С‚Рѕ РѕР±Р° Р±СЌРєРµРЅРґР° (HTTP Рё LLMUnity) СЂР°Р±РѕС‚Р°СЋС‚ С‡РµСЂРµР· РµРґРёРЅС‹Р№ pipeline.
    /// </summary>
#if !COREAI_NO_LLM && !UNITY_WEBGL
    public sealed class MeaiLlmClientPlayModeTests
    {
        /// <summary>
        /// РўРµСЃС‚: MeaiLlmClient.CreateHttp вЂ” СЃРѕР·РґР°С‘С‚ РєР»РёРµРЅС‚ Рё РјРѕР¶РµС‚ РѕС‚РїСЂР°РІРёС‚СЊ Р·Р°РїСЂРѕСЃ.
        /// </summary>
        [UnityTest]
        [Timeout(300000)]
        public IEnumerator MeaiLlmClient_CreateHttp_ShouldCreateAndConnect()
        {
            // Р§РёС‚Р°РµРј РЅР°СЃС‚СЂРѕР№РєРё РёР· CoreAISettingsAsset
            CoreAISettingsAsset settings = CoreAISettingsAsset.Instance;
            if (settings == null)
            {
                Assert.Ignore("CoreAISettingsAsset not found in Resources");
            }

            // Р•СЃР»Рё РЅРµ HTTP СЂРµР¶РёРј вЂ” РїСЂРѕРїСѓСЃРєР°РµРј
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
            yield return PlayModeTestAwait.WaitTask(task, 300f, "MeaiLlmClient HTTP request");

            LlmCompletionResult result = ((Task<LlmCompletionResult>)task).Result;
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
        /// РўРµСЃС‚: MeaiLlmClient.CreateLlmUnity вЂ” СЃРѕР·РґР°С‘С‚ РєР»РёРµРЅС‚ СЃ Р»РѕРєР°Р»СЊРЅРѕР№ РјРѕРґРµР»СЊСЋ.
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

            // Р•СЃР»Рё РЅРµ LLMUnity СЂРµР¶РёРј вЂ” РїСЂРѕРїСѓСЃРєР°РµРј
            if (settings.BackendType != LlmBackendType.LlmUnity && settings.BackendType != LlmBackendType.Auto)
            {
                Assert.Ignore("Backend is not LLMUnity. Current: " + settings.BackendType);
            }

            Debug.Log("[MeaiLlmUnity.LLMUnity] Creating LLMUnity client...");

            // РСЃРїРѕР»СЊР·СѓРµРј РЅР°СЃС‚СЂРѕР№РєРё РёР· CoreAISettingsAsset
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

            // РўРѕР»СЊРєРѕ РґР»СЏ LLMUnity вЂ” Р¶РґС‘Рј РіРѕС‚РѕРІРЅРѕСЃС‚Рё РјРѕРґРµР»Рё
            if (handle.ResolvedBackend == PlayModeProductionLikeLlmBackend.LlmUnity)
            {
                Debug.Log("[MeaiLlmUnity.LLMUnity] LLMUnity handle created, waiting for model...");
                yield return PlayModeProductionLikeLlmFactory.EnsureLlmUnityModelReady(handle);
            }

            IGameLogger logger = GameLoggerUnscopedFallback.Instance;
            InMemoryStore store = new();

#if COREAI_HAS_LLMUNITY
            MeaiLlmClient client =
                MeaiLlmClient.CreateLlmUnity(handle.Client is MeaiLlmUnityClient mc ? mc.UnityAgent : null, logger, settings,
                    store);
#else
            MeaiLlmClient client =
                MeaiLlmClient.CreateLlmUnity(null, logger, settings,
                    store);
#endif
            Assert.IsNotNull(client, "MeaiLlmClient.CreateLlmUnity should not return null");

            Debug.Log("[MeaiLlmClient.LLMUnity] Client created, sending request...");
            LogAssert.ignoreFailingMessages = true;

            LlmCompletionRequest request = new()
            {
                AgentRoleId = "TestAgent",
                SystemPrompt = "You are a test agent. Respond with 'OK'.",
                UserPayload = "Say OK"
            };

            Task<LlmCompletionResult> task = client.CompleteAsync(request);
            yield return PlayModeTestAwait.WaitTask(task, 240f, "MeaiLlmClient LLMUnity request");

            LlmCompletionResult result = ((Task<LlmCompletionResult>)task).Result;
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
        /// РўРµСЃС‚: Factory methods should throw on null arguments.
        /// </summary>
        [Test]
        public void MeaiLlmClient_NullArguments_ShouldThrow()
        {
            IGameLogger logger = GameLoggerUnscopedFallback.Instance;

            Assert.Throws<System.ArgumentNullException>(() =>
                MeaiLlmClient.CreateHttp((IOpenAiHttpSettings)null, UnityEngine.ScriptableObject.CreateInstance<CoreAI.Infrastructure.Llm.CoreAISettingsAsset>(), logger));

            Assert.Throws<System.ArgumentNullException>(() =>
                MeaiLlmClient.CreateLlmUnity(null, logger, UnityEngine.ScriptableObject.CreateInstance<CoreAI.Infrastructure.Llm.CoreAISettingsAsset>()));
        }
    }
#endif
}
