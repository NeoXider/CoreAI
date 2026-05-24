#if !COREAI_NO_LLM
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Networking;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// WebGL-safe OpenAI HTTP chat transport (<see cref="UnityWebRequest"/>).
    /// Does not support SSE streaming; <see cref="MeaiOpenAiChatClient"/> falls back to non-stream JSON + simulated updates.
    /// </summary>
    public sealed class UnityWebRequestOpenAiTransport : IOpenAiHttpTransport
    {
        public string DebugLabel => "UnityWebRequest";

        public bool SupportsSseStreaming => false;

        public async Task<OpenAiHttpPostResult> PostNonStreamingAsync(OpenAiHttpPostRequest request,
            CancellationToken cancellationToken = default)
        {
            byte[] raw = Encoding.UTF8.GetBytes(request.JsonBody ?? "");
            using UnityWebRequest uwr = new(request.Url, UnityWebRequest.kHttpVerbPOST);
            uwr.uploadHandler = new UploadHandlerRaw(raw);
            uwr.downloadHandler = new DownloadHandlerBuffer();
            uwr.SetRequestHeader("Content-Type", "application/json");
            ApplyHeaders(uwr, request);

            int t = request.TransportTimeoutSeconds <= 0 ? 120 : request.TransportTimeoutSeconds;
            uwr.timeout = t;

            UnityWebRequestAsyncOperation op = uwr.SendWebRequest();
            while (!op.isDone)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
            }

            cancellationToken.ThrowIfCancellationRequested();

            long codeLong = uwr.responseCode;
            int code = codeLong > 0 ? (int)codeLong : 0;
            string body = uwr.downloadHandler?.text ?? "";

            return new OpenAiHttpPostResult
            {
                StatusCode = code > 0 ? code : MapFailureToStatus(uwr),
                BodyText = body,
                ResponseHeaders = new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
            };
        }

        public Task<OpenAiHttpSseOpenResult> OpenSseResponseStreamAsync(OpenAiHttpPostRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException(
                $"{nameof(UnityWebRequestOpenAiTransport)} does not support SSE; use non-stream completion on WebGL.");
        }

        private static void ApplyHeaders(UnityWebRequest uwr, OpenAiHttpPostRequest request)
        {
            if (request.AcceptEventStream)
            {
                uwr.SetRequestHeader("Accept", "text/event-stream");
            }

            if (request.Headers == null)
            {
                return;
            }

            foreach (KeyValuePair<string, string> kv in request.Headers)
            {
                if (string.IsNullOrEmpty(kv.Key))
                {
                    continue;
                }

                uwr.SetRequestHeader(kv.Key, kv.Value ?? "");
            }
        }

        private static int MapFailureToStatus(UnityWebRequest uwr)
        {
            if (uwr.result == UnityWebRequest.Result.Success)
            {
                return 200;
            }

            return 0;
        }
    }
}
#endif