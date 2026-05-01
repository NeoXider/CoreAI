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

        public async Task<OpenAiHttpPostResult> PostNonStreamingAsync(OpenAiHttpPostRequest request,
            CancellationToken cancellationToken = default)
        {
            int sec = request.TransportTimeoutSeconds <= 0 ? 120 : request.TransportTimeoutSeconds;
            using HttpClient client = CreateBoundedHttpClient(sec);

            using HttpRequestMessage httpRequest = new(HttpMethod.Post, request.Url);
            httpRequest.Content = new StringContent(request.JsonBody ?? "", Encoding.UTF8, "application/json");
            ApplyHeaders(httpRequest, request.Headers, request.AcceptEventStream);

            using HttpResponseMessage response =
                await client.SendAsync(httpRequest, HttpCompletionOption.ResponseContentRead, cancellationToken);
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
            int stallBudget = request.TransportTimeoutSeconds <= 0 ? 120 : request.TransportTimeoutSeconds;
            HttpClient client = CreateStreamingHttpClient(stallBudget);
            try
            {
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
                    client.Dispose();
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
                }.WithStreamResponseAndClient(stream, response, client);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        private static void ApplyHeaders(HttpRequestMessage httpRequest,
            IReadOnlyList<KeyValuePair<string, string>> headers, bool acceptEventStream)
        {
            if (acceptEventStream)
            {
                httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            }

            if (headers == null) return;

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
            var d = new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase);
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

        private static HttpClient CreateBoundedHttpClient(int transportTimeoutSec)
        {
#if UNITY_EDITOR
            if (MeaiOpenAiChatClientEditorTestHooks.HttpClientFactory != null)
            {
                return MeaiOpenAiChatClientEditorTestHooks.HttpClientFactory();
            }
#endif
            int sec = transportTimeoutSec <= 0 ? 120 : transportTimeoutSec;
            return new HttpClient { Timeout = TimeSpan.FromSeconds(sec) };
        }

        private static HttpClient CreateStreamingHttpClient(int stallBudgetSeconds)
        {
#if UNITY_EDITOR
            if (MeaiOpenAiChatClientEditorTestHooks.HttpClientFactory != null)
            {
                return MeaiOpenAiChatClientEditorTestHooks.HttpClientFactory();
            }
#endif
            _ = stallBudgetSeconds;
            return new HttpClient { Timeout = TimeSpan.FromHours(24) };
        }
    }
}
#endif
