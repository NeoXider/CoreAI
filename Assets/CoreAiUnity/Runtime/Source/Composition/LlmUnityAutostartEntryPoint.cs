#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL
using System;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Llm;
using Cysharp.Threading.Tasks;
using LLMUnity;
using UnityEngine;
using VContainer.Unity;

namespace CoreAI.Composition
{
    /// <summary>
    /// Starts LLMUnity models during application startup when configured.
    /// </summary>
    public sealed class LlmUnityAutostartEntryPoint : IStartable
    {
        private readonly CoreAISettingsAsset _settings;
        private readonly IGameLogger _logger;
        private readonly ILlmAgentProvider _agentProvider;

        public LlmUnityAutostartEntryPoint(
            CoreAISettingsAsset settings,
            IGameLogger logger,
            ILlmAgentProvider agentProvider)
        {
            _settings = settings;
            _logger = logger ?? GameLoggerUnscopedFallback.Instance;
            _agentProvider = agentProvider ?? throw new ArgumentNullException(nameof(agentProvider));
        }

        /// <inheritdoc />
        public void Start()
        {
            if (_settings == null || !_settings.LlmUnityAutostartLocalServer || !_settings.UseLlmUnity)
            {
                return;
            }

            LLMAgent agent = _agentProvider.Resolve(_settings.LlmUnityAgentName);
            if (agent == null)
            {
                return;
            }

            LLM llm = agent.llm != null ? agent.llm : agent.GetComponent<LLM>();
            if (llm == null)
            {
                return;
            }

            LlmUnityHostConfigurator.ApplyFromSettings(llm, agent, _settings, _logger);

            if (string.IsNullOrWhiteSpace(llm.model))
            {
                return;
            }

            if (llm.started)
            {
                _logger.LogInfo(GameLogFeature.Llm, "LLMUnity: autostart status changed.");
                return;
            }

            try
            {
                agent.Start();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(GameLogFeature.Llm,
                    "LLMUnity: autostart status changed." + ex.Message);
            }

            WarmupAsync(llm).Forget();
        }

        private async UniTaskVoid WarmupAsync(LLM llm)
        {
            float timeout = _settings.LlmUnityStartupTimeoutSeconds;
            float start = Time.realtimeSinceStartup;
            while (!llm.started && !llm.failed)
            {
                if (Time.realtimeSinceStartup - start > timeout)
                {
                    _logger.LogWarning(GameLogFeature.Llm,
                        $"LLMUnity: autostart status changed.");
                    return;
                }

                await UniTask.Delay(TimeSpan.FromMilliseconds(200), DelayType.Realtime, PlayerLoopTiming.Update);
            }

            if (llm.failed)
            {
                _logger.LogWarning(GameLogFeature.Llm, "LLMUnity: autostart status changed.");
                return;
            }

            _logger.LogInfo(GameLogFeature.Llm, "LLMUnity: autostart status changed.");
        }
    }
}
#endif
