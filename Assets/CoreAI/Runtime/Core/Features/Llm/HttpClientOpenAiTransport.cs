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
        private static readonly Lazy<HttpClient> s_boundedClient = new(CreateSharedHttpClient);

        /// <summary>
        /// Shared <see cref="HttpClient"/> for SSE streaming requests. Streams are typically long-lived, so
        /// the client timeout is disabled and stall detection is left to the caller via cancellation.
        /// </summary>
        private static readonly Lazy<HttpClient> s_streamingClient = new(CreateSharedHttpClient);

        private static HttpClient CreateSharedHttpClient()
        {
            // HttpClientHandler is available on .NET Standard 2.0 (Unity's default Mono/IL2CPP profile).
            // Bypass any system/WinINET proxy: Mono's HttpClient uses the system proxy by default, which
            // can route even 127.0.0.1 / localhost requests through a proxy or VPN filter driver. A local
            // LLM endpoint must never go through a proxy. (SocketsHttpHandler is not exposed by Unity's
            // Mono profile, so it cannot be used here.)
            // IMPORTANT: do NOT also assign handler.Proxy = null. Mono's HttpClientHandler defers
            // property writes to an inner MonoWebRequestHandler, and set_Proxy after UseProxy=false
            // throws InvalidOperationException lazily on the FIRST REQUEST (not in the setter), which
            // would poison every request through this client.
            HttpClientHandler handler = new();
            try
            {
                handler.UseProxy = false;
            }
            catch
            {
                /* some profiles may not support the setter; fall back to default proxy behavior */
            }

            return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        }

        public async Task<OpenAiHttpPostResult> PostNonStreamingAsync(OpenAiHttpPostRequest request,
            CancellationToken cancellationToken = default)
        {
            int sec = request.TransportTimeoutSeconds <= 0 ? 120 : request.TransportTimeoutSeconds;
            HttpClient client = GetBoundedHttpClient();

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
            HttpClient client = GetStreamingHttpClient();

            using HttpRequestMessage httpRequest = new(HttpMethod.Post, request.Url);
            httpRequest.Content = new StringContent(request.JsonBody ?? "", Encoding.UTF8, "application/json");
            ApplyHeaders(httpRequest, request.Headers, request.AcceptEventStream);

            HttpResponseMessage response =
                await client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            int statusCode = (int)response.StatusCode;
            IReadOnlyDictionary<string, IEnumerable<string>> headers = CopyHeaders(response);

            if (!response.IsSuccessStatusCode)
            {
                string errBody = await response.Content.ReadAsStringAsync();
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

        private static HttpClient GetBoundedHttpClient()
        {
#if UNITY_EDITOR
            if (MeaiOpenAiChatClientEditorTestHooks.HttpClientFactory != null)
            {
                return MeaiOpenAiChatClientEditorTestHooks.HttpClientFactory();
            }
#endif
            return s_boundedClient.Value;
        }

        private static HttpClient GetStreamingHttpClient()
        {
#if UNITY_EDITOR
            if (MeaiOpenAiChatClientEditorTestHooks.HttpClientFactory != null)
            {
                return MeaiOpenAiChatClientEditorTestHooks.HttpClientFactory();
            }
#endif
            return s_streamingClient.Value;
        }
    }
}
#endif