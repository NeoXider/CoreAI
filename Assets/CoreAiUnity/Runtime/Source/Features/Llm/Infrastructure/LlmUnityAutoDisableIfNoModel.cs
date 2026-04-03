using CoreAI.Composition;
using CoreAI.Infrastructure.Logging;
using LLMUnity;
using UnityEngine;
using VContainer;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// В демо-сцене LLMUnity часто оставляют без назначенного GGUF (поле модели пустое).
    /// LLMUnity при этом пишет в консоль ошибку "No model file provided!" и может ломать smoke-check.
    ///
    /// Этот guard отключает компонент <see cref="LLM"/> (и сам <see cref="LLMAgent"/>), если <see cref="LLM.model"/> пустой.
    /// Соответствует рекомендациям LLMUnity: модель задаётся через Model Manager (Download / Load) и выбор радиокнопкой,
    /// иначе при старте будет ошибка «No model file provided!» — см. официальный Quick start и раздел «LLM model management»
    /// в репозитории пакета и на <see href="https://undream.ai/LLMUnity"/>.
    /// За счёт <see cref="DefaultExecutionOrderAttribute"/> выполняется раньше большинства Awake.
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    [DisallowMultipleComponent]
    public sealed class LlmUnityAutoDisableIfNoModel : MonoBehaviour
    {
        private void Awake()
        {
            var log = ResolveLogger();

            // Сначала LLM, привязанный к LLMAgent на сцене (избегаем «первого попавшегося» пустого LLM).
            var agent = UnityEngine.Object.FindFirstObjectByType<LLMAgent>();
            var llm = agent != null ? agent.GetComponent<LLM>() : UnityEngine.Object.FindFirstObjectByType<LLM>();
            if (llm == null)
                return;

            LlmUnityModelBootstrap.TryAutoAssignResolvableModel(llm, log);

            if (!string.IsNullOrWhiteSpace(llm.model))
                return;

            // Отключаем сервер и агент до того, как он начнёт стартовать.
            llm.enabled = false;

            if (agent != null)
                agent.enabled = false;

            log.LogWarning(
                GameLogFeature.Llm,
                "LLMUnity: поле LLM.model пусто в сохранённой сцене. В инспекторе нажмите радиокнопку у нужной модели " +
                "(или «Load model») и сохраните сцену. Если в Model Manager несколько моделей — оставьте одну с реальным .gguf " +
                "или явно выберите модель. Отключаем LLMUnity → StubLlmClient.");
        }

        private static IGameLogger ResolveLogger()
        {
            var scope = UnityEngine.Object.FindAnyObjectByType<CoreAILifetimeScope>();
            if (scope != null && scope.Container != null && scope.Container.TryResolve<IGameLogger>(out var log))
                return log;
            return GameLoggerUnscopedFallback.Instance;
        }
    }
}

