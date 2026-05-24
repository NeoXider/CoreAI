#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL
using CoreAI.Infrastructure.Logging;
using LLMUnity;
using UnityEngine;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Resolves LLMUnity agents from CoreAI settings and scene configuration.
    /// </summary>
    public sealed class ConfigurableLlmAgentProvider : ILlmAgentProvider
    {
        private readonly CoreAISettingsAsset _settings;
        private readonly IGameLogger _logger;
        private LLMAgent _cached;

        public ConfigurableLlmAgentProvider(CoreAISettingsAsset settings, IGameLogger logger)
        {
            _settings = settings;
            _logger = logger ?? GameLoggerUnscopedFallback.Instance;
        }

        /// <inheritdoc />
        public LLMAgent Resolve(string agentName)
        {
            if (_cached != null)
            {
                return _cached;
            }

            LLMAgent found = TryFindInScene(agentName);
            if (found != null)
            {
                LLM llm = found.llm != null ? found.llm : found.GetComponent<LLM>();
                if (llm != null && _settings != null)
                {
                    LlmUnityHostConfigurator.ApplyFromSettings(llm, found, _settings, _logger);
                }

                _cached = found;
                return _cached;
            }

            if (_settings != null &&
                _settings.LlmUnityAutoCreateRuntimeHost &&
                _settings.UseLlmUnity)
            {
                _logger.LogInfo(
                    GameLogFeature.Llm,
                    "LLMUnity: no LLMAgent exists in the scene; creating a runtime host from Core AI Settings.");
                _cached = LlmUnityRuntimeHost.Create(_settings, _logger);
                return _cached;
            }

            return null;
        }

        private static LLMAgent TryFindInScene(string agentName)
        {
            if (!string.IsNullOrWhiteSpace(agentName))
            {
                GameObject go = GameObject.Find(agentName);
                if (go != null)
                {
                    LLMAgent a = go.GetComponent<LLMAgent>();
                    if (a != null)
                    {
                        return a;
                    }
                }
            }

            return Object.FindFirstObjectByType<LLMAgent>(FindObjectsInactive.Exclude);
        }
    }
}
#endif
