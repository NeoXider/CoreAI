using System.Threading;
using System.Threading.Tasks;

namespace CoreAI.Ai
{
    public interface IAiOrchestrationService
    {
        /// <summary>
        /// Снимок → системный/пользовательский промпт → LLM → команда в шину.
        /// </summary>
        Task RunTaskAsync(AiTaskRequest task, CancellationToken cancellationToken = default);
    }
}
