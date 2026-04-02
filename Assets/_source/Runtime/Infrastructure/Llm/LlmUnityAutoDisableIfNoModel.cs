using LLMUnity;
using UnityEngine;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// В демо-сцене LLMUnity часто оставляют без назначенного GGUF (поле модели пустое).
    /// LLMUnity при этом пишет в консоль ошибку "No model file provided!" и может ломать smoke-check.
    ///
    /// Этот guard отключает компонент <see cref="LLM"/> (и сам <see cref="LLMAgent"/>), если <see cref="LLM.model"/> пустой.
    /// За счёт <see cref="DefaultExecutionOrderAttribute"/> выполняется раньше большинства Awake.
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    [DisallowMultipleComponent]
    public sealed class LlmUnityAutoDisableIfNoModel : MonoBehaviour
    {
        private void Awake()
        {
            var llm = FindFirstObjectByType<LLM>();
            if (llm == null)
                return;

            if (!string.IsNullOrWhiteSpace(llm.model))
                return;

            // Отключаем сервер и агент до того, как он начнёт стартовать.
            llm.enabled = false;

            var agent = FindFirstObjectByType<LLMAgent>();
            if (agent != null)
                agent.enabled = false;

            Debug.LogWarning("[CoreAI] LLMUnity LLM.model пустой — отключаем LLMUnity, используем StubLlmClient.");
        }

        private static T FindFirstObjectByType<T>() where T : Object
            => Object.FindFirstObjectByType<T>();
    }
}

