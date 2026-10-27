#if COREAI_LLM
using System;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;

namespace CoreAI.Infrastructure.Llm.Gateway
{
    /// <summary>Optional server preflight; a valid server denial is never replaced by availability fallback.</summary>
    public sealed class GatewayEntitlementPolicy : ILlmEntitlementPolicy
    {
        private readonly IGatewayServiceClient _service;
        private readonly GatewayPreflightUnavailablePolicy _unavailable;

        public GatewayEntitlementPolicy(IGatewayServiceClient service,
            GatewayPreflightUnavailablePolicy unavailable = GatewayPreflightUnavailablePolicy.Allow)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _unavailable = unavailable;
        }

        public async Task<LlmEntitlementDecision> CheckAsync(LlmCompletionRequest request,
            LlmRouteProfile profile, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GatewayEntitlementSnapshot snapshot = await _service.CheckEntitlementAsync(
                request?.AgentRoleId ?? "", cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (snapshot == null || !snapshot.IsAvailable)
            {
                if (_unavailable == GatewayPreflightUnavailablePolicy.Allow)
                    return LlmEntitlementDecision.Allowed;
                throw new LlmClientException("Gateway preflight is unavailable.",
                    LlmErrorCode.BackendUnavailable, snapshot?.HttpStatus, snapshot?.RetryAfterSeconds);
            }
            return snapshot.Allowed ? LlmEntitlementDecision.Allowed : new LlmEntitlementDecision
            {
                IsAllowed = false,
                ReasonCode = snapshot.ReasonCode ?? "",
                Message = string.IsNullOrWhiteSpace(snapshot.Message) ? "Gateway access was denied." : snapshot.Message,
                RetryAfterSeconds = snapshot.RetryAfterSeconds
            };
        }
    }
}
#endif
