using System;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
#if COREAI_LLM
using CoreAI.Infrastructure.Llm;
#endif

namespace CoreAI.Decisions
{
    /// <summary>Builds the backend client for one endpoint descriptor. Replace it to add a backend.</summary>
    public interface IDecisionClientFactory
    {
        /// <summary>A client for <paramref name="endpoint"/>; never null.</summary>
        IDecisionClient Create(LlmEndpointDescriptor endpoint);
    }

    /// <summary>A client that answers every call with one fixed failure.</summary>
    public sealed class UnavailableDecisionClient : IDecisionClient
    {
        private readonly LlmErrorCode _errorCode;
        private readonly string _reason;
        private readonly string _message;

        /// <summary>Creates a client that always fails with the given error.</summary>
        public UnavailableDecisionClient(LlmErrorCode errorCode, string reason, string message)
        {
            _errorCode = errorCode;
            _reason = reason ?? "";
            _message = message ?? "";
        }

        /// <inheritdoc />
        public Task<DecisionResult> DecideAsync(DecisionRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(DecisionResult.Failure(_errorCode, _reason, _message, "unavailable"));
        }
    }

    /// <summary>
    /// The factory of a build without an HTTP backend (no <c>COREAI_LLM</c> define): every endpoint gets a client
    /// that fails with <see cref="LlmErrorCode.NotConfigured"/>, so consumers fall back instead of crashing.
    /// </summary>
    public sealed class UnavailableDecisionClientFactory : IDecisionClientFactory
    {
        /// <inheritdoc />
        public IDecisionClient Create(LlmEndpointDescriptor endpoint)
        {
            return new UnavailableDecisionClient(LlmErrorCode.NotConfigured, DecisionErrorReasons.NotConfigured,
                "Decision backends need the COREAI_LLM scripting define (the HTTP transport is compiled out).");
        }
    }

#if COREAI_LLM
    /// <summary>Options shared by the clients <see cref="DecisionClientFactory"/> builds.</summary>
    public sealed class DecisionClientFactoryOptions
    {
        /// <summary>Options of OpenRouter clients (the route switch).</summary>
        public OpenRouterDecisionOptions OpenRouter { get; set; } = new();

        /// <summary>Options of logprob fallback clients.</summary>
        public LogprobDecisionOptions Logprobs { get; set; } = new();

        /// <summary>Forces one System One dialect; null picks one from each base URL.</summary>
        public SystemOneDialect SystemOneDialect { get; set; }
    }

    /// <summary>
    /// Default factory: picks the backend from <see cref="LlmEndpointDescriptor.DecisionProtocol"/> and gives every
    /// client the same shared transport and secret provider.
    /// </summary>
    public sealed class DecisionClientFactory : IDecisionClientFactory
    {
        private readonly IOpenAiHttpTransport _transport;
        private readonly ILlmEndpointSecretProvider _secrets;
        private readonly DecisionClientFactoryOptions _options;

        /// <summary>Creates a factory.</summary>
        public DecisionClientFactory(IOpenAiHttpTransport transport, ILlmEndpointSecretProvider secrets = null,
            DecisionClientFactoryOptions options = null)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _secrets = secrets;
            _options = options ?? new DecisionClientFactoryOptions();
        }

        /// <inheritdoc />
        public IDecisionClient Create(LlmEndpointDescriptor endpoint)
        {
            if (endpoint == null)
            {
                throw new ArgumentNullException(nameof(endpoint));
            }

            switch (endpoint.DecisionProtocol)
            {
                case LlmDecisionProtocol.SystemOne:
                    return new SystemOneHttpDecisionClient(endpoint, _transport, _secrets, _options.SystemOneDialect);
                case LlmDecisionProtocol.OpenRouter:
                    return new OpenRouterDecisionClient(endpoint, _transport, _secrets, _options.OpenRouter);
                case LlmDecisionProtocol.ChatLogprobs:
                    return new LogprobDecisionClient(endpoint, _transport, _secrets, _options.Logprobs);
                default:
                    return new UnavailableDecisionClient(LlmErrorCode.RoutingError, DecisionErrorReasons.CapabilityMissing,
                        $"Endpoint '{endpoint.EndpointId}' declares no decision protocol.");
            }
        }
    }
#endif
}
