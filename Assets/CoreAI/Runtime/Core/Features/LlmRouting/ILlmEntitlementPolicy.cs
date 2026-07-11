using System.Threading;
using System.Threading.Tasks;

namespace CoreAI.Ai
{
    /// <summary>
    /// Checks subscription, quota, model policy, and rate limits before an LLM request proceeds.
    /// </summary>
    public interface ILlmEntitlementPolicy
    {
        /// <summary>Returns whether the request may use the resolved route profile.</summary>
        Task<LlmEntitlementDecision> CheckAsync(
            LlmCompletionRequest request,
            LlmRouteProfile profile,
            CancellationToken cancellationToken = default);
    }
}
