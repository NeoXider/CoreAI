using System;
using CoreAI.Ai;
using CoreAI.Logging;
using VContainer.Unity;

namespace CoreAI.Composition
{
    /// <summary>
    /// VContainer entry point that initializes and resets the global CoreAI facade.
    /// </summary>
    public sealed class CoreAIGameEntryPoint : IStartable, IDisposable
    {
        private static readonly object StartGate = new();
        private static bool _isInitialized;

        /// <summary>
        /// Auto bootstrap.
        /// </summary>
        public static bool AutoBootstrap { get; set; } = false;

        private readonly ILog _logger;
        private readonly IAiOrchestrationService _orchestrator;
        private readonly AgentMemoryPolicy _policy;
        private readonly IAgentMemoryStore _memoryStore;
        private bool _started;

        /// <summary>Initializes a new instance of CoreAIGameEntryPoint.</summary>
        public CoreAIGameEntryPoint(ILog logger, IAiOrchestrationService orchestrator, AgentMemoryPolicy policy,
            IAgentMemoryStore memoryStore)
        {
            _logger = logger;
            _orchestrator = orchestrator;
            _policy = policy;
            _memoryStore = memoryStore;
        }

        /// <summary>Starts the entry point and registers CoreAI services for runtime use.</summary>
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
                    _logger.Debug(
                        "CoreAI already initialized in this process. Duplicate CoreAIGameEntryPoint start skipped.",
                        LogTag.Composition);
                    return;
                }

                _isInitialized = true;
                _started = true;
            }

            CoreAIAgent.Initialize(_orchestrator, _policy, _memoryStore);

            _logger.Info(
                "VContainer + MessagePipe (GlobalMessagePipe) + filtered ILog are registered.",
                LogTag.Composition);

            if (AutoBootstrap)
            {
                FireBootstrapAiTask();
            }
            else
            {
                _logger.Info(
                    "AutoBootstrap is disabled; the orchestrator will not start the Creator agent automatically.",
                    LogTag.Composition);
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
                _logger.Error($"Ai bootstrap: {ex}", LogTag.Composition);
            }
        }
    }
}
