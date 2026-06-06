using CoreAI.Composition;
using CoreAI.Infrastructure.Logging;
#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL
using LLMUnity;
#endif
using UnityEngine;
using VContainer;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Disables LLMUnity components when no local model is configured.
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    [DisallowMultipleComponent]
    public sealed class LlmUnityAutoDisableIfNoModel : MonoBehaviour
    {
        private void Awake()
        {
            IGameLogger log = ResolveLogger();

#if !COREAI_HAS_LLMUNITY || UNITY_WEBGL
            // This guard is only relevant when LLMUnity package is present.
            return;
#else
            LLMAgent agent = FindFirstObjectByType<LLMAgent>();
            LLM llm = agent != null ? agent.GetComponent<LLM>() : FindFirstObjectByType<LLM>();
            if (llm == null)
            {
                return;
            }

            CoreAISettingsAsset coreSettings = CoreAISettingsAsset.Instance;
            if (coreSettings != null)
            {
                LlmUnityModelBootstrap.TryAssignModelFromGgufHint(llm, log, coreSettings.GgufModelPath);
            }

            LlmUnityModelBootstrap.TryAutoAssignResolvableModel(llm, log);

            if (!string.IsNullOrWhiteSpace(llm.model))
            {
                return;
            }

            llm.enabled = false;

            if (agent != null)
            {
                agent.enabled = false;
            }

            log.LogWarning(
                GameLogFeature.Llm,
                "LLMUnity: LLM.model is empty in the saved scene. Select or load a GGUF model and save the scene. Disabling LLMUnity so CoreAI can fall back safely.");
#endif
        }

        private static IGameLogger ResolveLogger()
        {
            CoreAILifetimeScope scope = FindAnyObjectByType<CoreAILifetimeScope>();
            if (scope != null && scope.Container != null &&
                scope.Container.TryResolve<IGameLogger>(out IGameLogger log))
            {
                return log;
            }

            return GameLoggerUnscopedFallback.Instance;
        }
    }
}