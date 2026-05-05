#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL
using System;
using System.IO;
using CoreAI.Infrastructure.Logging;
using LLMUnity;
using UnityEngine;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Применяет поля <see cref="CoreAISettingsAsset"/> к паре <see cref="LLM"/> + <see cref="LLMAgent"/> и
    /// назначает GGUF из настроек CoreAI до fallback на Model Manager (см. <see cref="LlmUnityModelBootstrap"/>).
    /// </summary>
    public static class LlmUnityHostConfigurator
    {
        /// <summary>
        /// Синхронизирует локальный хост с Core AI Settings: remote, GPU-слои, flash attention, DontDestroyOnLoad, модель.
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
    /// Создаёт скрытый <see cref="GameObject"/> с <see cref="LLM"/> + <see cref="LLMAgent"/>, если в сцене нет хоста.
    /// Имя объекта берётся из настроек или <c>CoreAI_LLMUnity_Runtime</c>.
    /// </summary>
    public static class LlmUnityRuntimeHost
    {
        /// <summary>Создаёт и активирует новый хост; не вызывать если агент уже есть на сцене.</summary>
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
            // LLM.Awake запускает загрузку модели; без модели — «No model file provided!» и сервер остаётся в broken state.
            // Пока объект неактивен, Awake/OnEnable у добавленных компонентов откладываются — успеваем выставить model через ApplyFromSettings.
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
