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
    /// WHY (security): binds strictly to 127.0.0.1, is opt-in (never auto-started), and performs NO
    /// authentication - any local process can call it. That is acceptable ONLY on the loopback
    /// interface: never bind this to 0.0.0.0 or a routable address, and never expose the port through a
    /// tunnel or reverse proxy without adding auth first.
    /// </para>
    /// </summary>
    public sealed class McpHttpServer : IDisposable
    {
        private const string EndpointPath = "/mcp";

        private readonly int _port;
        private readonly McpRpcDispatcher _dispatcher;
        private readonly Action<string> _log;
        private readonly Action<string> _logError;

        private HttpListener _listener;
        private CancellationTokenSource _cts;

        /// <param name="port">TCP port to listen on (loopback).</param>
        /// <param name="dispatcher">The JSON-RPC router (which owns session issuance).</param>
        /// <param name="log">Info sink.</param>
        /// <param name="logError">Error sink.</param>
        public McpHttpServer(int port, McpRpcDispatcher dispatcher,
            Action<string> log = null, Action<string> logError = null)
        {
            _port = port;
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _log = log ?? (_ => { });
            _logError = logError ?? (_ => { });
        }

        /// <summary>True while the listener is accepting connections.</summary>
        public bool IsRunning => _listener is { IsListening: true };

        /// <summary>The loopback URL clients connect to.</summary>
        public string Url => $"http://127.0.0.1:{_port}{EndpointPath}";

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
            catch (Exception)
            {
                // ignore
            }

            try
            {
                _listener?.Stop();
                _listener?.Close();
            }
            catch (Exception)
            {
                // ignore
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
                catch (Exception)
                {
                    // Listener stopped/disposed - exit the loop quietly.
                    break;
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

                string body;
                using (StreamReader reader = new(request.InputStream, request.ContentEncoding ?? Encoding.UTF8))
                {
                    body = await reader.ReadToEndAsync().ConfigureAwait(false);
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
                catch (Exception)
                {
                    // ignore
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
