using System;
using System.IO;
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
    /// <c>Accept</c> header. Session ids are issued on initialize but never required.
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

        // Largest refused body still worth reading so the rejection response survives the close.
        private const int MaxDrainBytes = 64 * 1024;

        private readonly int _port;
        private readonly McpRpcDispatcher _dispatcher;
        private readonly Action<string> _log;
        private readonly Action<string> _logError;
        private readonly string _authToken;

        private HttpListener _listener;
        private CancellationTokenSource _cts;

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
            _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
            _log($"CoreAI MCP server listening on {Url}");
        }

        /// <summary>Stops the listener and cancels in-flight work.</summary>
        public void Stop()
        {
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
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Stop();
            _cts?.Dispose();
            _cts = null;
        }

        private async Task AcceptLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && _listener != null && _listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().ConfigureAwait(false);
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

                // WHY: handle each request without blocking the accept loop; local clients may pipeline.
                _ = Task.Run(() => HandleContextAsync(context, cancellationToken));
            }
        }

        private async Task HandleContextAsync(HttpListenerContext context, CancellationToken cancellationToken)
        {
            try
            {
                HttpListenerRequest request = context.Request;

                if (TryReject(context))
                {
                    return;
                }

                if (string.Equals(request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
                {
                    // WHY: this server offers no server-initiated SSE stream, so a GET to the endpoint gets
                    // 405 per the streamable-HTTP spec; legacy HTTP+SSE-only clients should bridge via
                    // `npx mcp-remote` (see README).
                    context.Response.StatusCode = 405;
                    context.Response.AddHeader("Allow", "POST");
                    context.Response.Close();
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
                    Deny(context, 415,
                        $"Unsupported Media Type: '{request.ContentType}'. POST JSON-RPC as application/json.");
                    return;
                }

                string body = await ReadBodyAsync(request).ConfigureAwait(false);
                if (body == null)
                {
                    Deny(context, 413,
                        $"Payload Too Large: the request body exceeds {MaxRequestBodyBytes} bytes.");
                    return;
                }

                bool wantsSse = AcceptsEventStream(request);

                if (!JsonRpc.TryParse(body, out JObject rpcRequest, out JObject parseError))
                {
                    await WriteJsonAsync(context, parseError, null, HttpStatusCode.OK, wantsSse)
                        .ConfigureAwait(false);
                    return;
                }

                McpDispatchResult result =
                    await _dispatcher.DispatchAsync(rpcRequest, cancellationToken).ConfigureAwait(false);

                if (result.IsNotification)
                {
                    // No body for a notification; acknowledge with 202.
                    context.Response.StatusCode = 202;
                    context.Response.Close();
                    return;
                }

                await WriteJsonAsync(context, result.Response, result.IssuedSessionId, HttpStatusCode.OK, wantsSse)
                    .ConfigureAwait(false);
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
        }

        // WHY: the whole security decision lives here, ahead of routing, so no method can bypass it.
        private bool TryReject(HttpListenerContext context)
        {
            HttpListenerRequest request = context.Request;

            if (!request.IsLocal)
            {
                Deny(context, 403, "Forbidden: the CoreAI MCP endpoint serves loopback clients only.");
                return true;
            }

            if (!McpRequestGuard.IsHostAllowed(request.Headers?["Host"], _port))
            {
                Deny(context, 403,
                    "Forbidden: unexpected Host header (DNS-rebinding protection). " +
                    $"Connect to http://127.0.0.1:{_port}{EndpointPath} directly.");
                return true;
            }

            string origin = request.Headers?["Origin"];
            if (!McpRequestGuard.IsOriginAllowed(origin, _port))
            {
                Deny(context, 403, $"Forbidden: Origin '{origin}' is not allowed for the CoreAI MCP endpoint.");
                return true;
            }

            if (!McpRequestGuard.IsAuthorized(request.Headers?["Authorization"], _authToken))
            {
                context.Response.AddHeader("WWW-Authenticate", "Bearer realm=\"coreai-mcp\"");
                Deny(context, 401,
                    "Unauthorized: send 'Authorization: Bearer <token>'. The token is printed to the game " +
                    "console when the CoreAI MCP server starts.");
                return true;
            }

            return false;
        }

        private void Deny(HttpListenerContext context, int status, string message)
        {
            _logError($"rejected {context.Request.HttpMethod} from {context.Request.RemoteEndPoint}: {message}");

            try
            {
                DrainRequestBody(context.Request);

                byte[] bytes = Encoding.UTF8.GetBytes(message);
                HttpListenerResponse response = context.Response;
                response.StatusCode = status;
                response.ContentType = "text/plain; charset=utf-8";
                // WHY: the request body may be unread (oversized payloads); do not reuse the connection.
                response.KeepAlive = false;
                response.ContentLength64 = bytes.Length;
                response.OutputStream.Write(bytes, 0, bytes.Length);
                response.Close();
            }
            catch (Exception ex)
            {
                _logError($"could not write the MCP rejection response: {ex.Message}");
            }
        }

        /// <summary>
        /// Discards what is left of a refused request's body, up to <see cref="MaxDrainBytes"/>.
        /// <para>
        /// WHY: closing the response while the client is still sending resets the connection, and the
        /// client then observes a transport error instead of the 413/415/401 we just wrote. Draining
        /// first lets the refusal actually arrive. The cap keeps the defence intact: a body too big to
        /// drain cheaply is dropped on the floor exactly as before, since reading it is the cost the
        /// limit exists to avoid.
        /// </para>
        /// </summary>
        private static void DrainRequestBody(HttpListenerRequest request)
        {
            if (request == null || !request.HasEntityBody || request.ContentLength64 > MaxDrainBytes)
            {
                return;
            }

            try
            {
                byte[] scratch = new byte[4096];
                int drained = 0;
                while (drained <= MaxDrainBytes)
                {
                    int read = request.InputStream.Read(scratch, 0, scratch.Length);
                    if (read <= 0)
                    {
                        return;
                    }

                    drained += read;
                }
            }
            catch (Exception)
            {
                // The client hung up or the body stalled; the refusal response is still worth attempting.
            }
        }

        /// <summary>Reads the body up to the cap, or returns null when the request is too large.</summary>
        private async Task<string> ReadBodyAsync(HttpListenerRequest request)
        {
            if (request.ContentLength64 > MaxRequestBodyBytes)
            {
                return null;
            }

            using StreamReader reader = new(request.InputStream, request.ContentEncoding ?? Encoding.UTF8);
            char[] buffer = new char[8192];
            StringBuilder body = new();

            while (true)
            {
                int read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                if (read <= 0)
                {
                    return body.ToString();
                }

                body.Append(buffer, 0, read);
                if (body.Length > MaxRequestBodyBytes)
                {
                    return null;
                }
            }
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
            HttpStatusCode status, bool asSse)
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
            await response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            response.Close();
        }
    }
}
