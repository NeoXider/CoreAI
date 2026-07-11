using System.Threading;
using System.Threading.Tasks;

namespace CoreAI.Ai
{
    /// <summary>
    /// Default entitlement policy that allows all requests.
    /// </summary>
    public sealed class AllowAllLlmEntitlementPolicy : ILlmEntitlementPolicy
    {
        /// <inheritdoc />
        public Task<LlmEntitlementDecision> CheckAsync(
            LlmCompletionRequest request,
            LlmRouteProfile profile,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(LlmEntitlementDecision.Allowed);
        }
    }
}
