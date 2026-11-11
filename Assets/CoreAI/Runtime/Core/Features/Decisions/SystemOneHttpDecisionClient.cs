#if COREAI_LLM
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;

namespace CoreAI.Decisions
{
    /// <summary>
    /// <see cref="IDecisionClient"/> over the System One route, <c>POST {base}/systemone</c>: llama.cpp
    /// <c>llama-server</c>, Ollama, LM Studio and TypeSafe. All questions travel in one request; probabilities are
    /// the model's own, so answers are marked calibrated. The network is reached only through the shared
    /// <see cref="IOpenAiHttpTransport"/>, so the WebGL player uses its <c>UnityWebRequest</c> transport here too.
    /// </summary>
    public sealed class SystemOneHttpDecisionClient : IDecisionClient
    {
        /// <summary>Backend name recorded on results.</summary>
        public const string BackendName = "systemone";

        private readonly DecisionHttpEndpoint _endpoint;
        private readonly IOpenAiHttpTransport _transport;
        private readonly SystemOneDialect _dialect;

        /// <summary>Creates a client for one endpoint.</summary>
        /// <param name="endpoint">The endpoint; copied at construction.</param>
        /// <param name="transport">The shared HTTP transport.</param>
        /// <param name="secrets">Resolves <see cref="LlmEndpointDescriptor.SecretReference"/>; null sends no key.</param>
        /// <param name="dialect">Server differences; null picks one from the base URL.</param>
        public SystemOneHttpDecisionClient(
            LlmEndpointDescriptor endpoint,
            IOpenAiHttpTransport transport,
            ILlmEndpointSecretProvider secrets = null,
            SystemOneDialect dialect = null)
        {
            _endpoint = new DecisionHttpEndpoint(endpoint, secrets);
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _dialect = dialect ?? SystemOneDialect.ForBaseUrl(_endpoint.BaseUrl);
        }

        /// <summary>The dialect in use.</summary>
        public SystemOneDialect Dialect => _dialect;

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

            string json = SystemOneWire.BuildRequestJson(request, _endpoint.Model, _dialect,
                out DecisionValidationError wireError);
            if (json == null)
            {
                return DecisionResult.Invalid(new[] { wireError }, BackendName);
            }

            string url = SystemOneWire.BuildUrl(_endpoint.BaseUrl);
            string secret = _endpoint.ResolveSecret();
            List<KeyValuePair<string, string>> headers = DecisionHttpExchange.BuildHeaders(url, secret);
            if (_dialect.SendImageOptInHeader && request.Images.Count > 0)
            {
                headers.Add(new KeyValuePair<string, string>(SystemOneWire.ImageOptInHeader, "true"));
            }

            DecisionHttpExchange.Outcome outcome = await DecisionHttpExchange.PostAsync(_transport, _endpoint, url,
                json, headers, BackendName, secret, cancellationToken);
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
    }
}
#endif
