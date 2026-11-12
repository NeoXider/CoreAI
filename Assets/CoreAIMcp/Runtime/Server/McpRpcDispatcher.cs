using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Mcp.Protocol;
using CoreAI.Mcp.Tools;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CoreAI.Mcp.Server
{
    /// <summary>Outcome of dispatching one JSON-RPC message: the response body (if any) plus transport hints.</summary>
    public sealed class McpDispatchResult
    {
        private McpDispatchResult(JObject response, bool isNotification, string issuedSessionId)
        {
            Response = response;
            IsNotification = isNotification;
            IssuedSessionId = issuedSessionId;
        }

        /// <summary>The JSON-RPC response object, or null for a notification (no body).</summary>
        public JObject Response { get; }

        /// <summary>True when the message was a notification; the transport should reply 202 with no body.</summary>
        public bool IsNotification { get; }

        /// <summary>A session id the transport should surface via <c>Mcp-Session-Id</c> (initialize only), else null.</summary>
        public string IssuedSessionId { get; }

        /// <summary>Builds a normal response result.</summary>
        public static McpDispatchResult Reply(JObject response, string issuedSessionId = null)
        {
            return new McpDispatchResult(response, false, issuedSessionId);
        }

        /// <summary>Builds a notification (no body) result.</summary>
        public static McpDispatchResult Notification()
        {
            return new McpDispatchResult(null, true, null);
        }
    }

    /// <summary>
    /// Pure JSON-RPC 2.0 / MCP method router. Owns NO sockets: it takes a parsed request object and
    /// returns a response object, marshalling <c>tools/call</c> onto the main thread via the injected
    /// <see cref="IMainThreadDispatcher"/>. Engine-free and fully unit-testable.
    /// <para>
    /// Cancellation: every <c>tools/call</c> in flight is registered under its session and request id, and
    /// a <c>notifications/cancelled</c> naming that id from the same session cancels it. Requests without a
    /// session share one sessionless namespace.
    /// </para>
    /// </summary>
    public sealed class McpRpcDispatcher
    {
        private readonly McpToolRegistry _registry;
        private readonly McpSessionStore _sessions;
        private readonly IMainThreadDispatcher _mainThread;
        private readonly object _inFlightGate = new();
        private readonly Dictionary<string, CancellationTokenSource> _inFlight = new(StringComparer.Ordinal);

        /// <param name="registry">The tools available in the current composition.</param>
        /// <param name="sessions">Session id issuer/validator.</param>
        /// <param name="mainThread">Main-thread marshaller for tool invocation.</param>
        public McpRpcDispatcher(McpToolRegistry registry, McpSessionStore sessions, IMainThreadDispatcher mainThread)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
            _mainThread = mainThread ?? throw new ArgumentNullException(nameof(mainThread));
        }

        /// <summary>The live catalog shared with host updates and notification transport.</summary>
        public McpToolRegistry Registry => _registry;

        /// <summary>
        /// Text returned as <c>instructions</c> by <c>initialize</c>: guidance an MCP client may hand to its
        /// model. Null or blank omits the field.
        /// </summary>
        public string Instructions { get; set; }

        private long _requestCount;
        private long _toolCallCount;
        private long _toolErrorCount;
        private string _lastError;

        /// <summary>JSON-RPC messages routed since this dispatcher was created, notifications included.</summary>
        public long RequestCount => Interlocked.Read(ref _requestCount);

        /// <summary><c>tools/call</c> requests routed.</summary>
        public long ToolCallCount => Interlocked.Read(ref _toolCallCount);

        /// <summary><c>tools/call</c> requests that ended in a protocol error or an <c>isError</c> result.</summary>
        public long ToolErrorCount => Interlocked.Read(ref _toolErrorCount);

        /// <summary>The last failure text (a tool's <c>isError</c> text or a protocol error), at most 300 characters.</summary>
        public string LastError => Volatile.Read(ref _lastError);

        /// <summary>Number of <c>tools/call</c> requests that can currently be cancelled by id.</summary>
        public int CancellableCallCount { get { lock (_inFlightGate) return _inFlight.Count; } }

        /// <summary>True only while a notification transport is available.</summary>
        internal bool SupportsListChanged { get; set; }

        internal bool IsKnownSession(string sessionId) => _sessions.IsKnown(sessionId);

        internal bool RemoveSession(string sessionId) => _sessions.Remove(sessionId);

        /// <summary>Routes a single parsed JSON-RPC request that arrived without a session.</summary>
        public Task<McpDispatchResult> DispatchAsync(JObject request, CancellationToken cancellationToken)
        {
            return DispatchAsync(request, null, cancellationToken);
        }

        /// <summary>
        /// Routes a single parsed JSON-RPC request. <paramref name="sessionId"/> is the request's
        /// <c>Mcp-Session-Id</c> (null when absent); it scopes request ids for cancellation.
        /// </summary>
        public async Task<McpDispatchResult> DispatchAsync(JObject request, string sessionId,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            JToken id = request?["id"];
            JToken version = request?["jsonrpc"];
            JToken methodToken = request?["method"];
            JToken parameters = request?["params"];
            bool validId = id == null || id.Type == JTokenType.Null || id.Type == JTokenType.String ||
                           id.Type == JTokenType.Integer || id.Type == JTokenType.Float;
            if (version?.Type != JTokenType.String || (string)version != JsonRpc.Version ||
                methodToken?.Type != JTokenType.String || !validId ||
                (parameters != null && parameters.Type != JTokenType.Object && parameters.Type != JTokenType.Array))
            {
                return McpDispatchResult.Reply(JsonRpc.Error(validId ? id : null, JsonRpcErrorCodes.InvalidRequest,
                    "Invalid Request: expected a JSON-RPC 2.0 request with a string method, structured params, and a scalar id."));
            }
            string method = (string)methodToken;

            if (string.IsNullOrEmpty(method))
            {
                return McpDispatchResult.Reply(JsonRpc.Error(id, JsonRpcErrorCodes.InvalidRequest,
                    "Invalid Request: 'method' is required."));
            }

            // WHY: Notifications (no id) never get a response body; the client fires and forgets.
            if (JsonRpc.IsNotification(request))
            {
                if (method == McpMethods.CancelledNotification)
                {
                    CancelInFlight(sessionId, (parameters as JObject)?["requestId"]);
                }

                // notifications/initialized and any other client notification are accepted as no-ops.
                return McpDispatchResult.Notification();
            }

            switch (method)
            {
                case McpMethods.Initialize:
                    return HandleInitialize(id, parameters as JObject);

                case McpMethods.Ping:
                    return McpDispatchResult.Reply(JsonRpc.Result(id, new JObject()));

                case McpMethods.ToolsList:
                    return McpDispatchResult.Reply(JsonRpc.Result(id, new JObject
                    {
                        ["tools"] = _registry.ToListJson()
                    }));

                case McpMethods.ToolsCall:
                    Interlocked.Increment(ref _toolCallCount);
                    McpDispatchResult called = await HandleToolsCallAsync(id, request, sessionId, cancellationToken)
                        .ConfigureAwait(false);
                    NoteFailure(called.Response);
                    return called;

                default:
                    return McpDispatchResult.Reply(JsonRpc.Error(id, JsonRpcErrorCodes.MethodNotFound,
                        $"Method not found: {method}. This server implements initialize, ping, tools/list and tools/call."));
            }
        }

        private McpDispatchResult HandleInitialize(JToken id, JObject parameters)
        {
            JToken requested = parameters?["protocolVersion"];
            string requestedVersion = requested?.Type == JTokenType.String ? (string)requested : null;
            string sessionId = _sessions.Issue();

            JObject result = new()
            {
                ["protocolVersion"] = McpServerInfo.NegotiateProtocolVersion(requestedVersion),
                ["capabilities"] = new JObject
                {
                    ["tools"] = SupportsListChanged ? new JObject { ["listChanged"] = true } : new JObject()
                },
                ["serverInfo"] = new JObject
                {
                    ["name"] = McpServerInfo.Name,
                    ["version"] = McpServerInfo.Version
                }
            };
            if (!string.IsNullOrWhiteSpace(Instructions))
            {
                result["instructions"] = Instructions;
            }

            return McpDispatchResult.Reply(JsonRpc.Result(id, result), sessionId);
        }

        private sealed class ToolBindingChangedException : Exception { }

        private async Task<McpDispatchResult> HandleToolsCallAsync(JToken id, JObject request, string sessionId,
            CancellationToken cancellationToken)
        {
            JObject parameters = request?["params"] as JObject;
            JToken nameToken = parameters?["name"];
            string name = nameToken?.Type == JTokenType.String ? (string)nameToken : null;
            if (string.IsNullOrEmpty(name))
            {
                return McpDispatchResult.Reply(JsonRpc.Error(id, JsonRpcErrorCodes.InvalidParams,
                    "Invalid params: tools/call requires a 'name'."));
            }

            JToken suppliedArguments = parameters?["arguments"];
            if (suppliedArguments != null && !(suppliedArguments is JObject))
                return McpDispatchResult.Reply(JsonRpc.Error(id, JsonRpcErrorCodes.InvalidParams,
                    "Invalid params: arguments must be a JSON object."));
            JObject arguments = suppliedArguments as JObject ?? new JObject();

            McpToolRegistry.InvocationPlan invocation = _registry.CaptureInvocation(name, arguments);
            if (invocation == null)
                return McpDispatchResult.Reply(JsonRpc.Error(id, JsonRpcErrorCodes.InvalidParams,
                    $"Invalid params: unknown tool '{name}'. Re-read tools/list, or call coreai_tools with " +
                    "action 'list' for the tools it brokers."));

            string key = InFlightKey(sessionId, id);
            using CancellationTokenSource requestLifetime =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bool registered;
            lock (_inFlightGate)
            {
                // WHY: a request id still in flight in the same session is a client error; the later call
                // runs but only the first one can be cancelled by that id.
                registered = !_inFlight.ContainsKey(key);
                if (registered) _inFlight.Add(key, requestLifetime);
            }
            CancellationToken callToken = requestLifetime.Token;
            try
            {
                callToken.ThrowIfCancellationRequested();
                Func<Task<McpToolResult>> work = () =>
                {
                    callToken.ThrowIfCancellationRequested();
                    if (!invocation.TryAdmit())
                        throw new ToolBindingChangedException();
                    return invocation.InvokeAsync(callToken);
                };
                McpToolResult toolResult = await (_mainThread is ICancellableMainThreadDispatcher cancellable
                        ? cancellable.RunOnMainThreadAsync(work, callToken)
                        : _mainThread.RunOnMainThreadAsync(work))
                    .ConfigureAwait(false);

                return McpDispatchResult.Reply(JsonRpc.Result(id,
                    (toolResult ?? McpToolResult.Failure("Tool returned no result.")).ToJson()));
            }
            catch (ToolBindingChangedException)
            {
                return McpDispatchResult.Reply(JsonRpc.Error(id, JsonRpcErrorCodes.InvalidParams,
                    $"Tool '{name}' changed or was removed; refresh tools/list and retry."));
            }
            catch (OperationCanceledException ex)
            {
                // WHY: Cancellation is a lifecycle signal (server stopping / client gone), never a tool
                // failure - surface it as a protocol-level internal error without an isError tool payload.
                return McpDispatchResult.Reply(JsonRpc.Error(id, JsonRpcErrorCodes.InternalError,
                    $"Tool '{name}' was cancelled: {ex.Message}"));
            }
            catch (TimeoutException ex)
            {
                // WHY: a stalled main thread (paused game, disabled host component) is a server lifecycle
                // failure, not a tool failure - report it as a JSON-RPC error carrying the cause so the
                // client fails fast with an actionable message instead of waiting forever.
                return McpDispatchResult.Reply(JsonRpc.Error(id, JsonRpcErrorCodes.InternalError,
                    $"Tool '{name}' timed out: {ex.Message}"));
            }
            catch (Exception ex)
            {
                // A handler that threw becomes an isError tool result so the model sees the failure text.
                return McpDispatchResult.Reply(JsonRpc.Result(id,
                    McpToolResult.Failure($"Tool '{name}' failed: {ex.Message}").ToJson()));
            }
            finally
            {
                if (registered)
                {
                    lock (_inFlightGate) _inFlight.Remove(key);
                }
            }
        }

        private void NoteFailure(JObject response)
        {
            string text = null;
            if (response?["error"] is JObject error)
            {
                text = (string)error["message"];
            }
            else if (response?["result"] is JObject result && result["isError"]?.Type == JTokenType.Boolean &&
                     (bool)result["isError"])
            {
                text = (result["content"] as JArray)?.First?["text"]?.ToString() ?? "isError";
            }

            if (text == null) return;
            Interlocked.Increment(ref _toolErrorCount);
            Volatile.Write(ref _lastError, text.Length > 300 ? text.Substring(0, 300) : text);
        }

        private void CancelInFlight(string sessionId, JToken requestId)
        {
            if (requestId == null || (requestId.Type != JTokenType.String && requestId.Type != JTokenType.Integer))
            {
                return;
            }

            CancellationTokenSource target;
            lock (_inFlightGate)
            {
                if (!_inFlight.TryGetValue(InFlightKey(sessionId, requestId), out target)) return;
            }

            // WHY: outside the lock, because cancellation callbacks run synchronously and belong to host tools.
            try
            {
                target.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        // WHY: the JSON form of the id, because a string "1" and a number 1 are different request ids.
        private static string InFlightKey(string sessionId, JToken id)
        {
            return (sessionId ?? "") + "\n" + (id ?? JValue.CreateNull()).ToString(Formatting.None);
        }
    }
}
