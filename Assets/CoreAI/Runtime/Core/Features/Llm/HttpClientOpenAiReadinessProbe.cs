#if COREAI_LLM
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>Portable <see cref="HttpClient"/> readiness adapter for ordinary .NET hosts.</summary>
    public sealed class HttpClientOpenAiReadinessProbe : ILlmEndpointReadinessProbe
    {
        private static readonly Lazy<HttpClient> s_loopbackClient =
            new(() => CreateClient(true));

        private static readonly Lazy<HttpClient> s_externalClient =
            new(() => CreateClient(false));

        private readonly HttpClient _client;

        public HttpClientOpenAiReadinessProbe()
        {
        }

        internal HttpClientOpenAiReadinessProbe(HttpClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
        }

        public async Task<LlmEndpointReadinessResult> ProbeAsync(
            LlmEndpointReadinessRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (!TryParseHttpBaseUri(request.BaseUrl, out Uri baseUri))
            {
                return Failed(0, "Endpoint base URL is invalid.");
            }

            int timeoutSeconds = Math.Max(1, request.TimeoutSeconds);
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(timeoutSeconds));
            using CancellationTokenSource linked =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

            try
            {
                if (request.Mode == LlmEndpointReadinessMode.ModelsThenCompletions)
                {
                    LlmEndpointReadinessResult models = await SendAsync(
                        BuildRoute(baseUri, "models"),
                        HttpMethod.Get,
                        request.ApiKey,
                        true,
                        linked.Token).ConfigureAwait(false);
                    if (models.IsReady || !LlmEndpointReadinessPolicy.ShouldTryCompletions(models.StatusCode))
                    {
                        return models;
                    }
                }

                return await SendAsync(
                    BuildRoute(baseUri, "chat/completions"),
                    HttpMethod.Post,
                    request.ApiKey,
                    false,
                    linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Failed(0, $"Endpoint readiness timed out after {timeoutSeconds}s.");
            }
            catch (HttpRequestException ex)
            {
                return Failed(0, "Endpoint readiness transport failed: " + ex.GetType().Name + ".");
            }
        }

        private static bool TryParseHttpBaseUri(string value, out Uri uri)
        {
            bool valid = Uri.TryCreate((value ?? "").TrimEnd('/'), UriKind.Absolute, out uri) &&
                         (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
                         string.IsNullOrEmpty(uri.UserInfo) &&
                         string.IsNullOrEmpty(uri.Query) &&
                         string.IsNullOrEmpty(uri.Fragment);
            return valid;
        }

        private static Uri BuildRoute(Uri baseUri, string route)
        {
            UriBuilder builder = new(baseUri)
            {
                Path = baseUri.AbsolutePath.TrimEnd('/') + "/" + route,
                Query = "",
                Fragment = ""
            };
            return builder.Uri;
        }

        private async Task<LlmEndpointReadinessResult> SendAsync(
            Uri uri,
            HttpMethod method,
            string apiKey,
            bool modelsRoute,
            CancellationToken cancellationToken)
        {
            using HttpRequestMessage message = new(method, uri);
            if (method == HttpMethod.Post)
            {
                message.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            }

            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
            }

            HttpClient client = _client ?? GetSharedClient(uri);
            using HttpResponseMessage response = await client.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            int status = (int)response.StatusCode;
            bool ready = modelsRoute
                ? status is >= 200 and < 300
                : LlmEndpointReadinessPolicy.IsHandlerReached(status);
            return ready
                ? new LlmEndpointReadinessResult { IsReady = true, StatusCode = status }
                : Failed(status, $"Endpoint readiness probe failed ({status}).");
        }

        private static LlmEndpointReadinessResult Failed(int statusCode, string error)
        {
            return new LlmEndpointReadinessResult
            {
                IsReady = false,
                StatusCode = statusCode,
                Error = error ?? ""
            };
        }

        private static HttpClient GetSharedClient(Uri uri)
        {
            return uri.IsLoopback ? s_loopbackClient.Value : s_externalClient.Value;
        }

        private static HttpClient CreateClient(bool bypassProxy)
        {
            HttpClientHandler handler = new();
            handler.AllowAutoRedirect = false;
            if (bypassProxy)
            {
                try
                {
                    handler.UseProxy = false;
                }
                catch
                {
                    // WHY: runtimes without a writable proxy setting retain their platform default.
                }
            }

            return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        }
    }
}
#endif
