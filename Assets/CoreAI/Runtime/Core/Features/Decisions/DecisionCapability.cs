using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;

namespace CoreAI.Decisions
{
    /// <summary>Status and body of one HTTP GET.</summary>
    public sealed class DecisionHttpResponse
    {
        /// <summary>HTTP status; 0 when no response arrived.</summary>
        public int StatusCode { get; set; }

        /// <summary>Response body.</summary>
        public string BodyText { get; set; } = "";
    }

    /// <summary>
    /// The one GET the decision layer needs (the model list). The chat transport only posts, so this seam sits next
    /// to it: the portable default uses <see cref="HttpClient"/>, the Unity host supplies a <c>UnityWebRequest</c>
    /// implementation for the WebGL player.
    /// </summary>
    public interface IDecisionHttpGet
    {
        /// <summary>Performs a GET; failures to connect may throw or return status 0.</summary>
        Task<DecisionHttpResponse> GetAsync(string url, IReadOnlyList<KeyValuePair<string, string>> headers,
            int timeoutSeconds, CancellationToken cancellationToken);
    }

    /// <summary><see cref="HttpClient"/> GET for editor, desktop, mobile and plain .NET hosts (not the WebGL player).</summary>
    public sealed class HttpClientDecisionHttpGet : IDecisionHttpGet, IDisposable
    {
        private readonly HttpClient _client;
        private readonly bool _ownsClient;

        /// <summary>Creates a GET seam over its own client.</summary>
        public HttpClientDecisionHttpGet()
            : this(new HttpClient { Timeout = Timeout.InfiniteTimeSpan }, true)
        {
        }

        /// <summary>Creates a GET seam over a caller-owned client (tests, shared clients).</summary>
        public HttpClientDecisionHttpGet(HttpClient client, bool ownsClient = false)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _ownsClient = ownsClient;
        }

