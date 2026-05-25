#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL
using System;
using System.IO;
using CoreAI.Infrastructure.Logging;
using LLMUnity;
using UnityEngine;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Creates and configures runtime LLMUnity host objects.
    /// </summary>
    public static class LlmUnityHostConfigurator
    {
        /// <summary>
        /// Applies CoreAI settings to the runtime LLMUnity host objects.
        /// </summary>
        public static void ApplyFromSettings(LLM llm, LLMAgent agent, CoreAISettingsAsset settings, IGameLogger logger)
        {
            if (llm == null || agent == null || settings == null || logger == null)
            {
                return;
            }

            agent.remote = false;
            agent.llm = llm;
            llm.dontDestroyOnLoad = settings.LlmUnityDontDestroyOnLoad;
            llm.numGPULayers = settings.NumGPULayers;
            llm.flashAttention = true;

            if (string.IsNullOrWhiteSpace(llm.model))
            {
                bool assigned = LlmUnityModelBootstrap.TryAssignModelFromGgufHint(llm, logger, settings.GgufModelPath);
                if (!assigned)
                {
                    LlmUnityModelBootstrap.TryAutoAssignResolvableModel(llm, logger);
                }
            }
        }
    }

    /// <summary>
    /// Runtime holder for LLMUnity objects created by CoreAI.
    /// </summary>
    public static class LlmUnityRuntimeHost
    {
        /// <summary>Creates and configures an LLMAgent from CoreAI settings.</summary>
        public static LLMAgent Create(CoreAISettingsAsset settings, IGameLogger logger)
        {
            if (settings == null || logger == null)
            {
                throw new ArgumentNullException(settings == null ? nameof(settings) : nameof(logger));
            }

            string goName = string.IsNullOrWhiteSpace(settings.LlmUnityRuntimeHostObjectName)
                ? "CoreAI_LLMUnity_Runtime"
                : settings.LlmUnityRuntimeHostObjectName.Trim();

            GameObject go = new(goName);
            go.SetActive(false);
            LLM llm = go.AddComponent<LLM>();
            LLMAgent agent = go.AddComponent<LLMAgent>();

            LlmUnityHostConfigurator.ApplyFromSettings(llm, agent, settings, logger);

            if (settings.LlmUnityDontDestroyOnLoad)
            {
                UnityEngine.Object.DontDestroyOnLoad(go);
            }

            go.SetActive(true);
            return agent;
        }
    }
}
#endif
