#if COREAI_LLM
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// HTTP transport for OpenAI-compatible <c>/chat/completions</c>.
    /// Core ships <see cref="HttpClientOpenAiTransport"/>; WebGL player uses UnityWebRequest implementation in the Unity integration package.
    /// </summary>
    public interface IOpenAiHttpTransport
    {
        string DebugLabel { get; }

        /// <summary>When false, streaming requests use full JSON completion and simulated ChatResponseUpdate sequence.</summary>
        bool SupportsSseStreaming { get; }

        Task<OpenAiHttpPostResult> PostNonStreamingAsync(OpenAiHttpPostRequest request,
            CancellationToken cancellationToken = default);

        /// <summary>Only used when <see cref="SupportsSseStreaming"/> is true. Caller must dispose the result.</summary>
        Task<OpenAiHttpSseOpenResult> OpenSseResponseStreamAsync(OpenAiHttpPostRequest request,
            CancellationToken cancellationToken = default);
    }

    public sealed class OpenAiHttpPostRequest
    {
        public string Url { get; set; } = "";
        public string JsonBody { get; set; } = "";
        public bool AcceptEventStream { get; set; }
        public int TransportTimeoutSeconds { get; set; }

        public IReadOnlyList<KeyValuePair<string, string>> Headers { get; set; } =
            new List<KeyValuePair<string, string>>();
    }

    public sealed class OpenAiHttpPostResult
    {
        public int StatusCode { get; set; }
        public string BodyText { get; set; } = "";
        public bool IsSuccessStatusCode => StatusCode >= 200 && StatusCode < 300;

        public IReadOnlyDictionary<string, IEnumerable<string>> ResponseHeaders { get; set; } =
            new Dictionary<string, IEnumerable<string>>();
    }

    /// <summary>Owns the response stream and any native HTTP resources; dispose after reading the body.</summary>
    public sealed class OpenAiHttpSseOpenResult : System.IDisposable
    {
        private System.IDisposable? _responseDispose;
        private Stream? _stream;
        private HttpClient? _httpClient;

        public int StatusCode { get; set; }
        public string ErrorBodyText { get; set; } = "";

        public IReadOnlyDictionary<string, IEnumerable<string>> ResponseHeaders { get; set; } =
            new Dictionary<string, IEnumerable<string>>();

        public Stream? ResponseStream => _stream;

        /// <summary>
        /// Transfers ownership of <paramref name="stream"/>, <paramref name="response"/>, and <paramref name="httpClient"/>.
        /// </summary>
        internal OpenAiHttpSseOpenResult WithStreamResponseAndClient(Stream? stream, HttpResponseMessage? response,
            HttpClient httpClient)
        {
            _stream = stream;
            _responseDispose = response;
            _httpClient = httpClient ?? throw new System.ArgumentNullException(nameof(httpClient));
            return this;
        }

        /// <summary>
        /// Transfers ownership of <paramref name="stream"/> and <paramref name="response"/>, but does not take
        /// ownership of any <see cref="HttpClient"/> (used when the transport reuses a shared, long-lived client
        /// that must not be disposed per-request).
        /// </summary>
        internal OpenAiHttpSseOpenResult WithStreamResponse(Stream? stream, HttpResponseMessage? response)
        {
            _stream = stream;
            _responseDispose = response;
            return this;
        }

        /// <summary>
        /// Transfers ownership of <paramref name="stream"/> only, with no HttpClient/HttpResponseMessage.
        /// Used by transports that bypass <c>System.Net.Http</c> (browser <c>fetch</c> bridge in WebGL).
        /// </summary>
        public OpenAiHttpSseOpenResult WithRawStream(Stream? stream)
        {
            _stream = stream;
            return this;
        }

        public void Dispose()
        {
            try
            {
                _stream?.Dispose();
            }
            catch
            {
                /* ignore */
            }

            _stream = null;
            try
            {
                _responseDispose?.Dispose();
            }
            catch
            {
                /* ignore */
            }

            _responseDispose = null;
            try
            {
                _httpClient?.Dispose();
            }
            catch
            {
                /* ignore */
            }

            _httpClient = null;
        }
    }
}
#endif
