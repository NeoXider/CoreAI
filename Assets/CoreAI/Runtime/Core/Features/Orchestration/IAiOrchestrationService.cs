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
        Task RunTaskAsync(AiTaskRequest task, CancellationToken cancellationToken = default);
    }
}