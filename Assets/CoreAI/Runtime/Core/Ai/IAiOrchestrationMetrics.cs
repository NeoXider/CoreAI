namespace CoreAI.Ai
{
    /// <summary>Счётчики/наблюдаемость оркестратора (no-op по умолчанию).</summary>
    public interface IAiOrchestrationMetrics
    {
        /// <summary>Завершён один раунд вызова LLM (успех/ошибка, длительность).</summary>
        void RecordLlmCompletion(string roleId, string traceId, bool ok, double wallMs);

        /// <summary>Оркестратор делает повтор из-за политики структурированного ответа.</summary>
        void RecordStructuredRetry(string roleId, string traceId, string reason);

        /// <summary>В шину ушла команда <see cref="CoreAI.Messaging.ApplyAiGameCommand"/>.</summary>
        void RecordCommandPublished(string roleId, string traceId);
    }
}
