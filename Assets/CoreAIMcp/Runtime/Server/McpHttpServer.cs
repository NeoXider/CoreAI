using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Mcp.Protocol;
using Newtonsoft.Json.Linq;

namespace CoreAI.Mcp.Server
{
    /// <summary>
    /// Localhost-only HTTP transport for the MCP server. Serves a single streamable-HTTP endpoint at
    /// <c>POST /mcp</c> speaking JSON-RPC 2.0, tolerant of the two response framings MCP clients use:
    /// plain <c>application/json</c> and <c>text/event-stream</c> (SSE) - chosen by the request's
    /// <c>Accept</c> header. GET notification streams require an initialized session; POST remains session-optional.
    /// <para>
    /// WHY (security): loopback binding alone does NOT protect this endpoint - a web page can POST to it
    /// cross-origin without a preflight, and DNS rebinding can even let it read the responses. Every
    /// request therefore passes <see cref="McpRequestGuard"/> first: it must arrive on the loopback
    /// interface, carry a loopback <c>Host</c>, carry no foreign <c>Origin</c>, use a JSON media type,
    /// stay under <see cref="MaxRequestBodyBytes"/>, and - when a token was configured - present
    /// <c>Authorization: Bearer &lt;token&gt;</c>. Never bind this to 0.0.0.0 or expose the port through a
    /// tunnel or reverse proxy.
    /// </para>
    /// </summary>
    public sealed class McpHttpServer : IDisposable
    {
        /// <summary>Default hard cap on an accepted request body, in bytes.</summary>
        public const int DefaultMaxRequestBodyBytes = 4 * 1024 * 1024;

        private const string EndpointPath = "/mcp";

        // WHY: ERROR_OPERATION_ABORTED - what GetContextAsync throws when the listener is stopped.
        private const int ListenerStoppedErrorCode = 995;

        private const int AcceptErrorBackoffMs = 250;

        // WHY: small rejected payloads are drained only within a short deadline.
        private const int MaxDrainBytes = 64 * 1024;
        private TimeSpan _bodyReadTimeout = TimeSpan.FromSeconds(10);
        private TimeSpan _writeTimeout = TimeSpan.FromSeconds(5);
        private int _maxNotificationStreams = 16;

        private readonly int _port;
        private readonly McpRpcDispatcher _dispatcher;
        private readonly Action<string> _log;
        private readonly Action<string> _logError;
        private readonly string _authToken;

        private HttpListener _listener;
        private CancellationTokenSource _cts;
        private readonly SemaphoreSlim _requestSlots = new(64, 64);
        private readonly object _contextsGate = new();
        private readonly Dictionary<HttpListenerContext, TaskCompletionSource<bool>> _contexts = new();
        private readonly object _streamsGate = new();
        private readonly Dictionary<string, NotificationStream> _streams = new(StringComparer.Ordinal);
        private Task _acceptLoop = Task.CompletedTask;

        /// <summary>Maximum time to receive one complete POST body.</summary>
        public TimeSpan BodyReadTimeout
        {
            get => _bodyReadTimeout;
            set => _bodyReadTimeout = value > TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(nameof(value));
        }

        /// <summary>Maximum time allowed for a network write to a slow client.</summary>
        public TimeSpan WriteTimeout
        {
            get => _writeTimeout;
            set => _writeTimeout = value > TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(nameof(value));
        }

        /// <summary>Maximum simultaneous notification streams; request admission has a separate hard limit of 64.</summary>
        public int MaxNotificationStreams
        {
            get => _maxNotificationStreams;
            set => _maxNotificationStreams = value > 0 && value <= 32 ? value : throw new ArgumentOutOfRangeException(nameof(value));
        }

        /// <summary>Completes after Stop releases transport handlers; never blocks the Unity main thread.</summary>
        public Task Completion { get; private set; } = Task.CompletedTask;

