namespace CoreAI
{
    /// <summary>
    /// Глобальные настройки CoreAI (инфраструктура, логирование, лимиты агентов).
    /// </summary>
    public interface ICoreAISettings
    {
        /// <summary>Максимум подряд неудачных попыток Programmer починить Lua код перед прерыванием.</summary>
        int MaxLuaRepairRetries { get; }
        
        /// <summary>Включить подробное логирование MEAI pipeline (запросы, ответы, json).</summary>
        bool EnableMeaiDebugLogging { get; }
        
        /// <summary>Таймаут запросов к LLM (в секундах).</summary>
        float LlmRequestTimeoutSeconds { get; }
        
        /// <summary>Количество попыток повторного запроса к LLM при сетевых ошибках.</summary>
        int MaxLlmRequestRetries { get; }
        
        /// <summary>Включить низкоуровневое логирование HTTP запросов/ответов.</summary>
        bool EnableHttpDebugLogging { get; }
        
        /// <summary>Логировать потребление токенов (input, output, total).</summary>
        bool LogTokenUsage { get; }
        
        /// <summary>Логировать задержку LLM (latency).</summary>
        bool LogLlmLatency { get; }
        
        /// <summary>Логировать ошибки соединения с LLM.</summary>
        bool LogLlmConnectionErrors { get; }
        
        /// <summary>Размер контекстного окна модели в токенах.</summary>
        int ContextWindowTokens { get; }
        
        /// <summary>Универсальный префикс системного промпта (правила для всех агентов).</summary>
        string UniversalSystemPromptPrefix { get; }
        
        /// <summary>Общая температура генерации по умолчанию (0.0 - 2.0).</summary>
        float Temperature { get; }
        
        /// <summary>Максимальное количество подряд неудачных вызовов инструментов (ошибок) до прерывания агента.</summary>
        int MaxToolCallRetries { get; }
        
        /// <summary>Логировать факт вызова инструментов агентом.</summary>
        bool LogToolCalls { get; }
        
        /// <summary>Логировать переданные в инструменты аргументы.</summary>
        bool LogToolCallArguments { get; }
        
        /// <summary>Логировать результаты выполнения инструментов.</summary>
        bool LogToolCallResults { get; }
        
        /// <summary>Логировать промежуточные шаги MEAI function calling цикла.</summary>
        bool LogMeaiToolCallingSteps { get; }
        
        /// <summary>Разрешить агенту вызывать один и тот же инструмент с теми же аргументами подряд. 
        /// Если отключено - защищает от зацикливания, но мешает выполнять одно действие много раз.</summary>
        bool AllowDuplicateToolCalls { get; }

        /// <summary>
        /// Глобальное включение стриминга ответов LLM (SSE для HTTP, callback для LLMUnity).
        /// Может быть переопределено на уровне роли через <c>AgentBuilder.WithStreaming()</c>
        /// или <c>AgentMemoryPolicy.SetStreamingEnabled()</c>, а также на уровне UI
        /// через <c>CoreAiChatConfig.EnableStreaming</c>.
        /// По умолчанию: true.
        /// </summary>
        bool EnableStreaming { get; }
    }
}