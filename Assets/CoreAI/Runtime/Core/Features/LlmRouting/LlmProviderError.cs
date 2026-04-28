namespace CoreAI.Ai
{
    /// <summary>
    /// Canonical backend/provider error for UI, retry, entitlement, and telemetry code.
    /// </summary>
    public sealed class LlmProviderError
    {
        /// <summary>Stable domain code such as quota_exceeded or subscription_required.</summary>
        public string Code { get; set; } = "";

        /// <summary>Matching CoreAI error category.</summary>
        public LlmErrorCode ErrorCode { get; set; } = LlmErrorCode.ProviderError;

        /// <summary>User-safe message.</summary>
        public string Message { get; set; } = "";

        /// <summary>HTTP status when available.</summary>
        public int? HttpStatus { get; set; }

        /// <summary>Retry hint in seconds when available.</summary>
        public int? RetryAfterSeconds { get; set; }

        /// <summary>Maps a stable backend code to a CoreAI error category.</summary>
        public static LlmErrorCode MapCode(string code)
        {
            switch ((code ?? "").Trim().ToLowerInvariant())
            {
                case "auth_required":
                case "auth_expired":
                case "subscription_required":
                    return LlmErrorCode.AuthExpired;
                case "quota_exceeded":
                    return LlmErrorCode.QuotaExceeded;
                case "rate_limited":
                    return LlmErrorCode.RateLimited;
                case "model_not_allowed":
                case "invalid_request":
                    return LlmErrorCode.InvalidRequest;
                case "backend_unavailable":
                    return LlmErrorCode.BackendUnavailable;
                default:
                    return LlmErrorCode.ProviderError;
            }
        }
    }
}
