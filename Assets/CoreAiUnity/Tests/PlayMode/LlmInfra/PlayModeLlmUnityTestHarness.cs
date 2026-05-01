#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL
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
    /// Play Mode:  <see cref="LLM"/> + <see cref="LLMAgent"/>    .
    ///    ,    Awake/Start  LLMUnity .
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

            //     
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

            //  
            llm.flashAttention = true;
            llm.numGPULayers = numGpuLayers; // 0 = CPU, >0 = GPU  (1-99)
            llm.enabled = true;
            agent.enabled = true;
            llm.dontDestroyOnLoad = false;

            Debug.Log("[TestHarness] GameObject created, model: " + llm.model);

            // SetActive(true)   Awake()  LLM  LLMAgent.
            // LLM.Awake()     llama.cpp.
            go.SetActive(true);
            Debug.Log("[TestHarness] GameObject activated (Awake invoked by Unity naturally)");

            // Unity   Start()   ,   AddComponent 
            //      .  .
            //   Start()  async void,    .
            agent.Start();
            Debug.Log("[TestHarness] agent.Start() invoked directly");

            return go;
        }

        public static GameObject CreateRuntimeLlmAndAgent(out LLM llm, out LLMAgent agent)
        {
            return CreateRuntimeLlmAndAgent(null, null, 99, out llm, out agent);
        }

        /// <summary>
        /// ,    ,  .
        /// </summary>
        public static void TriggerAwakeIfNeeded(LLM llmComponent)
        {
            if (llmComponent != null && !llmComponent.started)
            {
                //   ,  SetActive(true)   
                // llmComponent.Awake(); //   ,   
            }
        }
    }
}
#endif
