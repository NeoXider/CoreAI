using System;
using CoreAI.Ai;
using CoreAI.Infrastructure.Logging;
using VContainer.Unity;

namespace CoreAI.Composition
{
    /// <summary>
    /// Точка старта после построения контейнера (аналог раннего bootstrap без MonoBehaviour).
    /// </summary>
    public sealed class CoreAIGameEntryPoint : IStartable
    {
        private readonly IGameLogger _logger;
        private readonly IAiOrchestrationService _orchestrator;

        /// <summary>DI: лог и оркестратор для опционального bootstrap-задачи.</summary>
        public CoreAIGameEntryPoint(IGameLogger logger, IAiOrchestrationService orchestrator)
        {
            _logger = logger;
            _orchestrator = orchestrator;
        }

        /// <summary>Вызывается VContainer после сборки контейнера; пишет в лог и запускает тестовую задачу Creator.</summary>
        public void Start()
        {
            _logger.LogInfo(GameLogFeature.Composition,
                "VContainer + MessagePipe (GlobalMessagePipe) + IGameLogger с фильтром по фичам готовы.");
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
                _logger.LogError(GameLogFeature.Composition, $"Ai bootstrap: {ex.Message}");
            }
        }
    }
}