        /// <inheritdoc />
        public async Task<DecisionHttpResponse> GetAsync(string url,
            IReadOnlyList<KeyValuePair<string, string>> headers, int timeoutSeconds, CancellationToken cancellationToken)
        {
            using HttpRequestMessage message = new(HttpMethod.Get, url);
            foreach (KeyValuePair<string, string> header in headers ?? Array.Empty<KeyValuePair<string, string>>())
            {
                if (string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase) &&
                    AuthenticationHeaderValue.TryParse(header.Value, out AuthenticationHeaderValue auth))
                {
                    message.Headers.Authorization = auth;
                }
                else
                {
                    message.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            Task<HttpResponseMessage> send = _client.SendAsync(message, HttpCompletionOption.ResponseContentRead,
                cancellationToken);
            using HttpResponseMessage response = await send;
            string body = response.Content == null ? "" : await response.Content.ReadAsStringAsync();
            return new DecisionHttpResponse { StatusCode = (int)response.StatusCode, BodyText = body ?? "" };
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_ownsClient)
            {
                _client.Dispose();
            }
        }
    }

    /// <summary>What an endpoint's model list says about decisions.</summary>
    public sealed class DecisionCapabilityReport
    {
        /// <summary>Endpoint id.</summary>
        public string EndpointId { get; set; } = "";

        /// <summary>Model asked about.</summary>
        public string Model { get; set; } = "";

        /// <summary>True when the model list was read.</summary>
        public bool Reachable { get; set; }

        /// <summary>HTTP status of the model list; 0 when nothing answered.</summary>
        public int StatusCode { get; set; }

        /// <summary>True when the model appears in the list (or is the only one listed).</summary>
        public bool ModelListed { get; set; }

        /// <summary>True when the model lists <c>"decisions"</c> as an output modality.</summary>
        public bool AdvertisesDecisions { get; set; }

        /// <summary>True when the server lists supported parameters and <c>logprobs</c> is among them.</summary>
        public bool AdvertisesLogprobs { get; set; }

        /// <summary>True when the server lists supported parameters at all.</summary>
        public bool SupportedParametersListed { get; set; }

        /// <summary>Credential-free description of a failed read.</summary>
        public string Error { get; set; } = "";

        /// <summary>
        /// Whether the list proves <paramref name="protocol"/> unusable: a System One or OpenRouter model must
        /// advertise <c>"decisions"</c>; a logprob model must be listed, and when the server lists supported
        /// parameters <c>logprobs</c> must be one. An unreachable list proves nothing and never refuses.
        /// </summary>
        public bool Refutes(LlmDecisionProtocol protocol)
        {
            if (!Reachable)
            {
                return false;
            }

            switch (protocol)
            {
                case LlmDecisionProtocol.SystemOne:
                case LlmDecisionProtocol.OpenRouter:
                    return !AdvertisesDecisions;
                case LlmDecisionProtocol.ChatLogprobs:
                    return !ModelListed || (SupportedParametersListed && !AdvertisesLogprobs);
                default:
                    return true;
            }
        }
    }

    /// <summary>Reads an endpoint's model list to verify a declared decision protocol.</summary>
    public interface IDecisionCapabilityProbe
    {
        /// <summary>Probes <paramref name="endpoint"/>; never throws for network failures.</summary>
        Task<DecisionCapabilityReport> ProbeAsync(LlmEndpointDescriptor endpoint, CancellationToken cancellationToken);
    }

    /// <summary>Default probe: <c>GET {base}/models</c> parsed by <see cref="DecisionModelListWire"/>.</summary>
    public sealed class ModelListDecisionCapabilityProbe : IDecisionCapabilityProbe
    {
        /// <summary>Timeout of the model-list read.</summary>
        public const int DefaultTimeoutSeconds = 10;

        private readonly IDecisionHttpGet _http;
        private readonly ILlmEndpointSecretProvider _secrets;
        private readonly IDecisionClock _clock;

        /// <summary>Creates a probe.</summary>
        /// <param name="http">The GET seam.</param>
        /// <param name="secrets">Resolves the endpoint's secret reference; null sends no key.</param>
        /// <param name="clock">Bounds the read to <see cref="DefaultTimeoutSeconds"/>; null leaves it to the seam.</param>
        public ModelListDecisionCapabilityProbe(IDecisionHttpGet http, ILlmEndpointSecretProvider secrets = null,
            IDecisionClock clock = null)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _secrets = secrets;
            _clock = clock;
        }

        /// <inheritdoc />
        public async Task<DecisionCapabilityReport> ProbeAsync(LlmEndpointDescriptor endpoint,
            CancellationToken cancellationToken)
        {
            if (endpoint == null)
            {
                throw new ArgumentNullException(nameof(endpoint));
            }

            DecisionCapabilityReport report = new()
            {
                EndpointId = endpoint.EndpointId?.Trim() ?? "",
                Model = endpoint.Model?.Trim() ?? ""
            };
            string url = DecisionModelListWire.BuildUrl(endpoint.BaseUrl);
            if (url.Length == 0)
            {
                report.Error = "The endpoint has no base URL.";
                return report;
            }

            string secret = "";
            string reference = endpoint.SecretReference?.Trim() ?? "";
            if (reference.Length > 0 && _secrets != null && _secrets.TryResolve(reference, out string resolved))
            {
                secret = resolved?.Trim() ?? "";
            }

            List<KeyValuePair<string, string>> headers = new();
            if (secret.Length > 0)
            {
                headers.Add(new KeyValuePair<string, string>("Authorization", "Bearer " + secret));
            }

            DecisionHttpResponse response;
            using CancellationTokenSource readCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                Task<DecisionHttpResponse> read = _http.GetAsync(url, headers, DefaultTimeoutSeconds,
                    readCancellation.Token);
                if (_clock != null && !await FinishesInTimeAsync(read, readCancellation, cancellationToken))
                {
                    report.Error = $"The model list did not answer within {DefaultTimeoutSeconds} s.";
                    return report;
                }

                response = await read;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                report.Error = "The model list could not be read (" + ex.GetType().Name + ").";
                return report;
            }

            report.StatusCode = response?.StatusCode ?? 0;
            if (response == null || report.StatusCode < 200 || report.StatusCode >= 300)
            {
                report.Error = $"The model list answered HTTP {report.StatusCode}.";
                return report;
            }

            IReadOnlyList<DecisionModelInfo> models = DecisionModelListWire.Parse(response.BodyText);
            if (models.Count == 0)
            {
                report.Error = "The model list is empty or malformed.";
                return report;
            }

            report.Reachable = true;
            DecisionModelInfo model = DecisionModelListWire.Find(models, report.Model);
            if (model == null)
            {
                report.Error = $"Model '{report.Model}' is not in the model list.";
                return report;
            }

            report.ModelListed = true;
            report.AdvertisesDecisions = model.AdvertisesDecisions;
            report.SupportedParametersListed = model.SupportedParametersListed;
            report.AdvertisesLogprobs = model.AdvertisesLogprobs;
            return report;
        }

        private async Task<bool> FinishesInTimeAsync(Task read, CancellationTokenSource readCancellation,
            CancellationToken cancellationToken)
        {
            using CancellationTokenSource delayCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task delay = _clock.DelayAsync(DefaultTimeoutSeconds * 1000, delayCancellation.Token);
            Task first = await Task.WhenAny(read, delay);
            if (first == read)
            {
                delayCancellation.Cancel();
                _ = ObserveAsync(delay);
                return true;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (delay.Status != TaskStatus.RanToCompletion)
            {
                await read;
                return true;
            }

            readCancellation.Cancel();
            _ = ObserveAsync(read);
            return false;
        }

        private static async Task ObserveAsync(Task task)
        {
            try
            {
                await task;
            }
            catch (Exception)
            {
                // WHY: the outcome was already reported; this only keeps the abandoned task observed.
            }
        }
    }
}
