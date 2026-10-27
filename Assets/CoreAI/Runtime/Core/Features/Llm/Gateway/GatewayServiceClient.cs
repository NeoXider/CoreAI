#if COREAI_LLM
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CoreAI.Infrastructure.Llm.Gateway
{
    /// <summary>Optional v1 entitlement and usage protocol; server policy remains authoritative.</summary>
    public interface IGatewayServiceClient
    {
        Task<GatewayEntitlementSnapshot> CheckEntitlementAsync(string roleId,
            CancellationToken cancellationToken = default);
        Task ReportUsageAsync(GatewayUsagePayload payload, CancellationToken cancellationToken = default);
    }

    /// <summary>Unavailable is distinct from a valid positive or negative entitlement decision.</summary>
    public sealed class GatewayEntitlementSnapshot
    {
        public bool IsAvailable { get; set; }
        public bool Allowed { get; set; }
        public string ReasonCode { get; set; } = "";
        public string Message { get; set; } = "";
        public int? RetryAfterSeconds { get; set; }
        public int? HttpStatus { get; set; }
        public string PlanCode { get; set; } = "";
        public long TokensUsed { get; set; }
        public long TokensLimit { get; set; }
    }

    /// <summary>Observed CoreAiPro v1 wire fields; this is optional client analytics, not billing.</summary>
    public sealed class GatewayUsagePayload
    {
        [JsonProperty("trace_id")] public string TraceId { get; set; } = "";
        [JsonProperty("role_id")] public string RoleId { get; set; } = "";
        [JsonProperty("routing_profile_id")] public string RoutingProfileId { get; set; } = "";
        [JsonProperty("model")] public string Model { get; set; } = "";
        [JsonProperty("prompt_tokens")] public int PromptTokens { get; set; }
        [JsonProperty("completion_tokens")] public int CompletionTokens { get; set; }
        [JsonProperty("total_tokens")] public int TotalTokens { get; set; }
        [JsonProperty("streaming")] public bool Streaming { get; set; }
        [JsonProperty("success")] public bool Success { get; set; }
        [JsonProperty("estimated")] public bool Estimated { get; set; }
    }

    /// <summary>Bounded optional protocol client adapted from Neoxider CoreAiPro 0.1.0.</summary>
    public sealed class GatewayServiceClient : IGatewayServiceClient
    {
        private readonly GatewayClientOptions _options;
        private readonly IGatewayHttpTransport _transport;
        private readonly Func<string> _authorization;
        private readonly string _baseUrl;

        public GatewayServiceClient(GatewayClientOptions options, IGatewayHttpTransport transport,
            Func<string>? authorizationHeaderFactory = null)
        {
            _options = (options ?? throw new ArgumentNullException(nameof(options))).Snapshot();
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _authorization = authorizationHeaderFactory ?? (() => "");
            _baseUrl = GatewayClientOptions.ValidateEndpoint(_options.GatewayBaseUrl,
                _options.AllowInsecureHttp).AbsoluteUri.TrimEnd('/');
            ValidatePath(_options.EntitlementPath);
            ValidatePath(_options.UsagePath);
        }

        public async Task<GatewayEntitlementSnapshot> CheckEntitlementAsync(string roleId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                GatewayHttpResponse response = await _transport.SendAsync(CreateRequest("GET",
                    _options.EntitlementPath + "?role_id=" + Uri.EscapeDataString(roleId ?? ""), ""),
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                string responseBody = response.BodyText ?? "";
                if (!response.IsSuccess || Encoding.UTF8.GetByteCount(responseBody) > _options.MaxResponseBytes)
                    return Unavailable(response.StatusCode, RetryAfter(response));
                JObject body = JObject.Parse(responseBody);
                if (body["allowed"]?.Type != JTokenType.Boolean)
                    return Unavailable(response.StatusCode, RetryAfter(response));
                return new GatewayEntitlementSnapshot
                {
                    IsAvailable = true,
                    Allowed = body["allowed"]!.Value<bool>(),
                    ReasonCode = body["reason_code"]?.Type == JTokenType.String
                        ? body["reason_code"]!.Value<string>() ?? "" : "",
                    Message = body["message"]?.Type == JTokenType.String
                        ? body["message"]!.Value<string>() ?? "" : "",
                    RetryAfterSeconds = body["retry_after_seconds"]?.Type == JTokenType.Integer
                        ? (int?)Math.Max(0, Math.Min(int.MaxValue, body["retry_after_seconds"]!.Value<long>()))
                        : RetryAfter(response),
                    HttpStatus = response.StatusCode,
                    PlanCode = body["plan_code"]?.Type == JTokenType.String
                        ? body["plan_code"]!.Value<string>() ?? "" : "",
                    TokensUsed = body["tokens_used"]?.Type == JTokenType.Integer
                        ? body["tokens_used"]!.Value<long>() : 0,
                    TokensLimit = body["tokens_limit"]?.Type == JTokenType.Integer
                        ? body["tokens_limit"]!.Value<long>() : 0
                };
            }
            catch (OperationCanceledException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Unavailable();
            }
            catch (Exception)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Unavailable();
            }
        }

        public async Task ReportUsageAsync(GatewayUsagePayload payload,
            CancellationToken cancellationToken = default)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            cancellationToken.ThrowIfCancellationRequested();
            GatewayHttpResponse response = await _transport.SendAsync(
                CreateRequest("POST", _options.UsagePath, JsonConvert.SerializeObject(payload)), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!response.IsSuccess)
                throw new LlmClientException("Gateway usage service rejected the report.",
                    LlmErrorCode.BackendUnavailable, response.StatusCode, RetryAfter(response));
        }

        private GatewayHttpRequest CreateRequest(string method, string path, string body)
        {
            List<KeyValuePair<string, string>> headers = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("Accept", "application/json")
            };
            string authorization = _authorization() ?? "";
            if (!string.IsNullOrWhiteSpace(authorization))
                headers.Add(new KeyValuePair<string, string>("Authorization", authorization));
            return new GatewayHttpRequest
            {
                Method = method, Url = _baseUrl + path, JsonBody = body,
                Headers = headers, MaxResponseBytes = _options.MaxResponseBytes,
                TimeoutMilliseconds = _options.SideCallTimeoutMilliseconds
            };
        }

        private static void ValidatePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !path.StartsWith("/", StringComparison.Ordinal) ||
                path.StartsWith("//", StringComparison.Ordinal) || path.Contains("?") || path.Contains("#") ||
                path.Contains("\\") || path.Contains("\r") || path.Contains("\n"))
                throw new ArgumentException("Gateway service paths must be absolute paths without query or fragment.");
        }

        private static GatewayEntitlementSnapshot Unavailable(int? status = null, int? retry = null) =>
            new GatewayEntitlementSnapshot { HttpStatus = status, RetryAfterSeconds = retry };

        private static int? RetryAfter(GatewayHttpResponse response)
        {
            foreach (KeyValuePair<string, IEnumerable<string>> header in response.Headers)
            {
                if (!string.Equals(header.Key, "Retry-After", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (string value in header.Value)
                {
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds))
                        return Math.Max(0, seconds);
                }
            }
            return null;
        }
    }
}
#endif
