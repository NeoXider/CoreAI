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
    /// The HTTP fields of an endpoint descriptor that a decision client needs, copied at construction so a later
    /// edit of the descriptor object cannot change a client mid-call. The credential is never stored here: it is
    /// resolved from <see cref="SecretReference"/> through the host's <see cref="ILlmEndpointSecretProvider"/> on
    /// every call and never logged.
    /// </summary>
    public sealed class DecisionHttpEndpoint
    {
        /// <summary>Transport ceiling used when the descriptor leaves its timeouts unset.</summary>
        public const int DefaultTransportTimeoutSeconds = 30;

        private readonly ILlmEndpointSecretProvider _secrets;

        /// <summary>Copies the decision-relevant fields of <paramref name="descriptor"/>.</summary>
        public DecisionHttpEndpoint(LlmEndpointDescriptor descriptor, ILlmEndpointSecretProvider secrets = null)
        {
            if (descriptor == null)
            {
                throw new ArgumentNullException(nameof(descriptor));
            }

            EndpointId = descriptor.EndpointId?.Trim() ?? "";
            BaseUrl = descriptor.BaseUrl?.Trim() ?? "";
            Model = descriptor.Model?.Trim() ?? "";
            SecretReference = descriptor.SecretReference?.Trim() ?? "";
            TransportTimeoutSeconds = Math.Max(0, descriptor.RequestTimeoutSeconds ?? DefaultTransportTimeoutSeconds);
            ResponseWaitTimeoutSeconds =
                Math.Max(0, descriptor.ResponseWaitTimeoutSeconds ?? DefaultTransportTimeoutSeconds);
            _secrets = secrets;
        }

        /// <summary>Endpoint id.</summary>
        public string EndpointId { get; }

        /// <summary>OpenAI-style base URL (normally ending in <c>/v1</c>).</summary>
        public string BaseUrl { get; }

        /// <summary>Model id sent in the body; llama.cpp ignores it.</summary>
        public string Model { get; }

        /// <summary>Host-owned lookup key of the credential; empty for keyless local servers.</summary>
        public string SecretReference { get; }

        /// <summary>Transport ceiling in seconds; the per-call deadline lives in <see cref="BudgetedDecisionClient"/>.</summary>
        public int TransportTimeoutSeconds { get; }

        /// <summary>Response wait ceiling in seconds.</summary>
        public int ResponseWaitTimeoutSeconds { get; }

        /// <summary>The credential for this call, or empty. Never log the value.</summary>
        internal string ResolveSecret()
        {
            if (SecretReference.Length == 0 || _secrets == null)
            {
                return "";
            }

            return _secrets.TryResolve(SecretReference, out string secret) ? secret?.Trim() ?? "" : "";
        }
    }

    /// <summary>One decision POST over the shared <see cref="IOpenAiHttpTransport"/>, with failures as results.</summary>
    internal static class DecisionHttpExchange
    {
        internal readonly struct Outcome
        {
            public Outcome(string body, DecisionResult failure)
            {
                Body = body ?? "";
                Failure = failure;
            }

            public string Body { get; }

            public DecisionResult Failure { get; }
        }

        internal static List<KeyValuePair<string, string>> BuildHeaders(string url, string secret)
        {
            List<KeyValuePair<string, string>> headers = new();
            if (!string.IsNullOrEmpty(secret))
            {
                headers.Add(new KeyValuePair<string, string>("Authorization", "Bearer " + secret));
            }

            if ((url ?? "").IndexOf("openrouter", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                headers.Add(new KeyValuePair<string, string>(OpenAiHttpConstants.HttpRefererHeaderName,
                    OpenAiHttpConstants.HttpRefererUnityUrl));
                headers.Add(new KeyValuePair<string, string>("X-Title", "CoreAI"));
            }

            return headers;
        }

        internal static async Task<Outcome> PostAsync(
            IOpenAiHttpTransport transport,
            DecisionHttpEndpoint endpoint,
            string url,
            string json,
            IReadOnlyList<KeyValuePair<string, string>> headers,
            string backend,
            string secret,
            CancellationToken cancellationToken)
        {
            try
            {
                OpenAiHttpPostResult result = await transport.PostNonStreamingAsync(new OpenAiHttpPostRequest
                {
                    Url = url,
                    JsonBody = json,
                    AcceptEventStream = false,
                    TransportTimeoutSeconds = endpoint.TransportTimeoutSeconds,
                    ResponseWaitTimeoutSeconds = endpoint.ResponseWaitTimeoutSeconds,
                    Headers = headers
                }, cancellationToken);

                if (result == null)
                {
                    return new Outcome("", DecisionResult.Failure(LlmErrorCode.BackendUnavailable,
                        DecisionErrorReasons.TransportFailed, "The decision transport returned no result.", backend));
                }

                if (!result.IsSuccessStatusCode)
                {
                    return new Outcome("", DecisionErrorMapper.FromHttpFailure(result.StatusCode, result.BodyText,
                        backend, secret));
                }

                return new Outcome(result.BodyText, null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return new Outcome("", DecisionResult.Failure(LlmErrorCode.Timeout, DecisionErrorReasons.TransportFailed,
                    "The decision transport timed out.", backend));
            }
            catch (LlmClientException ex)
            {
                return new Outcome("", DecisionResult.Failure(ex.ErrorCode, DecisionErrorReasons.TransportFailed,
                    DecisionErrorMapper.Redact(ex.Message, secret), backend, null, ex.HttpStatus));
            }
            catch (Exception ex)
            {
                return new Outcome("", DecisionResult.Failure(LlmErrorCode.BackendUnavailable,
                    DecisionErrorReasons.TransportFailed,
                    "Decision transport failed (" + ex.GetType().Name + "): " +
                    DecisionErrorMapper.Redact(ex.Message, secret), backend));
            }
        }
    }
}
#endif
