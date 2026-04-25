using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CoreAI.Ai
{
    /// <summary>
    /// Точка входа «задача для ИИ»: промпты, вызов LLM, память, публикация <see cref="CoreAI.Messaging.ApplyAiGameCommand"/>.
    /// Реализация по умолчанию — очередь поверх <see cref="AiOrchestrator"/>.
    /// </summary>
    public interface IAiOrchestrationService
    {
        /// <summary>Снимок → системный/пользовательский промпт → LLM → команда в шину.</summary>
        Task<string> RunTaskAsync(AiTaskRequest task, CancellationToken cancellationToken = default);

        /// <summary>
        /// Стриминговый вариант <see cref="RunTaskAsync"/>: возвращает чанки модели по мере генерации.
        /// По умолчанию делает fallback к <see cref="RunTaskAsync"/> и выдаёт результат одним чанком
        /// с <c>IsDone=true</c>. Конкретные реализации (<see cref="AiOrchestrator"/>,
        /// <see cref="QueuedAiOrchestrator"/>) должны переопределять этот метод, чтобы пользователь
        /// получал токены по мере поступления.
        /// </summary>
        /// <remarks>
        /// ⚠️ Любая обёртка над <see cref="IAiOrchestrationService"/> (очередь, логирование,
        /// таймаут, авторити) <b>обязана</b> явно переопределять этот метод. Иначе default-fallback
        /// тихо убивает стриминг — аналогично контракту <see cref="ILlmClient.CompleteStreamingAsync"/>.
        /// </remarks>
#if UNITY_2021_3_OR_NEWER
        virtual async IAsyncEnumerable<LlmStreamChunk> RunStreamingAsync(
            AiTaskRequest task,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            string content = await RunTaskAsync(task, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(content))
            {
                yield return new LlmStreamChunk { IsDone = true, Error = "empty result" };
                yield break;
            }

            yield return new LlmStreamChunk { Text = content };
            yield return new LlmStreamChunk { IsDone = true, Text = string.Empty };
        }
#endif

        /// <summary>
        /// Отменяет все текущие и ожидающие задачи для указанного scope.
        /// Удобно для ручной остановки агента.
        /// </summary>
        void CancelTasks(string cancellationScope);
    }
}