        /// <param name="port">TCP port to listen on (loopback).</param>
        /// <param name="dispatcher">The JSON-RPC router (which owns session issuance).</param>
        /// <param name="log">Info sink.</param>
        /// <param name="logError">Error sink.</param>
        /// <param name="authToken">
        /// Bearer token every request must present. Null or empty disables token auth, leaving only the
        /// Origin/Host checks - acceptable only on a fully trusted machine.
        /// </param>
        public McpHttpServer(int port, McpRpcDispatcher dispatcher,
            Action<string> log = null, Action<string> logError = null, string authToken = null)
        {
            _port = port;
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _log = log ?? (_ => { });
            _logError = logError ?? (_ => { });
            _authToken = string.IsNullOrWhiteSpace(authToken) ? null : authToken.Trim();
        }

        /// <summary>True while the listener is accepting connections.</summary>
        public bool IsRunning => _listener is { IsListening: true };

        /// <summary>The loopback URL clients connect to.</summary>
        public string Url => $"http://127.0.0.1:{_port}{EndpointPath}";

        /// <summary>True when a bearer token is required on every request.</summary>
        public bool RequiresAuth => _authToken != null;

        /// <summary>Largest accepted request body in bytes; anything bigger is refused with 413.</summary>
        public int MaxRequestBodyBytes { get; set; } = DefaultMaxRequestBodyBytes;

        /// <summary>Starts the listener and the accept loop. Throws when the port cannot be bound.</summary>
        public void Start()
        {
            if (IsRunning)
            {
                return;
            }

            _listener = new HttpListener();
            // WHY: bind the loopback ROOT rather than "/mcp/" - an HttpListener path prefix of "/mcp/"
            // does not match a POST to "/mcp" (no trailing slash), which is exactly what MCP clients
            // send. Binding root and routing in the handler accepts "/mcp", "/mcp/", and "/" alike while
            // staying strictly on 127.0.0.1.
            _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
            _listener.Start();

            // WHY: a Start() after Stop() would otherwise leak the previous token source.
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            // WHY: fire-and-forget accept loop; its lifetime is bound to the listener + CTS, both stopped
            // in Stop(). We do not join it - GetContextAsync unblocks when the listener is closed.
            _dispatcher.SupportsListChanged = true;
            _dispatcher.Registry.Changed += SignalCatalogChanged;
            _acceptLoop = AcceptLoopAsync(_listener, _cts.Token);
            _log($"CoreAI MCP server listening on {Url}");
        }

        /// <summary>Stops the listener and cancels in-flight work.</summary>
        public void Stop()
        {
            _dispatcher.SupportsListChanged = false;
            _dispatcher.Registry.Changed -= SignalCatalogChanged;
            try
            {
                _cts?.Cancel();
            }
            catch (Exception ex)
            {
                _logError($"cancelling in-flight MCP work failed during stop: {ex.GetType().Name}: {ex.Message}");
            }

            try
            {
                _listener?.Stop();
                _listener?.Close();
            }
            catch (Exception ex)
            {
                _logError($"closing the MCP listener failed during stop: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                _listener = null;
            }

            lock (_contextsGate)
            {
                Completion = Task.WhenAll(_contexts.Values.Select(completion => completion.Task).Append(_acceptLoop));
                foreach (HttpListenerContext context in _contexts.Keys)
                {
                    try { context.Response.Abort(); }
                    catch (ObjectDisposedException) { }
                }
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Stop();
            _cts?.Dispose();
            _cts = null;
        }

        private async Task AcceptLoopAsync(HttpListener listener, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (InvalidOperationException)
                {
                    return;
                }
                catch (HttpListenerException ex) when (ex.ErrorCode == ListenerStoppedErrorCode)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // WHY: a transient accept failure (a client that reset mid-handshake) must not silently
                    // kill the loop while IsRunning keeps reporting true - log it and keep serving.
                    if (cancellationToken.IsCancellationRequested || !IsRunning)
                    {
                        return;
                    }

                    _logError($"accept loop error, still listening: {ex.GetType().Name}: {ex.Message}");
                    try
                    {
                        await Task.Delay(AcceptErrorBackoffMs, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    continue;
                }

                if (!_requestSlots.Wait(0))
                {
                    context.Response.StatusCode = 503;
                    context.Response.KeepAlive = false;
                    context.Response.Close();
                    continue;
                }
                TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_contextsGate)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        context.Response.Abort();
                        _requestSlots.Release();
                        return;
                    }
                    _contexts.Add(context, completion);
                }
                // WHY: admission is bounded before scheduling; synchronous host tools cannot block accepting connections.
                _ = Task.Run(() => HandleContextAsync(context, cancellationToken, completion));
            }
        }

