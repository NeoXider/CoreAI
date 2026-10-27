#if COREAI_LLM
using System;

namespace CoreAI.Infrastructure.Llm.Gateway
{
    /// <summary>Controls optional gateway services independently of the completion endpoint.</summary>
    public sealed class GatewayClientOptions
    {
        public bool EnableClientPreflight { get; set; }
        public bool ReportUsageToGateway { get; set; }
        public string GatewayBaseUrl { get; set; } = "";
        public string EntitlementPath { get; set; } = "/entitlement";
        public string UsagePath { get; set; } = "/usage";
        public int SideCallTimeoutMilliseconds { get; set; } = 15000;
        public int UsageTimeoutMilliseconds { get; set; } = 2000;
        public int MaxConcurrentUsageReports { get; set; } = 4;
        public int MaxResponseBytes { get; set; } = 65536;
        public bool AllowInsecureHttp { get; set; }
        public GatewayPreflightUnavailablePolicy PreflightUnavailablePolicy { get; set; } =
            GatewayPreflightUnavailablePolicy.Allow;

        /// <summary>Validates and isolates the configuration used by a constructed client.</summary>
        public GatewayClientOptions Snapshot()
        {
            if (SideCallTimeoutMilliseconds <= 0 || UsageTimeoutMilliseconds <= 0 ||
                MaxConcurrentUsageReports <= 0 || MaxResponseBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(GatewayClientOptions),
                    "Gateway timeouts, concurrency and response bounds must be positive.");
            }

            if (!Enum.IsDefined(typeof(GatewayPreflightUnavailablePolicy), PreflightUnavailablePolicy))
            {
                throw new ArgumentOutOfRangeException(nameof(PreflightUnavailablePolicy));
            }

            return new GatewayClientOptions
            {
                EnableClientPreflight = EnableClientPreflight,
                ReportUsageToGateway = ReportUsageToGateway,
                GatewayBaseUrl = GatewayBaseUrl ?? "",
                EntitlementPath = EntitlementPath ?? "",
                UsagePath = UsagePath ?? "",
                SideCallTimeoutMilliseconds = SideCallTimeoutMilliseconds,
                UsageTimeoutMilliseconds = UsageTimeoutMilliseconds,
                MaxConcurrentUsageReports = MaxConcurrentUsageReports,
                MaxResponseBytes = MaxResponseBytes,
                AllowInsecureHttp = AllowInsecureHttp,
                PreflightUnavailablePolicy = PreflightUnavailablePolicy
            };
        }

        /// <summary>Validates an explicitly selected credential destination without disclosing it.</summary>
        public static Uri ValidateEndpoint(string endpoint, bool allowInsecureHttp)
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri uri) ||
                !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) ||
                !string.IsNullOrEmpty(uri.Query) ||
                (uri.Scheme != Uri.UriSchemeHttps &&
                 !(allowInsecureHttp && uri.Scheme == Uri.UriSchemeHttp)))
            {
                throw new ArgumentException(
                    "Gateway endpoints require HTTPS or explicitly enabled HTTP.",
                    nameof(endpoint));
            }

            return uri;
        }
    }

    /// <summary>Availability behavior for missing, invalid or unavailable optional preflight responses.</summary>
    public enum GatewayPreflightUnavailablePolicy
    {
        Allow = 0,
        Deny = 1
    }
}
#endif
