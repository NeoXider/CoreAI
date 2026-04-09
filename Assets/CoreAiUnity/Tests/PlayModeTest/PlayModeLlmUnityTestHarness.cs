#if !COREAI_NO_LLM
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
    /// Play Mode: поднимает <see cref="LLM"/> + <see cref="LLMAgent"/> без сцены с префабом.
    /// Больше не использует рефлексию, так как методы Awake/Start в LLMUnity публичные.
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

            // Пробуем назначить модель из настроек
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

            // Настройки производительности
            llm.flashAttention = true;
            llm.numGPULayers = numGpuLayers; // 0 = CPU, >0 = GPU ускорение (1-99)
            llm.enabled = true;
            agent.enabled = true;
            llm.dontDestroyOnLoad = false;

            Debug.Log("[TestHarness] GameObject created, model: " + llm.model);

            // SetActive(true) автоматически вызывает Awake() у LLM и LLMAgent.
            // LLM.Awake() запускает асинхронное создание сервера llama.cpp.
            go.SetActive(true);
            Debug.Log("[TestHarness] GameObject activated (Awake invoked by Unity naturally)");

            // Unity НЕ вызывает Start() автоматически для компонентов, добавленных через AddComponent 
            // в том же кадре внутри теста. Вызываем вручную.
            // Так как Start() это async void, мы просто запускаем его.
            agent.Start();
            Debug.Log("[TestHarness] agent.Start() invoked directly");

            return go;
        }

        public static GameObject CreateRuntimeLlmAndAgent(out LLM llm, out LLMAgent agent)
        {
            return CreateRuntimeLlmAndAgent(null, null, 99, out llm, out agent);
        }

        /// <summary>
        /// Устаревшее, оставлено для совместимости интерфейса, если нужно.
        /// </summary>
        public static void TriggerAwakeIfNeeded(LLM llmComponent)
        {
            if (llmComponent != null && !llmComponent.started)
            {
                // Просто для страховки, хотя SetActive(true) уже всё сделал
                // llmComponent.Awake(); // Можно вызвать напрямую, если очень надо
            }
        }
    }
}
#endif