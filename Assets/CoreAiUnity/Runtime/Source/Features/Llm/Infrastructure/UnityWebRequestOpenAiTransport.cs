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
            UnityWebRequest uwr = new(request.Url, UnityWebRequest.kHttpVerbPOST);
            try
            {
                uwr.uploadHandler = new UploadHandlerRaw(raw);
                uwr.downloadHandler = new DownloadHandlerBuffer();
                uwr.SetRequestHeader("Content-Type", "application/json");
                ApplyHeaders(uwr, request);

                int t = request.TransportTimeoutSeconds <= 0 ? 120 : request.TransportTimeoutSeconds;
                uwr.timeout = t;

                UnityWebRequestAsyncOperation op = uwr.SendWebRequest();
                await AwaitCompletionAsync(op, uwr, cancellationToken);

                if (cancellationToken.IsCancellationRequested)
                {
                    AbortQuietly(uwr);
                    cancellationToken.ThrowIfCancellationRequested();
                }

                long codeLong = uwr.responseCode;
                int code = codeLong > 0 ? (int)codeLong : 0;
                string body = uwr.downloadHandler?.text ?? "";

                return new OpenAiHttpPostResult
                {
                    StatusCode = code > 0 ? code : MapFailureToStatus(uwr),
                    BodyText = body,
                    ResponseHeaders = ReadResponseHeaders(uwr)
                };
            }
            finally
            {
                uwr.Dispose();
            }
        }

        /// <summary>
        /// Awaits the request through <see cref="UnityWebRequestAsyncOperation.completed"/> instead of
        /// polling, aborting the in-flight request when <paramref name="cancellationToken"/> fires.
        /// </summary>
        private static async Task AwaitCompletionAsync(
            UnityWebRequestAsyncOperation op,
            UnityWebRequest uwr,
            CancellationToken cancellationToken)
        {
            TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            op.completed += _ => completion.TrySetResult(true);
            if (op.isDone)
            {
                completion.TrySetResult(true);
            }

            // WHY: aborting before Dispose tears down the native socket/handles instead of leaking them
            // until GC finalization.
            using (cancellationToken.Register(() =>
                   {
                       AbortQuietly(uwr);
                       completion.TrySetCanceled(cancellationToken);
                   }))
            {
                await completion.Task;
            }
        }

        /// <summary>
        /// Copies the response headers off <paramref name="uwr"/>. Without them a <c>429</c> carries no
        /// <c>Retry-After</c> and every backoff decorator falls back to a blind delay.
        /// </summary>
        private static Dictionary<string, IEnumerable<string>> ReadResponseHeaders(UnityWebRequest uwr)
        {
            Dictionary<string, IEnumerable<string>> headers =
                new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> raw = uwr.GetResponseHeaders();
            if (raw == null)
            {
                return headers;
            }

            foreach (KeyValuePair<string, string> kv in raw)
            {
                if (!string.IsNullOrEmpty(kv.Key))
                {
                    headers[kv.Key] = new[] { kv.Value ?? "" };
                }
            }

            return headers;
        }

        private static void AbortQuietly(UnityWebRequest uwr)
        {
            if (uwr == null)
            {
                return;
            }

            try
            {
                uwr.Abort();
            }
            catch
            {
                // Abort() can throw if the request already completed/disposed; the subsequent
                // Dispose() in finally still runs, so swallow and proceed.
            }
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
