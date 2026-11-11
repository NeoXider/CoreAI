using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace CoreAI.Decisions
{
    /// <summary>
    /// <see cref="IDecisionHttpGet"/> over <see cref="UnityWebRequest"/>, the GET the WebGL player can make. Awaited
    /// through the operation's completion callback, never by polling or a blocking wait; call it from the main
    /// thread like every <see cref="UnityWebRequest"/>.
    /// </summary>
    public sealed class UnityWebRequestDecisionHttpGet : IDecisionHttpGet
    {
        /// <inheritdoc />
        public async Task<DecisionHttpResponse> GetAsync(string url,
            IReadOnlyList<KeyValuePair<string, string>> headers, int timeoutSeconds, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using UnityWebRequest request = UnityWebRequest.Get(url);
            request.timeout = Math.Max(0, timeoutSeconds);
            foreach (KeyValuePair<string, string> header in headers ?? Array.Empty<KeyValuePair<string, string>>())
            {
                if (!string.IsNullOrEmpty(header.Key))
                {
                    request.SetRequestHeader(header.Key, header.Value ?? "");
                }
            }

            UnityWebRequestAsyncOperation operation = request.SendWebRequest();
            TaskCompletionSource<bool> completion = new();
            Action<AsyncOperation> onCompleted = _ => completion.TrySetResult(true);
            operation.completed += onCompleted;
            try
            {
                if (operation.isDone)
                {
                    completion.TrySetResult(true);
                }

                using (cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken)))
                {
                    await completion.Task;
                }
            }
            catch (OperationCanceledException)
            {
                request.Abort();
                throw;
            }
            finally
            {
                operation.completed -= onCompleted;
            }

            long status = request.responseCode;
            return new DecisionHttpResponse
            {
                StatusCode = status > 0 && status <= int.MaxValue ? (int)status : 0,
                BodyText = request.downloadHandler?.text ?? ""
            };
        }
    }
}
