using UnityEngine;

namespace CoreAI.Infrastructure.Llm
{
#if !UNITY_WEBGL && !COREAI_NO_LLM
    using LLMUnity;

    /// <summary>
    /// Абстракция для поиска <see cref="LLMAgent"/> без использования <c>FindFirstObjectByType</c> в DI composition root.
    /// </summary>
    public interface ILlmAgentProvider
    {
        /// <summary>Найти LLMAgent по имени (или первый доступный, если имя пустое). Null если не найден.</summary>
        LLMAgent Resolve(string agentName);
    }

    /// <summary>
    /// Реализация на основе <see cref="GameObject.Find"/> и <see cref="Object.FindFirstObjectByType{T}"/>.
    /// Вызывается **лениво** при первом запросе LLM-клиента, а не в composition root.
    /// </summary>
    public sealed class SceneLlmAgentProvider : ILlmAgentProvider
    {
        private LLMAgent _cached;

        /// <inheritdoc />
        public LLMAgent Resolve(string agentName)
        {
            if (_cached != null)
            {
                return _cached;
            }

            // Если указано имя — ищем по имени
            if (!string.IsNullOrWhiteSpace(agentName))
            {
                GameObject go = GameObject.Find(agentName);
                if (go != null)
                {
                    _cached = go.GetComponent<LLMAgent>();
                    if (_cached != null)
                    {
                        return _cached;
                    }
                }
            }

            // Fallback: первый активный LLMAgent на сцене
            _cached = Object.FindFirstObjectByType<LLMAgent>(FindObjectsInactive.Exclude);
            return _cached;
        }
    }
#else
    /// <summary>
    /// WebGL / COREAI_NO_LLM: локальный LLMUnity бэкенд недоступен, провайдер агента не используется.
    /// Оставляем интерфейс для DI без зависимости от пакета LLMUnity.
    /// </summary>
    public interface ILlmAgentProvider
    {
        /// <summary>Всегда возвращает <c>null</c> в WebGL/COREAI_NO_LLM.</summary>
        Object Resolve(string agentName);
    }

    public sealed class SceneLlmAgentProvider : ILlmAgentProvider
    {
        public Object Resolve(string agentName) => null;
    }
#endif
}
