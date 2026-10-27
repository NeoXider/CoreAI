#if COREAI_LLM && (!UNITY_WEBGL || UNITY_EDITOR)
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CoreAI.Infrastructure.Llm.Gateway
{
    /// <summary>Reusable HttpClient service transport with bounded reads and per-call cancellation.</summary>
    public sealed class HttpClientGatewayTransport : IGatewayHttpTransport, IDisposable
    {
        private readonly HttpClient _client;
        private readonly bool _ownsClient;
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private int _disposed;

#if !UNITY_WEBGL || UNITY_EDITOR
        public HttpClientGatewayTransport() : this(new HttpClient(
            new HttpClientHandler { AllowAutoRedirect = false })
            { Timeout = Timeout.InfiniteTimeSpan }, true) { }
#endif

        /// <summary>Injected clients must disable automatic redirects; ownership is explicit.</summary>
        public HttpClientGatewayTransport(HttpClient client, bool ownsClient = false)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _ownsClient = ownsClient;
        }

        public async Task<GatewayHttpResponse> SendAsync(GatewayHttpRequest request,
            CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(GetType().Name);
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (request.TimeoutMilliseconds <= 0 || request.MaxResponseBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(request));
            cancellationToken.ThrowIfCancellationRequested();
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _lifetime.Token);
            linked.CancelAfter(request.TimeoutMilliseconds);
            using HttpRequestMessage message = new HttpRequestMessage(new HttpMethod(request.Method), request.Url);
            if (request.Method == "POST")
                message.Content = new StringContent(request.JsonBody ?? "", Encoding.UTF8, "application/json");
            foreach (KeyValuePair<string, string> header in request.Headers)
                message.Headers.TryAddWithoutValidation(header.Key, header.Value);
            using HttpResponseMessage response = await _client.SendAsync(
                message, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            if (response.RequestMessage?.RequestUri != null &&
                !Uri.Equals(response.RequestMessage.RequestUri, new Uri(request.Url)))
                throw new InvalidOperationException("Gateway service redirects are not supported.");
            if (response.Content?.Headers.ContentLength > request.MaxResponseBytes)
                throw new InvalidDataException("Gateway service response exceeded its byte limit.");
            using Stream body = response.Content == null ? Stream.Null : await response.Content.ReadAsStreamAsync();
            using MemoryStream buffer = new MemoryStream();
            byte[] bytes = new byte[Math.Min(request.MaxResponseBytes, 8192)];
            while (true)
            {
                int read = await body.ReadAsync(bytes, 0, bytes.Length, linked.Token);
                if (read == 0) break;
                if (buffer.Length + read > request.MaxResponseBytes)
                    throw new InvalidDataException("Gateway service response exceeded its byte limit.");
                buffer.Write(bytes, 0, read);
            }
            Dictionary<string, IEnumerable<string>> headers = new Dictionary<string, IEnumerable<string>>(
                StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, IEnumerable<string>> header in response.Headers)
                headers[header.Key] = new List<string>(header.Value);
            return new GatewayHttpResponse
            {
                StatusCode = (int)response.StatusCode,
                BodyText = new UTF8Encoding(false, true).GetString(buffer.ToArray()),
                Headers = headers
            };
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { _lifetime.Cancel(); }
            finally
            {
                try { if (_ownsClient) _client.Dispose(); }
                finally { _lifetime.Dispose(); }
            }
        }
    }
}
#endif
