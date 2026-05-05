using UnityEngine;

namespace CoreAI.Infrastructure.Llm
{
#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL
    using LLMUnity;

    /// <summary>
    /// Абстракция для поиска или создания <see cref="LLMAgent"/> без вызова <c>FindFirstObjectByType</c> в composition root.
    /// Реализация по умолчанию: <see cref="ConfigurableLlmAgentProvider"/>.
    /// </summary>
    public interface ILlmAgentProvider
    {
        /// <summary>Найти LLMAgent по имени (или первый доступный, если имя пустое). Null если не найден.</summary>
        LLMAgent Resolve(string agentName);
    }
#else
    /// <summary>
    /// WebGL / no LLMUnity: локальный LLMUnity бэкенд недоступен, провайдер агента не используется.
    /// Оставляем интерфейс для DI без зависимости от пакета LLMUnity.
    /// </summary>
    public interface ILlmAgentProvider
    {
        /// <summary>Всегда возвращает <c>null</c> когда LLMUnity не установлен.</summary>
        Object Resolve(string agentName);
    }

    public sealed class SceneLlmAgentProvider : ILlmAgentProvider
    {
        public Object Resolve(string agentName) => null;
    }
#endif
}
