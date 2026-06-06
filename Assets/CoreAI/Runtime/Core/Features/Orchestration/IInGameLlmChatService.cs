using System.Threading;
using System.Threading.Tasks;

namespace CoreAI.Ai
{
    /// <summary>
    /// Defines the contract for in game llm chat service implementations.
    /// </summary>
    public interface IInGameLlmChatService
    {
        /// <summary>Sends a player chat message to the in-game LLM chat service.</summary>
        Task<LlmCompletionResult> SendPlayerMessageAsync(string message, CancellationToken cancellationToken = default);

        /// <summary>Clears the stored chat history for the selected agent role.</summary>
        void ClearHistory();

        /// <summary>Number of user/assistant message pairs currently retained in history.</summary>
        int HistoryPairCount { get; }

        /// <summary>Snapshot of the sliding-window rate limiter state for diagnostics / dashboard.</summary>
        RateLimiterMetrics GetRateLimiterMetrics()
        {
            return default;
        }
    }
}