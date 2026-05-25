#if UNITY_WEBGL && !UNITY_EDITOR
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AOT;
using CoreAI.Ai;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// WebGL-specific SSE transport using native browser <c>fetch</c> + <c>ReadableStream</c> via jslib.
    /// <para>
    /// Awaits the fetch response headers (status + headers) before returning, so
    /// <see cref="MeaiOpenAiChatClient"/> sees the real HTTP status and can build a proper
    /// <see cref="LlmClientException"/> on non-2xx (including <c>HTTP 0</c> for CORS/network failure).
    /// </para>
    /// </summary>
    public sealed class FetchSseOpenAiTransport : IOpenAiHttpTransport, IDisposable
    {
        private static readonly ConcurrentDictionary<int, StreamState> States = new();
        private static int _nextId;

        private readonly bool _sameOriginCredentials;

        public FetchSseOpenAiTransport(bool sameOriginCredentials)
        {
            _sameOriginCredentials = sameOriginCredentials;
        }

        public string DebugLabel => "FetchSSE";

        public bool SupportsSseStreaming => true;

        public Task<OpenAiHttpPostResult> PostNonStreamingAsync(OpenAiHttpPostRequest request, CancellationToken cancellationToken
 = default)
        {
            throw new NotSupportedException("Use UnityWebRequestOpenAiTransport for non-streaming in WebGL.");
        }

        public async Task<OpenAiHttpSseOpenResult> OpenSseResponseStreamAsync(OpenAiHttpPostRequest request,
            CancellationToken cancellationToken = default)
        {
            int id = Interlocked.Increment(ref _nextId);
            StreamState state = new StreamState(id);
            States[id] = state;

            using CancellationTokenRegistration ctReg = cancellationToken.Register(() =>
            {
                CoreAi_FetchSseAbort(id);
                state.SignalCancelled(cancellationToken);
            });

            string headers = BuildHeaderString(request.Headers);
            string credentialsMode = _sameOriginCredentials ? "same-origin" : "omit";

            CoreAi_FetchSseOpen(
                request.Url,
                request.JsonBody,
                headers,
                request.TransportTimeoutSeconds,
                credentialsMode,
                id,
                Marshal.GetFunctionPointerForDelegate(_onOpenDelegate),
                Marshal.GetFunctionPointerForDelegate(_onChunkDelegate),
                Marshal.GetFunctionPointerForDelegate(_onDoneDelegate),
                Marshal.GetFunctionPointerForDelegate(_onErrorDelegate));

            // Wait for fetch headers (status + Content-Type) before returning. Do NOT add
            // continuation through the (non-existent) browser ThreadPool and hang the
            // open path forever. The JS bridge already defers the first ReadableStream
            // read via setTimeout(0), so MeaiOpenAiChatClient gets time to attach its
            // line reader before the first onChunk lands.
            // to be captured so the continuation reliably runs on the browser main thread.
            // ConfigureAwait(false) routes the continuation to ThreadPool, which doesn't
            // exist in WebGL, and the await never resumes.
            StreamState.OpenInfo openInfo = await state.WaitForOpenAsync();

            OpenAiHttpSseOpenResult result = new OpenAiHttpSseOpenResult
            {
                StatusCode = openInfo.Status,
                ErrorBodyText = openInfo.ErrorBody ?? "",
                ResponseHeaders = openInfo.Headers ?? new Dictionary<string, IEnumerable<string>>()
            };

            if (openInfo.Status >= 200 && openInfo.Status < 300)
            {
                result.WithRawStream(state.Stream);
            }
            else
            {
                States.TryRemove(id, out _);
                state.Dispose();
            }

            return result;
        }

        public void Dispose()
        {
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void OpenCallback(int id, int status, IntPtr errBodyPtr, IntPtr headersPtr);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void ChunkCallback(int id, IntPtr strPtr);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void DoneCallback(int id);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void ErrorCallback(int id, IntPtr errPtr);

        private static readonly OpenCallback _onOpenDelegate = OnOpenStatic;
        private static readonly ChunkCallback _onChunkDelegate = OnChunkStatic;
        private static readonly DoneCallback _onDoneDelegate = OnDoneStatic;
        private static readonly ErrorCallback _onErrorDelegate = OnErrorStatic;

        [MonoPInvokeCallback(typeof(OpenCallback))]
        private static void OnOpenStatic(int id, int status, IntPtr errBodyPtr, IntPtr headersPtr)
        {
            if (!States.TryGetValue(id, out StreamState state)) return;
            string errBody = Marshal.PtrToStringUTF8(errBodyPtr) ?? "";
            string headersFlat = Marshal.PtrToStringUTF8(headersPtr) ?? "";
            state.SignalOpen(status, errBody, headersFlat);
        }

        [MonoPInvokeCallback(typeof(ChunkCallback))]
        private static void OnChunkStatic(int id, IntPtr strPtr)
        {
            if (!States.TryGetValue(id, out StreamState state)) return;
            string data = Marshal.PtrToStringUTF8(strPtr) ?? "";
            state.EnqueueChunk(data);
        }

        [MonoPInvokeCallback(typeof(DoneCallback))]
        private static void OnDoneStatic(int id)
        {
            if (States.TryGetValue(id, out StreamState state))
            {
                state.SignalDone();
            }
        }

        [MonoPInvokeCallback(typeof(ErrorCallback))]
        private static void OnErrorStatic(int id, IntPtr errPtr)
        {
            if (States.TryGetValue(id, out StreamState state))
            {
                string err = Marshal.PtrToStringUTF8(errPtr) ?? "Unknown error";
                state.SignalError(IsCancelledMessage(err)
                    ? new OperationCanceledException()
                    : new Exception(err));
            }
        }

        private static bool IsCancelledMessage(string message)
        {
            return string.Equals(message, "cancelled", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(message, "canceled", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(message, "Cancelled", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(message, "Canceled", StringComparison.OrdinalIgnoreCase);
        }

        [DllImport("__Internal")]
        private static extern void CoreAi_FetchSseOpen(
            string url,
            string body,
            string headers,
            int timeoutSec,
            string credentialsMode,
            int callId,
            IntPtr onOpen,
            IntPtr onChunk,
            IntPtr onDone,
            IntPtr onError);

        [DllImport("__Internal")]
        private static extern void CoreAi_FetchSseAbort(int callId);

        private static string BuildHeaderString(IReadOnlyList<KeyValuePair<string, string>> headers)
        {
            if (headers == null || headers.Count == 0) return "";
            var sb = new StringBuilder();
            foreach (KeyValuePair<string, string> h in headers)
            {
                sb.Append(h.Key).Append(':').Append(h.Value).Append('\n');
            }

            return sb.ToString().TrimEnd('\n');
        }

        private static IReadOnlyDictionary<string, IEnumerable<string>> ParseFlatHeaders(string flat)
        {
            Dictionary<string, IEnumerable<string>> map = new(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(flat)) return map;
            foreach (string line in flat.Split('\n'))
            {
                int idx = line.IndexOf(':');
                if (idx <= 0) continue;
                string name = line.Substring(0, idx).Trim();
                string value = line.Substring(idx + 1).Trim();
                if (string.IsNullOrEmpty(name)) continue;
                if (map.TryGetValue(name, out IEnumerable<string> existing))
                {
                    List<string> list = existing as List<string> ?? new List<string>(existing);
                    list.Add(value);
                    map[name] = list;
                }
                else
                {
                    map[name] = new List<string> { value };
                }
            }

            return map;
        }

        private sealed class StreamState : IDisposable
        {
            internal readonly struct OpenInfo
            {
                public int Status { get; }
                public string ErrorBody { get; }
                public IReadOnlyDictionary<string, IEnumerable<string>> Headers { get; }

                public OpenInfo(int status, string errorBody, IReadOnlyDictionary<string, IEnumerable<string>> headers)
                {
                    Status = status;
                    ErrorBody = errorBody;
                    Headers = headers;
                }
            }

            // Synchronous continuations: WebGL is single-threaded, so we want the awaiting C# code to resume
            // continuation to a thread pool that never runs in browser builds, hanging the await forever.
            private readonly TaskCompletionSource<OpenInfo> _openTcs = new();
            private readonly ConcurrentQueue<string> _queue = new();
            private readonly AutoResetEvent _signal = new AutoResetEvent(false);
            private readonly FetchSseStream _stream;
            private bool _cancelled;

            public int CallId { get; }

            public StreamState(int id)
            {
                CallId = id;
                _stream = new FetchSseStream(_queue, _signal, this);
            }

            public Task<OpenInfo> WaitForOpenAsync() => _openTcs.Task;

            public void SignalOpen(int status, string errorBody, string headersFlat)
            {
                IReadOnlyDictionary<string, IEnumerable<string>> hdrs = ParseFlatHeaders(headersFlat);
                _openTcs.TrySetResult(new OpenInfo(status, errorBody ?? "", hdrs));
            }

            public void EnqueueChunk(string chunk)
            {
                _queue.Enqueue(chunk);
                _signal.Set();
                _stream.PumpPendingRead();
            }

            public void SignalDone()
            {
                if (_cancelled) return;
                _stream.NotifyCompleted();
                _signal.Set();
                _stream.PumpPendingRead();
                // Defensive: if SignalDone fires before SignalOpen (shouldn't happen, but
                // caller surfaces a transport error instead of awaiting forever.
                _openTcs.TrySetResult(new OpenInfo(0, "fetch completed without headers",
                    new Dictionary<string, IEnumerable<string>>()));
            }

            public void SignalError(Exception ex)
            {
                if (_cancelled && ex is not OperationCanceledException)
                {
                    return;
                }

                if (ex is OperationCanceledException)
                {
                    _cancelled = true;
                }

                _stream.SetError(ex);
                _signal.Set();
                _stream.PumpPendingRead();
                if (ex is OperationCanceledException)
                {
                    _openTcs.TrySetCanceled();
                }
                else
                {
                    _openTcs.TrySetResult(new OpenInfo(0, ex?.Message ?? "fetch error",
                        new Dictionary<string, IEnumerable<string>>()));
                }
            }

            public void SignalCancelled(CancellationToken cancellationToken)
            {
                _cancelled = true;
                var ex = new OperationCanceledException(cancellationToken);
                _stream.SetError(ex);
                _signal.Set();
                _stream.PumpPendingRead();
                _openTcs.TrySetCanceled(cancellationToken);
            }

            public void Dispose()
            {
                try { _signal.Set(); } catch { }
                try { _signal.Dispose(); } catch { }
            }

            public FetchSseStream Stream => _stream;
        }

        private sealed class FetchSseStream : Stream
        {
            private readonly ConcurrentQueue<string> _queue;
            private readonly AutoResetEvent _signal;
            private readonly StreamState _owner;
            private bool _isDone;
            private Exception _error;
            private byte[] _currentBytes;
            private int _currentPos;

            // WebGL is single-threaded: a synchronous Stream.Read that blocks on _signal.WaitOne would
            // freeze the JS event loop, preventing fetch chunks from ever being delivered. Instead, we
            // expose true async via ReadAsync that returns a TaskCompletionSource<int> when no data is
            // ready; EnqueueChunk / SignalDone / SignalError fulfil it from the JS callback.
            private TaskCompletionSource<int> _pendingTcs;
            private byte[] _pendingBuffer;
            private int _pendingOffset;
            private int _pendingCount;

            public FetchSseStream(ConcurrentQueue<string> queue, AutoResetEvent signal, StreamState owner)
            {
                _queue = queue;
                _signal = signal;
                _owner = owner;
            }

            public void SetError(Exception ex) { _error = ex; }
            public void NotifyCompleted() { _isDone = true; }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            /// <summary>Reads buffered SSE bytes into the caller-provided destination buffer.</summary>
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_error != null) throw _error;
                int n = TryReadNonBlocking(buffer, offset, count);
                if (n > 0) return n;
                if (_isDone) return 0;
                // No data and not done: a *synchronous* caller would deadlock the WebGL event loop
                // if we blocked. Return 0; well-behaved consumers (StreamReader) use ReadAsync.
                return 0;
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                if (_error != null) return Task.FromException<int>(_error);
                if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<int>(cancellationToken);

                int n = TryReadNonBlocking(buffer, offset, count);
                if (n > 0) return Task.FromResult(n);
                if (_isDone) return Task.FromResult(0);

                // Park the read; PumpPendingRead fulfils when the next chunk arrives or the stream finishes.
                TaskCompletionSource<int> tcs = new TaskCompletionSource<int>();
                _pendingTcs = tcs;
                _pendingBuffer = buffer;
                _pendingOffset = offset;
                _pendingCount = count;
                if (cancellationToken.CanBeCanceled)
                {
                    cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
                }

                // Chunk/done may have fired before we parked; drain queue / EOF without waiting for another JS callback.
                PumpPendingRead();

                return tcs.Task;
            }

            internal void PumpPendingRead()
            {
                TaskCompletionSource<int> tcs = _pendingTcs;
                if (tcs == null) return;

                if (_error != null)
                {
                    _pendingTcs = null;
                    tcs.TrySetException(_error);
                    return;
                }

                int n = TryReadNonBlocking(_pendingBuffer, _pendingOffset, _pendingCount);
                if (n > 0)
                {
                    _pendingTcs = null;
                    _pendingBuffer = null;
                    tcs.TrySetResult(n);
                    return;
                }

                if (_isDone)
                {
                    _pendingTcs = null;
                    _pendingBuffer = null;
                    tcs.TrySetResult(0);
                }
            }

            private int TryReadNonBlocking(byte[] buffer, int offset, int count)
            {
                if (_currentBytes != null && _currentPos < _currentBytes.Length)
                {
                    int toCopy = Math.Min(count, _currentBytes.Length - _currentPos);
                    Array.Copy(_currentBytes, _currentPos, buffer, offset, toCopy);
                    _currentPos += toCopy;
                    return toCopy;
                }

                if (_queue.TryDequeue(out string chunk))
                {
                    // through unchanged so the OpenAI SSE parser owns framing, [DONE],
                    // tool_calls, role, finish_reason, etc.
                    if (string.IsNullOrEmpty(chunk)) return 0;
                    _currentBytes = Encoding.UTF8.GetBytes(chunk);
                    _currentPos = 0;
                    int toCopy = Math.Min(count, _currentBytes.Length - _currentPos);
                    Array.Copy(_currentBytes, _currentPos, buffer, offset, toCopy);
                    _currentPos += toCopy;
                    return toCopy;
                }

                return 0;
            }

            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _isDone = true;
                    try { _signal.Set(); } catch { }
                    CoreAi_FetchSseAbort(_owner.CallId);
                    States.TryRemove(_owner.CallId, out _);
                }

                base.Dispose(disposing);
            }
        }
    }
}
#endif
