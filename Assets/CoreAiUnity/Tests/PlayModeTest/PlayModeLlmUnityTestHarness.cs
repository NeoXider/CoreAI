#if !COREAI_NO_LLM && !UNITY_WEBGL
using System;
using System.Collections;
using System.Threading.Tasks;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Llm;
using LLMUnity;
using UnityEngine;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// Play Mode: РїРѕРґРЅРёРјР°РµС‚ <see cref="LLM"/> + <see cref="LLMAgent"/> Р±РµР· СЃС†РµРЅС‹ СЃ РїСЂРµС„Р°Р±РѕРј.
    /// Р‘РѕР»СЊС€Рµ РЅРµ РёСЃРїРѕР»СЊР·СѓРµС‚ СЂРµС„Р»РµРєСЃРёСЋ, С‚Р°Рє РєР°Рє РјРµС‚РѕРґС‹ Awake/Start РІ LLMUnity РїСѓР±Р»РёС‡РЅС‹Рµ.
    /// </summary>
    internal static class PlayModeLlmUnityTestHarness
    {
        public static GameObject CreateRuntimeLlmAndAgent(
            string agentName,
            string ggufPath,
            int numGpuLayers,
            out LLM llm,
            out LLMAgent agent)
        {
            llm = null;
            agent = null;

            GameObject go = new("CoreAI_PlayModeTest_LLMUnity");
            go.SetActive(false);

            llm = go.AddComponent<LLM>();
            agent = go.AddComponent<LLMAgent>();
            agent.remote = false;
            agent.llm = llm;

            IGameLogger log = GameLoggerUnscopedFallback.Instance;

            // РџСЂРѕР±СѓРµРј РЅР°Р·РЅР°С‡РёС‚СЊ РјРѕРґРµР»СЊ РёР· РЅР°СЃС‚СЂРѕРµРє
            bool assigned = false;
            if (!string.IsNullOrWhiteSpace(ggufPath))
            {
                assigned = LlmUnityModelBootstrap.TryAssignModelMatchingFilename(llm, log, ggufPath);
            }

            // Fallbacks
            if (!assigned)
            {
                assigned = LlmUnityModelBootstrap.TryAssignModelMatchingFilename(llm, log, "qwen", "4b");
            }

            if (!assigned)
            {
                assigned = LlmUnityModelBootstrap.TryAssignModelMatchingFilename(llm, log, "qwen", "2b");
            }

            if (!assigned)
            {
                assigned = LlmUnityModelBootstrap.TryAutoAssignResolvableModel(llm, log);
            }

            if (!assigned || string.IsNullOrWhiteSpace(llm.model))
            {
                UnityEngine.Object.Destroy(go);
                llm = null;
                agent = null;
                return null;
            }

            // РќР°СЃС‚СЂРѕР№РєРё РїСЂРѕРёР·РІРѕРґРёС‚РµР»СЊРЅРѕСЃС‚Рё
            llm.flashAttention = true;
            llm.numGPULayers = numGpuLayers; // 0 = CPU, >0 = GPU СѓСЃРєРѕСЂРµРЅРёРµ (1-99)
            llm.enabled = true;
            agent.enabled = true;
            llm.dontDestroyOnLoad = false;

            Debug.Log("[TestHarness] GameObject created, model: " + llm.model);

            // SetActive(true) Р°РІС‚РѕРјР°С‚РёС‡РµСЃРєРё РІС‹Р·С‹РІР°РµС‚ Awake() Сѓ LLM Рё LLMAgent.
            // LLM.Awake() Р·Р°РїСѓСЃРєР°РµС‚ Р°СЃРёРЅС…СЂРѕРЅРЅРѕРµ СЃРѕР·РґР°РЅРёРµ СЃРµСЂРІРµСЂР° llama.cpp.
            go.SetActive(true);
            Debug.Log("[TestHarness] GameObject activated (Awake invoked by Unity naturally)");

            // Unity РќР• РІС‹Р·С‹РІР°РµС‚ Start() Р°РІС‚РѕРјР°С‚РёС‡РµСЃРєРё РґР»СЏ РєРѕРјРїРѕРЅРµРЅС‚РѕРІ, РґРѕР±Р°РІР»РµРЅРЅС‹С… С‡РµСЂРµР· AddComponent 
            // РІ С‚РѕРј Р¶Рµ РєР°РґСЂРµ РІРЅСѓС‚СЂРё С‚РµСЃС‚Р°. Р’С‹Р·С‹РІР°РµРј РІСЂСѓС‡РЅСѓСЋ.
            // РўР°Рє РєР°Рє Start() СЌС‚Рѕ async void, РјС‹ РїСЂРѕСЃС‚Рѕ Р·Р°РїСѓСЃРєР°РµРј РµРіРѕ.
            agent.Start();
            Debug.Log("[TestHarness] agent.Start() invoked directly");

            return go;
        }

        public static GameObject CreateRuntimeLlmAndAgent(out LLM llm, out LLMAgent agent)
        {
            return CreateRuntimeLlmAndAgent(null, null, 99, out llm, out agent);
        }

        /// <summary>
        /// РЈСЃС‚Р°СЂРµРІС€РµРµ, РѕСЃС‚Р°РІР»РµРЅРѕ РґР»СЏ СЃРѕРІРјРµСЃС‚РёРјРѕСЃС‚Рё РёРЅС‚РµСЂС„РµР№СЃР°, РµСЃР»Рё РЅСѓР¶РЅРѕ.
        /// </summary>
        public static void TriggerAwakeIfNeeded(LLM llmComponent)
        {
            if (llmComponent != null && !llmComponent.started)
            {
                // РџСЂРѕСЃС‚Рѕ РґР»СЏ СЃС‚СЂР°С…РѕРІРєРё, С…РѕС‚СЏ SetActive(true) СѓР¶Рµ РІСЃС‘ СЃРґРµР»Р°Р»
                // llmComponent.Awake(); // РњРѕР¶РЅРѕ РІС‹Р·РІР°С‚СЊ РЅР°РїСЂСЏРјСѓСЋ, РµСЃР»Рё РѕС‡РµРЅСЊ РЅР°РґРѕ
            }
        }
    }
}
#endif