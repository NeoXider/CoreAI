using System;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Mcp.Protocol;
using CoreAI.Mcp.Tools;
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
    /// </summary>
    public sealed class McpRpcDispatcher
    {
        private readonly McpToolRegistry _registry;
        private readonly McpSessionStore _sessions;
        private readonly IMainThreadDispatcher _mainThread;

        /// <param name="registry">The tools available in the current composition.</param>
        /// <param name="sessions">Session id issuer/validator.</param>
        /// <param name="mainThread">Main-thread marshaller for tool invocation.</param>
        public McpRpcDispatcher(McpToolRegistry registry, McpSessionStore sessions, IMainThreadDispatcher mainThread)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
            _mainThread = mainThread ?? throw new ArgumentNullException(nameof(mainThread));
        }

        /// <summary>Routes a single parsed JSON-RPC request.</summary>
        public async Task<McpDispatchResult> DispatchAsync(JObject request, CancellationToken cancellationToken)
        {
            JToken id = request?["id"];
            string method = request?["method"]?.ToString();

            if (string.IsNullOrEmpty(method))
            {
                return McpDispatchResult.Reply(JsonRpc.Error(id, JsonRpcErrorCodes.InvalidRequest,
                    "Invalid Request: 'method' is required."));
            }

            // WHY: Notifications (no id) never get a response body; the client fires and forgets.
            if (JsonRpc.IsNotification(request))
            {
                // notifications/initialized and any other client notification are accepted as no-ops.
                return McpDispatchResult.Notification();
            }

            switch (method)
            {
                case McpMethods.Initialize:
                    return HandleInitialize(id, request);

                case McpMethods.Ping:
                    return McpDispatchResult.Reply(JsonRpc.Result(id, new JObject()));

                case McpMethods.ToolsList:
                    return McpDispatchResult.Reply(JsonRpc.Result(id, new JObject
                    {
                        ["tools"] = _registry.ToListJson()
                    }));

                case McpMethods.ToolsCall:
                    return await HandleToolsCallAsync(id, request, cancellationToken).ConfigureAwait(false);

                default:
                    return McpDispatchResult.Reply(JsonRpc.Error(id, JsonRpcErrorCodes.MethodNotFound,
                        $"Method not found: {method}"));
            }
        }

        private McpDispatchResult HandleInitialize(JToken id, JObject request)
        {
            // WHY: MCP version negotiation - echo the client's requested protocolVersion when present so
            // clients pinned to a specific version accept us; otherwise advertise our latest. Never
            // hard-fail on an unknown version, so newer/older clients still connect.
            string requested = request?["params"]?["protocolVersion"]?.ToString();
            string protocolVersion = string.IsNullOrWhiteSpace(requested)
                ? McpServerInfo.DefaultProtocolVersion
                : requested;

            string sessionId = _sessions.Issue();

            JObject result = new()
            {
                ["protocolVersion"] = protocolVersion,
                ["capabilities"] = new JObject
                {
                    ["tools"] = new JObject()
                },
                ["serverInfo"] = new JObject
                {
                    ["name"] = McpServerInfo.Name,
                    ["version"] = McpServerInfo.Version
                }
            };

            return McpDispatchResult.Reply(JsonRpc.Result(id, result), sessionId);
        }

        private async Task<McpDispatchResult> HandleToolsCallAsync(JToken id, JObject request,
            CancellationToken cancellationToken)
        {
            JObject parameters = request?["params"] as JObject;
            string name = parameters?["name"]?.ToString();
            if (string.IsNullOrEmpty(name))
            {
                return McpDispatchResult.Reply(JsonRpc.Error(id, JsonRpcErrorCodes.InvalidParams,
                    "Invalid params: tools/call requires a 'name'."));
            }

            IMcpTool tool = _registry.Find(name);
            if (tool == null)
            {
                return McpDispatchResult.Reply(JsonRpc.Error(id, JsonRpcErrorCodes.InvalidParams,
                    $"Invalid params: unknown tool '{name}'."));
            }

            JObject arguments = parameters?["arguments"] as JObject ?? new JObject();

            try
            {
                // Marshal the game-touching work onto the Unity main thread; the HTTP worker thread awaits.
                McpToolResult toolResult = await _mainThread
                    .RunOnMainThreadAsync(() => tool.InvokeAsync(arguments, cancellationToken))
                    .ConfigureAwait(false);

                return McpDispatchResult.Reply(JsonRpc.Result(id,
                    (toolResult ?? McpToolResult.Failure("Tool returned no result.")).ToJson()));
            }
            catch (OperationCanceledException)
            {
                // WHY: Cancellation is a lifecycle signal (server stopping / client gone), never a tool
                // failure - surface it as a protocol-level internal error without an isError tool payload.
                return McpDispatchResult.Reply(JsonRpc.Error(id, JsonRpcErrorCodes.InternalError,
                    $"Tool '{name}' was cancelled."));
            }
            catch (Exception ex)
            {
                // A handler that threw becomes an isError tool result so the model sees the failure text.
                return McpDispatchResult.Reply(JsonRpc.Result(id,
                    McpToolResult.Failure($"Tool '{name}' failed: {ex.Message}").ToJson()));
            }
        }
    }
}
