#if !COREAI_NO_LLM
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using UnityEngine.Networking;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>Unity and WebGL HTTP adapter for the portable endpoint readiness contract.</summary>
    public sealed class UnityWebRequestOpenAiReadinessProbe : ILlmEndpointReadinessProbe
    {
        public async Task<LlmEndpointReadinessResult> ProbeAsync(
            LlmEndpointReadinessRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (!Uri.TryCreate((request.BaseUrl ?? "").TrimEnd('/'), UriKind.Absolute, out Uri baseUri) ||
                (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps) ||
                !string.IsNullOrEmpty(baseUri.UserInfo) ||
                !string.IsNullOrEmpty(baseUri.Query) ||
                !string.IsNullOrEmpty(baseUri.Fragment))
            {
                return Failed(0, "Endpoint base URL is invalid.");
            }

            if (request.Mode == LlmEndpointReadinessMode.ModelsThenCompletions)
            {
                LlmEndpointReadinessResult models = await SendAsync(
                    BuildRoute(baseUri, "models"),
                    UnityWebRequest.kHttpVerbGET,
                    true,
                    request,
                    cancellationToken);
                if (models.IsReady || !LlmEndpointReadinessPolicy.ShouldTryCompletions(models.StatusCode))
                {
                    return models;
                }
            }

            return await SendAsync(
                BuildRoute(baseUri, "chat/completions"),
                UnityWebRequest.kHttpVerbPOST,
                false,
                request,
                cancellationToken);
        }

        private static async Task<LlmEndpointReadinessResult> SendAsync(
            Uri uri,
            string method,
            bool modelsRoute,
            LlmEndpointReadinessRequest request,
            CancellationToken cancellationToken)
        {
            using UnityWebRequest webRequest = new(uri.AbsoluteUri, method);
            webRequest.redirectLimit = 0;
            webRequest.downloadHandler = new DownloadHandlerBuffer();
            webRequest.timeout = Math.Max(1, request.TimeoutSeconds);
            if (method == UnityWebRequest.kHttpVerbPOST)
            {
                webRequest.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes("{}"));
                webRequest.SetRequestHeader("Content-Type", "application/json");
            }

            if (!string.IsNullOrWhiteSpace(request.ApiKey))
            {
                webRequest.SetRequestHeader("Authorization", "Bearer " + request.ApiKey.Trim());
            }

            UnityWebRequestAsyncOperation operation = webRequest.SendWebRequest();
            while (!operation.isDone)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    webRequest.Abort();
                    cancellationToken.ThrowIfCancellationRequested();
                }

                await Task.Yield();
            }

            cancellationToken.ThrowIfCancellationRequested();
            int status = webRequest.responseCode > 0 ? (int)webRequest.responseCode : 0;
            bool ready = modelsRoute
                ? status is >= 200 and < 300
                : LlmEndpointReadinessPolicy.IsHandlerReached(status);
            if (ready)
            {
                return new LlmEndpointReadinessResult { IsReady = true, StatusCode = status };
            }

            return Failed(
                status,
                status > 0
                    ? $"Endpoint readiness probe failed ({status})."
                    : "Endpoint readiness probe failed: " + (webRequest.error ?? "network error"));
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
    }
}
#endif
