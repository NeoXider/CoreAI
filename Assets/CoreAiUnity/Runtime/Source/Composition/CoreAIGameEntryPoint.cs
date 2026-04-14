using System;
using CoreAI.Ai;
using CoreAI.Logging;
using VContainer.Unity;

namespace CoreAI.Composition
{
    /// <summary>
    /// Точка старта после построения контейнера (аналог раннего bootstrap без MonoBehaviour).
    /// </summary>
    public sealed class CoreAIGameEntryPoint : IStartable
    {
        private readonly ILog _logger;
        private readonly IAiOrchestrationService _orchestrator;
        private readonly AgentMemoryPolicy _policy;
        private readonly IAgentMemoryStore _memoryStore;

        /// <summary>DI: лог, оркестратор и политика для bootstrap + глобальный фасад CoreAI.</summary>
        public CoreAIGameEntryPoint(ILog logger, IAiOrchestrationService orchestrator, AgentMemoryPolicy policy, IAgentMemoryStore memoryStore)
        {
            _logger = logger;
            _orchestrator = orchestrator;
            _policy = policy;
            _memoryStore = memoryStore;
        }

        /// <summary>Вызывается VContainer после сборки контейнера; инициализирует CoreAI фасад и запускает bootstrap.</summary>
        public void Start()
        {
            // Инициализируем глобальный фасад CoreAI — 
            // позволяет вызывать merchant.Ask("text") без DI/container
            CoreAIAgent.Initialize(_orchestrator, _policy, _memoryStore);

            _logger.Info(
                "VContainer + MessagePipe (GlobalMessagePipe) + ILog с фильтром по тегам готовы.",
                LogTag.Composition);
            FireBootstrapAiTask();
        }

        private async void FireBootstrapAiTask()
        {
            try
            {
                await _orchestrator.RunTaskAsync(new AiTaskRequest
                {
                    RoleId = BuiltInAgentRoleIds.Creator,
                    Hint = "bootstrap"
                });
            }
            catch (Exception ex)
            {
                _logger.Error($"Ai bootstrap: {ex.Message}", LogTag.Composition);
            }
        }
    }
}