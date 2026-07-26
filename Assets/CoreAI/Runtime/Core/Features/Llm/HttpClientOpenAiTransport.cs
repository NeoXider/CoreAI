#if !COREAI_NO_LLM
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// <see cref="HttpClient"/> transport for non-WebGL players and editor tests (with
    /// <see cref="MeaiOpenAiChatClientEditorTestHooks"/>).
    /// </summary>
    public sealed class HttpClientOpenAiTransport : IOpenAiHttpTransport
    {
        public string DebugLabel => "HttpClient";

        public bool SupportsSseStreaming => true;

        /// <summary>
        /// Shared <see cref="HttpClient"/> for non-streaming requests. A single instance is reused across
        /// requests to avoid per-request socket exhaustion (a fresh <see cref="HttpClient"/> per call leaves
        /// sockets in <c>TIME_WAIT</c> and exhausts ephemeral ports under load). Per-request timeouts are
        /// enforced via a linked <see cref="CancellationTokenSource"/> instead of mutating
        /// <see cref="HttpClient.Timeout"/> (which is not thread-safe to change after first use).
        /// </summary>
        private static readonly Lazy<HttpClient> s_boundedLoopbackClient =
            new(() => CreateSharedHttpClient(true));

        private static readonly Lazy<HttpClient> s_boundedExternalClient =
            new(() => CreateSharedHttpClient(false));

        /// <summary>
        /// Shared <see cref="HttpClient"/> for SSE streaming requests. Streams are typically long-lived, so
        /// the client timeout is disabled and stall detection is left to the caller via cancellation.
        /// </summary>
        private static readonly Lazy<HttpClient> s_streamingLoopbackClient =
            new(() => CreateSharedHttpClient(true));

        private static readonly Lazy<HttpClient> s_streamingExternalClient =
            new(() => CreateSharedHttpClient(false));

        private static HttpClient CreateSharedHttpClient(bool bypassProxy)
        {
            HttpClientHandler handler = new();
            if (bypassProxy)
            {
                try
                {
                    // WHY: local LLM sockets must not be routed through a system proxy or VPN filter.
                    handler.UseProxy = false;
                }
                catch
                {
                    // WHY: profiles without a writable UseProxy property retain their platform default.
                }
            }

            return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        }

        internal static bool ShouldBypassProxy(string url)
        {
            return Uri.TryCreate(url, UriKind.Absolute, out Uri uri) && uri.IsLoopback;
        }

        public async Task<OpenAiHttpPostResult> PostNonStreamingAsync(OpenAiHttpPostRequest request,
            CancellationToken cancellationToken = default)
        {
            int sec = request.TransportTimeoutSeconds <= 0 ? 120 : request.TransportTimeoutSeconds;
            HttpClient client = GetBoundedHttpClient(request.Url);

            using CancellationTokenSource timeoutCts = new(TimeSpan.FromSeconds(sec));
            using CancellationTokenSource linkedCts =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            using HttpRequestMessage httpRequest = new(HttpMethod.Post, request.Url);
            httpRequest.Content = new StringContent(request.JsonBody ?? "", Encoding.UTF8, "application/json");
            ApplyHeaders(httpRequest, request.Headers, request.AcceptEventStream);

            using HttpResponseMessage response =
                await client.SendAsync(httpRequest, HttpCompletionOption.ResponseContentRead, linkedCts.Token);
            string bodyText = await response.Content.ReadAsStringAsync();

            return new OpenAiHttpPostResult
            {
                StatusCode = (int)response.StatusCode,
                BodyText = bodyText ?? "",
                ResponseHeaders = CopyHeaders(response)
            };
        }

        public async Task<OpenAiHttpSseOpenResult> OpenSseResponseStreamAsync(OpenAiHttpPostRequest request,
            CancellationToken cancellationToken = default)
        {
            HttpClient client = GetStreamingHttpClient(request.Url);

            using HttpRequestMessage httpRequest = new(HttpMethod.Post, request.Url);
            httpRequest.Content = new StringContent(request.JsonBody ?? "", Encoding.UTF8, "application/json");
            ApplyHeaders(httpRequest, request.Headers, request.AcceptEventStream);

            // WHY: The streaming client's Timeout is infinite (SSE bodies are long-lived), so a backend
            // that accepts TCP but never sends response headers used to hang the turn until external
            // cancellation. Bound ONLY the time-to-headers phase with TransportTimeoutSeconds; the linked
            // CTS is disposed the moment headers arrive so it never bounds the streaming body (idle-stall
            // detection there stays with the caller). 0/unset keeps the legacy unbounded behavior.
            // TimeoutLlmClientDecorator still bounds the WHOLE request at a higher layer; this shorter
            // header bound only makes a headerless backend fail fast instead of eating the turn budget.
            HttpResponseMessage response;
            if (request.TransportTimeoutSeconds > 0)
            {
                using CancellationTokenSource headerCts =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                headerCts.CancelAfter(TimeSpan.FromSeconds(request.TransportTimeoutSeconds));
                response = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead,
                    headerCts.Token);
            }
            else
            {
                response = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
            }

            int statusCode = (int)response.StatusCode;
            IReadOnlyDictionary<string, IEnumerable<string>> headers = CopyHeaders(response);

            if (!response.IsSuccessStatusCode)
            {
                string errBody = await ReadBodyTextAsync(response.Content, cancellationToken);
                response.Dispose();
                return new OpenAiHttpSseOpenResult
                {
                    StatusCode = statusCode,
                    ErrorBodyText = errBody ?? "",
                    ResponseHeaders = headers
                };
            }

            Stream stream = await response.Content.ReadAsStreamAsync();
            return new OpenAiHttpSseOpenResult
            {
                StatusCode = statusCode,
                ErrorBodyText = "",
                ResponseHeaders = headers
            }.WithStreamResponse(stream, response);
        }

        /// <summary>
        /// Reads a response body as UTF-8 text with cancellation support. This profile has no
        /// <c>ReadAsStringAsync(CancellationToken)</c> overload, and after
        /// <see cref="HttpCompletionOption.ResponseHeadersRead"/> the body still comes from the
        /// network, so an uncancellable read could hang on a stalled backend.
        /// </summary>
        private static async Task<string> ReadBodyTextAsync(HttpContent content,
            CancellationToken cancellationToken)
        {
            using Stream stream = await content.ReadAsStreamAsync();
            using MemoryStream buffer = new();
            await stream.CopyToAsync(buffer, 81920, cancellationToken);
            return Encoding.UTF8.GetString(buffer.ToArray());
        }

        private static void ApplyHeaders(HttpRequestMessage httpRequest,
            IReadOnlyList<KeyValuePair<string, string>> headers, bool acceptEventStream)
        {
            if (acceptEventStream)
            {
                httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            }

            if (headers == null)
            {
                return;
            }

            foreach (KeyValuePair<string, string> kv in headers)
            {
                if (string.Equals(kv.Key, "Accept", StringComparison.OrdinalIgnoreCase) && acceptEventStream)
                {
                    continue;
                }

                httpRequest.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            }
        }

        private static Dictionary<string, IEnumerable<string>> CopyHeaders(HttpResponseMessage response)
        {
            Dictionary<string, IEnumerable<string>> d = new(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, IEnumerable<string>> h in response.Headers)
            {
                d[h.Key] = h.Value.ToList();
            }

            if (response.Content?.Headers != null)
            {
                foreach (KeyValuePair<string, IEnumerable<string>> h in response.Content.Headers)
                {
                    d[h.Key] = h.Value.ToList();
                }
            }

            return d;
        }

        private static HttpClient GetBoundedHttpClient(string url)
        {
#if UNITY_EDITOR
            if (MeaiOpenAiChatClientEditorTestHooks.HttpClientFactory != null)
            {
                return MeaiOpenAiChatClientEditorTestHooks.HttpClientFactory();
            }
#endif
            return ShouldBypassProxy(url) ? s_boundedLoopbackClient.Value : s_boundedExternalClient.Value;
        }

        private static HttpClient GetStreamingHttpClient(string url)
        {
#if UNITY_EDITOR
            if (MeaiOpenAiChatClientEditorTestHooks.HttpClientFactory != null)
            {
                return MeaiOpenAiChatClientEditorTestHooks.HttpClientFactory();
            }
#endif
            return ShouldBypassProxy(url) ? s_streamingLoopbackClient.Value : s_streamingExternalClient.Value;
        }
    }
}
#endif
