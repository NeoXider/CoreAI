#if COREAI_LLM
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CoreAI.Decisions
{
    /// <summary>Which OpenRouter route carries decisions.</summary>
    public enum OpenRouterDecisionRoute
    {
        /// <summary><c>POST https://openrouter.ai/api/v1/systemone</c>, TypeSafe-compatible (stable).</summary>
        SystemOne = 0,

        /// <summary><c>POST https://openrouter.ai/api/alpha/decisions</c> (alpha; its shape may change).</summary>
        AlphaDecisions = 1
    }

    /// <summary>Options of <see cref="OpenRouterDecisionClient"/>.</summary>
    public sealed class OpenRouterDecisionOptions
    {
        /// <summary>The route; the alpha route sits behind this one switch because it may change.</summary>
        public OpenRouterDecisionRoute Route { get; set; } = OpenRouterDecisionRoute.SystemOne;

        /// <summary>Optional <c>session_id</c> (at most 256 characters) for OpenRouter's grouping.</summary>
        public string SessionId { get; set; } = "";

        /// <summary>Optional end-user id sent as <c>user</c> (at most 256 characters).</summary>
        public string User { get; set; } = "";

        /// <summary>Optional raw JSON object sent as <c>provider</c> (routing preferences such as <c>order</c>).</summary>
        public string ProviderJson { get; set; } = "";
    }

    /// <summary>
    /// <see cref="IDecisionClient"/> for OpenRouter decision models such as <c>typesafe/jev-1.14</c>. The body is
    /// the System One body plus OpenRouter's optional <c>session_id</c>, <c>user</c> and <c>provider</c>; answers
    /// may include a per-question <c>refusal</c>. Errors arrive as <c>{"error": {"code", "message"}}</c>.
    /// </summary>
    public sealed class OpenRouterDecisionClient : IDecisionClient
    {
        /// <summary>Backend name recorded on results.</summary>
        public const string BackendName = "openrouter";

        private const int MaxIdChars = 256;

        private readonly DecisionHttpEndpoint _endpoint;
        private readonly IOpenAiHttpTransport _transport;
        private readonly OpenRouterDecisionOptions _options;
        private readonly SystemOneDialect _dialect = SystemOneDialect.OpenRouter();

        /// <summary>Creates a client for one OpenRouter endpoint (base URL normally <c>https://openrouter.ai/api/v1</c>).</summary>
        public OpenRouterDecisionClient(
            LlmEndpointDescriptor endpoint,
            IOpenAiHttpTransport transport,
            ILlmEndpointSecretProvider secrets = null,
            OpenRouterDecisionOptions options = null)
        {
            _endpoint = new DecisionHttpEndpoint(endpoint, secrets);
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _options = options ?? new OpenRouterDecisionOptions();
        }

        /// <summary>The URL this client posts to.</summary>
        public string Url => _options.Route == OpenRouterDecisionRoute.AlphaDecisions
            ? DecisionUrls.OpenRouterAlphaDecisions(_endpoint.BaseUrl)
            : SystemOneWire.BuildUrl(_endpoint.BaseUrl);

        /// <inheritdoc />
        public async Task<DecisionResult> DecideAsync(DecisionRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<DecisionValidationError> errors = DecisionRequestValidator.Validate(request);
            if (errors.Count > 0)
            {
                return DecisionResult.Invalid(errors, BackendName);
            }

            JObject body = SystemOneWire.BuildRequestBody(request, _endpoint.Model, _dialect,
                out DecisionValidationError wireError);
            if (body == null)
            {
                return DecisionResult.Invalid(new[] { wireError }, BackendName);
            }

            if (!TryApplyOptions(body, out DecisionResult optionsError))
            {
                return optionsError;
            }

            string url = Url;
            string secret = _endpoint.ResolveSecret();
            List<KeyValuePair<string, string>> headers = DecisionHttpExchange.BuildHeaders(url, secret);
            DecisionHttpExchange.Outcome outcome = await DecisionHttpExchange.PostAsync(_transport, _endpoint, url,
                body.ToString(Formatting.None), headers, BackendName, secret, cancellationToken);
            if (outcome.Failure != null)
            {
                return outcome.Failure;
            }

            DecisionResult result = SystemOneWire.ParseResponse(outcome.Body, request, BackendName);
            result.Usage.RoutingProfileId = _endpoint.EndpointId;
            if (result.Usage.Model.Length == 0)
            {
                result.Usage.Model = _endpoint.Model;
            }

            return result;
        }

        private bool TryApplyOptions(JObject body, out DecisionResult error)
        {
            error = null;
            if (!string.IsNullOrWhiteSpace(_options.SessionId))
            {
                body["session_id"] = Clip(_options.SessionId.Trim());
            }

            if (!string.IsNullOrWhiteSpace(_options.User))
            {
                body["user"] = Clip(_options.User.Trim());
            }

            if (!string.IsNullOrWhiteSpace(_options.ProviderJson))
            {
                if (!(ParseObject(_options.ProviderJson) is JObject provider))
                {
                    error = DecisionResult.Failure(LlmErrorCode.InvalidRequest, DecisionErrorReasons.NotConfigured,
                        "OpenRouterDecisionOptions.ProviderJson must be a JSON object.", BackendName);
                    return false;
                }

                body["provider"] = provider;
            }

            return true;
        }

        private static JObject ParseObject(string json)
        {
            try
            {
                return JToken.Parse(json) as JObject;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string Clip(string value)
        {
            return value.Length <= MaxIdChars ? value : value.Substring(0, MaxIdChars);
        }
    }
}
#endif
