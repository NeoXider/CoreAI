namespace CoreAI.Ai
{
    /// <summary>
    /// Exposes the global CoreAI agent facade used by host applications and Unity composition.
    /// </summary>
    /// <example>
    /// <code>
    /// var merchant = new AgentBuilder("Blacksmith")
    ///     .WithSystemPrompt("You are a blacksmith.")
    ///     .WithMemory()
    ///     .Build();
    /// merchant.ApplyToPolicy(CoreAIAgent.Policy);
    /// // Primary (awaitable):
    /// await merchant.AskAsync("Show me your swords");
    /// // Fire-and-forget convenience:
    /// merchant.AskWithCallback("Show me your swords", response => Debug.Log(response));
    /// </code>
    /// </example>
    public static class CoreAIAgent
    {
        // ARCH-2 fix: volatile backing fields prevent torn reads when Initialize
        // is called from the Unity main thread and properties are accessed from
        // async continuations on ThreadPool.
        private static volatile IAiOrchestrationService _orchestrator;
        private static volatile AgentMemoryPolicy _policy;
        private static volatile IAgentMemoryStore _memoryStore;

        /// <summary>
        /// Orchestration service currently registered for the global CoreAI agent facade.
        /// </summary>
        public static IAiOrchestrationService Orchestrator
        {
            get => _orchestrator;
            private set => _orchestrator = value;
        }

        /// <summary>
        /// Agent memory policy currently registered for the global CoreAI agent facade.
        /// </summary>
        public static AgentMemoryPolicy Policy
        {
            get => _policy;
            private set => _policy = value;
        }

        /// <summary>
        /// Memory store currently registered for the global CoreAI agent facade.
        /// </summary>
        public static IAgentMemoryStore MemoryStore
        {
            get => _memoryStore;
            private set => _memoryStore = value;
        }

        /// <summary>
        /// Registers the orchestration service, memory policy, and memory store used by the global CoreAI facade.
        /// </summary>
        public static void Initialize(IAiOrchestrationService orchestrator, AgentMemoryPolicy policy,
            IAgentMemoryStore memoryStore)
        {
            Orchestrator = orchestrator;
            Policy = policy;
            MemoryStore = memoryStore;
        }

        /// <summary>
        /// Clears the global CoreAI facade registrations.
        /// </summary>
        /// <remarks>
        /// CoreAI.Core is portable (no <c>UnityEngine</c> reference), so the Unity-specific
        /// <c>[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]</c> reset hook that
        /// clears this state across Play Mode / domain transitions lives in the Unity layer
        /// (<c>CoreAi.Invalidate()</c> in <c>com.nexoider.coreaiunity</c>), which calls this method.
        /// </remarks>
        public static void Reset()
        {
            Orchestrator = null;
            Policy = null;
            MemoryStore = null;
        }
    }
}
