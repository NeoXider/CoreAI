#if COREAI_LLM
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using UnityEngine.Networking;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// <see cref="ILlmConnectionHttp"/> over <see cref="UnityWebRequest"/>, so the connection check and the model
    /// list work in a WebGL player (which has no sockets) as in the Editor and on desktop.
    /// </summary>
    /// <remarks>
    /// WHY no redirects: a key must never follow a redirect to another host (the readiness probe's rule).
    /// </remarks>
    public sealed class UnityWebRequestLlmConnectionHttp : ILlmConnectionHttp
    {
        /// <inheritdoc />
        public async Task<LlmConnectionHttpResponse> SendAsync(LlmConnectionHttpRequest request,
            CancellationToken cancellationToken)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            cancellationToken.ThrowIfCancellationRequested();
            bool post = string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase);
            using UnityWebRequest web = new(request.Url,
                post ? UnityWebRequest.kHttpVerbPOST : UnityWebRequest.kHttpVerbGET);
            web.redirectLimit = 0;
            web.downloadHandler = new DownloadHandlerBuffer();
            web.timeout = Math.Max(1, request.TimeoutSeconds);
            if (post)
            {
                web.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(request.JsonBody ?? "{}"));
                web.SetRequestHeader("Content-Type", "application/json");
            }

            if (!string.IsNullOrWhiteSpace(request.ApiKey))
            {
                web.SetRequestHeader("Authorization", "Bearer " + request.ApiKey.Trim());
            }

            UnityWebRequestAsyncOperation operation = web.SendWebRequest();
            await UnityWebRequestOpenAiTransport.AwaitCompletionAsync(operation, web, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            int status = web.responseCode > 0 ? (int)web.responseCode : 0;
            string error = web.error ?? "";
            string lower = error.ToLowerInvariant();
            return new LlmConnectionHttpResponse
            {
                StatusCode = status,
                Body = web.downloadHandler?.text ?? "",
                TransportError = status == 0 ? error : "",
                TimedOut = status == 0 && (lower.Contains("timeout") || lower.Contains("timed out"))
            };
        }
    }
}
#endif
