using System;
using System.Threading;
using System.Threading.Tasks;

namespace CoreAI.Ai
{
    /// <summary>
    /// Упрощённые extension-методы для <see cref="AgentConfig"/>.
    /// Позволяют вызывать агента одной строкой без ручной сборки <see cref="AiTaskRequest"/>.
    /// <code>
    /// // Async:
    /// await merchant.AskAsync(CoreAIAgent.Orchestrator, "Что у тебя есть?");
    /// 
    /// // Fire-and-forget:
    /// merchant.Ask("Что у тебя есть?");
    /// 
    /// // С callback:
    /// merchant.Ask("Что у тебя есть?", onDone: () => Debug.Log("Готово!"));
    /// </code>
    /// </summary>
    public static class AgentConfigExtensions
    {
        /// <summary>
        /// Отправить запрос агенту (async).
        /// <para>Использует <see cref="CoreAIAgent.Orchestrator"/> по умолчанию.</para>
        /// </summary>
        /// <param name="config">Конфигурация агента (результат <see cref="AgentBuilder.Build"/>).</param>
        /// <param name="message">Сообщение/запрос для агента.</param>
        /// <param name="priority">Приоритет в очереди (больше = раньше).</param>
        /// <param name="cancellationToken">Токен отмены.</param>
        /// <example>
        /// await merchant.AskAsync("Show me your swords");
        /// </example>
        public static Task AskAsync(
            this AgentConfig config,
            string message,
            int priority = 0,
            CancellationToken cancellationToken = default)
        {
            return AskAsync(config, CoreAIAgent.Orchestrator, message, priority, cancellationToken);
        }

        /// <summary>
        /// Отправить запрос агенту (async, явный оркестратор).
        /// </summary>
        public static Task AskAsync(
            this AgentConfig config,
            IAiOrchestrationService orchestrator,
            string message,
            int priority = 0,
            CancellationToken cancellationToken = default)
        {
            if (orchestrator == null)
            {
                throw new InvalidOperationException(
                    "Orchestrator is null. Make sure CoreAILifetimeScope is initialized or pass orchestrator explicitly.");
            }

            return orchestrator.RunTaskAsync(new AiTaskRequest
            {
                RoleId = config.RoleId,
                Hint = message,
                Priority = priority
            }, cancellationToken);
        }

        /// <summary>
        /// Отправить запрос агенту (fire-and-forget, без await).
        /// Удобно для UI кнопок, событий и скриптов где async неудобен.
        /// <para>Использует <see cref="CoreAIAgent.Orchestrator"/> синглтон.</para>
        /// </summary>
        /// <param name="config">Конфигурация агента.</param>
        /// <param name="message">Сообщение/запрос.</param>
        /// <param name="onDone">Опциональный callback по завершению.</param>
        /// <param name="priority">Приоритет в очереди.</param>
        /// <example>
        /// // Простой вызов:
        /// merchant.Ask("Покажи мечи");
        /// 
        /// // С callback:
        /// merchant.Ask("Покажи мечи", onDone: () => Debug.Log("Готово!"));
        /// </example>
        public static async void Ask(
            this AgentConfig config,
            string message,
            Action onDone = null,
            int priority = 0)
        {
            try
            {
                await AskAsync(config, message, priority);
                onDone?.Invoke();
            }
            catch (Exception)
            {
                // Fire-and-forget: ошибки логируются на уровне оркестратора.
                // Если нужна обработка — используйте AskAsync с await.
            }
        }
    }
}
