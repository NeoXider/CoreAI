#if UNITY_WEBGL && !UNITY_EDITOR && COREAI_LLM
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
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
        private readonly int _maxBufferedBytes;
        private readonly int _maxBufferedChunks;

        /// <summary>Creates a fetch pipe with a 1 MiB and 1024-fragment backlog limit.</summary>
        public FetchSseOpenAiTransport(bool sameOriginCredentials)
            : this(sameOriginCredentials, FetchSseBacklog.DefaultMaxBufferedBytes, FetchSseBacklog.DefaultMaxBufferedChunks)
        {
        }

        /// <summary>Creates a fetch pipe with positive per-request byte and fragment backlog limits.</summary>
        public FetchSseOpenAiTransport(bool sameOriginCredentials, int maxBufferedBytes, int maxBufferedChunks)
        {
            if (maxBufferedBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBufferedBytes));
            if (maxBufferedChunks <= 0) throw new ArgumentOutOfRangeException(nameof(maxBufferedChunks));
            _sameOriginCredentials = sameOriginCredentials;
            _maxBufferedBytes = maxBufferedBytes;
            _maxBufferedChunks = maxBufferedChunks;
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
            cancellationToken.ThrowIfCancellationRequested();

            int id = Interlocked.Increment(ref _nextId);
            StreamState state = new StreamState(id, _maxBufferedBytes, _maxBufferedChunks);
            States[id] = state;

            state.AttachCancellation(cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                States.TryRemove(id, out _);
                state.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
            }

            string headers = BuildHeaderString(request.Headers);
            string credentialsMode = _sameOriginCredentials ? "same-origin" : "omit";

            // Wait for fetch headers (status + Content-Type) before returning. Do NOT add
            // continuation through the (non-existent) browser ThreadPool and hang the
            // open path forever. The JS bridge already defers the first ReadableStream
            // read via setTimeout(0), so MeaiOpenAiChatClient gets time to attach its
            // line reader before the first onChunk lands.
            // to be captured so the continuation reliably runs on the browser main thread.
            // ConfigureAwait(false) routes the continuation to ThreadPool, which doesn't
            // exist in WebGL, and the await never resumes.
            StreamState.OpenInfo openInfo;
            try
            {
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
                    Marshal.GetFunctionPointerForDelegate(_onErrorDelegate),
                    _maxBufferedBytes,
                    _maxBufferedChunks);
                openInfo = await state.WaitForOpenAsync();
            }
            catch
            {
                // WHY: WaitForOpenAsync can throw (typed jslib Timeout, cancellation). On that path the
                // stream is never handed to the caller, so nothing else would ever dispose the state or
                // remove its entry from the static States dictionary — clean up here before rethrowing.
                state.Dispose();
                throw;
            }

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
                state.SignalError(FetchSseTransportProtocol.CreateTransportException(err));
            }
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
            IntPtr onError,
            int maxBufferedBytes,
            int maxBufferedChunks);

        [DllImport("__Internal")]
        private static extern void CoreAi_FetchSseAcknowledge(int callId, int bytes);

        [DllImport("__Internal")]
        private static extern void CoreAi_FetchSseAbort(int callId);

        private static string BuildHeaderString(IReadOnlyList<KeyValuePair<string, string>> headers)
        {
            return FetchSseTransportProtocol.BuildHeaderString(headers);
        }

        private static IReadOnlyDictionary<string, IEnumerable<string>> ParseFlatHeaders(string flat)
        {
            return FetchSseTransportProtocol.ParseFlatHeaders(flat);
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
            private readonly FetchSseStream _stream;
            private CancellationTokenRegistration _cancelRegistration;
            private bool _cancelled;
            private bool _terminated;
            private int _disposed;
            private int _aborted;

            public int CallId { get; }

            public StreamState(int id, int maxBufferedBytes, int maxBufferedChunks)
            {
                CallId = id;
                _stream = new FetchSseStream(new FetchSseBacklog(maxBufferedBytes, maxBufferedChunks), this);
            }

            public Task<OpenInfo> WaitForOpenAsync() => _openTcs.Task;

            public void AttachCancellation(CancellationToken cancellationToken)
            {
                if (!cancellationToken.CanBeCanceled)
                {
                    return;
                }

                _cancelRegistration = cancellationToken.Register(() =>
                {
                    try
                    {
                        AbortBrowser();
                    }
                    catch (Exception ex)
                    {
                        // Browser abort must not throw back into the Unity cancellation path, but a
                        // failed abort leaks the browser-side fetch — log it instead of swallowing.
                        UnityEngine.Debug.LogWarning(
                            $"[FetchSSE] CoreAi_FetchSseAbort failed on cancellation (callId={CallId}): {ex.Message}");
                    }

                    SignalCancelled(cancellationToken);
                });
            }

            public void SignalOpen(int status, string errorBody, string headersFlat)
            {
                if (status == 0)
                {
                    SignalError(FetchSseTransportProtocol.CreateTransportException(errorBody));
                    return;
                }

                IReadOnlyDictionary<string, IEnumerable<string>> hdrs = ParseFlatHeaders(headersFlat);
                _openTcs.TrySetResult(new OpenInfo(status, errorBody ?? "", hdrs));
            }

            public void EnqueueChunk(string chunk)
            {
                if (_terminated || _disposed != 0) return;
                if (!_stream.TryEnqueue(chunk))
                {
                    SignalError(FetchSseTransportProtocol.CreateTransportException("BacklogOverflow"));
                    AbortBrowser();
                    return;
                }

                _stream.PumpPendingRead();
            }

            public void SignalDone()
            {
                if (_terminated) return;
                _terminated = true;
                _stream.NotifyCompleted();
                _stream.PumpPendingRead();
                // Defensive: if SignalDone fires before SignalOpen (shouldn't happen, but
                // caller surfaces a transport error instead of awaiting forever.
                // TODO: In the jslib flow onOpen always precedes onDone; the only way to get here
                // without an open result is when the onOpen dyncall itself threw and was swallowed
                // by CoreAiSseFetch.jslib's callOpen catch. In that case the fetch may actually have
                // succeeded (its chunks are already buffered in _queue), but the real HTTP status is
                // lost, so this surfaces a fake "HTTP 0 / no headers" error. Recovering the real
                // result would require the jslib to re-deliver the open info alongside done.
                _openTcs.TrySetResult(new OpenInfo(0, "fetch completed without headers",
                    new Dictionary<string, IEnumerable<string>>()));

                // onDone is terminal (the jslib finish() guard drops any later callback for this id),
                // so drop the States entry now: a consumer that abandons the stream without disposing
                // it would otherwise leak the entry in the static dictionary permanently.
                States.TryRemove(CallId, out _);
                _cancelRegistration.Dispose();
            }

            public void SignalError(Exception ex)
            {
                if (_terminated)
                {
                    return;
                }

                _terminated = true;
                if (ex is OperationCanceledException)
                {
                    _cancelled = true;
                }

                _stream.SetError(ex);
                _stream.PumpPendingRead();
                if (ex is OperationCanceledException)
                {
                    _openTcs.TrySetCanceled();
                }
                else if (ex is LlmClientException)
                {
                    // Typed transport failures (e.g. the jslib Timeout watchdog) propagate as-is
                    // from WaitForOpenAsync, so pre-header timeouts keep their LlmErrorCode
                    // instead of degrading into a status-0 "CORS/network" open result.
                    _openTcs.TrySetException(ex);
                }
                else
                {
                    _openTcs.TrySetResult(new OpenInfo(0, ex?.Message ?? "fetch error",
                        new Dictionary<string, IEnumerable<string>>()));
                }

                // onError is terminal (the jslib finish() guard drops any later callback for this id),
                // so drop the States entry now — same abandoned-stream leak rationale as SignalDone.
                States.TryRemove(CallId, out _);
                _cancelRegistration.Dispose();
            }

            public void SignalCancelled(CancellationToken cancellationToken)
            {
                _cancelled = true;
                _terminated = true;
                OperationCanceledException ex = new OperationCanceledException(cancellationToken);
                _stream.SetError(ex);
                _stream.PumpPendingRead();
                _openTcs.TrySetCanceled(cancellationToken);

                // On cancellation the consumer may never read to EOF or dispose the stream, so remove the
                // per-call state here too. Otherwise a cancelled-but-abandoned stream leaks its entry in the
                // static States dictionary, which grows unbounded over the app lifetime.
                States.TryRemove(CallId, out _);
                _cancelRegistration.Dispose();
            }

            public void AbortBrowser()
            {
                if (Interlocked.Exchange(ref _aborted, 1) == 0) CoreAi_FetchSseAbort(CallId);
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                _terminated = true;
                // Abandoned-before-read streams are reclaimed here. The dictionary is static, so an entry
                // that is never removed (consumer drops the stream without reading to completion) is a
                // permanent leak; removing on Dispose bounds the dictionary to live streams only.
                States.TryRemove(CallId, out _);
                try
                {
                    _cancelRegistration.Dispose();
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning(
                        $"[FetchSSE] cancellation registration dispose failed (callId={CallId}): {ex.Message}");
                }

                try
                {
                    AbortBrowser();
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning(
                        $"[FetchSSE] fetch release failed on dispose (callId={CallId}): {ex.Message}");
                }
            }

            public FetchSseStream Stream => _stream;
        }

        private sealed class FetchSseStream : Stream
        {
            private readonly FetchSseBacklog _backlog;
            private readonly StreamState _owner;
            private bool _isDone;
            private Exception _error;

            // WebGL is single-threaded: a synchronous Stream.Read that blocks on _signal.WaitOne would
            // freeze the JS event loop, preventing fetch chunks from ever being delivered. Instead, we
            // expose true async via ReadAsync that returns a TaskCompletionSource<int> when no data is
            // ready; EnqueueChunk / SignalDone / SignalError fulfil it from the JS callback.
            private TaskCompletionSource<int> _pendingTcs;
            private byte[] _pendingBuffer;
            private int _pendingOffset;
            private int _pendingCount;

            public FetchSseStream(FetchSseBacklog backlog, StreamState owner)
            {
                _backlog = backlog;
                _owner = owner;
            }

            public void SetError(Exception ex) { _error = ex; _backlog.Clear(); }
            public bool TryEnqueue(string chunk) => _backlog.TryEnqueue(chunk);
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
                if (buffer == null) return Task.FromException<int>(new ArgumentNullException(nameof(buffer)));
                if (offset < 0 || count < 0 || offset > buffer.Length - count)
                    return Task.FromException<int>(new ArgumentOutOfRangeException(nameof(count)));
                if (count == 0) return Task.FromResult(0);

                // WHY: there is a single pending-read slot; a second concurrent ReadAsync would silently
                // overwrite _pendingTcs and leave the first awaiter hanging until Dispose. Fail loudly.
                if (_pendingTcs != null)
                {
                    return Task.FromException<int>(new InvalidOperationException(
                        "FetchSseStream supports only one pending ReadAsync at a time; the previous read has not completed."));
                }

                int n = TryReadNonBlocking(buffer, offset, count);
                if (n > 0) return Task.FromResult(n);
                if (_isDone) return Task.FromResult(0);

                // WHY: park the read; PumpPendingRead fulfils it when the next chunk arrives or the stream ends.
                TaskCompletionSource<int> tcs = new TaskCompletionSource<int>();
                _pendingTcs = tcs;
                _pendingBuffer = buffer;
                _pendingOffset = offset;
                _pendingCount = count;
                if (cancellationToken.CanBeCanceled)
                {
                    // Dispose the registration once the read resolves; otherwise every parked read over a
                    // long stream leaves a live registration attached to the same token, accumulating until
                    // the token (request lifetime) is finally cancelled/disposed.
                    CancellationTokenRegistration registration =
                        cancellationToken.Register(() =>
                        {
                            // WHY: release the single pending-read slot only when it still belongs to this read.
                            if (ReferenceEquals(
                                    Interlocked.CompareExchange(ref _pendingTcs, null, tcs),
                                    tcs))
                            {
                                _pendingBuffer = null;
                                tcs.TrySetCanceled(cancellationToken);
                            }
                        });
                    tcs.Task.ContinueWith(
                        (_, state) => ((CancellationTokenRegistration)state).Dispose(),
                        registration,
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }

                // WHY: a chunk/done may have fired before we parked; drain now instead of waiting for another JS callback.
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
                    _pendingBuffer = null;
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
                int copied = _backlog.Read(buffer, offset, count, out int acknowledgedBytes);
                if (acknowledgedBytes > 0) CoreAi_FetchSseAcknowledge(_owner.CallId, acknowledgedBytes);
                return copied;
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
                    _backlog.Clear();
                    // A read parked by ReadWithIdleTimeoutAsync and then abandoned must complete
                    // (with EOF) here, otherwise its TCS and per-read cancellation registration
                    // stay alive until the whole request token ends.
                    PumpPendingRead();
                    try
                    {
                        _owner.AbortBrowser();
                    }
                    catch (Exception ex)
                    {
                        // A failed abort leaks the browser-side fetch/ReadableStream for this call.
                        UnityEngine.Debug.LogWarning(
                            $"[FetchSSE] CoreAi_FetchSseAbort failed on stream dispose (callId={_owner.CallId}): {ex.Message}");
                    }

                    // Owner disposal also removes the States entry and drops the request-token
                    // cancellation registration, which would otherwise pin this stream (queue,
                    // buffered chunks and all) to the caller's token lifetime on the success path.
                    try
                    {
                        _owner.Dispose();
                    }
                    catch (Exception ex)
                    {
                        UnityEngine.Debug.LogWarning(
                            $"[FetchSSE] stream state dispose failed (callId={_owner.CallId}): {ex.Message}");
                    }
                }

                base.Dispose(disposing);
            }
        }
    }
}
#endif
