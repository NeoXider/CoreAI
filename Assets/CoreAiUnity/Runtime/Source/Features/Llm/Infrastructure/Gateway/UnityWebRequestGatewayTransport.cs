#if COREAI_LLM
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Networking;

namespace CoreAI.Infrastructure.Llm.Gateway
{
    /// <summary>Main-thread service HTTP adapter with bounded response storage and player-loop deadline.</summary>
    public sealed class UnityWebRequestGatewayTransport : IGatewayHttpTransport
    {
        public Task<GatewayHttpResponse> SendAsync(GatewayHttpRequest request,
            CancellationToken cancellationToken = default) =>
            UnityMainThreadLlmAsyncMarshaler.Instance.InvokeAsync(
                () => SendOnMainThreadAsync(request, cancellationToken), cancellationToken);

        private static async Task<GatewayHttpResponse> SendOnMainThreadAsync(GatewayHttpRequest request,
            CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (request.MaxResponseBytes <= 0 || request.TimeoutMilliseconds <= 0)
                throw new ArgumentOutOfRangeException(nameof(request));
            cancellationToken.ThrowIfCancellationRequested();
            using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using CancellationTokenSource deadlineStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using UnityWebRequest operation = new UnityWebRequest(request.Url, request.Method);
            BoundedDownload body = new BoundedDownload(request.MaxResponseBytes);
            operation.downloadHandler = body;
            operation.redirectLimit = 0;
            operation.timeout = Math.Max(1, (int)Math.Ceiling(request.TimeoutMilliseconds / 1000d));
            if (request.Method == "POST")
            {
                operation.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(request.JsonBody ?? ""));
                operation.SetRequestHeader("Content-Type", "application/json");
            }
            foreach (KeyValuePair<string, string> header in request.Headers)
                operation.SetRequestHeader(header.Key, header.Value);
            Task complete = UnityWebRequestOpenAiTransport.AwaitCompletionAsync(
                operation.SendWebRequest(), operation, stop.Token);
            Task deadline = UnityMainThreadLlmAsyncMarshaler.Instance.DelayAsync(
                request.TimeoutMilliseconds, deadlineStop.Token);
            Task first = await Task.WhenAny(complete, deadline);
            if (first == deadline && !complete.IsCompleted)
            {
                stop.Cancel();
                try { await complete; }
                catch (OperationCanceledException) { }
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException("Gateway service request exceeded its deadline.");
            }
            deadlineStop.Cancel();
            await complete;
            cancellationToken.ThrowIfCancellationRequested();
            if (body.Exceeded) throw new InvalidDataException("Gateway service response exceeded its byte limit.");
            Dictionary<string, IEnumerable<string>> headers =
                new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> responseHeaders = operation.GetResponseHeaders();
            if (responseHeaders != null)
            {
                foreach (KeyValuePair<string, string> header in responseHeaders)
                    headers[header.Key] = new[] { header.Value ?? "" };
            }
            return new GatewayHttpResponse
            {
                StatusCode = operation.responseCode > 0 ? (int)operation.responseCode : 0,
                BodyText = body.ReadText(), Headers = headers
            };
        }

        private sealed class BoundedDownload : DownloadHandlerScript
        {
            private readonly int _maximum;
            private readonly MemoryStream _body = new MemoryStream();
            public BoundedDownload(int maximum) : base(new byte[Math.Min(maximum, 8192)]) => _maximum = maximum;
            public bool Exceeded { get; private set; }
            protected override bool ReceiveData(byte[] data, int dataLength)
            {
                if (dataLength <= 0) return true;
                if (data == null || _body.Length + dataLength > _maximum)
                {
                    Exceeded = true;
                    return false;
                }
                _body.Write(data, 0, dataLength);
                return true;
            }
            public string ReadText() => new UTF8Encoding(false, true).GetString(_body.ToArray());
        }
    }
}
#endif
