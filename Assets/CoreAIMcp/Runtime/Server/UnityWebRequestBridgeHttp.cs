using System;
using System.Text;
using UnityEngine.Networking;

namespace CoreAI.Mcp.Server
{
    /// <summary>
    /// <see cref="IMcpBridgeHttp"/> over <c>UnityWebRequest</c>: works in the editor, desktop players and a WebGL
    /// player (where it is the browser's fetch) without a plugin. The callback runs on the game thread, from the
    /// request's completion event.
    /// </summary>
    public sealed class UnityWebRequestBridgeHttp : IMcpBridgeHttp
    {
        /// <inheritdoc />
        public void Post(string url, string json, string bearerKey, int timeoutSeconds,
            Action<McpBridgeHttpResponse> done)
        {
            UnityWebRequest request = new(url, UnityWebRequest.kHttpVerbPOST)
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json ?? "{}")),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = Math.Max(1, timeoutSeconds)
            };
            request.SetRequestHeader("Content-Type", "application/json");
            if (!string.IsNullOrEmpty(bearerKey))
            {
                request.SetRequestHeader("Authorization", "Bearer " + bearerKey);
            }

            UnityWebRequestAsyncOperation operation;
            try
            {
                operation = request.SendWebRequest();
            }
            catch (Exception ex)
            {
                request.Dispose();
                done?.Invoke(new McpBridgeHttpResponse(0, "", ex.Message));
                return;
            }

            operation.completed += _ =>
            {
                McpBridgeHttpResponse response;
                try
                {
                    int status = request.result == UnityWebRequest.Result.ConnectionError ? 0 : (int)request.responseCode;
                    response = new McpBridgeHttpResponse(status, request.downloadHandler?.text,
                        status == 0 ? request.error : null);
                }
                finally
                {
                    request.Dispose();
                }

                done?.Invoke(response);
            };
        }
    }
}
