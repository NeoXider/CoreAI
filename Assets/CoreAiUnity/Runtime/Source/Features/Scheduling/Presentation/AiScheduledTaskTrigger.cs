using CoreAI.Ai;
using CoreAI.Composition;
using CoreAI.Infrastructure.Logging;
using UnityEngine;
using VContainer;

namespace CoreAI.Presentation
{
    /// <summary>
    /// Unity component that triggers scheduled AI tasks.
    /// </summary>
    public sealed class AiScheduledTaskTrigger : MonoBehaviour
    {
        [Tooltip("Empty value resolves through GetComponentInParent or FindAnyObjectByType.")]
        [SerializeField]
        private CoreAILifetimeScope lifetimeScope;

        [SerializeField] private string agentRoleId = BuiltInAgentRoleIds.Creator;

        [TextArea(2, 8)] [SerializeField] private string taskHint = "periodic_timer";

        [SerializeField] private int priority;

        [SerializeField] private string cancellationScope = "";

        [Tooltip("Source tag for logs and dashboard entries, for example scheduled_timer:my_id.")]
        [SerializeField]
        private string sourceTag = "scheduled_timer";

        [Header("Timer")] [SerializeField] private bool timerEnabled = true;

        [Min(0.1f)] [SerializeField] private float intervalSeconds = 30f;

        [Tooltip("When enabled, the timer starts counting from OnEnable.")] [SerializeField]
        private bool startTimerOnEnable = true;

        private float _accum;
        private bool _timerPaused;
        private bool _timerStopped;

        private void OnEnable()
        {
            _accum = 0f;
            if (timerEnabled && startTimerOnEnable)
            {
                _timerStopped = false;
                _timerPaused = false;
            }
        }

        private void Update()
        {
            if (!timerEnabled || _timerStopped || _timerPaused)
            {
                return;
            }

            _accum += Time.deltaTime;
            if (_accum < intervalSeconds)
            {
                return;
            }

            _accum = 0f;
            FireNowInternal();
        }

        /// <summary>Immediately triggers the scheduled AI task regardless of timer state.</summary>
        public void FireNow()
        {
            FireNowInternal();
        }

        private void FireNowInternal()
        {
            CoreAILifetimeScope scope =
                lifetimeScope != null ? lifetimeScope : GetComponentInParent<CoreAILifetimeScope>();
            if (scope == null)
            {
                scope = FindAnyObjectByType<CoreAILifetimeScope>();
            }

            IGameLogger log = GameLoggerUnscopedFallback.Instance;
            if (scope != null && scope.Container != null && scope.Container.TryResolve<IGameLogger>(out IGameLogger lg))
            {
                log = lg;
            }

            if (scope == null)
            {
                log.LogWarning(GameLogFeature.Composition, "AiScheduledTaskTrigger: IAiOrchestrationService is not registered.");
                return;
            }

            if (!scope.Container.TryResolve<IAiOrchestrationService>(out IAiOrchestrationService orch))
            {
                log.LogWarning(
                    GameLogFeature.Composition,
                    "AiScheduledTaskTrigger: IAiOrchestrationService is not registered.");
                return;
            }

            _ = orch.RunTaskAsync(new AiTaskRequest
            {
                RoleId = string.IsNullOrWhiteSpace(agentRoleId) ? BuiltInAgentRoleIds.Creator : agentRoleId.Trim(),
                Hint = taskHint ?? "",
                Priority = priority,
                SourceTag = string.IsNullOrWhiteSpace(sourceTag) ? "scheduled_timer" : sourceTag.Trim(),
                CancellationScope = cancellationScope ?? ""
            });
        }

        /// <summary>Pauses the scheduled task countdown without clearing elapsed state.</summary>
        public void PauseTimer()
        {
            _timerPaused = true;
        }

        public void ResumeTimer()
        {
            _timerPaused = false;
        }

        /// <summary>Stops the scheduled task countdown and clears active timer state.</summary>
        public void StopTimer()
        {
            _timerStopped = true;
            _accum = 0f;
        }

        /// <summary>Starts the scheduled task countdown if the trigger is enabled.</summary>
        public void StartTimer()
        {
            _timerStopped = false;
            _timerPaused = false;
            _accum = 0f;
        }

        /// <summary>Restarts the scheduled task countdown from its initial delay.</summary>
        public void RestartTimerCountdown()
        {
            _accum = 0f;
        }
    }
}
