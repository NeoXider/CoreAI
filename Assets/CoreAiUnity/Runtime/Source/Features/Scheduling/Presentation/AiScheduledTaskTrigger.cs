using CoreAI.Ai;
using CoreAI.Composition;
using CoreAI.Infrastructure.Logging;
using UnityEngine;
using VContainer;

namespace CoreAI.Presentation
{
    /// <summary>
    /// Обертка над <see cref="IAiOrchestrationService.RunTaskAsync"/>: опциональный таймер и ручной вызов.
    /// Разместите на объекте с <see cref="CoreAILifetimeScope"/> или укажите scope в инспекторе.
    /// Несколько ролей — несколько компонентов на одном GameObject.
    /// </summary>
    public sealed class AiScheduledTaskTrigger : MonoBehaviour
    {
        [Tooltip("Пусто — ищется GetComponentInParent или FindAnyObjectByType.")] [SerializeField]
        private CoreAILifetimeScope lifetimeScope;

        [SerializeField] private string agentRoleId = BuiltInAgentRoleIds.Creator;

        [TextArea(2, 8)] [SerializeField] private string taskHint = "periodic_timer";

        [SerializeField] private int priority;

        [SerializeField] private string cancellationScope = "";

        [Tooltip("Метка источника для логов/дашборда (например scheduled_timer:my_id).")] [SerializeField]
        private string sourceTag = "scheduled_timer";

        [Header("Таймер")] [SerializeField] private bool timerEnabled = true;

        [Min(0.1f)] [SerializeField] private float intervalSeconds = 30f;

        [Tooltip("Если таймер включён — начинать отсчёт при OnEnable.")] [SerializeField]
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

        /// <summary>Очередь задачу сразу (независимо от таймера и паузы таймера).</summary>
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
                log.LogWarning(GameLogFeature.Composition, "AiScheduledTaskTrigger: CoreAILifetimeScope не найден.");
                return;
            }

            if (!scope.Container.TryResolve<IAiOrchestrationService>(out IAiOrchestrationService orch))
            {
                log.LogWarning(
                    GameLogFeature.Composition,
                    "AiScheduledTaskTrigger: IAiOrchestrationService не зарегистрирован.");
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

        /// <summary>Пауза таймера; <see cref="FireNow"/> по-прежнему работает.</summary>
        public void PauseTimer()
        {
            _timerPaused = true;
        }

        public void ResumeTimer()
        {
            _timerPaused = false;
        }

        /// <summary>Остановить таймер до следующего <see cref="StartTimer"/>.</summary>
        public void StopTimer()
        {
            _timerStopped = true;
            _accum = 0f;
        }

        /// <summary>Включить таймер и снять паузу.</summary>
        public void StartTimer()
        {
            _timerStopped = false;
            _timerPaused = false;
            _accum = 0f;
        }

        /// <summary>Сбросить накопленное время до следующего тика.</summary>
        public void RestartTimerCountdown()
        {
            _accum = 0f;
        }
    }
}