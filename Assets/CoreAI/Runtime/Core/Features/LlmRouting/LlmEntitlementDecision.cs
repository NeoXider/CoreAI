namespace CoreAI.Ai
{
    /// <summary>
    /// Result of checking whether a request may use a route profile.
    /// </summary>
    public sealed class LlmEntitlementDecision
    {
        /// <summary>Allowed decision singleton for hosts without entitlement checks.</summary>
        public static readonly LlmEntitlementDecision Allowed = new()
        {
            IsAllowed = true
        };

        /// <summary>True when the request may proceed.</summary>
        public bool IsAllowed { get; set; }

        /// <summary>Stable denial reason such as subscription_required, quota_exceeded, or model_not_allowed.</summary>
        public string ReasonCode { get; set; } = "";

        /// <summary>User-safe denial message.</summary>
        public string Message { get; set; } = "";

        /// <summary>Optional retry hint in seconds.</summary>
        public int? RetryAfterSeconds { get; set; }

        /// <summary>Converts this decision to a provider error.</summary>
        public LlmProviderError ToProviderError()
        {
            return new LlmProviderError
            {
                Code = ReasonCode,
                ErrorCode = LlmProviderError.MapCode(ReasonCode),
                Message = Message,
                RetryAfterSeconds = RetryAfterSeconds
            };
        }
    }
}
