namespace CoreAI.Ai
{
    /// <summary>
    /// Статическая точка входа — синглтон-доступ к оркестратору и политике.
    /// Автоматически заполняется при инициализации <c>CoreAILifetimeScope</c>.
    /// <para>Для продвинутого DI и тестов — используйте <see cref="IAiOrchestrationService"/> напрямую из VContainer.</para>
    /// </summary>
    /// <example>
    /// // Создай агента
    /// var merchant = new AgentBuilder("Blacksmith")
    ///     .WithSystemPrompt("You are a blacksmith.")
    ///     .WithMemory()
    ///     .Build();
    /// merchant.ApplyToPolicy(CoreAIAgent.Policy);
    /// 
    /// // Вызови (самый простой способ)
    /// merchant.Ask("Show me your swords");
    /// 
    /// // Async:
    /// await merchant.AskAsync("Show me your swords");
    /// 
    /// // С callback:
    /// merchant.Ask("Show me your swords", onDone: () => Debug.Log("Done!"));
    /// </example>
    public static class CoreAIAgent
    {
        /// <summary>
        /// Глобальный оркестратор. Устанавливается при инициализации CoreAILifetimeScope.
        /// <para>Если null — CoreAI ещё не инициализован (не был Play или нет CoreAILifetimeScope на сцене).</para>
        /// </summary>
        public static IAiOrchestrationService Orchestrator { get; private set; }

        /// <summary>
        /// Глобальная политика памяти/инструментов. Устанавливается при инициализации CoreAILifetimeScope.
        /// </summary>
        public static AgentMemoryPolicy Policy { get; private set; }

        /// <summary>Инициализация (вызывается из CoreAILifetimeScope / CoreAIGameEntryPoint).</summary>
        public static void Initialize(IAiOrchestrationService orchestrator, AgentMemoryPolicy policy)
        {
            Orchestrator = orchestrator;
            Policy = policy;
        }

        /// <summary>Очистка при выходе из Play Mode.</summary>
        public static void Reset()
        {
            Orchestrator = null;
            Policy = null;
        }
    }
}
