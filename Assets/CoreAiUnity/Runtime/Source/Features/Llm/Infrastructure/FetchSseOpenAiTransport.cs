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
using CoreAI.Infrastructure.Logging;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// WebGL-specific SSE transport using native fetch + ReadableStream via jslib.
    /// </summary>
    public sealed class FetchSseOpenAiTransport : IOpenAiHttpTransport, IDisposable
    {
        private static readonly ConcurrentDictionary<int, StreamState> States = new();
        private static int _nextId;

        private readonly bool _sameOriginCredentials;

        /// <summary>
        /// <paramref name="sameOriginCredentials"/>: <c>true</c> → fetch <c>credentials: same-origin</c>;
        /// <c>false</c> → <c>include</c> (cross-origin cookies).
        /// </summary>
        public FetchSseOpenAiTransport(bool sameOriginCredentials)
        {
            _sameOriginCredentials = sameOriginCredentials;
        }

        public string DebugLabel => "FetchSSE";

        public bool SupportsSseStreaming => true;

        public Task<OpenAiHttpPostResult> PostNonStreamingAsync(OpenAiHttpPostRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("Use UnityWebRequestOpenAiTransport for non-streaming in WebGL.");
        }

        public Task<OpenAiHttpSseOpenResult> OpenSseResponseStreamAsync(OpenAiHttpPostRequest request,
            CancellationToken cancellationToken = default)
        {
            int id = Interlocked.Increment(ref _nextId);
            StreamState state = new StreamState(id, request);
            States[id] = state;

            try
            {
                string headers = BuildHeaderString(request.Headers);
                string credentialsMode = _sameOriginCredentials ? "same-origin" : "include";
                IntPtr controllerPtr = CoreAi_FetchSseOpen(
                    request.Url,
                    request.JsonBody,
                    headers,
                    request.TransportTimeoutSeconds,
                    credentialsMode,
                    id,
                    Marshal.GetFunctionPointerForDelegate(_onChunkDelegate),
                    Marshal.GetFunctionPointerForDelegate(_onDoneDelegate),
                    Marshal.GetFunctionPointerForDelegate(_onErrorDelegate));

                state.ControllerPtr = controllerPtr;
                return Task.FromResult(new OpenAiHttpSseOpenResult().WithRawStream(state.Stream));
            }
            catch (Exception ex)
            {
                States.TryRemove(id, out _);
                state.Dispose();
                throw new LlmClientException($"Failed to open SSE stream: {ex.Message}", LlmErrorCode.ProviderError);
            }
        }

        public void Dispose()
        {
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void ChunkCallback(int id, IntPtr strPtr);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void DoneCallback(int id);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void ErrorCallback(int id, IntPtr errPtr);

        private static readonly ChunkCallback _onChunkDelegate = OnChunkStatic;
        private static readonly DoneCallback _onDoneDelegate = OnDoneStatic;
        private static readonly ErrorCallback _onErrorDelegate = OnErrorStatic;

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
            if (States.TryRemove(id, out StreamState state))
            {
                state.SignalDone();
            }
        }

        [MonoPInvokeCallback(typeof(ErrorCallback))]
        private static void OnErrorStatic(int id, IntPtr errPtr)
        {
            if (States.TryRemove(id, out StreamState state))
            {
                string err = Marshal.PtrToStringUTF8(errPtr) ?? "Unknown error";
                state.SignalError(new Exception(err));
            }
        }

        [DllImport("__Internal")]
        private static extern IntPtr CoreAi_FetchSseOpen(
            string url,
            string body,
            string headers,
            int timeoutSec,
            string credentialsMode,
            int callId,
            IntPtr onChunk,
            IntPtr onDone,
            IntPtr onError);

        [DllImport("__Internal")]
        private static extern void CoreAi_FetchSseAbort(IntPtr controller);

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

        private sealed class StreamState : IDisposable
        {
            private readonly ConcurrentQueue<string> _queue = new();
            private readonly AutoResetEvent _signal = new AutoResetEvent(false);
            private readonly FetchSseStream _stream;

            public int CallId { get; }

            public IntPtr ControllerPtr { get; set; }

            public StreamState(int id, OpenAiHttpPostRequest request)
            {
                CallId = id;
                _stream = new FetchSseStream(_queue, _signal, this);
            }

            public void EnqueueChunk(string chunk) => _queue.Enqueue(chunk);

            public void SignalDone()
            {
                _stream.NotifyCompleted();
                _signal.Set();
            }

            public void SignalError(Exception ex)
            {
                _stream.SetError(ex);
                _signal.Set();
            }

            public void Dispose()
            {
                _signal.Set();
                _signal.Dispose();
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

            public FetchSseStream(ConcurrentQueue<string> queue, AutoResetEvent signal, StreamState owner)
            {
                _queue = queue;
                _signal = signal;
                _owner = owner;
            }

            public void SetError(Exception ex)
            {
                _error = ex;
            }

            public void NotifyCompleted()
            {
                _isDone = true;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_error != null) throw _error;
                if (_isDone && _queue.IsEmpty) return 0;

                while (!_isDone && _queue.IsEmpty && _error == null)
                {
                    _signal.WaitOne(100);
                }

                if (_error != null) throw _error;

                if (_currentBytes == null || _currentPos >= (_currentBytes?.Length ?? 0))
                {
                    if (!_queue.TryDequeue(out string chunk))
                    {
                        if (_isDone) return 0;
                        return 0;
                    }

                    _currentBytes = Encoding.UTF8.GetBytes(chunk);
                    _currentPos = 0;
                }

                int toCopy = Math.Min(count, _currentBytes.Length - _currentPos);
                if (toCopy <= 0) return 0;

                Array.Copy(_currentBytes, _currentPos, buffer, offset, toCopy);
                _currentPos += toCopy;
                return toCopy;
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _isDone = true;
                    _signal.Set();
                    if (_owner.ControllerPtr != IntPtr.Zero)
                    {
                        CoreAi_FetchSseAbort(_owner.ControllerPtr);
                        _owner.ControllerPtr = IntPtr.Zero;
                    }

                    States.TryRemove(_owner.CallId, out _);
                }

                base.Dispose(disposing);
            }
        }
    }
}
#endif
