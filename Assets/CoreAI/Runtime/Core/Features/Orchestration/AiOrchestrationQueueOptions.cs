namespace CoreAI.Ai
{
    /// <summary>Options that control AI orchestration queue concurrency and ordering.</summary>
    public sealed class AiOrchestrationQueueOptions
    {
        /// <summary>Maximum number of AI tasks that may run concurrently.</summary>
        public int MaxConcurrent { get; set; } = 2;
    }
}