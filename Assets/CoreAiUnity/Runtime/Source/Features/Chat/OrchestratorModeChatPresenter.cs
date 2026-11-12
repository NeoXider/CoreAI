using System;
using CoreAI.Ai.SubAgents;
using CoreAI.ChatOverlay;
using CoreAI.Messaging;
using VContainer.Unity;

namespace CoreAI.Chat
{
    /// <summary>
    /// The Unity side of orchestrator mode's HUD. It forwards every <c>CoreAi</c> tool-call event, with its tool name
    /// and outcome, to <see cref="OrchestratorChatDriver.RecordToolActivity"/> (a sub-agent's steps are counted for
    /// the HUD, land in a Team-style sub-agent's transcript and re-arm its own and its lead's idle deadline), and,
    /// when the in-world chat overlay is registered, drives <see cref="OrchestrationChatOverlayBridge"/> every frame:
    /// the plan or sub-agent line on the overlay's status line, a feed line per sub-agent change, the wake turns a
    /// sub-agent's completion asks for (as overlay host turns), and the <c>/orchestrator</c> and <c>/agents</c>
    /// commands. Event handlers only record; the overlay is touched on the main thread in <see cref="Tick"/>.
    /// </summary>
    public sealed class OrchestratorModeChatPresenter : IStartable, ITickable, IDisposable
    {
        private readonly OrchestratorChatDriver _driver;
        private readonly OrchestrationChatOverlayBridge _bridge;
        private readonly IChatCommandRegistry _commands;
        private bool _subscribed;

        /// <summary>Creates the presenter; without an overlay it only forwards tool activity.</summary>
        public OrchestratorModeChatPresenter(
            OrchestratorChatDriver driver,
            ChatOverlayController overlay = null,
            IChatCommandRegistry commands = null)
        {
            _driver = driver ?? throw new ArgumentNullException(nameof(driver));
            _commands = commands;
            if (overlay != null)
            {
                _bridge = new OrchestrationChatOverlayBridge(driver, overlay.Status, () => overlay.ActiveTraceId,
                    overlay, overlay.TryStartHostTurn, () => overlay.IsTurnActive);
            }
        }

        /// <inheritdoc />
        public void Start()
        {
            if (!_subscribed)
            {
                CoreAi.OnToolCallStarted += OnToolStarted;
                CoreAi.OnToolCallCompleted += OnToolCompleted;
                CoreAi.OnToolCallFailed += OnToolFailed;
                _subscribed = true;
            }

            _bridge?.RegisterCommand(_commands);
        }

        /// <inheritdoc />
        public void Tick()
        {
            _bridge?.Tick();
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_subscribed)
            {
                CoreAi.OnToolCallStarted -= OnToolStarted;
                CoreAi.OnToolCallCompleted -= OnToolCompleted;
                CoreAi.OnToolCallFailed -= OnToolFailed;
                _subscribed = false;
            }

            _bridge?.Dispose();
        }

        private void OnToolStarted(LlmToolCallStarted evt)
        {
            _driver.RecordToolActivity(evt.TraceId, evt.ToolName, null);
        }

        private void OnToolCompleted(LlmToolCallCompleted evt)
        {
            _driver.RecordToolActivity(evt.TraceId, evt.ToolName, true);
        }

        private void OnToolFailed(LlmToolCallFailed evt)
        {
            _driver.RecordToolActivity(evt.TraceId, evt.ToolName, false);
        }
    }
}
