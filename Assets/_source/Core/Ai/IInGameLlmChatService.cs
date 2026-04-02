using System.Threading;
using System.Threading.Tasks;

namespace CoreAI.Ai
{
    /// <summary>
    /// Диалог с игроком (как с GPT): история в памяти, системный промпт из провайдера для роли PlayerChat.
    /// </summary>
    public interface IInGameLlmChatService
    {
        Task<LlmCompletionResult> SendPlayerMessageAsync(string message, CancellationToken cancellationToken = default);

        void ClearHistory();

        int HistoryPairCount { get; }
    }
}
