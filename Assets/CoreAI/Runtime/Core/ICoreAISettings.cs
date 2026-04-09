namespace CoreAI
{
    /// <summary>
    /// Глобальные настройки CoreAI (инфраструктура, логирование, лимиты агентов).
    /// </summary>
    public interface ICoreAISettings
    {
        int MaxLuaRepairRetries { get; }
        bool EnableMeaiDebugLogging { get; }
        float LlmRequestTimeoutSeconds { get; }
        int MaxLlmRequestRetries { get; }
        bool EnableHttpDebugLogging { get; }
        bool LogTokenUsage { get; }
        bool LogLlmLatency { get; }
        bool LogLlmConnectionErrors { get; }
        int ContextWindowTokens { get; }
        string UniversalSystemPromptPrefix { get; }
        float Temperature { get; }
        int MaxToolCallRetries { get; }
        bool LogToolCalls { get; }
        bool LogToolCallArguments { get; }
        bool LogToolCallResults { get; }
        bool LogMeaiToolCallingSteps { get; }
    }
}