using System.Threading;
using System.Threading.Tasks;

namespace CoreAI.Decisions
{
    /// <summary>
    /// Asks a decision model typed questions about one state and returns, per question, the chosen option, the
    /// full probability vector and a confidence, in one call.
    /// <para>
    /// Contract shared by every backend and decorator (System One HTTP, OpenRouter, the chat-logprob fallback,
    /// budgets, routing): a failure is a <see cref="DecisionResult"/> carrying an
    /// <see cref="CoreAI.Ai.LlmErrorCode"/> and a named <see cref="DecisionResult.ErrorReason"/>, never an
    /// exception. The one exception is cancellation through the caller's token, which surfaces as
    /// <see cref="System.OperationCanceledException"/>. An invalid request is refused before anything is sent.
    /// </para>
    /// </summary>
    public interface IDecisionClient
    {
        /// <summary>Answers every question of <paramref name="request"/> in one call.</summary>
        Task<DecisionResult> DecideAsync(DecisionRequest request, CancellationToken cancellationToken = default);
    }
}