        private async Task HandleContextAsync(HttpListenerContext context, CancellationToken cancellationToken,
            TaskCompletionSource<bool> completion)
        {
            try
            {
                HttpListenerRequest request = context.Request;

                if (await TryRejectAsync(context, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                if (string.Equals(request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
                {
                    await ServeNotificationsAsync(context, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (!string.Equals(request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = 405;
                    context.Response.Close();
                    return;
                }

                if (!McpRequestGuard.IsContentTypeAllowed(request.ContentType))
                {
                    await DenyAsync(context, 415,
                        $"Unsupported Media Type: '{request.ContentType}'. POST JSON-RPC as application/json.", cancellationToken).ConfigureAwait(false);
                    return;
                }

                string body = await ReadBodyAsync(request, cancellationToken).ConfigureAwait(false);
                if (body == null)
                {
                    await DenyAsync(context, 413,
                        $"Payload Too Large: the request body exceeds {MaxRequestBodyBytes} bytes.", cancellationToken).ConfigureAwait(false);
                    return;
                }

                bool wantsSse = AcceptsEventStream(request);

                if (!JsonRpc.TryParse(body, out JObject rpcRequest, out JObject parseError))
                {
                    await WriteJsonAsync(context, parseError, null, HttpStatusCode.OK, wantsSse, cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }

                McpDispatchResult result =
                    await AwaitOperationAsync(_dispatcher.DispatchAsync(rpcRequest, cancellationToken),
                        Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);

                if (result.IsNotification)
                {
                    // No body for a notification; acknowledge with 202.
                    context.Response.StatusCode = 202;
                    context.Response.Close();
                    return;
                }

                await WriteJsonAsync(context, result.Response, result.IssuedSessionId, HttpStatusCode.OK, wantsSse, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                await DenyAsync(context, 408, "Request body or response exceeded its deadline.", cancellationToken, false).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                try { context.Response.Abort(); } catch (ObjectDisposedException) { }
            }
            catch (Exception ex)
            {
                _logError($"CoreAI MCP request failed: {ex.Message}");
                try
                {
                    context.Response.StatusCode = 500;
                    context.Response.Close();
                }
                catch (Exception closeError)
                {
                    _logError($"could not close the failed MCP response: {closeError.Message}");
                }
            }

            finally
            {
                lock (_contextsGate) _contexts.Remove(context);
                _requestSlots.Release();
                completion.TrySetResult(true);
            }
        }

        // WHY: the whole security decision lives here, ahead of routing, so no method can bypass it.
        private async Task<bool> TryRejectAsync(HttpListenerContext context, CancellationToken cancellationToken)
        {
            HttpListenerRequest request = context.Request;

            if (!request.IsLocal)
            {
                await DenyAsync(context, 403, "Forbidden: the CoreAI MCP endpoint serves loopback clients only.", cancellationToken).ConfigureAwait(false);
                return true;
            }

            if (!McpRequestGuard.IsHostAllowed(request.Headers?["Host"], _port))
            {
                await DenyAsync(context, 403,
                    "Forbidden: unexpected Host header (DNS-rebinding protection). " +
                    $"Connect to http://127.0.0.1:{_port}{EndpointPath} directly.", cancellationToken).ConfigureAwait(false);
                return true;
            }

            string origin = request.Headers?["Origin"];
            if (!McpRequestGuard.IsOriginAllowed(origin, _port))
            {
                await DenyAsync(context, 403, $"Forbidden: Origin '{origin}' is not allowed for the CoreAI MCP endpoint.", cancellationToken).ConfigureAwait(false);
                return true;
            }

            if (!McpRequestGuard.IsAuthorized(request.Headers?["Authorization"], _authToken))
            {
                context.Response.AddHeader("WWW-Authenticate", "Bearer realm=\"coreai-mcp\"");
                await DenyAsync(context, 401,
                    "Unauthorized: send 'Authorization: Bearer <token>'. The token is printed to the game " +
                    "console when the CoreAI MCP server starts.", cancellationToken).ConfigureAwait(false);
                return true;
            }

            string sessionId = request.Headers[McpServerInfo.SessionHeader];
            if (sessionId != null && !_dispatcher.IsKnownSession(sessionId))
            {
                await DenyAsync(context, 404, "The MCP session expired or was removed; initialize again without Mcp-Session-Id.",
                    cancellationToken).ConfigureAwait(false);
                return true;
            }
            // WHY: every retained session negotiated the advertised version; sessionless legacy POST uses 2025-03-26.
            string protocolVersion = request.Headers["MCP-Protocol-Version"] ??
                (sessionId != null ? McpServerInfo.DefaultProtocolVersion : "2025-03-26");
            if (protocolVersion != McpServerInfo.DefaultProtocolVersion && protocolVersion != "2025-03-26")
            {
                await DenyAsync(context, 400, "Unsupported MCP-Protocol-Version; negotiate a supported version with initialize.",
                    cancellationToken).ConfigureAwait(false);
                return true;
            }

            return false;
        }

        private async Task DenyAsync(HttpListenerContext context, int status, string message,
            CancellationToken cancellationToken, bool drain = true)
        {
            _logError($"rejected {context.Request.HttpMethod} from {context.Request.RemoteEndPoint}: {message}");
            try
            {
                if (drain) await DrainRequestBodyAsync(context.Request, cancellationToken).ConfigureAwait(false);
                byte[] bytes = Encoding.UTF8.GetBytes(message);
                context.Response.StatusCode = status;
                context.Response.ContentType = "text/plain; charset=utf-8";
                context.Response.KeepAlive = false;
                context.Response.ContentLength64 = bytes.Length;
                await WriteBytesAsync(context.Response, bytes, cancellationToken).ConfigureAwait(false);
                context.Response.Close();
            }
            catch (Exception)
            {
                try { context.Response.Abort(); } catch (ObjectDisposedException) { }
            }
        }

        private async Task DrainRequestBodyAsync(HttpListenerRequest request, CancellationToken cancellationToken)
        {
            if (!request.HasEntityBody || request.ContentLength64 < 0 || request.ContentLength64 > MaxDrainBytes) return;
            try
            {
                await AwaitOperationAsync(DrainKnownBodyAsync(request), TimeSpan.FromMilliseconds(250),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException) { }
            catch (IOException) { }
        }

        private static async Task<bool> DrainKnownBodyAsync(HttpListenerRequest request)
        {
            byte[] scratch = new byte[4096];
            int drained = 0;
            while (drained < MaxDrainBytes)
            {
                int read = await request.InputStream.ReadAsync(scratch, 0,
                    Math.Min(scratch.Length, MaxDrainBytes - drained)).ConfigureAwait(false);
                if (read == 0) break;
                drained += read;
            }
            return true;
        }

        private Task<string> ReadBodyAsync(HttpListenerRequest request, CancellationToken cancellationToken)
        {
            return AwaitOperationAsync(ReadBoundedBytesAsync(request, cancellationToken), BodyReadTimeout, cancellationToken);
        }

        private async Task<string> ReadBoundedBytesAsync(HttpListenerRequest request, CancellationToken cancellationToken)
        {
            int limit = Math.Max(0, MaxRequestBodyBytes);
            if (request.ContentLength64 > limit) return null;
            using MemoryStream body = new();
            byte[] buffer = new byte[8192];
            while (true)
            {
                int read = await request.InputStream.ReadAsync(buffer, 0,
                    (int)Math.Min(buffer.Length, (long)limit - body.Length + 1), cancellationToken).ConfigureAwait(false);
                if (read == 0) return Encoding.UTF8.GetString(body.ToArray());
                if (body.Length + read > limit) return null;
                body.Write(buffer, 0, read);
            }
        }

        private static async Task<T> AwaitOperationAsync<T>(Task<T> operation, TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (timeout != Timeout.InfiniteTimeSpan) deadline.CancelAfter(timeout);
            Task cancelled = Task.Delay(Timeout.Infinite, deadline.Token);
            if (await Task.WhenAny(operation, cancelled).ConfigureAwait(false) == operation)
            {
                deadline.Cancel();
                return await operation.ConfigureAwait(false);
            }
            _ = ObserveCompletionAsync(operation);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException();
        }

        private static async Task ObserveCompletionAsync(Task operation)
        {
            try { await operation.ConfigureAwait(false); } catch (Exception) { }
        }

        private async Task WriteBytesAsync(HttpListenerResponse response, byte[] bytes, CancellationToken cancellationToken)
        {
            await AwaitOperationAsync(WriteAndFlushAsync(response, bytes, cancellationToken), WriteTimeout,
                cancellationToken).ConfigureAwait(false);
        }

        private static async Task<bool> WriteAndFlushAsync(HttpListenerResponse response, byte[] bytes,
            CancellationToken cancellationToken)
        {
            await response.OutputStream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
            await response.OutputStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        private void SignalCatalogChanged()
        {
            lock (_streamsGate)
                foreach (NotificationStream stream in _streams.Values) stream.Signal();
        }

        private async Task ServeNotificationsAsync(HttpListenerContext context, CancellationToken cancellationToken)
        {
            if (!AcceptsEventStream(context.Request))
            {
                await DenyAsync(context, 406, "GET requires Accept: text/event-stream.", cancellationToken).ConfigureAwait(false);
                return;
            }
            string sessionId = context.Request.Headers[McpServerInfo.SessionHeader];
            if (!_dispatcher.IsKnownSession(sessionId))
            {
                await DenyAsync(context, string.IsNullOrEmpty(sessionId) ? 400 : 404,
                    "Initialize first and send a current Mcp-Session-Id for notifications.", cancellationToken).ConfigureAwait(false);
                return;
            }
            NotificationStream stream = new();
            bool admitted;
            lock (_streamsGate)
            {
                bool reconnect = _streams.TryGetValue(sessionId, out NotificationStream previous);
                admitted = reconnect || _streams.Count < MaxNotificationStreams;
                if (admitted)
                {
                    previous?.Cancel();
                    _streams[sessionId] = stream;
                }
            }
            if (!admitted)
            {
                stream.Dispose();
                await DenyAsync(context, 409, "Notification stream already open or stream capacity reached.", cancellationToken).ConfigureAwait(false);
                return;
            }

            // WHY: this loop is open-ended - a client can hold the connection for hours. HandleContextAsync
            // NOTE: this did NOT fix NotificationCapacity_IsBounded_AndPostContinuesWorking, which still
            // times out under the Unity editor and whose cause is not yet understood. It stands on its
            // own merits - a stream held open for hours has no business occupying a pooled worker - and
            // AbandonedNotificationStream_DoesNotBlockLaterRequests_OrCleanShutdown covers what it does
            // guarantee. Do not read it as a closed investigation.
            // dispatches every request (POST included) through the shared ThreadPool via Task.Run; awaiting
            // an unbounded loop inline on that pooled worker ties it up for the stream's whole lifetime.
            // TaskCreationOptions.LongRunning hints the scheduler to give the loop its own dedicated thread
            // instead of a pooled slot, so an open (or merely slow-draining) stream can never compete with -
            // or starve - ordinary short-lived request handling behind it. The thread is not leaked: it
            // observes the same linked cancellation as every other request (Stop() cancels the server-wide
            // token), and this method still awaits it to completion, so HandleContextAsync's request-slot
            // release and Stop()'s Completion tracking are unaffected.
            await Task.Factory.StartNew(
                    () => RunNotificationLoopAsync(context, sessionId, stream, cancellationToken),
                    CancellationToken.None, TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
                    TaskScheduler.Default)
                .Unwrap().ConfigureAwait(false);
        }

        private async Task RunNotificationLoopAsync(HttpListenerContext context, string sessionId,
            NotificationStream stream, CancellationToken cancellationToken)
        {
            using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stream.Token);
            cancellationToken = lifetime.Token;
            try
            {
                context.Response.ContentType = "text/event-stream";
                context.Response.AddHeader("Cache-Control", "no-cache");
                context.Response.SendChunked = true;
                long sentRevision = -1;
                while (!cancellationToken.IsCancellationRequested && _dispatcher.IsKnownSession(sessionId))
                {
                    long revision = _dispatcher.Registry.Revision;
                    if (revision > sentRevision)
                    {
                        JObject notification = new() { ["jsonrpc"] = "2.0", ["method"] = McpMethods.ToolsListChangedNotification,
                            ["params"] = new JObject { ["_meta"] = new JObject { ["coreai/catalogRevision"] = revision } } };
                        await WriteBytesAsync(context.Response, Encoding.UTF8.GetBytes("event: message\ndata: " +
                            notification.ToString(Newtonsoft.Json.Formatting.None) + "\n\n"), cancellationToken).ConfigureAwait(false);
                        sentRevision = revision;
                    }
                    bool signalled = await stream.WaitAsync(cancellationToken).ConfigureAwait(false);
                    if (!signalled)
                        await WriteBytesAsync(context.Response, Encoding.UTF8.GetBytes(": keepalive\n\n"),
                            cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            finally
            {
                lock (_streamsGate)
                    if (_streams.TryGetValue(sessionId, out NotificationStream current) && ReferenceEquals(current, stream))
                        _streams.Remove(sessionId);
                stream.Dispose();
                try { context.Response.Close(); } catch (ObjectDisposedException) { }
            }
        }

        private sealed class NotificationStream : IDisposable
        {
            private readonly SemaphoreSlim _signal = new(0, 1);
            private readonly CancellationTokenSource _cancel = new();
            public CancellationToken Token => _cancel.Token;
            public void Cancel() => _cancel.Cancel();
            public void Signal()
            {
                if (_signal.CurrentCount == 0) _signal.Release();
            }
            public Task<bool> WaitAsync(CancellationToken cancellationToken)
                => _signal.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            public void Dispose() { _signal.Dispose(); _cancel.Dispose(); }
        }

        private static bool AcceptsEventStream(HttpListenerRequest request)
        {
            string accept = request.Headers?["Accept"];
            if (string.IsNullOrEmpty(accept))
            {
                return false;
            }

            // WHY: prefer SSE only when the client explicitly asks for it; many clients send
            // "application/json, text/event-stream" and are happy with either, but if event-stream is
            // present we honor it since strict streamable-HTTP clients parse the SSE framing.
            return accept.IndexOf("text/event-stream", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private async Task WriteJsonAsync(HttpListenerContext context, JObject payload, string sessionId,
            HttpStatusCode status, bool asSse, CancellationToken cancellationToken)
        {
            byte[] bytes;
            HttpListenerResponse response = context.Response;
            response.StatusCode = (int)status;
            if (!string.IsNullOrEmpty(sessionId))
            {
                response.AddHeader(McpServerInfo.SessionHeader, sessionId);
            }

            string json = (payload ?? new JObject()).ToString(Newtonsoft.Json.Formatting.None);

            if (asSse)
            {
                // SSE framing: one "message" event carrying the JSON-RPC response, then the stream closes.
                response.ContentType = "text/event-stream";
                response.AddHeader("Cache-Control", "no-cache");
                bytes = Encoding.UTF8.GetBytes($"event: message\ndata: {json}\n\n");
            }
            else
            {
                response.ContentType = "application/json";
                bytes = Encoding.UTF8.GetBytes(json);
            }

            response.ContentLength64 = bytes.Length;
            // WHY: write then Close() (which flushes and closes the output stream). Do not also dispose
            // the stream via a using block - that double-closes and throws ObjectDisposedException.
            await WriteBytesAsync(response, bytes, cancellationToken).ConfigureAwait(false);
            response.Close();
        }
    }
}
