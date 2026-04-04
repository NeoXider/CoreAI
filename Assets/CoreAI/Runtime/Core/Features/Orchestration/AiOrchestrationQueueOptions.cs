namespace CoreAI.Ai
{
    /// <summary>Параметры <see cref="QueuedAiOrchestrator"/> (регистрируется из сцены).</summary>
    public sealed class AiOrchestrationQueueOptions
    {
        /// <summary>Сколько задач оркестратора может выполняться параллельно (остальные ждут в очереди).</summary>
        public int MaxConcurrent { get; set; } = 2;
    }
}