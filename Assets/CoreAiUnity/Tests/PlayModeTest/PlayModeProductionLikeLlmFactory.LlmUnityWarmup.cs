using System.Collections;
using CoreAI.Infrastructure.Llm;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using LLMUnity;

namespace CoreAI.Tests.PlayMode
{
    public static partial class PlayModeProductionLikeLlmFactory
    {
        /// <summary>РџРѕСЃР»Рµ <see cref="TryCreate"/> РґР»СЏ Р±СЌРєРµРЅРґР° LLMUnity вЂ” РґРѕР¶РґР°С‚СЊСЃСЏ РїРѕРґРЅСЏС‚РёСЏ РјРѕРґРµР»Рё.</summary>
        public static IEnumerator EnsureLlmUnityModelReady(PlayModeProductionLikeLlmHandle handle)
        {
#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL
            if (handle == null || handle.ResolvedBackend != PlayModeProductionLikeLlmBackend.LlmUnity)
            {
                yield break;
            }

            MeaiLlmUnityClient llmClient = handle.Client as MeaiLlmUnityClient;
            if (llmClient == null)
            {
                Debug.LogWarning("[LLMUnity Test] Could not get LLM client reference");
                yield break;
            }

            // РўРµРјРїРµСЂР°С‚СѓСЂР° РЅР°СЃС‚СЂР°РёРІР°РµС‚СЃСЏ РІ Р°РіРµРЅС‚Рµ
            LLMAgent agent = llmClient.UnityAgent;
            if (agent != null)
            {
                if (agent.temperature != 0.2f)
                {
                    agent.temperature = 0.2f;
                    Debug.Log("[LLMUnity Test] Set LLMAgent temperature to 0.2 for reliable tool calling.");
                }
            }

            LLM llm = llmClient.LLM;
            if (llm == null)
            {
                Debug.LogWarning("[LLMUnity Test] LLM component is null");
                yield break;
            }

            Debug.Log($"[LLMUnity Test] LLM model: {llm.model}");

            string modelPath = LLMManager.GetAssetPath(llm.model);
            Debug.Log($"[LLMUnity Test] Model path: {modelPath}");

            // First, wait for GameObject to be active (triggers Awake)
            Debug.Log("[LLMUnity Test] Waiting for LLM initialization...");

            float timeout = 120f;
            float startTime = Time.realtimeSinceStartup;
            float lastLog = startTime;

            while (!llm.started && !llm.failed)
            {
                float elapsed = Time.realtimeSinceStartup - startTime;
                if (elapsed > timeout)
                {
                    Debug.LogError(
                        $"[LLMUnity Test] Timeout after {elapsed}s, started={llm.started}, failed={llm.failed}");
                    Assert.Fail($"LLMUnity: model did not start within {timeout}s");
                    yield break;
                }

                if (Time.realtimeSinceStartup - lastLog > 10f)
                {
                    Debug.Log($"[LLMUnity Test] Waiting... {elapsed:F1}s, started={llm.started}, failed={llm.failed}");
                    lastLog = Time.realtimeSinceStartup;
                }

                yield return new WaitForSecondsRealtime(1f);
            }

            if (llm.failed)
            {
                Debug.LogError("[LLMUnity Test] LLM failed to load");
                Assert.Fail("LLMUnity: model failed to load");
                yield break;
            }

            Debug.Log("[LLMUnity Test] LLM is ready!");
#else
            yield break;
#endif
        }
    }
}