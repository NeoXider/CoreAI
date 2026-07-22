using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CoreAI.Mcp.Protocol
{
    /// <summary>
    /// JSON-RPC 2.0 error codes used by the MCP server (the standard reserved range plus the
    /// method-not-found / parse-error values the transport maps malformed and unknown calls to).
    /// </summary>
    public static class JsonRpcErrorCodes
    {
        /// <summary>Invalid JSON was received by the server.</summary>
        public const int ParseError = -32700;

        /// <summary>The JSON sent is not a valid Request object.</summary>
        public const int InvalidRequest = -32600;

        /// <summary>The method does not exist or is not available.</summary>
        public const int MethodNotFound = -32601;

        /// <summary>Invalid method parameter(s).</summary>
        public const int InvalidParams = -32602;

        /// <summary>Internal JSON-RPC error (a handler threw).</summary>
        public const int InternalError = -32603;
    }

    /// <summary>
    /// Pure helpers that build JSON-RPC 2.0 result and error envelopes as <see cref="JObject"/>s.
    /// Engine-free by design (see README architecture note): the whole protocol layer is exercised by
    /// EditMode tests without a socket or a Unity player loop.
    /// </summary>
    public static class JsonRpc
    {
        /// <summary>Constant "2.0" version tag every envelope carries.</summary>
        public const string Version = "2.0";

        /// <summary>Builds a successful JSON-RPC response carrying <paramref name="result"/>.</summary>
        public static JObject Result(JToken id, JToken result)
        {
            return new JObject
            {
                ["jsonrpc"] = Version,
                ["id"] = id ?? JValue.CreateNull(),
                ["result"] = result ?? new JObject()
            };
        }

        /// <summary>Builds a JSON-RPC error response.</summary>
        public static JObject Error(JToken id, int code, string message, JToken data = null)
        {
            JObject error = new()
            {
                ["code"] = code,
                ["message"] = message ?? ""
            };
            if (data != null)
            {
                error["data"] = data;
            }

            return new JObject
            {
                ["jsonrpc"] = Version,
                ["id"] = id ?? JValue.CreateNull(),
                ["error"] = error
            };
        }

        /// <summary>
        /// Parses a request body into a <see cref="JObject"/>. Returns false (with <paramref name="error"/>
        /// set to a -32700 envelope with a null id) when the body is not a single JSON object, so the
        /// transport can reply with a well-formed parse error rather than dropping the connection.
        /// </summary>
        public static bool TryParse(string body, out JObject request, out JObject error)
        {
            request = null;
            error = null;
            if (string.IsNullOrWhiteSpace(body))
            {
                error = Error(null, JsonRpcErrorCodes.ParseError, "Parse error: empty request body.");
                return false;
            }

            try
            {
                JToken token = JToken.Parse(body);
                if (token is not JObject obj)
                {
                    // WHY: Batch arrays and bare scalars are not supported by this minimal server; the
                    // MCP streamable-HTTP client only ever sends single request objects.
                    error = Error(null, JsonRpcErrorCodes.InvalidRequest,
                        "Invalid Request: expected a single JSON-RPC object.");
                    return false;
                }

                request = obj;
                return true;
            }
            catch (JsonException ex)
            {
                error = Error(null, JsonRpcErrorCodes.ParseError, $"Parse error: {ex.Message}");
                return false;
            }
        }

        /// <summary>True when the message is a JSON-RPC notification (no <c>id</c> member present).</summary>
        public static bool IsNotification(JObject request)
        {
            return request != null && request.Property("id") == null;
        }
    }
}
