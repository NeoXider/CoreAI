using System;
using CoreAI.Ai;
using CoreAI.Logging;
using VContainer.Unity;

namespace CoreAI.Composition
{
    /// <summary>
    /// Точка старта после построения контейнера (аналог раннего bootstrap без MonoBehaviour).
    /// </summary>
    public sealed class CoreAIGameEntryPoint : IStartable, IDisposable
    {
        private static readonly object StartGate = new();
        private static bool _isInitialized;

        /// <summary>
        /// Если true — при старте автоматически запускается Creator-агент (bootstrap).
        /// По умолчанию false — оркестратор не запускается сам, дочерний проект решает, когда и что запускать.
        /// </summary>
        public static bool AutoBootstrap { get; set; } = false;

        private readonly ILog _logger;
        private readonly IAiOrchestrationService _orchestrator;
        private readonly AgentMemoryPolicy _policy;
        private readonly IAgentMemoryStore _memoryStore;
        private bool _started;

        /// <summary>DI: лог, оркестратор и политика для bootstrap + глобальный фасад CoreAI.</summary>
        public CoreAIGameEntryPoint(ILog logger, IAiOrchestrationService orchestrator, AgentMemoryPolicy policy, IAgentMemoryStore memoryStore)
        {
            _logger = logger;
            _orchestrator = orchestrator;
            _policy = policy;
            _memoryStore = memoryStore;
        }

        /// <summary>Вызывается VContainer после сборки контейнера; инициализирует CoreAI фасад и опционально запускает bootstrap.</summary>
        public void Start()
        {
            lock (StartGate)
            {
                if (_started)
                {
                    return;
                }

                if (_isInitialized)
                {
                    _logger.Warn(
                        "CoreAI already initialized in this process. Duplicate CoreAIGameEntryPoint start skipped.",
                        LogTag.Composition);
                    return;
                }

                _isInitialized = true;
                _started = true;
            }

            // Инициализируем глобальный фасад CoreAI — 
            // позволяет вызывать merchant.Ask("text") без DI/container
            CoreAIAgent.Initialize(_orchestrator, _policy, _memoryStore);

            _logger.Info(
                "VContainer + MessagePipe (GlobalMessagePipe) + ILog с фильтром по тегам готовы.",
                LogTag.Composition);

            if (AutoBootstrap)
            {
                FireBootstrapAiTask();
            }
            else
            {
                _logger.Info("AutoBootstrap отключён — оркестратор не запускает Creator-агента автоматически.", LogTag.Composition);
            }
        }

        public void Dispose()
        {
            lock (StartGate)
            {
                if (!_started)
                {
                    return;
                }

                _started = false;
                _isInitialized = false;
            }

            CoreAIAgent.Reset();
        }

        internal static void ResetInitializationGuardForTests()
        {
            lock (StartGate)
            {
                _isInitialized = false;
            }
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