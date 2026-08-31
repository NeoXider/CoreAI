using System;
using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Logging;
using VContainer.Unity;

namespace CoreAI.Composition
{
    /// <summary>
    /// VContainer entry point that initializes and resets the global CoreAI facade.
    /// </summary>
    /// <remarks>
    /// When an additive scene spins up a second entry point, it is registered as a standby
    /// owner rather than dropped: if the current owner is disposed (e.g. its scope/scene is
    /// unloaded), ownership hands off to the next live standby so the CoreAI facade keeps
    /// resolving instead of going stale until someone re-initializes it manually.
    /// </remarks>
    public sealed class CoreAIGameEntryPoint : IStartable, IDisposable
    {
        private static readonly object StartGate = new();
        private static readonly List<CoreAIGameEntryPoint> StandbyInstances = new();
        private static bool _isInitialized;

        /// <summary>
        /// Auto bootstrap.
        /// </summary>
        public static bool AutoBootstrap { get; set; } = false;

        private readonly ILog _logger;
        private readonly IAiOrchestrationService _orchestrator;
        private readonly AgentMemoryPolicy _policy;
        private readonly IAgentMemoryStore _memoryStore;
        private readonly IActorIdentityProvider _actorIdentityProvider;
        private bool _started;
        private bool _isOwner;

        /// <summary>Initializes a new instance of CoreAIGameEntryPoint.</summary>
        public CoreAIGameEntryPoint(
            ILog logger,
            IAiOrchestrationService orchestrator,
            AgentMemoryPolicy policy,
            IAgentMemoryStore memoryStore,
            IActorIdentityProvider actorIdentityProvider = null)
        {
            _logger = logger;
            _orchestrator = orchestrator;
            _policy = policy;
            _memoryStore = memoryStore;
            _actorIdentityProvider = actorIdentityProvider ??
                                     CoreServicesInstaller.DefaultLocalHostIdentityProvider;
        }

        /// <summary>Starts the entry point and registers CoreAI services for runtime use.</summary>
        public void Start()
        {
            bool becomeOwner;

            lock (StartGate)
            {
                if (_started)
                {
                    return;
                }

                _started = true;

                if (_isInitialized)
                {
                    // Another entry point already owns the facade; register as a standby owner
                    // so we can take over if that owner is later disposed (e.g. additive scene unload).
                    StandbyInstances.Add(this);
                    becomeOwner = false;
                }
                else
                {
                    _isInitialized = true;
                    _isOwner = true;
                    becomeOwner = true;
                }
            }

            if (!becomeOwner)
            {
                _logger.Debug(
                    "CoreAI already initialized in this process. This CoreAIGameEntryPoint is registered as a standby owner.",
                    LogTag.Composition);
                return;
            }

            InitializeAsOwner();
        }

        private void InitializeAsOwner()
        {
            CoreAIAgent.Initialize(_orchestrator, _policy, _memoryStore, _actorIdentityProvider);

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
            bool wasOwner;
            CoreAIGameEntryPoint promoted = null;

            lock (StartGate)
            {
                if (!_started)
                {
                    return;
                }

                _started = false;
                wasOwner = _isOwner;

                if (wasOwner)
                {
                    _isOwner = false;
                    _isInitialized = false;

                    while (StandbyInstances.Count > 0)
                    {
                        CoreAIGameEntryPoint candidate = StandbyInstances[0];
                        StandbyInstances.RemoveAt(0);

                        // Skip standbys that were disposed while still waiting (never became owner).
                        if (!candidate._started)
                        {
                            continue;
                        }

                        candidate._isOwner = true;
                        _isInitialized = true;
                        promoted = candidate;
                        break;
                    }
                }
                else
                {
                    StandbyInstances.Remove(this);
                }
            }

            if (!wasOwner)
            {
                return;
            }

            if (promoted != null)
            {
                promoted.InitializeAsOwner();
            }
            else
            {
                CoreAIAgent.Reset();
            }
        }

        /// <summary>
        /// Clears the process-wide initialization guard and standby list. Called from
        /// <see cref="CoreAi"/>'s SubsystemRegistration hook so the entry-point guard and the
        /// <see cref="CoreAIAgent"/> facade it initializes are always reset together at play-mode
        /// entry (Enter Play Mode without Domain Reload).
        /// </summary>
        internal static void ResetStaticState()
        {
            lock (StartGate)
            {
                _isInitialized = false;
                StandbyInstances.Clear();
            }
        }

        internal static void ResetInitializationGuardForTests()
        {
            ResetStaticState();
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
